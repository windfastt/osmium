using System.Diagnostics;
using System.Text.Json.Nodes;

namespace UiEval;

/// <summary>
/// Where the preview's startup time and idle memory go, and what the options cost: in-process trimming of caches,
/// software rendering, trimmed and composite ReadyToRun builds, starting hidden, and the two-process layout (a tray
/// host without a UI framework that starts the window as its own process).
/// </summary>
internal static partial class Program
{
    private const uint WM_SYSCOMMAND = 0x0112;
    private static readonly IntPtr SC_CLOSE = 0xF060;

    /// <summary>The window's close button (WM_SYSCOMMAND SC_CLOSE), as the user presses it. WinForms hides its form on
    /// this; a bare WM_CLOSE would make it dispose the form instead (CloseReason.TaskManagerClosing).</summary>
    private static void UserClose(IntPtr h) => Win.PostMessage(h, WM_SYSCOMMAND, SC_CLOSE, IntPtr.Zero);

    /// <summary>A rounded number, or null when there was nothing to take a median of.</summary>
    private static JsonNode? R(double d) => double.IsFinite(d) ? JsonValue.Create(Math.Round(d)) : null;

    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static double IdlePrivate(AppRun run, JsonObject rec, string prefix)
    {
        rec[prefix + "Gc"] = run.Hook("gc");
        Thread.Sleep(2000);
        var samples = new List<double>();
        for (int s = 0; s < 3; s++) { samples.Add(run.PrivateBytes()); Thread.Sleep(500); }
        double idle = Median(samples);
        rec[prefix + "MiB"] = Math.Round(idle / 1048576, 1);
        return idle;
    }

    /// <summary>Idle memory and its options. Every variant: window open 3 s, the user's close button, 3 s, a full GC through
    /// the startup hook (both apps), median of three private-bytes samples; then reopen twice and measure again.</summary>
    private static JsonObject Memory(Config cfg)
    {
        var res = new JsonObject();
        var all = new JsonArray();
        var wf = new List<double>();
        var av = new List<double>();
        for (int i = 1; i <= 3; i++) { var r = MemWinForms(cfg, i, userClose: true); wf.Add(Num(r["idleMiB"]) ?? double.NaN); all.Add(r); }
        all.Add(MemWinForms(cfg, 1, userClose: false));
        for (int i = 1; i <= 3; i++) { var r = MemProto(cfg, "avalonia", "", null, i, reopen: i == 1); av.Add(Num(r["idleMiB"]) ?? double.NaN); all.Add(r); }
        all.Add(MemProto(cfg, "avalonia-idle-trim", "--idle-trim", null, 1, reopen: true));
        all.Add(MemProto(cfg, "avalonia-software", "--render software", null, 1, reopen: true));
        all.Add(MemProto(cfg, "avalonia-software-idle-trim", "--render software --idle-trim", null, 1, reopen: true));
        all.Add(MemProto(cfg, "avalonia-trayicon", "--tray avalonia", null, 1, reopen: false));
        if (cfg.ProtoTrimmed is not null) all.Add(MemProto(cfg, "avalonia-trimmed", "", cfg.ProtoTrimmed, 1, reopen: true));
        if (cfg.ProtoComposite is not null) all.Add(MemProto(cfg, "avalonia-composite", "", cfg.ProtoComposite, 1, reopen: true));
        if (cfg.ProtoAot is not null)
        {
            all.Add(MemProto(cfg, "avalonia-native-aot", "", cfg.ProtoAot, 1, reopen: true));
            all.Add(MemProto(cfg, "avalonia-native-aot-software", "--render software", cfg.ProtoAot, 1, reopen: true));
        }
        all.Add(MemHidden(cfg));
        for (int i = 1; i <= 2; i++) all.Add(MemHost(cfg, i, "tray-host", "", null));
        if (cfg.ProtoComposite is not null) all.Add(MemHost(cfg, 1, "tray-host-composite", "", cfg.ProtoComposite));
        all.Add(MemHost(cfg, 1, "tray-host-linger-20s", "--linger-ms 20000", cfg.ProtoComposite));
        if (cfg.ProtoAot is not null)
        {
            all.Add(MemHost(cfg, 1, "tray-host-native-aot", "", cfg.ProtoAot));
            all.Add(MemHost(cfg, 1, "tray-host-native-aot-linger-20s", "--linger-ms 20000", cfg.ProtoAot));
        }
        double wfMed = Median(wf), avMed = Median(av);
        res["runs"] = all;
        res["winformsIdleMiB"] = Math.Round(wfMed, 1);
        res["avaloniaIdleMiB"] = Math.Round(avMed, 1);
        res["ratio"] = Math.Round(avMed / wfMed, 2);
        res["pass"] = avMed / wfMed <= 1.5;
        var table = new JsonArray();
        foreach (var r in all.OfType<JsonObject>())
            table.Add(new JsonObject
            {
                ["kind"] = r["kind"]?.DeepClone(), ["run"] = r["run"]?.DeepClone(), ["openMiB"] = r["openMiB"]?.DeepClone(),
                ["idleMiB"] = r["idleMiB"]?.DeepClone(), ["ratioToWinForms"] = Num(r["idleMiB"]) is double d ? Math.Round(d / wfMed, 2) : null,
                ["reopenMs"] = r["reopenMs"]?.DeepClone(), ["idleAfterReopenMiB"] = r["idleAfterReopenMiB"]?.DeepClone(),
                ["breakdown"] = r["idleBreakdown"]?.DeepClone(),
            });
        res["table"] = table;
        res["method"] = "window shown 3 s, the close button (SC_CLOSE), 3 s, a full blocking compacting GC through a startup hook loaded into every process, 2 s, median of 3 private-bytes samples. Reopen: the tray's open command, timed to the next rendered frame.";
        return res;
    }

