using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using PsiTun.Models;
using PsiTun.Services;
using PsiTun.ViewModels;
using PsiTun.Views;
using Application = System.Windows.Application;

namespace PsiTun;

public partial class App : Application
{
    // Paths — portable: everything next to .exe
    public static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;

    // Ядро живёт в одном каталоге: он же передаётся ядру флагом -d, иначе
    // mihomo ищет geosite.dat в %USERPROFILE%\.config\mihomo и падает.
    public static readonly string MihomoDir = Path.Combine(BaseDir, "mihomo");
    public static readonly string MihomoExe = Path.Combine(MihomoDir, "mihomo.exe");
    public static readonly string ConfigPath = Path.Combine(MihomoDir, "config.yaml");
    public static readonly string ServersPath = Path.Combine(MihomoDir, "providers", "servers.yaml");
    public static readonly string UserRulesPath = Path.Combine(MihomoDir, "rules", "user.yaml");
    public static readonly string AutoRulesPath = Path.Combine(MihomoDir, "rules", "auto.yaml");

    public static readonly string AppConfigPath = Path.Combine(BaseDir, "appsettings.json");
    public static readonly string RulesFilePath = Path.Combine(BaseDir, "routing-rules.json");

    // Services
    public static SettingsService Settings { get; private set; } = null!;
    public static RoutingRuleService Rules { get; private set; } = null!;

    /// <summary>
    /// Единственное ядро. Имя оставлено прежним: на него смотрят
    /// <c>AutoProxyEngine</c>, <c>PingService</c> и модели представления —
    /// им нужны <c>IsRunning</c> и <c>OnLog</c>, которые есть у обоих типов.
    /// </summary>
    public static MihomoCore? Core { get; private set; }

    public static List<VpnServer> Servers { get; set; } = [];

    public static int SelectedServerIndex { get; set; }

    public static string ConnectionStatus { get; set; } = "Нет подключения";

    // UI
    public static App CurrentApp() => (App)Current;
    private TrayIconManager? _tray;
    private MainWindow? _mainWindow;
    private readonly bool _startMinimized;
    private AutoProxyEngine? _autoProxy;

    public App()
    {
        var args = Environment.GetCommandLineArgs();
        _startMinimized = args.Contains("--minimized");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Ранний выход для самопроверки (exit 0 = ok, иначе selfcheck.log)
        if (e.Args.Contains("--selfcheck"))
        {
            try
            {
                AutoProxyClassifier.SelfCheck();
                CandidateCollector.SelfCheck();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(BaseDir, "selfcheck.log"), ex.ToString());
                Environment.Exit(1);
            }
        }

