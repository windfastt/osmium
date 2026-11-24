using System.Globalization;
using Osmium.Desktop.Jobs;
using Osmium.Desktop.Settings;

namespace Osmium.App;

/// <summary>Main window: navigation rail + lazily created pages + status bar. Closing hides to the tray.</summary>
internal sealed class MainForm : Form
{
    private static readonly (string Name, string Glyph)[] Nav =
    [
        ("Compress", ""), ("Jobs", ""), ("Archives", ""), ("Lab", ""),
        ("Rules", ""), ("Cluster", ""), ("Settings", ""), ("Log", ""),
    ];

    private readonly TrayApp _app;
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _rail = new() { Dock = DockStyle.Left, Width = 200, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4, 12, 4, 4) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), Tag = "muted" };
    private readonly Dictionary<string, Control> _pages = [];
    private readonly Dictionary<string, NavButton> _buttons = [];
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };
    private bool _allowClose, _hintShown;

    public string CurrentPage { get; private set; } = "Compress";

    public MainForm(TrayApp app)
    {
        _app = app;
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Text = "Osmium";
        Icon = app.AppIcon;
        MinimumSize = new Size(900, 600);
        Size = new Size(1180, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Body;

        var brand = new Label { Text = "Osmium", Font = new Font(Theme.Heading.FontFamily, 13f, FontStyle.Bold), AutoSize = true, Margin = new Padding(14, 0, 0, 14) };
        _rail.Controls.Add(brand);
        foreach (var (name, glyph) in Nav)
        {
            var b = new NavButton(name, glyph);
            b.Click += (_, _) => Navigate(name);
            _buttons[name] = b;
            _rail.Controls.Add(b);
        }
        Controls.Add(_content);
        Controls.Add(_rail);
        Controls.Add(_status);
        Theme.Apply(this);
        ResumeLayout(true);

        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        Navigate("Compress");
    }

    public CompressPage CompressPage => (CompressPage)GetPage("Compress");
    public ArchivesPage ArchivesPage => (ArchivesPage)GetPage("Archives");

    private Control GetPage(string name)
    {
        if (_pages.TryGetValue(name, out var p)) return p;
        p = name switch
        {
            "Compress" => new CompressPage(_app),
            "Jobs" => new JobsPage(_app),
            "Archives" => new ArchivesPage(_app),
            "Lab" => new LabPage(_app),
            "Rules" => new RulesPage(_app),
            "Cluster" => new ClusterPage(_app),
            "Settings" => new SettingsPage(_app),
            _ => new LogPage(),
        };
        p.Dock = DockStyle.Fill;
        p.Visible = false;
        Theme.Apply(p);
        _content.Controls.Add(p);
        _pages[name] = p;
        return p;
    }

    public void Navigate(string name)
    {
        if (!_buttons.ContainsKey(name)) name = "Compress";
        var page = GetPage(name);
        foreach (var (n, c) in _pages) c.Visible = n == name;
        foreach (var (n, b) in _buttons) b.Selected = n == name;
        CurrentPage = name;
        if (page is IPage ip) ip.Activated();
    }

    private void UpdateStatus()
    {
        var jobs = _app.Jobs.Snapshot();
        int running = jobs.Count(j => j.State is JobState.Running or JobState.Paused), queued = jobs.Count(j => j.State == JobState.Queued);
        var w = _app.LocalWorker?.Stats;
        _status.Text = $"{(running + queued == 0 ? "Idle" : $"{running} running, {queued} queued")}   ·   " +
                       $"Profile: {_app.Settings.Compression.Profile}   ·   " +
                       (w is not null ? $"Worker: {w.Connections} coordinator(s), {w.TasksDone} task(s) done"
                           : _app.WorkerError is not null ? "Worker: not running (see Cluster)" : "Worker: off") +
                       (Osmium.Desktop.Settings.AppPaths.Portable ? "   ·   portable mode" : "");
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Native.SetDarkTitleBar(Handle, Theme.IsDark); } catch (Exception) { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing && _app.Settings.General.CloseButton == CloseAction.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                AppLog.Info("window hidden; Osmium keeps running in the notification area");
            }
            return;
        }
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _app.ExitApp(confirm: true);
            return;
        }
        _statusTimer.Stop();
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        int idx = keyData switch
        {
            Keys.Control | Keys.D1 => 0, Keys.Control | Keys.D2 => 1, Keys.Control | Keys.D3 => 2, Keys.Control | Keys.D4 => 3,
            Keys.Control | Keys.D5 => 4, Keys.Control | Keys.D6 => 5, Keys.Control | Keys.D7 => 6, Keys.Control | Keys.D8 => 7, _ => -1,
        };
        if (idx >= 0) { Navigate(Nav[idx].Name); return true; }
        if (keyData == (Keys.Control | Keys.Oemcomma)) { Navigate("Settings"); return true; }
        if (keyData == (Keys.Control | Keys.Q)) { _app.ExitApp(confirm: true); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

internal interface IPage
{
    /// <summary>Called every time the page is shown.</summary>
    void Activated();

    /// <summary>The page holds edits that are not saved yet (asked about before Osmium exits).</summary>
    bool HasUnsavedChanges => false;
}

/// <summary>
/// Generic settings editor driven by <see cref="SettingDescriptor"/> metadata: one row per setting with label,
/// badges, wrapped description and a type-appropriate editor. Also used for codec parameters in the Tune dialog.
/// </summary>
internal sealed class SettingsRenderer : Panel
{
    private readonly FlowLayoutPanel _flow = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly List<(SettingDescriptor D, Control Row, Control Editor)> _rows = [];
    /// <summary>The error line under each editor, and the errors shown now (that input was not applied).</summary>
    private readonly Dictionary<SettingDescriptor, Label> _errorLabels = [];
    private readonly Dictionary<SettingDescriptor, string> _errors = [];
    private IReadOnlyList<SettingDescriptor> _all = [];
    private Func<bool> _experimentalEnabled = () => true;

    /// <summary>Raised after the user changed a setting's value (the descriptor's setter has run).</summary>
    public event Action<SettingDescriptor>? Changed;

    public SettingsRenderer()
    {
        AutoScroll = true;
        Controls.Add(_flow);
        Resize += (_, _) => { foreach (var (_, row, _) in _rows) row.Width = Math.Max(300, ClientSize.Width - 30); };
    }

    public void Render(IEnumerable<SettingDescriptor> items, IReadOnlyList<SettingDescriptor> all, Func<bool>? experimentalEnabled = null, string? banner = null)
    {
        _all = all;
        _experimentalEnabled = experimentalEnabled ?? (() => true);
        SuspendLayout();
        _flow.SuspendLayout();
        var old = _flow.Controls.Cast<Control>().ToList();
        _flow.Controls.Clear();
        foreach (var c in old) c.Dispose();
        _rows.Clear();
        _errorLabels.Clear();
        _errors.Clear();
        if (banner is not null)
        {
            var b = new Label { Text = banner, AutoSize = true, MaximumSize = new Size(Math.Max(300, ClientSize.Width - 40), 0), Padding = new Padding(8), Tag = "experimental", Margin = new Padding(0, 0, 0, 10) };
            _flow.Controls.Add(b);
        }
        string? group = null;
        foreach (var d in items)
        {
            if (d.Group is not null && d.Group != group)
            {
                group = d.Group;
                _flow.Controls.Add(new Label { Text = group, Font = Theme.Bold, AutoSize = true, Margin = new Padding(0, 12, 0, 4) });
            }
            var (row, editor) = BuildRow(d);
            _rows.Add((d, row, editor));
            _flow.Controls.Add(row);
        }
        Theme.Apply(this);
        UpdateStates();
        _flow.ResumeLayout(true);
        ResumeLayout(true);
    }

    private (Control Row, Control Editor) BuildRow(SettingDescriptor d)
    {
        var row = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = Math.Max(300, ClientSize.Width - 30), Margin = new Padding(0, 4, 0, 8) };
        var title = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        title.Controls.Add(new Label { Text = d.Label, AutoSize = true, Font = Theme.Bold, Margin = new Padding(0, 0, 6, 0) });
        if (d.Experimental) title.Controls.Add(new Label { Text = "EXPERIMENTAL", AutoSize = true, Font = Theme.Small, Tag = "experimental" });
        if (d.Dangerous) title.Controls.Add(new Label { Text = "CAUTION", AutoSize = true, Font = Theme.Small, Tag = "experimental" });
        if (d.Advanced) title.Controls.Add(new Label { Text = "advanced", AutoSize = true, Font = Theme.Small, Tag = "muted" });
        if (d.RequiresRestart) title.Controls.Add(new Label { Text = "restart required", AutoSize = true, Font = Theme.Small, Tag = "muted" });
        row.Controls.Add(title);
        if (!string.IsNullOrEmpty(d.Description))
            row.Controls.Add(new Label { Text = d.Description, AutoSize = true, MaximumSize = new Size(760, 0), Tag = "muted", Margin = new Padding(0, 1, 0, 3) });
        var error = new Label { AutoSize = true, Visible = false, MaximumSize = new Size(760, 0), Tag = "danger", Margin = new Padding(0, 3, 0, 0), UseMnemonic = false };
        _errorLabels[d] = error;
        var editor = BuildEditor(d);
        row.Controls.Add(editor);
        row.Controls.Add(error);
        return (row, editor);
    }

    /// <summary>Settings whose last input was rejected, as "label: reason". The rejected input was not applied.</summary>
    public IReadOnlyList<string> Errors => _errors.Select(kv => $"{kv.Key.Label}: {kv.Value}").ToList();

    private Control BuildEditor(SettingDescriptor d)
    {
        object? value = d.Get();
        var t = d.ValueType;
        if (t == typeof(bool))
        {
            var cb = new CheckBox { Text = "Enabled", AutoSize = true, Checked = value is true };
            cb.CheckedChanged += (_, _) =>
            {
                if (cb.Checked && d.WarningText is not null && !Ui.Confirm(d.WarningText + "\n\nContinue?")) { cb.Checked = false; return; }
                Commit(d, cb.Checked);
            };
            return cb;
        }
        if (t.IsEnum)
        {
            var combo = Ui.Combo(d.Choices ?? [], value?.ToString());
            combo.Width = 220;
            combo.SelectedIndexChanged += (_, _) => Commit(d, combo.SelectedItem?.ToString());
            return combo;
        }
        if (SettingDescriptor.IsNumeric(t))
        {
            var n = new NumericUpDown
            {
                Minimum = (decimal)(double.IsNaN(d.Min) ? -1_000_000_000 : d.Min),
                Maximum = (decimal)(double.IsNaN(d.Max) ? 1_000_000_000 : d.Max),
                Increment = (decimal)d.Step, Width = 160, ThousandsSeparator = true,
            };
            n.Value = Math.Clamp(Convert.ToDecimal(value ?? 0, CultureInfo.InvariantCulture), n.Minimum, n.Maximum);
            n.ValueChanged += (_, _) => Commit(d, t == typeof(int) ? (object)(int)n.Value : t == typeof(long) ? (long)n.Value : (double)n.Value);
            if (d.Unit is null) return n;
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            p.Controls.Add(n);
            p.Controls.Add(new Label { Text = d.Unit.Trim(), AutoSize = true, Padding = new Padding(0, 5, 0, 0), Tag = "muted" });
            return p;
        }
        if (t == typeof(List<string>))
        {
            var tb = new TextBox { Multiline = true, Width = 420, Height = 80, ScrollBars = ScrollBars.Vertical, Text = string.Join(Environment.NewLine, value as List<string> ?? []) };
            tb.TextChanged += (_, _) => Commit(d, tb.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
            return tb;
        }
        var text = new TextBox { Width = 420, Text = value?.ToString() ?? "", UseSystemPasswordChar = d.Editor == SettingEditor.Password };
        text.TextChanged += (_, _) => Commit(d, text.Text);
        if (d.Editor == SettingEditor.Password)
        {
            // Wide enough for a whole cluster key (70 characters), scaled for the monitor's DPI.
            text.Font = Theme.Mono;
            text.Width = text.LogicalToDeviceUnits(560);
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            p.Controls.Add(text);
            p.Controls.Add(Ui.RevealButton(text));
            return p;
        }
        if (d.Editor is SettingEditor.FolderPath or SettingEditor.FilePath)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            p.Controls.Add(text);
            p.Controls.Add(Ui.Button("Browse…", (_, _) =>
            {
                if (d.Editor == SettingEditor.FolderPath)
                {
                    using var f = new FolderBrowserDialog { SelectedPath = text.Text };
                    if (f.ShowDialog() == DialogResult.OK) text.Text = f.SelectedPath;
                }
                else
                {
                    using var f = new OpenFileDialog { FileName = text.Text };
                    if (f.ShowDialog() == DialogResult.OK) text.Text = f.FileName;
                }
            }));
            return p;
        }
        return text;
    }

    private void Commit(SettingDescriptor d, object? value)
    {
        string? err = d.Validate(value);
        if (err is null)
        {
            try { d.Set(value); }
            catch (Exception e) when (e is FormatException or InvalidCastException or ArgumentException or OverflowException) { err = e.Message; }
        }
        SetError(d, err);
        if (err is not null) return;
        UpdateStates();
        Changed?.Invoke(d);
    }

    /// <summary>Shows (or clears) the error line under a setting's editor.</summary>
    private void SetError(SettingDescriptor d, string? message)
    {
        if (message is null) _errors.Remove(d);
        else _errors[d] = message;
        if (!_errorLabels.TryGetValue(d, out var label)) return;
        label.Text = message ?? "";
        label.Visible = message is not null;
    }

    private void UpdateStates()
    {
        bool exp = _experimentalEnabled();
        foreach (var (d, row, editor) in _rows)
        {
            row.Visible = SettingReflector.Evaluate(d.VisibleWhen, _all, d.Category);
            editor.Enabled = SettingReflector.Evaluate(d.EnabledWhen, _all, d.Category) && (!d.Experimental || exp);
        }
    }
}
