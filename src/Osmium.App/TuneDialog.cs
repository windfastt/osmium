// Layout: logical units only
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
internal sealed class TuneDialog : ThemedDialog
{
    private readonly TrayApp _app;
    private readonly string _path;
    private readonly EditBox _pipeline = new() { Dock = DockStyle.Fill };
    private readonly Label _valid = Ui.Text("", muted: true);
    private readonly CheckedListBox _filters = new() { CheckOnClick = true, IntegralHeight = false };
    private readonly ComboBox _codec;
    private readonly Label _codecInfo = Ui.Text("", muted: true);
    private readonly SettingsRenderer _params = new() { Dock = DockStyle.Fill };
    private readonly TextBox _result = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Visible = false, Tag = "code" };
    private readonly Label _resultHint = Ui.Text("Quick test compresses and extracts the file with this pipeline. Deep auto-tune searches for a smaller one.", muted: true);
    private readonly ComboBox _profile = Ui.Combo(["ultra", "insane", "max"], "insane");
    private readonly TableLayoutPanel _root = new() { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true };
    private readonly CardPanel _builderCard, _testCard;
    private readonly TableLayoutPanel _builderLeft = new() { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
    private readonly Dictionary<string, ParamValues> _paramState = [];
    private CancellationTokenSource? _cts;
    private bool _syncing;

    public string? Pipeline { get; private set; }

    /// <summary>Completes once the file's analysis is shown (it replaces the "Analyzing…" line).</summary>
    internal Task Analysis { get; }

    public TuneDialog(TrayApp app, string path, string? current, string profile, bool startDeep)
        : base("Tune — " + Path.GetFileName(path))
    {
        _app = app;
        _path = path;
        Icon = app.AppIcon;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimumSize = new Size(LogicalToDeviceUnits(600), LogicalToDeviceUnits(440));
        FitTo(Screen.FromPoint(Cursor.Position).WorkingArea);
        _pipeline.Font = Theme.Mono;
        _result.Font = Theme.Mono;
        if (_profile.Items.Contains(profile)) _profile.SelectedItem = profile;

        var reg = MethodRegistry.Default;
        foreach (var f in reg.Filters) _filters.Items.Add(f.Name);
        // Tall enough for every filter, so the order and the ticks are seen at once.
        int filterRows = Math.Max(1, _filters.Items.Count);
        _filters.Height = _filters.ItemHeight * filterRows + LogicalToDeviceUnits(6);
        _filters.Width = LogicalToDeviceUnits(180);
        _codec = Ui.Combo(reg.Codecs.Select(c => c.Name), "lzma");
        _codec.Width = _filters.Width;
        _codec.SelectedIndexChanged += (_, _) => { RenderParams(); SyncFromBuilder(); };
        _filters.ItemCheck += (_, _) => { if (!_syncing && IsHandleCreated) BeginInvoke(SyncFromBuilder); };
        _pipeline.TextChanged += (_, _) => SyncFromText();

        _root.Padding = Space.Pad(_root, Space.Xl, 20, Space.Xl, Space.Sm);
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var header = new PageHeader("Tune", "", "Analyzing the file…");
        int row = 0;
        foreach (var line in header.Lines)
        {
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _root.Controls.Add(line, 0, row++);
        }
        header.Lede = $"Build a pipeline for {Path.GetFileName(path)} by hand, test it on the file, or let Osmium search for a better one.";

        // The pipeline as text, and the same pipeline as filters, a codec and its parameters.
        var builder = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        builder.Margin = Padding.Empty;
        builder.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        builder.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        builder.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        builder.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        builder.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _pipeline.Margin = Space.Pad(_pipeline, 0, 0, 0, 2);
        builder.Controls.Add(_pipeline, 0, 0);
        builder.SetColumnSpan(_pipeline, 2);
        builder.Controls.Add(_valid, 0, 1);
        builder.SetColumnSpan(_valid, 2);
        _valid.Margin = Space.Pad(_valid, 0, 0, 0, Space.Md);
        _builderLeft.Margin = Space.Pad(_builderLeft, 0, 0, Space.Lg, 0);
        _builderLeft.Controls.Add(FieldLabel("Filters, in order"));
        _builderLeft.Controls.Add(_filters);
        var codecLabel = FieldLabel("Codec");
        codecLabel.Margin = Space.Pad(codecLabel, 0, Space.Md, 0, Space.Xs);
        _builderLeft.Controls.Add(codecLabel);
        _builderLeft.Controls.Add(_codec);
        builder.Controls.Add(_builderLeft, 0, 2);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        right.Margin = Padding.Empty;
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(_codecInfo, 0, 0);
        right.Controls.Add(_params, 0, 1);
        _params.Margin = Padding.Empty;
        builder.Controls.Add(right, 1, 2);
        _builderCard = Ui.Card(builder, "Pipeline");

        var test = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        test.Margin = Padding.Empty;
        test.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var deepLabel = new Label { Text = "Deep auto-tune with", AutoSize = true, UseMnemonic = false };
        deepLabel.Margin = Space.Pad(deepLabel, Space.Sm, 8, 4, 0);
        var bar = Ui.Bar(
            Ui.Button("Quick test", (_, _) => QuickTest()),
            deepLabel, _profile,
            Ui.Button("Deep auto-tune", (_, _) => DeepTune()),
            Ui.Button("Stop", (_, _) => _cts?.Cancel()));
        bar.Dock = DockStyle.Fill;
        bar.Margin = Space.Pad(bar, 0, 0, 0, Space.Xs);
        test.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        test.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        test.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        test.Controls.Add(bar, 0, 0);
        test.Controls.Add(_resultHint, 0, 1);
        test.Controls.Add(_result, 0, 2);
        _testCard = Ui.Card(test, "Test");
        _testCard.AutoSize = true; // in a row that takes its height from the card
        _testCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _builderCard.Dock = DockStyle.Fill;
        _builderCard.Margin = Space.Pad(_builderCard, 0, 0, 0, Space.Lg);
        _root.Controls.Add(_builderCard, 0, row++);
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _testCard.Dock = DockStyle.Fill;
        _testCard.Margin = Padding.Empty;
        _root.Controls.Add(_testCard, 0, row);
        Controls.Add(_root);

        var ok = Ui.Primary("Use this pipeline", (_, _) => Accept());
        var cancel = Ui.Button("Cancel", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        ButtonBar.Controls.Add(ok);
        ButtonBar.Controls.Add(cancel);
        AcceptButton = ok;     // Enter (and Ctrl+Enter from a box that takes Enter)
        CancelButton = cancel; // Esc
        Theme.Apply(this);
        UpdateMinimumLayout();

        _pipeline.Text = current is null or "store" or "exclude" ? "lzma" : current;
        Analysis = Ui.RunSafeAsync(this, async () =>
        {
            var a = await Task.Run(() => Sniffer.Analyze(path, 32));
            header.Spec = $"{Ui.Bytes(new FileInfo(path).Length)} · {a.Summary}";
            if (current is null)
                _pipeline.Text = Candidates.Default(a.Class, a, Profile.ByName("max"), MethodRegistry.Default).ToString();
            if (startDeep) DeepTune();
        });
    }

    private static Label FieldLabel(string text)
    {
        var l = new Label { Text = text, AutoSize = true, UseMnemonic = false, Font = Theme.BodyStrong };
        l.Margin = Space.Pad(l, 0, 0, 0, Space.Xs);
        return l;
    }

    /// <summary>Sizes the dialog for a screen's working area: 860 × 760 logical pixels, or 90 % of the area when that is
    /// smaller, so the buttons along the bottom are always on screen.</summary>
    internal void FitTo(Rectangle workingArea)
    {
        int width = Math.Min(LogicalToDeviceUnits(860), workingArea.Width * 9 / 10);
        int height = Math.Min(LogicalToDeviceUnits(760), workingArea.Height * 9 / 10);
        Size = new Size(width, height);
    }

    /// <summary>
    /// The content's smallest height: every row at its natural height, the filters and codec column whole. When the
    /// dialog is smaller, the content scrolls above the buttons instead of being cut.
    /// </summary>
    private void UpdateMinimumLayout()
    {
        int width = Math.Max(1, _root.ClientSize.Width);
        int height = _root.Padding.Vertical;
        foreach (Control c in _root.Controls)
        {
            if (!c.Visible && c.IsHandleCreated) continue;
            if (ReferenceEquals(c, _builderCard))
                height += _builderCard.ChromeHeight + _pipeline.PreferredSize.Height + _pipeline.Margin.Vertical
                          + _valid.GetPreferredSize(new Size(width, 0)).Height + _valid.Margin.Vertical
                          + _builderLeft.GetPreferredSize(new Size(width, 0)).Height + _builderLeft.Margin.Vertical + c.Margin.Vertical
                          + LogicalToDeviceUnits(Space.Xs); // rounding in the nested tables
            else height += c.GetPreferredSize(new Size(width, 0)).Height + c.Margin.Vertical;
        }
        _root.AutoScrollMinSize = new Size(0, height);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (IsHandleCreated) UpdateMinimumLayout();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter)) { Accept(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Shows test or tuning output in the result box (hidden while there is none).</summary>
    private void ShowResult(string text)
    {
        _result.Text = text;
        bool show = text.Length > 0;
        if (_result.Visible == show) return;
        _result.Height = LogicalToDeviceUnits(120);
        _result.Visible = show;
        _resultHint.Visible = !show;
        UpdateMinimumLayout();
    }

    private void RenderParams()
    {
        var m = MethodRegistry.Default.Get(_codec.SelectedItem!.ToString()!);
        if (!_paramState.ContainsKey(m.Name)) _paramState[m.Name] = ParamValues.Empty;
        // On/off parameters as checkboxes, choices by name, sizes as "64M"; the values map back unchanged.
        var descs = ParamDescriptors.For(m, () => _paramState[m.Name], p => { _paramState[m.Name] = p; SyncFromBuilder(); });
        _params.ClearRejected();
        _codecInfo.Text = m.Description;
        _params.Render(descs, descs, () => true);
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
            ShowResult($"Testing {spec} …");
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
            ShowResult($"{spec}{Environment.NewLine}{Ui.Bytes(r.Item1)}{(r.sample ? " sample" : "")} → {Ui.Bytes(r.Item2)} ({(double)r.Item2 / Math.Max(1, r.Item1):P2})" +
                       $"{Environment.NewLine}compress {r.te.TotalSeconds:F2}s, extract {r.Item4.TotalSeconds:F2}s — round trip verified");
        });
    }

    private void DeepTune()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var profileName = _profile.SelectedItem?.ToString() ?? "insane";
        Ui.RunSafe(this, async () =>
        {
            ShowResult($"Searching with the {profileName} profile (successive halving + hill climbing)…");
            var po = JobManager.BuildPlanOptions(_app.Settings, new JobRequest { Profile = profileName }, _app.GlobalRules, null);
            po = po with { Profile = po.Profile with { RaceTopTwo = false } };
            var plan = await Task.Run(() => new Planner(po).BuildAsync(Scanner.Scan([_path]), ct), ct);
            var g = plan.Groups.FirstOrDefault();
            if (g?.Spec is null) { ShowResult("Nothing to tune (empty file?)."); return; }
            _pipeline.Text = g.Spec.ToString();
            ShowResult(string.Join(Environment.NewLine, g.Explain));
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
