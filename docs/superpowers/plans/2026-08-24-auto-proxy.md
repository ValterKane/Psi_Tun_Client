# Авто-прокси: адаптивная маршрутизация — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Модуль, который по DNS/connection-логу sing-box находит «серые» хосты, прощупывает их через direct и proxy, и при подтверждённой блокировке/троттлинге автоматически заносит как доменное правило в xray (с per-core reload), с auto-heal по повторным заходам.

**Architecture:** sing-box (log level `info`) отдаёт строки `inbound connection to <host>:<port>` — это источник кандидатов. `ProbeService` меряет хост через два существующих xray-SOCKS-инбаунда (10810 blocked-only→direct, 10811 force→proxy). `AutoProxyEngine` классифицирует (гистерезис 2×bad), пишет `RoutingRule{IsAutoLearned}` в `routing-rules.json` и перезапускает **только xray** (`xray -test` перед рестартом). Auto-heal срабатывает только при повторном заходе пользователя на хост после TTL=24ч. sing-box TUN не трогаем никогда.

**Tech Stack:** .NET 9 WPF (net9.0-windows), xray-core 26.3.27, sing-box 1.13.14, System.Net.Http (SOCKS5 через `WebProxy("socks5://…")`). Без новых NuGet.

**Spec:** `docs/superpowers/specs/2026-08-24-auto-proxy-design.md`

## Global Constraints

- **Строгая схема sing-box 1.13.14**: неизвестное поле в конфиге = FATAL. Меняем только `log.level` (`warn → info`). Никаких новых полей (`disable_color` и пр. не существуют).
- **sing-box никогда не перезапускаем** из модуля авто-прокси. Рестарт только xray. TUN живёт.
- `process_name`/`.exe`-правила остаются ручными — модуль пишет только `Domain`-правила.
- **Auto-heal только по повторному заходу**: фоновых поллеров нет.
- `RevertAfterGood = 3` (по таблице порогов спеки; формулировка «второго посещения» в спеке — нижняя граница: удаление никогда не раньше 2-го визита; константа одна, меняется в `AutoProxyClassifier`).
- Нет новых NuGet-пакетов. Копирайт UI — русский.
- Сборка/проверка: `dotnet build PsiTun.csproj -c Debug` и `PsiTun.exe --selfcheck` (exit 0 = ок).

## Spike-факты (уже проверены на этой машине)

- sing-box 1.13.14 на `log.level: "info"` печатает `inbound/socks[socks-in]: inbound connection to <host>:<port>` (и `inbound/tun[tun]: …`) — **готовый источник хостов**, DNS-специфичный лог не нужен.
- Вывод sing-box содержит ANSI-коды (`\x1b[36m…\x1b[0m`) даже при redirect — парсер должен их срезать.
- `xray -test -c <path>`: exit 0 = валидный конфиг, exit 2 = невалидный.
- На этой машине нет интернета — live-прощупывание в CI/selfcheck не работает. Runnable-чеки = чистый классификатор + парсер (см. `--selfcheck`).
- Порты: `XrayInboundPort`=10810 (blocked-only), force-proxy=10811 (захардкожен в `ConfigGenerator.BuildInbounds`).

---

### Task 1: Модель, классификатор, selfcheck-харнесс

**Files:**
- Modify: `Models/RoutingRule.cs` (добавить 2 поля)
- Create: `Models/ProbeResult.cs`
- Create: `Services/AutoProxyClassifier.cs`
- Modify: `App.xaml.cs` (блок `--selfcheck` в начале `OnStartup`)

**Interfaces:**
- Produces:
  - `RoutingRule.IsAutoLearned : bool`, `RoutingRule.LastCheckedAt : DateTime?`
  - `record ProbeResult(string Host, bool DirectOk, long DirectMs, bool ProxyOk, long ProxyMs)`
  - `enum ProbeVerdict { Good, Bad, Inconclusive }`
  - `static class AutoProxyClassifier`: константы `TimeoutMs=5000`, `LearnAfterBad=2`, `RevertAfterGood=3`, `Ttl=24ч`, `Cooldown=5мин`, `MaxAutoHosts=65535`; методы `Classify(ProbeResult)→ProbeVerdict`, `IsHealthyForRevert(ProbeResult)→bool`, `SelfCheck()`.

- [ ] **Step 1: Поля модели**

`Models/RoutingRule.cs` — после `public bool IsDefault { get; set; }` добавить:

