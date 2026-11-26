using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static Proto.Desktop.Native;

namespace Proto.Desktop;

/// <summary>
/// The two-process layout (--tray-host): a small notification-area process with no UI framework loaded (Win32 message
/// loop, Shell_NotifyIcon with balloons, a native popup menu that follows dark mode through uxtheme), which starts the
/// Avalonia window as its own process (--ui-child) when the user opens it; that process exits when its window closes,
/// so the idle tray costs what a plain .NET process costs. Nothing in this file may touch an Avalonia type.
/// </summary>
internal static class TrayHost
{
    private const uint CmdMenu = WM_APP + 2, CmdEndMenu = WM_APP + 3, CmdQuit = WM_APP + 4, CmdOpen = WM_APP + 5;
    private static ShellTray _tray = null!;
    private static Process? _child;
    private static string? _childLog, _childPipe;
    private static int _childSeq;
    private static Options _opt = new();

    public static int Run(Options opt)
    {
        _opt = opt;
        EventLog.Write("host-start", new { assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).ToArray() });
        // Touch the desktop logic the WinForms tray process carries, so the comparison includes it.
        EventLog.Write("engine", new { assembly = typeof(Osmium.Desktop.Jobs.JobManager).Assembly.GetName().Name });
        DarkMenus.Apply(opt.Theme);
        _tray = new ShellTray("Osmium UI preview", Path.Combine(AppContext.BaseDirectory, "osmium.ico"), a => a(), messageOnly: false);
        _tray.Activated += () => OpenUi(-1);
        _tray.MenuRequested += ShowMenu;
        _tray.Balloon += what => { if (what == "click") OpenUi(1); };
        _tray.Extra = (msg, w, l) =>
        {
            switch (msg)
            {
                case CmdMenu: ShowMenu((int)w, (int)l); return true;
                case CmdEndMenu: EndMenu(); return true;
                case CmdOpen: OpenUi((int)w); return true;
                case CmdQuit: Quit(); return true;
                case WM_SETTINGCHANGE:
                    var what = l == IntPtr.Zero ? null : Marshal.PtrToStringUni(l);
                    if (what == "ImmersiveColorSet") { DarkMenus.Refresh(); EventLog.Write("host-theme", new { dark = DarkMenus.SystemDark() }); }
                    return false;
            }
            return false;
        };
        if (opt.Control is { } pipe) new Thread(() => Serve(pipe)) { IsBackground = true, Name = "host control" }.Start();
        EventLog.Write("started", new { tray = "host" });
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        EventLog.Write("exit");
        return 0;
    }

    private static void ShowMenu(int x, int y)
    {
        if (x == 0 && y == 0 && _tray.Rect() is { } r) { x = r[0] + r[2] / 2; y = r[1] + r[3] / 2; }
        var m = CreatePopupMenu();
        AppendMenu(m, MF_STRING | MF_GRAYED, 1, _child is { HasExited: false } ? "Osmium window is open · preview" : "Osmium is idle · preview");
        AppendMenu(m, MF_SEPARATOR, 0, null);
        AppendMenu(m, MF_STRING, 2, "Open Osmium");
        AppendMenu(m, MF_STRING, 3, "Open Archives");
        AppendMenu(m, MF_STRING, 4, "Show a test notification");
        AppendMenu(m, MF_SEPARATOR, 0, null);
        AppendMenu(m, MF_STRING, 5, "Exit");
        SetMenuDefaultItem(m, 2, 0);
        SetForegroundWindow(_tray.Handle);
        EventLog.Write("tray-menu-opened", new { route = "host", dark = DarkMenus.SystemDark(), mode = DarkMenus.Mode });
        int cmd = TrackPopupMenuEx(m, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, x, y, _tray.Handle, IntPtr.Zero);
        PostMessage(_tray.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(m);
        EventLog.Write("tray-menu-closed", new { cmd });
        switch (cmd)
        {
            case 2: OpenUi(-1); break;
            case 3: OpenUi(2); break;
            case 4: TestBalloon(); break;
            case 5: Quit(); break;
        }
    }

    private static bool TestBalloon() =>
        _tray.ShowBalloon("Compression finished", "project-2026-11.osa · 64.7 MB · 38.8 % of the original (preview: nothing was compressed)");

    /// <summary>Starts the window process, or asks the running one to come forward.</summary>
    private static object OpenUi(int page)
    {
        var t0 = Stopwatch.StartNew();
        EventLog.Write("open-requested", new { page, childRunning = _child is { HasExited: false } });
        if (_child is { HasExited: false } && _childPipe is not null)
        {
            var reply = Send(_childPipe, page >= 0 ? $"open-window {page}" : "open-window");
            return new { reused = true, childPid = _child.Id, reply };
        }
        _childSeq++;
        var dir = _opt.Log is { } l ? Path.GetDirectoryName(Path.GetFullPath(l))! : Path.Combine(Path.GetTempPath(), "OsmiumUiPreview");
        _childLog = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(_opt.Log ?? "host")}-ui-{_childSeq}.jsonl");
        _childPipe = $"OsmiumUiPreview-ui-{Environment.ProcessId}-{_childSeq}";
        var args = $"--ui-child --log \"{_childLog}\" --control {_childPipe}" + (page >= 0 ? $" --page {page}" : "") + (_opt.Theme is { } th ? $" --theme {th}" : "")
                   + (_opt.LingerMs > 0 ? $" --linger-ms {_opt.LingerMs}" : "") + (_opt.Render is { } rm ? $" --render {rm}" : "");
        var psi = new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false };
        psi.Environment.Remove("DOTNET_STARTUP_HOOKS");
        _child = Process.Start(psi);
        AllowSetForegroundWindow(_child!.Id);
        EventLog.Write("ui-started", new { pid = _child.Id, log = _childLog, startMs = t0.Elapsed.TotalMilliseconds });
        return new { reused = false, childPid = _child.Id, childLog = _childLog, childPipe = _childPipe };
    }

    private static void Quit()
    {
        if (_child is { HasExited: false } && _childPipe is not null) Send(_childPipe, "quit");
        _tray.Dispose();
        PostQuitMessage(0);
    }

    private static string? Send(string pipe, string line)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut);
            c.Connect(5000);
            using var w = new StreamWriter(c, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var r = new StreamReader(c, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            w.WriteLine(line);
            return r.ReadLine();
        }
        catch (Exception e) { return "error " + e.Message; }
    }

    /// <summary>The harness's commands for the host (the same line protocol as the window process).</summary>
    private static void Serve(string name)
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                var line = reader.ReadLine() ?? "";
                var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
                EventLog.Write("command", new { cmd = parts[0], arg = parts.Length > 1 ? parts[1] : "" });
                object reply = parts[0] switch
                {
                    "ping" => new { ok = true, pid = Environment.ProcessId },
                    "state" => new
                    {
                        ok = true, tray = "host", trayHwnd = _tray.Handle.ToInt64(), trayAdded = _tray.Added, trayRect = _tray.Rect(),
                        childPid = _child is { HasExited: false } ? _child.Id : (int?)null, childLog = _childLog, childPipe = _childPipe,
                        darkMenus = DarkMenus.Mode, systemDark = DarkMenus.SystemDark(),
                    },
                    "open-window" => OpenOnUiThread(parts.Length > 1 && int.TryParse(parts[1], out var pg) ? pg : -1),
                    "tray-menu-open" => new { ok = PostMessage(_tray.Handle, CmdMenu, IntPtr.Zero, IntPtr.Zero), route = "host" },
                    "tray-menu-close" => new { ok = PostMessage(_tray.Handle, CmdEndMenu, IntPtr.Zero, IntPtr.Zero) },
                    "balloon" => new { ok = TestBalloon(), route = "host" },
                    "gc" => GcReport(),
                    "memory-report" => new { ok = true, report = MemoryReport.Collect() },
                    "quit" => new { ok = PostMessage(_tray.Handle, CmdQuit, IntPtr.Zero, IntPtr.Zero) },
                    _ => new { ok = false, error = "unknown command " + parts[0] },
                };
                writer.WriteLine(EventLog.Json(reply));
                pipe.WaitForPipeDrain();
            }
            catch (Exception e)
            {
                EventLog.Write("control-error", new { error = e.Message });
                Thread.Sleep(100);
            }
        }
    }

    private static object GcReport()
    {
        long before = Process.GetCurrentProcess().PrivateMemorySize64;
        MemoryReport.FullGc();
        return new { ok = true, privateBefore = before, report = MemoryReport.Collect() };
    }

    /// <summary>Opening the window runs on the host's UI thread (it owns the child handle); the reply carries where the
    /// child logs, so the harness can wait for its first frame.</summary>
    private static object OpenOnUiThread(int page)
    {
        int seqBefore = _childSeq;
        bool reuse = _child is { HasExited: false };
        PostMessage(_tray.Handle, CmdOpen, page, IntPtr.Zero);
        var sw = Stopwatch.StartNew();
        while (!reuse && _childSeq == seqBefore && sw.ElapsedMilliseconds < 5000) Thread.Sleep(5);
        // A dictionary, not an anonymous object: a native AOT build keeps no property metadata for those.
        return new Dictionary<string, object?>
        {
            ["ok"] = true, ["reused"] = reuse, ["childPid"] = _child?.Id, ["childLog"] = _childLog, ["childPipe"] = _childPipe,
        };
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);
}

