// Layout: logical units only
using System.ComponentModel;
using System.Globalization;

namespace Osmium.App;

internal enum MeterState { Normal, Paused, Failed, Done }

/// <summary>
/// A progress bar, as the product page's <c>.meter</c>: <see cref="Value"/> from 0 to 1 filled with the mark colour on a
/// sunk track, both rounded, or, while it is null, a moving segment (the amount of work is not known yet). The fill
/// shows <see cref="State"/>: paused in the warning colour, failed in the danger colour, done in the success colour.
/// 8 logical pixels high unless sized otherwise. The moving segment steps at most every 50 ms, and only while the meter
/// is shown and its window is not minimised. Screen readers get a progress bar with its value in words.
/// </summary>
internal sealed class Meter : Control
{
    private const int DefaultHeight = 8;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private double? _value = 0;
    private MeterState _state;
    private int _phase;

    public Meter()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Tag = "keep";
        Height = LogicalToDeviceUnits(DefaultHeight);
        AccessibleRole = AccessibleRole.ProgressBar;
        _timer.Tick += (_, _) =>
        {
            if (FindForm() is { WindowState: FormWindowState.Minimized }) return;
            _phase = (_phase + 2) % 100;
            Invalidate();
        };
        Describe();
    }

    /// <summary>How much is done, from 0 to 1 (clamped); null while that is not known.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double? Value
    {
        get => _value;
        set
        {
            _value = value is { } v ? Math.Clamp(double.IsNaN(v) ? 0 : v, 0, 1) : null;
            Describe();
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
            Describe();
            UpdateTimer();
            Invalidate();
        }
    }

    /// <summary>Where the moving segment is (0 to 99): the self-test draws one frame of it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int Phase
    {
        get => _phase;
        set { _phase = ((value % 100) + 100) % 100; Invalidate(); }
    }

    /// <summary>The value in words, as screen readers read it ("29 %", "paused at 29 %").</summary>
    public string ValueText
    {
        get
        {
            string pct = _value is { } v ? string.Format(CultureInfo.CurrentCulture, "{0:0} %", v * 100) : "";
            return (_state, _value) switch
            {
                (MeterState.Paused, null) => "paused",
                (MeterState.Paused, _) => "paused at " + pct,
                (MeterState.Failed, null) => "failed",
                (MeterState.Failed, _) => "failed at " + pct,
                (MeterState.Done, _) => "done",
                (_, null) => "working",
                _ => pct,
            };
        }
    }

    private void Describe() => AccessibleDescription = ValueText;

    protected override AccessibleObject CreateAccessibilityInstance() => new MeterAccessible(this);

    protected override Size DefaultSize => new(100, DefaultHeight); // layout: device pixels on purpose (the base constructor's size; the constructor sets the scaled height)

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
        g.Clear(Draw.Behind(this));
        int w = ClientSize.Width, h = ClientSize.Height;
        float radius = Math.Min(LogicalToDeviceUnits(Radius.Meter), h / 2f);
        Draw.RoundedBox(g, ClientRectangle, radius, p.Sunk, null);
        var mark = _state switch
        {
            MeterState.Paused => p.Warning,
            MeterState.Failed => p.Danger,
            MeterState.Done => p.Success,
            _ => p.Mark,
        };
        if (_value is { } v)
        {
            int fill = _state == MeterState.Done ? w : (int)Math.Round(w * v);
            if (fill > 0) Draw.RoundedBox(g, new Rectangle(0, 0, Math.Max(fill, (int)Math.Ceiling(radius * 2)), h), radius, mark, null);
        }
        else
        {
            int seg = Math.Max(1, w / 4);
            int x = (int)((long)(w + seg) * _phase / 100) - seg;
            using var clip = Draw.Rounded(ClientRectangle, radius);
            var state = g.Save();
            g.SetClip(clip);
            Draw.RoundedBox(g, new Rectangle(x, 0, seg, h), radius, mark, null);
            g.Restore(state);
        }
    }

    private sealed class MeterAccessible(Meter owner) : ControlAccessibleObject(owner)
    {
        public override string? Value => owner.ValueText;
        public override AccessibleRole Role => AccessibleRole.ProgressBar;
    }
}
