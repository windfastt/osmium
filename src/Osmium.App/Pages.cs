using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Osmium.Core.Analysis;
using Osmium.Core.Container;
using Osmium.Core.Distributed;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;
using Osmium.Desktop.Ipc;
using Osmium.Desktop.Jobs;
using Osmium.Desktop.Settings;

namespace Osmium.App;

// ============================================================================ Compress

internal sealed class CompressPage : UserControl, IPage
{
    private readonly TrayApp _app;
    private readonly FastList _list = Ui.List(("Name", 240), ("Folder", 220), ("Size", 90), ("Detected type", 170), ("Plan", 320));
    private readonly ComboBox _profile;
    private readonly TextBox _output = new() { Width = 520 };
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
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

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
        _list.DoubleClick += (_, _) => Tune(deep: false);

        var tools = Ui.Bar(
            Ui.Button("Add files…", (_, _) => BrowseFiles()),
            Ui.Button("Add folder…", (_, _) => BrowseFolder()),
            Ui.Button("Remove", (_, _) => RemoveSelected()),
            Ui.Button("Clear", (_, _) => { _list.Items.Clear(); _patches.Clear(); UpdateOutputName(); }),
            new Label { Text = "Profile:", AutoSize = true, Padding = new Padding(12, 6, 0, 0) }, _profile,
            Ui.Button("Analyze", (_, _) => Analyze()));

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
        bottom.Controls.Add(Ui.Bar(Ui.Button("Compress", (_, _) => Start(), accent: true)));

        var body = new Panel();
        body.Controls.Add(_list);
        _list.Dock = DockStyle.Fill;
        body.Controls.Add(bottom);
        Controls.Add(Ui.Page("Compress", "Drop files or folders here. Right-click a file to tune its pipeline by hand (a \"patch\") or let Osmium search for the best one.", body, tools));
    }

    public void Activated() { }

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

    private void Analyze()
    {
        var files = _list.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!).Where(File.Exists).ToList();
        Ui.RunSafe(this, async () =>
        {
            var results = await Task.Run(() => files.AsParallel().Select(f => (f, a: Sniffer.Analyze(f, 16))).ToList());
            foreach (var (f, a) in results) _analysis[f] = a;
            foreach (ListViewItem i in _list.Items)
                if (_analysis.TryGetValue((string)i.Tag!, out var a)) i.SubItems[3].Text = $"{a.Format} → {a.Class}";
            RefreshPlans();
        });
    }

    private void Start()
    {
        var paths = _list.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();
        if (paths.Count == 0) { Ui.Info("Add some files or folders first."); return; }
        var output = _output.Text.Trim();
        if (output.Length == 0) { Ui.Error("Choose where to save the archive."); return; }
        if (_app.Settings.General.Output == OutputLocation.Ask)
        {
            using var d = new SaveFileDialog { Filter = "Osmium archive (*.osa)|*.osa", FileName = Path.GetFileName(output), InitialDirectory = SafeDir(output) };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            output = d.FileName;
        }
        if (!output.EndsWith(".osa", StringComparison.OrdinalIgnoreCase)) output += ".osa";
        bool overwrite = false;
        if (File.Exists(output))
        {
            if (!Ui.Confirm($"{output} already exists. Replace it (only after the new archive is verified)?")) return;
            overwrite = true;
        }
        string? password = null;
        if (_encrypt.Checked)
        {
            password = PasswordDialog.Ask("Password for the new archive", confirm: true);
            if (password is null) return;
        }
        var profile = _profile.SelectedItem?.ToString() ?? "balanced";
        if (profile is "insane" or "exhaustive" or "ultra" && _app.Settings.Codecs.AllowCm)
            AppLog.Info($"{profile}: context mixing may be chosen — extraction will be roughly as slow as compression.");
        _app.Jobs.Enqueue(new JobRequest
        {
            Kind = JobKind.Compress, Inputs = paths, Output = output, Profile = profile, Password = password, Overwrite = overwrite,
            Patches = _patches.Select(kv => MakeRule(kv.Key, kv.Value, global: false)).ToList(),
        });
        _app.Main?.Navigate("Jobs");
    }
}

