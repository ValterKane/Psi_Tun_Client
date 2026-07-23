using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace PsiTun.Services;

public static class PingService
{
    private const string TestUrl = "https://www.google.com/generate_204";
    private const int Samples = 4;
    private const int TimeoutMs = 3000;

    public static async Task<int> MeasureLatencyAsync(string host, int port, int timeoutMs = TimeoutMs)
    {
        try
        {
            if (App.Core is not null)
                return await MeasureThroughProxy();
            else
                return await MeasureDirectTcp(host, port, timeoutMs);
        }
        catch
        {
            return -1;
        }
    }

    private static async Task<int> MeasureThroughProxy()
    {
        var proxy = new WebProxy("127.0.0.1:10809");
        var latencies = new List<int>(Samples);

        for (int i = 0; i < Samples; i++)
        {
            using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(TimeoutMs) };
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(TimeoutMs);
                using var req = new HttpRequestMessage(HttpMethod.Head, TestUrl);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                sw.Stop();
                if (i > 0) // skip warmup
                    latencies.Add((int)sw.ElapsedMilliseconds);
            }
            catch
            {
                return -1;
            }
        }

        return latencies.Count > 0 ? (int)latencies.Average() : -1;
    }

    private static async Task<int> MeasureDirectTcp(string host, int port, int timeoutMs)
    {
        var latencies = new List<int>(Samples);

        for (int i = 0; i < Samples; i++)
        {
            using var client = new TcpClient();
            var cts = new CancellationTokenSource(timeoutMs);
            var sw = Stopwatch.StartNew();
            try
            {
                await client.ConnectAsync(host, port, cts.Token);
                sw.Stop();
                if (i > 0)
                    latencies.Add((int)sw.ElapsedMilliseconds);
            }
            catch
            {
                return -1;
            }
        }

        return latencies.Count > 0 ? (int)latencies.Average() : -1;
    }
}
