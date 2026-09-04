using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace PsiTun.Services;

public sealed class CandidateCollector
{
    private static readonly Regex AnsiStrip = new(@"\x1b\[[0-9;]*m");
    private static readonly Regex ConnToRegex = new(@"inbound connection to ([A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?):\d+");
    // TUN логирует IP назначения, а не имя — имя связываем с IP через DNS-ответ.
    // Кандидат = только хост, к которому реально было соединение (голый DNS-резолв не считается).
    // ponytail: только A-записи (IPv4); AAAA-хосты не коррелируются, добавить при необходимости.
    private static readonly Regex DnsARegex = new(
        @"dns: exchanged A ([A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?)\. \d+ IN A (\d{1,3}(?:\.\d{1,3}){3})");

    private static readonly TimeSpan DnsEntryTtl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new();
    private readonly ConcurrentDictionary<string, (HashSet<string> Hosts, DateTime At)> _ipToHosts = new();
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

        var m = ConnToRegex.Match(clean);
        if (m.Success)
        {
            var target = m.Groups[1].Value;
            if (IPAddress.TryParse(target, out _))
            {
                foreach (var host in ResolveIp(target)) TryEnqueue(host);
            }
            else if (LooksLikeHost(target))
            {
                TryEnqueue(target);
            }
            return;
        }

        var d = DnsARegex.Match(clean);
        if (!d.Success) return;
        PruneDnsMap();
        var ip = d.Groups[2].Value;
        var dnsHost = d.Groups[1].Value;
        var entry = _ipToHosts.GetOrAdd(ip, _ => (new HashSet<string>(StringComparer.OrdinalIgnoreCase), DateTime.UtcNow));
        lock (entry.Hosts) entry.Hosts.Add(dnsHost);
        _ipToHosts[ip] = (entry.Hosts, DateTime.UtcNow); // TTL от последнего ответа
    }

    public bool TryDequeue(out string host) => _queue.TryDequeue(out host!);

    private IEnumerable<string> ResolveIp(string ip)
    {
        if (!_ipToHosts.TryGetValue(ip, out var entry)) yield break;
        if (DateTime.UtcNow - entry.At > DnsEntryTtl) { _ipToHosts.TryRemove(ip, out _); yield break; }
        string[] hosts;
        lock (entry.Hosts) hosts = entry.Hosts.ToArray();
        foreach (var h in hosts) yield return h;
    }

    private void TryEnqueue(string host)
    {
        if (!LooksLikeHost(host)) return;
        lock (_skip) if (_skip.Contains(host)) return;
        if (_lastSeen.TryGetValue(host, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(5)) return;
        _lastSeen[host] = DateTime.UtcNow;
        _queue.Enqueue(host);
    }

    private void PruneDnsMap()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _ipToHosts)
            if (now - kv.Value.At > DnsEntryTtl) _ipToHosts.TryRemove(kv.Key, out _);
    }

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
        c.HandleLine("inbound/tun[tun]: inbound connection to 35.81.100.31:443"); // коннект по IP из DNS-ответа → кандидат
        c.HandleLine("inbound/tun[tun]: inbound connection to 90.156.233.121:443"); // IP без DNS-ответа → не кандидат
        c.HandleLine("outbound/direct[direct]: outbound connection to example.com:443"); // не матчится
        Assert(c.TryDequeue(out var h1) && h1 == "example.com", "socks line parse");
        Assert(c.TryDequeue(out var h2) && h2 == "api.mywot.com", "dns+connection correlation");
        Assert(!c.TryDequeue(out _), "no more candidates");
    }
}
