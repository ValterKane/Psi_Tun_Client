using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PsiTun.Models;
using PsiTun.Services;

namespace PsiTun.Views;

public partial class RoutingRulesWindow : Window
{
    private ObservableCollection<RoutingRule> _rules = [];

    public RoutingRulesWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _rules = new ObservableCollection<RoutingRule>(
            App.Rules.Load().Select(r => new RoutingRule
            {
                Description = r.Description,
                MatchType = r.MatchType,
                Value = r.Value,
                Action = r.Action,
                Protocol = r.Protocol,
                Port = r.Port,
                IsEnabled = r.IsEnabled,
                IsDefault = r.IsDefault
            }));

        RulesGrid.ItemsSource = _rules;

        // Set up ComboBox column sources
        var typeCol = (DataGridComboBoxColumn)RulesGrid.Columns[1];
        typeCol.ItemsSource = Enum.GetValues<RuleMatchType>();

        var protCol = (DataGridComboBoxColumn)RulesGrid.Columns[3];
        protCol.ItemsSource = new string?[] { null, "tcp", "udp" };

        var actionCol = (DataGridComboBoxColumn)RulesGrid.Columns[5];
        actionCol.ItemsSource = Enum.GetValues<RuleAction>();
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        _rules.Add(new RoutingRule
        {
            Description = "",
            MatchType = RuleMatchType.DomainSuffix,
            Value = "",
            Action = RuleAction.Proxy,
            IsEnabled = true,
            IsDefault = false
        });
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is RoutingRule rule)
        {
            if (rule.IsDefault)
            {
                MessageBox.Show("Default rules cannot be deleted. Disable them instead.",
                    "Protected Rule", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _rules.Remove(rule);
        }
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Reset all rules to defaults? Custom rules will be lost.",
            "Confirm Reset", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var defaults = RoutingRuleService.GetDefaults();
        _rules.Clear();
        foreach (var r in defaults)
            _rules.Add(r);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        App.Rules.Save(_rules.ToList());

        // Auto-reconnect if connected
        if (App.Core?.IsRunning == true)
        {
            try
            {
                App.CurrentApp().Disconnect();
                await App.CurrentApp().ConnectAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Reconnect failed: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        Close();
    }
}
