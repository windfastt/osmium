// Layout: logical units only
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Osmium.App;

internal enum BannerKind { Info, Warning, Danger, Success }

/// <summary>
/// A message across a page or dialog (information, a warning, an error, a success), as the product page's
/// <c>.callout</c>: on the card colour inside a rounded one-pixel border in the kind's colour, with a three-pixel bar of
/// that colour on the left, the kind's glyph and the wrapped text, and an optional quiet button at its end. It is as
/// tall as its text needs at its width; dock it to the top or put it in a layout row.
/// </summary>
internal sealed class Banner : Control
{
    private readonly Label _text = new() { AutoSize = false, Dock = DockStyle.Fill, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft, Tag = "keep" };
    private Button? _action;
    private BannerKind _kind;

    public Banner(BannerKind kind = BannerKind.Info, string text = "")
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Tag = "keep";
        // Left: the bar, then the glyph's column; the text starts after it.
        Padding = Space.Pad(this, 3 + Space.Md + 16 + 10, Space.Md, Space.Md, Space.Md);
        Margin = Space.Pad(this, 0, 0, 0, Space.Sm);
        _text.Padding = Padding.Empty;
        _text.Margin = Padding.Empty;
        Controls.Add(_text);
        AccessibleRole = AccessibleRole.Alert;
        Kind = kind;
        Text = text;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public BannerKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            BackColor = Theme.P.Card;
            _text.BackColor = Theme.P.Card;
            _text.ForeColor = Theme.P.Text;
            AccessibleDescription = value switch
            {
                BannerKind.Warning => "Warning", BannerKind.Danger => "Error", BannerKind.Success => "Done", _ => "Information",
            };
            Invalidate();
        }
    }

    [AllowNull]
    public override string Text
    {
        get => _text.Text;
        set
        {
            base.Text = value;
            _text.Text = value ?? "";
            AccessibleName = value;
            FitHeight();
        }
    }

    /// <summary>A button at the banner's end (a second call replaces it).</summary>
    public void SetAction(string text, Action onClick)
    {
        if (_action is not null)
        {
            Controls.Remove(_action);
            _action.Dispose();
        }
        _action = Ui.Button(text, (_, _) => onClick());
        _action.Dock = DockStyle.Right;
        Controls.Add(_action);
        _text.BringToFront(); // the text fills what the button leaves
        FitHeight();
    }

    /// <summary>The kind's colour: border, bar and glyph.</summary>
    private Color KindColor => _kind switch
    {
        BannerKind.Warning => Theme.P.Warning,
        BannerKind.Danger => Theme.P.Danger,
        BannerKind.Success => Theme.P.Success,
        _ => Theme.P.AccentInk,
    };

    private string Glyph => _kind switch
    {
        BannerKind.Warning => "",
        BannerKind.Danger => "",
        BannerKind.Success => "",
        _ => "",
    };

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width is > 0 and < int.MaxValue ? proposedSize.Width : Width;
        int textWidth = Math.Max(1, width - Padding.Horizontal - (_action?.Width ?? 0));
        var text = TextRenderer.MeasureText(_text.Text.Length == 0 ? " " : _text.Text, _text.Font, new Size(textWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        int height = Math.Max(Math.Max(text.Height, Theme.Glyph.Height), _action?.PreferredSize.Height ?? 0) + Padding.Vertical;
        return new Size(width, height);
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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Draw.Behind(this));
        var color = KindColor;
        float radius = LogicalToDeviceUnits(Radius.Card);
        Draw.RoundedBox(g, ClientRectangle, radius, Theme.P.Card, color);
        using (var clip = Draw.Rounded(ClientRectangle, radius))
        {
            var state = g.Save();
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.SetClip(clip);
            g.FillRectangle(Paints.Brush(color), 0, 0, LogicalToDeviceUnits(3), Height);
            g.Restore(state);
        }
        int x = LogicalToDeviceUnits(3 + Space.Md);
        // Beside the first line of a long text; centred beside a single line, as the text is.
        int lineHeight = Math.Max(Theme.Glyph.Height, _text.Font.Height);
        int content = Height - Padding.Vertical;
        int textWidth = Math.Max(1, _text.Width);
        bool oneLine = TextRenderer.MeasureText(_text.Text, _text.Font, new Size(textWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height <= _text.Font.Height * 3 / 2;
        var glyph = new Rectangle(x, Padding.Top, LogicalToDeviceUnits(20), oneLine ? content : lineHeight);
        TextRenderer.DrawText(g, Glyph, Theme.Glyph, glyph, color, TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void FitHeight()
    {
        int h = GetPreferredSize(new Size(Width, 0)).Height;
        if (Height != h) Height = h;
    }
}
