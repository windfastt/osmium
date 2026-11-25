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

    /// <summary>A page by its navigation name, created if needed.</summary>
    internal T Page<T>(string name) where T : Control => (T)GetPage(name);

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
        // Keyboard users continue in the page (its first control), not on the navigation button.
        if (Visible && !page.ContainsFocus) page.SelectNextControl(null, forward: true, tabStopOnly: true, nested: true, wrap: false);
    }

    /// <summary>Names of the pages that hold unsaved edits, in navigation order.</summary>
    public IReadOnlyList<string> PagesWithUnsavedChanges() =>
        Nav.Select(n => n.Name).Where(n => _pages.TryGetValue(n, out var p) && p is IPage ip && ip.HasUnsavedChanges).ToList();

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
        // Ctrl+1..8 with the top-row digits or the numeric keypad
        int idx = keyData switch
        {
            Keys.Control | Keys.D1 or Keys.Control | Keys.NumPad1 => 0, Keys.Control | Keys.D2 or Keys.Control | Keys.NumPad2 => 1,
            Keys.Control | Keys.D3 or Keys.Control | Keys.NumPad3 => 2, Keys.Control | Keys.D4 or Keys.Control | Keys.NumPad4 => 3,
            Keys.Control | Keys.D5 or Keys.Control | Keys.NumPad5 => 4, Keys.Control | Keys.D6 or Keys.Control | Keys.NumPad6 => 5,
            Keys.Control | Keys.D7 or Keys.Control | Keys.NumPad7 => 6, Keys.Control | Keys.D8 or Keys.Control | Keys.NumPad8 => 7,
            _ => -1,
        };
        if (idx >= 0) { Navigate(Nav[idx].Name); return true; }
        if (keyData == (Keys.Control | Keys.Oemcomma)) { Navigate("Settings"); return true; }
        // Exit asks first when jobs run or a page holds unsaved edits (see TrayApp.ExitApp).
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
