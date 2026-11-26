using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Proto.Views;

namespace Proto.Desktop;

/// <summary>
/// The tray application: owns the notification icon, creates the main window on demand and closes (destroys) it when
/// the user closes it, so the idle tray process holds no window, visual tree or render target.
/// </summary>
internal static class DesktopShell
{
    private static IClassicDesktopStyleApplicationLifetime _life = null!;
    private static ShellTray? _shellTray;
    private static TrayIcon? _avaloniaTray;
    private static bool _firstFrameLogged;
    private static Stopwatch _main = null!;

    public static MainWindow? Main { get; private set; }
    public static TrayMenuWindow? OpenTrayMenu { get; private set; }
    public static string TrayRoute => Program.Opt.Tray;
    public static ShellTray? Shell => _shellTray;

    public static void Start(IClassicDesktopStyleApplicationLifetime life, Stopwatch main)
    {
        _life = life;
        _main = main;
        var app = Application.Current!;
        if (Program.Opt.Theme is "light") app.RequestedThemeVariant = ThemeVariant.Light;
        else if (Program.Opt.Theme is "dark") app.RequestedThemeVariant = ThemeVariant.Dark;
        LogColors("startup");
        if (app.PlatformSettings is { } ps) ps.ColorValuesChanged += (_, _) => LogColors("changed");
        app.ActualThemeVariantChanged += (_, _) => EventLog.Write("app-theme", new { actual = app.ActualThemeVariant.ToString() });
        RowDrag.Started += p => EventLog.Write("drag-start", new { path = p });
        RowDrag.Dragged += (p, e) => EventLog.Write("drag-end", new { path = p, effect = e.ToString() });

        switch (TrayRoute)
        {
            case "shell":
                _shellTray = new ShellTray("Osmium UI preview", IconFile(), a => Dispatcher.UIThread.Post(a), messageOnly: true);
                _shellTray.Activated += () => ShowMain();
                _shellTray.MenuRequested += (x, y) => ShowTrayMenu(new PixelPoint(x, y));
                _shellTray.Balloon += what => { if (what == "click") ShowMain(); };
                break;
            case "avalonia":
                CreateAvaloniaTray();
                break;
        }
        if (Program.Opt.Control is { } pipe) ControlServer.Start(pipe);
        if (!Program.Opt.Hidden) ShowMain(Program.Opt.Page);
        EventLog.Write("started", new { tray = TrayRoute, sinceMainMs = main.Elapsed.TotalMilliseconds });
    }

