using System.Windows.Input;

namespace PsiTun.ViewModels;

/// <summary>
/// Состояние подсказки о домене, доступ к которому ограничен. Ответ сообщается
/// ровно один раз: <c>true</c> — проксировать, <c>false</c> — отказ,
/// <c>null</c> — молчание (истёк срок или окно закрыто без ответа).
/// Свойства не меняются: уведомлять не о чем.
/// </summary>
public class AutoProxyPromptViewModel
{
    private readonly Action<bool?> _answered;
    private bool _closed;

    public AutoProxyPromptViewModel(string host, Action<bool?> answered)
    {
        Host = host;
        _answered = answered;
        ProxyCommand = new RelayCommand(_ => Answer(true));
        DenyCommand = new RelayCommand(_ => Answer(false));
    }

    public string Host { get; }

    public string Message => $"Доступ к {Host} ограничён провайдером. Проксировать?";

    public ICommand ProxyCommand { get; }
    public ICommand DenyCommand { get; }

    /// <summary>Окно закрывается: подписчик снимает себя.</summary>
    public event Action? Closed;

    /// <summary>Молчание: правило не добавляется, вопрос повторится позже.</summary>
    public void Timeout() => Answer(null);

    private void Answer(bool? proxy)
    {
        if (_closed) return; // ответ сообщается один раз
        _closed = true;
        _answered(proxy);
        Closed?.Invoke();
    }
}
