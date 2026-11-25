using Osmium.Core.Analysis;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;
using Osmium.Desktop.Ipc;
using Osmium.Desktop.Jobs;
using Osmium.Desktop.Settings;

namespace Osmium.App;

internal sealed class CompressPage : UserControl, IPage
{
    private readonly TrayApp _app;
    private readonly FastList _list = Ui.List(("Name", 240), ("Folder", 220), ("Size", 90), ("Detected type", 170), ("Plan", 320));
    private readonly ComboBox _profile;
    private readonly EditBox _output = new() { Width = 520 };
    private readonly CheckBox _encrypt = new() { Text = "Encrypt (AES-256, names hidden)", AutoSize = true };
    private readonly Label _estimate = Ui.Text("", muted: true);
    /// <summary>Per-input manual patches: full path → pipeline text, "store", or "exclude".</summary>
    private readonly Dictionary<string, string> _patches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileAnalysis> _analysis = new(StringComparer.OrdinalIgnoreCase);

    public CompressPage(TrayApp app)
    {
        _app = app;
        _profile = Ui.Combo(Profile.All.Where(p => !p.Experimental || app.Settings.Experimental is { Enabled: true, Exhaustive: true }).Select(p => p.Name),
            app.Settings.Compression.Profile.ToString().ToLowerInvariant());
        _profile.SelectedIndexChanged += (_, _) => RefreshPlans();
        _encrypt.Checked = app.Settings.Security.EncryptByDefault;
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddPaths(files); };
        _list.AllowDrop = true;
        _list.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        _list.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddPaths(files); };
        _list.KeyDown += (_, e) => { if (e.KeyData == Keys.Delete) RemoveSelected(); };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Tune…", null, (_, _) => Tune(deep: false));
        menu.Items.Add("Deep auto-tune…", null, (_, _) => Tune(deep: true));
        menu.Items.Add("Store as-is", null, (_, _) => Patch("store"));
        menu.Items.Add("Exclude", null, (_, _) => Patch("exclude"));
        menu.Items.Add("Clear patch", null, (_, _) => Patch(null));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Save patch as global rule", null, (_, _) => SavePatchAsRule());
        menu.Items.Add("Open containing folder", null, (_, _) => { if (Selected().FirstOrDefault() is { } p) Shell.Reveal(p); });
        _list.ContextMenuStrip = menu;
        _list.DefaultAction = () => Tune(deep: false); // Enter or double-click

        var tools = Ui.Bar(
            Ui.Button("Add files…", (_, _) => BrowseFiles()),
            Ui.Button("Add folder…", (_, _) => BrowseFolder()),
            Ui.Button("Remove", (_, _) => RemoveSelected()),
            Ui.Button("Clear", (_, _) => { _list.Items.Clear(); _patches.Clear(); UpdateOutputName(); }),
            new Label { Text = "Profile:", AutoSize = true, Padding = new Padding(12, 6, 0, 0) }, _profile,
            Ui.Button("Analyze", (_, _) => _ = AnalyzeAsync()));

        var bottom = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Bottom, Padding = new Padding(0, 8, 0, 0) };
        var outRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        outRow.Controls.Add(new Label { Text = "Archive:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        outRow.Controls.Add(_output);
        outRow.Controls.Add(Ui.Button("Browse…", (_, _) =>
        {
            using var d = new SaveFileDialog { Filter = "Osmium archive (*.osa)|*.osa", FileName = Path.GetFileName(_output.Text), InitialDirectory = SafeDir(_output.Text) };
            if (d.ShowDialog(this) == DialogResult.OK) _output.Text = d.FileName;
        }));
        bottom.Controls.Add(outRow);
        bottom.Controls.Add(_encrypt);
        bottom.Controls.Add(_estimate);
        bottom.Controls.Add(Ui.Bar(Ui.Button("Compress", (_, _) => StartCompression(), accent: true)));

        var body = new Panel();
        body.Controls.Add(_list);
        _list.Dock = DockStyle.Fill;
        body.Controls.Add(bottom);
        Controls.Add(Ui.Page("Compress", "Drop files or folders here. Right-click a file to tune its pipeline by hand (a \"patch\") or let Osmium search for the best one.", body, tools));
    }

    public void Activated() { }

    /// <summary>Ctrl+V adds files and folders copied in Explorer; in the archive box it pastes text as usual.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.V) && ActiveControl is not TextBoxBase && PasteFiles()) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Adds the files and folders on the clipboard; false when it holds none.</summary>
    internal bool PasteFiles()
    {
        List<string> paths;
        try
        {
            if (!Clipboard.ContainsFileDropList()) return false;
            paths = Clipboard.GetFileDropList().Cast<string>().Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        }
        catch (System.Runtime.InteropServices.ExternalException) { return false; } // another program holds the clipboard
        if (paths.Count == 0) return false;
        AddPaths(paths);
        return true;
    }

    private static string SafeDir(string p) { try { return Path.GetDirectoryName(p) ?? ""; } catch (ArgumentException) { return ""; } }

    public void BrowseFiles()
    {
        using var d = new OpenFileDialog { Multiselect = true, Title = "Add files" };
        if (d.ShowDialog(this) == DialogResult.OK) AddPaths(d.FileNames);
    }

    public void BrowseFolder()
    {
        using var d = new FolderBrowserDialog { Description = "Add a folder" };
        if (d.ShowDialog(this) == DialogResult.OK) AddPaths([d.SelectedPath]);
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        var existing = _list.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _list.BeginUpdate();
        foreach (var raw in paths)
        {
            var p = Path.GetFullPath(raw);
            if (!existing.Add(p)) continue;
            bool dir = Directory.Exists(p);
            long size = dir ? 0 : new FileInfo(p).Length;
            var item = new ListViewItem([Path.GetFileName(p), Path.GetDirectoryName(p) ?? "", dir ? "folder" : Ui.Bytes(size), dir ? "folder" : "", ""]) { Tag = p };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        UpdateOutputName();
        RefreshPlans();
    }

    private void UpdateOutputName()
    {
        var paths = _list.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();
        if (paths.Count == 0) { _output.Text = ""; return; }
        var name = InvocationCoalescer.ArchiveNameFor(paths);
        if (_app.Settings.General.Output == OutputLocation.Folder && Directory.Exists(_app.Settings.General.OutputFolder))
            name = Path.Combine(_app.Settings.General.OutputFolder, Path.GetFileName(name));
        _output.Text = name;
    }

    private List<string> Selected() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();

    private void RemoveSelected()
    {
        foreach (ListViewItem i in _list.SelectedItems) { _patches.Remove((string)i.Tag!); _list.Items.Remove(i); }
        UpdateOutputName();
    }

    private void Patch(string? value)
    {
        foreach (var p in Selected())
        {
            if (value is null) _patches.Remove(p); else _patches[p] = value;
        }
        RefreshPlans();
    }

    private void Tune(bool deep)
    {
        var p = Selected().FirstOrDefault();
        if (p is null || Directory.Exists(p)) { Ui.Info("Select a single file to tune (folders can be patched with Store/Exclude)."); return; }
        using var d = new TuneDialog(_app, p, _patches.GetValueOrDefault(p), _profile.SelectedItem?.ToString() ?? "insane", deep);
        if (d.ShowDialog(this) == DialogResult.OK && d.Pipeline is not null)
        {
            _patches[p] = d.Pipeline;
            RefreshPlans();
        }
    }

    private void SavePatchAsRule()
    {
        var p = Selected().FirstOrDefault();
        if (p is null || !_patches.TryGetValue(p, out var patch)) { Ui.Info("Select a patched file first."); return; }
        var rs = File.Exists(AppPaths.RulesFile) ? RuleSet.Load(AppPaths.RulesFile) : new RuleSet();
        rs.Rules.Add(MakeRule(p, patch, global: true));
        rs.Save(AppPaths.RulesFile);
        _app.LoadRules();
        Ui.Info($"Saved a rule for **/{Path.GetFileName(p)} to rules.json.");
    }

    private static Rule MakeRule(string fullPath, string patch, bool global)
    {
        bool dir = Directory.Exists(fullPath);
        var match = new RuleMatch();
        if (global) match.Globs = [dir ? $"**/{Path.GetFileName(fullPath)}/**" : $"**/{Path.GetFileName(fullPath)}"];
        else if (dir) match.Globs = [$"{Path.GetFileName(fullPath)}/**"];
        else match.Path = Path.GetFileName(fullPath);
        var actions = patch switch
        {
            "store" => new RuleActions { Store = true },
            "exclude" => new RuleActions { Exclude = true },
            _ => new RuleActions { Pipeline = patch, Solid = "own" },
        };
        return new Rule
        {
            Id = (global ? "patch-" : "job-") + Path.GetFileName(fullPath).ToLowerInvariant(),
            Description = "Manual patch from the Compress page",
            Match = match, Actions = actions,
            Origin = new RuleOrigin { Type = "manual", At = DateTime.UtcNow },
        };
    }

    private void RefreshPlans()
    {
        var profile = Profile.ByName(_profile.SelectedItem?.ToString() ?? "balanced");
        foreach (ListViewItem i in _list.Items)
        {
            var p = (string)i.Tag!;
            string plan;
            if (_patches.TryGetValue(p, out var patch)) plan = "✎ patched: " + patch;
            else if (_analysis.TryGetValue(p, out var a))
                plan = profile.Rungs.Length == 0
                    ? "auto: " + Candidates.Default(a.Class, a, profile, MethodRegistry.Default)
                    : $"auto: trials among {Math.Min(profile.MaxCandidates, Candidates.For(a.Class, a, profile, MethodRegistry.Default).Count)} pipelines";
            else plan = "auto";
            i.SubItems[4].Text = plan;
        }
        _estimate.Text = profile.Description;
    }

    /// <summary>The Analyze button: detects each file's type and shows the plan. Completes when the list shows it.</summary>
    internal Task AnalyzeAsync()
    {
        var files = _list.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!).Where(File.Exists).ToList();
        return Ui.RunSafeAsync(this, async () =>
        {
            var results = await Task.Run(() => files.AsParallel().Select(f => (f, a: Sniffer.Analyze(f, 16))).ToList());
            foreach (var (f, a) in results) _analysis[f] = a;
            foreach (ListViewItem i in _list.Items)
                if (_analysis.TryGetValue((string)i.Tag!, out var a)) i.SubItems[3].Text = $"{a.Format} → {a.Class}";
            RefreshPlans();
        });
    }

    /// <summary>Selects a profile in the Profile list (as if picked there).</summary>
    internal void SelectProfile(string name)
    {
        if (_profile.Items.Contains(name)) _profile.SelectedItem = name;
    }

    /// <summary>The Compress button: queues the job and shows the Jobs page. Null when nothing was started.</summary>
    internal Guid? StartCompression()
    {
        var paths = _list.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();
        if (paths.Count == 0) { Ui.Info("Add some files or folders first."); return null; }
        var output = _output.Text.Trim();
        if (output.Length == 0) { Ui.Error("Choose where to save the archive."); return null; }
        if (_app.Settings.General.Output == OutputLocation.Ask)
        {
            using var d = new SaveFileDialog { Filter = "Osmium archive (*.osa)|*.osa", FileName = Path.GetFileName(output), InitialDirectory = SafeDir(output) };
            if (d.ShowDialog(this) != DialogResult.OK) return null;
            output = d.FileName;
        }
        if (!output.EndsWith(".osa", StringComparison.OrdinalIgnoreCase)) output += ".osa";
        bool overwrite = false;
        if (File.Exists(output))
        {
            if (!Ui.Confirm($"{output} already exists. Replace it (only after the new archive is verified)?")) return null;
            overwrite = true;
        }
        string? password = null;
        if (_encrypt.Checked)
        {
            password = PasswordDialog.Ask("Password for the new archive", confirm: true);
            if (password is null) return null;
        }
        var profile = _profile.SelectedItem?.ToString() ?? "balanced";
        if (profile is "insane" or "exhaustive" or "ultra" && _app.Settings.Codecs.AllowCm)
            AppLog.Info($"{profile}: context mixing may be chosen — extraction will be roughly as slow as compression.");
        var id = _app.Jobs.Enqueue(new JobRequest
        {
            Kind = JobKind.Compress, Inputs = paths, Output = output, Profile = profile, Password = password, Overwrite = overwrite,
            Patches = _patches.Select(kv => MakeRule(kv.Key, kv.Value, global: false)).ToList(),
        });
        _app.Main?.Navigate("Jobs");
        return id;
    }
}