    private static string? IconFile()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "osmium.ico");
        if (File.Exists(beside)) return beside;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "OsmiumUiPreview", "osmium.ico");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var src = AssetLoader.Open(new Uri("avares://Proto.Views/Assets/osmium.ico"));
            using var dst = File.Create(path);
            src.CopyTo(dst);
            return path;
        }
        catch (Exception) { return null; }
    }

    private static Stopwatch? _openRequested;
    private static DispatcherTimer? _linger;

    public static void ShowMain(int page = -1)
    {
        _linger?.Stop();
        if (Main is null)
        {
            _openRequested = Stopwatch.StartNew();
            EventLog.Write("open-requested", new { page });
            var w = new MainWindow();
            Main = w;
            w.Opened += (_, _) => OnOpened(w);
            w.Closed += (_, _) =>
            {
                if (ReferenceEquals(Main, w)) Main = null;
                EventLog.Write("window-closed");
                if (Program.Opt.UiChild && Program.Opt.LingerMs > 0)
                {
                    _linger?.Stop();
                    _linger = new DispatcherTimer(TimeSpan.FromMilliseconds(Program.Opt.LingerMs), DispatcherPriority.Background, (_, _) =>
                    {
                        _linger?.Stop();
                        if (Main is null) { EventLog.Write("linger-expired"); Exit(); }
                    });
                    _linger.Start();
                    EventLog.Write("lingering", new { ms = Program.Opt.LingerMs });
                }
                else if (Program.Opt.UiChild) Dispatcher.UIThread.Post(Exit);
                else if (Program.Opt.IdleTrim) Dispatcher.UIThread.Post(TrimIdle, DispatcherPriority.Background);
            };
            w.ScalingChanged += (_, _) => EventLog.Write("scaling", new { scaling = w.RenderScaling, dpi = Dpi(w) });
            w.ActualThemeVariantChanged += (_, _) => EventLog.Write("window-theme", new { actual = w.ActualThemeVariant.ToString(), darkTitleBar = DarkTitleBar(w) });
            w.PageShown += (i, _) => EventLog.Write("page", new { index = i, name = NavItem.All[i].Name });
            if (page >= 0) w.Navigate(page);
            w.Show();
            EventLog.Write("show-called", new { sinceRequestMs = _openRequested.Elapsed.TotalMilliseconds });
        }
        else
        {
            if (page >= 0) Main.Navigate(page);
            if (Main.WindowState == WindowState.Minimized) Main.WindowState = WindowState.Normal;
            Main.Activate();
        }
    }

    private static async void OnOpened(MainWindow w)
    {
        EventLog.Write("opened", new { sinceMainMs = _main.Elapsed.TotalMilliseconds, scaling = w.RenderScaling, dpi = Dpi(w) });
        var compositor = ElementComposition.GetElementVisual(w)?.Compositor;
        if (compositor is not null) await compositor.RequestCompositionBatchCommitAsync().Rendered;
        if (_firstFrameLogged)
        {
            EventLog.Write("frame-after-reopen", new { sinceRequestMs = _openRequested?.Elapsed.TotalMilliseconds, jit = Jit() });
            return;
        }
        _firstFrameLogged = true;
        object? gpu = null;
        try { gpu = compositor is null ? null : await compositor.TryGetCompositionGpuInterop(); }
        catch (Exception e) { EventLog.Write("gpu-interop-error", new { error = e.Message }); }
        EventLog.Write("first-frame", new
        {
            sinceMainMs = _main.Elapsed.TotalMilliseconds,
            gpuInterop = gpu is not null,
            scaling = w.RenderScaling,
            dpi = Dpi(w),
            client = new[] { w.ClientSize.Width, w.ClientSize.Height },
            theme = w.ActualThemeVariant.ToString(),
            darkTitleBar = DarkTitleBar(w),
            workingSetMiB = Environment.WorkingSet / 1048576.0,
            sinceRequestMs = _openRequested?.Elapsed.TotalMilliseconds,
            jit = Jit(),
        });
        if (Program.Opt.ExitAfterFirstFrame) Exit();
    }

    public static object Jit() => new
    {
        methods = System.Runtime.JitInfo.GetCompiledMethodCount(false),
        ilKiB = System.Runtime.JitInfo.GetCompiledILBytes(false) / 1024,
        ms = Math.Round(System.Runtime.JitInfo.GetCompilationTime(false).TotalMilliseconds, 1),
    };

    public static object SkiaStats() => new
    {
        resourceCacheKiB = SkiaSharp.SKGraphics.GetResourceCacheTotalBytesUsed() / 1024,
        resourceCacheLimitKiB = SkiaSharp.SKGraphics.GetResourceCacheTotalByteLimit() / 1024,
        fontCacheKiB = SkiaSharp.SKGraphics.GetFontCacheUsed() / 1024,
        fontCacheCount = SkiaSharp.SKGraphics.GetFontCacheCountUsed(),
    };

    /// <summary>After the window closed: drop Skia's CPU caches (glyphs, images) and compact the managed heap.</summary>
    public static void TrimIdle()
    {
        var before = MemoryReport.Collect(SkiaStats());
        SkiaSharp.SKGraphics.PurgeAllCaches();
        MemoryReport.FullGc();
        EventLog.Write("idle-trim", new { before, after = MemoryReport.Collect(SkiaStats()) });
    }

    public static void LogColors(string when)
    {
        var cv = Application.Current?.PlatformSettings?.GetColorValues();
        bool hc = Native.HighContrastOn(out var scheme);
        EventLog.Write("colors", new
        {
            when,
            platformTheme = cv?.ThemeVariant.ToString(),
            contrast = cv?.ContrastPreference.ToString(),
            accent = cv?.AccentColor1.ToString(),
            spiHighContrast = hc,
            hcScheme = scheme,
            appActual = Application.Current?.ActualThemeVariant.ToString(),
        });
    }

    public static uint Dpi(TopLevel t) =>
        t.TryGetPlatformHandle()?.Handle is { } h && h != IntPtr.Zero ? Native.GetDpiForWindow(h) : 0;

    public static bool? DarkTitleBar(TopLevel t)
    {
        if (t.TryGetPlatformHandle()?.Handle is not { } h || h == IntPtr.Zero) return null;
        return Native.DwmGetWindowAttribute(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, out int v, 4) == 0 ? v != 0 : null;
    }

    public static IReadOnlyList<TrayMenuEntry> MenuEntries() =>
    [
        new("Osmium is idle · preview", null, Enabled: false),
        TrayMenuEntry.Separator,
        new("Open Osmium", () => ShowMain(), Bold: true),
        new("Open Archives", () => ShowMain(NavItem.ArchivesIndex)),
        new("Show a test notification", () => TestBalloon()),
        TrayMenuEntry.Separator,
        new("Exit", Exit),
    ];

    public static void ShowTrayMenu(PixelPoint anchor)
    {
        OpenTrayMenu?.Close();
        var menu = new TrayMenuWindow(MenuEntries(), anchor);
        OpenTrayMenu = menu;
        menu.Opened += (_, _) => EventLog.Write("tray-menu-opened", new { route = "shell", theme = menu.ActualThemeVariant.ToString(), hwnd = menu.TryGetPlatformHandle()?.Handle.ToInt64() });
        menu.Closed += (_, _) => { if (ReferenceEquals(OpenTrayMenu, menu)) OpenTrayMenu = null; };
        menu.Show();
    }

    public static bool TestBalloon() =>
        _shellTray?.ShowBalloon("Compression finished", "project-2026-11.osa · 64.7 MB · 38.8 % of the original (preview: nothing was compressed)") ?? false;

    private static void CreateAvaloniaTray()
    {
        var menu = new NativeMenu();
        var status = new NativeMenuItem("Osmium is idle · preview") { IsEnabled = false };
        var open = new NativeMenuItem("Open Osmium");
        open.Click += (_, _) => ShowMain();
        var archives = new NativeMenuItem("Open Archives");
        archives.Click += (_, _) => ShowMain(NavItem.ArchivesIndex);
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => Exit();
        menu.Items.Add(status);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(open);
        menu.Items.Add(archives);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);
        _avaloniaTray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Proto.Views/Assets/osmium.ico"))),
            ToolTipText = "Osmium UI preview",
            Menu = menu,
        };
        _avaloniaTray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(Application.Current!, [_avaloniaTray]);
        var hwnd = AvaloniaMessageWindow();
        EventLog.Write("tray-added", new
        {
            route = "avalonia",
            hwnd = hwnd.ToInt64(),
            items = menu.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToArray(),
            rect = IconRect(hwnd, 1),
        });
    }

    /// <summary>Avalonia's hidden platform window (class "AvaloniaMessageWindow {guid}"): its TrayIcon's owner.</summary>
    public static IntPtr AvaloniaMessageWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
        {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString().StartsWith("AvaloniaMessageWindow", StringComparison.Ordinal)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static int[]? IconRect(IntPtr hwnd, int id)
    {
        var nid = new Native.NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(), hWnd = hwnd, uID = id };
        return Native.Shell_NotifyIconGetRect(ref nid, out var r) == 0 ? [r.Left, r.Top, r.Width, r.Height] : null;
    }

    /// <summary>Opens the notification-area menu the way a right-click on the icon does (for the harness).</summary>
    public static string OpenMenuLikeRightClick()
    {
        if (TrayRoute == "shell" && _shellTray is { } t)
        {
            // Through the icon's own callback, so the path is the one a real right-click takes.
            var anchor = t.Rect() is { } r ? (r[0] + r[2] / 2) | ((r[1] + r[3] / 2) << 16) : 0;
            Native.PostMessage(t.Handle, ShellTray.CallbackMessage, anchor, (ShellTray.IconId << 16) | Native.WM_CONTEXTMENU);
            return "posted WM_CONTEXTMENU to the shell icon";
        }
        if (TrayRoute == "avalonia")
        {
            // Avalonia's TrayIconImpl: WM_USER + 1024 with wParam = icon id (1) and lParam = WM_RBUTTONUP.
            Native.PostMessage(AvaloniaMessageWindow(), Native.WM_USER + 1024, 1, Native.WM_RBUTTONUP);
            return "posted WM_RBUTTONUP to Avalonia's TrayIcon";
        }
        return "no tray";
    }

    /// <summary>The open notification-area menu window, whichever route drew it.</summary>
    public static Window? FindTrayMenuWindow() =>
        OpenTrayMenu ?? _life.Windows.FirstOrDefault(w => w.Name?.StartsWith("AvaloniaTrayPopupRoot", StringComparison.Ordinal) == true);

    public static void Exit()
    {
        EventLog.Write("exit");
        _shellTray?.Dispose();
        _shellTray = null;
        _avaloniaTray?.Dispose();
        _avaloniaTray = null;
        _life.Shutdown();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc proc, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
}
