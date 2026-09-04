using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace PsiTun.Services;

/// <summary>
/// Обновляет geoip.dat/geosite.dat (Xray) и .srs rule-set'ы (sing-box)
/// из релиза runetfreedom/russia-v2ray-rules-dat (обновляется ежедневно).
/// Прямые download-URL, без api.github.com (который в РФ режется).
/// </summary>
public class GeoUpdateService
{
    private const string BaseUrl =
        "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download";

    // .srs, на которые ссылается SingBoxConfigGenerator (route.rule_set + dns.rules)
    private static readonly string[] SrsFiles =
    {
        "geosite-ru-available-only-inside.srs",
        "geosite-ru-blocked.srs",
        "geosite-ru-blocked-all.srs",
        "geosite-category-ads-all.srs",
        "geosite-win-spy.srs",
        "geosite-private.srs",
    };

    private readonly string _xrayDir;
    private readonly string _geositeDir;
    private readonly string _marker;

    public GeoUpdateService(string baseDir)
    {
        _xrayDir = Path.Combine(baseDir, "xray");
        _geositeDir = Path.Combine(baseDir, "sing-box", "rules", "rule-set-geosite");
        _marker = Path.Combine(baseDir, ".geo-updated");
    }

    public bool NeedsUpdate(TimeSpan maxAge) =>
        !File.Exists(_marker) ||
        DateTime.UtcNow - File.GetLastWriteTimeUtc(_marker) > maxAge;

    public async Task UpdateAsync(
        IProgress<(string Status, int Percent)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_xrayDir);
        Directory.CreateDirectory(_geositeDir);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Add("User-Agent", "PsiTun");

        await DownloadToAsync(http, $"{BaseUrl}/geoip.dat", Path.Combine(_xrayDir, "geoip.dat"), ct);
        progress?.Report(("geoip.dat обновлён", 30));
        await DownloadToAsync(http, $"{BaseUrl}/geosite.dat", Path.Combine(_xrayDir, "geosite.dat"), ct);
        progress?.Report(("geosite.dat обновлён", 60));

        var zipPath = Path.Combine(Path.GetTempPath(), $"psi-rules-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadToAsync(http, $"{BaseUrl}/sing-box.zip", zipPath, ct);
            progress?.Report(("sing-box.zip загружен, распаковка...", 75));
            ExtractSrs(zipPath);
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }

        File.WriteAllText(_marker, DateTime.UtcNow.ToString("O"));
        progress?.Report(("geo-данные обновлены", 100));
    }

    private void ExtractSrs(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var name in SrsFiles)
        {
            var entry = zip.GetEntry($"rule-set-geosite/{name}")
                        ?? zip.Entries.FirstOrDefault(e =>
                            e.FullName.EndsWith(name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            entry.ExtractToFile(Path.Combine(_geositeDir, name), overwrite: true);
        }
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
