// Layout: logical units only
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Osmium.App;

/// <summary>
/// The app's details list: Explorer's look in both themes (no grid lines, Explorer selection, flicker-free), rows 30
/// logical pixels high, column headers in the secondary text colour, and columns that fit the list's width: the
/// column given to <see cref="Flex"/> (by default the last one) takes the room the others leave, and when even that
/// is not enough the others give up width in proportion, so a list never needs a horizontal scroll bar (until the
/// user resizes a column by hand). Shortcuts: Ctrl+A selects every row (also in virtual mode), Ctrl+C copies the
/// selected rows tab-separated (<see cref="RowText"/> supplies them in virtual mode), Enter and double-click run
/// <see cref="DefaultAction"/>. While the list has no rows, <see cref="Empty"/> covers it below the headers.
/// </summary>
internal sealed partial class FastList : ListView
{
    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref LvItem lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [LibraryImport("gdi32.dll")]
    private static partial int SetTextColor(IntPtr hdc, int color);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LvItem
    {
        public uint Mask;
        public int Item, SubItem;
        public uint State, StateMask;
        public IntPtr Text;
        public int TextMax, Image;
        public IntPtr Param;
        public int Indent, GroupId;
        public uint Columns;
        public IntPtr ColumnFormats;
        public int Group;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NmHdr { public IntPtr From; public IntPtr Id; public int Code; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NmCustomDraw
    {
        public NmHdr Header;
        public uint DrawStage;
        public IntPtr Hdc;
        public Rect Rc;
        public IntPtr ItemSpec;
        public uint ItemState;
        public IntPtr ItemParam;
    }

    private const int LvmGetHeader = 0x101F, LvmSetItemState = 0x102B, WmNotify = 0x004E, NmCustomDrawCode = -12;
    private const uint LvisSelected = 0x2, CddsPrepaint = 0x1, CddsItemPrepaint = 0x10001;
    private const int CdrfDoDefault = 0x0, CdrfNotifyItemDraw = 0x20;

    /// <summary>Row height in logical pixels.</summary>
    public const int LogicalRowHeight = 30;

    private readonly ImageList _rows = new() { ColorDepth = ColorDepth.Depth32Bit };
    private int _scaledFor = 96;
    private int _flexColumn = -1, _flexMin = 48;
    private bool _fitting, _userSized;
    private int[]? _design;
    private IntPtr _header;
    private EmptyState? _empty;

    public FastList()
    {
        View = View.Details;
        FullRowSelect = true;
        GridLines = false;
        HideSelection = false;
        DoubleBuffered = true;
        BorderStyle = BorderStyle.None;
        _rows.ImageSize = new Size(1, LogicalToDeviceUnits(LogicalRowHeight)); // layout: device pixels on purpose (a blank image one pixel wide only sets the row height)
        SmallImageList = _rows;
        Disposed += (_, _) => _rows.Dispose();
    }

    /// <summary>The page's default action for the selected rows (Enter, double-click), if it has one.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action? DefaultAction { get; set; }

    /// <summary>A row's cells for Ctrl+C in virtual mode (the rows are not items there).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int, string[]>? RowText { get; set; }

    /// <summary>The selected rows, one per line, columns separated by tabs.</summary>
    public string SelectionAsText() => string.Join(Environment.NewLine, SelectedIndices.Cast<int>().Order().Select(i =>
        string.Join("\t", VirtualMode
            ? RowText?.Invoke(i) ?? []
            : Items[i].SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text))));

    /// <summary>Selects every row with one message, which also works in virtual mode.</summary>
    public void SelectAllRows()
    {
        if (!IsHandleCreated || !MultiSelect) return;
        var item = new LvItem { StateMask = LvisSelected, State = LvisSelected };
        SendMessage(Handle, LvmSetItemState, -1, ref item);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        switch (e.KeyData)
        {
            case Keys.Control | Keys.A when MultiSelect:
                SelectAllRows();
                e.SuppressKeyPress = true;
                break;
            case Keys.Control | Keys.C:
                Ui.CopyToClipboard(SelectionAsText());
                e.SuppressKeyPress = true;
                break;
            case Keys.Enter when DefaultAction is not null && SelectedIndices.Count > 0:
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
            _fitting = true;
            foreach (ColumnHeader c in Columns) c.Width = c.Width * DeviceDpi / _scaledFor;
            _fitting = false;
            _scaledFor = DeviceDpi;
        }
        ApplyTheme();
        FitColumns();
        PlaceEmpty();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        if (_rows.Images.Count == 0) _rows.ImageSize = new Size(_rows.ImageSize.Width, LogicalToDeviceUnits(LogicalRowHeight));
        _design = null;
        FitColumns();
        PlaceEmpty();
    }

    /// <summary>Explorer's list and header styles, dark or light; failures leave the system look.</summary>
    private void ApplyTheme()
    {
        try
        {
            SetWindowTheme(Handle, Theme.IsDark ? "DarkMode_Explorer" : "Explorer", null);
            _header = SendMessage(Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
            if (_header != IntPtr.Zero) SetWindowTheme(_header, Theme.IsDark ? "DarkMode_ItemsView" : "ItemsView", null);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            AppLog.Warn("list style not applied: " + ex.Message);
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Column headers draw their text in the secondary text colour.
        if (m.Msg == WmNotify && _header != IntPtr.Zero && Marshal.PtrToStructure<NmHdr>(m.LParam) is { Code: NmCustomDrawCode } hdr
            && hdr.From == _header)
        {
            var draw = Marshal.PtrToStructure<NmCustomDraw>(m.LParam);
            if (draw.DrawStage == CddsPrepaint) { m.Result = CdrfNotifyItemDraw; return; }
            if (draw.DrawStage == CddsItemPrepaint)
            {
                SetTextColor(draw.Hdc, Osmium.Desktop.Theming.ColorTokens.ColorRef(Theme.P.TextSecondary.ToArgb() & 0xFFFFFF));
                m.Result = CdrfDoDefault;
                return;
            }
        }
        base.WndProc(ref m);
    }

    /// <summary>Gives the width the other columns leave to <paramref name="column"/> whenever the list is resized,
    /// never less than <paramref name="minWidth"/> logical pixels.</summary>
    public void Flex(int column, int minWidth)
    {
        _flexColumn = column;
        _flexMin = minWidth;
        FitColumns();
    }

    /// <summary>Right-aligns the text of these columns (numbers, sizes). The first column always stays left-aligned.</summary>
    public void RightAlign(params int[] columns)
    {
        foreach (int c in columns) Columns[c].TextAlign = HorizontalAlignment.Right;
    }

    /// <summary>The height of one row in device pixels.</summary>
    public int RowHeight =>
        IsHandleCreated && (VirtualMode ? VirtualListSize : Items.Count) > 0 ? GetItemRect(0).Height
        : Math.Max(Font.Height + LogicalToDeviceUnits(4), _rows.ImageSize.Height);

    /// <summary>
    /// Shows one of <paramref name="icons"/> before each row's first column (<see cref="ListViewItem.ImageKey"/> or
    /// <see cref="ListViewItem.ImageIndex"/>), or none. The icons are copied, centred, into images as tall as a row, so
    /// they never change <see cref="RowHeight"/>; the caller keeps and disposes its own list.
    /// </summary>
    public void SetIcons(ImageList? icons)
    {
        int height = LogicalToDeviceUnits(LogicalRowHeight);
        _rows.Images.Clear();
        _rows.ImageSize = new Size(icons?.ImageSize.Width ?? 1, height); // layout: device pixels on purpose (the icons' own width, or a blank pixel)
        if (icons is not null)
        {
            _ = _rows.Handle; // with its native list made, Add copies each image, so the copies below can be disposed
            var keys = icons.Images.Keys;
            for (int i = 0; i < icons.Images.Count; i++)
            {
                using var source = icons.Images[i];
                using var bmp = new Bitmap(icons.ImageSize.Width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.DrawImage(source, 0, (height - icons.ImageSize.Height) / 2, icons.ImageSize.Width, icons.ImageSize.Height);
                }
                string key = i < keys.Count ? keys[i] ?? "" : "";
                if (key.Length > 0) _rows.Images.Add(key, bmp); else _rows.Images.Add(bmp);
            }
        }
        SmallImageList = _rows;
    }

    /// <summary>What the list shows while it has no rows (null: nothing). Set it once the list's rows are known, then
    /// call <see cref="ItemsChanged"/> after adding or removing rows.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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
                value.Dock = DockStyle.None;
                Controls.Add(value);
                PlaceEmpty();
            }
            ItemsChanged();
        }
    }

