using System.Text.Json;
using System.Text.RegularExpressions;
using PsiTun.Models;

namespace PsiTun.Services;

public static partial class ShareLinkParser
{
    // vless://uuid@host:port?params#name
    [GeneratedRegex(@"^vless://(?<uuid>[^@]+)@(?<host>[^:]+):(?<port>\d+)(?<query>\?[^#]*)?(#(?<name>.*))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex VLessRegex();

    // vmess://base64json
    [GeneratedRegex(@"^vmess://(?<b64>[A-Za-z0-9+/=]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VMessRegex();

    // trojan://password@host:port?params#name
    [GeneratedRegex(@"^trojan://(?<password>[^@]+)@(?<host>[^:]+):(?<port>\d+)(?<query>\?[^#]*)?(#(?<name>.*))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TrojanRegex();

    // ss://base64method:password@host:port#name  OR  ss://base64#name
    [GeneratedRegex(@"^ss://(?<payload>[A-Za-z0-9+/=]+@[^#]*|[A-Za-z0-9+/=]+)(#(?<name>.*))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SSRegex();

    public static VpnServer? Parse(string link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;

        return link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ? ParseVLess(link)
             : link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase) ? ParseVMess(link)
             : link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase) ? ParseTrojan(link)
             : link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase) ? ParseSS(link)
             : null;
    }

    private static VpnServer? ParseVLess(string link)
    {
        var m = VLessRegex().Match(link);
        if (!m.Success) return null;

        var server = new VpnServer
        {
            Protocol = VpnProtocol.VLess,
            Uuid = Uri.UnescapeDataString(m.Groups["uuid"].Value),
            Address = m.Groups["host"].Value,
            Port = int.Parse(m.Groups["port"].Value),
            Name = Uri.UnescapeDataString(m.Groups["name"].Value.TrimEnd('#')),
            RawLink = link
        };

        ParseQuery(m.Groups["query"].Value, server);
        return server;
    }

    private static VpnServer? ParseVMess(string link)
    {
        var m = VMessRegex().Match(link);
        if (!m.Success) return null;

        try
        {
            var json = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(m.Groups["b64"].Value));
            return ParseVMessJson(json, link);
        }
        catch { return null; }
    }

    private static VpnServer? ParseVMessJson(string json, string rawLink)
    {
        // Minimal JSON parse without Newtonsoft — use regex to extract fields
        var server = new VpnServer { Protocol = VpnProtocol.VMess, RawLink = rawLink };

        server.Name = ExtractJsonField(json, "ps") ?? ExtractJsonField(json, "remark") ?? "VMess Server";
        server.Address = ExtractJsonField(json, "add") ?? "";
        server.Port = int.TryParse(ExtractJsonField(json, "port"), out var p) ? p : 0;
        server.Uuid = ExtractJsonField(json, "id") ?? "";
        server.Network = ExtractJsonField(json, "net") ?? "tcp";
        server.Security = ExtractJsonField(json, "security") ?? ExtractJsonField(json, "tls") ?? "none";
        server.Sni = ExtractJsonField(json, "sni") ?? ExtractJsonField(json, "host") ?? "";
        server.Path = ExtractJsonField(json, "path") ?? "";
        server.Host = ExtractJsonField(json, "host") ?? "";
        server.Alpn = ExtractJsonField(json, "alpn") ?? "";
        server.Fingerprint = ExtractJsonField(json, "fp") ?? "";
        server.ServiceName = ExtractJsonField(json, "serviceName") ?? "";
        server.Flow = ExtractJsonField(json, "flow") ?? "";

        return string.IsNullOrEmpty(server.Address) ? null : server;
    }

    private static VpnServer? ParseTrojan(string link)
    {
        var m = TrojanRegex().Match(link);
        if (!m.Success) return null;

        var server = new VpnServer
        {
            Protocol = VpnProtocol.Trojan,
            Password = Uri.UnescapeDataString(m.Groups["password"].Value),
            Address = m.Groups["host"].Value,
            Port = int.Parse(m.Groups["port"].Value),
            Name = Uri.UnescapeDataString(m.Groups["name"].Value.TrimEnd('#')),
            Security = "tls",
            RawLink = link
        };

        ParseQuery(m.Groups["query"].Value, server);
        return server;
    }

    private static VpnServer? ParseSS(string link)
    {
        var m = SSRegex().Match(link);
        if (!m.Success) return null;

        var payload = m.Groups["payload"].Value;
        var name = Uri.UnescapeDataString(m.Groups["name"].Value.TrimEnd('#'));

        try
        {
            // Format: base64(method:password)@host:port
            if (payload.Contains('@'))
            {
                var atIdx = payload.LastIndexOf('@');
                var methodPass = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(payload[..atIdx].PadRight(
                        payload[..atIdx].Length + (4 - payload[..atIdx].Length % 4) % 4, '=')));
                var parts = methodPass.Split(':', 2);
                var hostPort = payload[(atIdx + 1)..].Split(':');

                if (hostPort.Length < 2) return null;

                return new VpnServer
                {
                    Protocol = VpnProtocol.Shadowsocks,
                    Cipher = parts[0],
                    Password = parts.Length > 1 ? parts[1] : "",
                    Address = hostPort[0],
                    Port = int.Parse(hostPort[1]),
                    Name = string.IsNullOrEmpty(name) ? $"{parts[0]}-{hostPort[0]}" : name,
                    RawLink = link
                };
            }

            // Format: base64(method:password@host:port)  (whole payload is base64)
            var decoded = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(payload.PadRight(
                    payload.Length + (4 - payload.Length % 4) % 4, '=')));

            if (decoded.Contains('@'))
            {
                // It's method:password@host:port
                return Parse(decoded.StartsWith("ss://")
                    ? decoded
                    : $"ss://{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(decoded))}");
            }

            // Legacy SIP002: method:password@host:port
            return null;
        }
        catch { return null; }
    }

    private static void ParseQuery(string query, VpnServer server)
    {
        if (string.IsNullOrEmpty(query) || !query.StartsWith('?')) return;

        var qs = query.TrimStart('?');
        var pairs = qs.Split('&');

        foreach (var pair in pairs)
        {
            var kv = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]).ToLowerInvariant();
            var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";

            switch (key)
            {
                case "security": server.Security = val; break;
                case "type" or "network": server.Network = val; break;
                case "sni": server.Sni = val; break;
                case "alpn": server.Alpn = val; break;
                case "fp" or "fingerprint": server.Fingerprint = val; break;
                case "pbk" or "publickey": server.PublicKey = val; break;
                case "sid" or "shortid": server.ShortId = val; break;
                case "flow": server.Flow = val; break;
                case "path" or "wspath": server.Path = val; break;
                case "host" or "wsHost": server.Host = val; break;
                case "servicename": server.ServiceName = val; break;
                case "mode": server.XhttpMode = val; break;
                case "allowinsecure": break;
                // Reality/Mux extras — multiple param name aliases
                case "spx" or "spiderx": server.SpiderX = val; break;
                case "encryption": server.Encryption = val; break;
                case "pqv" or "mldsa65verify": server.Mldsa65Verify = val; break;
                case "fm" or "finalmask": server.Finalmask = val; break;
                case "x_padding_bytes" or "xpaddingbytes": server.XpaddingBytes = val; break;
                // Parse extra JSON for xmux config
                case "extra":
                    try
                    {
                        using var doc = JsonDocument.Parse(val);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("mode", out var m)) server.XhttpMode = m.GetString() ?? "auto";
                        if (root.TryGetProperty("xPaddingBytes", out var p)) server.XpaddingBytes = p.GetString() ?? "";
                        if (root.TryGetProperty("xmux", out var x)) server.XmuxConfig = x.GetRawText();
                    }
                    catch { /* ignore invalid JSON */ }
                    break;
            }
        }
    }

    private static string? ExtractJsonField(string json, string field)
    {
        var pattern = $@"""{field}"":\s*""([^""]*)""";
        var m = Regex.Match(json, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }
}