```csharp
public bool IsAutoLearned { get; set; }      // найдено модулем авто-прокси
public DateTime? LastCheckedAt { get; set; } // для TTL-перепроверки
```

- [ ] **Step 2: ProbeResult**

`Models/ProbeResult.cs`:

```csharp
namespace PsiTun.Models;

public record ProbeResult(string Host, bool DirectOk, long DirectMs, bool ProxyOk, long ProxyMs);
```

- [ ] **Step 3: Классификатор**

`Services/AutoProxyClassifier.cs`:

```csharp
using PsiTun.Models;

namespace PsiTun.Services;

public enum ProbeVerdict { Good, Bad, Inconclusive }

public static class AutoProxyClassifier
{
    public const int TimeoutMs = 5000;
    public const double SlowRatioMin = 3.0;          // рандом [3, 4)
    public const int LearnAfterBad = 2;
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
}
```

- [ ] **Step 4: SelfCheck классификатора** — добавить в тот же файл:

```csharp
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
```

- [ ] **Step 5: Харнесс `--selfcheck`** — `App.xaml.cs`, в `OnStartup` сразу после `base.OnStartup(e);`:

```csharp
        // Ранний выход для самопроверки (exit 0 = ok, иначе selfcheck.log)
        if (e.Args.Contains("--selfcheck"))
        {
            try
            {
                AutoProxyClassifier.SelfCheck();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(BaseDir, "selfcheck.log"), ex.ToString());
                Environment.Exit(1);
            }
        }
```

- [ ] **Step 6: Сборка + проверка**

Run: `dotnet build PsiTun.csproj -c Debug` — Expected: Build succeeded.
Run: `bin/Debug/net9.0-windows/PsiTun.exe --selfcheck; echo $?` — Expected: `0`, файла `selfcheck.log` нет.

- [ ] **Step 7: Commit**

```bash
git add Models/RoutingRule.cs Models/ProbeResult.cs Services/AutoProxyClassifier.cs App.xaml.cs
git commit -m "feat: auto-proxy model fields, classifier, selfcheck harness"
```

---

### Task 2: CandidateCollector (парсер connection-лога) + log level info

**Files:**
- Modify: `Services/SingBoxConfigGenerator.cs` (log level + сделать `DnsHosts` internal)
- Create: `Services/CandidateCollector.cs`
- Modify: `App.xaml.cs` (добавить `CandidateCollector.SelfCheck()` в `--selfcheck`)

**Interfaces:**
- Produces:
  - `sealed class CandidateCollector`: `HandleLine(string raw)`, `TryDequeue(out string host)→bool`, `SetSkipHosts(IEnumerable<string>)`, `static SelfCheck()`.
- Consumes: `App.Core.OnLog` (в Task 5 подпишем), `SingBoxConfigGenerator.DnsHosts` (стал internal).

- [ ] **Step 1: log level + internal DnsHosts**

`Services/SingBoxConfigGenerator.cs`: в `BuildDnsConfig`/`Generate` поле `["level"]` поменять `"warn"` → `"info"` (строка `["level"] = "warn",` внутри `log`). **Других полей в `log` не добавлять** (строгая схема).

В том же файле: `private static readonly Dictionary<string, string[]> DnsHosts` → `internal static readonly Dictionary<string, string[]> DnsHosts`.

- [ ] **Step 2: Парсер**

`Services/CandidateCollector.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace PsiTun.Services;

public sealed class CandidateCollector
{
    private static readonly Regex AnsiStrip = new(@"\x1b\[[0-9;]*m");
    private static readonly Regex ConnToRegex = new(@"inbound connection to ([A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?):\d+");

    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new();
    private readonly HashSet<string> _skip = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _queue.Count;

    public void SetSkipHosts(IEnumerable<string> hosts)
    {
        lock (_skip)
        {
            _skip.Clear();
            foreach (var h in hosts) _skip.Add(h);
        }
    }

    public void HandleLine(string raw)
    {
        var clean = AnsiStrip.Replace(raw, "");
        var m = ConnToRegex.Match(clean);
        if (!m.Success) return;
        var host = m.Groups[1].Value;
        if (!LooksLikeHost(host)) return;

        lock (_skip) if (_skip.Contains(host)) return;

        if (_lastSeen.TryGetValue(host, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(5)) return;
        _lastSeen[host] = DateTime.UtcNow;
        _queue.Enqueue(host);
    }

    public bool TryDequeue(out string host) => _queue.TryDequeue(out host!);

    private static bool LooksLikeHost(string h) =>
        h.Contains('.') &&
        !h.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
        !IPAddress.TryParse(h, out _) &&
        h.All(c => !char.IsWhiteSpace(c));
}
```