        // Ранний выход: дамп каркаса mihomo для внешней проверки `mihomo -t`.
        // Заглушки провайдеров создаются только если файлов ещё нет.
        if (e.Args.Contains("--dump-mihomo-config"))
        {
            try
            {
                DumpMihomoConfig();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(BaseDir, "dump-mihomo.log"), ex.ToString());
                Environment.Exit(1);
            }
        }

        // Ранний выход: дамп провайдеров mihomo из реальных servers.json и
        // routing-rules.json — для внешней проверки `mihomo -t`.
        if (e.Args.Contains("--dump-mihomo-providers"))
        {
            try
            {
                DumpMihomoProviders();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(BaseDir, "dump-mihomo.log"), ex.ToString());
                Environment.Exit(1);
            }
        }

        // Global exception handlers for debugging
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;

            File.WriteAllText(Path.Combine(BaseDir, "crash.log"),
                $"Unhandled: {ex?.ToString() ?? args.ExceptionObject?.ToString()}");
        };

        DispatcherUnhandledException += (_, args) =>
        {
            File.WriteAllText(Path.Combine(BaseDir, "crash.log"),
                $"Dispatcher: {args.Exception}");

            MessageBox.Show(args.Exception.ToString(), "PsiTun Error",
                MessageBoxButton.OK, MessageBoxImage.Error);

            args.Handled = true;
        };

        LoadSettings();

        if (Settings.TunStack != "gvisor" && !IsAdministrator())
        {
            if (MessageBox.Show("Перезапускаю от имени администратора?", "Повышение прав приложения!", MessageBoxButton
                    .YesNo, MessageBoxImage
                    .Question) == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath, UseShellExecute = true, Verb = "runas"
                });

                ShutdownApp();
                return;
            }
        }

        Directory.CreateDirectory(BaseDir);

        // Bootstrap: download mihomo core if not present
        if (!File.Exists(MihomoExe))
        {
            var bootWindow = new BootstrapWindow();
            bootWindow.Show();

            var bootstrapper = new BootstrapService(BaseDir);

            var progress = new Progress<(string status, int pct)>(update =>
            {
                Dispatcher.Invoke(() =>
                    bootWindow.UpdateProgress(update.status, "", update.pct));
            });

            Task.Run(async () =>
            {
                try
                {
                    await bootstrapper.BootstrapAsync(progress);

                    Dispatcher.Invoke(() =>
                    {
                        bootWindow.Close();
                        ContinueStartup();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        bootWindow.ShowError($"Download failed: {ex.Message}\nVPN won't work without mihomo-core.");

                        Task.Delay(3000).ContinueWith(_ =>
                            Dispatcher.Invoke(() =>
                            {
                                bootWindow.Close();
                                ContinueStartup();
                            }));
                    });
                }
            });

            return;// Don't continue — bootstrapper will call ContinueStartup()
        }

        ContinueStartup();
    }

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static void LoadSettings()
    {
        Settings = SettingsService.Load(AppConfigPath);
        Rules = new RoutingRuleService(RulesFilePath);
    }

    /// <summary>
    /// Пишет каркас mihomo и заглушки провайдеров в каталог mihomo/, чтобы
    /// `mihomo -t -f mihomo/config.yaml` провалидировал конфиг без запуска
    /// приложения. Существующие провайдеры не перезаписываются.
    /// </summary>
    private static void DumpMihomoConfig()
    {
        Directory.CreateDirectory(MihomoDir);
        Directory.CreateDirectory(Path.GetDirectoryName(ServersPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(UserRulesPath)!);

        var settings = SettingsService.Load(AppConfigPath);
        File.WriteAllText(ConfigPath, MihomoConfigGenerator.Generate(settings, "verification-secret"));

        WriteStub(ServersPath, "proxies: []\n");
        WriteStub(UserRulesPath, "payload: []\n");
        WriteStub(AutoRulesPath, "payload: []\n");
    }

    /// <summary>
    /// Пишет провайдеры mihomo из реальных данных приложения, чтобы
    /// `mihomo -t` проверил их вместе с каркасом. Читает, не пишет: файлы
    /// настроек приложения не изменяются. Читает с диска, а не из статики:
    /// дамп идёт до <see cref="LoadSettings"/>.
    /// </summary>
    private static void DumpMihomoProviders()
    {
        var serversPath = Path.Combine(BaseDir, "servers.json");
        var servers = File.Exists(serversPath)
            ? JsonSerializer.Deserialize<List<VpnServer>>(File.ReadAllText(serversPath)) ?? []
            : [];

        WriteServersAndRules(servers, new RoutingRuleService(RulesFilePath));
    }

    private static void WriteStub(string path, string content)
    {
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }

    /// <summary>
    /// Провайдеры: список серверов и два файла правил. Пишутся при подключении и
    /// при каждой правке правил; ядро подхватывает их через REST, поэтому ни
    /// ядро, ни TUN-адаптер не пересоздаются. Разделение правил по файлам:
    /// выученные авто-прокси уходят в свой провайдер, остальные — в
    /// пользовательский; в каркасе `psi-auto` стоит перед `psi-user`.
    /// </summary>
    private static void WriteServersAndRules(IReadOnlyList<VpnServer> servers, RoutingRuleService rules)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ServersPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(UserRulesPath)!);

        MihomoProviderWriter.WriteServers(ServersPath, servers);

        var all = rules.Load();
        MihomoProviderWriter.WriteRules(UserRulesPath, all.Where(r => !r.IsAutoLearned).ToList());
        MihomoProviderWriter.WriteRules(AutoRulesPath, all.Where(r => r.IsAutoLearned).ToList());
    }

    /// <summary>
    /// Каркас пишется после создания ядра: секрет внешнего контроллера рождается
    /// в <see cref="MihomoCore"/> и обязан совпасть с записанным в конфиг.
    /// </summary>
    private static async Task WriteScaffoldAsync()
    {
        Directory.CreateDirectory(MihomoDir);
        await File.WriteAllTextAsync(ConfigPath, MihomoConfigGenerator.Generate(Settings, Core!.Secret));
    }

    private void ContinueStartup()
    {
        // Setup tray
        _tray = new TrayIconManager();
        _tray.OnExit += ShutdownApp;
        _tray.OnToggleConnection += ToggleConnection;
        _tray.OnOpenWindow += ShowMainWindow;
        _tray.OnSwitchServer += SwitchServer;
        _tray.OnUpdateGeo += () => _ = UpdateGeoDataAsync();
        _tray.UpdateStatus(false);

        // Настройка и реестр могут разойтись: переустановка, перенос каталога,
        // снятый вручную ключ. Настройка — источник истины.
        if (Settings.AutoStart != AutostartHelper.IsEnabled())
            AutostartHelper.Set(Settings.AutoStart);

        // First run or no subscription?
        if (string.IsNullOrEmpty(Settings.SubscriptionUrl))
        {
            ShowFirstRun();
        }
        else
        {
            // Try to load servers from last save
            var serversPath = Path.Combine(BaseDir, "servers.json");

            if (File.Exists(serversPath))
            {
                try
                {
                    Servers = JsonSerializer.Deserialize<List<VpnServer>>(
                        File.ReadAllText(serversPath)) ?? [];

                    SelectedServerIndex = Settings.LastServerIndex;
                }
                catch
                {
                    /* will refresh */
                }
            }

            _mainWindow = new MainWindow();

            if (!_startMinimized)
                _mainWindow.Show();

            // Порядок обязателен: окно уже создано — подключение обновляет его
            // состояние. Совместимо с --minimized.
            if (Settings.AutoConnect && Servers.Count > 0)
                _ = ConnectAsync();
        }
    }

    private void ShowFirstRun()
    {
        var firstRun = new FirstRunWindow();

        firstRun.OnCompleted += (servers) =>
        {
            try
            {
                Servers = servers;
                SelectedServerIndex = 0;

                // Save servers
                File.WriteAllText(Path.Combine(BaseDir, "servers.json"),
                    JsonSerializer.Serialize(Servers));

                // Провайдеры пишутся сразу; каркас создаётся при подключении,
                // когда рождается секрет контроллера.
                if (File.Exists(MihomoExe))
                    WriteServersAndRules(Servers, Rules);

                _mainWindow = new MainWindow();
                _mainWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка инициализации: {ex.Message}", "PsiTun",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        firstRun.Show();
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }

        _mainWindow.Show();
        _mainWindow.Activate();
    }

    public void UpdateTrayServers()
    {
        _tray?.UpdateServerList(Servers, SelectedServerIndex);
    }

    public async void ToggleConnection()
    {
        if (Core is { IsRunning: true })
        {
            Disconnect();
        }
        else
        {
            await ConnectAsync();
        }
    }

    /// <summary>
    /// Переписывает провайдеры правил и обновляет их через REST. Ядро и
    /// TUN-адаптер не пересоздаются: правка применяется к новым соединениям
    /// (измерено, Фаза 6, шаг 3).
    /// </summary>
    public async Task<bool> ReloadRulesAsync()
    {
        if (Core is not { IsRunning: true }) return false;

        try
        {
            WriteServersAndRules(Servers, Rules);
            await Core.Api.RefreshRuleProviderAsync(MihomoConfigGenerator.UserRulesProvider, CancellationToken.None);
            await Core.Api.RefreshRuleProviderAsync(MihomoConfigGenerator.AutoRulesProvider, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _mainWindow?.AppendLog($"[Core] rule reload failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Подсказка о домене, доступ к которому ограничен. Окно показывается в потоке
    /// UI; ответ приходит из окна либо молчанием по таймеру. Ошибка показа
    /// считается молчанием: иначе ожидающий ответ остался бы незавершённым.
    /// </summary>
    public Task<bool?> AskProxyAsync(string host)
    {
        var answered = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var vm = new AutoProxyPromptViewModel(host, answer => answered.TrySetResult(answer));
                new AutoProxyPromptWindow(vm).Show();
            }
            catch
            {
                answered.TrySetResult(null);
            }
        });

        return answered.Task;
    }

    public void SetAutoProxyEnabled(bool enabled)
    {
        Settings.AutoProxyEnabled = enabled;
        Settings.Save(AppConfigPath);

        if (enabled && Core is { IsRunning: true } && _autoProxy is null)
        {
            _autoProxy = new AutoProxyEngine
            {
                Log = line => _mainWindow?.AppendLog(line),
                ConfirmAsync = AskProxyAsync
            };
            _autoProxy.Start();
        }
        else if (!enabled)
        {
            _autoProxy?.Dispose();
            _autoProxy = null;
        }
    }

    public async Task UpdateGeoDataAsync()
    {
        try
        {
            _mainWindow?.AppendLog("[geo] обновление запущено...");
            await new GeoUpdateService(BaseDir).UpdateAsync(
                new Progress<(string status, int pct)>(u => _mainWindow?.AppendLog($"[geo] {u.status}")));
            _mainWindow?.AppendLog("[geo] готово");
        }
        catch (Exception ex)
        {
            _mainWindow?.AppendLog($"[geo] ошибка: {ex.Message}");
        }
    }

    public async Task ConnectAsync()
    {
        if (Servers.Count == 0) return;

        if (!File.Exists(MihomoExe))
        {
            MessageBox.Show("mihomo не найден!", "PsiTun",
                MessageBoxButton.OK, MessageBoxImage.Error);

            return;
        }

        // Секрет внешнего контроллера рождается в ядре, поэтому каркас пишется
        // после создания ядра и до его запуска.
        Core?.Dispose();
        Core = new MihomoCore(MihomoExe, MihomoDir, ConfigPath) { TunEnabled = Settings.UseTun };

        Core.OnLog += (line) =>
        {
            try
            {
                _mainWindow?.AppendLog(line);
            }
            catch
            {

            }
        };

        Core.OnTunStatusChanged += (exists) =>
        {
            try { _mainWindow?.UpdateTunStatus(exists); } catch { }
        };

        try
        {
            WriteServersAndRules(Servers, Rules);
            await WriteScaffoldAsync();

            await Core.StartAsync();

            if (Core.IsRunning)
            {
                // Set system proxy for non-TUN mode
                if (!Settings.UseTun)
                    SetSystemProxy(true, Settings.HttpPort);

                ConnectionStatus = $"Подключено: {Servers[SelectedServerIndex].Name}";
                _tray?.UpdateStatus(true);
                _mainWindow?.UpdateStatus(true, Servers[SelectedServerIndex].Name);
                Settings.LastServerIndex = SelectedServerIndex;
                Settings.Save(AppConfigPath);

                _autoProxy?.Dispose();
                _autoProxy = null;
                if (Settings.AutoProxyEnabled)
                {
                    _autoProxy = new AutoProxyEngine
            {
                Log = line => _mainWindow?.AppendLog(line),
                ConfirmAsync = AskProxyAsync
            };
                    _autoProxy.Start();
                }

                // Автообновление geo-данных (раз в сутки) — в фоне, не блокирует
                var geo = new GeoUpdateService(BaseDir);
                if (geo.NeedsUpdate(TimeSpan.FromHours(24)))
                    _ = Task.Run(() => geo.UpdateAsync());
            }
            else
            {
                var error = Core.LastError;

                if (!string.IsNullOrEmpty(error))
                    error = $"\n\nLast error:\n{error}";

                ConnectionStatus = "Ошибка подключения";
                _tray?.UpdateStatus(false);
                _mainWindow?.UpdateStatus(false);

                MessageBox.Show($"Не удалось запустить VPN ядро.{error}", "PsiTun",
                    MessageBoxButton.OK, MessageBoxImage.Error);

                Core.Dispose();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка запуска VPN ядра: {ex.Message}", "PsiTun",
                MessageBoxButton.OK, MessageBoxImage.Error);

            Core.Dispose();
        }
    }

    public void Disconnect()
    {
        // Remove system proxy
        SetSystemProxy(false);

        Core?.Stop();
        Core?.Dispose();
        Core = null;
        _autoProxy?.Dispose();
        _autoProxy = null;
        ConnectionStatus = "Нет подключения";
        _tray?.UpdateStatus(false);
        _mainWindow?.UpdateStatus(false);
    }

    public async void SwitchServer(int index)
    {
        if (index < 0 || index >= Servers.Count) return;

        SelectedServerIndex = index;
        Settings.LastServerIndex = index;
        Settings.Save(AppConfigPath);

        if (Core is { IsRunning: true })
        {
            try
            {
                // Горячая смена: перезапись провайдера и выбор прокси в группе.
                // Ядро и TUN-адаптер не пересоздаются.
                MihomoProviderWriter.WriteServers(ServersPath, Servers);
                await Core.Api.RefreshProxyProviderAsync(
                    MihomoConfigGenerator.ServersProvider, CancellationToken.None);
                await Core.Api.SelectProxyAsync(
                    MihomoConfigGenerator.ProxyGroup,
                    MihomoProviderWriter.ProxyNames(Servers)[index],
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _mainWindow?.AppendLog($"[Core] server switch failed: {ex.Message}");
            }
        }

        UpdateTrayServers();
        _mainWindow?.UpdateServerList(Servers, index);
        _mainWindow?.UpdateStatus(true, Servers[index].Name);
    }

    private static void SetSystemProxy(bool enable, int port = 10809)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);

            if (key == null) return;

            if (enable)
            {
                key.SetValue("ProxyEnable", 1, Microsoft.Win32.RegistryValueKind.DWord);
                key.SetValue("ProxyServer", $"127.0.0.1:{port}", Microsoft.Win32.RegistryValueKind.String);

                key.SetValue("ProxyOverride", "localhost;127.*;172.16.*;192.168.*;10.*;169.254.*;<local>",
                    Microsoft.Win32.RegistryValueKind.String);
            }
            else
            {
                key.SetValue("ProxyEnable", 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
        }
        catch
        {
        }
    }

    public void ShutdownApp()
    {
        Disconnect();
        _tray?.Dispose();
        Shutdown();
    }
}
