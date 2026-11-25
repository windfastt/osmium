using System.Globalization;
using Osmium.Desktop.Settings;

namespace Osmium.App;

/// <summary>Small factory helpers so code-built layouts stay readable and DPI-safe (no absolute pixel layouts).</summary>
internal static class Ui
{
    // Headings and text show '&' as is ("Rules & patches", file names): with mnemonics on, it would underline the
    // next letter instead.
    public static Label Heading(string text) => new() { Text = text, Font = Theme.Heading, AutoSize = true, Margin = new Padding(0, 0, 0, 6), UseMnemonic = false };

    public static Label Text(string text, bool muted = false, int maxWidth = 0) => new()
    {
        Text = text, AutoSize = true, Tag = muted ? "muted" : null, UseMnemonic = false,
        MaximumSize = maxWidth > 0 ? new Size(maxWidth, 0) : Size.Empty,
        Margin = new Padding(0, 2, 0, 2),
    };

    public static Button Button(string text, EventHandler? click = null, bool accent = false)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 0, 6, 0), Tag = accent ? "accent" : null };
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
    public static Control Page(string title, string subtitle, Control content, params Control[] top) =>
        Page(new PageHeader(title, subtitle), content, top);

    /// <summary>A page: its header's lines, the <paramref name="top"/> rows (tool bars, status lines), then
    /// <paramref name="content"/> filling the rest. The header can be changed while the page is shown.</summary>
    public static Control Page(PageHeader header, Control content, params Control[] top)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 12) };
        // Every row is placed by number: a hidden line (an empty spec) leaves its row empty, so the rows after it keep
        // their styles.
        int row = 0;
        foreach (var line in header.Lines)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(line, 0, row++);
        }
        foreach (var t in top)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Dock = DockStyle.Fill;
            root.Controls.Add(t, 0, row++);
        }
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Dock = DockStyle.Fill;
        root.Controls.Add(content, 0, row);
        return root;
    }

    /// <summary>Content in a card, with an optional heading above it.</summary>
    public static CardPanel Card(Control content, string? heading = null) => new(content, heading);

    /// <summary>The page's main action.</summary>
    public static Button Primary(string text, EventHandler? click = null) => Button(text, click, accent: true);

    /// <summary>A line of facts in the spec style (muted, small).</summary>
    public static Label Spec(string text) => new() { Text = text, Font = Theme.Spec, AutoSize = true, Tag = "muted", UseMnemonic = false, Margin = new Padding(0, 2, 0, 2) };

    /// <summary>A short label for a state: EXPERIMENTAL, CAUTION, ENCRYPTED…</summary>
    public static Label Tag(string text, TagKind kind = TagKind.Neutral)
    {
        var l = new Label { Text = text, Font = Theme.Small, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 6, 0) };
        switch (kind)
        {
            case TagKind.Neutral: l.Tag = "muted"; break;
            case TagKind.Warning: l.Tag = "experimental"; break;
            case TagKind.Danger: l.Tag = "danger"; break;
            default:
                l.Tag = "keep"; // Theme.Apply leaves its colour alone
                l.ForeColor = kind == TagKind.Success ? Theme.P.SuccessText : Theme.P.AccentInk;
                break;
        }
        return l;
    }

    /// <summary>A one-pixel line across its parent, between sections.</summary>
    public static Control Divider() => new Panel { Height = 1, Dock = DockStyle.Top, BackColor = Theme.P.Line, Tag = "keep", Margin = new Padding(0, 6, 0, 6) };

    public static string Bytes(long b) => Osmium.Desktop.Tray.TrayText.Bytes(b);

    /// <summary>
    /// Set only by the UI self-test, which runs unattended in CI: a message box would wait for a click that never
    /// comes, so its text is reported here instead (and a question is answered No).
    /// </summary>
    internal static Action<string>? Unattended { get; set; }

    public static void Info(string text, string title = "Osmium")
    {
        if (Unattended is { } report) { report($"{title}: {text}"); return; }
        MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public static void Error(string text, string title = "Osmium")
    {
        if (Unattended is { } report) { report($"{title}: {text}"); return; }
        MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    public static bool Confirm(string text, string title = "Osmium")
    {
        if (Unattended is { } report) { report($"{title}: {text}"); return false; }
        return MessageBox.Show(text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
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

/// <summary>What a <see cref="Ui.Tag"/> stands for, which sets its colour.</summary>
internal enum TagKind { Neutral, Accent, Warning, Danger, Success }

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

/// <summary>
/// Flicker-free details ListView with list shortcuts: Ctrl+A selects every row, Ctrl+C copies the selected rows
/// (tab-separated, so they paste into a spreadsheet), Enter and double-click run <see cref="DefaultAction"/>.
/// </summary>
internal sealed class FastList : ListView
{
    private int _scaledFor = 96;

    /// <summary>The page's default action for the selected rows (Enter, double-click), if it has one.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? DefaultAction { get; set; }

    /// <summary>The selected rows, one per line, columns separated by tabs.</summary>
    public string SelectionAsText() => string.Join(Environment.NewLine, SelectedItems.Cast<ListViewItem>()
        .Select(i => string.Join("\t", i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text))));

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        switch (e.KeyData)
        {
            case Keys.Control | Keys.A when MultiSelect:
                BeginUpdate();
                foreach (ListViewItem i in Items) i.Selected = true;
                EndUpdate();
                e.SuppressKeyPress = true;
                break;
            case Keys.Control | Keys.C:
                Ui.CopyToClipboard(SelectionAsText());
                e.SuppressKeyPress = true;
                break;
            case Keys.Enter when DefaultAction is not null && SelectedItems.Count > 0:
                DefaultAction();
                e.SuppressKeyPress = true;
                break;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && DefaultAction is not null && HitTest(e.Location).Item is not null) DefaultAction();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (DeviceDpi != _scaledFor)
        {
            foreach (ColumnHeader c in Columns) c.Width = c.Width * DeviceDpi / _scaledFor;
            _scaledFor = DeviceDpi;
        }
        FitFlexColumn();
    }

    public FastList()
    {
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        DoubleBuffered = true;
        BorderStyle = BorderStyle.None;
    }

    private int _flexColumn = -1, _flexMin;
    private EmptyState? _empty;

    /// <summary>Gives the width the other columns leave to <paramref name="column"/> whenever the list is resized,
    /// never less than <paramref name="minWidth"/> logical pixels.</summary>
    public void Flex(int column, int minWidth)
    {
        _flexColumn = column;
        _flexMin = minWidth;
        FitFlexColumn();
    }

    /// <summary>Right-aligns the text of these columns (numbers, sizes). The first column always stays left-aligned.</summary>
    public void RightAlign(params int[] columns)
    {
        foreach (int c in columns) Columns[c].TextAlign = HorizontalAlignment.Right;
    }

    /// <summary>The height of one row in device pixels.</summary>
    public int RowHeight =>
        IsHandleCreated && (VirtualMode ? VirtualListSize : Items.Count) > 0 ? GetItemRect(0).Height
        : Math.Max(Font.Height + LogicalToDeviceUnits(4), SmallImageList?.ImageSize.Height ?? 0);

    /// <summary>Shows one of <paramref name="icons"/> before each row's first column (<see cref="ListViewItem.ImageKey"/>),
    /// or none. Icons no taller than <see cref="RowHeight"/> (16 pixels at 100 %) leave the rows as tall as they are.</summary>
    public void SetIcons(ImageList? icons) => SmallImageList = icons;

    /// <summary>What the list shows while it has no rows (null: nothing). Set it once the list's rows are known, then
    /// call <see cref="ItemsChanged"/> after adding or removing rows.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public EmptyState? Empty
    {
        get => _empty;
        set
        {
            if (ReferenceEquals(_empty, value)) return;
            if (_empty is not null) Controls.Remove(_empty);
            _empty = value;
            if (value is not null)
            {
                value.Dock = DockStyle.Fill;
                Controls.Add(value);
            }
            ItemsChanged();
        }
    }

    /// <summary>Call after adding or removing rows: shows or hides <see cref="Empty"/>.</summary>
    public void ItemsChanged()
    {
        if (_empty is not null) _empty.Visible = (VirtualMode ? VirtualListSize : Items.Count) == 0;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        FitFlexColumn();
    }

    private void FitFlexColumn()
    {
        if (_flexColumn < 0 || _flexColumn >= Columns.Count) return;
        int others = 0;
        foreach (ColumnHeader c in Columns) if (c.Index != _flexColumn) others += c.Width;
        int width = Math.Max(LogicalToDeviceUnits(_flexMin), ClientSize.Width - others);
        if (Columns[_flexColumn].Width != width) Columns[_flexColumn].Width = width;
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

internal static class Num
{
    public static string Pct(double v) => v.ToString("P1", CultureInfo.CurrentCulture);
}
