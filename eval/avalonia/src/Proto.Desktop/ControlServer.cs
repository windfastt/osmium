using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using Proto.Views;

namespace Proto.Desktop;

/// <summary>
/// The harness's control channel: a named pipe that takes one command line per connection and answers one JSON line.
/// Commands run on the UI thread. Only started with --control (the preview itself never listens).
/// </summary>
internal static class ControlServer
{
    public static void Start(string name)
    {
        var t = new Thread(() => Loop(name)) { IsBackground = true, Name = "UI evaluation control" };
        t.Start();
        EventLog.Write("control", new { pipe = name });
    }

    private static void Loop(string name)
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                var line = reader.ReadLine();
                if (line is null) continue;
                string reply;
                try
                {
                    reply = Dispatcher.UIThread.InvokeAsync(() => Handle(line)).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    reply = JsonSerializer.Serialize(new { ok = false, error = e.ToString() });
                }
                writer.WriteLine(reply);
                pipe.WaitForPipeDrain();
            }
            catch (Exception e)
            {
                EventLog.Write("control-error", new { error = e.Message });
                Thread.Sleep(100);
            }
        }
    }

    private static string J(object o) => JsonSerializer.Serialize(o);

    private static async Task<string> Handle(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        string cmd = parts[0], arg = parts.Length > 1 ? parts[1] : "";
        EventLog.Write("command", new { cmd, arg });
        var main = DesktopShell.Main;
        switch (cmd)
        {
            case "ping":
                return J(new { ok = true, pid = Environment.ProcessId });
            case "state":
                return J(new
                {
                    ok = true,
                    window = main is not null,
                    hwnd = main?.TryGetPlatformHandle()?.Handle.ToInt64(),
                    scaling = main?.RenderScaling,
                    dpi = main is null ? 0 : DesktopShell.Dpi(main),
                    appTheme = Application.Current?.ActualThemeVariant.ToString(),
                    windowTheme = main?.ActualThemeVariant.ToString(),
                    darkTitleBar = main is null ? null : DesktopShell.DarkTitleBar(main),
                    platform = Application.Current?.PlatformSettings?.GetColorValues() is { } cv
                        ? new { theme = cv.ThemeVariant.ToString(), contrast = cv.ContrastPreference.ToString(), accent = cv.AccentColor1.ToString() } : null,
                    spiHighContrast = Native.HighContrastOn(out var scheme),
                    hcScheme = scheme,
                    tray = DesktopShell.TrayRoute,
                    trayHwnd = DesktopShell.TrayRoute == "shell" ? DesktopShell.Shell?.Handle.ToInt64() : DesktopShell.AvaloniaMessageWindow().ToInt64(),
                    trayRect = DesktopShell.TrayRoute == "shell" ? DesktopShell.Shell?.Rect() : DesktopShell.IconRect(DesktopShell.AvaloniaMessageWindow(), 1),
                });
            case "open-window":
                // Returns at once: the frame is reported in the log ("first-frame" or "frame-after-reopen").
                DesktopShell.ShowMain(int.TryParse(arg, out var p) ? p : -1);
                return J(new { ok = DesktopShell.Main is not null });
            case "close-window":
                main?.Close();
                return J(new { ok = true, hadWindow = main is not null });
            case "nav":
                main?.Navigate(int.Parse(arg));
                await Frame();
                return J(new { ok = main is not null });
            case "theme":
                Application.Current!.RequestedThemeVariant = arg switch { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
                await Frame();
                return J(new { ok = true, actual = Application.Current.ActualThemeVariant.ToString(), darkTitleBar = main is null ? null : DesktopShell.DarkTitleBar(main) });
            case "gc":
            {
                long pb0 = Process.GetCurrentProcess().PrivateMemorySize64;
                MemoryReport.FullGc();
                return J(new { ok = true, privateBefore = pb0, report = MemoryReport.Collect(DesktopShell.SkiaStats()) });
            }
            case "memory-report":
                return J(new { ok = true, report = MemoryReport.Collect(DesktopShell.SkiaStats()) });
            case "trim":
                DesktopShell.TrimIdle();
                return J(new { ok = true, report = MemoryReport.Collect(DesktopShell.SkiaStats()) });
            case "bench-archives":
            {
                if (main is null) return J(new { ok = false, error = "no window" });
                var steps = await ArchiveBench.Run(main, ArchiveBench.LiveFrame);
                return J(new { ok = steps.All(s => s.Ok), steps });
            }
            case "boxes":
                return main is null ? J(new { ok = false, error = "no window" }) : WinChecks.Boxes(main);
            case "capture":
                return main is null ? J(new { ok = false, error = "no window" }) : await WinChecks.Capture(main, arg);
            case "scaled":
                return main is null ? J(new { ok = false, error = "no window" }) : WinChecks.Scaled(main, arg);
            case "fonts":
                return main is null ? J(new { ok = false, error = "no window" }) : WinChecks.Fonts(main, arg);
            case "tray-menu-open":
            {
                var how = DesktopShell.OpenMenuLikeRightClick();
                for (int i = 0; i < 50 && DesktopShell.FindTrayMenuWindow() is null; i++) await Task.Delay(40);
                var menu = DesktopShell.FindTrayMenuWindow();
                if (menu is null) return J(new { ok = false, how, error = "no menu window appeared" });
                await Frame(menu);
                return J(new { ok = true, how, hwnd = menu.TryGetPlatformHandle()?.Handle.ToInt64(), theme = menu.ActualThemeVariant.ToString(), check = WinChecks.MenuColours(menu, arg) });
            }
            case "tray-menu-close":
            {
                var menu = DesktopShell.FindTrayMenuWindow();
                menu?.Close();
                return J(new { ok = true, closed = menu is not null });
            }
            case "balloon":
                return J(new { ok = DesktopShell.TestBalloon(), route = DesktopShell.TrayRoute });
            case "colors":
                DesktopShell.LogColors("asked");
                return J(new { ok = true });
            case "quit":
                Dispatcher.UIThread.Post(DesktopShell.Exit);
                return J(new { ok = true });
            default:
                return J(new { ok = false, error = "unknown command " + cmd });
        }
    }

    private static Task Frame(Avalonia.Controls.TopLevel? t = null) =>
        (t ?? DesktopShell.Main) is { } top ? ArchiveBench.LiveFrame(top) : Task.CompletedTask;
}
