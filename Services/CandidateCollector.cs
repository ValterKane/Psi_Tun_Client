using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace PsiTun.Services;

public sealed class CandidateCollector
{
    private static readonly Regex AnsiStrip = new(@"\x1b\[[0-9;]*m");
    private static readonly Regex ConnToRegex = new(@"inbound connection to ([A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?):\d+");
    // TUN логирует IP назначения, а не имя — имя видно только в DNS-обмене.
    private static readonly Regex DnsRegex = new(@"dns: exchanged [A-Z]+ ([A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?)\.");

    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new();
    private readonly HashSet<string> _skip = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _queue.Count;

    public void SetSkipHosts(IEnumerable<string> hosts)
    {
        lock (_skip)
        {
            _skip.Clear();
            foreach (var h in hosts) _skip.Add(h);
        }
    }

    public void HandleLine(string raw)
    {
        var clean = AnsiStrip.Replace(raw, "");
        var host = MatchHost(clean);
        if (host == null || !LooksLikeHost(host)) return;

        lock (_skip) if (_skip.Contains(host)) return;

        if (_lastSeen.TryGetValue(host, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(5)) return;
        _lastSeen[host] = DateTime.UtcNow;
        _queue.Enqueue(host);
    }

    private static string? MatchHost(string clean)
    {
        var m = ConnToRegex.Match(clean);
        if (m.Success) return m.Groups[1].Value;
        m = DnsRegex.Match(clean);
        return m.Success ? m.Groups[1].Value : null;
    }

    public bool TryDequeue(out string host) => _queue.TryDequeue(out host!);

    private static bool LooksLikeHost(string h) =>
        h.Contains('.') &&
        !h.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
        !IPAddress.TryParse(h, out _) &&
        h.All(c => !char.IsWhiteSpace(c));

    public static void SelfCheck()
    {
        static void Assert(bool cond, string msg)
        {
            if (!cond) throw new InvalidOperationException("collector: " + msg);
        }

        var c = new CandidateCollector();
        c.HandleLine("\u001b[36mINFO\u001b[0m [\u001b[38;5;66m123\u001b[0m 12ms] inbound/socks[socks-in]: inbound connection to example.com:443");
        c.HandleLine("INFO [1400171491 25.47s] dns: exchanged A api.mywot.com. 60 IN A 35.81.100.31");
        c.HandleLine("outbound/direct[direct]: outbound connection to example.com:443"); // не матчится
        c.HandleLine("inbound/tun[tun]: inbound connection to 90.156.233.121:443"); // IP → отфильтрован
        Assert(c.TryDequeue(out var h1) && h1 == "example.com", "socks line parse");
        Assert(c.TryDequeue(out var h2) && h2 == "api.mywot.com", "dns line parse");
        Assert(!c.TryDequeue(out _), "no more candidates");
    }
}
