// Layout: logical units only
using System.ComponentModel;

namespace Osmium.App;

/// <summary>
/// One choice out of a few, side by side ("Ask · Rename · Replace"), as the product page's <c>.seg</c>: one rounded
/// one-pixel border, soft lines between the segments, the selected one on the sunk colour in the strong face. It
/// behaves as a group of radio buttons: Tab enters and leaves it as one stop, the arrow keys move to the next or
/// previous segment and select it, Space selects the segment with the focus, and screen readers see one radio button
/// per segment. The focus ring is drawn outside the border, as on buttons. As wide and tall as its items.
/// </summary>
internal sealed class SegmentedChoice : Control
{
    private readonly string[] _items;
    private int _selected = -1, _focus, _hover = -1;
    private bool _forceFocus;

    public SegmentedChoice(params string[] items)
    {
        _items = [.. items];
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                 | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        Tag = "keep";
        Font = Theme.Body;
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = string.Join(", ", _items); // callers that have a label for the group set their own
        Size = GetPreferredSize(Size.Empty);
    }

    /// <summary>The selected item's position, or -1 when none is selected.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < -1 || value >= _items.Length) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == _selected) return;
            _selected = value;
            if (value >= 0) _focus = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            if (value >= 0) AccessibilityNotifyClients(AccessibleEvents.Selection, value);
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when another item becomes selected.</summary>
    public event EventHandler? SelectedIndexChanged;

    internal IReadOnlyList<string> Items => _items;

    /// <summary>Draws the focus ring regardless of focus (the self-test's specimen only).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool ForceFocusLook
    {
        get => _forceFocus;
        set { _forceFocus = value; Invalidate(); }
    }

    private int Ring => LogicalToDeviceUnits(4);

    private int SegmentWidth(int i) =>
        TextRenderer.MeasureText(_items[i].Length == 0 ? " " : _items[i], Theme.BodyStrong, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width
        + 2 * LogicalToDeviceUnits(14);

    /// <summary>The segments' rectangles, inside the ring's margin.</summary>
    internal Rectangle Segment(int i)
    {
        int x = Ring;
        for (int j = 0; j < i; j++) x += SegmentWidth(j);
        return new Rectangle(x, Ring, SegmentWidth(i), Math.Max(1, ClientSize.Height - 2 * Ring));
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = 0;
        for (int i = 0; i < _items.Length; i++) width += SegmentWidth(i);
        int height = Math.Max(LogicalToDeviceUnits(32), Theme.BodyStrong.Height + LogicalToDeviceUnits(14));
        return new Size(width + Ring + Ring, height + Ring + Ring);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Size = GetPreferredSize(Size.Empty);
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _items.Length; i++) if (Segment(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.Location);
        if (hit != _hover) { _hover = hit; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        int hit = HitTest(e.Location);
        if (hit >= 0) SelectedIndex = hit;
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & ~Keys.Shift) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Length == 0) return;
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Up:
                SelectedIndex = _focus = (_focus - 1 + _items.Length) % _items.Length;
                e.Handled = true;
                break;
            case Keys.Right or Keys.Down:
                SelectedIndex = _focus = (_focus + 1) % _items.Length;
                e.Handled = true;
                break;
            case Keys.Space:
                SelectedIndex = _focus;
                e.Handled = true;
                break;
        }
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_selected >= 0) _focus = _selected;
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.Focus, _focus);
    }

    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(Draw.Behind(this));
        int ring = Ring;
        var face = Rectangle.Inflate(ClientRectangle, -ring, -ring);
        float radius = LogicalToDeviceUnits(8);
        bool focused = _forceFocus || (Focused && ShowFocusCues);
        Draw.RoundedBox(g, face, radius, p.Card, null);
        using (var clip = Draw.Rounded(face, radius))
        {
            var state = g.Save();
            g.SetClip(clip);
            for (int i = 0; i < _items.Length; i++)
            {
                var r = Segment(i);
                bool selected = i == _selected;
                if (selected) g.FillRectangle(Paints.Brush(Enabled ? p.Sunk : p.Card), r);
                else if (i == _hover && Enabled) g.FillRectangle(Paints.Brush(p.Subtle), r);
                if (i > 0) g.DrawLine(Paints.Pen(p.LineSoft), r.X, face.Top + 1, r.X, face.Bottom - 2);
                var color = !Enabled ? p.Muted : selected ? p.Text : p.TextSecondary;
                TextRenderer.DrawText(g, _items[i], selected ? Theme.BodyStrong : Theme.Body, r, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                if (focused && i == _focus && _selected != _focus)
                    g.DrawLine(Paints.Pen(p.Focus, LogicalToDeviceUnits(2)), r.X + ring, r.Bottom - ring, r.Right - ring, r.Bottom - ring);
            }
            g.Restore(state);
        }
        Draw.RoundedBox(g, face, radius, null, p.Line);
        if (focused) Draw.RoundedBox(g, ClientRectangle, radius + ring, null, p.Focus, LogicalToDeviceUnits(2));
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new GroupAccessible(this);

    private sealed class GroupAccessible(SegmentedChoice owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.Grouping;
        public override int GetChildCount() => owner._items.Length;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < owner._items.Length ? new SegmentAccessible(owner, this, index) : null;
        public override AccessibleObject? GetFocused() => owner.Focused ? GetChild(owner._focus) : base.GetFocused();
        public override AccessibleObject? GetSelected() => owner._selected >= 0 ? GetChild(owner._selected) : null;
    }

    private sealed class SegmentAccessible(SegmentedChoice owner, AccessibleObject parent, int index) : AccessibleObject
    {
        public override string? Name => owner._items[index];
        public override AccessibleRole Role => AccessibleRole.RadioButton;
        public override AccessibleObject? Parent => parent;
        public override Rectangle Bounds => owner.RectangleToScreen(owner.Segment(index));
        public override string? DefaultAction => "Select";
        public override AccessibleStates State
        {
            get
            {
                var s = AccessibleStates.Focusable | AccessibleStates.Selectable;
                if (index == owner._selected) s |= AccessibleStates.Checked | AccessibleStates.Selected;
                if (owner.Focused && index == owner._focus) s |= AccessibleStates.Focused;
                if (!owner.Enabled) s |= AccessibleStates.Unavailable;
                return s;
            }
        }
        public override void DoDefaultAction() => owner.SelectedIndex = index;
        public override void Select(AccessibleSelection flags)
        {
            if ((flags & (AccessibleSelection.TakeSelection | AccessibleSelection.AddSelection)) != 0) owner.SelectedIndex = index;
            if ((flags & AccessibleSelection.TakeFocus) != 0) owner.Focus();
        }
    }
}
