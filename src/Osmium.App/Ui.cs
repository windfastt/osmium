// Layout: logical units only
using System.Globalization;
using Osmium.Desktop.Settings;
using Osmium.Desktop.Theming;

namespace Osmium.App;

/// <summary>Small factory helpers so code-built layouts stay readable and DPI-safe (no absolute pixel layouts).</summary>
internal static class Ui
{
    /// <summary>How wide ledes and descriptions run at most, in logical pixels, for readable lines.</summary>
    public const int ReadableWidth = 760;

    // Headings and text show '&' as is ("Rules & patches", file names): with mnemonics on, it would underline the
    // next letter instead.
    public static Label Heading(string text)
    {
        var l = new Label { Text = text, Font = Theme.FontFor(TypeRole.Title, text), AutoSize = true, UseMnemonic = false };
        l.Margin = Space.Pad(l, 0, 0, 0, 6);
        return l;
    }

    /// <summary>Text that wraps at the width it has, never wider than <paramref name="maxWidth"/> logical pixels
    /// (<see cref="ReadableWidth"/> when 0).</summary>
    public static Label Text(string text, bool muted = false, int maxWidth = 0)
    {
        var l = new WrapLabel { Text = text, Tag = muted ? "muted" : null, Cap = maxWidth > 0 ? maxWidth : ReadableWidth };
        l.Margin = Space.Pad(l, 0, 2, 0, 2);
        return l;
    }

    /// <summary>A button in the product page's style: quiet (ghost), or filled with the accent colour when
    /// <paramref name="accent"/> is set. It keeps four pixels around its face for the focus ring, so buttons in a row
    /// sit eight pixels apart without margins.</summary>
    public static Button Button(string text, EventHandler? click = null, bool accent = false)
    {
        var b = new OsmiumButton { Text = text, IsPrimary = accent, Tag = accent ? "accent" : null };
        b.Margin = Padding.Empty;
        if (click is not null) b.Click += click;
        return b;
    }

    /// <summary>A "Show"/"Hide" button that unmasks or masks <paramref name="box"/>; its text says what a click does.</summary>
    public static Button RevealButton(TextBox box)
    {
        var b = Button(box.UseSystemPasswordChar ? "Show" : "Hide");
        b.Click += (_, _) => SetMasked(box, b, !box.UseSystemPasswordChar);
        return b;
    }

    /// <summary>Masks or unmasks a box and keeps its <see cref="RevealButton"/> text in step.</summary>
    public static void SetMasked(TextBox box, Button reveal, bool masked)
    {
        box.UseSystemPasswordChar = masked;
        reveal.Text = masked ? "Show" : "Hide";
    }

    /// <summary>Copies text to the clipboard; another program holding the clipboard is reported, not thrown.</summary>
    public static void CopyToClipboard(string text)
    {
        if (text.Length == 0) return; // SetText throws on an empty string
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { Error("Another program is using the clipboard. Try again."); }
    }

