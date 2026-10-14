using System.Buffers.Text;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PsiTun.Services;

/// <summary>
/// Супервизор единственного ядра mihomo: проверка конфигурации, запуск, ожидание
/// готовности, остановка. Сменил пару Xray + sing-box.
///
/// Рабочий каталог передаётся ядру флагом <c>-d</c> и обязателен: без него mihomo
/// берёт <c>%USERPROFILE%\.config\mihomo</c>, не находит там <c>geosite.dat</c>,
/// скачивает свой из <c>meta-rules-dat</c> и падает на `list ru-blocked not found
/// in GeoSite.dat`. Тот же флаг нужен проверке конфигурации.
///
/// Секрет внешнего контроллера ядро порождает само (<see cref="Secret"/>): каркас
/// конфигурации пишется вызывающим уже после создания ядра, поэтому секрет
/// попадает и в конфиг, и в <see cref="Api"/> из одного источника.
/// </summary>
public sealed class MihomoCore : IDisposable
{
    private readonly string _exePath;
    private readonly string _workDir;
    private readonly string _configPath;
    private readonly List<string> _errorLines = [];
    private readonly TaskCompletionSource<bool> _tunReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private bool _disposed;

    public event Action<string>? OnLog;
    public event Action? OnExited;
    public event Action<bool>? OnTunStatusChanged;

    /// <summary>
    /// Строка ядра о готовности TUN-адаптера. Подтверждена в поставляемом
    /// двоичном файле v1.19.32: <c>grep -a "Tun adapter listening at:" mihomo.exe</c>.
    /// </summary>
    public const string TunReadyMarker = "Tun adapter listening at:";

    /// <summary>
    /// Ожидание адаптера ограничено сверху: измеренный максимум создания — 16 с
    /// (Task 6.3). Модуль ядра отвечает контроллеру раньше, поэтому готовность
    /// по контроллеру готовности адаптера не означает.
    /// </summary>
    public static readonly TimeSpan TunReadyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Признак включённого TUN задаёт вызывающий: ядро каркас не читает.
    /// Выключенный TUN снимает ожидание — адаптера не будет.
    /// </summary>
    public bool TunEnabled { get; init; }

    /// <summary>Имя TUN-адаптера: по нему снимается остаток прошлого запуска.</summary>
    public string TunDevice { get; init; } = "sing-tun";

    /// <summary>
    /// Порты ядра для ожидания освобождения после останова. Пустой список —
    /// ожидания нет.
    /// </summary>
    public IReadOnlyList<int> PortsToRelease { get; init; } = [];

    public static bool IsTunReadyLine(string line) =>
        line.Contains(TunReadyMarker, StringComparison.Ordinal);

    /// <summary>
    /// Строка ошибки ядра. Уровень стоит в самой строке, но написание различно:
    /// mihomo пишет строчными (<c>level=error</c>), прежнее ядро sing-box —
    /// прописными (<c>ERROR</c>, метка <c>[sing-box:ERR]</c>). Признак держится в
    /// одном месте: по нему и <see cref="LastError"/>, и запись журнала на диск.
    /// </summary>
    public static bool IsErrorLine(string line) =>
        line.Contains("level=error", StringComparison.Ordinal)
        || line.Contains("level=fatal", StringComparison.Ordinal)
        || line.Contains("ERROR", StringComparison.Ordinal)
        || line.Contains(":ERR", StringComparison.Ordinal);

    public MihomoCore(string exePath, string workDir, string configPath)
    {
        _exePath = exePath;
        _workDir = workDir;
        _configPath = configPath;
        Secret = NewSecret();
        Api = new MihomoApiClient(MihomoConfigGenerator.ControllerPort, Secret);
    }

    /// <summary>Секрет внешнего контроллера — тот же, что записан в каркас конфигурации.</summary>
    public string Secret { get; }

    /// <summary>Клиент к контроллеру с уже подставленным секретом.</summary>
    public MihomoApiClient Api { get; }

    public bool IsRunning => _process is { HasExited: false };

    public int? ExitCode { get; private set; }

    public string LastError => _errorLines.Count > 0
        ? string.Join("\n", _errorLines.TakeLast(5))
        : "";

