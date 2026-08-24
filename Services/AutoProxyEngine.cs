using System.Collections.Concurrent;
using System.Linq;
using PsiTun.Models;

namespace PsiTun.Services;

public sealed class AutoProxyEngine : IDisposable
{
    private readonly CandidateCollector _collector = new();
    private readonly ConcurrentDictionary<string, int> _badStreak = new();
    private readonly ConcurrentDictionary<string, int> _goodStreak = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public Action<string>? Log { get; set; }

    public void Start()
    {
        if (App.Core is { } core) core.OnLog += _collector.HandleLine;
        _collector.SetSkipHosts(BuildSkipHosts());
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        Log?.Invoke("[auto] engine started");
    }

    private IEnumerable<string> BuildSkipHosts()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };
        if (App.SelectedServerIndex >= 0 && App.SelectedServerIndex < App.Servers.Count)
            hosts.Add(App.Servers[App.SelectedServerIndex].Address);
        foreach (var d in SingBoxConfigGenerator.DnsHosts.Keys) hosts.Add(d);
        return hosts;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_collector.TryDequeue(out var host))
                    await HandleCandidateAsync(host, ct);
                else
                    await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* движок не роняет приложение */ }
        }
    }

    private async Task HandleCandidateAsync(string host, CancellationToken ct)
    {
        var rules = App.Rules.Load();
        var auto = rules.FirstOrDefault(r => r.IsAutoLearned && MatchesHost(r, host));

        if (auto != null)
        {
            // auto-heal только по повторному заходу после TTL
            if (auto.LastCheckedAt is { } last && DateTime.UtcNow - last < AutoProxyClassifier.Ttl) return;
            await RecheckAutoAsync(auto, rules, ct);
            return;
        }

        if (IsKnown(rules, host)) return;

        var probe = await ProbeService.ProbeAsync(host, ct);
        var verdict = AutoProxyClassifier.Classify(probe);
        if (verdict == ProbeVerdict.Inconclusive) return;
        if (verdict == ProbeVerdict.Good) { _badStreak[host] = 0; return; }

        var bad = _badStreak.AddOrUpdate(host, 1, (_, n) => n + 1);
        if (bad < AutoProxyClassifier.LearnAfterBad) return;
        Learn(host, rules);
    }

    private async Task RecheckAutoAsync(RoutingRule auto, List<RoutingRule> rules, CancellationToken ct)
    {
        var probe = await ProbeService.ProbeDirectOnlyAsync(auto.Value, ct);
        auto.LastCheckedAt = DateTime.UtcNow;
        if (AutoProxyClassifier.IsHealthyForRevert(probe))
        {
            var good = _goodStreak.AddOrUpdate(auto.Value, 1, (_, n) => n + 1);
            if (good >= AutoProxyClassifier.RevertAfterGood)
            {
                _goodStreak.TryRemove(auto.Value, out _);
                rules.Remove(auto);
                App.Rules.Save(rules);
                Log?.Invoke($"[auto] healed, moved back to direct: {auto.Value}");
                _ = App.CurrentApp().ReloadXrayAsync();
                return;
            }
        }
        else
        {
            _goodStreak[auto.Value] = 0;
        }
        App.Rules.Save(rules); // персистим LastCheckedAt
    }

    private void Learn(string host, List<RoutingRule> rules)
    {
        _badStreak.TryRemove(host, out _);
        var existing = rules.FirstOrDefault(r => r.IsAutoLearned && MatchesHost(r, host));
        if (existing is null)
        {
            if (rules.Count(r => r.IsAutoLearned) >= AutoProxyClassifier.MaxAutoHosts)
            {
                var oldest = rules.Where(r => r.IsAutoLearned)
                    .OrderBy(r => r.LastCheckedAt ?? DateTime.MinValue).First();
                rules.Remove(oldest);
                Log?.Invoke($"[auto] evicted oldest: {oldest.Value}");
            }
            existing = new RoutingRule
            {
                MatchType = RuleMatchType.Domain,
                Action = RuleAction.Proxy,
                Value = host,
                IsAutoLearned = true,
                IsEnabled = true,
                Description = $"auto: {DateTime.Now:yyyy-MM-dd HH:mm}",
                LastCheckedAt = DateTime.UtcNow
            };
            rules.Add(existing);
            Log?.Invoke($"[auto] learned: {host}");
            App.Rules.Save(rules);
            _ = App.CurrentApp().ReloadXrayAsync();
        }
        else
        {
            existing.LastCheckedAt = DateTime.UtcNow;
            App.Rules.Save(rules);
        }
    }

    private static bool MatchesHost(RoutingRule r, string host) =>
        r.MatchType == RuleMatchType.Domain &&
        host.Equals(r.Value, StringComparison.OrdinalIgnoreCase);

    private static bool IsKnown(List<RoutingRule> rules, string host) =>
        rules.Any(r => r.IsEnabled && (MatchesHost(r, host) || IsSubdomain(host, r.Value)));

    private static bool IsSubdomain(string host, string parent) =>
        parent.Length > 0 && host.Length > parent.Length &&
        host.EndsWith("." + parent, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _cts?.Cancel();
        if (App.Core is { } core) core.OnLog -= _collector.HandleLine;
        _cts?.Dispose();
        _cts = null;
    }
}
