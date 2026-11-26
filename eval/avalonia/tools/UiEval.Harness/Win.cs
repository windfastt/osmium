using System.Runtime.InteropServices;
using System.Text;

namespace UiEval;

/// <summary>Win32 calls the harness uses: window lookup, paint state, input, display mode and scale, theme, captures.</summary>
internal static class Win
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; public override readonly string ToString() => $"[{Left},{Top},{Width}x{Height}]"; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetUpdateRect(IntPtr hWnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr h, int index, StringBuilder sb, int len, out int needed);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    public const uint WM_CLOSE = 0x0010, WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 2;
    public static readonly IntPtr HWND_BROADCAST = new(0xffff), HWND_MESSAGE = new(-3);

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);

    /// <summary>The top-level window under a screen point.</summary>
    public static IntPtr TopAt(int x, int y) => GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2);

    public static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "none";
        GetWindowThreadProcessId(h, out var pid);
        string name;
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName; } catch (Exception) { name = "?"; }
        GetWindowRect(h, out var r);
        return $"{name} ({pid}) class '{ClassOf(h)}' title '{TextOf(h)}' {r}";
    }

    /// <summary>Every visible top-level window with its process, class, title and rectangle (what is on the desktop).</summary>
    public static List<string> VisibleWindows()
    {
        var list = new List<string>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h)) { GetWindowRect(h, out var r); if (r.Width > 0 && r.Height > 0) list.Add(Describe(h)); }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string TextOf(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }

    public static List<IntPtr> TopWindows(int pid, bool visibleOnly)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p == pid && (!visibleOnly || IsWindowVisible(h))) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The process's largest visible top-level window (its main window), or zero.</summary>
    public static IntPtr MainWindow(int pid)
    {
        IntPtr best = IntPtr.Zero;
        long area = 0;
        foreach (var h in TopWindows(pid, true))
        {
            GetWindowRect(h, out var r);
            long a = (long)r.Width * r.Height;
            if (a > area && r.Width > 300 && r.Height > 200) { area = a; best = h; }
        }
        return best;
    }

    /// <summary>True when neither the window nor any child has a pending update region (everything has been painted).</summary>
    public static bool FullyPainted(IntPtr top)
    {
        if (GetUpdateRect(top, IntPtr.Zero, false)) return false;
        bool clean = true;
        EnumChildWindows(top, (h, _) =>
        {
            if (IsWindowVisible(h) && GetUpdateRect(h, IntPtr.Zero, false)) { clean = false; return false; }
            return true;
        }, IntPtr.Zero);
        return clean;
    }

    public static RECT ClientScreenRect(IntPtr h)
    {
        GetClientRect(h, out var c);
        var p = new POINT();
        ClientToScreen(h, ref p);
        return new RECT { Left = p.X, Top = p.Y, Right = p.X + c.Width, Bottom = p.Y + c.Height };
    }

    public static string InputDesktopName()
    {
        var h = OpenInputDesktop(0, false, 0x0001);
        if (h == IntPtr.Zero) return "none (error " + Marshal.GetLastWin32Error() + ")";
        try
        {
            var sb = new StringBuilder(256);
            return GetUserObjectInformation(h, 2, sb, 512, out _) ? sb.ToString() : "?";
        }
        finally { CloseDesktop(h); }
    }

    // ---------------------------------------------------------------- input

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public int type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy, mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public short wVk, wScan; public int dwFlags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);

    private const int MOUSEEVENTF_MOVE = 1, MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private static uint Send(params INPUT[] i) => SendInput((uint)i.Length, i, Marshal.SizeOf<INPUT>());

    public static void MouseMove(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        int nx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1)), ny = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));
        Send(new INPUT { type = 0, u = new INPUTUNION { mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } } });
    }

    public static void MouseButton(bool down) =>
        Send(new INPUT { type = 0, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP } } });

    public static void Key(ushort vk, bool shift = false)
    {
        var l = new List<INPUT>();
        if (shift) l.Add(new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0x10 } } });
        l.Add(new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (short)vk } } });
        l.Add(new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (short)vk, dwFlags = 2 } } });
        if (shift) l.Add(new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0x10, dwFlags = 2 } } });
        Send([.. l]);
    }

    // ---------------------------------------------------------------- display mode

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettingsEx(string? device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);

    public static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>(), dmDeviceName = "", dmFormName = "" };

    public static List<(int W, int H, int Bpp, int Hz)> DisplayModes()
    {
        var modes = new HashSet<(int, int, int, int)>();
        var dm = NewDevMode();
        for (int i = 0; EnumDisplaySettings(null, i, ref dm); i++) modes.Add((dm.dmPelsWidth, dm.dmPelsHeight, dm.dmBitsPerPel, dm.dmDisplayFrequency));
        return [.. modes.OrderBy(m => m.Item1 * m.Item2)];
    }

    public static (int W, int H) CurrentMode()
    {
        var dm = NewDevMode();
        EnumDisplaySettings(null, -1, ref dm);
        return (dm.dmPelsWidth, dm.dmPelsHeight);
    }

    public static int SetMode(int w, int h)
    {
        var dm = NewDevMode();
        EnumDisplaySettings(null, -1, ref dm);
        dm.dmPelsWidth = w;
        dm.dmPelsHeight = h;
        dm.dmFields = 0x80000 | 0x100000; // DM_PELSWIDTH | DM_PELSHEIGHT
        return ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
    }

    // ---------------------------------------------------------------- per-monitor scale (DisplayConfig)

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, byte[] pathArray, ref uint modes, byte[] modeArray, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(byte[] packet);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(byte[] packet);

    private static readonly int[] DpiSteps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];
    private const int PathSize = 72, ModeSize = 64;

    /// <summary>The primary path's source: adapter LUID and source id (first active path).</summary>
    private static (uint Low, int High, uint Id)? PrimarySource()
    {
        if (GetDisplayConfigBufferSizes(2, out var np, out var nm) != 0) return null;
        var paths = new byte[np * PathSize];
        var modes = new byte[nm * ModeSize];
        if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0 || np == 0) return null;
        return (BitConverter.ToUInt32(paths, 0), BitConverter.ToInt32(paths, 4), BitConverter.ToUInt32(paths, 8));
    }

    /// <summary>Current, minimum and maximum scale in percent for the primary display (undocumented DPI_SCALE packets).</summary>
    public static (int Current, int Min, int Max, int Recommended)? GetScale()
    {
        if (PrimarySource() is not { } s) return null;
        var p = new byte[32];
        BitConverter.GetBytes(-3).CopyTo(p, 0);
        BitConverter.GetBytes(32).CopyTo(p, 4);
        BitConverter.GetBytes(s.Low).CopyTo(p, 8);
        BitConverter.GetBytes(s.High).CopyTo(p, 12);
        BitConverter.GetBytes(s.Id).CopyTo(p, 16);
        if (DisplayConfigGetDeviceInfo(p) != 0) return null;
        int min = BitConverter.ToInt32(p, 20), cur = BitConverter.ToInt32(p, 24), max = BitConverter.ToInt32(p, 28);
        int rec = Math.Abs(min);
        int At(int i) => DpiSteps[Math.Clamp(rec + i, 0, DpiSteps.Length - 1)];
        return (At(cur), At(min), At(max), DpiSteps[rec]);
    }

    public static int SetScale(int percent)
    {
        if (PrimarySource() is not { } s || GetScale() is not { } now) return -1;
        int rec = Array.IndexOf(DpiSteps, now.Recommended);
        int idx = Array.IndexOf(DpiSteps, percent);
        if (idx < 0) return -2;
        var p = new byte[24];
        BitConverter.GetBytes(-4).CopyTo(p, 0);
        BitConverter.GetBytes(24).CopyTo(p, 4);
        BitConverter.GetBytes(s.Low).CopyTo(p, 8);
        BitConverter.GetBytes(s.High).CopyTo(p, 12);
        BitConverter.GetBytes(s.Id).CopyTo(p, 16);
        BitConverter.GetBytes(idx - rec).CopyTo(p, 20);
        return DisplayConfigSetDeviceInfo(p);
    }

    // ---------------------------------------------------------------- high contrast

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HIGHCONTRAST { public int cbSize; public int dwFlags; public IntPtr lpszDefaultScheme; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(int action, int param, ref HIGHCONTRAST hc, int winIni);

    public static (bool On, string? Scheme) GetHighContrast()
    {
        var hc = new HIGHCONTRAST { cbSize = Marshal.SizeOf<HIGHCONTRAST>() };
        if (!SystemParametersInfo(0x42, hc.cbSize, ref hc, 0)) return (false, null);
        return ((hc.dwFlags & 1) != 0, hc.lpszDefaultScheme == IntPtr.Zero ? null : Marshal.PtrToStringUni(hc.lpszDefaultScheme));
    }

    public static (bool Ok, int Error) SetHighContrast(bool on, string scheme = "High Contrast Black")
    {
        var s = Marshal.StringToHGlobalUni(scheme);
        try
        {
            var hc = new HIGHCONTRAST { cbSize = Marshal.SizeOf<HIGHCONTRAST>(), dwFlags = on ? 1 : 0, lpszDefaultScheme = s };
            bool ok = SystemParametersInfo(0x43, hc.cbSize, ref hc, 0x01 | 0x02);
            return (ok, ok ? 0 : Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(s); }
    }

    public static void BroadcastSetting(string what) =>
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, what, SMTO_ABORTIFHUNG, 5000, out _);

    // ---------------------------------------------------------------- notification icons

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONIDENTIFIER { public int cbSize; public IntPtr hWnd; public int uID; public Guid guidItem; }
    [DllImport("shell32.dll")] public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);

    public static (int Hr, RECT Rect) IconRect(IntPtr owner, int id)
    {
        var nid = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = owner, uID = id };
        int hr = Shell_NotifyIconGetRect(ref nid, out var r);
        return (hr, r);
    }

    /// <summary>Message-only windows of a class owned by a process (the preview's notification icon owner).</summary>
    public static IntPtr MessageWindow(int pid, string cls)
    {
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(HWND_MESSAGE, h, cls, null)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(h, out var p);
            if (p == pid) return h;
        }
        return IntPtr.Zero;
    }

    /// <summary>A top-level window of a process whose class starts with a prefix (Avalonia's platform window).</summary>
    public static IntPtr WindowByClassPrefix(int pid, string prefix) =>
        TopWindows(pid, false).FirstOrDefault(h => ClassOf(h).StartsWith(prefix, StringComparison.Ordinal));
}
