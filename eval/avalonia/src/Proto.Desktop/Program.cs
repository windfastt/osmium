using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Logging;
using Proto.Views;

namespace Proto.Desktop;

/// <summary>
/// The Osmium UI preview for Windows: the Avalonia prototype on the Win32 backend with a notification icon.
/// Options (the evaluation harness uses them; the preview runs without any):
///   --log FILE                 JSON-lines event log (first frame, theme, contrast, scaling, tray, balloons, drags)
///   --control NAME             a named pipe that takes the harness's commands (see ControlServer)
///   --tray shell|avalonia|none notification icon through Shell_NotifyIcon (default; has balloons) or Avalonia's TrayIcon
///   --render angle|software|wgl  Avalonia's rendering mode (default: Avalonia's own choice)
///   --theme light|dark         fix the variant (default: follow Windows)
///   --page N                   start on rail entry N (2 = Archives)
///   --hidden                   start in the notification area only
///   --exit-after-first-frame   log the first frame and quit (startup runs)
///   --tray-host                the notification-area process only (no UI framework); starts the window as --ui-child
///   --ui-child                 the window only: no notification icon, exits when the window closes
///   --idle-trim                when the window closes: purge Skia's caches and compact the managed heap
///   --linger-ms N              with --ui-child: stay alive N ms after the window closes, so a reopen is instant
/// </summary>
internal static class Program
{
    public static Options Opt { get; private set; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        var main = Stopwatch.StartNew();
        Opt = Options.Parse(args);
        EventLog.Open(Opt.Log);
        EventLog.Write("main", new
        {
            args,
            arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            os = Environment.OSVersion.VersionString,
            r2r = IsReadyToRun(),
            jitMethods = System.Runtime.JitInfo.GetCompiledMethodCount(false),
        });
        // The tray host never loads Avalonia: it branches off before any method that names an Avalonia type.
        return Opt.TrayHost ? TrayHost.Run(Opt) : RunUi(args, main);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunUi(string[] args, Stopwatch main)
    {
        EventLog.Write("ui-init", new { avalonia = typeof(AvaloniaObject).Assembly.GetName().Version?.ToString() });
        // The engine and desktop logic the WinForms app ships are part of this build (sizes compare like for like).
        EventLog.Write("engine", new { assembly = typeof(Osmium.Desktop.Jobs.JobManager).Assembly.GetName().Name });

        Logger.Sink = new AvaloniaLogSink();
        App.CreateMainWindow = false;
        App.Trace = what => EventLog.Write(what);
        var win32 = new Win32PlatformOptions();
        if (Opt.Render is { } r)
            win32.RenderingMode = r switch
            {
                "software" => [Win32RenderingMode.Software],
                "wgl" => [Win32RenderingMode.Wgl, Win32RenderingMode.Software],
                _ => [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            };
        var builder = AppBuilder.Configure<App>().UseWin32().UseSkia().UseHarfBuzz().With(win32)
            .AfterPlatformServicesSetup(_ => EventLog.Write("platform-ready"))
            .AfterSetup(_ => EventLog.Write("setup-done"));
        EventLog.Write("builder");
        try
        {
            return builder.StartWithClassicDesktopLifetime(args, lifetime =>
            {
                lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                lifetime.Startup += (_, _) => DesktopShell.Start(lifetime, main);
            });
        }
        catch (Exception e)
        {
            EventLog.Write("fatal", new { error = e.ToString() });
            throw;
        }
    }

    /// <summary>True when the main assembly carries ReadyToRun code (the published preview should).</summary>
    private static bool IsReadyToRun()
    {
        try
        {
            using var s = File.OpenRead(typeof(Program).Assembly.Location);
            using var pe = new System.Reflection.PortableExecutable.PEReader(s);
            return pe.PEHeaders.CorHeader?.ManagedNativeHeaderDirectory.Size > 0;
        }
        catch (Exception) { return false; }
    }
}

internal sealed record Options
{
    public string? Log { get; init; }
    public string? Control { get; init; }
    public string Tray { get; init; } = "shell";
    public string? Render { get; init; }
    public string? Theme { get; init; }
    public int Page { get; init; }
    public bool Hidden { get; init; }
    public bool ExitAfterFirstFrame { get; init; }
    public bool TrayHost { get; init; }
    public bool UiChild { get; init; }
    public bool IdleTrim { get; init; }
    public int LingerMs { get; init; }

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            string? Next() => i + 1 < a.Length ? a[++i] : null;
            o = a[i] switch
            {
                "--log" => o with { Log = Next() },
                "--control" => o with { Control = Next() },
                "--tray" => o with { Tray = Next() ?? "shell" },
                "--render" => o with { Render = Next() },
                "--theme" => o with { Theme = Next() },
                "--page" => o with { Page = int.TryParse(Next(), out var p) ? p : 0 },
                "--hidden" => o with { Hidden = true },
                "--exit-after-first-frame" => o with { ExitAfterFirstFrame = true },
                "--tray-host" => o with { TrayHost = true },
                "--ui-child" => o with { UiChild = true, Tray = "none" },
                "--idle-trim" => o with { IdleTrim = true },
                "--linger-ms" => o with { LingerMs = int.TryParse(Next(), out var lm) ? lm : 0 },
                _ => o,
            };
        }
        return o;
    }
}