    /// <summary>A row of controls (a tool bar, a row of buttons) that wraps onto more lines when it is too narrow.</summary>
    public static FlowLayoutPanel Bar(params Control[] controls)
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
        f.Padding = Padding.Empty;
        f.Margin = Space.Pad(f, 0, 0, 0, Space.Md);
        f.Controls.AddRange(controls);
        return f;
    }

    public static ComboBox Combo(IEnumerable<string> items, string? selected = null)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        c.Width = c.LogicalToDeviceUnits(140);
        c.Margin = Space.Pad(c, 4, 8, 4, 0); // centred on the faces of the buttons beside it
        foreach (var i in items) c.Items.Add(i);
        if (selected is not null && c.Items.Contains(selected)) c.SelectedItem = selected;
        else if (c.Items.Count > 0) c.SelectedIndex = 0;
        return c;
    }

    /// <summary>A details list with these columns (widths in logical pixels).</summary>
    public static FastList List(params (string Name, int Width)[] cols)
    {
        var l = new FastList();
        foreach (var (n, w) in cols) l.Columns.Add(n, w);
        return l;
    }

    /// <summary>A page: heading, optional subtitle, then the given content filling the rest.</summary>
    public static Control Page(string title, string subtitle, Control content, params Control[] top) =>
        Page(new PageHeader(title, subtitle), content, top);

    /// <summary>A page: its header's lines, the <paramref name="top"/> rows (tool bars, status lines), then
    /// <paramref name="content"/> filling the rest. The header can be changed while the page is shown. Everything is as
    /// wide as the window: tool bars and text wrap, lists scroll inside themselves.</summary>
    public static Control Page(PageHeader header, Control content, params Control[] top)
    {
        // 24 logical pixels to the window's edges. Rows of buttons start 4 pixels further out, where their faces keep
        // the room for the focus ring, so the faces line up with the text and the cards.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = false };
        root.Padding = Space.Pad(root, Space.Xl - Space.Xs, 20, Space.Xl - Space.Xs, Space.Lg);
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int edge = root.LogicalToDeviceUnits(Space.Xs);
        // Every row is placed by number: a hidden line (an empty spec) leaves its row empty, so the rows after it keep
        // their styles.
        int row = 0;
        foreach (var line in header.Lines)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            line.Margin = new Padding(edge, line.Margin.Top, edge, line.Margin.Bottom);
            root.Controls.Add(line, 0, row++);
        }
        foreach (var t in top)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Dock = DockStyle.Fill;
            int side = t is FlowLayoutPanel ? 0 : edge;
            t.Margin = new Padding(side, 0, side, root.LogicalToDeviceUnits(Space.Md));
            root.Controls.Add(t, 0, row++);
        }
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Dock = DockStyle.Fill;
        content.Margin = new Padding(edge, 0, edge, 0);
        root.Controls.Add(content, 0, row);
        return root;
    }

    /// <summary>Content in a card, with an optional heading above it.</summary>
    public static CardPanel Card(Control content, string? heading = null) => new(content, heading);

    /// <summary>The page's main action: a primary button, 36 logical pixels high.</summary>
    public static Button Primary(string text, EventHandler? click = null)
    {
        var b = (OsmiumButton)Button(text, click, accent: true);
        b.Large = true;
        return b;
    }

    /// <summary>A line of facts in the spec style: capitals in the product page's monospaced face, muted.</summary>
    public static Label Spec(string text)
    {
        var l = new Label { Text = TypeScale.Cased(TypeRole.Spec, text), Font = Theme.FontFor(TypeRole.Spec, text), AutoSize = true, Tag = "muted", UseMnemonic = false };
        l.Margin = Space.Pad(l, 0, 2, 0, 2);
        return l;
    }

    /// <summary>A short label for a state: EXPERIMENTAL, CAUTION, ENCRYPTED…</summary>
    public static Label Tag(string text, TagKind kind = TagKind.Neutral)
    {
        var l = new TagLabel(text, kind);
        l.Margin = Space.Pad(l, 0, 2, 6, 2);
        return l;
    }

    /// <summary>A one-pixel line across its parent, between sections.</summary>
    public static Control Divider()
    {
        var d = new Panel { Dock = DockStyle.Top, BackColor = Theme.P.Line, Tag = "keep" };
        d.Height = 1; // layout: device pixels on purpose (a hairline stays one pixel at every scale)
        d.Margin = Space.Pad(d, 0, 6, 0, 6);
        return d;
    }

    public static string Bytes(long b) => Osmium.Desktop.Tray.TrayText.Bytes(b);

    /// <summary>
    /// Set only by the UI self-test, which runs unattended in CI: a message box would wait for a click that never
    /// comes, so its text is reported here instead (and a question is answered No).
    /// </summary>
    internal static Action<string>? Unattended { get; set; }

    public static void Info(string text, string title = "Osmium")
    {
        if (Unattended is { } report) { report($"{title}: {text}"); return; }
        ShowMessage(BuildMessage(MessageKind.Information, text, title));
    }

    public static void Error(string text, string title = "Osmium")
    {
        if (Unattended is { } report) { report($"{title}: {text}"); return; }
        ShowMessage(BuildMessage(MessageKind.Error, text, title));
    }

    /// <summary>A yes-or-no question. No is the default answer: Enter and Esc both give it.</summary>
    public static bool Confirm(string text, string title = "Osmium")
    {
        if (Unattended is { } report) { report($"{title}: {text}"); return false; }
        return ShowMessage(BuildMessage(MessageKind.Question, text, title, "Yes", "No")) == DialogResult.Yes;
    }

    /// <summary>
    /// A question answered with the caller's own words ("Extract anyway" / "Stop"). <paramref name="no"/> is the default
    /// answer and Esc gives it, unless <paramref name="yesIsDefault"/>. True when the answer was <paramref name="yes"/>.
    /// </summary>
    public static bool Ask(string text, string yes, string no, string title = "Osmium", bool yesIsDefault = false)
    {
        if (Unattended is { } report) { report($"{title}: {text}"); return false; }
        return ShowMessage(BuildMessage(MessageKind.Question, text, title, yes, no, yesIsDefault)) == DialogResult.Yes;
    }

    /// <summary>A message dialog, not shown yet: one button (<paramref name="no"/>, or OK) without <paramref name="yes"/>,
    /// otherwise the two answers.</summary>
    internal static MessageDialog BuildMessage(MessageKind kind, string text, string title = "Osmium", string? yes = null, string? no = null, bool yesIsDefault = false) =>
        new(kind, text, title, yes, no, yesIsDefault);

    /// <summary>Shows a message over the active Osmium window, or on its own in front of everything when there is none
    /// (a message from the notification area).</summary>
    private static DialogResult ShowMessage(MessageDialog dialog)
    {
        using (dialog)
        {
            var owner = Form.ActiveForm is { IsDisposed: false, Visible: true } active && active != dialog ? active : null;
            if (owner is null)
            {
                dialog.StartPosition = FormStartPosition.CenterScreen;
                dialog.TopMost = true;
            }
            return dialog.ShowDialog(owner);
        }
    }

    /// <summary>Runs work off the UI thread and marshals the result back; exceptions are shown, never crash the tray.</summary>
    public static async void RunSafe(Control owner, Func<Task> work) => await RunSafeAsync(owner, work);

    /// <summary>As <see cref="RunSafe"/>, for callers that wait: the task completes (never faults) when the work has
    /// finished, its error shown.</summary>
    public static async Task RunSafeAsync(Control owner, Func<Task> work)
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


