using System.Globalization;
using Osmium.Desktop.Settings;

namespace Osmium.App;

/// <summary>Small factory helpers so code-built layouts stay readable and DPI-safe (no absolute pixel layouts).</summary>
internal static class Ui
{
    public static Label Heading(string text) => new() { Text = text, Font = Theme.Heading, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };

    public static Label Text(string text, bool muted = false, int maxWidth = 0) => new()
    {
        Text = text, AutoSize = true, Tag = muted ? "muted" : null,
        MaximumSize = maxWidth > 0 ? new Size(maxWidth, 0) : Size.Empty,
        Margin = new Padding(0, 2, 0, 2),
    };

    public static Button Button(string text, EventHandler? click = null, bool accent = false)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 0, 6, 0), Tag = accent ? "accent" : null };
        if (click is not null) b.Click += click;
        return b;
    }

    public static FlowLayoutPanel Bar(params Control[] controls)
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(0, 4, 0, 6) };
        f.Controls.AddRange(controls);
        return f;
    }

    public static ComboBox Combo(IEnumerable<string> items, string? selected = null)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Margin = new Padding(0, 2, 6, 0) };
        foreach (var i in items) c.Items.Add(i);
        if (selected is not null && c.Items.Contains(selected)) c.SelectedItem = selected;
        else if (c.Items.Count > 0) c.SelectedIndex = 0;
        return c;
    }

    public static FastList List(params (string Name, int Width)[] cols)
    {
        var l = new FastList();
        foreach (var (n, w) in cols) l.Columns.Add(n, w);
        return l;
    }

    /// <summary>A page: heading, optional subtitle, then the given content filling the rest.</summary>
    public static Control Page(string title, string subtitle, Control content, params Control[] top)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(Heading(title));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(Text(subtitle, muted: true, maxWidth: 900));
        foreach (var t in top)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Dock = DockStyle.Fill;
            root.Controls.Add(t);
        }
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Dock = DockStyle.Fill;
        root.Controls.Add(content);
        return root;
    }

    public static string Bytes(long b) => Osmium.Desktop.Tray.TrayText.Bytes(b);

    public static void Info(string text, string title = "Osmium") => MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    public static void Error(string text, string title = "Osmium") => MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    public static bool Confirm(string text, string title = "Osmium") => MessageBox.Show(text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

    /// <summary>Runs work off the UI thread and marshals the result back; exceptions are shown, never crash the tray.</summary>
    public static async void RunSafe(Control owner, Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            AppLog.Error(e.ToString());
            if (!owner.IsDisposed) Error(e.Message);
        }
    }
}

/// <summary>Flicker-free details ListView.</summary>
internal sealed class FastList : ListView
{
    private int _scaledFor = 96;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (DeviceDpi != _scaledFor)
        {
            foreach (ColumnHeader c in Columns) c.Width = c.Width * DeviceDpi / _scaledFor;
            _scaledFor = DeviceDpi;
        }
    }

    public FastList()
    {
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        DoubleBuffered = true;
        BorderStyle = BorderStyle.None;
    }
}

/// <summary>Owner-drawn navigation button with a glyph and a selection marker.</summary>
internal sealed class NavButton : Button
{
    public string Glyph { get; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }
    private bool _selected, _hover;

    public NavButton(string text, string glyph)
    {
        Text = text;
        Glyph = glyph;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Height = 40;
        Width = 190;
        Margin = new Padding(4, 2, 4, 2);
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(_selected ? p.Hover : _hover ? Color.FromArgb(Math.Min(255, p.Hover.R + 8), Math.Min(255, p.Hover.G + 8), Math.Min(255, p.Hover.B + 8)) : p.Background);
        if (_selected)
        {
            using var accent = new SolidBrush(p.Accent);
            int h = LogicalToDeviceUnits(18);
            g.FillRectangle(accent, 0, (Height - h) / 2, LogicalToDeviceUnits(3), h);
        }
        int pad = LogicalToDeviceUnits(14);
        TextRenderer.DrawText(g, Glyph, Theme.Glyph, new Rectangle(pad, 0, LogicalToDeviceUnits(24), Height), p.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(pad + LogicalToDeviceUnits(32), 0, Width - pad * 2, Height), p.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, new Rectangle(2, 2, Width - 4, Height - 4));
    }
}

internal static class PasswordDialog
{
    public static string? Ask(string prompt, bool confirm)
    {
        using var f = new Form
        {
            Text = "Osmium", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96f, 96f), Padding = new Padding(12), TopMost = true,
        };
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        t.Controls.Add(Ui.Text(prompt));
        var p1 = new TextBox { UseSystemPasswordChar = true, Width = 280 };
        t.Controls.Add(p1);
        TextBox? p2 = null;
        if (confirm)
        {
            t.Controls.Add(Ui.Text("Confirm password"));
            p2 = new TextBox { UseSystemPasswordChar = true, Width = 280 };
            t.Controls.Add(p2);
        }
        var ok = Ui.Button("OK", accent: true);
        ok.DialogResult = DialogResult.OK;
        var cancel = Ui.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        t.Controls.Add(Ui.Bar(ok, cancel));
        f.Controls.Add(t);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        Theme.Apply(f);
        while (true)
        {
            if (f.ShowDialog() != DialogResult.OK) return null;
            if (p1.Text.Length == 0) { Ui.Error("The password cannot be empty."); continue; }
            if (p2 is not null && p2.Text != p1.Text) { Ui.Error("The passwords do not match."); continue; }
            return p1.Text;
        }
    }
}

internal static class Num
{
    public static string Pct(double v) => v.ToString("P1", CultureInfo.CurrentCulture);
}
