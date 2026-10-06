using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PsiTun.ViewModels;

namespace PsiTun.Views;

/// <summary>
/// Подсказка внизу экрана. Фокус не перехватывается: окно создаётся с
/// <c>ShowActivated="False"</c> и расширенным стилем <c>WS_EX_NOACTIVATE</c>,
/// поэтому работа впереди идущего приложения не прерывается. Три исхода —
/// «Проксировать», «Нет» и молчание по таймеру; закрытие окна без ответа также
/// считается молчанием, иначе ожидающий ответ остался бы висеть.
/// </summary>
public partial class AutoProxyPromptWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x80;

    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly AutoProxyPromptViewModel _vm;
    private readonly DispatcherTimer _timer;

    public AutoProxyPromptWindow(AutoProxyPromptViewModel vm)
    {
        InitializeComponent();

        _vm = vm;
        DataContext = vm;
        vm.Closed += Close;

        _timer = new DispatcherTimer(Lifetime, DispatcherPriority.Normal,
            (_, _) => _vm.Timeout(), Dispatcher.CurrentDispatcher);

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GwlExstyle,
                GetWindowLong(handle, GwlExstyle) | WsExNoactivate | WsExToolwindow);
        };

        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Bottom - ActualHeight - 48;
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _vm.Timeout(); // молчание, если ответа не было
        };

        _timer.Start();
    }
}
