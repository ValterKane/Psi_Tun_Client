using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using PsiTun.Services;

namespace PsiTun.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly Action _closeAction;

    public SettingsViewModel(Action closeAction)
    {
        _closeAction = closeAction;

        var s = App.Settings;
        _tunName = s.TunName;
        _tunAddress = s.TunAddress;
        _tunGateway = s.TunGateway;
        _tunDns = s.TunDns;
        _tunMtu = s.TunMtu;
        _autoRoute = s.AutoRoute;
        _strictRoute = s.StrictRoute;
        _enableSniffing = s.EnableSniffing;
        _usePac = s.UsePac;
        _httpPort = s.HttpPort;
        _socksPort = s.SocksPort;
        _tunStackIndex = s.TunStack switch { "system" => 1, "mixed" => 2, _ => 0 };

        SaveCommand = new RelayCommand(_ => Save());
        CancelCommand = new RelayCommand(_ => _closeAction());
    }

    // --- Properties ---

    private string _tunName = "";
    public string TunName { get => _tunName; set { _tunName = value; OnPropertyChanged(); } }

    private string _tunAddress = "";
    public string TunAddress
    {
        get => _tunAddress;
        set { _tunAddress = value; OnPropertyChanged(); OnPropertyChanged(nameof(TunMask)); }
    }

    public string TunMask => CidrToMask(TunAddress);

    private string _tunGateway = "";
    public string TunGateway { get => _tunGateway; set { _tunGateway = value; OnPropertyChanged(); } }

    private string _tunDns = "";
    public string TunDns { get => _tunDns; set { _tunDns = value; OnPropertyChanged(); } }

    private int _tunMtu = 1500;
    public string TunMtu { get => _tunMtu.ToString(); set { if (int.TryParse(value, out var v)) _tunMtu = v; OnPropertyChanged(); } }

    private bool _autoRoute;
    public bool AutoRoute { get => _autoRoute; set { _autoRoute = value; OnPropertyChanged(); } }

    private bool _strictRoute;
    public bool StrictRoute { get => _strictRoute; set { _strictRoute = value; OnPropertyChanged(); } }

    private bool _enableSniffing;
    public bool EnableSniffing { get => _enableSniffing; set { _enableSniffing = value; OnPropertyChanged(); } }

    private bool _usePac;
    public bool UsePac { get => _usePac; set { _usePac = value; OnPropertyChanged(); } }

    private int _httpPort = 10809;
    public string HttpPort { get => _httpPort.ToString(); set { if (int.TryParse(value, out var v)) _httpPort = v; OnPropertyChanged(); } }

    private int _socksPort = 10808;
    public string SocksPort { get => _socksPort.ToString(); set { if (int.TryParse(value, out var v)) _socksPort = v; OnPropertyChanged(); } }

    private int _tunStackIndex;
    public int TunStackIndex { get => _tunStackIndex; set { _tunStackIndex = value; OnPropertyChanged(); } }

    // --- Commands ---
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private void Save()
    {
        var s = App.Settings;
        s.TunName = _tunName.Trim();
        s.TunAddress = _tunAddress.Trim();
        s.TunGateway = _tunGateway.Trim();
        s.TunDns = _tunDns.Trim();
        s.TunMtu = _tunMtu;
        s.AutoRoute = _autoRoute;
        s.StrictRoute = _strictRoute;
        s.EnableSniffing = _enableSniffing;
        s.UsePac = _usePac;
        s.HttpPort = _httpPort;
        s.SocksPort = _socksPort;
        s.TunStack = _tunStackIndex switch { 1 => "system", 2 => "mixed", _ => "gvisor" };
        s.Save(App.AppConfigPath);
        _closeAction();
    }

    // --- Helpers ---

    private static string CidrToMask(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var bits) || bits < 0 || bits > 32)
            return "255.255.255.252";
        var mask = bits == 0 ? 0u : ~((1u << (32 - bits)) - 1);
        return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
    }

    // --- INotifyPropertyChanged ---
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
