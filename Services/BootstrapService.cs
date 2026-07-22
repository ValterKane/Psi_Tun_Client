using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsiTun.Services;

public class BootstrapService
{
    private readonly string _baseDir;
    private readonly string _coreDir;
    private readonly string _coreExe;
    private readonly string _singBoxDir;
    private readonly string _singBoxExe;

    private readonly string _bootMarker;

    public BootstrapService(string baseDir)
    {
        _baseDir = baseDir;
        _coreDir = Path.Combine(baseDir, "xray");
        _coreExe = Path.Combine(_coreDir, "xray.exe");
        _singBoxDir = Path.Combine(baseDir, "sing-box");
        _singBoxExe = Path.Combine(_singBoxDir, "sing-box.exe");
        _bootMarker = Path.Combine(_coreDir, ".bootstrapped");
    }

    public bool NeedsBootstrap => !File.Exists(_bootMarker);

    public async Task BootstrapAsync(IProgress<(string Status, int Percent)>? progress = null)
    {
        Directory.CreateDirectory(_coreDir);

        progress?.Report(("Checking latest Xray-core release...", 5));

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "PsiTun");
        http.Timeout = TimeSpan.FromSeconds(30);

        var releaseUrl = "https://api.github.com/repos/XTLS/Xray-core/releases/latest";
        var release = await http.GetFromJsonAsync<GitHubRelease>(releaseUrl)
            ?? throw new Exception("Failed to get Xray-core release info");

        var tag = release.TagName ?? "v25.0.0";
        var assetName = Environment.Is64BitOperatingSystem
            ? "Xray-windows-64.zip"
            : "Xray-windows-32.zip";

        var asset = release.Assets?.FirstOrDefault(a => a.Name == assetName);
        if (asset?.BrowserDownloadUrl is null)
            throw new Exception($"Could not find Xray-core asset: {assetName}");

        progress?.Report(($"Downloading Xray-core {tag}...", 10));

        // Download
        var zipPath = Path.Combine(Path.GetTempPath(), "xray.zip");
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
                    var pct = 10 + (int)(totalRead * 70 / totalBytes);
                    progress?.Report(($"Downloading Xray-core... ({totalRead / 1024 / 1024} MB)", pct));
                }
            }
        }

        progress?.Report(("Extracting Xray-core...", 85));

        // Extract and flatten
        var tempDir = Path.Combine(Path.GetTempPath(), "xray_extract");
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        ZipFile.ExtractToDirectory(zipPath, tempDir);
        File.Delete(zipPath);

        // Copy all files to core dir
        foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(_coreDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }
        Directory.Delete(tempDir, true);

        progress?.Report(("Downloading sing-box...", 88));
        await DownloadSingBoxAsync(http, progress);

        // Runetfreedom geo data — replaces Xray's default geosite.dat/geoip.dat
        progress?.Report(("Downloading RU geo data...", 93));
        await DownloadRunetFreedomGeoAsync(http, progress);

        // Write marker so we don't re-bootstrap
        File.WriteAllText(_bootMarker, DateTime.Now.ToString("O"));

        progress?.Report(("Ready!", 100));
    }

    private async Task DownloadRunetFreedomGeoAsync(HttpClient http, IProgress<(string, int)>? progress)
    {
        var geoUrl = "https://api.github.com/repos/runetfreedom/russia-v2ray-rules-dat/releases/latest";

        try
        {
            var release = await http.GetFromJsonAsync<GitHubRelease>(geoUrl);
            if (release?.Assets is null) return;

            foreach (var asset in release.Assets)
            {
                if (asset.Name is not ("geosite.dat" or "geoip.dat")) continue;
                if (asset.BrowserDownloadUrl is null) continue;

                progress?.Report(($"Downloading {asset.Name}...", 94));

                using var response = await http.GetAsync(asset.BrowserDownloadUrl);
                response.EnsureSuccessStatusCode();

                // Overwrite Xray's default geo files
                var destCore = Path.Combine(_coreDir, asset.Name);
                await using (var stream = await response.Content.ReadAsStreamAsync())
                await using (var fs = File.Create(destCore))
                {
                    await stream.CopyToAsync(fs);
                }

            }
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(_baseDir, "bootstrap.log");
            File.AppendAllText(logPath, $"[{DateTime.Now}] Runetfreedom geo download failed: {ex.Message}\n");
        }
    }

    private async Task DownloadSingBoxAsync(HttpClient http, IProgress<(string, int)>? progress)
    {
        if (File.Exists(_singBoxExe)) return;

        Directory.CreateDirectory(_singBoxDir);

        var releaseUrl = "https://api.github.com/repos/SagerNet/sing-box/releases/latest";

        try
        {
            var release = await http.GetFromJsonAsync<GitHubRelease>(releaseUrl);
            if (release?.Assets is null) return;

            var arch = Environment.Is64BitOperatingSystem ? "amd64" : "386";
            var assetName = $"sing-box-windows-{arch}.zip";
            var asset = release.Assets.FirstOrDefault(a =>
                a.Name?.Contains("windows") == true && a.Name.Contains(arch));

            if (asset?.BrowserDownloadUrl is null)
            {
                var logPath = Path.Combine(_baseDir, "bootstrap.log");
                File.AppendAllText(logPath,
                    $"[{DateTime.Now}] sing-box: no asset found matching 'windows-{arch}'\n");
                return;
            }

            progress?.Report(($"Downloading sing-box...", 89));

            var zipPath = Path.Combine(Path.GetTempPath(), "sing-box.zip");
            using (var response = await http.GetAsync(asset.BrowserDownloadUrl))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var fs = File.Create(zipPath);
                await stream.CopyToAsync(fs);
            }

            // Extract sing-box.exe
            using var zip = ZipFile.OpenRead(zipPath);
            var exeEntry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase));
            exeEntry?.ExtractToFile(_singBoxExe, overwrite: true);

            File.Delete(zipPath);
            progress?.Report(("sing-box ready", 90));
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(_baseDir, "bootstrap.log");
            File.AppendAllText(logPath, $"[{DateTime.Now}] sing-box download failed: {ex.Message}\n");
        }
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