// ============================================================================ Jobs

internal sealed class JobsPage : UserControl, IPage
{
    private readonly TrayApp _app;
    private readonly FastList _list = Ui.List(("Name", 220), ("Kind", 70), ("State", 80), ("Progress", 170), ("Input", 80), ("Output", 80), ("Ratio", 60), ("Elapsed", 70), ("ETA", 70));
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Height = 170, Dock = DockStyle.Bottom };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    public JobsPage(TrayApp app)
    {
        _app = app;
        _log.Font = Theme.Mono;
        var tools = Ui.Bar(
            Ui.Button("Pause", (_, _) => Each(app.Jobs.Pause)),
            Ui.Button("Resume", (_, _) => Each(app.Jobs.Resume)),
            Ui.Button("Cancel", (_, _) => Each(app.Jobs.Cancel)),
            Ui.Button("Show output", (_, _) => { if (SelectedSnapshot()?.Output is { } o) Shell.Reveal(o); }),
            Ui.Button("Clear finished", (_, _) => { app.Jobs.ClearFinished(); RefreshList(); }));
        var body = new Panel();
        _list.Dock = DockStyle.Fill;
        body.Controls.Add(_list);
        body.Controls.Add(_log);
        _list.SelectedIndexChanged += (_, _) => ShowLog();
        Controls.Add(Ui.Page("Jobs", "Queued, running and finished jobs. Jobs run one at a time; each uses all configured threads.", body, tools));
        _timer.Tick += (_, _) => { if (Visible) RefreshList(); };
        _timer.Start();
    }

    public void Activated() => RefreshList();

    private void Each(Action<Guid> a) { foreach (ListViewItem i in _list.SelectedItems) a((Guid)i.Tag!); }

    private JobSnapshot? SelectedSnapshot()
    {
        if (_list.SelectedItems.Count == 0) return null;
        var id = (Guid)_list.SelectedItems[0].Tag!;
        return _app.Jobs.Snapshot().FirstOrDefault(j => j.Id == id);
    }

    private void RefreshList()
    {
        var jobs = _app.Jobs.Snapshot().OrderByDescending(j => j.Created).ToList();
        _list.BeginUpdate();
        var byId = _list.Items.Cast<ListViewItem>().ToDictionary(i => (Guid)i.Tag!);
        foreach (var j in jobs)
        {
            string[] cols =
            [
                j.Name, j.Kind.ToString(), j.State.ToString(),
                j.State is JobState.Running or JobState.Paused ? $"{j.Fraction:P0} {j.Phase}" : j.Error ?? j.Phase,
                Ui.Bytes(j.BytesIn), Ui.Bytes(j.BytesOut), j.BytesIn > 0 ? $"{j.Ratio:P1}" : "",
                Osmium.Desktop.Tray.TrayText.Duration(j.Elapsed), j.Eta is { } e ? Osmium.Desktop.Tray.TrayText.Duration(e) : "",
            ];
            if (!byId.Remove(j.Id, out var item))
            {
                item = new ListViewItem(cols) { Tag = j.Id };
                _list.Items.Add(item);
            }
            else for (int c = 0; c < cols.Length; c++) if (item.SubItems[c].Text != cols[c]) item.SubItems[c].Text = cols[c];
            item.ForeColor = j.State == JobState.Failed ? Theme.P.Danger : Theme.P.Text;
        }
        foreach (var stale in byId.Values) _list.Items.Remove(stale);
        _list.EndUpdate();
        ShowLog();
    }

    private void ShowLog()
    {
        var j = SelectedSnapshot();
        var text = j is null ? "" : string.Join(Environment.NewLine, _app.Jobs.GetLog(j.Id).TakeLast(400));
        if (_log.Text != text) { _log.Text = text; _log.SelectionStart = _log.TextLength; _log.ScrollToCaret(); }
    }
}

// ============================================================================ Archives

