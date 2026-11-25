// Layout: logical units only
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Osmium.Desktop.Theming;

namespace Osmium.App;

/// <summary>What a <see cref="Ui.Tag"/> stands for, which sets its colour.</summary>
internal enum TagKind { Neutral, Accent, Warning, Danger, Success }

/// <summary>
/// A short state label, as the product page's <c>.tag</c>: capitals in the monospaced face inside a pill with a
/// one-pixel border in the text's colour. The word carries the meaning; the colour only repeats it.
/// </summary>
internal sealed class TagLabel : Label
{
    private TagKind _kind;

    public TagLabel(string text, TagKind kind)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        UseMnemonic = false;
        AutoSize = true;
        Tag = "keep"; // painted from the palette, not coloured by Theme.Apply
        _kind = kind;
        Text = text;
        AccessibleRole = AccessibleRole.StaticText;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TagKind Kind
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    [AllowNull]
    public override string Text
    {
        get => base.Text;
        set
        {
            base.Text = value;
            Font = Theme.FontFor(TypeRole.Tag, value ?? "");
            AccessibleName = value;
        }
    }

    private string Shown => TypeScale.Cased(TypeRole.Tag, Text);

    public Color InkColor => _kind switch
    {
        TagKind.Accent => Theme.P.AccentInk,
        TagKind.Warning => Theme.P.WarningText,
        TagKind.Danger => Theme.P.Danger,
        TagKind.Success => Theme.P.SuccessText,
        _ => Theme.P.TextSecondary,
    };

    private const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding
        | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Shown.Length == 0 ? " " : Shown, Font, Size.Empty, Flags);
        int across = LogicalToDeviceUnits(8), down = LogicalToDeviceUnits(2), border = 2; // one pixel of border each side
        return new Size(text.Width + across + across + border, text.Height + down + down + border);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Draw.Behind(this));
        var color = Enabled ? InkColor : Theme.P.Muted;
        Draw.RoundedBox(g, ClientRectangle, ClientSize.Height / 2f, null, color);
        TextRenderer.DrawText(g, Shown, Font, ClientRectangle, color, Flags);
    }
}