/// <summary>
/// Dark native menus for a process without a UI framework: uxtheme's SetPreferredAppMode (ordinal 135) and
/// FlushMenuThemes (ordinal 136), present since Windows 10 1903. "AllowDark" follows the system's app theme.
/// </summary>
internal static class DarkMenus
{
    private delegate int SetPreferredAppModeFn(int mode);
    private delegate void VoidFn();
    private static VoidFn? _flush, _refresh;
    public static string Mode { get; private set; } = "unset";

    public static void Apply(string? theme)
    {
        try
        {
            var ux = LoadLibrary("uxtheme.dll");
            var set = GetProcAddress(ux, 135);
            var flush = GetProcAddress(ux, 136);
            var refresh = GetProcAddress(ux, 104);
            if (set == IntPtr.Zero) { Mode = "uxtheme ordinal 135 missing"; return; }
            int mode = theme switch { "dark" => 2, "light" => 3, _ => 1 }; // ForceDark, ForceLight, AllowDark
            Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeFn>(set)(mode);
            if (flush != IntPtr.Zero) _flush = Marshal.GetDelegateForFunctionPointer<VoidFn>(flush);
            if (refresh != IntPtr.Zero) _refresh = Marshal.GetDelegateForFunctionPointer<VoidFn>(refresh);
            Refresh();
            Mode = mode switch { 2 => "ForceDark", 3 => "ForceLight", _ => "AllowDark" };
        }
        catch (Exception e) { Mode = "error " + e.Message; }
        EventLog.Write("dark-menus", new { mode = Mode, systemDark = SystemDark() });
    }

    public static void Refresh()
    {
        _refresh?.Invoke();
        _flush?.Invoke();
    }

    public static bool SystemDark()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}
