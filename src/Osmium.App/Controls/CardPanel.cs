using System.ComponentModel;

namespace Osmium.App;

/// <summary>
/// A card: content on the card colour inside a one-pixel line, with an optional heading above it. Built with
/// <see cref="Ui.Card"/>; the content fills the card.
/// </summary>
internal sealed class CardPanel : Panel
{
    private readonly Label _heading = new() { AutoSize = true, Dock = DockStyle.Top, UseMnemonic = false, Tag = "keep" };

    public CardPanel(Control content, string? heading = null)
    {
        Content = content;
        Tag = "keep"; // Theme.Apply leaves the card colour alone
        BackColor = Theme.P.Card;
        ForeColor = Theme.P.Text;
        Padding = Space.Pad(this, Space.Md, Space.Md, Space.Md, Space.Md);
        _heading.Font = Theme.CardHeading;
        _heading.ForeColor = Theme.P.Text;
        _heading.Padding = new Padding(0, 0, 0, Space.Px(this, Space.Sm));
        content.Dock = DockStyle.Fill;
        Controls.Add(content);
        Controls.Add(_heading); // added last, so it docks first and the content fills the rest
        Heading = heading;
        SetStyle(ControlStyles.ResizeRedraw, true);
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
            _heading.Visible = _headingText is not null;
        }
    }

    private string? _headingText;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var line = new Pen(Theme.P.Line);
        e.Graphics.DrawRectangle(line, 0, 0, Width - 1, Height - 1);
    }
}
