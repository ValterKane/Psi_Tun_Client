using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using PsiTun.Models;
using PsiTun.Services;

namespace PsiTun.ViewModels;

public class FirstRunViewModel : INotifyPropertyChanged
{
    private string _url = "";
    public string Url { get => _url; set { _url = value; OnPropertyChanged(); } }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; set { _isLoading = value; OnPropertyChanged(); } }

    private string _status = "";
    public string Status { get => _status; set { _status = value; OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(HasStatus)); } }

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    public ICommand SkipCommand { get; }
    public ICommand LoadCommand { get; }

    public event Action<List<VpnServer>>? Completed;
    public event Action? CloseRequested;

    public FirstRunViewModel()
    {
        if (!string.IsNullOrEmpty(App.Settings.SubscriptionUrl))
            _url = App.Settings.SubscriptionUrl;

        SkipCommand = new RelayCommand(_ =>
        {
            Completed?.Invoke([]);
            CloseRequested?.Invoke();
        });

        LoadCommand = new RelayCommand(async _ => await LoadAsync());
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            Status = "Введите URL подписки.";
            return;
        }

        IsLoading = true;
        Status = "";

        try
        {
            var servers = await SubscriptionParser.ParseAsync(Url);

            if (servers.Count == 0)
            {
                Status = "Не удалось найти сервера в подписке. Проверьте URL.";
                return;
            }

            App.Settings.SubscriptionUrl = Url;
            App.Settings.Save(App.AppConfigPath);

            Completed?.Invoke(servers);
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Status = $"Ошибка загрузки: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
