namespace Osmium.App;

/// <summary>One result: a big <see cref="Value"/> ("604 KB"), what it is (<see cref="Label"/>), an optional
/// <see cref="Caption"/> below and an optional <see cref="Bar"/> from 0 to 1 (a ratio).</summary>
internal sealed record StatTile(string Value, string Label, string? Caption = null, double? Bar = null);

/// <summary>A row of equal tiles with big numbers and small labels (an archive's summary, a job's result). As tall as
/// its tiles need.</summary>
internal sealed class StatTiles : Control
{
    private IReadOnlyList<StatTile> _tiles = [];

    public StatTiles()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        Height = PreferredHeight();
    }

    public void SetTiles(IReadOnlyList<StatTile> tiles)
    {
        _tiles = [.. tiles];
        AccessibleName = string.Join(", ", _tiles.Select(t => $"{t.Label}: {t.Value}" + (t.Caption is { } c ? $" ({c})" : "")));
        Height = PreferredHeight();
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Width, PreferredHeight());

    private int Gap => Space.Px(this, Space.Sm);
    private int Pad => Space.Px(this, Space.Md);
    private int BarHeight => Space.Px(this, Space.Xs);

    private int PreferredHeight()
    {
        int h = Pad + Theme.Number.Height + Theme.Caption.Height;
        if (_tiles.Any(t => t.Caption is not null)) h += Theme.Caption.Height;
        if (_tiles.Any(t => t.Bar is not null)) h += Gap + BarHeight;
        return h + Pad;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Height = PreferredHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_tiles.Count == 0) return;
        int gap = Gap, pad = Pad;
        int width = (ClientSize.Width - gap * (_tiles.Count - 1)) / _tiles.Count;
        using var line = new Pen(p.Line);
        using var track = new SolidBrush(p.Sunk);
        using var mark = new SolidBrush(p.Mark);
        using var card = new SolidBrush(p.Card);
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        for (int i = 0; i < _tiles.Count; i++)
        {
            var t = _tiles[i];
            var r = new Rectangle(i * (width + gap), 0, width, ClientSize.Height);
            g.FillRectangle(card, r);
            g.DrawRectangle(line, r.X, r.Y, r.Width - 1, r.Height - 1);
            int x = r.X + pad, w = Math.Max(1, r.Width - 2 * pad), y = pad;
            TextRenderer.DrawText(g, t.Value, Theme.Number, new Rectangle(x, y, w, Theme.Number.Height), p.Text, flags);
            y += Theme.Number.Height;
            TextRenderer.DrawText(g, t.Label, Theme.Caption, new Rectangle(x, y, w, Theme.Caption.Height), p.Muted, flags);
            y += Theme.Caption.Height;
            if (t.Caption is { } caption)
            {
                TextRenderer.DrawText(g, caption, Theme.Caption, new Rectangle(x, y, w, Theme.Caption.Height), p.Muted, flags);
                y += Theme.Caption.Height;
            }
            if (t.Bar is { } bar)
            {
                y += gap;
                g.FillRectangle(track, x, y, w, BarHeight);
                g.FillRectangle(mark, x, y, (int)Math.Round(w * Math.Clamp(double.IsNaN(bar) ? 0 : bar, 0, 1)), BarHeight);
            }
        }
    }
}