- [ ] **Step 3: SelfCheck парсера** — добавить в `CandidateCollector.cs`:

```csharp
    public static void SelfCheck()
    {
        static void Assert(bool cond, string msg)
        {
            if (!cond) throw new InvalidOperationException("collector: " + msg);
        }

        var c = new CandidateCollector();
        c.HandleLine("\u001b[36mINFO\u001b[0m [\u001b[38;5;66m123\u001b[0m 12ms] inbound/socks[socks-in]: inbound connection to example.com:443");
        c.HandleLine("inbound/tun[tun]: inbound connection to habr.com:443");
        c.HandleLine("outbound/direct[direct]: outbound connection to example.com:443"); // не матчится
        c.HandleLine("inbound connection to 192.168.1.1:443"); // IP → отфильтрован
        Assert(c.TryDequeue(out var h1) && h1 == "example.com", "socks line parse");
        Assert(c.TryDequeue(out var h2) && h2 == "habr.com", "tun line parse");
        Assert(!c.TryDequeue(out _), "no more candidates");
    }
```

- [ ] **Step 4: Дописать харнесс** — в `App.xaml.cs` блок `--selfcheck`:

```csharp
                AutoProxyClassifier.SelfCheck();
                CandidateCollector.SelfCheck();
                Environment.Exit(0);
```

- [ ] **Step 5: Сборка + проверка**

Run: `dotnet build PsiTun.csproj -c Debug` — Expected: Build succeeded.
Run: `bin/Debug/net9.0-windows/PsiTun.exe --selfcheck; echo $?` — Expected: `0`.

- [ ] **Step 6: Commit**

```bash
git add Services/SingBoxConfigGenerator.cs Services/CandidateCollector.cs App.xaml.cs
git commit -m "feat: candidate collector from sing-box info log + log level info"
```

---

### Task 3: ProbeService (двухпутевое HTTP-прощупывание)

**Files:**
- Create: `Services/ProbeService.cs`

**Interfaces:**
- Produces: `static class ProbeService`:
  - `Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default)` — оба пути;
  - `Task<ProbeResult> ProbeDirectOnlyAsync(string host, CancellationToken ct = default)` — только direct (для auto-heal).
- Consumes: `App.Settings.XrayInboundPort` (10810), константа 10811, `AutoProxyClassifier.TimeoutMs`, `ProbeResult`.

- [ ] **Step 1: Реализация**

`Services/ProbeService.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using PsiTun.Models;

namespace PsiTun.Services;

public static class ProbeService
{
    private const int ForceProxyPort = 10811; // force-in в ConfigGenerator.BuildInbounds
    private static readonly SemaphoreSlim Gate = new(Math.Max(1, Environment.ProcessorCount / 2));

    public static async Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default)
    {
        using var _ = await Gate.WaitAsync(ct);
        var direct = await ProbeOnceAsync(host, App.Settings.XrayInboundPort, ct);
        var proxy = await ProbeOnceAsync(host, ForceProxyPort, ct);
        return new ProbeResult(host, direct.ok, direct.ms, proxy.ok, proxy.ms);
    }

    public static async Task<ProbeResult> ProbeDirectOnlyAsync(string host, CancellationToken ct = default)
    {
        using var _ = await Gate.WaitAsync(ct);
        var direct = await ProbeOnceAsync(host, App.Settings.XrayInboundPort, ct);
        return new ProbeResult(host, direct.ok, direct.ms, false, 0);
    }

    // ok = получен ЛЮБОЙ HTTP-ответ (статус не важен: 403/502 ≠ блокировка,
    // блок = сбой соединения/таймаут). ms = полная длительность попытки.
    private static async Task<(bool ok, long ms)> ProbeOnceAsync(string host, int socksPort, CancellationToken ct)
    {
        var proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}");
        using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(AutoProxyClassifier.TimeoutMs) };
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, $"https://{host}/");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            return (true, sw.ElapsedMilliseconds);
        }
        catch
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds);
        }
    }
}
```

- [ ] **Step 2: Сборка**

Run: `dotnet build PsiTun.csproj -c Debug` — Expected: Build succeeded. (IO-зависимый код — live-проверка в Task 7 вручную на живой машине.)

- [ ] **Step 3: Commit**

```bash
git add Services/ProbeService.cs
git commit -m "feat: probe service dual-path (direct 10810 / force-proxy 10811)"
```

---