    /// <summary>32 случайных байта в Base64Url: безопасен в URL и в YAML без кавычек.</summary>
    public static string NewSecret() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// <c>mihomo -t -f &lt;configPath&gt; -d &lt;workDir&gt;</c>. Провайдеры проверка
    /// не читает — их содержимое подтверждается только живым запуском.
    ///
    /// Отсутствующий файл проверяется до запуска ядра: <c>-t</c> с несуществующим
    /// путём не падает, а **создаёт** по этому пути шаблон (<c>mixed-port: 7890</c>)
    /// и валидирует его, возвращая код 0. Без этой проверки предвалидация
    /// подтверждала бы конфиг, которого нет (измерено, Task 4.1 шаг 5).
    /// </summary>
    public static bool ValidateConfig(string exePath, string workDir, string configPath)
    {
        if (!File.Exists(configPath)) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"-t -f \"{configPath}\" -d \"{workDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(15000);
            return p is { HasExited: true, ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StartAsync()
    {
        if (IsRunning) return true;

        KillStaleProcesses();
        _errorLines.Clear();
        ExitCode = null;

        // Остаток прошлого останова снимается до запуска ядра: иначе повторный
        // коннект в том же процессе стартует на живом ещё адаптере и обрывается.
        if (TunEnabled) await CleanupTunAdapterAsync();
        await WaitForPortReleaseAsync();

        if (!ValidateConfig(_exePath, _workDir, _configPath))
        {
            OnLog?.Invoke("[Core] mihomo config invalid, start aborted");
            return false;
        }

        _process = StartProcess();
        OnLog?.Invoke("[Core] Starting mihomo (proxy + TUN + DNS)...");

        if (!await WaitForReadyAsync())
        {
            // ExitCode живого процесса читать нельзя — .NET бросает
            // InvalidOperationException. Ядро могло подняться и молчать по
            // контроллеру (занятый порт, отказ TUN, пустой провайдер): тогда
            // выходного кода ещё нет.
            ExitCode = IsRunning ? null : _process?.ExitCode;
            OnLog?.Invoke(ExitCode is null
                ? "[Core] mihomo did not answer the controller"
                : $"[Core] mihomo exited early (code {ExitCode})");
            return false;
        }

        OnLog?.Invoke("[Core] mihomo ready");

        // Индикатор TUN поднимается только по факту адаптера. Без TUN ждать
        // нечего: прежняя семантика события (готовность ядра) сохраняется.
        if (TunEnabled && !await WaitForTunAsync())
        {
            // Событие обязательно: иначе индикатор держит состояние прошлой сессии.
            OnTunStatusChanged?.Invoke(false);
            OnLog?.Invoke(
                $"[Core] TUN adapter not detected within {TunReadyTimeout.TotalSeconds:0}s");
            return true;
        }

        OnTunStatusChanged?.Invoke(true);
        return true;
    }

    /// <summary>
    /// Снятие остаточного TUN-адаптера прошлого запуска. <see cref="Stop"/>
    /// снимает ядро через <c>TerminateProcess</c>, поэтому адаптер и его маршруты
    /// ядро за собой не убирает, а Windows снимает их не мгновенно. Повторный
    /// коннект в том же процессе стартует через доли секунды и застаёт адаптер
    /// живым: тогда собственное соединение ядра к узлу уходит внутрь тунеля и
    /// получает <c>context canceled</c>. Мера восстановлена из <c>CoreManager</c>
    /// (до v2.0), где стояла на каждом старте.
    /// </summary>
    private async Task CleanupTunAdapterAsync()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!await AdapterPresentAsync(TunDevice))
            {
                if (attempt > 0) OnLog?.Invoke("[Core] stale TUN adapter gone");
                return;
            }

            // Первый заход — снять принудительно, дальше только ждать: снятие
            // адаптера в ядре асинхронно настолько, что вызов его не дожидается.
            if (attempt == 0) await RemoveAdapterAsync(TunDevice);
            await Task.Delay(500);
        }

