using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace PsiTun.Services;

public sealed class CandidateCollector
{
    private static readonly Regex AnsiStrip = new(@"\x1b\[[0-9;]*m");

    // Формат ядра mihomo v1.19.32; обе формы подтверждены в двоичном файле:
    //   [TCP] <источник> --> <назначение> match <правило> using <политика>
    //   [TCP] <источник> --> <назначение> using <политика>
    // Источник не разбирается — он различается по режиму TUN (адрес с портом либо
    // имя процесса). Назначение — имя хоста, если ядро его вынюхало, иначе IP.
    // Политика — только буквы: строка приходит в обёртке stderr (`msg="… using
    // DIRECT"`), а туннельная запись несёт имя прокси в скобках
    // (`using PROXY[VLESS …]`). Оба хвоста отсекаются этим классом, поэтому
    // сравнение с DIRECT проходит, а PROXY отбрасывается.
    private static readonly Regex ConnRegex = new(
        @"--> (?<dst>[^\s]+) (?:match \S+ )?using (?<policy>[A-Za-z]+)");

    // Прямой дозвон не состоялся — самая частая форма блокировки. Слова «using»
    // в строке нет, основным выражением она не разбирается:
    //   [TCP] dial DIRECT (match Match/) <источник> --> <назначение> error: connect failed: …
    private static readonly Regex DirectFailRegex = new(
        @"dial DIRECT \(match [^)]*\) \S+ --> (?<dst>[^\s]+) error:");

    // Ответ DNS. Порядок сторон зависит от режима и версии, поэтому пара
    // разбирается по типу значения, а не по позиции.
    private static readonly Regex DnsRegex = new(@"\[DNS\] (?<a>[^\s,]+) --> (?<b>[^\s,]+)");

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

        var m = ConnRegex.Match(clean);
        if (m.Success)
        {
            // Кандидат — только то, что пошло напрямую: соединение через PROXY уже в туннеле.
            if (!m.Groups["policy"].Value.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) return;

            EnqueueDestination(m.Groups["dst"].Value);
            return;
        }

        var f = DirectFailRegex.Match(clean);
        if (f.Success)
        {
            EnqueueDestination(f.Groups["dst"].Value);
            return;
        }

        var d = DnsRegex.Match(clean);
        if (!d.Success) return;

        var left = d.Groups["a"].Value;
        var right = d.Groups["b"].Value;
        var (ip, dnsHost) = IPAddress.TryParse(left, out _) ? (left, right) : (right, left);
        if (!IPAddress.TryParse(ip, out _) || !LooksLikeHost(dnsHost)) return;

        PruneDnsMap();
        var entry = _ipToHosts.GetOrAdd(ip,
            _ => (new HashSet<string>(StringComparer.OrdinalIgnoreCase), DateTime.UtcNow));
        lock (entry.Hosts) entry.Hosts.Add(dnsHost);
        _ipToHosts[ip] = (entry.Hosts, DateTime.UtcNow); // TTL от последнего ответа
    }

    public bool TryDequeue(out string host) => _queue.TryDequeue(out host!);

    // Назначение приходит как «хост:порт». Соединение по IP связывается с именем
    // через ответ DNS этого же ядра.
    private void EnqueueDestination(string dst)
    {
        var colon = dst.LastIndexOf(':');
        if (colon <= 0) return;
        var host = dst[..colon];

        if (IPAddress.TryParse(host, out _))
        {
            foreach (var resolved in ResolveIp(host)) TryEnqueue(resolved);
            return;
        }

        TryEnqueue(host);
    }

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

        // Образцы взяты из живого лога ядра v1.19.32 и обёрнуты так, как строка
        // приходит приложению: stderr ядра, `level=info msg="…"`. Обёртка
        // обязательна — без неё закрывающая кавычка приклеивалась к политике и
        // сравнение с DIRECT не проходило.
        const string Env = "time=\"2026-10-07T02:03:55.252286500+03:00\" level=info ";

        var c = new CandidateCollector();
        c.HandleLine("\u001b[36mINFO\u001b[0m " + Env + "msg=\"[TCP] 198.18.0.1:65353(PsiTun.exe) --> github.com:443 match ProcessName(PsiTun.exe) using DIRECT\"");
        c.HandleLine(Env + "msg=\"[TCP] 198.18.0.1:54124(curl.exe) --> direct.example.net:443 using DIRECT\"");                 // форма без match
        c.HandleLine(Env + "msg=\"[TCP] 198.18.0.1:54639(curl.exe) --> tunneled.example.org:443 match RuleSet(psi-user) using PROXY[VLESS XHTTP Reality 443-Rockwell@Admin]\""); // туннель — не кандидат
        c.HandleLine("level=warning msg=\"[TCP] dial DIRECT (match Match/) 198.18.0.1:54514(curl.exe) --> example.net:443 error: connect failed: dial tcp 8.6.112.0:443: i/o timeout\ndial tcp 8.47.69.0:443: i/o timeout\""); // отказ дозвона
        c.HandleLine("[DNS] by-ip.example.com --> 35.81.100.31");
        c.HandleLine(Env + "msg=\"[TCP] 198.18.0.1:54126(curl.exe) --> 35.81.100.31:443 using DIRECT\"");                       // IP из ответа DNS
        c.HandleLine(Env + "msg=\"[TCP] 198.18.0.1:54127(curl.exe) --> 90.156.233.121:443 using DIRECT\"");                     // IP без ответа — не кандидат

        Assert(c.TryDequeue(out var h1) && h1 == "github.com", "форма с match и обёрткой stderr");
        Assert(c.TryDequeue(out var h2) && h2 == "direct.example.net", "форма без match");
        Assert(c.TryDequeue(out var h3) && h3 == "example.net", "строка отказа дозвона");
        Assert(c.TryDequeue(out var h4) && h4 == "by-ip.example.com", "связка с ответом DNS");
        Assert(!c.TryDequeue(out _), "лишних кандидатов нет");
    }
}
