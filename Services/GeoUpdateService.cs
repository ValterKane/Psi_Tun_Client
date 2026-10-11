using System.IO;
using System.Net.Http;

namespace PsiTun.Services;

/// <summary>
/// Обновляет geoip.dat/geosite.dat из релиза runetfreedom/russia-v2ray-rules-dat
/// (обновляется ежедневно). Прямые download-URL, без api.github.com (который в РФ
/// режется).
///
/// Оба файла кладутся в каталог ядра: mihomo читает гео из каталога <c>-d</c>,
/// отдельного ключа для пути в v1.19.32 нет. Наборы <c>.srs</c> (sing-box)
/// больше не скачиваются — mihomo их не читает.
/// </summary>
public class GeoUpdateService
{
    private const string BaseUrl =
        "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download";

    private readonly string _baseDir;
    private readonly string _geoDir;
    private readonly string _marker;

    public GeoUpdateService(string baseDir)
    {
        _baseDir = baseDir;
        _geoDir = Path.Combine(baseDir, "mihomo");
        _marker = Path.Combine(baseDir, ".geo-updated");
    }

    public bool NeedsUpdate(TimeSpan maxAge) =>
        !File.Exists(_marker) ||
        DateTime.UtcNow - File.GetLastWriteTimeUtc(_marker) > maxAge;

    public async Task UpdateAsync(
        IProgress<(string Status, int Percent)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_geoDir);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Add("User-Agent", "PsiTun");

        await DownloadToAsync(http, $"{BaseUrl}/geoip.dat", Path.Combine(_geoDir, "geoip.dat"), ct);
        progress?.Report(("geoip.dat обновлён", 50));

        var geositeStaged = Path.Combine(_geoDir, "geosite.dat.download");
        await DownloadToAsync(http, $"{BaseUrl}/geosite.dat", geositeStaged, ct);
        InstallSanitizedGeosite(geositeStaged, Path.Combine(_geoDir, "geosite.dat"));
        progress?.Report(("geosite.dat обновлён", 95));

        File.WriteAllText(_marker, DateTime.UtcNow.ToString("O"));
        progress?.Report(("geo-данные обновлены", 100));
    }

    // Скачиваем во временный файл, затем атомарно переносим — не портим рабочие файлы.
    private static async Task DownloadToAsync(HttpClient http, string url, string dest, CancellationToken ct)
    {
        var tmp = dest + ".tmp";
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = File.Create(tmp);
            await stream.CopyToAsync(fs, ct);
        }
        File.Move(tmp, dest, overwrite: true);
    }

    /// <summary>
    /// Чистит набор, если он ещё не вычищен после последнего изменения. Нужна
    /// потому, что обновление гео идёт раз в сутки, а каркас ссылается на
    /// <c>ru-blocked-all</c> при каждом старте: сырой набор валит запуск ядра.
    /// </summary>
    public void EnsureSanitized()
    {
        var target = Path.Combine(_geoDir, "geosite.dat");
        if (!File.Exists(target)) return;

        var marker = target + ".sanitized";
        if (File.Exists(marker) &&
            File.GetLastWriteTimeUtc(marker) >= File.GetLastWriteTimeUtc(target)) return;

        WriteSanitized(target, target);
    }

    private void InstallSanitizedGeosite(string staged, string target)
    {
        try { WriteSanitized(staged, target); }
        finally { try { File.Delete(staged); } catch { /* best effort */ } }
    }

    /// <summary>
    /// Пишет вычищенный набор в <paramref name="target"/>. При отказе разбора
    /// прежний рабочий файл остаётся на месте: набор без части категорий
    /// работоспособен, а с хвостовой точкой ядро не стартует вовсе. Ошибка
    /// уходит в журнал и не прерывает загрузку.
    /// </summary>
    private void WriteSanitized(string source, string target)
    {
        try
        {
            var cleaned = GeoSiteSanitizer.Sanitize(File.ReadAllBytes(source)).Cleaned;
            var tmp = target + ".tmp";
            File.WriteAllBytes(tmp, cleaned);
            File.Move(tmp, target, overwrite: true);
            File.WriteAllText(target + ".sanitized", DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            File.AppendAllText(Path.Combine(_baseDir, "bootstrap.log"),
                $"[{DateTime.Now}] geosite sanitize failed, previous set kept: {ex.Message}\n");
        }
    }
}