### Task 4: Per-core reload xray (валидация + рестарт)

**Files:**
- Modify: `Services/CoreManager.cs` (добавить `RestartXrayAsync`, `ValidateXrayConfig`)
- Modify: `App.xaml.cs` (вынести `WriteConfigsAsync`, добавить `ReloadXrayAsync`, использовать в `ConnectAsync`)

**Interfaces:**
- Produces:
  - `CoreManager.RestartXrayAsync() → Task<bool>`
  - `App.WriteConfigsAsync() → Task` (private)
  - `App.ReloadXrayAsync() → Task<bool>`
- Consumes: `App.ConfigPath`, `App.CoreExe`, `App.Settings.XrayInboundPort`, `ConfigGenerator`, `SingBoxConfigGenerator`, `Rules.Load()`.

- [ ] **Step 1: CoreManager — валидация + рестарт xray**

`Services/CoreManager.cs`, добавить (после `StartAsync`):

```csharp
    // Hot-reload xray: валидация → стоп → старт → ждём порт. sing-box не трогаем.
    public async Task<bool> RestartXrayAsync()
    {
        if (!ValidateXrayConfig(_xrayConfigPath))
        {
            OnLog?.Invoke("[Core] xray config invalid, reload aborted (old config still running)");
            return false;
        }
        StopXray();
        await WaitForPortReleaseAsync(App.Settings.XrayInboundPort);
        _xrayProcess = StartProcess(_xrayPath, _xrayConfigPath, "xray");
        var ready = await WaitForPortAsync(App.Settings.XrayInboundPort, 10);
        OnLog?.Invoke(ready ? "[Core] xray reloaded" : "[Core] xray failed to reload");
        return ready;
    }

    private bool ValidateXrayConfig(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _xrayPath,
                Arguments = $"-test -c \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(10000);
            return p is { ExitCode: 0 };
        }
        catch { return false; }
    }
```

- [ ] **Step 2: App — WriteConfigsAsync + ReloadXrayAsync**

`App.xaml.cs`, добавить (рядом с `ConnectAsync`):

```csharp
    // Общая генерация обоих конфигов (переиспользуется ConnectAsync и ReloadXrayAsync)
    private async Task WriteConfigsAsync()
    {
        var customRules = Rules.Load();
        var config = ConfigGenerator.Generate(Servers, SelectedServerIndex, customRules: customRules);
        await File.WriteAllTextAsync(ConfigPath, config);
        var singBoxConfig = SingBoxConfigGenerator.Generate(Settings, Servers, SelectedServerIndex, customRules: customRules);
        await File.WriteAllTextAsync(SingBoxConfigPath, singBoxConfig);
    }

    public async Task<bool> ReloadXrayAsync()
    {
        if (Core is not { IsRunning: true }) return false;
        await WriteConfigsAsync();
        return await Core.RestartXrayAsync();
    }
```

- [ ] **Step 3: ConnectAsync — использовать WriteConfigsAsync**

В `App.ConnectAsync()` заменить блок генерации (строки с `ConfigGenerator.Generate` и `SingBoxConfigGenerator.Generate` плюс два `WriteAllTextAsync`) на одну строку:

```csharp
        await WriteConfigsAsync();
```

(Строку `var customRules = Rules.Load();` тоже удалить — она переехала в `WriteConfigsAsync`.)

- [ ] **Step 4: Сборка**

Run: `dotnet build PsiTun.csproj -c Debug` — Expected: Build succeeded. Ручная проверка reload в Task 7.

- [ ] **Step 5: Commit**

```bash
git add Services/CoreManager.cs App.xaml.cs
git commit -m "feat: per-core xray reload with config validation"
```

---

### Task 5: AutoProxyEngine (learn + revisit-triggered heal) и жизненный цикл

**Files:**
- Create: `Services/AutoProxyEngine.cs`
- Modify: `App.xaml.cs` (поле + старт/стоп в `ConnectAsync`/`Disconnect`)

**Interfaces:**
- Produces: `sealed class AutoProxyEngine : IDisposable`: `Start()`, `Dispose()`, `Log: Action<string>?`.
- Consumes: `CandidateCollector`, `ProbeService`, `AutoProxyClassifier`, `App.Rules`, `App.CurrentApp().ReloadXrayAsync()`, `App.Core.OnLog`, `App.Servers`, `SingBoxConfigGenerator.DnsHosts`.

- [ ] **Step 1: Реализация движка**

`Services/AutoProxyEngine.cs`:

```csharp
using System.Collections.Concurrent;
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
```

