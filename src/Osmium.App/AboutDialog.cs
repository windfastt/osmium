// Layout: logical units only
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Osmium.Desktop;

namespace Osmium.App;

/// <summary>
/// Version, engine fingerprint and licence, with buttons that open the licence files shipped next to the program
/// (LICENSE.txt, THIRD-PARTY-NOTICES.txt; the packaging step copies them into the program folder).
/// </summary>
internal sealed class AboutDialog : Form
{
    public const string ProjectUrl = "https://github.com/windfastt/osmium";

    public AboutDialog(Icon icon)
    {
        var asm = typeof(AboutDialog).Assembly;
        string version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Application.ProductVersion;
        string? copyright = asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;

        // Scaled for the monitor's DPI before any child exists; sizes below are in device pixels.
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        PerformAutoScale();
        Text = "About Osmium";
        Icon = icon;
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Space.Pad(this, Space.Lg, Space.Lg, Space.Lg, Space.Lg);

        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        t.Controls.Add(Ui.Heading("Osmium"));
        t.Controls.Add(Ui.Text("Version " + AboutInfo.DisplayVersion(version)));
        t.Controls.Add(Ui.Text("Engine fingerprint " + AboutInfo.EngineFingerprint(), muted: true));
        if (copyright is not null) t.Controls.Add(Ui.Text(copyright, muted: true));
        var license = Ui.Text("MIT License — provided as is, without warranty of any kind.", maxWidth: 440);
        license.Margin = Space.Pad(license, 0, 10, 0, 2);
        t.Controls.Add(license);
        var link = new LinkLabel { Text = ProjectUrl, AutoSize = true, LinkColor = Theme.P.AccentInk, ActiveLinkColor = Theme.P.AccentInk, UseMnemonic = false };
        link.Margin = Space.Pad(link, 0, 2, 0, 10);
        link.LinkClicked += (_, _) => Open(ProjectUrl);
        t.Controls.Add(link);
        var close = Ui.Button("Close");
        close.DialogResult = DialogResult.OK;
        t.Controls.Add(Ui.Bar(
            Ui.Button("License", (_, _) => OpenNotice("LICENSE.txt")),
            Ui.Button("Third-party notices", (_, _) => OpenNotice("THIRD-PARTY-NOTICES.txt")),
            close));
        Controls.Add(t);
        AcceptButton = close;
        CancelButton = close;
        Theme.Apply(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(this);
    }

    /// <summary>Opens a licence file from the program folder with its default program (normally Notepad).</summary>
    private static void OpenNotice(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, file);
        if (!File.Exists(path))
        {
            Ui.Info($"{file} is not in the program folder ({AppContext.BaseDirectory}). It is published with Osmium's source code at {ProjectUrl}.");
            return;
        }
        Open(path);
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { Ui.Error($"Could not open {target}: {e.Message}"); }
    }
}
