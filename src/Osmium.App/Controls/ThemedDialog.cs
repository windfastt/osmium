namespace Osmium.App;

/// <summary>
/// Base of Osmium's dialogs: scaled for the monitor's DPI, in the body font and the current theme, a fixed border,
/// centred on its owner, without minimize and maximize buttons and without a taskbar button. Put the dialog's buttons
/// in <see cref="ButtonBar"/> (they line up from the right, the first one added rightmost) and the content docked to
/// fill the rest.
/// </summary>
internal class ThemedDialog : Form
{
    protected ThemedDialog(string title)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Text = title;
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ButtonBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Padding = new Padding(12, 8, 12, 12),
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
        Theme.Apply(this);
        base.OnLoad(e);
    }
}
