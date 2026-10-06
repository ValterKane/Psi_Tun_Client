using PsiTun.Models;
using PsiTun.Services;

namespace PsiTun.Tests;

/// <summary>
/// Признак блокировки — локальные сигналы ядра: через туннель проходит, напрямую
/// нет. Список заглушек приходит снаружи, чтобы правило оставалось чистым.
/// </summary>
public class AutoProxyClassifierTests
{
    private static readonly string[] Stubs = ["lawfilter.ertelecom.ru"];

    [Fact]
    public void DirectFails_ProxyReaches_IsBlocked() =>
        Assert.Equal(ProbeVerdict.Bad, AutoProxyClassifier.Classify(
            new ProbeResult("x", DirectOk: false, DirectMs: 5000, ProxyOk: true, ProxyMs: 150), Stubs));

    [Fact]
    public void StubRedirect_IsBlocked() =>
        Assert.Equal(ProbeVerdict.Bad, AutoProxyClassifier.Classify(
            new ProbeResult("x", true, 120, true, 150, 302, "lawfilter.ertelecom.ru"), Stubs));

    [Fact]
    public void StubSubdomain_IsBlocked() =>
        Assert.Equal(ProbeVerdict.Bad, AutoProxyClassifier.Classify(
            new ProbeResult("x", true, 120, true, 150, 302, "sub.lawfilter.ertelecom.ru"), Stubs));

    [Fact]
    public void RedirectToOrdinaryHost_IsNotBlocked() =>
        Assert.Equal(ProbeVerdict.Good, AutoProxyClassifier.Classify(
            new ProbeResult("x", true, 120, true, 150, 302, "www.example.com"), Stubs));

    [Fact]
    public void LegalReasons_IsBlocked() =>
        Assert.Equal(ProbeVerdict.Bad, AutoProxyClassifier.Classify(
            new ProbeResult("x", true, 120, true, 150, 451), Stubs));

    [Fact]
    public void Forbidden_IsNotBlocked() =>
        Assert.Equal(ProbeVerdict.Good, AutoProxyClassifier.Classify(
            new ProbeResult("x", true, 120, true, 150, 403), Stubs));

    [Fact]
    public void ProxyArmUnknown_IsInconclusive() =>
        Assert.Equal(ProbeVerdict.Inconclusive, AutoProxyClassifier.Classify(
            new ProbeResult("x", true, 120, ProxyOk: false, ProxyMs: 0), Stubs));

    [Fact]
    public void BothArmsDown_IsInconclusive() =>
        Assert.Equal(ProbeVerdict.Inconclusive, AutoProxyClassifier.Classify(
            new ProbeResult("x", false, 5000, ProxyOk: false, ProxyMs: 0), Stubs));

    [Fact]
    public void SlowDirect_IsBlocked() =>
        Assert.Equal(ProbeVerdict.Bad, AutoProxyClassifier.Classify(
            new ProbeResult("x", true, 4000, true, 200), Stubs));

    [Fact]
    public void EmptyStubList_NeverMatchesRedirect() =>
        Assert.False(AutoProxyClassifier.IsStubRedirect("lawfilter.ertelecom.ru", []));
}
