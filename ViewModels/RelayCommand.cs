using System.Windows;
using System.Windows.Input;

namespace PsiTun.ViewModels;

public class RelayCommand : ICommand
{
    private readonly Func<object?, Task>? _executeAsync;
    private readonly Action<object?>? _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public async void Execute(object? parameter)
    {
        try
        {
            if (_executeAsync != null)
                await _executeAsync(parameter);
            else
                _execute?.Invoke(parameter);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Command error: {ex}", "PsiTun",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
}