internal sealed class ArchivesPage : UserControl, IPage
{
    private readonly TrayApp _app;
    private readonly Label _path = Ui.Text("No archive open.", muted: true);
    private readonly TextBox _info = new() { Multiline = true, ReadOnly = true, Height = 150, Dock = DockStyle.Top, ScrollBars = ScrollBars.Vertical };
    private readonly FastList _list = Ui.List(("Path", 420), ("Size", 100), ("Modified", 140), ("Pipeline", 380));
    private string? _archive;
    private string? _password;

    public ArchivesPage(TrayApp app)
    {
        _app = app;
        _info.Font = Theme.Mono;
        var tools = Ui.Bar(
            Ui.Button("Open…", (_, _) => Browse()),
            Ui.Button("Extract all…", (_, _) => Extract(all: true)),
            Ui.Button("Extract selected…", (_, _) => Extract(all: false)),
            Ui.Button("Test", (_, _) => { if (_archive is not null) app.Jobs.Enqueue(new JobRequest { Kind = JobKind.Test, Inputs = [_archive], Password = _password }); }),
            Ui.Button("Explain", (_, _) => Explain()));
        var body = new Panel();
        _list.Dock = DockStyle.Fill;
        body.Controls.Add(_list);
        body.Controls.Add(_info);
        Controls.Add(Ui.Page("Archives", "Browse, test and extract .osa archives. Every extracted file is verified against its stored hash.", body, tools, _path));
    }

    public void Activated() { }

    public void Browse()
    {
        using var d = new OpenFileDialog { Filter = "Osmium archive (*.osa)|*.osa|All files|*.*" };
        if (d.ShowDialog(this) == DialogResult.OK) OpenArchive(d.FileName);
    }

    public void OpenArchive(string path)
    {
        try
        {
            string? pw = null;
            using var r = OsaReader.Open(path, () => pw = _app.AskPassword(path));
            _archive = path;
            _password = pw;
            _path.Text = path;
            var sb = new StringBuilder();
            sb.AppendLine($"Created by {r.Header.Creator}, profile {r.Index.MetaString("profile") ?? "?"}{(r.Header.Encrypted ? ", encrypted" : "")}{(r.Header.Experimental ? ", EXPERIMENTAL features" : "")}");
            sb.AppendLine($"{r.Entries.Count(e => !e.IsDirectory):N0} files, {Ui.Bytes(r.TotalUnpacked)} → {Ui.Bytes(r.FileLength)} ({(double)r.FileLength / Math.Max(1, r.TotalUnpacked):P2})");
            sb.AppendLine($"Extraction: ~{Osmium.Desktop.Tray.TrayText.Duration(TimeSpan.FromSeconds(r.EstimatedDecodeSeconds))} single-core, up to {Ui.Bytes(r.MaxBlockDecodeMemory)} RAM per block");
            foreach (var b in r.Blocks.Take(30)) sb.AppendLine($"  block {b.Frame}: {Ui.Bytes(b.RawSize)} → {Ui.Bytes(b.PackedSize)}  {b.Descriptor.Spec}");
            if (r.Blocks.Count > 30) sb.AppendLine($"  … {r.Blocks.Count - 30} more blocks");
            _info.Text = sb.ToString();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var e in r.Entries)
            {
                string spec = e.Segments.Count > 0 ? r.Blocks[e.Segments[0].Frame].Descriptor.Spec.ToString() : "";
                _list.Items.Add(new ListViewItem([e.Path + (e.IsDirectory ? "/" : ""), e.IsDirectory ? "" : Ui.Bytes(e.Size), e.ModifiedUtc?.ToLocalTime().ToString("g") ?? "", spec]) { Tag = e.Path });
            }
            _list.EndUpdate();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or WrongPasswordException or PasswordRequiredException)
        {
            if (e is WrongPasswordException) _app.ForgetPassword(path);
            Ui.Error(e.Message, "Cannot open archive");
        }
    }

    private void Extract(bool all)
    {
        if (_archive is null) { Browse(); return; }
        using var d = new FolderBrowserDialog { Description = "Extract to", SelectedPath = Path.GetDirectoryName(_archive) ?? "" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        if (!all && _list.SelectedItems.Count > 0)
        {
            // selected-entry extraction runs directly (small, interactive)
            var sel = _list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToHashSet();
            var archive = _archive;
            var pw = _password;
            Ui.RunSafe(this, async () =>
            {
                var res = await Task.Run(() =>
                {
                    using var r = OsaReader.Open(archive, () => pw);
                    return Extractor.Extract(r, d.SelectedPath, e => sel.Contains(e.Path) || sel.Any(s => e.Path.StartsWith(s + "/", StringComparison.Ordinal)), null, null, CancellationToken.None);
                });
                Ui.Info($"Extracted {res.FilesOk} file(s){(res.Errors.Count > 0 ? "\n" + string.Join("\n", res.Errors.Take(10)) : "")}");
            });
            return;
        }
        _app.Jobs.Enqueue(new JobRequest { Kind = JobKind.Extract, Inputs = [_archive], Output = d.SelectedPath, Password = _password });
        _app.Main?.Navigate("Jobs");
    }

    private void Explain()
    {
        if (_archive is null) return;
        try
        {
            using var r = OsaReader.Open(_archive, () => _password);
            if (!r.Index.Metadata.TryGetValue("plan", out var json)) { Ui.Info("This archive has no planner log."); return; }
            _info.Text = string.Join(Environment.NewLine, JsonSerializer.Deserialize<string[]>(json) ?? []);
        }
        catch (Exception e) when (e is IOException or JsonException) { Ui.Error(e.Message); }
    }
}

