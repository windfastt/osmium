using System.ComponentModel;

namespace Osmium.App;

internal enum MeterState { Normal, Paused, Failed, Done }

/// <summary>
/// A progress bar: <see cref="Value"/> from 0 to 1 filled on a track, or, while it is null, a moving segment (the amount
/// of work is not known yet). <see cref="State"/> colours it. 8 logical pixels high unless sized otherwise.
/// </summary>
internal sealed class Meter : Control
{
    private const int DefaultHeight = 8;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
    private double? _value = 0;
    private MeterState _state;
    private int _phase;

    public Meter()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Height = LogicalToDeviceUnits(DefaultHeight);
        AccessibleRole = AccessibleRole.ProgressBar;
        _timer.Tick += (_, _) =>
        {
            _phase = (_phase + 1) % 100;
            Invalidate();
        };
    }

    /// <summary>How much is done, from 0 to 1 (clamped); null while that is not known.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double? Value
    {
        get => _value;
        set
        {
            _value = value is { } v ? Math.Clamp(double.IsNaN(v) ? 0 : v, 0, 1) : null;
            AccessibleDescription = _value is { } d ? $"{d:P0}" : "working";
            UpdateTimer();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public MeterState State
    {
        get => _state;
        set
        {
            _state = value;
            UpdateTimer();
            Invalidate();
        }
    }

    protected override Size DefaultSize => new(100, DefaultHeight);

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateTimer();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateTimer();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _timer.Stop();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>The segment moves only while it is shown, indeterminate and running.</summary>
    private void UpdateTimer()
    {
        bool run = _value is null && _state == MeterState.Normal && Visible && IsHandleCreated;
        if (run) _timer.Start(); else _timer.Stop();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        using var track = new SolidBrush(p.Sunk);
        g.FillRectangle(track, ClientRectangle);
        var mark = _state switch
        {
            MeterState.Paused => p.Warning,
            MeterState.Failed => p.Danger,
            MeterState.Done => p.Success,
            _ => p.Mark,
        };
        using var fill = new SolidBrush(mark);
        int w = ClientSize.Width, h = ClientSize.Height;
        if (_value is { } v) g.FillRectangle(fill, 0, 0, (int)Math.Round(w * v), h);
        else
        {
            int seg = Math.Max(1, w / 4);
            int x = (int)((long)(w + seg) * _phase / 100) - seg;
            g.FillRectangle(fill, x, 0, seg, h);
        }
    }
}
