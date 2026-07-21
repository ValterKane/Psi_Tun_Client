using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace PsiTun.Services;

public static class PingService
{
    public static async Task<int> MeasureLatencyAsync(string host, int port, int timeoutMs = 3000)
    {
        try
        {
            if (App.Core is not null)
            {
                // Connected — measure through sing-box HTTP proxy
                var proxy = new WebProxy("127.0.0.1:10809");
                using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
                var sw = Stopwatch.StartNew();
                using var cts = new CancellationTokenSource(timeoutMs);
                using var req = new HttpRequestMessage(HttpMethod.Head, "http://cp.cloudflare.com/");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                sw.Stop();
                return (int)sw.ElapsedMilliseconds;
            }
            else
            {
                // Not connected — direct TCP connect
                using var client = new TcpClient();
                var cts = new CancellationTokenSource(timeoutMs);
                var sw = Stopwatch.StartNew();
                await client.ConnectAsync(host, port, cts.Token);
                sw.Stop();
                return (int)sw.ElapsedMilliseconds;
            }
        }
        catch
        {
            return -1;
        }
    }
}
