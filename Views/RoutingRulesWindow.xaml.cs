using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PsiTun.ViewModels;

namespace PsiTun.Views;

public partial class RoutingRulesWindow : Window
{
    private readonly RoutingRulesViewModel _vm;

    public RoutingRulesWindow()
    {
        InitializeComponent();
        _vm = new RoutingRulesViewModel();
        DataContext = _vm;

        // ComboBox column sources from ViewModel
        ((DataGridComboBoxColumn)RulesGrid.Columns[1]).ItemsSource = RoutingRulesViewModel.MatchTypes;
        ((DataGridComboBoxColumn)RulesGrid.Columns[3]).ItemsSource = RoutingRulesViewModel.NetworkValues;
        ((DataGridComboBoxColumn)RulesGrid.Columns[4]).ItemsSource = RoutingRulesViewModel.AppProtocolValues;
        ((DataGridComboBoxColumn)RulesGrid.Columns[6]).ItemsSource = RoutingRulesViewModel.Actions;
    }

    private void Port_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, @"^[\d:\-]+$");
    }
}
