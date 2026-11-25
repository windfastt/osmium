// Layout: logical units only
using System.ComponentModel;

namespace Osmium.App;

/// <summary>
/// A label that wraps its text at the width it has: as wide as the nearest container that does not size itself to its
/// content allows (less the paddings and margins in between, or its table column's width), and never wider than
/// <see cref="Cap"/> logical pixels, for readable lines. It grows as tall as the wrapped text needs, so nothing is cut.
/// The width is worked out again when that container is resized or moves to a monitor with another DPI.
/// </summary>
internal sealed class WrapLabel : Label
{
    private Control? _host;
    private int _cap = 760;
    private bool _fitting;

    public WrapLabel()
    {
        AutoSize = true;
        UseMnemonic = false;
    }

    /// <summary>The widest the text may run, in logical pixels (760 unless set).</summary>
    [DefaultValue(760)]
    public int Cap
    {
        get => _cap;
        set
        {
            _cap = Math.Max(40, value);
            Fit();
        }
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        Rehost();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Rehost(); // the ancestors are complete by now
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Fit();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (MaximumSize.Width == 0) Fit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _host is not null) _host.SizeChanged -= HostResized;
        base.Dispose(disposing);
    }

    /// <summary>The nearest ancestor that does not size itself to its content.</summary>
    private Control? FindHost()
    {
        Control? c = Parent;
        while (c is not null && c.AutoSize && c.Parent is not null) c = c.Parent;
        return c;
    }

    private void Rehost()
    {
        var host = FindHost();
        if (!ReferenceEquals(host, _host))
        {
            if (_host is not null) _host.SizeChanged -= HostResized;
            _host = host;
            if (_host is not null) _host.SizeChanged += HostResized;
        }
        Fit();
    }

    private void HostResized(object? sender, EventArgs e) => Fit();

    /// <summary>The width the text may use in device pixels, or null while the label has no sized container.</summary>
    internal int? AvailableWidth()
    {
        // A container that sizes itself to its content (an auto-sized dialog) gives no width: the cap decides.
        if (_host is null || _host.AutoSize || _host.ClientSize.Width <= 0) return null;
        int width;
        if (Parent is TableLayoutPanel table && ReferenceEquals(table, _host) && table.ColumnCount > 1
            && table.GetPositionFromControl(this).Column is var col and >= 0)
        {
            int[] widths = table.GetColumnWidths();
            int span = Math.Max(1, table.GetColumnSpan(this));
            width = widths.Skip(col).Take(span).Sum();
        }
        else
        {
            width = _host.ClientSize.Width - _host.Padding.Horizontal;
            for (Control? c = Parent; c is not null && !ReferenceEquals(c, _host); c = c.Parent)
                width -= c.Padding.Horizontal + c.Margin.Horizontal;
        }
        if (_host is ScrollableControl { VerticalScroll.Visible: true }) width -= SystemInformation.VerticalScrollBarWidth;
        return width - Margin.Horizontal;
    }

    private void Fit()
    {
        if (_fitting) return;
        int cap = LogicalToDeviceUnits(_cap);
        int width = AvailableWidth() is { } available ? Math.Clamp(available, LogicalToDeviceUnits(40), cap) : cap;
        if (MaximumSize.Width == width) return;
        _fitting = true;
        var parent = Parent;
        try
        {
            parent?.SuspendLayout();
            MaximumSize = new Size(width, 0);
        }
        finally
        {
            parent?.ResumeLayout(true);
            _fitting = false;
        }
    }
}
