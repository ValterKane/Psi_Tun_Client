namespace PsiTun.Models;

/// <summary>
/// Итог пробы домена двумя руками: напрямую и через туннель.
/// <c>ProxyOk</c> ложен, когда рука «через прокси» неизвестна — тогда вердикта не
/// выносится. <c>RedirectHost</c> несёт хост из <c>Location</c> при 3xx: редирект
/// на заглушку провайдера — признак блокировки.
/// </summary>
public record ProbeResult(
    string Host,
    bool DirectOk,
    long DirectMs,
    bool ProxyOk,
    long ProxyMs,
    int? StatusCode = null,
    string? RedirectHost = null);
