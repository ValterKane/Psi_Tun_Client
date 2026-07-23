using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using PsiTun.ViewModels;

namespace PsiTun;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _subscriptionPlaceholder = true;
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush WarnBrush = new(Color.FromRgb(0xFF, 0x98, 0x00));
    private static readonly SolidColorBrush InfoBrush = new(Color.FromRgb(0x90, 0xA4, 0xAE));

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        if (!string.IsNullOrEmpty(App.Settings.SubscriptionUrl))
            _subscriptionPlaceholder = false;

        _vm.RefreshServerList();

        // Colored log via RichTextBox
        _vm.LogAppended += text =>
        {
            var doc = LogBox.Document;
            foreach (var line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                var isError = line.Contains("ERROR") || line.Contains(":ERR");
                var isWarn = line.Contains("WARN") || line.Contains("Warning");

                if (isError)
                {
                    File.AppendAllLines("error.log", new[] { line });
                }
                
                var brush = isError ? ErrorBrush : isWarn ? WarnBrush : InfoBrush;
                var run = new Run(line) { Foreground = brush };
                var p = new Paragraph { Margin = new Thickness(0), Padding = new Thickness(0), LineHeight = 1};
                p.Inlines.Add(run);
                doc.Blocks.Add(p);
            }

            while (doc.Blocks.Count > 2000)
                doc.Blocks.Remove(doc.Blocks.FirstBlock);

            if (_vm.AutoScroll)
                LogBox.ScrollToEnd();
        };
    }

    public void AppendLog(string line) => _vm.AppendLog(line);
    public void RefreshServerList() => _vm.RefreshServerList();
    public void UpdateStatus(bool connected, string? serverName = null)
        => _vm.UpdateStatus(connected, serverName);
    public void UpdateServerList(List<Models.VpnServer> servers, int selectedIndex)
        => _vm.UpdateServerList(servers, selectedIndex);

    public void UpdateTunStatus(bool exists)
    {
        _vm.TunStatusText = exists ? "TUN: ✅" : "TUN: ❌";
        _vm.TunStatusBrush = exists
            ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
            : new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
    }

    private void SubscriptionBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_subscriptionPlaceholder) { SubscriptionBox.Text = ""; _subscriptionPlaceholder = false; }
    }

    private void SubscriptionBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SubscriptionBox.Text)) { SubscriptionBox.Text = "URL подписки..."; _subscriptionPlaceholder = true; }
    }

    private void ServerRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb && rb.DataContext is Models.ServerListItem item)
            _vm.SyncSelection(item);
    }

    private void Window_Closing(object sender, CancelEventArgs e) { e.Cancel = true; Hide(); }

    private void RoutingRules_Click(object sender, RoutedEventArgs e)
    {
        var window = new Views.RoutingRulesWindow { Owner = this };
        window.ShowDialog();
    }
}
