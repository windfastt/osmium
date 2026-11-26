using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace Proto.Views;

public sealed partial class ArchivesPage : UserControl
{
    private readonly DataGrid _grid;
    private readonly ListBox _list;
    private readonly DockPanel _listPanel;
    private readonly TextBlock _timing;
    private IReadOnlyList<ArchiveEntry> _listItems;

    public IReadOnlyList<ArchiveEntry> Entries { get; }
    public IReadOnlyList<FolderNode> Folders { get; }
    public string SpecLine { get; }

    /// <summary>Time spent generating the sample entries (the real page reads them from the archive index).</summary>
    public double DataMs { get; }

    public DataGrid Grid => _grid;
    public ListBox List => _list;
    public bool ShowsGrid => _grid.IsVisible;

    public ArchivesPage()
    {
        var sw = Stopwatch.StartNew();
        var (entries, root) = SampleArchive.Build();
        DataMs = sw.Elapsed.TotalMilliseconds;
        Entries = entries;
        _listItems = entries;
        Folders = [root];
        long total = entries.Sum(e => e.SizeBytes);
        SpecLine = string.Create(CultureInfo.InvariantCulture,
            $"ARCHIVES · {root.Name.ToUpperInvariant()} · {entries.Count:N0} ENTRIES · {ArchiveEntry.Bytes(total).ToUpperInvariant()}");
        DataContext = this;
        AvaloniaXamlLoader.Load(this);
        _grid = this.FindControl<DataGrid>("EntryGrid")!;
        _list = this.FindControl<ListBox>("EntryList")!;
        _listPanel = this.FindControl<DockPanel>("ListPanel")!;
        _timing = this.FindControl<TextBlock>("Timing")!;
        _list.ItemsSource = _listItems;
        this.FindControl<Button>("GridView")!.Click += (_, _) => UseGrid(true);
        this.FindControl<Button>("ListView")!.Click += (_, _) => UseGrid(false);
        this.FindControl<Button>("SortName")!.Click += (_, _) => SortList(e => e.Name, descending: false);
        this.FindControl<Button>("SortKind")!.Click += (_, _) => SortList(e => e.Kind, descending: false);
        this.FindControl<Button>("SortSize")!.Click += (_, _) => SortListBySize();
        this.FindControl<Button>("SortPacked")!.Click += (_, _) => SortList(e => e.PackedBytes, descending: true);
        this.FindControl<Button>("SortModified")!.Click += (_, _) => SortList(e => e.Modified, descending: true);
        var folders = this.FindControl<TreeView>("FolderTree")!;
        folders.ContainerPrepared += (_, e) =>
        {
            // Expand the archive root so the first level of folders is visible.
            if (e.Container is TreeViewItem { DataContext: FolderNode n } item && ReferenceEquals(n, root)) item.IsExpanded = true;
        };
        // DataGrid rows are recycled: name each one for UI Automation as it is loaded with an entry.
        _grid.LoadingRow += (_, e) => Avalonia.Automation.AutomationProperties.SetName(e.Row, e.Row.DataContext?.ToString() ?? "");
        RowDrag.Attach(_list);
        RowDrag.Attach(_grid);
    }

    public void UseGrid(bool grid)
    {
        _grid.IsVisible = grid;
        _listPanel.IsVisible = !grid;
    }

    public void ShowTiming(string text) => _timing.Text = text;

    /// <summary>Sorts the DataGrid by the Size column (largest first), as a click on its header does.</summary>
    public void SortGridBySize() => _grid.Columns[2].Sort(ListSortDirection.Descending);

    public void SortListBySize() => SortList(e => e.SizeBytes, descending: true);

    private void SortList<TKey>(Func<ArchiveEntry, TKey> key, bool descending)
    {
        _listItems = descending ? [.. Entries.OrderByDescending(key)] : [.. Entries.OrderBy(key)];
        _list.ItemsSource = _listItems;
    }

    public ArchiveEntry LastListItem => _listItems[^1];
    public ArchiveEntry FirstListItem => _listItems[0];

    /// <summary>The entries the grid's rows currently show (realized rows only), top to bottom.</summary>
    public IReadOnlyList<ArchiveEntry> RealizedGridRows() =>
        [.. _grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsVisible && r.DataContext is ArchiveEntry)
            .OrderBy(r => r.TranslatePoint(new Point(0, 0), _grid)?.Y ?? 0).Select(r => (ArchiveEntry)r.DataContext!)];

    public IReadOnlyList<ArchiveEntry> RealizedListRows() =>
        [.. _list.GetRealizedContainers().Where(c => c.IsVisible && c.DataContext is ArchiveEntry)
            .OrderBy(c => c.TranslatePoint(new Point(0, 0), _list)?.Y ?? 0).Select(c => (ArchiveEntry)c.DataContext!)];
}
