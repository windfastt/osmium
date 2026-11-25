// Layout: logical units only
using System.ComponentModel;

namespace Osmium.App;

/// <summary>How a button looks at one moment; the self-test sets one to show every state side by side.</summary>
internal enum ButtonLook { Normal, Hover, Pressed, Focused, Disabled, Default }

/// <summary>
/// The product page's buttons (<c>.btn-primary</c>, <c>.btn-ghost</c>) as a Windows button: keyboard, mnemonics,
/// <see cref="Button.DialogResult"/>, Accept and Cancel buttons and accessibility work as for any button. A ghost
/// button is the card colour inside a one-pixel line that darkens under the pointer and sinks when pressed; a primary
/// button is filled with the accent colour. Disabled buttons show muted text and do not react to the pointer. The
/// dialog's default button carries a one-pixel accent line inside its border. Keyboard focus draws a two-pixel ring
/// outside the face, separated from it by a two-pixel gap in the background colour, so the ring stands out from the
/// background and from a primary face alike. The control keeps a four-pixel margin around the face for that ring.
/// </summary>
internal sealed class OsmiumButton : Button
{
    private bool _primary, _large, _hover, _pressed;
    private ButtonLook? _forced;

    public OsmiumButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; // no visual-style animations behind the custom painting
        UseVisualStyleBackColor = false;
        Font = Theme.BodyStrong;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        AccessibleRole = AccessibleRole.PushButton;
    }

    /// <summary>Filled with the accent colour: the one main action of a page or dialog.</summary>
    [DefaultValue(false)]
    public bool IsPrimary
    {
        get => _primary;
        set { _primary = value; Invalidate(); }
    }

    /// <summary>36 logical pixels high instead of 32: a page's main action.</summary>
    [DefaultValue(false)]
    public bool Large
    {
        get => _large;
        set { _large = value; PerformLayout(); Parent?.PerformLayout(); Invalidate(); }
    }

    /// <summary>Shows one state regardless of pointer and focus (the self-test's specimen only).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ButtonLook? ForcedLook
    {
        get => _forced;
        set { _forced = value; Invalidate(); }
    }

    /// <summary>The space kept around the face for the focus ring: two pixels of gap and two of ring.</summary>
    internal int RingSpace => LogicalToDeviceUnits(4);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, Size.Empty, TextFlags & ~TextFormatFlags.EndEllipsis);
        int ring = RingSpace;
        int faceHeight = Math.Max(LogicalToDeviceUnits(_large ? 36 : 32), text.Height + LogicalToDeviceUnits(12));
        int faceWidth = Math.Max(LogicalToDeviceUnits(64), text.Width + LogicalToDeviceUnits(_large ? 20 : 16) * 2);
        return new Size(faceWidth + ring + ring, faceHeight + ring + ring);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnKeyDown(KeyEventArgs kevent)
    {
        if (kevent.KeyCode == Keys.Space) { _pressed = true; Invalidate(); }
        base.OnKeyDown(kevent);
    }

    protected override void OnKeyUp(KeyEventArgs kevent)
    {
        if (kevent.KeyCode == Keys.Space) { _pressed = false; Invalidate(); }
        base.OnKeyUp(kevent);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    private TextFormatFlags TextFlags =>
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
        | (!UseMnemonic ? TextFormatFlags.NoPrefix : ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix);

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var p = Theme.P;
        var g = pevent.Graphics;
        var look = _forced ?? (!Enabled ? ButtonLook.Disabled : _pressed ? ButtonLook.Pressed : _hover ? ButtonLook.Hover : ButtonLook.Normal);
        bool focused = _forced == ButtonLook.Focused || (_forced is null && Focused && ShowFocusCues);
        bool isDefault = _forced == ButtonLook.Default || (_forced is null && IsDefault);
        g.Clear(Draw.Behind(this));

        int ring = RingSpace;
        var face = Rectangle.Inflate(ClientRectangle, -ring, -ring);
        float radius = LogicalToDeviceUnits(Radius.Control);
        Color fill, text;
        Color? border;
        if (look == ButtonLook.Disabled)
        {
            fill = _primary ? p.Sunk : p.Card;
            border = _primary ? null : p.Line;
            text = p.Muted;
        }
        else if (_primary)
        {
            fill = look switch { ButtonLook.Pressed => p.AccentPressed, ButtonLook.Hover => p.AccentHover, _ => p.Accent };
            border = null;
            text = p.AccentText;
        }
        else
        {
            fill = look == ButtonLook.Pressed ? p.Sunk : p.Card;
            border = look is ButtonLook.Hover or ButtonLook.Pressed ? p.LineStrong : p.Line;
            text = p.Text;
        }
        Draw.RoundedBox(g, face, radius, fill, border);
        // The default button: an accent line inside a quiet button's border; on a filled button, a line of its label's
        // colour, half mixed into the face.
        if (isDefault && look != ButtonLook.Disabled)
            Draw.RoundedBox(g, Rectangle.Inflate(face, -1, -1), Math.Max(0, radius - 1), null, _primary ? Mix(text, fill, 0.45) : p.Accent);
        if (focused)
        {
            int width = LogicalToDeviceUnits(2);
            Draw.RoundedBox(g, ClientRectangle, radius + ring, null, p.Focus, width);
        }
        TextRenderer.DrawText(g, Text, Font, face, text, TextFlags);
    }

    private static Color Mix(Color a, Color b, double share) => Color.FromArgb(
        (int)Math.Round(a.R * share + b.R * (1 - share)), (int)Math.Round(a.G * share + b.G * (1 - share)), (int)Math.Round(a.B * share + b.B * (1 - share)));
}
