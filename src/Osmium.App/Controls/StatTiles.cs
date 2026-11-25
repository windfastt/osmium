// Layout: logical units only
using System.ComponentModel;
using Osmium.Desktop.Theming;

namespace Osmium.App;

/// <summary>One result: a big <see cref="Value"/> ("604 KB"), what it is (<see cref="Label"/>), an optional
/// <see cref="Caption"/> below and an optional <see cref="Bar"/> from 0 to 1 (a ratio).</summary>
internal sealed record StatTile(string Value, string Label, string? Caption = null, double? Bar = null);

/// <summary>
/// A row of equal tiles with big numbers and small labels (an archive's summary, a job's result), as the product page's
/// <c>.tiles</c>: one rounded one-pixel border around them all, one-pixel lines between them, each tile on the card
/// colour with its value, its label, an optional caption and an optional bar (the mark colour on the sunk colour).
/// When the row is too narrow for every tile, the tiles wrap onto two rows. As tall as its tiles need.
/// </summary>
internal sealed class StatTiles : Control
{
    private IReadOnlyList<StatTile> _tiles = [];
    private bool _live;

    public StatTiles()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Tag = "keep";
        AccessibleRole = AccessibleRole.StaticText;
        Height = PreferredHeight(Width);
    }

    public void SetTiles(IReadOnlyList<StatTile> tiles)
    {
        _tiles = [.. tiles];
        AccessibleName = string.Join(", ", _tiles.Select(t => $"{t.Label}: {t.Value}" + (t.Caption is { } c ? $" ({c})" : "")));
        FitHeight();
        Invalidate();
    }

    /// <summary>The values change while shown (progress, speed): drawn with digits of equal width, so they do not
    /// jitter.</summary>
    [DefaultValue(false)]
    public bool Live
    {
        get => _live;
        set { _live = value; FitHeight(); Invalidate(); }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width is > 0 and < int.MaxValue ? proposedSize.Width : Width;
        return new Size(width, PreferredHeight(width));
    }

    private int Pad => LogicalToDeviceUnits(Space.Lg);
    private int Gap => LogicalToDeviceUnits(6);
    private int BarHeight => LogicalToDeviceUnits(8);
    private int MinTileWidth => LogicalToDeviceUnits(150);

    private Font ValueFont(string value) => _live ? Theme.NumberLive : Theme.FontFor(TypeRole.Number, value);

    /// <summary>Tiles per row: all of them, or as many as fit at their smallest width (at least half, rounded up).</summary>
    private int Columns(int width)
    {
        int n = Math.Max(1, _tiles.Count);
        if (width <= 0 || width / n >= MinTileWidth || n == 1) return n;
        return Math.Max(1, Math.Max((n + 1) / 2, width / MinTileWidth));
    }

    private int TileHeight()
    {
        int h = Pad + Theme.Number.Height + Gap + Theme.Body.Height;
        if (_tiles.Any(t => t.Caption is not null)) h += LogicalToDeviceUnits(2) + Theme.Caption.Height;
        if (_tiles.Any(t => t.Bar is not null)) h += LogicalToDeviceUnits(10) + BarHeight;
        return h + Pad;
    }

    private int PreferredHeight(int width)
    {
        if (_tiles.Count == 0) return TileHeight();
        int columns = Columns(width);
        int rows = (_tiles.Count + columns - 1) / columns;
        return rows * TileHeight() + (rows - 1);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        FitHeight();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        FitHeight();
    }

    private void FitHeight()
    {
        int h = PreferredHeight(Width);
        if (Height != h) Height = h;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(Draw.Behind(this));
        float radius = LogicalToDeviceUnits(Radius.Card);
        Draw.RoundedBox(g, ClientRectangle, radius, p.Card, null);
        if (_tiles.Count > 0)
        {
            int columns = Columns(Width);
            int tileHeight = TileHeight();
            var linePen = Paints.Pen(p.Line);
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            for (int i = 0; i < _tiles.Count; i++)
            {
                var t = _tiles[i];
                int col = i % columns, row = i / columns;
                // Columns share the width; the dividers are the pixel between them.
                int left = col * (Width + 1) / columns, right = (col + 1) * (Width + 1) / columns - 1;
                int top = row * (tileHeight + 1);
                if (col > 0) g.DrawLine(linePen, left - 1, top, left - 1, top + tileHeight - 1);
                if (row > 0 && col == 0) g.DrawLine(linePen, 0, top - 1, Width - 1, top - 1);
                int x = left + Pad, w = Math.Max(1, right - left - 2 * Pad), y = top + Pad;
                var valueFont = ValueFont(t.Value);
                TextRenderer.DrawText(g, t.Value, valueFont, new Rectangle(x, y, w, Theme.Number.Height), p.Text, flags | TextFormatFlags.Bottom);
                y += Theme.Number.Height + Gap;
                TextRenderer.DrawText(g, t.Label, Theme.Body, new Rectangle(x, y, w, Theme.Body.Height), p.TextSecondary, flags);
                y += Theme.Body.Height;
                if (_tiles.Any(s => s.Caption is not null))
                {
                    y += LogicalToDeviceUnits(2);
                    if (t.Caption is { } caption)
                        TextRenderer.DrawText(g, caption, Theme.Caption, new Rectangle(x, y, w, Theme.Caption.Height), p.Muted, flags);
                    y += Theme.Caption.Height;
                }
                if (t.Bar is { } bar)
                {
                    y += LogicalToDeviceUnits(10);
                    float r = LogicalToDeviceUnits(Radius.Meter);
                    Draw.RoundedBox(g, new Rectangle(x, y, w, BarHeight), r, p.Sunk, null);
                    int fill = (int)Math.Round(w * Math.Clamp(double.IsNaN(bar) ? 0 : bar, 0, 1));
                    if (fill > 0) Draw.RoundedBox(g, new Rectangle(x, y, Math.Max(fill, (int)Math.Ceiling(2 * r)), BarHeight), r, p.Mark, null);
                }
            }
        }
        Draw.RoundedBox(g, ClientRectangle, radius, null, p.Line);
    }
}
