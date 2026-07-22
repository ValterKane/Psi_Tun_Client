using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;

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
    private CancellationTokenSource? _tunCts;

    public event Action<string>? OnLog;
    public event Action? OnExited;
    public event Action<bool>? OnXrayStatusChanged;
    public event Action<bool>? OnTunStatusChanged;

    public string LastError => _errorLines.Count > 0
        ? string.Join("\n", _errorLines.TakeLast(5))
        : "";

    public int? ExitCode { get; private set; }
    public bool IsTunCreated { get; private set; }
    public bool IsXrayRunning => _xrayProcess is { HasExited: false };

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
        CleanupAdapter();
        // Give Windows time to release the adapter
        await Task.Delay(2000);
        _errorLines.Clear();
        ExitCode = null;

        // 1. Start Xray first (SOCKS server must be ready for sing-box)
        _xrayProcess = StartProcess(_xrayPath, _xrayConfigPath, "xray");
        OnLog?.Invoke("[Core] Starting Xray (proxy)...");
        OnXrayStatusChanged?.Invoke(true);

        // 2. Wait for Xray SOCKS port to be ready
        var xrayReady = await WaitForPortAsync(App.Settings.XrayInboundPort, 10);
        if (!xrayReady)
        {
            OnLog?.Invoke("[Core] Xray failed to start");
            ExitCode = _xrayProcess.ExitCode;
            OnXrayStatusChanged?.Invoke(false);
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

        // Start sing-box
        _singBoxProcess = StartProcess(_singBoxPath, _singBoxConfigPath, "sing-box");
        OnLog?.Invoke("[Core] Starting sing-box (TUN+DNS)...");

        // Wait for port
        var sbReady = await WaitForPortAsync(App.Settings.HttpPort, 10);
        if (sbReady)
            OnLog?.Invoke("[Core] sing-box ready");
        else
            OnLog?.Invoke("[Core] sing-box may still be starting...");

        // Start TUN monitoring (checks adapter status, retries if needed)
        _tunCts = new CancellationTokenSource();
        _ = TunMonitorLoopAsync(_tunCts.Token);
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

    private async Task TunMonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(3000, ct); } catch { break; }
            if (ct.IsCancellationRequested) break;

            var tunExists = CheckTunAdapterExists();
            IsTunCreated = tunExists;
            OnTunStatusChanged?.Invoke(tunExists);

            if (tunExists)
                continue; // All good, keep monitoring

            // TUN not detected — restart sing-box
            OnLog?.Invoke("[Core] TUN adapter not found, restarting sing-box...");
            StopSingBox();
            try { await Task.Delay(2000, ct); } catch { break; }
            CleanupAdapter();
            try { await Task.Delay(1000, ct); } catch { break; }
            _singBoxProcess = StartProcess(_singBoxPath, _singBoxConfigPath, "sing-box");
            OnLog?.Invoke("[Core] Restarted sing-box (TUN recovery)...");
        }
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
            if (tag == "xray")
                try { OnXrayStatusChanged?.Invoke(false); } catch { }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private static async Task<bool> WaitForPortAsync(int port, int maxAttempts)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        for (int i = 0; i < maxAttempts; i++)
        {
            await Task.Delay(500);
            try
            {
                var response = await client.GetAsync(
                    $"http://127.0.0.1:{port}/",
                    HttpCompletionOption.ResponseHeadersRead);
                return true;
            }
            catch { /* port not ready yet */ }
        }
        return false;
    }

    public async Task<bool> HealthCheckAsync(int port = 0, int timeoutMs = 3000)
    {
        var checkPort = port > 0 ? port : App.Settings.HttpPort;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            await client.GetAsync($"http://127.0.0.1:{checkPort}/",
                HttpCompletionOption.ResponseHeadersRead);
            return true;
        }
        catch { return false; }
    }

    // ── Stop ──

    public void Stop()
    {
        _tunCts?.Cancel();
        _tunCts?.Dispose();
        _tunCts = null;
        // Stop sing-box first (removes TUN routes), then Xray
        StopSingBox();
        StopXray();
        IsTunCreated = false;
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

    private static void CleanupAdapter()
    {
        try
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
            var p = Process.Start(psi);
            p?.WaitForExit(5000);
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
