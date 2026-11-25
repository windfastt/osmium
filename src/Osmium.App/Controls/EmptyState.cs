// Layout: logical units only
using System.ComponentModel;
using System.Drawing.Drawing2D;
using Osmium.Desktop.Theming;

namespace Osmium.App;

/// <summary>
/// What a list shows while it has no rows: a glyph, a title, a hint and an optional action ("Add files…"), centred on
/// the list's colour. Attached to a <see cref="FastList"/> it covers the list below its column headers while the list
/// has no rows (<c>Items.Count == 0</c>, or <c>VirtualListSize == 0</c> in virtual mode), checked again each time the
/// list's <see cref="FastList.ItemsChanged"/> is called. Files dropped on it go to the list. While
/// <see cref="Highlight"/> is set (something is dragged over the list), a dashed accent outline marks the drop area and
/// the title reads <see cref="ReleaseTitle"/> when one is set.
/// </summary>
internal sealed class EmptyState : Control
{
    private string _glyph, _title, _hint, _releaseTitle = "";
    private Button? _action;
    private bool _highlight;

    public EmptyState(string glyph = "", string title = "", string hint = "")
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        _glyph = glyph;
        _title = title;
        _hint = hint;
        Tag = "keep";
        BackColor = Theme.P.Card;
        ForeColor = Theme.P.Text;
        AllowDrop = true;
        AccessibleRole = AccessibleRole.StaticText;
        Describe();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get => _glyph; set { _glyph = value; Relayout(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get => _title; set { _title = value; Describe(); Relayout(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Hint { get => _hint; set { _hint = value; Describe(); Relayout(); } }

    /// <summary>The title while something is dragged over the list ("Release to add"); empty: the title stays.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ReleaseTitle { get => _releaseTitle; set { _releaseTitle = value; Relayout(); } }

    /// <summary>Marks the list as a drop target while something is dragged over it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Highlight
    {
        get => _highlight;
        set
        {
            if (_highlight == value) return;
            _highlight = value;
            Describe();
            Relayout();
        }
    }

    private string ShownTitle => _highlight && _releaseTitle.Length > 0 ? _releaseTitle : _title;

    private void Describe()
    {
        AccessibleName = ShownTitle;
        AccessibleDescription = _hint;
    }

    /// <summary>A button below the hint (one per empty state: a second call replaces it).</summary>
    public void SetAction(string text, Action onClick)
    {
        if (_action is not null)
        {
            Controls.Remove(_action);
            _action.Dispose();
        }
        _action = Ui.Button(text, (_, _) => onClick());
        Controls.Add(_action);
        Relayout();
    }

    /// <summary>Shows this over <paramref name="list"/> while it has no rows (the same as setting <see cref="FastList.Empty"/>).</summary>
    public void AttachTo(FastList list) => list.Empty = this;

    private const TextFormatFlags CenterLine = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags CenterWrap = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;

    /// <summary>Where the glyph, title, hint and action go: a column centred in the control.</summary>
    private (Rectangle Glyph, Rectangle Title, Rectangle Hint, Rectangle Action) Arrange()
    {
        int width = Math.Max(1, Math.Min(ClientSize.Width - LogicalToDeviceUnits(2 * Space.Lg), LogicalToDeviceUnits(420)));
        var titleFont = Theme.FontFor(TypeRole.CardHeading, ShownTitle);
        int glyphH = _glyph.Length > 0 ? Theme.GlyphLarge.Height : 0;
        int titleH = ShownTitle.Length > 0 ? titleFont.Height : 0;
        int hintH = _hint.Length > 0 ? TextRenderer.MeasureText(_hint, Theme.Body, new Size(width, 0), CenterWrap).Height : 0;
        var actionSize = _action?.GetPreferredSize(Size.Empty) ?? Size.Empty;
        int gap = LogicalToDeviceUnits(Space.Sm);
        int total = glyphH + (glyphH > 0 ? gap : 0) + titleH + (hintH > 0 ? LogicalToDeviceUnits(Space.Xs) + hintH : 0)
                    + (_action is not null ? LogicalToDeviceUnits(Space.Md) + actionSize.Height : 0);
        int x = (ClientSize.Width - width) / 2, y = Math.Max(LogicalToDeviceUnits(Space.Lg), (ClientSize.Height - total) / 2);
        var glyph = new Rectangle(x, y, width, glyphH);
        y += glyphH + (glyphH > 0 ? gap : 0);
        var title = new Rectangle(x, y, width, titleH);
        y += titleH + (hintH > 0 ? LogicalToDeviceUnits(Space.Xs) : 0);
        var hint = new Rectangle(x, y, width, hintH);
        y += hintH + LogicalToDeviceUnits(Space.Md);
        var action = new Rectangle((ClientSize.Width - actionSize.Width) / 2, y, actionSize.Width, actionSize.Height);
        return (glyph, title, hint, action);
    }

    private void Relayout()
    {
        if (_action is not null) _action.Bounds = Arrange().Action;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_action is not null) _action.Bounds = Arrange().Action;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(p.Card);
        var (glyph, title, hint, _) = Arrange();
        if (_glyph.Length > 0) TextRenderer.DrawText(g, _glyph, Theme.GlyphLarge, glyph, _highlight ? p.AccentInk : p.Muted, CenterLine);
        if (ShownTitle.Length > 0) TextRenderer.DrawText(g, ShownTitle, Theme.FontFor(TypeRole.CardHeading, ShownTitle), title, p.Text, CenterLine);
        if (_hint.Length > 0) TextRenderer.DrawText(g, _hint, Theme.Body, hint, p.Muted, CenterWrap);
        if (_highlight)
        {
            int inset = LogicalToDeviceUnits(Space.Xs), width = LogicalToDeviceUnits(2);
            var r = Rectangle.Inflate(ClientRectangle, -inset, -inset);
            using var pen = new Pen(p.AccentInk, width) { DashStyle = DashStyle.Dash };
            var smoothing = g.SmoothingMode;
            var offset = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var path = Draw.Rounded(new RectangleF(r.X + width / 2f, r.Y + width / 2f, r.Width - width, r.Height - width), LogicalToDeviceUnits(Radius.Control));
            g.DrawPath(pen, path);
            g.SmoothingMode = smoothing;
            g.PixelOffsetMode = offset;
        }
    }

    // Files dragged over the empty state are the list's.
    protected override void OnDragEnter(DragEventArgs drgevent) { base.OnDragEnter(drgevent); (Parent as FastList)?.ForwardDrag(drgevent, FastList.DragStage.Enter); }
    protected override void OnDragOver(DragEventArgs drgevent) { base.OnDragOver(drgevent); (Parent as FastList)?.ForwardDrag(drgevent, FastList.DragStage.Over); }
    protected override void OnDragDrop(DragEventArgs drgevent) { base.OnDragDrop(drgevent); (Parent as FastList)?.ForwardDrag(drgevent, FastList.DragStage.Drop); }
    protected override void OnDragLeave(EventArgs e) { base.OnDragLeave(e); (Parent as FastList)?.ForwardDragLeave(); }
}
