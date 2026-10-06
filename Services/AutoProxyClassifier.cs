using PsiTun.Models;

namespace PsiTun.Services;

public enum ProbeVerdict { Good, Bad, Inconclusive }

public static class AutoProxyClassifier
{
    public const int TimeoutMs = 5000;
    public const double SlowRatioMin = 3.0;          // рандом [3, 4)
    public const int RevertAfterGood = 3;
    public const int MaxAutoHosts = 65535;
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    /// <summary>Отказ пользователя: столько времени домен не спрашивается повторно.</summary>
    public static readonly TimeSpan DeclinedTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// Молчание: правило не добавляется, вопрос повторяется через этот срок.
    /// Значение выведено мной, владельцем не задавалось (см. план, открытый пункт 11).
    /// </summary>
    public static readonly TimeSpan SilenceRetry = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Заблокировано = через туннель проходит, а напрямую нет. Ручка через прокси
    /// неизвестна → вердикта нет: без неё признак неотличим от «сайт лежит».
    /// Обычные 403/502 признаком не служат — их отдаёт и рабочий сайт.
    /// </summary>
    public static ProbeVerdict Classify(ProbeResult r, IReadOnlyCollection<string> stubHosts)
    {
        if (!r.ProxyOk) return ProbeVerdict.Inconclusive;
        if (!r.DirectOk) return ProbeVerdict.Bad;
        if (IsStubRedirect(r.RedirectHost, stubHosts)) return ProbeVerdict.Bad;
        if (r.StatusCode == 451) return ProbeVerdict.Bad;
        if (r.ProxyMs > 0 && r.DirectMs > SlowRatio() * r.ProxyMs) return ProbeVerdict.Bad;
        return ProbeVerdict.Good;
    }

    /// <summary>Редирект на хост-заглушку провайдера: точное совпадение или поддомен.</summary>
    public static bool IsStubRedirect(string? host, IReadOnlyCollection<string> stubHosts) =>
        host is not null && stubHosts.Any(s =>
            host.Equals(s, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));

    // откат консервативен: возвращаем в direct, только если direct явно работает
    public static bool IsHealthyForRevert(ProbeResult r) => r.DirectOk;

    private static double SlowRatio() => SlowRatioMin + Random.Shared.NextDouble(); // [3, 4)

    public static void SelfCheck()
    {
        static void Assert(bool cond, string msg)
        {
            if (!cond) throw new InvalidOperationException("classifier: " + msg);
        }

        var stubs = new[] { "lawfilter.ertelecom.ru" };

        Assert(Classify(new ProbeResult("x", DirectOk: false, DirectMs: 5000, ProxyOk: true, ProxyMs: 150), stubs)
               == ProbeVerdict.Bad, "direct fail must be Bad");
        Assert(Classify(new ProbeResult("x", true, 120, true, 150, 302, "lawfilter.ertelecom.ru"), stubs)
               == ProbeVerdict.Bad, "stub redirect must be Bad");
        Assert(Classify(new ProbeResult("x", true, 120, true, 150, 302, "www.example.com"), stubs)
               == ProbeVerdict.Good, "ordinary redirect must be Good");
        Assert(Classify(new ProbeResult("x", true, 120, true, 150, 451), stubs)
               == ProbeVerdict.Bad, "451 must be Bad");
        Assert(Classify(new ProbeResult("x", true, 120, true, 150, 403), stubs)
               == ProbeVerdict.Good, "403 must be Good");
        Assert(Classify(new ProbeResult("x", true, 120, ProxyOk: false, ProxyMs: 0), stubs)
               == ProbeVerdict.Inconclusive, "unknown tunnel arm must be Inconclusive");
        Assert(Classify(new ProbeResult("x", false, 5000, ProxyOk: false, ProxyMs: 0), stubs)
               == ProbeVerdict.Inconclusive, "both arms down must be Inconclusive");
        Assert(Classify(new ProbeResult("x", true, 4000, true, 200), stubs)
               == ProbeVerdict.Bad, "4x slower must be Bad");
        Assert(IsHealthyForRevert(new ProbeResult("x", true, 10, false, 0)),
            "revert needs DirectOk");
        Assert(!IsHealthyForRevert(new ProbeResult("x", false, 0, true, 10)),
            "revert must reject failed direct");
    }
}
