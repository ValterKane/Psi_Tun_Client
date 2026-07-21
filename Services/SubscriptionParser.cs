using System.Net.Http;
using System.Text.RegularExpressions;
using PsiTun.Models;

namespace PsiTun.Services;

public static partial class SubscriptionParser
{
    [GeneratedRegex(@"(vless|vmess|trojan|ss)://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkExtractRegex();

    private static readonly HttpClient _defaultClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static SubscriptionParser()
    {
        _defaultClient.DefaultRequestHeaders.Add("User-Agent", "PsiTun/1.0");
        _defaultClient.DefaultRequestHeaders.Add("Accept", "*/*");
    }

    public static async Task<List<VpnServer>> ParseAsync(string url, HttpClient? client = null)
    {
        client ??= _defaultClient;

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            throw new Exception($"Server returned error: {ex.Message} (status: {ex.StatusCode})");
        }
        catch (TaskCanceledException)
        {
            throw new Exception("Connection timed out. Check the URL and your network.");
        }

        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ParseContent(content, url);
    }

    public static List<VpnServer> ParseContent(string content, string sourceUrl = "")
    {
        // Try to detect format
        content = content.Trim();

        // 1. Base64-encoded list of share links
        if (TryDecodeBase64(content, out var decoded))
            return ParseLinks(decoded);

        // 2. Clash YAML format
        if (content.Contains("proxies:") || content.Contains("Proxy:"))
            return ParseClash(content);

        // 3. Plain text share links (one per line)
        if (content.Contains("://"))
            return ParseLinks(content);

        // 4. sing-box JSON format
        if (content.StartsWith('{'))
            return ParseSingBoxJson(content);

        return [];
    }

    private static bool TryDecodeBase64(string text, out string decoded)
    {
        decoded = "";
        try
        {
            // Strip whitespace
            text = Regex.Replace(text, @"\s+", "");
            var padded = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(padded);
            decoded = System.Text.Encoding.UTF8.GetString(bytes);
            return decoded.Contains("://");
        }
        catch { return false; }
    }

    private static List<VpnServer> ParseLinks(string text)
    {
        var matches = LinkExtractRegex().Matches(text);
        var servers = new List<VpnServer>();

        foreach (Match match in matches)
        {
            var parsed = ShareLinkParser.Parse(match.Value);
            if (parsed is not null)
                servers.Add(parsed);
        }

        return servers;
    }

    private static List<VpnServer> ParseClash(string yaml)
    {
        var servers = new List<VpnServer>();

        // Clash YAML proxies section
        var proxySection = Regex.Match(yaml, @"proxies:\s*\n((?:\s+-.*\n?)*)", RegexOptions.IgnoreCase);
        if (!proxySection.Success) return servers;

        var proxyEntries = Regex.Matches(proxySection.Value, @"-\s*\{[^}]+\}");
        foreach (Match entry in proxyEntries)
        {
            var parsed = ParseClashEntry(entry.Value);
            if (parsed is not null) servers.Add(parsed);
        }

        return servers;
    }

    private static VpnServer? ParseClashEntry(string entry)
    {
        var name = ExtractYamlField(entry, "name");
        var type = ExtractYamlField(entry, "type")?.ToLowerInvariant();
        var server = new VpnServer { Name = name ?? "Unknown" };

        server.Address = ExtractYamlField(entry, "server") ?? "";
        server.Port = int.TryParse(ExtractYamlField(entry, "port"), out var p) ? p : 0;
        server.Network = ExtractYamlField(entry, "network") ?? "tcp";

        switch (type)
        {
            case "vmess":
                server.Protocol = VpnProtocol.VMess;
                server.Uuid = ExtractYamlField(entry, "uuid") ?? "";
                server.Cipher = ExtractYamlField(entry, "cipher") ?? "auto";
                break;
            case "vless":
                server.Protocol = VpnProtocol.VLess;
                server.Uuid = ExtractYamlField(entry, "uuid") ?? "";
                server.Flow = ExtractYamlField(entry, "flow") ?? "";
                break;
            case "trojan":
                server.Protocol = VpnProtocol.Trojan;
                server.Password = ExtractYamlField(entry, "password") ?? "";
                break;
            case "ss":
            case "shadowsocks":
                server.Protocol = VpnProtocol.Shadowsocks;
                server.Cipher = ExtractYamlField(entry, "cipher") ?? "";
                server.Password = ExtractYamlField(entry, "password") ?? "";
                break;
            default: return null;
        }

        // Transport
        server.Security = ExtractYamlField(entry, "security") ?? ExtractYamlField(entry, "tls")?.ToLowerInvariant() ?? "none";
        if (server.Security == "true") server.Security = "tls";
        server.Sni = ExtractYamlField(entry, "sni") ?? ExtractYamlField(entry, "servername") ?? "";
        server.Path = ExtractYamlField(entry, "path") ?? ExtractYamlField(entry, "ws-path") ?? "";
        server.Host = ExtractYamlField(entry, "host") ?? ExtractYamlField(entry, "ws-headers.Host") ?? "";
        server.Alpn = ExtractYamlField(entry, "alpn") ?? "";
        server.Fingerprint = ExtractYamlField(entry, "fingerprint") ?? ExtractYamlField(entry, "fp") ?? "";
        server.PublicKey = ExtractYamlField(entry, "reality-opts.public-key") ?? "";
        server.ShortId = ExtractYamlField(entry, "reality-opts.short-id") ?? "";

        return string.IsNullOrEmpty(server.Address) ? null : server;
    }

    private static List<VpnServer> ParseSingBoxJson(string json)
    {
        // Extract outbounds array — simplistic approach without full JSON parsing
        var servers = new List<VpnServer>();

        var outboundsMatch = Regex.Match(json, @"""outbounds""\s*:\s*\[([\s\S]*?)\](?=\s*[,}\]])");
        if (!outboundsMatch.Success) return servers;

        // Extract individual outbound objects
        var entries = Regex.Matches(outboundsMatch.Groups[1].Value, @"\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}");
        foreach (Match entry in entries)
        {
            var type = ExtractJsonField(entry.Value, "type");
            if (type is "direct" or "dns" or "block") continue;

            var server = new VpnServer
            {
                Name = ExtractJsonField(entry.Value, "tag") ?? type ?? "Server",
                Address = ExtractJsonField(entry.Value, "server") ?? "",
                Port = int.TryParse(ExtractJsonField(entry.Value, "server_port"), out var p) ? p : 0
            };

            switch (type)
            {
                case "vless": server.Protocol = VpnProtocol.VLess; break;
                case "vmess": server.Protocol = VpnProtocol.VMess; break;
                case "trojan": server.Protocol = VpnProtocol.Trojan; break;
                case "shadowsocks": server.Protocol = VpnProtocol.Shadowsocks; break;
                default: continue;
            }

            server.Uuid = ExtractJsonField(entry.Value, "uuid") ?? "";
            server.Password = ExtractJsonField(entry.Value, "password") ?? "";
            server.Cipher = ExtractJsonField(entry.Value, "method") ?? "";
            server.Security = ExtractJsonField(entry.Value, "security") ?? ExtractJsonField(entry.Value, "tls") ?? "none";

            if (!string.IsNullOrEmpty(server.Address))
                servers.Add(server);
        }

        return servers;
    }

    private static string? ExtractYamlField(string yaml, string field)
    {
        var pattern = $@"{Regex.Escape(field)}:\s*""?([^""\r\n,]+)""?";
        var m = Regex.Match(yaml, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string? ExtractJsonField(string json, string field)
    {
        var pattern = $@"""{field}"":\s*""([^""]*)""";
        var m = Regex.Match(json, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }
}
