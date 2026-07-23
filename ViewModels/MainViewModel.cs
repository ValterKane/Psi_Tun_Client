using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PsiTun.Models;
using PsiTun.Services;

namespace PsiTun.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly DispatcherTimer _logTimer;
    private bool _subscriptionPlaceholder = true;

    public ObservableCollection<ServerListItem> Servers { get; } = [];
    public event Action<string>? LogAppended;

    // --- Status ---
    private string _statusText = "Нет подключения";
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }

    private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
    public Brush StatusBrush { get => _statusBrush; set { _statusBrush = value; OnPropertyChanged(); } }

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; set { _isConnected = value; OnPropertyChanged(); } }

    // --- Ping ---
    private string _pingLabelText = "";
    public string PingLabelText { get => _pingLabelText; set { _pingLabelText = value; OnPropertyChanged(); } }

    private bool _pingLabelVisible;
    public bool PingLabelVisible { get => _pingLabelVisible; set { _pingLabelVisible = value; OnPropertyChanged(); } }

    private bool _isPinging;
    public bool IsPinging { get => _isPinging; set { _isPinging = value; OnPropertyChanged(); } }

    private string _tunStatusText = "TUN: ⏳";
    public string TunStatusText { get => _tunStatusText; set { _tunStatusText = value; OnPropertyChanged(); } }
    private Brush _tunStatusBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x00));
    public Brush TunStatusBrush { get => _tunStatusBrush; set { _tunStatusBrush = value; OnPropertyChanged(); } }

    // --- Settings ---
    private string _subscriptionUrl = "";
    public string SubscriptionUrl
    {
        get => _subscriptionUrl;
        set { _subscriptionUrl = value; App.Settings.SubscriptionUrl = value; OnPropertyChanged(); }
    }

    private bool _autoStart;
    public bool AutoStart { get => _autoStart; set { _autoStart = value; App.Settings.AutoStart = value; OnPropertyChanged(); } }

    private bool _autoConnect;
    public bool AutoConnect { get => _autoConnect; set { _autoConnect = value; App.Settings.AutoConnect = value; OnPropertyChanged(); } }

    private bool _autoScroll = true;
    public bool AutoScroll { get => _autoScroll; set => _autoScroll = value; }

    // --- Server list ---
    public ServerListItem? SelectedServer => Servers.FirstOrDefault(s => s.IsSelected);

    // --- Commands ---
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand RefreshSubscriptionCommand { get; }
    public ICommand PingCommand { get; }
    public ICommand SettingsCommand { get; }

    public MainViewModel()
    {
        ConnectCommand = new RelayCommand(_ => App.CurrentApp().ConnectAsync());
        DisconnectCommand = new RelayCommand(_ => App.CurrentApp().Disconnect());
        RefreshSubscriptionCommand = new RelayCommand(async _ => await RefreshSubscription());
        PingCommand = new RelayCommand(async _ => await PingSelected(), _ => !IsPinging);
        SettingsCommand = new RelayCommand(_ => new SettingsWindow().ShowDialog());

        // Log timer
        _logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => FlushLog(), Dispatcher.CurrentDispatcher);
        _logTimer.Start();

        // Load settings
        _subscriptionPlaceholder = string.IsNullOrEmpty(App.Settings.SubscriptionUrl);
        SubscriptionUrl = App.Settings.SubscriptionUrl;
        AutoStart = App.Settings.AutoStart;
        AutoConnect = App.Settings.AutoConnect;
    }

    public void AppendLog(string line) => _logQueue.Enqueue(line);

    public void RefreshServerList()
    {
        Servers.Clear();
        foreach (var s in App.Servers)
            Servers.Add(new ServerListItem(s));

        if (App.SelectedServerIndex >= 0 && App.SelectedServerIndex < Servers.Count)
            Servers[App.SelectedServerIndex].IsSelected = true;

        App.CurrentApp().UpdateTrayServers();
    }

    public void UpdateServerList(List<VpnServer> servers, int selectedIndex)
    {
        Servers.Clear();
        foreach (var s in servers)
            Servers.Add(new ServerListItem(s));
        if (selectedIndex >= 0 && selectedIndex < Servers.Count)
            Servers[selectedIndex].IsSelected = true;
        App.CurrentApp().UpdateTrayServers();
    }

    public void UpdateStatus(bool connected, string? serverName = null)
    {
        IsConnected = connected;
        if (connected)
        {
            StatusBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
            StatusText = $"Подключено: {serverName ?? App.Servers[App.SelectedServerIndex].Name}";
        }
        else
        {
            StatusBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
            StatusText = App.Core is not null ? "Переподключение..." : "Нет подключения";
        }
    }

    // --- Private helpers ---

    private void FlushLog()
    {
        var count = 0;
        var sb = new System.Text.StringBuilder();
        while (count < 30 && _logQueue.TryDequeue(out var line))
        {
            sb.AppendLine(line);
            count++;
        }

        var remaining = _logQueue.Count;
        if (remaining > 0)
            sb.AppendLine($"[...] ещё {remaining} строк в очереди");

        if (sb.Length > 0)
            LogAppended?.Invoke(sb.ToString());
    }

    public void SyncSelection(ServerListItem item)
    {
        foreach (var s in Servers) s.IsSelected = s == item;
        if (item != null)
        {
            App.SelectedServerIndex = Servers.IndexOf(item);
            App.Settings.LastServerIndex = App.SelectedServerIndex;
            App.CurrentApp().UpdateTrayServers();
        }
    }

    private async Task PingSelected()
    {
        var item = Servers.FirstOrDefault(s => s.IsSelected);
        if (item is null) return;
        IsPinging = true;
        item.Status = "ping...";

        var server = item.Server;
        item.Latency = await PingService.MeasureLatencyAsync(server.Address, server.Port);
        item.Status = item.Latency >= 0 ? $"{item.Latency} ms" : "timeout";

        PingLabelText = item.Latency >= 0 ? $"{item.Latency} ms" : "";
        PingLabelVisible = item.Latency >= 0;
        IsPinging = false;
    }

    private async Task RefreshSubscription()
    {
        if (string.IsNullOrWhiteSpace(App.Settings.SubscriptionUrl))
        {
            MessageBox.Show("Введите URL подписки.", "PsiTun",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var servers = await SubscriptionParser.ParseAsync(App.Settings.SubscriptionUrl);

            if (servers.Count == 0)
            {
                MessageBox.Show("Не удалось найти сервера в подписке.", "PsiTun",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            App.Servers = servers;
            App.SelectedServerIndex = 0;
            App.Settings.LastServerIndex = 0;
            App.Settings.Save(App.AppConfigPath);

            System.IO.File.WriteAllText(
                System.IO.Path.Combine(App.BaseDir, "servers.json"),
                System.Text.Json.JsonSerializer.Serialize(servers));

            RefreshServerList();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка загрузки подписки: {ex.Message}", "PsiTun",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- INotifyPropertyChanged ---
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

