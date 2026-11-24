using Osmium.Core.Planning;
using Osmium.Desktop.Ipc;
using Osmium.Desktop.Jobs;
using Osmium.Desktop.Settings;
using Osmium.Desktop.Shell;
using Osmium.Desktop.Tray;
using Osmium.Desktop.Watch;

namespace Osmium.Desktop.Tests;

public class DesktopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void EverySettingDescriptorIsWellFormed()
    {
        var s = new OsmiumSettings();
        var all = SettingReflector.Describe(s);
        Assert.True(all.Count >= 50, $"only {all.Count} settings");
        var cats = SettingReflector.Categories(s).Select(c => c.Name).ToHashSet();
        Assert.Contains("Experimental", cats);
        foreach (var d in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Label), d.Key);
            Assert.Contains(d.Category, cats);
            if (!double.IsNaN(d.Min) && !double.IsNaN(d.Max)) Assert.True(d.Min <= d.Max, d.Key);
            Assert.Null(d.Validate(d.Default));
            foreach (var cond in new[] { d.VisibleWhen, d.EnabledWhen })
            {
                if (cond is null) continue;
                string name = cond.TrimStart('!').Split('=')[0];
                Assert.Contains(all, x => x.Key.EndsWith("." + name));
            }
        }
    }

    [Fact]
    public void SettingsRoundTripAndRecoverFromCorruption()
    {
        var path = Path.Combine(Path.GetTempPath(), "osm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(path);
            var s = new OsmiumSettings();
            s.Compression.Profile = ProfileName.Insane;
            s.Cluster.Workers.Add("10.0.0.2:46510");
            store.Save(s);
            var back = store.Load();
            Assert.Equal(ProfileName.Insane, back.Compression.Profile);
            Assert.Equal(["10.0.0.2:46510"], back.Cluster.Workers);
            File.WriteAllText(path, "{ not json");
            var recovered = store.Load();
            Assert.Equal(ProfileName.Balanced, recovered.Compression.Profile);
            Assert.NotNull(store.LastError);
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public void VisibleWhenConditionsEvaluate()
    {
        var s = new OsmiumSettings();
        var all = SettingReflector.Describe(s);
        var folder = all.Single(d => d.Key == "General.OutputFolder");
        Assert.False(SettingReflector.Evaluate(folder.VisibleWhen, all, folder.Category));
        s.General.Output = OutputLocation.Folder;
        Assert.True(SettingReflector.Evaluate(folder.VisibleWhen, all, folder.Category));
    }

    [Fact]
    public void SettingsTranslateIntoPlannerOptions()
    {
        var s = new OsmiumSettings();
        s.Codecs.AllowPpmd = false;
        s.Filters.Text = false;
        s.Compression.Profile = ProfileName.Ultra;
        s.Analysis.SearchEvals = 0;
        var po = JobManager.BuildPlanOptions(s, new JobRequest(), new RuleSet(), null);
        Assert.Equal("ultra", po.Profile.Name);
        Assert.Equal(0, po.Profile.SearchEvals);
        var eff = po.Rules.Resolve("a/b.txt", 10, null, Core.Pipeline.MethodRegistry.Default);
        Assert.Contains("ppmd", eff.ForbidCodecs);
        Assert.Contains("text", eff.ForbidFilters);
    }

    [Fact]
    public void ExperimentalSettingsReachThePlanner()
    {
        var s = new OsmiumSettings();
        s.Compression.Profile = ProfileName.Insane;
        s.Experimental.CmAllModels = true;                       // without the master switch: no effect
        Assert.False(JobManager.BuildPlanOptions(s, new JobRequest(), new RuleSet(), null).Profile.CmAllModels);
        s.Experimental.Enabled = true;
        var po = JobManager.BuildPlanOptions(s, new JobRequest(), new RuleSet(), null);
        Assert.True(po.Profile.CmAllModels);
        Assert.True(po.AllowExperimental);
        var cms = Candidates.For(Osmium.Core.Analysis.CompatClass.Text, null, po.Profile, Core.Pipeline.MethodRegistry.Default)
            .Where(c => c.Codec.Method.Name == "cm").ToList();
        Assert.NotEmpty(cms);
        Assert.All(cms, c => Assert.Equal((long)Osmium.Core.Codecs.Cm.CmModels.All, c.Codec.Params.Get("models", -1)));
    }

    [Fact]
    public async Task IpcRoundTripsOverARealPipe()
    {
        var name = IpcProtocol.PipeName("test-" + Guid.NewGuid().ToString("N")[..6], 1);
        var got = new TaskCompletionSource<IpcMessage>();
        await using var server = new IpcServer(name, m => { got.TrySetResult(m); return Task.CompletedTask; });
        server.Start();
        var path = Path.GetFullPath("some file.txt");
        bool ok = await IpcProtocol.ForwardAsync(name, new IpcMessage { Verb = "compress-quick", Profile = "ultra", Args = [path] }, TimeSpan.FromSeconds(5), Ct);
        Assert.True(ok);
        var m = await got.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("compress-quick", m.Verb);
        Assert.Equal([path], m.Args);
        // relative paths are refused
        Assert.False(await IpcProtocol.ForwardAsync(name, new IpcMessage { Verb = "open", Args = ["relative.osa"] }, TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public void CoalescerBatchesExplorerStorms()
    {
        var now = new DateTime(2026, 1, 1);
        var c = new InvocationCoalescer(TimeSpan.FromMilliseconds(350), TimeSpan.FromSeconds(4), clock: () => now);
        for (int i = 0; i < 50; i++)
        {
            c.Add(new IpcMessage { Verb = "compress-quick", Profile = "balanced", Args = [Path.GetFullPath($"f{i}.txt")] });
            now = now.AddMilliseconds(20);
        }
        c.Add(new IpcMessage { Verb = "compress-quick", Profile = "balanced", Args = [Path.GetFullPath("f1.txt")] }); // duplicate
        c.Add(new IpcMessage { Verb = "extract-here", Args = [Path.GetFullPath("a.osa")] });
        Assert.Empty(c.Due());
        now = now.AddMilliseconds(400);
        var due = c.Due();
        Assert.Equal(2, due.Count);
        Assert.Equal(50, due.Single(b => b.Verb == "compress-quick").Paths.Count);
    }

    [Fact]
    public void ShellPlanIsConsistent()
    {
        var plan = ShellRegistrationPlan.Build(@"C:\Program Files\Osmium\Osmium.App.exe", @"C:\Program Files\Osmium\icons\archive.ico", ShellFeatures.All, ["balanced", "ultra"]);
        Assert.Contains(plan, v => v.Key == @"Software\Classes\.osa" && v.Name is null && v.Value == "Osmium.Archive");
        Assert.Contains(plan, v => v.Key.EndsWith(@"\20quickbalanced\command") && v.Value.Contains("--profile balanced"));
        Assert.Contains(plan, v => v.Key == ShellRegistrationPlan.RunKey && v.Value.EndsWith("--tray"));
        // every key we write is under a key we know how to remove (or is the shared .osa / Run key)
        foreach (var v in plan.Where(v => v.Key != ShellRegistrationPlan.RunKey && v.Key != @"Software\Classes\.osa"))
            Assert.Contains(ShellRegistrationPlan.OwnedKeys, k => v.Key.StartsWith(k, StringComparison.OrdinalIgnoreCase));
        // every verb carries MultiSelectModel=Player (otherwise verbs vanish beyond 15 selected items)
        foreach (var cmd in plan.Where(v => v.Key.EndsWith(@"\command")))
            Assert.Contains(plan, v => v.Key == cmd.Key[..^@"\command".Length] && v.Name == "MultiSelectModel" && v.Value == "Player");
    }

    [Theory]
    [InlineData(new[] { "--tray" }, "tray")]
    [InlineData(new[] { "--register", "--scope", "machine", "--features", "contextmenu,assoc" }, "register")]
    [InlineData(new[] { "--shell", "compress-quick", "--profile", "ultra", "x.txt" }, "invoke")]
    [InlineData(new string[0], "show")]
    public void CommandLineParses(string[] args, string kind) => Assert.Equal(kind, AppCommand.Parse(args).Kind);

    [Fact]
    public void TrayTextRespectsWindowsLimits()
    {
        var longName = new string('x', 500);
        var j = new JobSnapshot(Guid.NewGuid(), longName, JobKind.Compress, JobState.Running, "Compressing", 0.37, 100, 50, 200,
            TimeSpan.FromSeconds(10), null, null, null, null, DateTime.Now, []);
        Assert.True(TrayText.Tooltip([j]).Length <= TrayText.TooltipMax);
        var (t, b, _) = TrayText.Completion(j with { State = JobState.Completed });
        Assert.True(t.Length <= TrayText.BalloonTitleMax && b.Length <= TrayText.BalloonBodyMax);
        Assert.Equal(9, TrayText.Bucket(0.37));
    }

    [Fact]
    public void WatchEngineWaitsForFilesToSettle()
    {
        var now = new DateTime(2026, 1, 1);
        bool unlocked = false;
        var e = new WatchFolderEngine(() => now, _ => unlocked);
        var cfg = new WatchFolderConfig { Path = Path.GetFullPath("watched"), SettleSeconds = 10 };
        var file = Path.Combine(cfg.Path, "report.txt");
        var mtime = now;
        e.Touch(cfg, file, 100, mtime);
        e.Touch(cfg, Path.Combine(cfg.Path, "x.tmp"), 1, mtime);              // excluded
        e.Touch(cfg, Path.Combine(cfg.Path, "_osmium", "y.txt"), 1, mtime);   // our own output
        Assert.Equal(1, e.PendingCount);
        now = now.AddSeconds(5);
        Assert.Empty(e.Tick(_ => (100, mtime)));                               // not settled yet
        now = now.AddSeconds(6);
        Assert.Empty(e.Tick(_ => (100, mtime)));                               // settled but locked
        now = now.AddSeconds(11);
        unlocked = true;
        Assert.Single(e.Tick(_ => (100, mtime)));
        e.Touch(cfg, file, 100, mtime);                                       // same version: ignored
        Assert.Equal(0, e.PendingCount);
    }

    [Fact]
    public async Task JobManagerCompressesAndExtracts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "osm-jobs-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "in"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "in", "a.txt"), string.Concat(Enumerable.Repeat("job manager test ", 2000)));
            var s = new OsmiumSettings();
            s.Compression.Profile = ProfileName.Max;
            using var jm = new JobManager(() => s, () => new RuleSet());
            var done = new TaskCompletionSource<JobSnapshot>();
            jm.JobFinished += j => done.TrySetResult(j);
            jm.Enqueue(new JobRequest { Kind = JobKind.Compress, Inputs = [Path.Combine(dir, "in")], Output = Path.Combine(dir, "a.osa") });
            var r = await done.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal(JobState.Completed, r.State);
            Assert.True(r.BytesOut < r.BytesIn / 10);

            done = new TaskCompletionSource<JobSnapshot>();
            jm.Enqueue(new JobRequest { Kind = JobKind.Extract, Inputs = [Path.Combine(dir, "a.osa")], Output = Path.Combine(dir, "out") });
            r = await done.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal(JobState.Completed, r.State);
            Assert.Equal(File.ReadAllText(Path.Combine(dir, "in", "a.txt")), File.ReadAllText(Path.Combine(dir, "out", "in", "a.txt")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
