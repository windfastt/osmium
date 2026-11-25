using System.Diagnostics;
using Osmium.Core.Analysis;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;
using Osmium.Desktop.Jobs;
using Osmium.Desktop.Settings;

namespace Osmium.App;

/// <summary>
/// Per-file "patch" editor: build a pipeline by hand (filters + codec + parameters, rendered from the engine's
/// parameter metadata), quick-test it on the file, or run a deep auto-tune that searches for the best pipeline.
/// </summary>
internal sealed class TuneDialog : Form
{
    private readonly TrayApp _app;
    private readonly string _path;
    private readonly EditBox _pipeline = new() { Width = 620 };
    private readonly Label _valid = Ui.Text("", muted: true);
    private readonly CheckedListBox _filters = new() { Height = 130, Width = 220, CheckOnClick = true, IntegralHeight = false };
    private readonly ComboBox _codec;
    private readonly SettingsRenderer _params = new() { Width = 520, Height = 300 };
    private readonly TextBox _result = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 150, Width = 760 };
    private readonly ComboBox _profile = Ui.Combo(["ultra", "insane", "max"], "insane");
    private readonly Dictionary<string, ParamValues> _paramState = [];
    private CancellationTokenSource? _cts;
    private bool _syncing;

    public string? Pipeline { get; private set; }

    /// <summary>Completes once the file's analysis is shown (it replaces the "Analyzing…" line).</summary>
    internal Task Analysis { get; }

    public TuneDialog(TrayApp app, string path, string? current, string profile, bool startDeep)
    {
        _app = app;
        _path = path;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Text = "Tune — " + Path.GetFileName(path);
        Icon = app.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(860, 760);
        MinimumSize = new Size(760, 600);
        Font = Theme.Body;
        _result.Font = Theme.Mono;
        if (_profile.Items.Contains(profile)) _profile.SelectedItem = profile;

        var reg = MethodRegistry.Default;
        foreach (var f in reg.Filters) _filters.Items.Add(f.Name);
        _codec = Ui.Combo(reg.Codecs.Select(c => c.Name), "lzma");
        _codec.SelectedIndexChanged += (_, _) => { RenderParams(); SyncFromBuilder(); };
        _filters.ItemCheck += (_, _) => { if (!_syncing && IsHandleCreated) BeginInvoke(SyncFromBuilder); };
        _pipeline.TextChanged += (_, _) => SyncFromText();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16), AutoScroll = true };
        root.Controls.Add(Ui.Heading(Path.GetFileName(path)));
        var info = Ui.Text("Analyzing…", muted: true, maxWidth: 780);
        root.Controls.Add(info);
        root.Controls.Add(new Label { Text = "Pipeline (filters + codec):", AutoSize = true, Margin = new Padding(0, 10, 0, 2) });
        root.Controls.Add(_pipeline);
        root.Controls.Add(_valid);

        var builder = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        left.Controls.Add(new Label { Text = "Filters (in order):", AutoSize = true });
        left.Controls.Add(_filters);
        left.Controls.Add(new Label { Text = "Codec:", AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        left.Controls.Add(_codec);
        builder.Controls.Add(left, 0, 0);
        builder.Controls.Add(_params, 1, 0);
        root.Controls.Add(builder);

        root.Controls.Add(Ui.Bar(
            Ui.Button("Quick test", (_, _) => QuickTest()),
            new Label { Text = "Deep auto-tune with:", AutoSize = true, Padding = new Padding(12, 6, 0, 0) }, _profile,
            Ui.Button("Deep auto-tune", (_, _) => DeepTune()),
            Ui.Button("Stop", (_, _) => _cts?.Cancel())));
        root.Controls.Add(_result);
        var ok = Ui.Button("Use this pipeline", (_, _) => Accept(), accent: true);
        var cancel = Ui.Button("Cancel", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        root.Controls.Add(Ui.Bar(ok, cancel));
        AcceptButton = ok;     // Enter
        CancelButton = cancel; // Esc
        Controls.Add(root);
        Theme.Apply(this);

        _pipeline.Text = current is null or "store" or "exclude" ? "lzma" : current;
        Analysis = Ui.RunSafeAsync(this, async () =>
        {
            var a = await Task.Run(() => Sniffer.Analyze(path, 32));
            info.Text = $"{Ui.Bytes(new FileInfo(path).Length)} · {a.Summary}";
            if (current is null)
                _pipeline.Text = Candidates.Default(a.Class, a, Profile.ByName("max"), MethodRegistry.Default).ToString();
            if (startDeep) DeepTune();
        });
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Native.SetDarkTitleBar(Handle, Theme.IsDark); } catch (Exception) { }
    }

    private void RenderParams()
    {
        var m = MethodRegistry.Default.Get(_codec.SelectedItem!.ToString()!);
        if (!_paramState.ContainsKey(m.Name)) _paramState[m.Name] = ParamValues.Empty;
        // On/off parameters as checkboxes, choices by name, sizes as "64M"; the values map back unchanged.
        var descs = ParamDescriptors.For(m, () => _paramState[m.Name], p => { _paramState[m.Name] = p; SyncFromBuilder(); });
        _params.ClearRejected();
        _params.Render(descs, descs, () => true, m.Description);
    }

    private void SyncFromBuilder()
    {
        if (_syncing) return;
        var codec = MethodRegistry.Default.Get(_codec.SelectedItem!.ToString()!);
        var stages = _filters.CheckedItems.Cast<string>().Select(f => MethodRegistry.Default.Stage(f)).ToList();
        stages.Add(new StageSpec(codec, _paramState.GetValueOrDefault(codec.Name) ?? ParamValues.Empty));
        try
        {
            _syncing = true;
            _pipeline.Text = new PipelineSpec(stages).ToString();
        }
        catch (ArgumentException e) { _valid.Text = e.Message; }
        finally { _syncing = false; }
    }

    private void SyncFromText()
    {
        try
        {
            var spec = PipelineSpec.Parse(_pipeline.Text, MethodRegistry.Default);
            _valid.Text = $"✓ valid · encode ~{spec.Stages.Sum(s => 1 / s.Method.EncodeSpeed(s.Params)) * 1:F2} s/MB, decode ~{spec.Stages.Sum(s => 1 / s.Method.DecodeSpeed(s.Params)):F2} s/MB" +
                          (spec.Experimental ? " · EXPERIMENTAL" : "");
            if (_syncing) return;
            _syncing = true;
            for (int i = 0; i < _filters.Items.Count; i++)
                _filters.SetItemChecked(i, spec.Filters.Any(f => f.Method.Name == (string)_filters.Items[i]));
            _paramState[spec.Codec.Method.Name] = spec.Codec.Params;
            _codec.SelectedItem = spec.Codec.Method.Name;
            RenderParams();
        }
        catch (Exception e) when (e is FormatException or ArgumentException) { _valid.Text = "✗ " + e.Message; }
        finally { _syncing = false; }
    }

    private void QuickTest()
    {
        PipelineSpec spec;
        try { spec = PipelineSpec.Parse(_pipeline.Text, MethodRegistry.Default); }
        catch (Exception e) when (e is FormatException or ArgumentException) { Ui.Error(e.Message); return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Ui.RunSafe(this, async () =>
        {
            _result.Text = $"Testing {spec} …";
            var r = await Task.Run(() =>
            {
                long len = new FileInfo(_path).Length;
                byte[] data;
                using (var h = File.OpenHandle(_path))
                    data = len <= (16 << 20) ? File.ReadAllBytes(_path) : Sniffer.SampleBody(h, len, 256);
                var sw = Stopwatch.StartNew();
                var (d, payload) = PipelineExecutor.Encode(spec, data, new CodecContext { Token = ct });
                var te = sw.Elapsed;
                sw.Restart();
                var back = PipelineExecutor.Decode(d, payload, new CodecContext { Token = ct });
                if (!back.AsSpan().SequenceEqual(data)) throw new InvalidOperationException("Round-trip mismatch — please report this pipeline.");
                return (data.Length, payload.Length, te, sw.Elapsed, sample: len > data.Length);
            }, ct);
            _result.Text = $"{spec}{Environment.NewLine}{Ui.Bytes(r.Item1)}{(r.sample ? " sample" : "")} → {Ui.Bytes(r.Item2)} ({(double)r.Item2 / Math.Max(1, r.Item1):P2})" +
                           $"{Environment.NewLine}compress {r.te.TotalSeconds:F2}s, extract {r.Item4.TotalSeconds:F2}s — round trip verified";
        });
    }

    private void DeepTune()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var profileName = _profile.SelectedItem?.ToString() ?? "insane";
        Ui.RunSafe(this, async () =>
        {
            _result.Text = $"Searching with the {profileName} profile (successive halving + hill climbing)…";
            var po = JobManager.BuildPlanOptions(_app.Settings, new JobRequest { Profile = profileName }, _app.GlobalRules, null);
            po = po with { Profile = po.Profile with { RaceTopTwo = false } };
            var plan = await Task.Run(() => new Planner(po).BuildAsync(Scanner.Scan([_path]), ct), ct);
            var g = plan.Groups.FirstOrDefault();
            if (g?.Spec is null) { _result.Text = "Nothing to tune (empty file?)."; return; }
            _pipeline.Text = g.Spec.ToString();
            _result.Text = string.Join(Environment.NewLine, g.Explain);
        });
    }

    private void Accept()
    {
        Validate(); // commits a number still being typed (Enter can arrive before the box loses focus)
        if (_params.Errors.Count > 0)
        {
            Ui.Error("Correct these parameters first:\n\n" + string.Join("\n", _params.Errors));
            return;
        }
        try
        {
            Pipeline = PipelineSpec.Parse(_pipeline.Text, MethodRegistry.Default).ToString();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception e) when (e is FormatException or ArgumentException) { Ui.Error(e.Message); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosed(e);
    }
}
