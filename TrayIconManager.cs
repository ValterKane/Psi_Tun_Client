using System.Drawing;
using System.Windows.Forms;
using PsiTun.Models;

namespace PsiTun;

public class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private bool _isConnected;
    private List<VpnServer> _servers = [];

    public event Action? OnToggleConnection;
    public event Action? OnOpenWindow;
    public event Action<int>? OnSwitchServer;
    public event Action? OnExit;

    public TrayIconManager()
    {
        _menu = new ContextMenuStrip();

        _icon = new NotifyIcon
        {
            Text = "PsiTun",
            ContextMenuStrip = _menu,
            Visible = true
        };

        // Double click = open window
        _icon.DoubleClick += (_, _) => OnOpenWindow?.Invoke();

        SetDisconnectedIcon();
        BuildMenu();
    }

    public void UpdateStatus(bool connected)
    {
        _isConnected = connected;
        if (connected)
            SetConnectedIcon();
        else
            SetDisconnectedIcon();

        _icon.Text = connected ? "PsiTun — Подключено" : "PsiTun — Отключено";
        BuildMenu();
    }

    public void UpdateServerList(List<VpnServer> servers, int selectedIndex)
    {
        _servers = servers;
        BuildMenu();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();

        // Status line
        var statusItem = new ToolStripMenuItem(_isConnected ? "Подключено" : "Отключено")
        {
            Enabled = false,
            Font = new System.Drawing.Font("Segoe UI", 9f, FontStyle.Bold)
        };
        _menu.Items.Add(statusItem);
        _menu.Items.Add(new ToolStripSeparator());

        // Server list
        if (_servers.Count > 0)
        {
            var serverMenu = new ToolStripMenuItem("Сервера");
            for (int i = 0; i < _servers.Count; i++)
            {
                var idx = i;
                var s = _servers[i];
                var item = new ToolStripMenuItem(
                    $"{s.Protocol.ToString().ToLowerInvariant()}: {s.Name}")
                {
                    Checked = (i == App.SelectedServerIndex),
                    CheckOnClick = false
                };
                item.Click += (_, _) => OnSwitchServer?.Invoke(idx);
                serverMenu.DropDownItems.Add(item);
            }
            _menu.Items.Add(serverMenu);
            _menu.Items.Add(new ToolStripSeparator());
        }

        // Connect/Disconnect
        var toggleItem = new ToolStripMenuItem(
            _isConnected ? "Отключиться" : "Подключиться");
        toggleItem.Click += (_, _) => OnToggleConnection?.Invoke();
        _menu.Items.Add(toggleItem);

        // Open window
        var openItem = new ToolStripMenuItem("Открыть окно");
        openItem.Click += (_, _) => OnOpenWindow?.Invoke();
        _menu.Items.Add(openItem);

        _menu.Items.Add(new ToolStripSeparator());

        // Exit
        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => OnExit?.Invoke();
        _menu.Items.Add(exitItem);
    }

    private void SetConnectedIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(76, 175, 80)); // Green
        g.FillEllipse(brush, 2, 2, 12, 12);
        var hIcon = bmp.GetHicon();
        _icon.Icon = Icon.FromHandle(hIcon);
    }

    private void SetDisconnectedIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(244, 67, 54)); // Red
        g.FillEllipse(brush, 2, 2, 12, 12);
        var hIcon = bmp.GetHicon();
        _icon.Icon = Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
