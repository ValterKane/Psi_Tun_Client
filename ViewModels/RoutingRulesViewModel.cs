using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using PsiTun.Models;
using PsiTun.Services;

namespace PsiTun.ViewModels;

public class RoutingRulesViewModel : INotifyPropertyChanged
{
    public ObservableCollection<RoutingRule> Rules { get; } = [];

    public static IReadOnlyList<RuleMatchType> MatchTypes { get; } = Enum.GetValues<RuleMatchType>();
    public static IReadOnlyList<string?> NetworkValues { get; } = [null, "tcp", "udp"];
    public static IReadOnlyList<string?> AppProtocolValues { get; } =
        [null, "http", "tls", "bittorrent", "dns", "quic", "stun", "ssh", "rdp", "ntp", "dtls"];
    public static IReadOnlyList<RuleAction> Actions { get; } = Enum.GetValues<RuleAction>();

    public ICommand AddRuleCommand { get; }
    public ICommand DeleteRuleCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ResetDefaultsCommand { get; }

    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }

    public RoutingRulesViewModel()
    {
        AddRuleCommand = new RelayCommand(_ => AddRule());
        DeleteRuleCommand = new RelayCommand(_ => DeleteRule(_ as RoutingRule));
        MoveUpCommand = new RelayCommand(_ => MoveRule(_ as RoutingRule, -1));
        MoveDownCommand = new RelayCommand(_ => MoveRule(_ as RoutingRule, 1));
        SaveCommand = new RelayCommand(async _ => await Save());
        ResetDefaultsCommand = new RelayCommand(_ => ResetDefaults());

        LoadRules();
    }

    private void LoadRules()
    {
        Rules.Clear();
        foreach (var r in App.Rules.Load())
            Rules.Add(r);
    }

    private void AddRule()
    {
        Rules.Add(new RoutingRule
        {
            MatchType = RuleMatchType.DomainSuffix,
            Action = RuleAction.Proxy,
            IsEnabled = true
        });
    }

    private void DeleteRule(RoutingRule? rule)
    {
        if (rule == null) return;
        if (rule.IsDefault)
        {
            MessageBox.Show("Системные правила нельзя удалить. Отключите их.",
                "Защита", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Rules.Remove(rule);
    }

    private void MoveRule(RoutingRule? rule, int offset)
    {
        if (rule == null) return;
        var idx = Rules.IndexOf(rule);
        var newIdx = idx + offset;
        if (newIdx < 0 || newIdx >= Rules.Count) return;
        Rules.Move(idx, newIdx);
    }

    private void ResetDefaults()
    {
        var result = MessageBox.Show(
            "Сбросить все правила к значениям по умолчанию? Пользовательские правила будут удалены.",
            "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var defaults = RoutingRuleService.GetDefaults();
        Rules.Clear();
        foreach (var r in defaults)
            Rules.Add(r);
    }

    private async Task Save()
    {
        App.Rules.Save(Rules.ToList());

        if (App.Core?.IsRunning == true)
        {
            try
            {
                App.CurrentApp().Disconnect();
                await App.CurrentApp().ConnectAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка переподключения: {ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // --- INotifyPropertyChanged ---
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
