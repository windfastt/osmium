using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace UiEval;

internal static partial class Program
{
    private static JsonObject Env(Config cfg)
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] fontFiles = ["SegUIVar.ttf", "segoeui.ttf", "seguisb.ttf", "segoeuib.ttf", "msyh.ttc", "YuGothR.ttc", "msgothic.ttc", "malgun.ttf", "simsun.ttc", "meiryo.ttc"];
        var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var push = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
        var hc = Win.GetHighContrast();
        var r = new JsonObject
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["osVersion"] = Environment.OSVersion.VersionString,
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["osArch"] = RuntimeInformation.OSArchitecture.ToString(),
            ["cpus"] = Environment.ProcessorCount,
            ["imageOS"] = Environment.GetEnvironmentVariable("ImageOS"),
            ["imageVersion"] = Environment.GetEnvironmentVariable("ImageVersion"),
            ["runner"] = Environment.GetEnvironmentVariable("RUNNER_NAME"),
            ["session"] = Process.GetCurrentProcess().SessionId,
            ["inputDesktop"] = Win.InputDesktopName(),
            ["explorer"] = Process.GetProcessesByName("explorer").Length,
            ["taskbar"] = Win.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null) != IntPtr.Zero,
            ["notifyArea"] = Win.FindWindowEx(Win.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null), IntPtr.Zero, "TrayNotifyWnd", null) != IntPtr.Zero,
            ["explorerPolicyNoTray"] = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer")?.GetValue("NoTrayItemsDisplay")?.ToString(),
            ["screen"] = $"{Win.GetSystemMetrics(0)}x{Win.GetSystemMetrics(1)}",
            ["virtualScreen"] = $"{Win.GetSystemMetrics(78)}x{Win.GetSystemMetrics(79)}",
            ["monitors"] = Win.GetSystemMetrics(80),
            ["appsUseLightTheme"] = personalize?.GetValue("AppsUseLightTheme")?.ToString(),
            ["systemUsesLightTheme"] = personalize?.GetValue("SystemUsesLightTheme")?.ToString(),
            ["toastEnabled"] = push?.GetValue("ToastEnabled")?.ToString(),
            ["highContrast"] = hc.On,
            ["highContrastScheme"] = hc.Scheme,
            ["fonts"] = new JsonObject(fontFiles.Select(f => KeyValuePair.Create(f, (JsonNode?)JsonValue.Create(File.Exists(Path.Combine(fonts, f)))))),
            ["protoExists"] = File.Exists(cfg.Proto),
            ["winformsExists"] = File.Exists(cfg.WinForms),
            ["refFiles"] = Arr(Directory.Exists(cfg.Ref) ? Directory.GetFiles(cfg.Ref).Select(Path.GetFileName) : []),
        };
        r["cpu"] = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("ProcessorNameString")?.ToString()?.Trim();
        r["cpuBusyPercent"] = CpuBusy(2000);
        r["workloadMs"] = Workload();
        r["visibleWindows"] = Arr(Win.VisibleWindows());
        try { r["uiaRoot"] = Uia.Name(System.Windows.Automation.AutomationElement.RootElement); }
        catch (Exception e) { r["uiaRoot"] = "error " + e.Message; }
        return r;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    /// <summary>Share of all CPUs busy over an interval (other work on the runner while the harness measures).</summary>
    internal static double CpuBusy(int ms)
    {
        GetSystemTimes(out var i0, out var k0, out var u0);
        Thread.Sleep(ms);
        GetSystemTimes(out var i1, out var k1, out var u1);
        double total = (k1 - k0) + (u1 - u0), idle = i1 - i0;
        return total <= 0 ? double.NaN : Math.Round(100 * (1 - idle / total), 1);
    }

    /// <summary>A fixed single-thread workload (SHA-256 of 64 MiB), to compare runner speed between runs.</summary>
    internal static double Workload()
    {
        var data = new byte[64 << 20];
        new Random(1).NextBytes(data);
        var sw = Stopwatch.StartNew();
        System.Security.Cryptography.SHA256.HashData(data);
        return Math.Round(sw.Elapsed.TotalMilliseconds, 1);
    }

    /// <summary>A display mode big enough for the 1040 x 760 window plus a drop target beside it (runners start at 1024 x 768).</summary>
    private static JsonObject Display()
    {
        var before = Win.CurrentMode();
        var modes = Win.DisplayModes();
        var r = new JsonObject
        {
            ["before"] = $"{before.W}x{before.H}",
            ["modes"] = Arr(modes.Select(m => $"{m.W}x{m.H}@{m.Bpp}bpp/{m.Hz}Hz").Distinct().TakeLast(40)),
        };
        var sizes = modes.Select(m => (m.W, m.H)).Distinct().ToList();
        (int W, int H)? want = sizes.Contains((1920, 1080)) ? (1920, 1080)
            : sizes.Where(s => s.W <= 2560 && s.W >= 1280 && s.H >= 900).OrderByDescending(s => s.W * s.H).Cast<(int, int)?>().FirstOrDefault();
        if (want is { } w && (w.W != before.W || w.H != before.H))
        {
            int code = Win.SetMode(w.W, w.H);
            Thread.Sleep(2000);
            r["set"] = $"{w.W}x{w.H}";
            r["setResult"] = code;
        }
        var after = Win.CurrentMode();
        r["after"] = $"{after.W}x{after.H}";
        r["screen"] = $"{Win.GetSystemMetrics(0)}x{Win.GetSystemMetrics(1)}";
        if (Win.GetScale() is { } s) r["scale"] = $"current {s.Current} %, min {s.Min} %, max {s.Max} %, recommended {s.Recommended} %";
        r["pass"] = after.W >= 1280 && after.H >= 800;
        return r;
    }

    /// <summary>Launches without a crash and time to the first frame, interleaved with the WinForms app.</summary>
    private static JsonObject Startup(Config cfg)
    {
        var wfPainted = new List<double>();
        var wfVisible = new List<double>();
        var avFrame = new List<double>();
        var avVisible = new List<double>();
        var avValidated = new List<double>();
        var runs = new JsonArray();
        int avCrashes = 0, wfCrashes = 0;
        for (int i = 1; i <= cfg.Runs; i++)
        {
            foreach (var kind in i % 2 == 1 ? new[] { "winforms", "avalonia" } : ["avalonia", "winforms"])
            {
                Apps.KillAll();
                Thread.Sleep(1500);
                var rec = new JsonObject { ["run"] = i, ["kind"] = kind };
                if (kind == "winforms")
                {
                    using var run = StartWinForms(cfg);
                    var (vis, painted) = WatchPaint(run, 30_000);
                    rec["visibleMs"] = vis;
                    rec["paintedMs"] = painted;
                    if (painted is double p) wfPainted.Add(p);
                    if (vis is double v) wfVisible.Add(v);
                    Thread.Sleep(500);
                    var end = run.Quit(cfg.WinForms);
                    rec["end"] = end;
                    bool crash = painted is null || !end.StartsWith("exited, code 0", StringComparison.Ordinal);
                    if (crash) wfCrashes++;
                    rec["crash"] = crash;
                }
                else
                {
                    using var run = StartProto(cfg, $"startup-{i}");
                    var (vis, validated) = WatchPaint(run, 30_000);
                    var ff = run.WaitEvent("first-frame", 30_000);
                    double? frame = ff?["ms"]?.GetValue<double>();
                    rec["visibleMs"] = vis;
                    rec["validatedMs"] = validated;
                    rec["firstFrameMs"] = frame;
                    rec["sinceMainMs"] = ff?["sinceMainMs"]?.GetValue<double>();
                    rec["gpuInterop"] = ff?["gpuInterop"]?.GetValue<bool>();
                    rec["scaling"] = ff?["scaling"]?.GetValue<double>();
                    if (frame is double f) avFrame.Add(f);
                    if (vis is double v) avVisible.Add(v);
                    if (validated is double va) avValidated.Add(va);
                    Thread.Sleep(500);
                    var end = run.Quit();
                    rec["end"] = end;
                    var fatal = run.Events().Where(e => e["ev"]?.GetValue<string>() is "fatal" or "control-error").Select(e => e.ToJsonString()).ToArray();
                    var warnings = run.Events().Where(e => e["ev"]?.GetValue<string>() == "avalonia-log").Select(e => e.ToJsonString()).Take(10).ToArray();
                    rec["fatal"] = Arr(fatal);
                    if (i == 1) rec["avaloniaLog"] = Arr(warnings);
                    bool crash = frame is null || fatal.Length > 0 || !end.StartsWith("exited, code 0", StringComparison.Ordinal);
                    if (crash) avCrashes++;
                    rec["crash"] = crash;
                }
                Log($"startup run {i} {kind}: {rec.ToJsonString()}");
                runs.Add(rec);
            }
        }
        double wfMed = Median(wfPainted), avMed = Median(avFrame);
        double ratio = avMed / wfMed;
        return new JsonObject
        {
            ["runs"] = runs,
            ["winformsPaintedMs"] = Arr(wfPainted.Select(x => Math.Round(x))),
            ["winformsVisibleMs"] = Arr(wfVisible.Select(x => Math.Round(x))),
            ["avaloniaFirstFrameMs"] = Arr(avFrame.Select(x => Math.Round(x))),
            ["avaloniaVisibleMs"] = Arr(avVisible.Select(x => Math.Round(x))),
            ["avaloniaValidatedMs"] = Arr(avValidated.Select(x => Math.Round(x))),
            ["winformsMedianMs"] = Math.Round(wfMed),
            ["avaloniaMedianMs"] = Math.Round(avMed),
            ["ratio"] = Math.Round(ratio, 2),
            ["avaloniaLaunches"] = avFrame.Count,
            ["avaloniaCrashes"] = avCrashes,
            ["winformsCrashes"] = wfCrashes,
            ["launchPass"] = avCrashes == 0 && avFrame.Count == cfg.Runs,
            ["firstFramePass"] = avMed <= 1000 && ratio <= 1.5,
            ["pass"] = avCrashes == 0 && avFrame.Count == cfg.Runs && avMed <= 1000 && ratio <= 1.5,
            ["method"] = "WinForms: process creation to the main window visible with no pending update region in it or any child " +
                         "(GetUpdateRect, polled every 2 ms). Preview: process creation to the compositor's first rendered batch after Opened " +
                         "(in-process, same kernel creation time). Interleaved, alternating order.",
        };
    }

    /// <summary>Polls for the process's main window, then until it and its children have no update region.</summary>
    private static (double? Visible, double? Painted) WatchPaint(AppRun run, int timeoutMs)
    {
        double? visible = null, painted = null;
        var sw = Stopwatch.StartNew();
        IntPtr h = IntPtr.Zero;
        while (sw.ElapsedMilliseconds < timeoutMs && !run.Process.HasExited)
        {
            if (h == IntPtr.Zero)
            {
                h = Win.MainWindow(run.Pid);
                if (h != IntPtr.Zero) visible = run.SinceStartMs;
            }
            if (h != IntPtr.Zero && Win.FullyPainted(h)) { painted = run.SinceStartMs; break; }
            Thread.Sleep(2);
        }
        return (visible, painted);
    }

    /// <summary>First frame cross-check, measured the same way for both apps: screen pixels of the window until they match the
    /// settled window (98 % of pixels within 8 levels), captured from the screen every frame.</summary>
    private static JsonObject VisualStartup(Config cfg)
    {
        var res = new JsonObject();
        var wf = new List<double>();
        var av = new List<double>();
        var all = new JsonArray();
        for (int i = 1; i <= 3; i++)
        {
            foreach (var kind in new[] { "winforms", "avalonia" })
            {
                Apps.KillAll();
                Thread.Sleep(1500);
                using var run = kind == "winforms" ? StartWinForms(cfg) : StartProto(cfg, $"visual-{i}");
                var frames = new List<(double T, Img Img)>();
                var sw = Stopwatch.StartNew();
                IntPtr h = IntPtr.Zero;
                while (sw.ElapsedMilliseconds < 20_000 && !run.Process.HasExited)
                {
                    if (h == IntPtr.Zero) h = Win.MainWindow(run.Pid);
                    if (h != IntPtr.Zero)
                    {
                        var r = Win.ClientScreenRect(h);
                        var img = Img.Capture(h, r, printWindow: false);
                        if (img is not null) frames.Add((run.SinceStartMs, Downsample(img)));
                        if (frames.Count > 0 && run.SinceStartMs - frames[0].T > 2500) break;
                    }
                    else Thread.Sleep(2);
                }
                Thread.Sleep(1000);
                Img? final = h == IntPtr.Zero ? null : Img.Capture(h, Win.ClientScreenRect(h), false);
                double? complete = null, nonBlank = null;
                if (final is not null)
                {
                    var f = Downsample(final);
                    final.Save(Path.Combine(_shots, $"startup-final-{kind}-{i}.png"));
                    foreach (var (t, img) in frames)
                    {
                        if (nonBlank is null && !img.IsBlank()) nonBlank = t;
                        if (img.SameShare(f, 8) >= 0.98) { complete = t; break; }
                    }
                }
                if (complete is double c) (kind == "winforms" ? wf : av).Add(c);
                var rec = new JsonObject { ["kind"] = kind, ["run"] = i, ["frames"] = frames.Count, ["firstNonBlankMs"] = nonBlank, ["visuallyCompleteMs"] = complete };
                all.Add(rec);
                Log($"visual startup {kind} {i}: {rec.ToJsonString()}");
                run.Quit(kind == "winforms" ? cfg.WinForms : null);
            }
        }
        res["runs"] = all;
        res["winformsMedianMs"] = Math.Round(Median(wf));
        res["avaloniaMedianMs"] = Math.Round(Median(av));
        res["ratio"] = Math.Round(Median(av) / Median(wf), 2);
        res["pass"] = av.Count == 3 && wf.Count == 3 ? Median(av) <= 1000 && Median(av) / Median(wf) <= 1.5 : null;
        res["method"] = "screen capture (BitBlt) of the client area every frame from process creation; time of the first frame within 8 levels on 98 % of pixels of the settled window. Needs an interactive desktop.";
        return res;
    }

    private static Img Downsample(Img s)
    {
        int w = Math.Max(1, s.W / 4), h = Math.Max(1, s.H / 4);
        var px = new int[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = s.Px[(y * 4) * s.W + x * 4];
        return new Img(w, h, px);
    }

    private static JsonObject TrimmedStartup(Config cfg)
    {
        var frames = new List<double>();
        var all = new JsonArray();
        for (int i = 1; i <= 3; i++)
        {
            Apps.KillAll();
            Thread.Sleep(1000);
            using var run = StartProto(cfg, $"trimmed-{i}", exe: cfg.ProtoTrimmed);
            var ff = run.WaitEvent("first-frame", 30_000);
            double? f = ff?["ms"]?.GetValue<double>();
            if (f is double x) frames.Add(x);
            var end = run.Quit();
            all.Add(new JsonObject { ["run"] = i, ["firstFrameMs"] = f, ["end"] = end, ["events"] = run.Events().Count });
        }
        return new JsonObject { ["runs"] = all, ["medianMs"] = Math.Round(Median(frames)), ["pass"] = frames.Count == 3 };
    }
}
