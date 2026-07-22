using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using PsiTun.Models;
using PsiTun.Services;
using Application = System.Windows.Application;

namespace PsiTun;

public partial class App : Application
{
    // Paths — portable: everything next to .exe
    public static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
    public static readonly string CoreDir = Path.Combine(BaseDir, "xray");
    public static readonly string CoreExe = Path.Combine(CoreDir, "xray.exe");
    public static readonly string SingBoxDir = Path.Combine(BaseDir, "sing-box");
    public static readonly string SingBoxExe = Path.Combine(SingBoxDir, "sing-box.exe");
    public static readonly string ConfigPath = Path.Combine(BaseDir, "config.json");
    public static readonly string SingBoxConfigPath = Path.Combine(BaseDir, "sing-box-config.json");
    public static readonly string AppConfigPath = Path.Combine(BaseDir, "appsettings.json");

    // Services
    public static SettingsService Settings { get; private set; } = null!;

    public static CoreManager? Core { get; private set; }

    public static List<VpnServer> Servers { get; set; } = [];

    public static int SelectedServerIndex { get; set; }

    public static string ConnectionStatus { get; set; } = "Нет подключения";

    // UI
    public static App CurrentApp() => (App)Current;
    private TrayIconManager? _tray;
    private MainWindow? _mainWindow;
    private readonly bool _startMinimized;

    public App()
    {
        var args = Environment.GetCommandLineArgs();
        _startMinimized = args.Contains("--minimized");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        // Bootstrap: download Xray-core if not present
        if (!File.Exists(CoreExe))
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
                        bootWindow.ShowError($"Download failed: {ex.Message}\nVPN won't work without Xray-core.");

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
    }

    private void ContinueStartup()
    {
        // Setup tray
        _tray = new TrayIconManager();
        _tray.OnExit += ShutdownApp;
        _tray.OnToggleConnection += ToggleConnection;
        _tray.OnOpenWindow += ShowMainWindow;
        _tray.OnSwitchServer += SwitchServer;
        _tray.UpdateStatus(false);

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

                // Generate config
                if (File.Exists(CoreExe))
                {
                    var config = ConfigGenerator.Generate(Servers, SelectedServerIndex);
                    Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                    File.WriteAllText(ConfigPath, config);
                }

                if (File.Exists(SingBoxExe))
                {
                    var sbConfig = SingBoxConfigGenerator.Generate(Settings, Servers, SelectedServerIndex);
                    File.WriteAllText(SingBoxConfigPath, sbConfig);
                }

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

    public async Task ConnectAsync()
    {
        if (Servers.Count == 0) return;

        if (!File.Exists(CoreExe))
        {
            MessageBox.Show("Xray-core не найден!", "PsiTun",
                MessageBoxButton.OK, MessageBoxImage.Error);

            return;
        }

        // Generate configs
        var config = ConfigGenerator.Generate(Servers, SelectedServerIndex);
        await File.WriteAllTextAsync(ConfigPath, config);

        var singBoxConfig = SingBoxConfigGenerator.Generate(Settings, Servers, SelectedServerIndex);
        await File.WriteAllTextAsync(SingBoxConfigPath, singBoxConfig);

        // Start cores (Xray first = SOCKS server, then sing-box = TUN)
        Core?.Dispose();
        Core = new CoreManager(CoreExe, ConfigPath, SingBoxExe, SingBoxConfigPath);

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
            }
            else
            {
                var error = Core.LastError;
                var exitCode = Core.ExitCode;

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
            Disconnect();
            await Task.Delay(300);
            await ConnectAsync();
        }

        UpdateTrayServers();
        _mainWindow?.UpdateServerList(Servers, index);
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
