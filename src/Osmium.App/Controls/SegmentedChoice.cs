using System.ComponentModel;

namespace Osmium.App;

/// <summary>
/// One choice out of a few, side by side ("Ask · Rename · Replace"), with radio-button behaviour: exactly one is
/// selected once the user picked one, and the arrow keys move between them. As wide and tall as its items.
/// </summary>
internal sealed class SegmentedChoice : Control
{
    private readonly FlowLayoutPanel _row = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly RadioButton[] _items;

    public SegmentedChoice(params string[] items)
    {
        _items = items.Select(text => new RadioButton { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, Space.Px(this, Space.Md), 0) }).ToArray();
        foreach (var b in _items) b.CheckedChanged += (_, _) => { if (b.Checked) SelectedIndexChanged?.Invoke(this, EventArgs.Empty); };
        _row.Controls.AddRange(_items);
        _row.SizeChanged += (_, _) => Size = _row.Size;
        Controls.Add(_row);
        Size = _row.PreferredSize;
        TabStop = false; // the radio buttons take the focus
    }

    /// <summary>The selected item's position, or -1 when none is selected.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => Array.FindIndex(_items, b => b.Checked);
        set
        {
            if (value < -1 || value >= _items.Length) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == -1) foreach (var b in _items) b.Checked = false;
            else _items[value].Checked = true;
        }
    }

    /// <summary>Raised when another item becomes selected.</summary>
    public event EventHandler? SelectedIndexChanged;

    public override Size GetPreferredSize(Size proposedSize) => _row.GetPreferredSize(proposedSize);
}
