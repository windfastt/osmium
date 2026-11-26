using System.Runtime.InteropServices;
using static Proto.Desktop.Native;

namespace Proto.Desktop;

/// <summary>
/// The notification icon through Shell_NotifyIcon directly (Avalonia's TrayIcon has no balloons): NOTIFYICON_VERSION_4,
/// NIF_INFO balloons with their show / click / timeout callbacks, and a menu on right-click. No UI framework types: the
/// same class serves the Avalonia app (callbacks posted to its dispatcher) and the framework-free tray host.
/// If the shell refuses the icon (the notification area is not ready yet), it retries every second for 30 seconds.
/// </summary>
internal sealed class ShellTray : IDisposable
{
    public const string WindowClass = "OsmiumUiPreviewTray";
    public const int IconId = 1;
    public const int CallbackMessage = WM_APP + 1;
    private const int RetryTimer = 1;

    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly WndProc _proc;
    private readonly IntPtr _icon;
    private readonly string _tip;
    private readonly Action<Action> _post;
    private bool _added;
    private int _attempts;

    public IntPtr Handle { get; }
    public bool Added => _added;

    /// <summary>Left click or keyboard select on the icon.</summary>
    public event Action? Activated;

    /// <summary>Right click or Shift+F10 / menu key: the anchor point in screen pixels.</summary>
    public event Action<int, int>? MenuRequested;

    /// <summary>Balloon events: "show", "hide", "timeout", "click".</summary>
    public event Action<string>? Balloon;

    /// <summary>Any other message to the window (the tray host's own commands); return true when handled.</summary>
    public Func<uint, IntPtr, IntPtr, bool>? Extra { get; set; }

    /// <param name="post">Runs a callback on the UI thread after the current message (Avalonia: Dispatcher.Post).</param>
    /// <param name="messageOnly">A message-only window, or a hidden top-level one (gets WM_SETTINGCHANGE broadcasts,
    /// can own a native popup menu).</param>
    public ShellTray(string tooltip, string? icoFile, Action<Action> post, bool messageOnly)
    {
        _tip = tooltip;
        _post = post;
        _proc = Proc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = WindowClass,
        };
        if (RegisterClassEx(ref wc) == 0) EventLog.Write("tray-register-class", new { error = Marshal.GetLastWin32Error() });
        Handle = CreateWindowEx(messageOnly ? 0 : 0x80 /* WS_EX_TOOLWINDOW */, WindowClass, "Osmium UI preview tray",
            messageOnly ? 0 : unchecked((int)0x80000000) /* WS_POPUP */, 0, 0, 0, 0,
            messageOnly ? HWND_MESSAGE : IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        // The exe's own icon (resource 32512 is the first group icon the SDK embeds), small size for the taskbar DPI.
        _icon = LoadImage(wc.hInstance, "#32512", 1, GetSystemMetrics(49), GetSystemMetrics(50), 0);
        if (_icon == IntPtr.Zero && icoFile is not null && File.Exists(icoFile))
            _icon = LoadImage(IntPtr.Zero, icoFile, 1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10 /* LR_LOADFROMFILE */);
        Add();
    }

    private void Add()
    {
        _attempts++;
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        d.szTip = _tip;
        _added = Shell_NotifyIcon(NIM_ADD, ref d);
        int addError = _added ? 0 : Marshal.GetLastWin32Error();
        if (!_added)
        {
            // Already there (a TaskbarCreated race): modify succeeds then.
            _added = Shell_NotifyIcon(NIM_MODIFY, ref d);
        }
        bool version = false;
        if (_added)
        {
            d.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            version = Shell_NotifyIcon(NIM_SETVERSION, ref d);
            KillTimer(Handle, RetryTimer);
        }
        else if (_attempts == 1) SetTimer(Handle, RetryTimer, 1000, IntPtr.Zero);
        EventLog.Write("tray-added", new
        {
            route = "shell", ok = _added, attempt = _attempts, error = addError, version4 = version, hwnd = Handle.ToInt64(), icon = _icon != IntPtr.Zero,
            rect = Rect(), shellTray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null) != IntPtr.Zero,
        });
    }

    private NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = "", szInfo = "", szInfoTitle = "",
    };

    /// <summary>Where the shell shows the icon (screen pixels), or null if it has none.</summary>
    public int[]? Rect()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = Handle, uID = IconId };
        return Shell_NotifyIconGetRect(ref id, out var r) == 0 ? [r.Left, r.Top, r.Width, r.Height] : null;
    }

    /// <summary>Shows a balloon (Windows 10 and 11 show it as a notification) on the icon: NIF_INFO.</summary>
    public bool ShowBalloon(string title, string text, int kind = NIIF_INFO)
    {
        var d = Data(NIF_INFO);
        d.szInfoTitle = title;
        d.szInfo = text;
        d.dwInfoFlags = kind | NIIF_NOSOUND;
        bool ok = Shell_NotifyIcon(NIM_MODIFY, ref d);
        EventLog.Write("balloon-sent", new { ok, error = ok ? 0 : Marshal.GetLastWin32Error(), title, text });
        return ok;
    }

    private IntPtr Proc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            // NOTIFYICON_VERSION_4: wParam carries the anchor point in screen coordinates.
            int x = (short)(wParam.ToInt64() & 0xFFFF), y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            switch (ev)
            {
                case NIN_SELECT or NIN_KEYSELECT or WM_LBUTTONUP:
                    _post(() => Activated?.Invoke());
                    break;
                case WM_CONTEXTMENU or WM_RBUTTONUP:
                    EventLog.Write("tray-menu-request", new { x, y });
                    SetForegroundWindow(Handle);
                    _post(() => MenuRequested?.Invoke(x, y));
                    break;
                case NIN_BALLOONSHOW: Raise("show"); break;
                case NIN_BALLOONHIDE: Raise("hide"); break;
                case NIN_BALLOONTIMEOUT: Raise("timeout"); break;
                case NIN_BALLOONUSERCLICK: Raise("click"); break;
            }
            return IntPtr.Zero;
        }
        if (msg == TaskbarCreated)
        {
            EventLog.Write("taskbar-created");
            _added = false;
            Add();
        }
        if (msg == WM_TIMER && wParam == RetryTimer)
        {
            if (!_added && _attempts < 30) Add();
            else KillTimer(Handle, RetryTimer);
            return IntPtr.Zero;
        }
        if (Extra?.Invoke(msg, wParam, lParam) == true) return IntPtr.Zero;
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Raise(string what)
    {
        EventLog.Write("balloon-" + what);
        _post(() => Balloon?.Invoke(what));
    }

    public void Dispose()
    {
        KillTimer(Handle, RetryTimer);
        if (_added)
        {
            var d = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref d);
            _added = false;
        }
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        DestroyWindow(Handle);
    }
}
