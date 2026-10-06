using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PsiTun.Services;

/// <summary>
/// Первый запуск: скачивает ядро mihomo и гео-данные. Ядро — единственное,
/// сменило пару Xray + sing-box.
///
/// Имя ассета собирается точно: <c>mihomo-windows-amd64-{tag}.zip</c>. У релиза
/// 127 ассетов с пересекающимися именами (go120…go125, micro-архитектуры v1/v2/v3,
/// <c>-compatible</c>), поэтому поиск по подстроке выбрал бы не тот файл. Хеш
/// содержимого этого ассета совпадает с ядром, прошедшим стенд.
/// </summary>
public class BootstrapService
{
    private readonly string _baseDir;
    private readonly string _coreDir;
    private readonly string _coreExe;
    private readonly string _bootMarker;

    public BootstrapService(string baseDir)
    {
        _baseDir = baseDir;
        _coreDir = Path.Combine(baseDir, "mihomo");
        _coreExe = Path.Combine(_coreDir, "mihomo.exe");
        _bootMarker = Path.Combine(_coreDir, ".bootstrapped");
    }

    public bool NeedsBootstrap => !File.Exists(_bootMarker);

    public async Task BootstrapAsync(IProgress<(string Status, int Percent)>? progress = null)
    {
        Directory.CreateDirectory(_coreDir);

        progress?.Report(("Checking latest mihomo release...", 5));

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "PsiTun");
        http.Timeout = TimeSpan.FromSeconds(30);

        var release = await http.GetFromJsonAsync<GitHubRelease>(
                "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest")
            ?? throw new Exception("Failed to get mihomo release info");

        // Без тега точное имя ассета не построить, а поиск по подстроке рискует
        // взять вариант с чужой микро-архитектурой — отказ лучше тихой подмены.
        var tag = release.TagName
            ?? throw new Exception("mihomo release has no tag_name");

        var assetName = $"mihomo-windows-amd64-{tag}.zip";
        var asset = release.Assets?.FirstOrDefault(a => a.Name == assetName);
        if (asset?.BrowserDownloadUrl is null)
            throw new Exception($"Could not find mihomo asset: {assetName}");

        progress?.Report(($"Downloading mihomo {tag}...", 10));

        var zipPath = Path.Combine(Path.GetTempPath(), "mihomo.zip");
        using (var response = await http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = File.Create(zipPath);

            var buffer = new byte[8192];
            long totalRead = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;
                if (totalBytes > 0)
                {
                    var pct = 10 + (int)(totalRead * 75 / totalBytes);
                    progress?.Report(($"Downloading mihomo... ({totalRead / 1024 / 1024} MB)", pct));
                }
            }
        }

        progress?.Report(("Extracting mihomo...", 88));

        // В архиве один файл — mihomo-windows-amd64.exe, имя не совпадает с тем,
        // что нужно приложению.
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e =>
                            e.Name.Equals("mihomo.exe", StringComparison.OrdinalIgnoreCase))
                        ?? zip.Entries.FirstOrDefault(e =>
                            Path.GetFileNameWithoutExtension(e.Name)
                                .StartsWith("mihomo-", StringComparison.OrdinalIgnoreCase))
                        ?? throw new Exception($"mihomo.exe not found in {assetName} (entries: {zip.Entries.Count})");

            entry.ExtractToFile(_coreExe, overwrite: true);
        }
        File.Delete(zipPath);

        // Гео лежит рядом с конфигом: ядро ищет geoip.dat/geosite.dat в каталоге -d,
        // ключа для отдельного пути в v1.19.32 нет.
        progress?.Report(("Downloading RU geo data...", 93));
        try
        {
            await new GeoUpdateService(_baseDir).UpdateAsync(progress);
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(_baseDir, "bootstrap.log");
            File.AppendAllText(logPath, $"[{DateTime.Now}] geo download failed: {ex.Message}\n");
        }

        File.WriteAllText(_bootMarker, DateTime.Now.ToString("O"));

        progress?.Report(("Ready!", 100));
    }
}

public class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
}

public class GitHubAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}
