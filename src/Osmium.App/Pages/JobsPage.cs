using Osmium.Desktop.Jobs;

namespace Osmium.App;

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

    /// <summary>Selects a job's row, which shows its log below the list.</summary>
    internal void SelectJob(Guid id)
    {
        RefreshList();
        foreach (ListViewItem i in _list.Items)
        {
            bool match = (Guid)i.Tag! == id;
            i.Selected = match;
            if (!match) continue;
            _list.FocusedItem = i;
            i.EnsureVisible();
        }
        ShowLog();
    }

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
                j.State is JobState.Running or JobState.Paused ? $"{j.Fraction:P0} {j.Phase}" : Osmium.Desktop.Tray.TrayText.FinishedStatus(j),
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
