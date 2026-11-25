using System.Text.Json;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;
using Osmium.Desktop.Settings;

namespace Osmium.App;

internal sealed class RulesPage : UserControl, IPage
{
    private readonly TrayApp _app;
    private readonly FastList _list = Ui.List(("Id", 160), ("On", 40), ("Priority", 60), ("Match", 300), ("Action", 360));
    private readonly EditBox _json = new() { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsReturn = true, AcceptsTab = true };
    private readonly EditBox _testPath = new() { Width = 360, PlaceholderText = "e.g. photos/raw/img001.bmp" };
    private readonly Label _testResult = Ui.Text("", muted: true, maxWidth: 900);
    /// <summary>The editor holds changes that are not in rules.json (typed, or added with "Add example").</summary>
    private bool _dirty;
    private bool _loading;

    public RulesPage(TrayApp app)
    {
        _app = app;
        _json.Font = Theme.Mono;
        _json.TextChanged += (_, _) => { if (!_loading) _dirty = true; };
        _testPath.EnterAction = TestPath;
        var tools = Ui.Bar(
            Ui.Button("Reload", (_, _) =>
            {
                if (_dirty && !Ui.Confirm("Discard your unsaved changes and reload rules.json?")) return;
                ReloadRules();
            }),
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

    public bool HasUnsavedChanges => _dirty;

    /// <summary>Shows rules.json again (it may have been changed by "Save patch as rule" or the Lab), unless the
    /// editor holds unsaved changes.</summary>
    public void Activated()
    {
        if (!_dirty) ReloadRules();
    }

    private void ReloadRules()
    {
        string text;
        try { text = File.Exists(AppPaths.RulesFile) ? File.ReadAllText(AppPaths.RulesFile) : new RuleSet().ToJson(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("rules.json could not be read: " + e.Message);
            _testResult.Text = "rules.json could not be read: " + e.Message;
            return;
        }
        _loading = true;
        try { _json.Text = text; }
        finally { _loading = false; }
        _dirty = false;
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
        _dirty = false;
        _app.LoadRules();
        RefreshList();
    }

    /// <summary>The "Add example" button: appends the example rule to the editor (not saved).</summary>
    internal void AddExample()
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
