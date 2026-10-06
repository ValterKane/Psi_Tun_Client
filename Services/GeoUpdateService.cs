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

    private readonly string _geoDir;
    private readonly string _marker;

    public GeoUpdateService(string baseDir)
    {
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

        await DownloadToAsync(http, $"{BaseUrl}/geosite.dat", Path.Combine(_geoDir, "geosite.dat"), ct);
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
}
