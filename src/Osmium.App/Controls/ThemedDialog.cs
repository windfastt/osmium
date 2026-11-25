// Layout: logical units only
namespace Osmium.App;

/// <summary>
/// Base of Osmium's dialogs: scaled for the monitor's DPI, in the body font and the current theme, a fixed border,
/// centred on its owner, without minimize and maximize buttons and without a taskbar button. Put the dialog's buttons
/// in <see cref="ButtonBar"/> (they line up from the right, the first one added rightmost: the primary action first)
/// and the content docked to fill the rest. The button bar sits on the page colour under a soft rule and stays visible
/// however small the dialog gets; Esc presses the <see cref="Form.CancelButton"/>.
/// </summary>
internal class ThemedDialog : Form
{
    protected ThemedDialog(string title)
    {
        // Scaled for the monitor's DPI now, before any child exists: sizes below and in derived dialogs are given in
        // device pixels (LogicalToDeviceUnits, Space.Px), and a later DPI change scales them all together.
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        PerformAutoScale();
        Text = title;
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.P.Page;
        ButtonBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Tag = "keep", BackColor = Theme.P.Page,
        };
        // The buttons keep 4 pixels around their faces for the focus ring: the faces sit 24 pixels from the sides,
        // 12 below the rule and 16 above the bottom edge.
        ButtonBar.Padding = Space.Pad(ButtonBar, Space.Xl - Space.Xs, Space.Md - Space.Xs, Space.Xl - Space.Xs, Space.Lg - Space.Xs);
        ButtonBar.Paint += (_, e) =>
        {
            using var rule = new Pen(Theme.P.LineSoft);
            e.Graphics.DrawLine(rule, 0, 0, ButtonBar.Width, 0);
        };
        Controls.Add(ButtonBar);
    }

    /// <summary>The row of buttons along the bottom, right to left.</summary>
    protected FlowLayoutPanel ButtonBar { get; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        ButtonBar.SendToBack(); // docks first, so content docked to fill stays above it
        foreach (Control b in ButtonBar.Controls) b.Margin = Space.Pad(b, Space.Xs, 0, 0, 0); // faces 12 pixels apart
        Theme.Apply(this);
        base.OnLoad(e);
    }
}