- [ ] **Step 2: Жизненный цикл в App**

`App.xaml.cs`:
- Поле: `private AutoProxyEngine? _autoProxy;`
- В `ConnectAsync`, в блоке успеха (после `_mainWindow?.UpdateStatus(...)`), добавить:

```csharp
                _autoProxy?.Dispose();
                _autoProxy = new AutoProxyEngine { Log = line => _mainWindow?.AppendLog(line) };
                _autoProxy.Start();
```

- В `Disconnect()`, после `Core?.Dispose(); Core = null;`, добавить:

```csharp
        _autoProxy?.Dispose();
        _autoProxy = null;
```

- [ ] **Step 3: Сборка**

Run: `dotnet build PsiTun.csproj -c Debug` — Expected: Build succeeded. Live-проверка в Task 7.

- [ ] **Step 4: Commit**

```bash
git add Services/AutoProxyEngine.cs App.xaml.cs
git commit -m "feat: auto-proxy engine (learn + revisit-triggered heal)"
```

---

### Task 6: UI вкладка «Авто-прокси» + полировка

**Files:**
- Modify: `ViewModels/RoutingRulesViewModel.cs`
- Modify: `Views/RoutingRulesWindow.xaml`
- Modify: `Views/RoutingRulesWindow.xaml.cs` (если понадобится — см. ниже)

**Interfaces:**
- Produces: `RoutingRulesViewModel.AutoRules : ObservableCollection<RoutingRule>`, `DeleteAutoRuleCommand : ICommand`.
- Consumes: `App.Rules.Load()/Save()`, `App.CurrentApp().ReloadXrayAsync()`.

- [ ] **Step 1: ViewModel**

`ViewModels/RoutingRulesViewModel.cs`:
- Добавить коллекцию и команду:

```csharp
    public ObservableCollection<RoutingRule> AutoRules { get; } = [];
    public ICommand DeleteAutoRuleCommand { get; }
```

- В конструкторе:

```csharp
        DeleteAutoRuleCommand = new RelayCommand(_ => DeleteAutoRule(_ as RoutingRule));
```

- `LoadRules()` заменить на разделение:

```csharp
    private void LoadRules()
    {
        Rules.Clear();
        AutoRules.Clear();
        foreach (var r in App.Rules.Load())
        {
            if (r.IsAutoLearned) AutoRules.Add(r);
            else Rules.Add(r);
        }
    }
```

- Добавить метод:

```csharp
    private void DeleteAutoRule(RoutingRule? rule)
    {
        if (rule == null) return;
        var rules = App.Rules.Load();
        rules.RemoveAll(r => r.IsAutoLearned && r.MatchType == rule.MatchType && r.Value == rule.Value);
        App.Rules.Save(rules);
        AutoRules.Remove(rule);
        _ = App.CurrentApp().ReloadXrayAsync();
    }
```

(`RelayCommand` уже используется в проекте; `_ = ReloadXrayAsync()` — fire-and-forget: если не подключено, метод сам вернёт false.)

- [ ] **Step 2: XAML — TabControl с двумя вкладками**

`Views/RoutingRulesWindow.xaml`: обернуть существующий `DataGrid` (строки 67–129) в `TabControl`, добавив вкладку «Авто-прокси». Вместо:

```xml
        <DataGrid x:Name="RulesGrid" Grid.Row="0" Margin="0,0,0,10" ...>
            ...
        </DataGrid>
```

положить:

```xml
        <TabControl Grid.Row="0" Margin="0,0,0,10" Background="#252535"
                    Foreground="#E0E0E0" BorderThickness="0">
            <TabItem Header="Правила" Foreground="#E0E0E0">
                <DataGrid x:Name="RulesGrid" Margin="8" AlternationCount="2"
                          CanUserResizeRows="False" RowHeight="32"
                          ItemsSource="{Binding Rules}">
                    <!-- все существующие DataGrid.Columns из прежней разметки, без изменений -->
                </DataGrid>
            </TabItem>
            <TabItem Header="Авто-прокси" Foreground="#E0E0E0">
                <DataGrid x:Name="AutoGrid" Margin="8" AlternationCount="2"
                          CanUserResizeRows="False" RowHeight="32"
                          ItemsSource="{Binding AutoRules}">
                    <DataGrid.Columns>
                        <DataGridTextColumn Header="Хост" Binding="{Binding Value}" Width="*"/>
                        <DataGridTextColumn Header="Проверен"
                            Binding="{Binding LastCheckedAt, StringFormat=yyyy-MM-dd HH:mm, TargetNullValue=''}"
                            Width="140"/>
                        <DataGridTemplateColumn Header="" Width="60">
                            <DataGridTemplateColumn.CellTemplate>
                                <DataTemplate>
                                    <Button Content="✕" Width="24" Height="24"
                                            Background="Transparent" Foreground="#F44336"
                                            BorderThickness="0" FontSize="14" FontWeight="Bold"
                                            Command="{Binding DataContext.DeleteAutoRuleCommand, Source={x:Reference window}}"
                                            CommandParameter="{Binding}"/>
                                </DataTemplate>
                            </DataGridTemplateColumn.CellTemplate>
                        </DataGridTemplateColumn>
                    </DataGrid.Columns>
                </DataGrid>
            </TabItem>
        </TabControl>
```

