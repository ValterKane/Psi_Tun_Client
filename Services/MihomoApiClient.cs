using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace PsiTun.Services;

/// <summary>
/// REST-клиент внешнего контроллера mihomo (порт <see cref="MihomoConfigGenerator.ControllerPort"/>).
/// Позволяет применить правку провайдера без перезапуска ядра: ядро и TUN-адаптер
/// при этом не пересоздаются.
///
/// Дисциплина та же, что в <see cref="MihomoConfigGenerator"/>: не-2xx — исключение
/// с телом ответа, <c>ReadyAsync</c> — единственный метод, гасящий сетевую ошибку.
/// </summary>
public sealed class MihomoApiClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly string _base;
    private readonly string? _secret;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public MihomoApiClient(int controllerPort, string secret, HttpClient? http = null)
    {
        _base = $"http://127.0.0.1:{controllerPort}";
        _secret = string.IsNullOrEmpty(secret) ? null : secret;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        if (_ownsHttp) _http.Timeout = RequestTimeout;
    }

    /// <summary>Ядро отвечает на <c>GET /version</c>. Сетевая ошибка — <c>false</c>.</summary>
    public async Task<bool> ReadyAsync(CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "/version", null, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary><c>PUT /providers/proxies/{name}</c> — ядро перечитывает файл провайдера серверов.</summary>
    public async Task RefreshProxyProviderAsync(string name, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, $"/providers/proxies/{Uri.EscapeDataString(name)}", null, ct).ConfigureAwait(false);
    }

    /// <summary><c>PUT /providers/rules/{name}</c> — ядро перечитывает файл провайдера правил.</summary>
    public async Task RefreshRuleProviderAsync(string name, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, $"/providers/rules/{Uri.EscapeDataString(name)}", null, ct).ConfigureAwait(false);
    }

    /// <summary><c>PUT /proxies/{group}</c> с телом <c>{"name":...}</c> — выбор прокси в группе.</summary>
    public async Task SelectProxyAsync(string group, string proxyName, CancellationToken ct)
    {
        var body = new StringContent(
            $"{{\"name\":{System.Text.Json.JsonSerializer.Serialize(proxyName)}}}", Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Put, $"/proxies/{Uri.EscapeDataString(group)}", body, ct).ConfigureAwait(false);
    }

    public async Task<string> GetRulesAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "/rules", null, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /proxies/{group}/delay?timeout={ms}&amp;url={url}</c>: ядро само выполняет
    /// запрос через выбранный в группе прокси и отдаёт задержку. Контракт измерен на
    /// этом ядре: <c>{"delay":157}</c> для gstatic, 162 для google, 300 для x.com.
    /// Ноль и отсутствие ключа означают, что через прокси не прошло.
    /// </summary>
    public async Task<int> ProxyDelayAsync(string group, string url, int timeoutMs, CancellationToken ct)
    {
        var path = $"/proxies/{Uri.EscapeDataString(group)}/delay" +
                   $"?timeout={timeoutMs}&url={Uri.EscapeDataString(url)}";

        using var response = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("delay", out var delay) ? delay.GetInt32() : 0;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, _base + path) { Content = body };
        if (_secret is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);

        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;

        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        response.Dispose();
        throw new HttpRequestException($"mihomo {method} {path} — {status}: {text}");
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
