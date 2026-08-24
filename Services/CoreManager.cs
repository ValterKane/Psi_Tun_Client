using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PsiTun.Services;

public class CoreManager : IDisposable
{
    private Process? _xrayProcess;
    private Process? _singBoxProcess;
    private readonly string _xrayPath;
    private readonly string _xrayConfigPath;
    private readonly string _singBoxPath;
    private readonly string _singBoxConfigPath;
    private readonly List<string> _errorLines = [];
    private bool _disposed;

    public event Action<string>? OnLog;
    public event Action? OnExited;
    public event Action<bool>? OnTunStatusChanged;

    public string LastError => _errorLines.Count > 0
        ? string.Join("\n", _errorLines.TakeLast(5))
        : "";

    public int? ExitCode { get; private set; }

    public CoreManager(string xrayPath, string xrayConfigPath,
                       string singBoxPath, string singBoxConfigPath)
    {
        _xrayPath = xrayPath;
        _xrayConfigPath = xrayConfigPath;
        _singBoxPath = singBoxPath;
        _singBoxConfigPath = singBoxConfigPath;
    }

    public bool IsRunning =>
        (_xrayProcess is { HasExited: false }) &&
        (_singBoxProcess is { HasExited: false });

    public async Task StartAsync()
    {
        if (IsRunning) return;

        KillStaleProcesses();

        // Wait for ports to be released after killing stale processes
        await WaitForPortReleaseAsync(App.Settings.XrayInboundPort, App.Settings.HttpPort);

        // Start adapter cleanup in background (runs in parallel with Xray)
        var cleanupTask = CheckTunAdapterExists()
            ? CleanupAdapterAsync()
            : Task.CompletedTask;

        _errorLines.Clear();
        ExitCode = null;

        // 1. Start Xray immediately (SOCKS server for sing-box)
        _xrayProcess = StartProcess(_xrayPath, _xrayConfigPath, "xray");
        OnLog?.Invoke("[Core] Starting Xray (proxy)...");

        // 2. Wait for Xray SOCKS port to be ready
        var xrayReady = await WaitForPortAsync(App.Settings.XrayInboundPort, 10);
        if (!xrayReady)
        {
            OnLog?.Invoke("[Core] Xray failed to start");
            ExitCode = _xrayProcess.ExitCode;
            StopXray();
            return;
        }
        OnLog?.Invoke("[Core] Xray ready");

        // 3. Start sing-box (TUN + DNS + routing, connects to Xray SOCKS)
        if (!File.Exists(_singBoxPath))
        {
            OnLog?.Invoke("[Core] sing-box.exe not found, running proxy-only");
            return;
        }

        // Wait for adapter cleanup to finish before starting sing-box
        await cleanupTask;

        // Retry loop: on Win10 TUN adapter creation can fail intermittently
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (attempt > 1)
            {
                OnLog?.Invoke($"[Core] Retrying sing-box (attempt {attempt}/3)...");
                await CleanupAdapterAsync();
                await Task.Delay(1000);
            }

            _singBoxProcess = StartProcess(_singBoxPath, _singBoxConfigPath, "sing-box");
            OnLog?.Invoke($"[Core] Starting sing-box (TUN+DNS)...");

            // Adaptive warmup: longer initial delay on retries (slow systems need more time)
            var warmupMs = attempt switch { 1 => 500, 2 => 1500, _ => 3000 };

            // 4. Wait for sing-box port (up to ~15s with progressive backoff)
            var sbReady = await WaitForPortAsync(App.Settings.HttpPort, 20, warmupMs);

            // If the process already exited with an error, retry
            if (!sbReady && _singBoxProcess is { HasExited: true })
            {
                var code = _singBoxProcess.ExitCode;
                OnLog?.Invoke($"[Core] sing-box exited early (code {code}), will retry");
                _singBoxProcess.Dispose();
                _singBoxProcess = null;
                continue;
            }

            if (sbReady)
            {
                OnLog?.Invoke("[Core] sing-box ready");
                OnTunStatusChanged?.Invoke(true);
                return;
            }

            // Port not ready but process still alive — give it one more wait window
            if (_singBoxProcess is { HasExited: false })
            {
                OnLog?.Invoke("[Core] sing-box still initializing, waiting longer...");
                sbReady = await WaitForPortAsync(App.Settings.HttpPort, 20, 0);
                if (sbReady)
                {
                    OnLog?.Invoke("[Core] sing-box ready");
                    OnTunStatusChanged?.Invoke(true);
                    return;
                }
            }

            // Still not ready on last attempt — don't kill, let it run if alive
            if (attempt == 3)
            {
                if (_singBoxProcess is { HasExited: false })
                {
                    OnLog?.Invoke("[Core] sing-box still alive after timeout, continuing...");
                    OnTunStatusChanged?.Invoke(true);
                    return;
                }
                break;
            }

            OnLog?.Invoke("[Core] sing-box timeout, will retry");
            try { if (!_singBoxProcess!.HasExited) _singBoxProcess.Kill(true); } catch { }
            _singBoxProcess.Dispose();
            _singBoxProcess = null;
        }

