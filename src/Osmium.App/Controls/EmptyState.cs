using System.ComponentModel;

namespace Osmium.App;

/// <summary>
/// What a list shows while it has no rows: a glyph, a title, a hint and an optional action ("Add files…"). Attached to
/// a <see cref="FastList"/> it covers the list while the list has no rows (<c>Items.Count == 0</c>, or
/// <c>VirtualListSize == 0</c> in virtual mode), checked again each time the list's
/// <see cref="FastList.ItemsChanged"/> is called. Files dropped on it go to the list.
/// </summary>
internal sealed class EmptyState : Control
{
    // "keep": Theme.Apply leaves their background alone, so they show the empty state's own (the list's colour).
    private readonly Label _glyph = new() { AutoSize = true, UseMnemonic = false, Tag = "muted", Anchor = AnchorStyles.Top };
    private readonly Label _title = new() { AutoSize = true, UseMnemonic = false, Tag = "keep", Anchor = AnchorStyles.Top };
    private readonly Label _hint = new() { AutoSize = true, UseMnemonic = false, Tag = "muted", Anchor = AnchorStyles.Top };
    private readonly FlowLayoutPanel _stack = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Tag = "keep" };
    private Button? _action;
    private bool _highlight;

    public EmptyState(string glyph = "", string title = "", string hint = "")
    {
        _glyph.Font = Theme.GlyphLarge;
        _title.Font = Theme.CardHeading;
        _title.ForeColor = Theme.P.Text;
        _hint.MaximumSize = new Size(LogicalToDeviceUnits(420), 0);
        _glyph.Text = glyph;
        _title.Text = title;
        _hint.Text = hint;
        _stack.Controls.AddRange([_glyph, _title, _hint]);
        _stack.SizeChanged += (_, _) => Center();
        Controls.Add(_stack);
        BackColor = Theme.P.Card;
        ForeColor = Theme.P.Text;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get => _glyph.Text; set => _glyph.Text = value; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get => _title.Text; set => _title.Text = value; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Hint { get => _hint.Text; set => _hint.Text = value; }

    /// <summary>Marks the list as a drop target while something is dragged over it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Highlight
    {
        get => _highlight;
        set
        {
            _highlight = value;
            BackColor = value ? Theme.P.Subtle : Theme.P.Card;
        }
    }

    /// <summary>A button below the hint (one per empty state: a second call replaces it).</summary>
    public void SetAction(string text, Action onClick)
    {
        if (_action is not null)
        {
            _stack.Controls.Remove(_action);
            _action.Dispose();
        }
        _action = Ui.Button(text, (_, _) => onClick());
        _action.Anchor = AnchorStyles.Top;
        _action.Margin = new Padding(0, Space.Px(this, Space.Sm), 0, 0);
        _stack.Controls.Add(_action);
        Theme.Apply(_stack);
        _stack.BackColor = Color.Empty; // back to the empty state's own
    }

    /// <summary>Shows this over <paramref name="list"/> while it has no rows (the same as setting <see cref="FastList.Empty"/>).</summary>
    public void AttachTo(FastList list) => list.Empty = this;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Center();
    }

    private void Center() =>
        _stack.Location = new Point(Math.Max(0, (ClientSize.Width - _stack.Width) / 2), Math.Max(0, (ClientSize.Height - _stack.Height) / 2));
}
