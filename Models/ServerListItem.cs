using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PsiTun.Models;

public class ServerListItem : INotifyPropertyChanged
{
    public VpnServer Server { get; }

    private int _latency = -1;
    public int Latency { get => _latency; set { _latency = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); } }

    private string _status = "";
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); } }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

    public string DisplayText => ToString();

    public ServerListItem(VpnServer server) { Server = server; }

    public override string ToString()
    {
        var proto = Server.Protocol.ToString().ToUpperInvariant();
        var network = string.IsNullOrEmpty(Server.Network) || Server.Network == "tcp"
            ? "" : $" [{Server.Network}]";
        var security = Server.Security == "none" ? "" : $" ({Server.Security})";
        var ping = Latency >= 0 ? $"  —  {Latency} ms" : "";
        return $"{proto}{security}{network}  {Server.Name}  —  {Server.Address}:{Server.Port}{ping}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
