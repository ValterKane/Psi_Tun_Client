using System.Windows;

namespace PsiTun;

public partial class BootstrapWindow : Window
{
    public BootstrapWindow()
    {
        InitializeComponent();
    }

    public void UpdateProgress(string status, string detail, int percent)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = status;
            DetailText.Text = detail;
            ProgressBar.Value = percent;
        });
    }

    public void ShowError(string error)
    {
        Dispatcher.Invoke(() =>
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
        });
    }
}