    private static JsonObject MemWinForms(Config cfg, int i, bool userClose)
    {
        Apps.KillAll();
        Thread.Sleep(1500);
        var rec = new JsonObject { ["kind"] = userClose ? "winforms" : "winforms-wm-close", ["run"] = i };
        using var run = StartWinForms(cfg, hook: true);
        WatchPaint(run, 30_000);
        var h = Win.MainWindow(run.Pid);
        Thread.Sleep(3000);
        rec["openMiB"] = Math.Round(run.PrivateBytes() / 1048576.0, 1);
        if (userClose) UserClose(h); else Win.PostMessage(h, Win.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(3000);
        rec["windowAfterClose"] = Win.IsWindow(h) ? (Win.IsWindowVisible(h) ? "visible" : "hidden (kept)") : "destroyed";
        IdlePrivate(run, rec, "idle");
        rec["idleHook"] = run.Hook("mem");
        if (i == 1 && userClose)
        {
            // Reopen the way Explorer or a second start does: a new Osmium.App process forwards "show" over the pipe.
            var reopen = new JsonArray();
            for (int k = 0; k < 2; k++)
            {
                var sw = Stopwatch.StartNew();
                using var fwd = Process.Start(new ProcessStartInfo(cfg.WinForms) { UseShellExecute = false })!;
                double? ms = null;
                while (sw.ElapsedMilliseconds < 15_000)
                {
                    if (Win.IsWindowVisible(h) && Win.FullyPainted(h)) { ms = sw.Elapsed.TotalMilliseconds; break; }
                    Thread.Sleep(2);
                }
                reopen.Add(ms is double d ? Math.Round(d) : null);
                Thread.Sleep(1500);
                UserClose(h);
                Thread.Sleep(2000);
            }
            rec["reopenMs"] = reopen;
            rec["reopenNote"] = "a second Osmium.App process forwards 'show' to the running one (process start included)";
            IdlePrivate(run, rec, "idleAfterReopen");
        }
        rec["end"] = run.Quit(cfg.WinForms);
        Log($"memory {rec["kind"]} {i}: {rec.ToJsonString()}");
        return rec;
    }

    private static JsonObject MemProto(Config cfg, string kind, string extra, string? exe, int i, bool reopen)
    {
        Apps.KillAll();
        Thread.Sleep(1500);
        var rec = new JsonObject { ["kind"] = kind, ["run"] = i, ["args"] = extra };
        using var run = StartProto(cfg, $"mem-{kind}-{i}", extra, hook: true, exe: exe);
        var h = ReadyProto(run);
        Thread.Sleep(3000);
        rec["openMiB"] = Math.Round(run.PrivateBytes() / 1048576.0, 1);
        rec["openBreakdown"] = run.Command("memory-report")?["report"]?.DeepClone();
        UserClose(h);
        Thread.Sleep(3000);
        rec["windowAfterClose"] = Win.IsWindow(h) ? (Win.IsWindowVisible(h) ? "visible" : "hidden (kept)") : "destroyed";
        rec["idleTrimEvent"] = run.Events().LastOrDefault(e => e["ev"]?.GetValue<string>() == "idle-trim")?.DeepClone();
        IdlePrivate(run, rec, "idle");
        rec["idleBreakdown"] = run.Command("memory-report")?["report"]?.DeepClone();
        if (reopen)
        {
            var times = new JsonArray();
            for (int k = 0; k < 2; k++)
            {
                int n0 = run.EventCount;
                run.Command("open-window");
                var ev = run.WaitEvent("frame-after-reopen", 15_000, after: n0);
                var req = run.Events().Skip(n0).FirstOrDefault(e => e["ev"]?.GetValue<string>() == "open-requested");
                times.Add(Num(ev?["utc"]) is double b && Num(req?["utc"]) is double a ? Math.Round(b - a) : null);
                var h2 = run.WaitMainWindow(5000);
                Thread.Sleep(1500);
                UserClose(h2);
                Thread.Sleep(2500);
            }
            rec["reopenMs"] = times;
            IdlePrivate(run, rec, "idleAfterReopen");
        }
        rec["end"] = run.Quit();
        Log($"memory {kind} {i}: {rec.ToJsonString()}");
        return rec;
    }

    /// <summary>Started into the notification area (--hidden): idle before any window, then the first open's latency.</summary>
    private static JsonObject MemHidden(Config cfg)
    {
        Apps.KillAll();
        Thread.Sleep(1500);
        var rec = new JsonObject { ["kind"] = "avalonia-started-hidden", ["run"] = 1 };
        using var run = StartProto(cfg, "mem-hidden", "--hidden", hook: true);
        run.WaitEvent("started", 30_000);
        Thread.Sleep(3000);
        IdlePrivate(run, rec, "idleBeforeFirstOpen");
        rec["idleMiB"] = rec["idleBeforeFirstOpenMiB"]?.DeepClone();
        int n0 = run.EventCount;
        run.Command("open-window");
        var ff = run.WaitEvent("first-frame", 20_000, after: n0);
        rec["firstOpenMs"] = Num(ff?["sinceRequestMs"]) is double d ? Math.Round(d) : null;
        rec["firstOpenEvent"] = ff?.DeepClone();
        var h = run.WaitMainWindow(5000);
        Thread.Sleep(1500);
        UserClose(h);
        Thread.Sleep(3000);
        IdlePrivate(run, rec, "idleAfterClose");
        rec["end"] = run.Quit();
        Log($"memory hidden: {rec.ToJsonString()}");
        return rec;
    }

    /// <summary>The two-process layout: the tray host idles without a UI framework; opening starts the window process,
    /// which exits when its window closes.</summary>
    private static JsonObject MemHost(Config cfg, int i, string kind, string extra, string? exe)
    {
        Apps.KillAll();
        Thread.Sleep(1500);
        var rec = new JsonObject { ["kind"] = kind, ["run"] = i, ["args"] = extra };
        bool linger = extra.Contains("--linger-ms", StringComparison.Ordinal);
        using var host = StartProto(cfg, $"mem-{kind}-{i}", $"--tray-host {extra}".Trim(), hook: true, exe: exe);
        var started = host.WaitEvent("started", 20_000);
        rec["hostStart"] = started is null ? null : Num(started["ms"]);
        rec["trayAdded"] = host.WaitEvent("tray-added", 30_000, e => e["ok"]?.GetValue<bool>() == true)?.DeepClone();
        rec["assembliesAtStart"] = host.Events().FirstOrDefault(e => e["ev"]?.GetValue<string>() == "host-start")?["assemblies"]?.DeepClone();
        Thread.Sleep(3000);
        rec["openMiB"] = Math.Round(host.PrivateBytes() / 1048576.0, 1);
        IdlePrivate(host, rec, "idle");
        rec["idleBreakdown"] = host.Command("memory-report")?["report"]?.DeepClone();
        var opens = new JsonArray();
        for (int k = 0; k < 2; k++)
        {
            var o = new JsonObject();
            int n0 = host.EventCount;
            var reply = host.Command("open-window");
            o["reply"] = reply?.DeepClone();
            var req = host.WaitEvent("open-requested", 5000, after: n0);
            string? childLog = reply?["childLog"]?.GetValue<string>();
            int? childPid = reply?["childPid"] is JsonValue pv && pv.TryGetValue<int>(out var cp) ? cp : null;
            if (childLog is null || childPid is null) { o["error"] = "no window process"; opens.Add(o); continue; }
            bool reused = reply?["reused"]?.GetValue<bool>() == true;
            o["reusedLingeringWindowProcess"] = reused;
            using var child = AppRun.Attach("avalonia", childPid.Value, childLog, reply?["childPipe"]?.GetValue<string>());
            var ff = reused
                ? child.WaitEvent("frame-after-reopen", 20_000, e => Num(e["utc"]) >= Num(req?["utc"]))
                : child.WaitEvent("first-frame", 20_000);
            double? reqUtc = Num(req?["utc"]), ffUtc = Num(ff?["utc"]);
            o["openToFirstFrameMs"] = reqUtc is double a && ffUtc is double b ? Math.Round(b - a) : null;
            o["childFirstFrameSinceStartMs"] = Num(ff?["ms"]);
            Thread.Sleep(2000);
            o["withWindowHostMiB"] = Math.Round(host.PrivateBytes() / 1048576.0, 1);
            o["withWindowChildMiB"] = Math.Round(child.PrivateBytes() / 1048576.0, 1);
            var h = child.WaitMainWindow(5000);
            UserClose(h);
            if (linger)
            {
                Thread.Sleep(4000);
                o["lingeringChildMiB"] = Math.Round(child.PrivateBytes() / 1048576.0, 1);
                o["lingeringTotalMiB"] = Math.Round((child.PrivateBytes() + host.PrivateBytes()) / 1048576.0, 1);
                if (k == 1) o["childExitedAfterLinger"] = child.Process.WaitForExit(30_000);
            }
            else o["childExited"] = child.Process.WaitForExit(10_000);
            opens.Add(o);
            Thread.Sleep(2000);
        }
        rec["opens"] = opens;
        rec["reopenMs"] = new JsonArray([.. opens.OfType<JsonObject>().Select(o => o["openToFirstFrameMs"]?.DeepClone())]);
        IdlePrivate(host, rec, "idleAfterReopen");
        rec["end"] = host.Quit();
        Log($"memory host {i}: {rec.ToJsonString()}");
        return rec;
    }

    private static readonly string[] Stages =
    [
        "main", "ui-init", "builder", "platform-ready", "app-initialize-start", "app-initialize-done", "setup-done", "tray-added",
        "open-requested", "window-ctor-start", "window-xaml-done", "window-ctor-done", "show-called", "opened", "first-frame",
    ];

    /// <summary>First frame in detail: where the first frame's time goes (stage marks in the log) and what the build and
    /// start-up options change; plus the warm open (process already in the notification area) for both apps.</summary>
    private static JsonObject StartupBreakdown(Config cfg)
    {
        var res = new JsonObject();
        var variants = new List<(string Name, string Args, string? Exe)>
        {
            ("r2r", "", null), ("r2r-software-rendering", "--render software", null),
        };
        if (cfg.ProtoTrimmed is not null) variants.Add(("trimmed-r2r", "", cfg.ProtoTrimmed));
        if (cfg.ProtoComposite is not null)
        {
            variants.Add(("composite-r2r", "", cfg.ProtoComposite));
            variants.Add(("composite-r2r-software-rendering", "--render software", cfg.ProtoComposite));
        }
        if (cfg.ProtoAot is not null)
        {
            variants.Add(("native-aot", "", cfg.ProtoAot));
            variants.Add(("native-aot-software-rendering", "--render software", cfg.ProtoAot));
        }
        var summary = new JsonArray();
        foreach (var (name, args, exe) in variants)
        {
            var stageTimes = Stages.ToDictionary(s => s, _ => new List<double>());
            var jit = new List<double>();
            var jitMethods = new List<double>();
            for (int i = 1; i <= 5; i++)
            {
                Apps.KillAll();
                Thread.Sleep(1200);
                using var run = StartProto(cfg, $"breakdown-{name}-{i}", args, exe: exe);
                var ff = run.WaitEvent("first-frame", 30_000);
                var evs = run.Events();
                foreach (var s in Stages)
                    if (evs.FirstOrDefault(e => e["ev"]?.GetValue<string>() == s) is { } e && Num(e["ms"]) is double ms) stageTimes[s].Add(ms);
                if (Num(ff?["jit"]?["ms"]) is double j) jit.Add(j);
                if (Num(ff?["jit"]?["methods"]) is double jm) jitMethods.Add(jm);
                run.Quit();
            }
            var stages = new JsonObject();
            foreach (var s in Stages) if (stageTimes[s].Count > 0) stages[s] = Math.Round(Median(stageTimes[s]));
            summary.Add(new JsonObject
            {
                ["variant"] = name, ["firstFrameMedianMs"] = stages["first-frame"]?.DeepClone(), ["stagesMedianMs"] = stages,
                ["firstFrames"] = Arr(stageTimes["first-frame"].Select(x => Math.Round(x))),
                ["jitMsMedian"] = R(Median(jit)), ["jitMethodsMedian"] = R(Median(jitMethods)),
            });
            Log($"breakdown {name}: {summary[^1]!.ToJsonString()}");
        }
        res["variants"] = summary;

        // Warm open: the process is already in the notification area; time from the open command to the first frame.
        var warmAv = new List<double>();
        for (int i = 1; i <= 5; i++)
        {
            Apps.KillAll();
            Thread.Sleep(1200);
            using var run = StartProto(cfg, $"warm-open-{i}", "--hidden");
            run.WaitEvent("started", 30_000);
            Thread.Sleep(2000);
            int n0 = run.EventCount;
            run.Command("open-window");
            var ff = run.WaitEvent("first-frame", 20_000, after: n0);
            if (Num(ff?["sinceRequestMs"]) is double d) warmAv.Add(d);
            run.Quit();
        }
        var warmWf = new List<double>();
        for (int i = 1; i <= 5; i++)
        {
            Apps.KillAll();
            Thread.Sleep(1200);
            using var run = Apps.Start("winforms", cfg.WinForms, "--tray", null, null);
            Thread.Sleep(3000);
            var sw = Stopwatch.StartNew();
            using var fwd = Process.Start(new ProcessStartInfo(cfg.WinForms) { UseShellExecute = false })!;
            IntPtr h = IntPtr.Zero;
            while (sw.ElapsedMilliseconds < 15_000)
            {
                if (h == IntPtr.Zero) h = Win.MainWindow(run.Pid);
                if (h != IntPtr.Zero && Win.FullyPainted(h)) { warmWf.Add(sw.Elapsed.TotalMilliseconds); break; }
                Thread.Sleep(2);
            }
            run.Quit(cfg.WinForms);
        }
        res["warmOpenAvaloniaMs"] = Arr(warmAv.Select(x => Math.Round(x)));
        res["warmOpenAvaloniaMedianMs"] = R(Median(warmAv));
        res["warmOpenWinFormsMs"] = Arr(warmWf.Select(x => Math.Round(x)));
        res["warmOpenWinFormsMedianMs"] = R(Median(warmWf));
        res["winformsFirstPaintMedianMs"] = Results["startup"]?["winformsMedianMs"]?.DeepClone();
        res["cpuBusyPercentAfter"] = CpuBusy(1000);
        res["warmOpenNote"] = "Avalonia: the open command to the first rendered frame inside the running tray process (window built for the first time). " +
                              "WinForms: start of a second Osmium.App process (it forwards 'show') to the main form painted, process start included.";
        res["pass"] = null;
        return res;
    }
}