    /// <summary>Call after adding or removing rows: shows or hides <see cref="Empty"/>.</summary>
    public void ItemsChanged()
    {
        if (_empty is not null) _empty.Visible = (VirtualMode ? VirtualListSize : Items.Count) == 0;
    }

    /// <summary>Marks the list as a drop target while files are dragged over it (the empty state's outline).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool DropHighlight
    {
        get => _empty?.Highlight ?? false;
        set { if (_empty is not null) _empty.Highlight = value; }
    }

    internal enum DragStage { Enter, Over, Drop }

    /// <summary>Drag events that reached the empty state, raised as the list's own.</summary>
    internal void ForwardDrag(DragEventArgs e, DragStage stage)
    {
        switch (stage)
        {
            case DragStage.Enter: OnDragEnter(e); break;
            case DragStage.Over: OnDragOver(e); break;
            default: OnDragDrop(e); break;
        }
    }

    internal void ForwardDragLeave() => OnDragLeave(EventArgs.Empty);

    /// <summary>The column headers' height in device pixels (0 without headers).</summary>
    private int HeaderHeight()
    {
        if (!IsHandleCreated || HeaderStyle == ColumnHeaderStyle.None) return 0;
        return _header != IntPtr.Zero && GetWindowRect(_header, out var r) ? r.Bottom - r.Top : 0;
    }

