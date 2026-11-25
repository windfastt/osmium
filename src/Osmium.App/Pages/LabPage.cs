using System.Diagnostics;
using Osmium.Core.Analysis;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;
using Osmium.Desktop.Settings;

namespace Osmium.App;

internal sealed class LabPage : UserControl, IPage
{
    private readonly TrayApp _app;
    private readonly Label _file = Ui.Text("Choose a file to benchmark every candidate pipeline on it.", muted: true);
    private readonly ComboBox _profile = Ui.Combo(Profile.All.Select(p => p.Name), "insane");
    private readonly NumericUpDown _sample = new() { Minimum = 1, Maximum = 1024, Value = 4, Width = 70 };
    private readonly FastList _list = Ui.List(("Size", 110), ("Ratio", 80), ("Compress MB/s", 110), ("Extract MB/s", 110), ("Pipeline", 520));
    private string? _path;
    private CancellationTokenSource? _cts;

    public LabPage(TrayApp app)
    {
        _app = app;
        var tools = Ui.Bar(
            Ui.Button("Choose file…", (_, _) =>
            {
                using var d = new OpenFileDialog();
                if (d.ShowDialog(this) == DialogResult.OK) SetFile(d.FileName);
            }),
            new Label { Text = "Candidates from:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, _profile,
            new Label { Text = "Sample (MiB):", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, _sample,
            Ui.Button("Run", (_, _) => _ = RunAsync(), accent: true),
            Ui.Button("Stop", (_, _) => _cts?.Cancel()),
            Ui.Button("Save best as rule", (_, _) => SaveBest()));
        Controls.Add(Ui.Page("Lab", "Benchmark: compress and decompress a sample with each candidate, verify the round trip, and compare size against speed.", _list, tools, _file));
    }

    public void Activated() { }

    /// <summary>The file to benchmark (as chosen with "Choose file…").</summary>
    internal void SetFile(string path)
    {
        _path = path;
        _file.Text = path;
    }

    /// <summary>Selects the profile whose candidates are benchmarked.</summary>
    internal void SelectProfile(string name)
    {
        if (_profile.Items.Contains(name)) _profile.SelectedItem = name;
    }

    /// <summary>The Run button. Completes when every candidate has been benchmarked (or the run stopped).</summary>
    internal Task RunAsync()
    {
        if (_path is null) { Ui.Info("Choose a file first."); return Task.CompletedTask; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var path = _path;
        var profile = Profile.ByName(_profile.SelectedItem!.ToString()!);
        long sampleLen = (long)_sample.Value << 20;
        _list.Items.Clear();
        return Ui.RunSafeAsync(this, async () =>
        {
            var (an, data) = await Task.Run(() =>
            {
                var a = Sniffer.Analyze(path, profile.SampleWindows);
                long len = new FileInfo(path).Length;
                using var h = File.OpenHandle(path);
                var d = len <= sampleLen ? File.ReadAllBytes(path) : Sniffer.SampleBody(h, len, (int)(sampleLen / 65536));
                return (a, d);
            }, ct);
            _file.Text = $"{path} — {an.Summary}";
            foreach (var spec in Candidates.For(an.Class, an, profile, MethodRegistry.Default))
            {
                ct.ThrowIfCancellationRequested();
                var row = await Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    var (desc, payload) = PipelineExecutor.Encode(spec, data, new CodecContext { Token = ct });
                    double te = sw.Elapsed.TotalSeconds;
                    sw.Restart();
                    var back = PipelineExecutor.Decode(desc, payload, new CodecContext { Token = ct });
                    double td = sw.Elapsed.TotalSeconds;
                    if (!back.AsSpan().SequenceEqual(data)) throw new InvalidOperationException($"round-trip mismatch for {spec}");
                    return (size: (long)payload.Length, enc: data.Length / 1e6 / Math.Max(te, 1e-6), dec: data.Length / 1e6 / Math.Max(td, 1e-6));
                }, ct);
                _list.Items.Add(new ListViewItem([row.size.ToString("N0"), $"{(double)row.size / Math.Max(1, data.Length):P2}", $"{row.enc:F2}", $"{row.dec:F2}", spec.ToString()]) { Tag = row.size });
                _list.ListViewItemSorter = Comparer<ListViewItem>.Create((a, b) => ((long)a.Tag!).CompareTo((long)b.Tag!));
                _list.Sort();
            }
        });
    }

    private void SaveBest()
    {
        if (_path is null || _list.Items.Count == 0) return;
        var best = _list.Items[0].SubItems[4].Text;
        var rs = File.Exists(AppPaths.RulesFile) ? RuleSet.Load(AppPaths.RulesFile) : new RuleSet();
        rs.Rules.Add(new Rule
        {
            Id = "lab-" + Path.GetFileName(_path).ToLowerInvariant(),
            Description = "Best pipeline found in the Lab",
            Match = new RuleMatch { Globs = ["**/" + Path.GetFileName(_path)] },
            Actions = new RuleActions { Pipeline = best, Solid = "own" },
            Origin = new RuleOrigin { Type = "tuned", At = DateTime.UtcNow },
        });
        rs.Save(AppPaths.RulesFile);
        _app.LoadRules();
        Ui.Info("Saved as a global rule (Rules page).");
    }
}
