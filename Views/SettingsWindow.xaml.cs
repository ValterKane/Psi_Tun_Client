using System.Windows;

namespace PsiTun;

public partial class SettingsWindow : Window
{
    public bool Saved { get; private set; }

    public SettingsWindow()
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;

        // Load current settings
        var s = App.Settings;
        TunName.Text = s.TunName;
        TunAddress.Text = s.TunAddress;
        TunMask.Text = CidrToMask(s.TunAddress);
        TunGateway.Text = s.TunGateway;
        TunDns.Text = s.TunDns;
        TunMtu.Text = s.TunMtu.ToString();
        AutoRoute.IsChecked = s.AutoRoute;
        StrictRoute.IsChecked = s.StrictRoute;
        Sniffing.IsChecked = s.EnableSniffing;
        UsePac.IsChecked = s.UsePac;
        HttpPort.Text = s.HttpPort.ToString();
        SocksPort.Text = s.SocksPort.ToString();

        TunStack.SelectedIndex = s.TunStack switch
        {
            "system" => 1,
            "mixed" => 2,
            _ => 0 // gvisor
        };

        // Auto-update mask when address changes
        TunAddress.TextChanged += (_, _) =>
        {
            TunMask.Text = CidrToMask(TunAddress.Text);
        };
    }

    private static string CidrToMask(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var bits) || bits < 0 || bits > 32)
            return "255.255.255.252";

        var mask = bits == 0 ? 0u : ~((1u << (32 - bits)) - 1);
        return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.TunName = TunName.Text.Trim();
        s.TunAddress = TunAddress.Text.Trim();
        s.TunGateway = TunGateway.Text.Trim();
        s.TunDns = TunDns.Text.Trim();
        s.TunMtu = int.TryParse(TunMtu.Text, out var mtu) ? mtu : 1500;
        s.AutoRoute = AutoRoute.IsChecked == true;
        s.StrictRoute = StrictRoute.IsChecked == true;
        s.EnableSniffing = Sniffing.IsChecked == true;
        s.UsePac = UsePac.IsChecked == true;
        s.HttpPort = int.TryParse(HttpPort.Text, out var hp) ? hp : 10809;
        s.SocksPort = int.TryParse(SocksPort.Text, out var sp) ? sp : 10808;

        s.TunStack = TunStack.SelectedIndex switch
        {
            1 => "system",
            2 => "mixed",
            _ => "gvisor"
        };

        s.Save(App.AppConfigPath);
        Saved = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
