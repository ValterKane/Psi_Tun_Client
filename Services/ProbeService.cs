using System.Diagnostics;
using System.Net.Http;
using PsiTun.Models;

namespace PsiTun.Services;

public static class ProbeService
{
    private static readonly SemaphoreSlim Gate = new(Math.Max(1, Environment.ProcessorCount / 2));

    public static async Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var https = await ProbeDirectHttpsAsync(host, ct);
            var redirectHost = https.redirectHost;
            // Порт 80 опрашивается, только если https не дал ни отказа, ни редиректа:
            // заглушка провайдера отдаётся по HTTP, и именно там виден Location.
            if (https.ok && redirectHost is null)
                redirectHost = await ProbeHttpStubRedirectAsync(host, ct);

            var proxyMs = await ProbeProxyAsync(host, ct);

            return new ProbeResult(host, https.ok, https.ms, proxyMs > 0, proxyMs,
                https.status, redirectHost);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Проба одной прямой рукой: нужна самолечению выученных правил.</summary>
    public static async Task<ProbeResult> ProbeDirectOnlyAsync(string host, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var https = await ProbeDirectHttpsAsync(host, ct);
            return new ProbeResult(host, https.ok, https.ms, ProxyOk: false, ProxyMs: 0);
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Рука «через прокси»: запрос выполняет само ядро, выбранным в группе прокси.
    /// Свой запрос через http-порт не годится: неизвестный домен уходит на
    /// <c>MATCH,DIRECT</c>, то есть мимо туннеля, и рука перестаёт различать пути.
    /// Ноль означает, что ядро не ответило или через туннель не прошло.
    /// </summary>
    private static async Task<long> ProbeProxyAsync(string host, CancellationToken ct)
    {
        if (App.Core is not { IsRunning: true }) return 0;
        try
        {
            return await App.Core.Api.ProxyDelayAsync(
                MihomoConfigGenerator.ProxyGroup, $"https://{host}/",
                AutoProxyClassifier.TimeoutMs, ct);
        }
        catch
        {
            return 0;
        }
    }

    // ok = получен ЛЮБОЙ HTTP-ответ. Прямая рука идёт без прокси: процесс PsiTun.exe
    // первым правилом каркаса отправлен на DIRECT, поэтому и при включённом TUN
    // это прямой путь. Редирект читается как признак и не раскрывается.
    private static async Task<(bool ok, long ms, int? status, string? redirectHost)> ProbeDirectHttpsAsync(
        string host, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(AutoProxyClassifier.TimeoutMs)
            };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            var status = (int)resp.StatusCode;
            var redirect = status is >= 300 and < 400 ? resp.Headers.Location?.Host : null;
            return (true, sw.ElapsedMilliseconds, status, redirect);
        }
        catch
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, null, null);
        }
    }

    /// <summary>Хост из <c>Location</c> при 3xx на HTTP, иначе <c>null</c>.</summary>
    private static async Task<string?> ProbeHttpStubRedirectAsync(string host, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(AutoProxyClassifier.TimeoutMs)
            };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{host}/");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)resp.StatusCode;
            return status is >= 300 and < 400 ? resp.Headers.Location?.Host : null;
        }
        catch
        {
            return null;
        }
    }
}