(Стили `DataGrid`/`DataGridColumnHeader`/`DataGridCell` из `Window.Resources` применяются к обеим сеткам автоматически.)

- [ ] **Step 3: Полировка кнопочного ряда (нижняя панель)**

`Views/RoutingRulesWindow.xaml`, панель `StackPanel Grid.Row="1"`: выровнять высоты и отступы — все кнопки `Height="30"`, `Margin="0,0,8,0` у всех, кроме последней:

```xml
        <StackPanel Grid.Row="1" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,4,0,0">
            <Button Content="+ Добавить" Style="{StaticResource ActionButton}" Height="30"
                    Background="#5C6BC0" Command="{Binding AddRuleCommand}" Margin="0,0,8,0"/>
            <Button Content="Сбросить" Style="{StaticResource ActionButton}" Height="30"
                    Background="#455A64" Command="{Binding ResetDefaultsCommand}" Margin="0,0,8,0"/>
            <Button Content="Сохранить" Style="{StaticResource ActionButton}" Height="30"
                    Background="#43A047" Command="{Binding SaveCommand}"/>
        </StackPanel>
```

- [ ] **Step 4: Сборка**

Run: `dotnet build PsiTun.csproj -c Debug` — Expected: Build succeeded. Открыть окно правил вручную: обе вкладки отображаются, удаление авто-хоста работает (правило исчезает из `routing-rules.json`).

- [ ] **Step 5: Commit**

```bash
git add ViewModels/RoutingRulesViewModel.cs Views/RoutingRulesWindow.xaml
git commit -m "feat: auto-proxy UI tab in routing rules window + polish"
```

---

### Task 7: End-to-end ручная верификация (на живой машине с интернетом)

**Files:** нет (проверка, без кода)

- [ ] **Step 1: Харнесс**

Run: `dotnet build PsiTun.csproj -c Debug` — Expected: Build succeeded.
Run: `bin/Debug/net9.0-windows/PsiTun.exe --selfcheck; echo $?` — Expected: `0`, нет `selfcheck.log`.

- [ ] **Step 2: Обучение**

1. Подключиться (TUN). В логе окна появляется `[auto] engine started`.
2. Открыть заблокированный/троттлящийся сайт. В течение ~2 прогонов (кулдаун 5 мин, гистерезис 2×bad):
   - в `routing-rules.json` появляется `{ "IsAutoLearned": true, "MatchType": 1, "Value": "<host>", "Action": 0, ... }` (`RuleMatchType.Domain`=1, `RuleAction.Proxy`=0);
   - в логе `[auto] learned: <host>` и `[Core] xray reloaded`;
   - TUN не падал (sing-box не перезапускался) — соединение не рвалось.
3. Вкладка «Авто-прокси» в окне правил показывает хост с датой проверки.

- [ ] **Step 3: Auto-heal**

Для ускоренного теста временно уменьшить `AutoProxyClassifier.Ttl` до `TimeSpan.FromMinutes(1)`, пересобрать:
1. Отключить блокировку/открыть хост напрямую (или просто повторить заходы).
2. После 3 «хороших» повторных заходов (после TTL) правило удаляется: `[auto] healed, moved back to direct: <host>` + `[Core] xray reloaded`.
3. Вернуть `Ttl = 24ч`.

- [ ] **Step 4: Ручное удаление**

В окне правил удалить авто-хост — правило исчезает из файла, `[Core] xray reloaded`, ложное срабатывание устранено.

- [ ] **Step 5: Регресс**

1. Ручные правила: добавить/сохранить — работает как раньше (полный disconnect→connect, поведение не меняли).
2. Смена сервера — как раньше.
3. `--selfcheck` по-прежнему `0`.
