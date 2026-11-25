using Osmium.Desktop.Settings;

namespace Osmium.App;

internal sealed class SettingsPage : UserControl, IPage
{
    private readonly TrayApp _app;
    private readonly ListBox _categories = new() { Dock = DockStyle.Left, Width = 190, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly EditBox _search = new() { Width = 260, PlaceholderText = "Search settings" };
    private readonly SettingsRenderer _renderer = new() { Dock = DockStyle.Fill };
    private readonly Panel _watchPanel = new() { Dock = DockStyle.Bottom, Height = 0 };
    /// <summary>Copy of the settings being edited. Only the entries in <see cref="_edited"/> are saved.</summary>
    private OsmiumSettings _working;
    /// <summary>Keys of the settings the user changed since the last Apply or Revert (see <see cref="SettingsEdits"/>).</summary>
    private readonly HashSet<string> _edited = new(StringComparer.Ordinal);
    private bool _applying;
    private List<Osmium.Desktop.Settings.SettingDescriptor> _all = [];
    private readonly Label _dirty = Ui.Text("", muted: true);
    /// <summary>Re-renders once typing in the search box pauses, not on every keystroke.</summary>
    private readonly System.Windows.Forms.Timer _searchDelay = new() { Interval = 250 };

    public SettingsPage(TrayApp app)
    {
        _app = app;
        _working = app.Settings.Clone();
        foreach (var c in SettingReflector.Categories(_working)) _categories.Items.Add(c.Name);
        _categories.SelectedIndexChanged += (_, _) => { _search.Text = ""; RenderNow(); };
        _searchDelay.Tick += (_, _) => RenderNow();
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _search.EnterAction = RenderNow;
        Disposed += (_, _) => _searchDelay.Dispose();
        _renderer.Changed += d => MarkEdited(d.Key);
        var tools = Ui.Bar(_search,
            Ui.Button("Apply", (_, _) => Apply(), accent: true),
            Ui.Button("Revert", (_, _) => Revert()),
            Ui.Button("Restore defaults", (_, _) => RestoreDefaults()),
            Ui.Button("Repair Explorer integration", (_, _) => Repair()),
            Ui.Button("About Osmium…", (_, _) => _app.ShowAbout()),
            _dirty);
        var body = new Panel();
        body.Controls.Add(_renderer);
        body.Controls.Add(_watchPanel);
        body.Controls.Add(_categories);
        Controls.Add(Ui.Page("Settings", "Every option Osmium has. Settings marked EXPERIMENTAL need the master switch on the Experimental page.", body, tools));
        _categories.SelectedIndex = 0;
        app.SettingsChanged += OnSettingsChanged;
        Disposed += (_, _) => app.SettingsChanged -= OnSettingsChanged;
    }

    public bool HasUnsavedChanges => _edited.Count > 0;

    public void Activated() => RefreshFromCurrent();

    /// <summary>Settings were saved elsewhere (tray, Cluster page): show them, unless the page holds unsaved edits.
    /// Those are kept, and Apply merges them onto whatever is current then.</summary>
    private void OnSettingsChanged()
    {
        if (!_applying && !IsDisposed) RefreshFromCurrent();
    }

    private void RefreshFromCurrent()
    {
        if (_edited.Count > 0) return;
        _working = _app.Settings.Clone();
        Render();
    }

    private void MarkEdited(string key)
    {
        _edited.Add(key);
        _dirty.Text = "Unsaved changes";
    }

    public void ShowCategory(string name)
    {
        int i = _categories.Items.IndexOf(name);
        if (i >= 0) _categories.SelectedIndex = i;
    }

    public IEnumerable<string> CategoryNames => _categories.Items.Cast<string>();

    private void RenderNow()
    {
        _searchDelay.Stop();
        Render();
    }

    private void Render()
    {
        _all = SettingReflector.Describe(_working);
        string cat = _categories.SelectedItem as string ?? "General";
        var items = string.IsNullOrWhiteSpace(_search.Text)
            ? _all.Where(d => d.Category == cat)
            : _all.Where(d => d.Matches(_search.Text));
        string? banner = cat == "Experimental" && string.IsNullOrWhiteSpace(_search.Text)
            ? "⚠ Experimental features may take days, use many GB of RAM, or produce archives that need this exact Osmium version's experimental support. Archives made with them are flagged."
            : null;
        _renderer.Render(items, _all, () => _working.Experimental.Enabled, banner);
        BuildWatchPanel(cat == "Watch folders" && string.IsNullOrWhiteSpace(_search.Text));
    }

    private void BuildWatchPanel(bool show)
    {
        var old = _watchPanel.Controls.Cast<Control>().ToList();
        _watchPanel.Controls.Clear();
        foreach (var c in old) c.Dispose();
        _watchPanel.Height = show ? 220 : 0;
        if (!show) return;
        var list = Ui.List(("Folder", 360), ("Profile", 90), ("Settle (s)", 80), ("On", 40));
        list.Dock = DockStyle.Fill;
        void Fill()
        {
            list.Items.Clear();
            foreach (var f in _working.Watch.Folders)
                list.Items.Add(new ListViewItem([f.Path, f.Profile.ToString(), f.SettleSeconds.ToString(), f.Enabled ? "✓" : ""]) { Tag = f });
        }
        var bar = Ui.Bar(
            Ui.Button("Add folder…", (_, _) =>
            {
                using var d = new FolderBrowserDialog();
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _working.Watch.Folders.Add(new WatchFolderConfig { Path = d.SelectedPath, Profile = _working.Compression.Profile });
                Fill();
                MarkEdited(SettingsEdits.WatchFolders);
            }),
            Ui.Button("Remove", (_, _) =>
            {
                if (list.SelectedItems.Count == 0) return;
                foreach (ListViewItem i in list.SelectedItems) _working.Watch.Folders.Remove((WatchFolderConfig)i.Tag!);
                Fill();
                MarkEdited(SettingsEdits.WatchFolders);
            }),
            Ui.Button("Toggle", (_, _) =>
            {
                if (list.SelectedItems.Count == 0) return;
                foreach (ListViewItem i in list.SelectedItems) { var f = (WatchFolderConfig)i.Tag!; f.Enabled = !f.Enabled; }
                Fill();
                MarkEdited(SettingsEdits.WatchFolders);
            }),
            Ui.Text("New files are compressed to an \"_osmium\" subfolder once they stop changing. The originals are kept unless "
                + "Compression → \"After a verified compression, move the originals to the Recycle Bin\" is on.", muted: true, maxWidth: 760));
        _watchPanel.Controls.Add(list);
        _watchPanel.Controls.Add(bar);
        Theme.Apply(_watchPanel);
        Fill();
    }

    /// <summary>Saves the edited settings on top of the current ones, so changes made elsewhere since the page
    /// was shown (tray toggles, a key saved on the Cluster page) are kept.</summary>
    private void Apply()
    {
        if (_renderer.Errors is { Count: > 0 } errors)
        {
            Ui.Error("Correct these settings first (they show the problem below the field):\n\n" + string.Join("\n", errors), "Settings not saved");
            return;
        }
        var merged = SettingsEdits.Apply(_app.Settings, _working, _edited);
        _applying = true;
        try { _app.SaveSettings(merged); }
        finally { _applying = false; }
        _edited.Clear();
        _renderer.ClearRejected();
        _working = _app.Settings.Clone();
        _dirty.Text = "Saved";
        Render();
    }

    private void Revert()
    {
        _edited.Clear();
        _renderer.ClearRejected();
        _working = _app.Settings.Clone();
        Render();
        _dirty.Text = "";
    }

    private void RestoreDefaults()
    {
        string cat = _categories.SelectedItem as string ?? "General";
        var keys = SettingsEdits.KeysOf(cat);
        string question = $"Restore the defaults for \"{cat}\"?";
        if (keys.Contains("Cluster.ClusterKey") && !string.IsNullOrWhiteSpace(_working.Cluster.ClusterKey))
            question += "\n\nThis also removes the cluster key when you click Apply. Your other PCs will then need a new key, or you will have to enter this one again.";
        if (keys.Contains(SettingsEdits.WatchFolders) && _working.Watch.Folders.Count > 0)
            question += $"\n\nThis also removes the {_working.Watch.Folders.Count} watched folder(s) from the list when you click Apply.";
        if (!Ui.Confirm(question)) return;
        var defaults = new OsmiumSettings();
        var prop = typeof(OsmiumSettings).GetProperties().FirstOrDefault(p =>
            p.PropertyType.GetCustomAttributes(typeof(SettingsCategoryAttribute), false).OfType<SettingsCategoryAttribute>().FirstOrDefault()?.Name == cat);
        if (prop is null) return;
        prop.SetValue(_working, prop.GetValue(defaults));
        _renderer.ClearRejected(cat);
        foreach (var key in keys) _edited.Add(key);
        Render();
        _dirty.Text = "Unsaved changes";
    }

    /// <summary>Re-applies the saved integration settings (unsaved edits on this page are not used).</summary>
    private void Repair()
    {
        if (_app.ApplyIntegration(_app.Settings)) Ui.Info("Explorer integration updated for your account.");
        else Ui.Error("Could not update the registry; see the Log page.");
    }
}
