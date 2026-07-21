using System.Windows;
using PsiTun.Models;
using PsiTun.ViewModels;

namespace PsiTun;

public partial class FirstRunWindow : Window
{
    public event Action<List<VpnServer>>? OnCompleted;

    public FirstRunWindow()
    {
        InitializeComponent();
        var vm = new FirstRunViewModel();
        DataContext = vm;
        vm.Completed += (servers) => OnCompleted?.Invoke(servers);
        vm.CloseRequested += Close;
    }
}
