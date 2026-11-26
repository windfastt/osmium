using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UiEval;

/// <summary>
/// The UI evaluation on a Windows runner. Usage:
///   UiEval.Harness all --proto DIR --wf DIR --hook DLL --ref DIR --out DIR [--proto-trimmed DIR] [--runs 7]
///   UiEval.Harness sizes --out FILE NAME=DIR [NAME=DIR ...]
/// Every step logs what it did and what it saw; results.json holds the values and the verdicts.
/// </summary>
internal static partial class Program
{
    private static readonly JsonObject Results = new();
    private static StreamWriter? _log;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string _out = ".";
    private static string _shots = ".";
    private static string _logs = ".";

    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 0) { Console.WriteLine("usage: UiEval.Harness all|sizes ..."); return 2; }
        var opt = ParseOptions(args.Skip(1).ToArray());
        if (args[0] == "sizes") return Sizes.Run(opt);
        _out = Path.GetFullPath(opt.GetValueOrDefault("out") ?? "w0-out");
        _shots = Path.Combine(_out, "shots");
        _logs = Path.Combine(_out, "logs");
        Directory.CreateDirectory(_shots);
        Directory.CreateDirectory(_logs);
        _log = new StreamWriter(Path.Combine(_out, "harness.log"), append: true) { AutoFlush = true };
        var cfg = new Config
        {
            Proto = Path.Combine(Path.GetFullPath(opt["proto"]), "OsmiumUiPreview.exe"),
            ProtoTrimmed = opt.TryGetValue("proto-trimmed", out var pt) ? Path.Combine(Path.GetFullPath(pt), "OsmiumUiPreview.exe") : null,
            ProtoComposite = opt.TryGetValue("proto-composite", out var pc) ? Path.Combine(Path.GetFullPath(pc), "OsmiumUiPreview.exe") : null,
            ProtoAot = opt.TryGetValue("proto-aot", out var pa) ? Path.Combine(Path.GetFullPath(pa), "OsmiumUiPreview.exe") : null,
            WinForms = Path.Combine(Path.GetFullPath(opt["wf"]), "Osmium.App.exe"),
            Hook = Path.GetFullPath(opt["hook"]),
            Ref = Path.GetFullPath(opt["ref"]),
            Runs = int.Parse(opt.GetValueOrDefault("runs") ?? "7", CultureInfo.InvariantCulture),
            Only = opt.GetValueOrDefault("only")?.Split(',').ToHashSet(),
        };
        Log($"UI evaluation harness: proto={cfg.Proto} wf={cfg.WinForms} hook={cfg.Hook} ref={cfg.Ref} runs={cfg.Runs}");
        Apps.KillAll();

        Step("env", () => Env(cfg));
        Step("display", () => Display());
        Step("startup", () => Startup(cfg));
        Step("startup_visual", () => VisualStartup(cfg));
        Step("startup_breakdown", () => StartupBreakdown(cfg));
        Step("memory", () => Memory(cfg));
        Step("tray_shell", () => Tray(cfg, "shell"));
        Step("tray_avalonia", () => Tray(cfg, "avalonia"));
        Step("tray_host", () => TrayHostMenu(cfg));
        Step("uia_peers", () => UiaPeers(cfg));
        Step("archives", () => Archives(cfg));
        Step("capture", () => CaptureCompare(cfg));
        if (cfg.ProtoAot is not null) Step("capture_native_aot", () => CaptureCompare(cfg, cfg.ProtoAot));
        Step("dpi", () => Dpi(cfg));
        Step("fonts", () => Fonts(cfg));
        Step("theme_follow", () => ThemeSwitch(cfg));
        Step("high_contrast", () => HighContrast(cfg));
        Step("drag", () => Drag(cfg));
        Step("keyboard", () => Keyboard(cfg));
        if (cfg.ProtoTrimmed is not null) Step("trimmed_startup", () => TrimmedStartup(cfg));

        Apps.KillAll();
        Results["verdicts"] = Verdicts();
        File.WriteAllText(Path.Combine(_out, "results.json"), Results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log("== verdicts: " + Results["verdicts"]!.ToJsonString());
        Log($"done in {Clock.Elapsed.TotalSeconds:0} s; results in {_out}");
        return 0;
    }

    internal sealed class Config
    {
        public required string Proto { get; init; }
        public string? ProtoTrimmed { get; init; }
        public string? ProtoComposite { get; init; }
        public string? ProtoAot { get; init; }
        public required string WinForms { get; init; }
        public required string Hook { get; init; }
        public required string Ref { get; init; }
        public int Runs { get; init; } = 7;
        public HashSet<string>? Only { get; init; }
    }

    private static Dictionary<string, string> ParseOptions(string[] a)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < a.Length) d[a[i][2..]] = a[++i];
            else d["arg" + i] = a[i];
        }
        return d;
    }

    internal static void Log(string s)
    {
        var line = $"[{Clock.Elapsed.TotalSeconds,7:0.0}s] {s}";
        Console.WriteLine(line);
        _log?.WriteLine(line);
    }

    private static void Step(string id, Func<JsonObject> body)
    {
        Log($"== step {id}");
        DismissShellFlyouts();
        var sw = Stopwatch.StartNew();
        JsonObject r;
        try { r = body(); }
        catch (Exception e)
        {
            r = new JsonObject { ["pass"] = false, ["error"] = e.ToString() };
            Log($"!! step {id} threw: {e}");
        }
        r["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
        Results[id] = r;
        Log($"== step {id}: pass={r["pass"]?.ToJsonString() ?? "n/a"} in {sw.Elapsed.TotalSeconds:0.0} s :: {Trunc(r.ToJsonString(), 3000)}");
        File.WriteAllText(Path.Combine(_out, "results.partial.json"), Results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Apps.KillAll();
    }

    /// <summary>If the Start menu or Search is in front (the runner sometimes opens it), Escape closes it.</summary>
    private static void DismissShellFlyouts()
    {
        var fg = Win.GetForegroundWindow();
        var who = Win.Describe(fg);
        if (who.StartsWith("StartMenuExperienceHost", StringComparison.Ordinal) || who.StartsWith("SearchHost", StringComparison.Ordinal)
            || who.StartsWith("ShellExperienceHost", StringComparison.Ordinal))
        {
            Win.Key(0x1B);
            Thread.Sleep(500);
            Log($"closed a shell flyout: {who}; foreground now {Win.Describe(Win.GetForegroundWindow())}");
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static JsonArray Arr<T>(IEnumerable<T> e) => new([.. e.Select(x => JsonValue.Create(x) as JsonNode)]);

    private static double Median(IEnumerable<double> v)
    {
        var a = v.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        if (a.Length == 0) return double.NaN;
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    private static int _seq;

    /// <summary>Starts the preview with an event log and a control pipe; waits for its window (unless hidden).</summary>
    internal static AppRun StartProto(Config cfg, string tag, string extra = "", bool hook = false, string? exe = null)
    {
        int n = Interlocked.Increment(ref _seq);
        var log = Path.Combine(_logs, $"proto-{n:00}-{tag}.jsonl");
        var pipe = $"OsmiumUiPreview-eval-{Environment.ProcessId}-{n}";
        var env = hook ? new Dictionary<string, string> { ["DOTNET_STARTUP_HOOKS"] = cfg.Hook } : null;
        var run = Apps.Start("avalonia", exe ?? cfg.Proto, $"--log \"{log}\" --control {pipe} {extra}".Trim(), log, pipe, env);
        Log($"started preview #{n} ({tag}) pid {run.Pid}: {extra}");
        return run;
    }

    internal static AppRun StartWinForms(Config cfg, bool hook = false)
    {
        var env = hook ? new Dictionary<string, string> { ["DOTNET_STARTUP_HOOKS"] = cfg.Hook } : null;
        var run = Apps.Start("winforms", cfg.WinForms, "", null, null, env);
        Log($"started WinForms Osmium.App pid {run.Pid}");
        return run;
    }

    /// <summary>Waits for the preview's control pipe and first frame, then moves its window to a fixed spot on screen.</summary>
    internal static IntPtr ReadyProto(AppRun run, int x = 20, int y = 20)
    {
        var ff = run.WaitEvent("first-frame", 30_000);
        if (ff is null) throw new InvalidOperationException("the preview logged no first frame: " + Trunc(string.Join("\n", run.Events().Select(e => e.ToJsonString())), 4000));
        var h = run.WaitMainWindow(10_000);
        if (h != IntPtr.Zero)
        {
            Win.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004); // SWP_NOSIZE | SWP_NOZORDER
            Win.SetForegroundWindow(h);
            Thread.Sleep(400);
        }
        return h;
    }
}