        OnLog?.Invoke("[Core] sing-box failed to start after 3 attempts");
    }

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

    public static bool CheckTunAdapterExists()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Any(ni => ni.Name.Equals("singbox_tun", StringComparison.OrdinalIgnoreCase)
                         && ni.OperationalStatus == OperationalStatus.Up);
        }
        catch { return false; }
    }

    private Process StartProcess(string exePath, string configPath, string tag)
    {
        var workingDir = Path.GetDirectoryName(exePath)!;
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = tag == "sing-box" ? $"run -c \"{configPath}\"" : $"-c \"{configPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDir
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                try { OnLog?.Invoke($"[{tag}] {e.Data}"); } catch { }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _errorLines.Add(e.Data);
                try { OnLog?.Invoke($"[{tag}:ERR] {e.Data}"); } catch { }
            }
        };

        process.Exited += (_, _) =>
        {
            ExitCode = process.ExitCode;
            try { OnLog?.Invoke($"[Core] {tag} exited (code {process.ExitCode})"); } catch { }
            try { OnExited?.Invoke(); } catch { }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private static async Task<bool> WaitForPortAsync(int port, int maxAttempts, int initialDelayMs = 500)
    {
        if (initialDelayMs > 0)
            await Task.Delay(initialDelayMs);

        for (int i = 0; i < maxAttempts; i++)
        {
            // Progressive backoff: first 10 attempts 500ms, next 10 1000ms, rest 1500ms
            var delay = i < 10 ? 500 : i < 20 ? 1000 : 1500;
            await Task.Delay(delay);
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", port);
                return true;
            }
            catch { /* port not ready yet */ }
        }
        return false;
    }
    // ── Stop ──

    public void Stop()
    {
        // Stop sing-box first (removes TUN routes), then Xray
        StopSingBox();
        StopXray();
        OnTunStatusChanged?.Invoke(false);
    }

    private void StopXray()
    {
        if (_xrayProcess is null) return;
        try { if (!_xrayProcess.HasExited) { _xrayProcess.Kill(true); _xrayProcess.WaitForExit(5000); } }
        catch { }
        ExitCode ??= _xrayProcess.ExitCode;
        _xrayProcess.Dispose();
        _xrayProcess = null;
    }

    private void StopSingBox()
    {
        if (_singBoxProcess is null) return;
        try { if (!_singBoxProcess.HasExited) { _singBoxProcess.Kill(true); _singBoxProcess.WaitForExit(5000); } }
        catch { }
        _singBoxProcess?.Dispose();
        _singBoxProcess = null;
    }

    // ── Cleanup ──

    private static void KillStaleProcesses()
    {
        try
        {
            foreach (var name in new[] { "xray", "sing-box" })
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try { proc.Kill(true); } catch { }
            }
        }
        catch { /* best effort */ }
    }

    private static async Task WaitForPortReleaseAsync(params int[] ports)
    {
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(200);
            var allFree = true;
            foreach (var port in ports)
            {
                try
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync("127.0.0.1", port);
                    allFree = false; // port still in use
                    break;
                }
                catch { /* port is free */ }
            }
            if (allFree) return;
        }
    }

    private static async Task CleanupAdapterAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -Command \"Get-NetAdapter -Name 'singbox_tun' -ErrorAction SilentlyContinue | Remove-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
            });
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
