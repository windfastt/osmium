using System.Windows.Forms;

namespace UiEval;

/// <summary>
/// A plain WinForms window that accepts file drops (an OLE drop target, as Explorer is): it records the formats the
/// drag offered and the CF_HDROP file list, so the harness can prove a row dragged out of the preview arrives as files.
/// </summary>
internal sealed class DropTarget : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private Form? _form;

    public string[] Formats { get; private set; } = [];
    public string[]? Files { get; private set; }
    public string? Effect { get; private set; }
    public int Enters { get; private set; }
    public ManualResetEventSlim Dropped { get; } = new();
    public Win.RECT Bounds { get; private set; }

    public DropTarget(int x, int y, int w, int h)
    {
        _thread = new Thread(() =>
        {
            var f = new Form
            {
                Text = "Drop target", StartPosition = FormStartPosition.Manual, Left = x, Top = y, Width = w, Height = h,
                AllowDrop = true, TopMost = true, ShowInTaskbar = false, BackColor = System.Drawing.Color.FromArgb(0xEE, 0xF0, 0xF4),
            };
            f.Controls.Add(new Label { Text = "Drop target (CF_HDROP)", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleCenter });
            foreach (Control c in f.Controls) c.AllowDrop = false;
            f.DragEnter += (_, e) =>
            {
                Enters++;
                Formats = e.Data?.GetFormats() ?? [];
                e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            };
            f.DragOver += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            f.DragDrop += (_, e) =>
            {
                Formats = e.Data?.GetFormats() ?? [];
                Files = e.Data?.GetData(DataFormats.FileDrop) as string[];
                Effect = e.Effect.ToString();
                Dropped.Set();
            };
            f.Shown += (_, _) =>
            {
                Win.GetWindowRect(f.Handle, out var r);
                Bounds = r;
                _ready.Set();
            };
            _form = f;
            Application.Run(f);
        }) { IsBackground = true, Name = "drop target" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(15_000);
    }

    public void Dispose()
    {
        try { _form?.Invoke(() => _form.Close()); } catch (Exception) { }
        _thread.Join(5000);
    }
}
