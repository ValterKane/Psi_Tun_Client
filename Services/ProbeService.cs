using System.Diagnostics;
using System.Net;
using System.Net.Http;
using PsiTun.Models;

namespace PsiTun.Services;

public static class ProbeService
{
    private const int ForceProxyPort = 10811; // force-in в ConfigGenerator.BuildInbounds
    private static readonly SemaphoreSlim Gate = new(Math.Max(1, Environment.ProcessorCount / 2));

    public static async Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var direct = await ProbeOnceAsync(host, App.Settings.XrayInboundPort, ct);
            if (!direct.ok)
                direct = await ProbeOnceAsync(host, App.Settings.XrayInboundPort, ct); // повтор на «лаг»
            var proxy = await ProbeOnceAsync(host, ForceProxyPort, ct);
            return new ProbeResult(host, direct.ok, direct.ms, proxy.ok, proxy.ms);
        }
        finally { Gate.Release(); }
    }

    public static async Task<ProbeResult> ProbeDirectOnlyAsync(string host, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var direct = await ProbeOnceAsync(host, App.Settings.XrayInboundPort, ct);
            return new ProbeResult(host, direct.ok, direct.ms, false, 0);
        }
        finally { Gate.Release(); }
    }

    // ok = получен ЛЮБОЙ HTTP-ответ (статус не важен: 403/502 ≠ блокировка,
    // блок = сбой соединения/таймаут). ms = полная длительность попытки.
    private static async Task<(bool ok, long ms)> ProbeOnceAsync(string host, int socksPort, CancellationToken ct)
    {
        var proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}");
        using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(AutoProxyClassifier.TimeoutMs) };
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, $"https://{host}/");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            return (true, sw.ElapsedMilliseconds);
        }
        catch
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds);
        }
    }
}