/// <summary>
/// TextBox with the keyboard behaviour Osmium wants on top of WinForms (which already makes Ctrl+Backspace delete the
/// previous word): Enter in a single-line box runs <see cref="EnterAction"/> instead of the edit control's error
/// beep, and Ctrl+Backspace in a masked box deletes everything before the caret, so it does not reveal where the
/// words of a password are.
/// </summary>
internal sealed class EditBox : TextBox
{
    /// <summary>What Enter does in a single-line box (null: nothing, but no beep). Multi-line boxes keep Enter.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? EnterAction { get; set; }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (UseSystemPasswordChar && !ReadOnly && keyData is (Keys.Control | Keys.Back) or (Keys.Control | Keys.Shift | Keys.Back))
        {
            if (SelectionLength == 0) Select(0, SelectionStart);
            SelectedText = "";
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Multiline || e.KeyData != Keys.Enter) return;
        e.SuppressKeyPress = true; // no WM_CHAR, so no beep
        EnterAction?.Invoke();
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
        Height = LogicalToDeviceUnits(40);
        Width = LogicalToDeviceUnits(190);
        Margin = Space.Pad(this, 4, 2, 4, 2);
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

internal static class Num
{
    public static string Pct(double v) => v.ToString("P1", CultureInfo.CurrentCulture);
}
