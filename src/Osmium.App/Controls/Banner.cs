using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Osmium.App;

internal enum BannerKind { Info, Warning, Danger, Success }

/// <summary>
/// A message across a page or dialog (information, a warning, an error, a success), with an optional action at its
/// end. It is as tall as its text needs at its width; dock it to the top or put it in a layout row.
/// </summary>
internal sealed class Banner : Control
{
    private readonly Label _text = new() { AutoSize = false, Dock = DockStyle.Fill, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft, Tag = "keep" };
    private Button? _action;
    private BannerKind _kind;

    public Banner(BannerKind kind = BannerKind.Info, string text = "")
    {
        Padding = Space.Pad(this, Space.Sm, Space.Sm, Space.Sm, Space.Sm);
        Margin = new Padding(0, 0, 0, Space.Px(this, Space.Sm));
        Controls.Add(_text);
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
            var p = Theme.P;
            BackColor = p.Card;
            _text.ForeColor = value switch
            {
                BannerKind.Warning => p.WarningText,
                BannerKind.Danger => p.Danger,
                BannerKind.Success => p.SuccessText,
                _ => p.Text,
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

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width is > 0 and < int.MaxValue ? proposedSize.Width : Width;
        int textWidth = Math.Max(1, width - Padding.Horizontal - (_action?.Width ?? 0));
        var text = TextRenderer.MeasureText(_text.Text.Length == 0 ? " " : _text.Text, _text.Font, new Size(textWidth, 0), TextFormatFlags.WordBreak);
        int height = Math.Max(text.Height, _action?.PreferredSize.Height ?? 0) + Padding.Vertical;
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
        base.OnPaint(e);
        var p = Theme.P;
        var stripe = _kind switch { BannerKind.Warning => p.Warning, BannerKind.Danger => p.Danger, BannerKind.Success => p.Success, _ => p.Accent };
        using var border = new Pen(p.Line);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using var brush = new SolidBrush(stripe);
        e.Graphics.FillRectangle(brush, 0, 0, Space.Px(this, 3), Height);
    }

    private void FitHeight()
    {
        int h = GetPreferredSize(new Size(Width, 0)).Height;
        if (Height != h) Height = h;
    }
}
