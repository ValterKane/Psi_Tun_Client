using System.Windows;
using Microsoft.Win32;
using PsiTun.Models;
using PsiTun.Services;
using PsiTun.ViewModels;

namespace PsiTun;

public partial class FirstRunWindow : Window
{
    public event Action<List<VpnServer>>? OnCompleted;

    private readonly FirstRunViewModel _vm;

    public FirstRunWindow()
    {
        InitializeComponent();
        _vm = new FirstRunViewModel();
        DataContext = _vm;
        _vm.Completed += (servers) => OnCompleted?.Invoke(servers);
        _vm.CloseRequested += Close;
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
            _vm.Status = $"Не удалось прочитать буфер обмена: {ex.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            _vm.Status = "В буфере обмена нет ссылки или QR-кода.";
            return;
        }

        await LoadFromSourceAsync(source);
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
            _vm.Status = $"Не удалось открыть файл: {ex.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            _vm.Status = "QR-код не распознан. Проверьте изображение.";
            return;
        }

        await LoadFromSourceAsync(source);
    }

    private async Task LoadFromSourceAsync(string source)
    {
        _vm.Url = source;
        await _vm.LoadAsync();
    }
}
