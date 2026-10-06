using System.Net;
using System.Net.Http;
using PsiTun.Services;

namespace PsiTun.Tests;

public class MihomoApiClientTests
{
    [Fact]
    public async Task SelectProxyAsync_SendsPutWithNameBody()
    {
        var handler = new RecordingHandler();
        var api = new MihomoApiClient(10812, "s3cr3t", new HttpClient(handler));

        await api.SelectProxyAsync("PROXY", "Node #0", CancellationToken.None);

        var req = handler.LastRequest!;
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal("http://127.0.0.1:10812/proxies/PROXY", req.RequestUri!.ToString());
        Assert.Equal("Bearer s3cr3t", req.Headers.Authorization!.ToString());
        Assert.Equal("{\"name\":\"Node #0\"}", handler.LastBody);
    }

    [Fact]
    public async Task SelectProxyAsync_UrlEncodesGroupName()
    {
        var handler = new RecordingHandler();
        var api = new MihomoApiClient(10812, "s", new HttpClient(handler));

        await api.SelectProxyAsync("PSI SERVERS", "n", CancellationToken.None);

        // ToString() разворачивает %20 обратно в пробел — проверяем AbsolutePath.
        Assert.Equal("/proxies/PSI%20SERVERS", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RefreshProxyProviderAsync_PutsToProvidersProxies()
    {
        var handler = new RecordingHandler();
        var api = new MihomoApiClient(10812, "s", new HttpClient(handler));

        await api.RefreshProxyProviderAsync("psi-servers", CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal("http://127.0.0.1:10812/providers/proxies/psi-servers", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task RefreshRuleProviderAsync_PutsToProvidersRules()
    {
        var handler = new RecordingHandler();
        var api = new MihomoApiClient(10812, "s", new HttpClient(handler));

        await api.RefreshRuleProviderAsync("psi-user", CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal("http://127.0.0.1:10812/providers/rules/psi-user", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task ReadyAsync_IsTrueOnVersionAndFalseOnFailure()
    {
        var okHandler = new RecordingHandler { Response = new(HttpStatusCode.OK) { Content = new StringContent("{\"version\":\"1.19.32\"}") } };
        var ok = new MihomoApiClient(10812, "s", new HttpClient(okHandler));
        var down = new MihomoApiClient(10812, "s", new HttpClient(new RecordingHandler { ThrowOnSend = true }));

        Assert.True(await ok.ReadyAsync(CancellationToken.None));
        Assert.False(await down.ReadyAsync(CancellationToken.None));
        Assert.Equal("http://127.0.0.1:10812/version", okHandler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetRulesAsync_ReturnsBody()
    {
        var handler = new RecordingHandler
        {
            Response = new(HttpStatusCode.OK) { Content = new StringContent("{\"rules\":[]}") }
        };
        var api = new MihomoApiClient(10812, "s", new HttpClient(handler));

        var body = await api.GetRulesAsync(CancellationToken.None);

        Assert.Equal("{\"rules\":[]}", body);
        Assert.Equal("http://127.0.0.1:10812/rules", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ProxyDelayAsync_ReturnsDelayAndEncodesUrl()
    {
        var handler = new RecordingHandler
        {
            Response = new(HttpStatusCode.OK) { Content = new StringContent("{\"delay\":157}") }
        };
        var api = new MihomoApiClient(10812, "s", new HttpClient(handler));

        var delay = await api.ProxyDelayAsync("PROXY", "https://x.com/", 5000, CancellationToken.None);

        Assert.Equal(157, delay);
        Assert.Equal(
            "http://127.0.0.1:10812/proxies/PROXY/delay?timeout=5000&url=https%3A%2F%2Fx.com%2F",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ProxyDelayAsync_IsZeroWhenControllerOmitsDelay()
    {
        var handler = new RecordingHandler
        {
            Response = new(HttpStatusCode.OK) { Content = new StringContent("{\"message\":\"timeout\"}") }
        };
        var api = new MihomoApiClient(10812, "s", new HttpClient(handler));

        Assert.Equal(0, await api.ProxyDelayAsync("PROXY", "https://x.com/", 5000, CancellationToken.None));
    }

    [Fact]
    public async Task NonSuccessStatus_ThrowsWithBody()
    {
        var handler = new RecordingHandler
        {
            Response = new(HttpStatusCode.BadRequest) { Content = new StringContent("payload format error") }
        };
        var api = new MihomoApiClient(10812, "s", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => api.RefreshRuleProviderAsync("psi-user", CancellationToken.None));

        Assert.Contains("payload format error", ex.Message);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.NoContent);
        public bool ThrowOnSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (ThrowOnSend) throw new HttpRequestException("connection refused");
            return Response;
        }
    }
}
