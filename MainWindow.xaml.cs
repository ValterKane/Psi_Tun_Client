using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using PsiTun.Services;
using PsiTun.ViewModels;

namespace PsiTun;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush WarnBrush = new(Color.FromRgb(0xFF, 0x98, 0x00));
    private static readonly SolidColorBrush InfoBrush = new(Color.FromRgb(0x90, 0xA4, 0xAE));

    public MainWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "0.1.0";
        Title = $"PsiTun {version}";

        _vm = new MainViewModel();
        DataContext = _vm;

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

    private async void QrFromClipboard_Click(object sender, RoutedEventArgs e)
    {
        string? source;
        try
        {
            source = QrCodeService.ReadFromClipboard();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось прочитать буфер обмена: {ex.Message}", "PsiTun",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            MessageBox.Show("В буфере обмена нет ссылки или QR-кода.", "PsiTun",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ImportSourceAsync(source);
    }

    private async void QrFromFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите QR-код с конфигом",
            Filter = "Изображения (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|Все файлы (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true) return;

        string? source;
        try
        {
            source = QrCodeService.DecodeFile(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть файл: {ex.Message}", "PsiTun",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            MessageBox.Show("QR-код не распознан. Проверьте изображение.", "PsiTun",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ImportSourceAsync(source);
    }

    private async void PasteLink_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SourceWindow { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            await ImportSourceAsync(dialog.InputText);
    }

    private async Task ImportSourceAsync(string source)
    {
        try
        {
            await _vm.ImportSourceAsync(source);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка импорта: {ex.Message}", "PsiTun",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