        OnLog?.Invoke($"[Core] stale TUN adapter '{TunDevice}' still present");
    }

    private static async Task<bool> AdapterPresentAsync(string name) =>
        (await RunPowerShellAsync(
            $"Get-NetAdapter -Name '{name}' -ErrorAction SilentlyContinue | " +
            "Select-Object -ExpandProperty Name")).Length > 0;

    private static Task<string> RemoveAdapterAsync(string name) =>
        RunPowerShellAsync(
            $"Get-NetAdapter -Name '{name}' -ErrorAction SilentlyContinue | " +
            "Remove-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue");

    /// <summary>
    /// Вывод <c>powershell</c> или пустая строка. Отсутствие самой <c>powershell</c>
    /// ожиданием не считается: без вывода адаптер признаётся отсутствующим.
    /// </summary>
    private static async Task<string> RunPowerShellAsync(string script)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var p = Process.Start(psi);
            if (p is null) return "";

            var output = p.StandardOutput.ReadToEndAsync();
            p.WaitForExit(5000);
            return (await output).Trim();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Освобождение портов ядра после останова. Слушающий сокет убитого процесса
    /// освобождается не мгновенно, а занятый порт ядро роняет ошибкой привязки.
    /// </summary>
    private async Task WaitForPortReleaseAsync()
    {
        foreach (var port in PortsToRelease)
        {
            for (var attempt = 0; attempt < 25 && await IsPortBusyAsync(port); attempt++)
                await Task.Delay(200);
        }
    }

    /// <summary>True, если на петлевом порту кто-то принимает соединения.</summary>
    public static async Task<bool> IsPortBusyAsync(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ждёт строку о готовности адаптера. Выход ядра снимает ожидание досрочно и
    /// ложью: иначе мёртвое ядро держало бы подключение до срока и поднимало
    /// индикатор адаптера, которого нет.
    /// </summary>
    private async Task<bool> WaitForTunAsync()
    {
        var finished = await Task.WhenAny(_tunReady.Task, Task.Delay(TunReadyTimeout));
        return finished == _tunReady.Task && _tunReady.Task.Result;
    }

    /// <summary>
    /// Готовность = ответ контроллера. Проверка порта отдельно не нужна: контроллер
    /// слушает тот же процесс, а ответ на <c>GET /version</c> означает, что и
    /// SOCKS-инбаунд уже поднят. Прогрессивный бэкофф: 10 попыток по 500 мс,
    /// далее по 1000 мс — около 20 с.
    /// </summary>
    private async Task<bool> WaitForReadyAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (_process is not { HasExited: false }) return false;
            if (await Api.ReadyAsync(CancellationToken.None)) return true;
            await Task.Delay(attempt < 10 ? 500 : 1000);
        }
        return false;
    }

    public void Stop()
    {
        if (_process is null) return;
        try { if (!_process.HasExited) { _process.Kill(true); _process.WaitForExit(5000); } }
        catch { }
        // Если ядро не успело выйти за 5 с, кода ещё нет — читать его нельзя.
        try { if (_process.HasExited) ExitCode ??= _process.ExitCode; } catch { }
        _process.Dispose();
        _process = null;
        OnTunStatusChanged?.Invoke(false);
    }

    private Process StartProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            Arguments = $"-d \"{_workDir}\" -f \"{_configPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _workDir,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) => HandleLine("mihomo", e.Data);
        process.ErrorDataReceived += (_, e) => HandleLine("mihomo", e.Data);

        process.Exited += (_, _) =>
        {
            _tunReady.TrySetResult(false);
            ExitCode = process.ExitCode;
            try { OnLog?.Invoke($"[Core] mihomo exited (code {process.ExitCode})"); } catch { }
            try { OnExited?.Invoke(); } catch { }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    /// <summary>
    /// Ядро пишет и в stdout, и в stderr, уровень стоит в самой строке, поэтому
    /// разбор по потоку невозможен: ошибкой считается строка по признаку
    /// <see cref="IsErrorLine"/>. Иначе весь лог ядра попал бы в
    /// <see cref="LastError"/>.
    /// </summary>
    private void HandleLine(string tag, string? data)
    {
        if (string.IsNullOrEmpty(data)) return;

        if (IsErrorLine(data))
            _errorLines.Add(data);

        if (IsTunReadyLine(data)) _tunReady.TrySetResult(true);

        try { OnLog?.Invoke($"[{tag}] {data}"); } catch { }
    }

    private static void KillStaleProcesses()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("mihomo"))
            {
                try { proc.Kill(true); } catch { }
            }
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        Api.Dispose();
    }
}