    private void PlaceEmpty()
    {
        if (_empty is null) return;
        int top = HeaderHeight();
        _empty.Bounds = new Rectangle(0, top, ClientSize.Width, Math.Max(0, ClientSize.Height - top));
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        FitColumns();
        PlaceEmpty();
    }

    protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
    {
        base.OnColumnWidthChanged(e);
        if (!_fitting && IsHandleCreated && MouseButtons == MouseButtons.Left) _userSized = true; // a column dragged by hand stays as the user left it
    }

    /// <summary>
    /// Fits the columns to the list's width: the flexible column (the last one unless <see cref="Flex"/> chose another)
    /// takes the room the others leave, at least its minimum; when that is not enough, the other columns shrink in
    /// proportion, none below 48 logical pixels.
    /// </summary>
    private void FitColumns()
    {
        if (_fitting || _userSized || Columns.Count == 0 || ClientSize.Width <= 0 || !IsHandleCreated) return;
        // The widths the columns were made with, so a list that grows again gets them back.
        if (_design is null || _design.Length != Columns.Count) _design = Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
        int flex = _flexColumn >= 0 && _flexColumn < Columns.Count ? _flexColumn : Columns.Count - 1;
        int available = ClientSize.Width;
        int flexMin = LogicalToDeviceUnits(_flexMin), floor = LogicalToDeviceUnits(48);
        _fitting = true;
        try
        {
            int others = 0;
            for (int i = 0; i < _design.Length; i++) if (i != flex) others += _design[i];
            double scale = others + flexMin > available && others > 0 ? Math.Max(0, (double)(available - flexMin) / others) : 1;
            others = 0;
            foreach (ColumnHeader c in Columns)
            {
                if (c.Index == flex) continue;
                int w = scale < 1 ? Math.Max(floor, (int)(_design[c.Index] * scale)) : _design[c.Index];
                if (c.Width != w) c.Width = w;
                others += w;
            }
            int width = Math.Max(flexMin, available - others);
            if (Columns[flex].Width != width) Columns[flex].Width = width;
        }
        finally { _fitting = false; }
    }
}
