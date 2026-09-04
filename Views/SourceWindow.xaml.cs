using System.Windows;

namespace PsiTun;

public partial class SourceWindow : Window
{
    /// <summary>Trimmed source string entered by the user (valid after OK).</summary>
    public string? InputText { get; private set; }

    public SourceWindow()
    {
        InitializeComponent();
        SourceBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        InputText = SourceBox.Text.Trim();
        DialogResult = true;
    }
}
