using System.IO;
using System.Text;

namespace PsiTun.Services;

/// <summary>
/// Проверяет и чистит набор geosite.dat перед передачей ядру. Причина — домены с
/// хвостовой точкой: mihomo валит загрузку категории целиком
/// (`invalid domain "+.psb-marketplace.ru.": trailing dot is not allowed`), а
/// набор runetfreedom такие записи содержит.
///
/// Формат: GeoSiteList{repeated GeoSite entry=1},
/// GeoSite{string country_code=1; repeated Domain domain=2},
/// Domain{int32 type=1; string value=2}. Разбирается вручную — формат простой
/// (varint + length-delimited), внешняя зависимость не нужна.
/// </summary>
public static class GeoSiteSanitizer
{
    public readonly record struct DomainEntry(int Type, string Value);

    public readonly record struct Category(string Name, IReadOnlyList<DomainEntry> Domains);

    public readonly record struct SanitizeResult(byte[] Cleaned, int RemovedDomains, int Categories);

    /// <summary>Тип 1 — регулярное выражение; его синтаксис проверяет ядро.</summary>
    private const int TypeRegex = 1;

    public static IReadOnlyList<Category> Parse(byte[] source)
    {
        var buf = source.AsSpan();
        var result = new List<Category>();
        var pos = 0;

        while (pos < buf.Length)
        {
            var tag = ReadVarint(buf, ref pos);
            if ((tag >> 3) != 1 || (tag & 7) != 2)
                throw new InvalidDataException($"geosite.dat: ожидалась запись GeoSite, тег {tag}");

            var length = checked((int)ReadVarint(buf, ref pos));
            var end = pos + length;
            if (end > buf.Length) throw new InvalidDataException("geosite.dat: запись обрезана");

            string? name = null;
            var domains = new List<DomainEntry>();

            while (pos < end)
            {
                var field = ReadVarint(buf, ref pos);
                var number = (int)(field >> 3);

                switch (field & 7)
                {
                    case 2:
                        var len = checked((int)ReadVarint(buf, ref pos));
                        if (pos + len > end) throw new InvalidDataException("geosite.dat: поле обрезано");
                        if (number == 1) name = Encoding.UTF8.GetString(buf.Slice(pos, len));
                        else if (number == 2) domains.Add(ParseDomain(buf.Slice(pos, len)));
                        pos += len;
                        break;
                    case 0:
                        ReadVarint(buf, ref pos);
                        break;
                    case 5:
                        pos += 4;
                        break;
                    case 1:
                        pos += 8;
                        break;
                    default:
                        throw new InvalidDataException($"geosite.dat: неизвестный wire type {field & 7}");
                }
            }

            if (name is not null) result.Add(new Category(name, domains));
            pos = end;
        }

        return result;
    }

    public static byte[] Serialize(IReadOnlyList<Category> categories)
    {
        using var output = new MemoryStream();

        foreach (var category in categories)
        {
            var body = SerializeCategory(category);
            WriteFieldHeader(output, 1, 2);
            WriteVarint(output, (ulong)body.Length);
            output.Write(body);
        }

        return output.ToArray();
    }

    public static SanitizeResult Sanitize(byte[] source)
    {
        var categories = Parse(source);
        var removed = 0;
        var cleaned = new List<Category>(categories.Count);

        foreach (var category in categories)
        {
            var kept = new List<DomainEntry>(category.Domains.Count);
            foreach (var domain in category.Domains)
            {
                if (IsValid(domain)) kept.Add(domain);
                else removed++;
            }

            cleaned.Add(category with { Domains = kept });
        }

        return new SanitizeResult(Serialize(cleaned), removed, cleaned.Count);
    }

    /// <summary>
    /// Запись годится, если ядро её примет. Регулярные выражения проверяет
    /// ядро: правило хвостовой точки к ним не применимо.
    /// </summary>
    private static bool IsValid(DomainEntry domain)
    {
        var value = domain.Value;
        if (string.IsNullOrEmpty(value)) return false;

        if (domain.Type == TypeRegex)
            return !value.Any(char.IsControl);

        if (value[0] == '.' || value[^1] == '.') return false;
        if (value.Contains("..", StringComparison.Ordinal)) return false;

        foreach (var ch in value)
            if (ch <= ' ' || char.IsControl(ch)) return false;

        return true;
    }

    private static DomainEntry ParseDomain(ReadOnlySpan<byte> message)
    {
        var type = 0;
        var value = "";
        var pos = 0;

        while (pos < message.Length)
        {
            var field = ReadVarint(message, ref pos);
            var number = (int)(field >> 3);

            switch (field & 7)
            {
                case 0:
                    var numberValue = ReadVarint(message, ref pos);
                    if (number == 1) type = (int)numberValue;
                    break;
                case 2:
                    var len = checked((int)ReadVarint(message, ref pos));
                    if (pos + len > message.Length) throw new InvalidDataException("geosite.dat: домен обрезан");
                    if (number == 2) value = Encoding.UTF8.GetString(message.Slice(pos, len));
                    pos += len;
                    break;
                case 5:
                    pos += 4;
                    break;
                case 1:
                    pos += 8;
                    break;
                default:
                    pos = message.Length;
                    break;
            }
        }

        return new DomainEntry(type, value);
    }

    private static byte[] SerializeCategory(Category category)
    {
        using var output = new MemoryStream();

        if (category.Name.Length > 0)
        {
            var name = Encoding.UTF8.GetBytes(category.Name);
            WriteFieldHeader(output, 1, 2);
            WriteVarint(output, (ulong)name.Length);
            output.Write(name);
        }

        foreach (var domain in category.Domains)
        {
            var body = SerializeDomain(domain);
            WriteFieldHeader(output, 2, 2);
            WriteVarint(output, (ulong)body.Length);
            output.Write(body);
        }

        return output.ToArray();
    }

    private static byte[] SerializeDomain(DomainEntry domain)
    {
        using var output = new MemoryStream();

        if (domain.Type != 0)
        {
            WriteFieldHeader(output, 1, 0);
            WriteVarint(output, (ulong)domain.Type);
        }

        var value = Encoding.UTF8.GetBytes(domain.Value);
        WriteFieldHeader(output, 2, 2);
        WriteVarint(output, (ulong)value.Length);
        output.Write(value);

        return output.ToArray();
    }

    private static void WriteFieldHeader(Stream output, int field, int wireType) =>
        WriteVarint(output, (ulong)((field << 3) | wireType));

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        output.WriteByte((byte)value);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> buf, ref int pos)
    {
        ulong result = 0;
        var shift = 0;

        while (true)
        {
            if (pos >= buf.Length) throw new InvalidDataException("geosite.dat: varint обрезан");

            var b = buf[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;

            shift += 7;
            if (shift > 63) throw new InvalidDataException("geosite.dat: varint слишком длинный");
        }
    }
}
