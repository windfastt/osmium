using System.Text;
using System.Text.Json;
using Osmium.Core.Container;
using Osmium.Desktop.Jobs;

namespace Osmium.App;

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

    /// <summary>Opens an archive on the page; the task completes when the page shows it (or the error about it).</summary>
    internal Task OpenArchiveAsync(string path)
    {
        OpenArchive(path);
        return Task.CompletedTask;
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