// ============================================================================ Lab

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
                if (d.ShowDialog(this) == DialogResult.OK) { _path = d.FileName; _file.Text = d.FileName; }
            }),
            new Label { Text = "Candidates from:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, _profile,
            new Label { Text = "Sample (MiB):", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, _sample,
            Ui.Button("Run", (_, _) => Run(), accent: true),
            Ui.Button("Stop", (_, _) => _cts?.Cancel()),
            Ui.Button("Save best as rule", (_, _) => SaveBest()));
        Controls.Add(Ui.Page("Lab", "Benchmark: compress and decompress a sample with each candidate, verify the round trip, and compare size against speed.", _list, tools, _file));
    }

    public void Activated() { }

    private void Run()
    {
        if (_path is null) { Ui.Info("Choose a file first."); return; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var path = _path;
        var profile = Profile.ByName(_profile.SelectedItem!.ToString()!);
        long sampleLen = (long)_sample.Value << 20;
        _list.Items.Clear();
        Ui.RunSafe(this, async () =>
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

// ============================================================================ Rules

internal sealed class RulesPage : UserControl, IPage
{
    private readonly TrayApp _app;
    private readonly FastList _list = Ui.List(("Id", 160), ("On", 40), ("Priority", 60), ("Match", 300), ("Action", 360));
    private readonly TextBox _json = new() { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsReturn = true, AcceptsTab = true };
    private readonly TextBox _testPath = new() { Width = 360, PlaceholderText = "e.g. photos/raw/img001.bmp" };
    private readonly Label _testResult = Ui.Text("", muted: true, maxWidth: 900);

    public RulesPage(TrayApp app)
    {
        _app = app;
        _json.Font = Theme.Mono;
        var tools = Ui.Bar(
            Ui.Button("Reload", (_, _) => ReloadRules()),
            Ui.Button("Validate", (_, _) => ValidateRules()),
            Ui.Button("Save", (_, _) => Save(), accent: true),
            Ui.Button("Add example", (_, _) => AddExample()),
            new Label { Text = "Test path:", AutoSize = true, Padding = new Padding(12, 6, 0, 0) }, _testPath,
            Ui.Button("Explain", (_, _) => TestPath()));
        // SplitterDistance is clamped against the control's current (default 150x100) size if set too early,
        // so apply it once the real height is known.
        var split = new SplitContainer { Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel1 };
        bool splitSet = false;
        split.SizeChanged += (_, _) =>
        {
            if (splitSet || split.Height < 300) return;
            split.SplitterDistance = Math.Min(LogicalToDeviceUnits(220), split.Height - LogicalToDeviceUnits(140));
            splitSet = true;
        };
        _list.Dock = DockStyle.Fill;
        _json.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_json);
        Controls.Add(Ui.Page("Rules & patches",
            "Rules apply per file: force or forbid pipelines, override parameters (\"lzma.dict\": \"256M\"), store, exclude, or give a file its own block. Later and higher-priority rules win; forbids always apply.",
            split, tools, _testResult));
        ReloadRules();
    }

    public void Activated() => ReloadRules();

    private void ReloadRules()
    {
        _json.Text = File.Exists(AppPaths.RulesFile) ? File.ReadAllText(AppPaths.RulesFile) : new RuleSet().ToJson();
        RefreshList();
    }

    private RuleSet? Parse()
    {
        try { return RuleSet.Parse(_json.Text, RuleSet.LayerGlobal); }
        catch (JsonException e) { Ui.Error("Invalid JSON: " + e.Message); return null; }
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        RuleSet? rs;
        try { rs = RuleSet.Parse(_json.Text); } catch (JsonException) { return; }
        foreach (var r in rs.Rules)
        {
            var m = r.Match;
            string match = string.Join("; ", new[]
            {
                m.Path, m.Globs is { Length: > 0 } ? string.Join(",", m.Globs) : null, m.Regex,
                m.Extensions is { Length: > 0 } ? "ext " + string.Join(",", m.Extensions) : null,
                m.Kinds is { Length: > 0 } ? "kind " + string.Join(",", m.Kinds) : null,
                m.MinSize is not null || m.MaxSize is not null ? $"size {m.MinSize}..{m.MaxSize}" : null,
            }.Where(x => x is not null));
            var a = r.Actions;
            string action = string.Join("; ", new[]
            {
                a.Exclude == true ? "exclude" : null, a.Store == true ? "store" : null, a.Pipeline,
                a.Solid is not null ? "solid=" + a.Solid : null,
                a.ForbidCodecs is { Length: > 0 } ? "no " + string.Join(",", a.ForbidCodecs) : null,
                a.ForbidFilters is { Length: > 0 } ? "no " + string.Join(",", a.ForbidFilters) : null,
                a.Params is { Count: > 0 } ? string.Join(",", a.Params.Select(kv => kv.Key + "=" + kv.Value)) : null,
            }.Where(x => x is not null));
            _list.Items.Add(new ListViewItem([r.Id, r.Enabled ? "✓" : "", r.Priority.ToString(), match, action]));
        }
    }

    private void ValidateRules()
    {
        var rs = Parse();
        if (rs is null) return;
        var problems = rs.Check(MethodRegistry.Default);
        if (problems.Count == 0) Ui.Info($"All {rs.Rules.Count} rule(s) are valid.");
        else Ui.Error(string.Join("\n", problems));
        RefreshList();
    }

    private void Save()
    {
        var rs = Parse();
        if (rs is null) return;
        var problems = rs.Check(MethodRegistry.Default);
        if (problems.Count > 0 && !Ui.Confirm("There are problems:\n" + string.Join("\n", problems) + "\n\nSave anyway?")) return;
        rs.Save(AppPaths.RulesFile);
        _app.LoadRules();
        RefreshList();
    }

    private void AddExample()
    {
        var rs = Parse() ?? new RuleSet();
        rs.Rules.Add(new Rule
        {
            Id = "example-logs", Description = "Big log files: PPMd with a long context",
            Match = new RuleMatch { Globs = ["**/*.log"], MinSize = 1 << 20 },
            Actions = new RuleActions { Params = new() { ["ppmd.order"] = "16", ["lzma.dict"] = "256M" } },
        });
        _json.Text = rs.ToJson();
        RefreshList();
    }

    private void TestPath()
    {
        var rs = Parse();
        if (rs is null || string.IsNullOrWhiteSpace(_testPath.Text)) return;
        var all = RuleSet.Combine(RuleSet.BuiltIn(), rs);
        var e = all.Resolve(_testPath.Text.Replace('\\', '/'), long.MaxValue / 2, null, MethodRegistry.Default);
        _testResult.Text = e.Provenance.Count == 0 && !e.Any
            ? "No rule matches (kind-based rules need file analysis and are not shown here)."
            : string.Join("  ·  ", e.Provenance.Concat(e.ForbidCodecs.Select(c => "forbid " + c)).Concat(e.ForbidFilters.Select(f => "forbid " + f)));
    }
}

// ============================================================================ Cluster

internal sealed class ClusterPage : UserControl, IPage
{
    private readonly TrayApp _app;
    private readonly TextBox _key = new() { UseSystemPasswordChar = true };
    private readonly Button _reveal;
    private readonly FastList _list = Ui.List(("Name", 180), ("Address", 200), ("Slots", 60), ("Status", 400));
    private readonly Label _local = Ui.Text("", muted: true, maxWidth: 900);
    private readonly Label _found = Ui.Text("", muted: true, maxWidth: 900);
    /// <summary>Shows and changes the "Lend this PC as a worker" setting.</summary>
    private readonly CheckBox _lend = new() { Text = "Lend this PC to the cluster", AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
    /// <summary>The key box holds text the user typed, pasted or generated and has not saved yet.</summary>
    private bool _keyEdited;
    private bool _loadingKey, _syncingLend;

    public ClusterPage(TrayApp app)
    {
        _app = app;
        // A whole key (OSM1- and 11 groups: 70 characters) fits; monospaced so the groups line up when read aloud.
        _key.Font = Theme.Mono;
        _key.Width = _key.LogicalToDeviceUnits(560);
        _key.TextChanged += (_, _) => { if (!_loadingKey) _keyEdited = true; };
        LoadKey();
        _reveal = Ui.RevealButton(_key);
        var keyRow = Ui.Bar(new Label { Text = "Cluster key:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) }, _key,
            _reveal,
            Ui.Button("Generate", (_, _) => GenerateKey()),
            Ui.Button("Copy", (_, _) => Ui.CopyToClipboard(ClusterSettings.NormalizeClusterKey(_key.Text))),
            Ui.Button("Save", (_, _) => SaveKey(), accent: true));
        _lend.CheckedChanged += (_, _) =>
        {
            if (_syncingLend) return;
            var s = _app.Settings.Clone();
            s.Cluster.LendThisPc = _lend.Checked;
            _app.SaveSettings(s); // SettingsChanged updates the status line
        };
        var tools = Ui.Bar(Ui.Button("Find workers on this network", (_, _) => Discover()), _lend);
        Controls.Add(Ui.Page("Cluster",
            "PCs that share the same key can split compression work. Workers receive file contents (never names or passwords). Workers are refused if they run a different engine version; every remote result is re-verified here.",
            _list, keyRow, tools, _local, _found));
        UpdateLocal();
        app.SettingsChanged += OnSettingsChanged;
        app.WorkerChanged += UpdateLocalIfAlive;
        Disposed += (_, _) => { app.SettingsChanged -= OnSettingsChanged; app.WorkerChanged -= UpdateLocalIfAlive; };
    }

    private void UpdateLocalIfAlive()
    {
        if (!IsDisposed) UpdateLocal();
    }

    public bool HasUnsavedChanges => _keyEdited && _key.Text != _app.Settings.Cluster.ClusterKey;

    public void Activated()
    {
        LoadKey();
        UpdateLocal();
    }

    private void OnSettingsChanged()
    {
        if (IsDisposed) return;
        LoadKey();
        UpdateLocal();
    }

    /// <summary>Shows the saved key, unless the box holds an unsaved one.</summary>
    private void LoadKey()
    {
        if (_keyEdited) return;
        _loadingKey = true;
        try { _key.Text = _app.Settings.Cluster.ClusterKey; }
        finally { _loadingKey = false; }
    }

    /// <summary>Puts a new random key in the box, unmasked so it can be read and copied (it is saved with Save).</summary>
    internal void GenerateKey()
    {
        if (_key.Text.Length > 0 && !Ui.Confirm("Replace the current key? Other PCs will need the new key.")) return;
        _key.Text = ClusterKey.Generate().ToString();
        Ui.SetMasked(_key, _reveal, masked: false);
    }

    /// <summary>Saves the key in its canonical form (OSM1-XXXXX-…), however it was pasted; an empty box removes it.</summary>
    private void SaveKey()
    {
        string key;
        if (string.IsNullOrWhiteSpace(_key.Text)) key = "";
        else
        {
            try { key = ClusterKey.Parse(_key.Text).ToString(); }
            catch (FormatException e) { Ui.Error(e.Message, "Cluster key not saved"); return; }
        }
        var s = _app.Settings.Clone();
        s.Cluster.ClusterKey = key;
        _app.SaveSettings(s);
        _keyEdited = false;
        LoadKey();
        Ui.Info(key.Length == 0 ? "Cluster key removed." : "Cluster key saved.");
    }

    /// <summary>The "Lend this PC" check and the status line: the worker's state, or why it does not run.</summary>
    private void UpdateLocal()
    {
        var c = _app.Settings.Cluster;
        _syncingLend = true;
        try
        {
            _lend.Checked = c.LendThisPc;
            _lend.Enabled = c.LendThisPc || !string.IsNullOrWhiteSpace(c.ClusterKey);
        }
        finally { _syncingLend = false; }
        var w = _app.LocalWorker;
        string? problem = _app.WorkerError;
        _local.Text = problem
            ?? (w is not null
                ? $"This PC is a worker on port {c.WorkerPort}: {w.Stats.Connections} coordinator(s), {w.Stats.Running} running, {w.Stats.TasksDone} task(s) done, {Ui.Bytes(w.Stats.BytesIn)} received."
                : string.IsNullOrWhiteSpace(c.ClusterKey)
                    ? "This PC is not lending its CPU. Save a cluster key first."
                    : "This PC is not lending its CPU. Turn on \"Lend this PC to the cluster\" to start the worker.");
        _local.ForeColor = problem is null ? Theme.P.Muted : Theme.P.Danger;
    }

    private void Discover()
    {
        if (string.IsNullOrWhiteSpace(_key.Text))
        {
            Ui.Info("Enter, paste or generate a cluster key first: workers only answer PCs that use the same key.");
            return;
        }
        ClusterKey key;
        try { key = ClusterKey.Parse(_key.Text); }
        catch (FormatException e) { Ui.Error(e.Message, "Cluster key"); return; }
        Ui.RunSafe(this, async () =>
        {
            _list.Items.Clear();
            _found.Text = "Looking for workers…";
            var found = await Discovery.ProbeAsync(key, TimeSpan.FromSeconds(2), CancellationToken.None);
            foreach (var f in found) _list.Items.Add(new ListViewItem([f.Name, f.Endpoint, f.Slots.ToString(), "found"]));
            foreach (var manual in _app.Settings.Cluster.Workers) _list.Items.Add(new ListViewItem(["(manual)", manual, "", "configured"]));
            int authenticated = 0;
            foreach (ListViewItem item in _list.Items)
            {
                try
                {
                    await using var n = await RemoteNode.ConnectAsync(item.SubItems[1].Text, key, null, CancellationToken.None);
                    item.SubItems[0].Text = n.Name;
                    item.SubItems[2].Text = n.Slots.ToString();
                    item.SubItems[3].Text = $"OK — authenticated, {Ui.Bytes(n.Memory)} memory";
                    authenticated++;
                }
                catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidDataException or OperationCanceledException)
                {
                    item.SubItems[3].Text = "error: " + e.Message;
                }
            }
            // Discovery answers are authenticated with the key, so a PC with a different key stays silent.
            _found.Text = _list.Items.Count == 0
                ? "No workers answered. Every PC must use the same cluster key as this one and have \"Lend this PC\" turned on, and its firewall must allow TCP 46510 and UDP 46511."
                : $"{_list.Items.Count} worker(s) listed, {authenticated} authenticated." +
                  (authenticated < _list.Items.Count ? " The others show why they failed, for example a different cluster key or Osmium version." : "");
        });
    }
}

// ============================================================================ Settings

internal sealed class SettingsPage : UserControl, IPage
{
    private readonly TrayApp _app;
    private readonly ListBox _categories = new() { Dock = DockStyle.Left, Width = 190, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly TextBox _search = new() { Width = 260, PlaceholderText = "Search settings" };
    private readonly SettingsRenderer _renderer = new() { Dock = DockStyle.Fill };
    private readonly Panel _watchPanel = new() { Dock = DockStyle.Bottom, Height = 0 };
    /// <summary>Copy of the settings being edited. Only the entries in <see cref="_edited"/> are saved.</summary>
    private OsmiumSettings _working;
    /// <summary>Keys of the settings the user changed since the last Apply or Revert (see <see cref="SettingsEdits"/>).</summary>
    private readonly HashSet<string> _edited = new(StringComparer.Ordinal);
    private bool _applying;
    private List<Osmium.Desktop.Settings.SettingDescriptor> _all = [];
    private readonly Label _dirty = Ui.Text("", muted: true);

    public SettingsPage(TrayApp app)
    {
        _app = app;
        _working = app.Settings.Clone();
        foreach (var c in SettingReflector.Categories(_working)) _categories.Items.Add(c.Name);
        _categories.SelectedIndexChanged += (_, _) => { _search.Text = ""; Render(); };
        _search.TextChanged += (_, _) => Render();
        _renderer.Changed += d => MarkEdited(d.Key);
        var tools = Ui.Bar(_search,
            Ui.Button("Apply", (_, _) => Apply(), accent: true),
            Ui.Button("Revert", (_, _) => Revert()),
            Ui.Button("Restore defaults", (_, _) => RestoreDefaults()),
            Ui.Button("Repair Explorer integration", (_, _) => Repair()),
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
            Ui.Text("New files are compressed to an \"_osmium\" subfolder once they stop changing; originals are kept.", muted: true));
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
        _working = _app.Settings.Clone();
        _dirty.Text = "Saved";
        Render();
    }

    private void Revert()
    {
        _edited.Clear();
        _working = _app.Settings.Clone();
        Render();
        _dirty.Text = "";
    }

    private void RestoreDefaults()
    {
        string cat = _categories.SelectedItem as string ?? "General";
        if (!Ui.Confirm($"Restore the defaults for \"{cat}\"?")) return;
        var defaults = new OsmiumSettings();
        var prop = typeof(OsmiumSettings).GetProperties().FirstOrDefault(p =>
            p.PropertyType.GetCustomAttributes(typeof(SettingsCategoryAttribute), false).OfType<SettingsCategoryAttribute>().FirstOrDefault()?.Name == cat);
        if (prop is null) return;
        prop.SetValue(_working, prop.GetValue(defaults));
        foreach (var key in SettingsEdits.KeysOf(cat)) _edited.Add(key);
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

// ============================================================================ Log

internal sealed class LogPage : UserControl, IPage
{
    private readonly FastList _list = Ui.List(("Time", 90), ("Level", 60), ("Message", 900));
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private int _shown;

    public LogPage()
    {
        _list.Font = Theme.Mono;
        var tools = Ui.Bar(Ui.Button("Open log folder", (_, _) => AppLog.OpenFolder()),
            Ui.Button("Copy all", (_, _) =>
            {
                var text = string.Join(Environment.NewLine, AppLog.Snapshot().Select(e => $"{e.Time:HH:mm:ss} [{e.Level}] {e.Message}"));
                if (text.Length > 0) Clipboard.SetText(text); // SetText throws on an empty string
            }));
        Controls.Add(Ui.Page("Log", "Application events, shell invocations, worker activity and errors.", _list, tools));
        _timer.Tick += (_, _) => { if (Visible) Fill(); };
        _timer.Start();
    }

    public void Activated() => Fill();

    private void Fill()
    {
        var entries = AppLog.Snapshot();
        if (entries.Count == _shown) return;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in entries.TakeLast(2000))
            _list.Items.Add(new ListViewItem([e.Time.ToString("HH:mm:ss"), e.Level, e.Message]) { ForeColor = e.Level == "error" ? Theme.P.Danger : Theme.P.Text });
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.EnsureVisible(_list.Items.Count - 1);
        _shown = entries.Count;
    }
}
