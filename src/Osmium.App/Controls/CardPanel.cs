// Layout: logical units only
using System.ComponentModel;
using Osmium.Desktop.Theming;

namespace Osmium.App;

/// <summary>
/// A card, as on the product page: content on the card colour inside a one-pixel line with rounded corners and no
/// shadow, 16 logical pixels of padding, and an optional heading row above the content (the heading on the left,
/// controls added with <see cref="AddHeadingControl"/> on the right). Built with <see cref="Ui.Card"/>; the content
/// fills the card. The padding keeps every child clear of the rounded corners; a list may sit as close as 4 logical
/// pixels to the border. In a table row that takes its height from its content (<see cref="SizeType.AutoSize"/>), set
/// <see cref="Control.AutoSize"/>: the card is then as tall as its heading and content need at the row's width. A card
/// that fills the rest of a page or dialog keeps <see cref="Control.AutoSize"/> off.
/// </summary>
internal sealed class CardPanel : Panel
{
    private readonly Label _heading = new() { AutoSize = true, UseMnemonic = false, Tag = "keep", Anchor = AnchorStyles.Left };
    private readonly FlowLayoutPanel _headingControls = new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
        Anchor = AnchorStyles.Right, Margin = Padding.Empty,
    };
    private readonly TableLayoutPanel _headingRow = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1 };
    private bool _dropHighlight;

    public CardPanel(Control content, string? heading = null)
    {
        Content = content;
        Tag = "keep"; // Theme.Apply leaves the card colour alone
        BackColor = Theme.P.Card;
        ForeColor = Theme.P.Text;
        Padding = Space.Pad(this, Space.Lg, Space.Lg, Space.Lg, Space.Lg);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        AccessibleRole = AccessibleRole.Grouping;

        _heading.ForeColor = Theme.P.Text;
        _heading.Margin = Padding.Empty;
        _headingRow.Margin = Padding.Empty;
        _headingRow.Padding = Space.Pad(_headingRow, 0, 0, 0, Space.Md);
        _headingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _headingRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _headingRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _headingRow.Controls.Add(_heading, 0, 0);
        _headingRow.Controls.Add(_headingControls, 1, 0);
        _headingControls.Visible = false;

        content.Dock = DockStyle.Fill;
        Controls.Add(content);
        Controls.Add(_headingRow); // added last, so it docks first and the content fills the rest
        Heading = heading;
    }

    public Control Content { get; }

    /// <summary>The line above the content; null or empty: none.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Heading
    {
        get => _headingText;
        set
        {
            _headingText = string.IsNullOrEmpty(value) ? null : value;
            _heading.Text = value ?? "";
            _heading.Font = Theme.FontFor(TypeRole.CardHeading, value ?? "");
            AccessibleName = _headingText;
            UpdateHeadingRow();
        }
    }

    private string? _headingText;

    /// <summary>A control at the right end of the heading row (a button, a tag); the first one added is rightmost.</summary>
    public void AddHeadingControl(Control control)
    {
        control.Margin = Space.Pad(control, Space.Sm, 0, 0, 0);
        _headingControls.Controls.Add(control);
        _headingControls.Visible = true;
        UpdateHeadingRow();
    }

    /// <summary>Marks the card as a drop target while something is dragged over it: a dashed accent outline inside
    /// the border.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool DropHighlight
    {
        get => _dropHighlight;
        set
        {
            if (_dropHighlight == value) return;
            _dropHighlight = value;
            Invalidate();
        }
    }

    /// <summary>The card's height without its content: padding and the heading row.</summary>
    public int ChromeHeight
    {
        get
        {
            int inner = Math.Max(1, Width - Padding.Horizontal);
            return Padding.Vertical + (_headingRow.Visible ? _headingRow.GetPreferredSize(new Size(inner, 0)).Height : 0);
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width is > 0 and < int.MaxValue ? proposedSize.Width : Width;
        int inner = Math.Max(1, width - Padding.Horizontal);
        var content = Content.Visible ? Content.GetPreferredSize(new Size(inner - Content.Margin.Horizontal, 0)) : Size.Empty;
        int height = Padding.Vertical + content.Height + (Content.Visible ? Content.Margin.Vertical : 0)
                     + (_headingRow.Visible ? _headingRow.GetPreferredSize(new Size(inner, 0)).Height : 0);
        return new Size(width, height);
    }

    private void UpdateHeadingRow() => _headingRow.Visible = _headingText is not null || _headingControls.Controls.Count > 0;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var p = Theme.P;
        e.Graphics.Clear(Draw.Behind(this));
        int radius = LogicalToDeviceUnits(Radius.Card);
        Draw.RoundedBox(e.Graphics, ClientRectangle, radius, p.Card, p.Line);
        if (_dropHighlight)
        {
            int inset = LogicalToDeviceUnits(Space.Xs);
            var inner = Rectangle.Inflate(ClientRectangle, -inset, -inset);
            using var pen = new Pen(p.AccentInk, LogicalToDeviceUnits(2)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            var smoothing = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float half = pen.Width / 2f;
            using var path = Draw.Rounded(new RectangleF(inner.X + half, inner.Y + half, inner.Width - pen.Width, inner.Height - pen.Width), Math.Max(0, radius - inset));
            e.Graphics.DrawPath(pen, path);
            e.Graphics.SmoothingMode = smoothing;
        }
    }

    protected override void OnPaint(PaintEventArgs e) { }
}
