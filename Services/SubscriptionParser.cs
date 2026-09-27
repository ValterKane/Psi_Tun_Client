using System.Net.Http;
using System.Text.Json;
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

    /// <summary>
    /// Accepts either a subscription URL (http/https) or a direct share link
    /// (vless://, vmess://, trojan://, ss:// — optionally multi-line, base64,
    /// Clash YAML or sing-box JSON) and routes to the right parser.
    /// </summary>
    public static async Task<List<VpnServer>> ParseInputAsync(string input, HttpClient? client = null)
    {
        var trimmed = input?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed))
            return [];

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return await ParseAsync(trimmed, client).ConfigureAwait(false);
        }

        // Direct share link(s) — ParseContent already routes plain links to ShareLinkParser
        return ParseContent(trimmed, "");
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

        // 3. sing-box JSON format — tested before the "://" check below, because a
        // config with a DoH server or a rule_set URL also contains "://" and would
        // otherwise be fed to the share-link scanner, matching nothing.
        if (content.StartsWith('{'))
            return ParseSingBoxJson(content);

        // 4. Plain text share links (one per line)
        if (content.Contains("://"))
            return ParseLinks(content);

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

        // Clash YAML proxies section — every following indented line, stopping
        // at the next top-level key (proxy-groups:, rules:, ...).
        var proxySection = Regex.Match(yaml,
            @"(?im)^[ \t]*proxies:[ \t]*\r?\n(?<body>(?:[ \t]+[^\r\n]*(?:\r?\n|$))*)");
        if (!proxySection.Success) return servers;

        // One entry per '- ' line; the indented lines under it belong to the
        // same entry. That covers flow style ('- {name: .., type: ..}', one
        // line) and block style ('- name: ..' with the rest below) alike, and
        // keeps the nested groups — ws-opts, grpc-opts, reality-opts — attached
        // instead of cutting the entry short at the first '}'.
        var entries = new List<string>();
        foreach (var line in proxySection.Groups["body"].Value.Split('\n'))
        {
            if (Regex.IsMatch(line, @"^[ \t]*-[ \t]"))
                entries.Add(line);
            else if (entries.Count > 0)
                entries[^1] += "\n" + line;
        }

        foreach (var entry in entries)
        {
            var parsed = ParseClashEntry(entry);
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
        server.ServiceName = ExtractYamlField(entry, "grpc-service-name") ?? "";
        // clash spells the uTLS preset "client-fingerprint"; "fingerprint"/"fp" are the
        // v2ray-style names that show up in hand-written entries
        server.Fingerprint = ExtractYamlField(entry, "client-fingerprint")
                          ?? ExtractYamlField(entry, "fingerprint")
                          ?? ExtractYamlField(entry, "fp") ?? "";
        // clash writes these as reality-opts: {public-key: .., short-id: ..} — the
        // keys live INSIDE the group, so a "reality-opts.public-key:" lookup never
        // matched anything and both fields stayed empty.
        server.PublicKey = ExtractYamlField(entry, "public-key") ?? "";
        server.ShortId = ExtractYamlField(entry, "short-id") ?? "";

        return string.IsNullOrEmpty(server.Address) ? null : server;
    }

    /// <summary>
    /// sing-box outbound JSON. Real JSON parsing (not regex) because every
    /// transport/security parameter worth keeping lives one level down —
    /// <c>tls.reality</c>, <c>tls.utls</c>, <c>transport.headers</c> — which a
    /// flat field scan cannot reach.
    /// </summary>
    private static List<VpnServer> ParseSingBoxJson(string json)
    {
        var servers = new List<VpnServer>();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return servers; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("outbounds", out var outbounds) ||
                outbounds.ValueKind != JsonValueKind.Array)
                return servers;

            foreach (var ob in outbounds.EnumerateArray())
            {
                var parsed = ParseSingBoxOutbound(ob);
                if (parsed is not null) servers.Add(parsed);
            }
        }

        return servers;
    }

    private static VpnServer? ParseSingBoxOutbound(JsonElement ob)
    {
        if (ob.ValueKind != JsonValueKind.Object) return null;

        var type = Str(ob, "type")?.ToLowerInvariant();
        var server = new VpnServer
        {
            Name = Str(ob, "tag") ?? type ?? "Server",
            Address = Str(ob, "server") ?? "",
            Port = Int(ob, "server_port") ?? 0,
            Uuid = Str(ob, "uuid") ?? "",
            Password = Str(ob, "password") ?? "",
            Flow = Str(ob, "flow") ?? ""
        };

        switch (type)
        {
            case "vless":
                server.Protocol = VpnProtocol.VLess;
                break;
            case "vmess":
                server.Protocol = VpnProtocol.VMess;
                // vmess carries its cipher in "security"; it is NOT the tls mode
                server.Cipher = Str(ob, "security") ?? "auto";
                break;
            case "trojan":
                server.Protocol = VpnProtocol.Trojan;
                break;
            case "shadowsocks":
                server.Protocol = VpnProtocol.Shadowsocks;
                server.Cipher = Str(ob, "method") ?? "";
                break;
            default:
                return null; // direct/dns/block/selector/socks/http are not servers
        }

        ApplySingBoxTls(ob, server);
        ApplySingBoxTransport(ob, server);

        return string.IsNullOrEmpty(server.Address) ? null : server;
    }

    private static void ApplySingBoxTls(JsonElement ob, VpnServer s)
    {
        s.Security = "none";
        if (!ob.TryGetProperty("tls", out var tls) || tls.ValueKind != JsonValueKind.Object) return;
        if (!IsTrue(tls, "enabled")) return;

        bool reality = tls.TryGetProperty("reality", out var r) &&
                       r.ValueKind == JsonValueKind.Object && IsTrue(r, "enabled");

        if (reality)
        {
            s.Security = "reality";
            s.PublicKey = Str(r, "public_key") ?? "";
            s.ShortId = Str(r, "short_id") ?? "";
        }
        else
        {
            s.Security = "tls";
        }

        s.Sni = Str(tls, "server_name") ?? "";
        s.Alpn = JoinStrings(tls, "alpn");

        if (tls.TryGetProperty("utls", out var utls) && utls.ValueKind == JsonValueKind.Object)
            s.Fingerprint = Str(utls, "fingerprint") ?? "";
    }

    private static void ApplySingBoxTransport(JsonElement ob, VpnServer s)
    {
        if (!ob.TryGetProperty("transport", out var t) || t.ValueKind != JsonValueKind.Object) return;

        s.Network = Str(t, "type") ?? "tcp";
        switch (s.Network)
        {
            case "ws":
                s.Path = Str(t, "path") ?? "";
                s.Host = HeaderValue(t, "Host") ?? HeaderValue(t, "host") ?? "";
                break;
            case "grpc":
                s.ServiceName = Str(t, "service_name") ?? "";
                break;
            case "httpupgrade":
            case "xhttp":
                s.Path = Str(t, "path") ?? "";
                s.Host = Str(t, "host") ?? "";
                if (s.Network == "xhttp") s.XhttpMode = Str(t, "mode") ?? "";
                break;
            case "http":
            case "h2":
                s.Path = Str(t, "path") ?? "";
                s.Host = JoinStrings(t, "host");
                break;
        }
    }

    private static bool IsTrue(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt32(out var i) ? i : null;

    /// <summary>Comma-joined array of strings — the shape VpnServer uses for alpn/host lists.</summary>
    private static string JoinStrings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? string.Join(",", v.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()))
            : "";

    private static string? HeaderValue(JsonElement transport, string header) =>
        transport.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object
            ? Str(h, header) : null;

    /// <summary>
    /// Flat scan of a clash entry, flow or block style. The regex is unanchored,
    /// so a key nested inside <c>ws-opts: {path: /x, headers: {Host: y}}</c> is
    /// found too — which is why '}' is excluded from the scalar value class:
    /// without it the capture runs on past the closing brace and yields "y}}".
    /// Lists are read before the scalar pattern, because that pattern stops at
    /// the first comma — exactly where 'alpn: [h2, http/1.1]' would be cut in
    /// half, keeping "h2" and silently dropping the second protocol.
    /// </summary>
    private static string? ExtractYamlField(string yaml, string field)
    {
        var f = Regex.Escape(field);

        // Inline list — 'alpn: [h2, http/1.1]' / 'alpn: ["h2", "http/1.1"]'
        var inline = Regex.Match(yaml, $@"{f}:[ \t]*(\[[^\]\r\n]*\])", RegexOptions.IgnoreCase);
        if (inline.Success) return inline.Groups[1].Value.Trim();

        // YAML block list — 'alpn:' followed by '- h2' lines
        var block = Regex.Match(yaml,
            $@"{f}:[ \t]*\r?\n(?<items>(?:[ \t]*-[ \t]*[^\r\n]+(?:\r?\n|$))+)",
            RegexOptions.IgnoreCase);
        if (block.Success)
            return string.Join(",", Regex
                .Matches(block.Groups["items"].Value, @"[ \t]*-[ \t]*([^\r\n]+)")
                .Select(m => m.Groups[1].Value.Trim().Trim('"', '\'')));

        // Scalar — 'path: /x', 'servername: y'
        var m = Regex.Match(yaml, $@"{f}:\s*""?([^""\r\n,}}]+)""?", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}
