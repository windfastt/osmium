namespace Osmium.App;

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
                Ui.CopyToClipboard(string.Join(Environment.NewLine, AppLog.Snapshot().Select(e => $"{e.Time:HH:mm:ss} [{e.Level}] {e.Message}")))));
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
