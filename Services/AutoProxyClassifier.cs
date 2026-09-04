using PsiTun.Models;

namespace PsiTun.Services;

public enum ProbeVerdict { Good, Bad, Inconclusive }

public static class AutoProxyClassifier
{
    public const int TimeoutMs = 5000;
    public const double SlowRatioMin = 3.0;          // рандом [3, 4)
    public const int LearnAfterBad = 3;
    public const int RevertAfterGood = 3;
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);
    public const int MaxAutoHosts = 65535;

    // bad: direct упал, ЛИБО (proxy жив И direct > 3..4× proxy)
    public static ProbeVerdict Classify(ProbeResult r)
    {
        if (!r.DirectOk && !r.ProxyOk) return ProbeVerdict.Inconclusive;
        if (!r.DirectOk) return ProbeVerdict.Bad;
        if (r.ProxyOk && r.DirectMs > SlowRatio() * r.ProxyMs) return ProbeVerdict.Bad;
        return ProbeVerdict.Good;
    }

    // откат консервативен: возвращаем в direct, только если direct явно работает
    public static bool IsHealthyForRevert(ProbeResult r) => r.DirectOk;

    private static double SlowRatio() => SlowRatioMin + Random.Shared.NextDouble(); // [3, 4)

    public static void SelfCheck()
    {
        static void Assert(bool cond, string msg)
        {
            if (!cond) throw new InvalidOperationException("classifier: " + msg);
        }

        Assert(Classify(new ProbeResult("x", DirectOk: false, DirectMs: 0, ProxyOk: true, ProxyMs: 100)) == ProbeVerdict.Bad,
            "direct fail must be Bad");
        Assert(Classify(new ProbeResult("x", DirectOk: true, DirectMs: 4000, ProxyOk: true, ProxyMs: 1000)) == ProbeVerdict.Bad,
            "4x slower must be Bad");
        Assert(Classify(new ProbeResult("x", DirectOk: true, DirectMs: 200, ProxyOk: true, ProxyMs: 300)) == ProbeVerdict.Good,
            "fast direct must be Good");
        Assert(Classify(new ProbeResult("x", DirectOk: true, DirectMs: 200, ProxyOk: false, ProxyMs: 0)) == ProbeVerdict.Good,
            "direct ok + proxy down must be Good");
        Assert(Classify(new ProbeResult("x", DirectOk: false, DirectMs: 0, ProxyOk: false, ProxyMs: 0)) == ProbeVerdict.Inconclusive,
            "both down must be Inconclusive");
        Assert(IsHealthyForRevert(new ProbeResult("x", DirectOk: true, DirectMs: 10, ProxyOk: false, ProxyMs: 0)),
            "revert needs DirectOk");
        Assert(!IsHealthyForRevert(new ProbeResult("x", DirectOk: false, DirectMs: 0, ProxyOk: true, ProxyMs: 10)),
            "revert must reject failed direct");
    }
}
