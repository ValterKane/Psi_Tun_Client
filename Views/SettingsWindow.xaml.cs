using System.Windows;
using PsiTun.ViewModels;

namespace PsiTun;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel(() => Close());
    }
}
