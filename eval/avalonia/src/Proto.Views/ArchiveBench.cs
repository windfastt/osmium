using System.Collections;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace Proto.Views;

/// <summary>One timed step of the archive-browser benchmark: what was done, how long until the frame was on screen,
/// and the check that proves the frame shows the right rows.</summary>
public sealed record BenchStep(string Name, double Ms, bool Ok, string Check);

/// <summary>
/// Archive-browser scale (A1.23): 100,000 entries. Times first show, scroll to the end and sort by size, for the MIT
/// DataGrid and for a virtualized ListBox. Each time runs from the call until the compositor reports the next frame
/// rendered (layout, render and present included).
/// </summary>
public static class ArchiveBench
{
    /// <summary>Waits for layout and the next rendered frame on a live window (Win32).</summary>
    public static async Task LiveFrame(TopLevel top)
    {
        top.UpdateLayout();
        var compositor = ElementComposition.GetElementVisual(top)?.Compositor;
        if (compositor is null) return;
        await compositor.RequestCompositionBatchCommitAsync().Rendered;
    }

    public static async Task<List<BenchStep>> Run(MainWindow w, Func<TopLevel, Task> frame)
    {
        var steps = new List<BenchStep>();
        var sw = Stopwatch.StartNew();

        async Task<double> Frames(int n = 2)
        {
            // DataGrid scrolls in its own layout pass, so the first frame after a jump can still show old rows:
            // the step ends when two frames have rendered.
            for (int i = 0; i < n; i++) await frame(w);
            return sw.Elapsed.TotalMilliseconds;
        }

        // 1. First show: build the page (100,000 entries, folder tree) and render it with the DataGrid.
        sw.Restart();
        w.Navigate(NavItem.ArchivesIndex);
        double ms = await Frames();
        var page = w.Archives!;
        var rows = page.RealizedGridRows();
        steps.Add(new("grid: first show", ms, rows.Count > 0 && rows[0].Index == 0,
            F($"{page.Entries.Count:N0} entries; sample data built in {page.DataMs:0} ms of it; {rows.Count} rows realized, first {First(rows)}")));

        // 2. Scroll to the last entry.
        sw.Restart();
        page.Grid.ScrollIntoView(page.Entries[^1], null);
        ms = await Frames();
        rows = page.RealizedGridRows();
        steps.Add(new("grid: scroll to end", ms, rows.Count > 0 && rows[^1].Index == page.Entries.Count - 1,
            F($"{rows.Count} rows realized, last {Last(rows)}")));

        // 3. Sort by size (largest first), as a click on the Size header does; then back to the top.
        sw.Restart();
        page.SortGridBySize();
        ms = await Frames();
        var view = page.Grid.CollectionView as Avalonia.Collections.DataGridCollectionView;
        long max = page.Entries.Max(e => e.SizeBytes);
        bool sorted = view is not null && view.Count == page.Entries.Count && ((IEnumerable)view).Cast<ArchiveEntry>().First().SizeBytes == max
                      && IsDescending(((IEnumerable)view).Cast<ArchiveEntry>().Take(2000));
        steps.Add(new("grid: sort by size", ms, sorted, F($"view count {view?.Count}, first size {max:N0} B expected")));

        sw.Restart();
        if (view is not null) page.Grid.ScrollIntoView(((IEnumerable)view).Cast<ArchiveEntry>().First(), null);
        ms = await Frames();
        rows = page.RealizedGridRows();
        steps.Add(new("grid: scroll to top after sort", ms, rows.Count > 0 && rows[0].SizeBytes == max && IsDescending(rows),
            F($"{rows.Count} rows realized, first {First(rows)}")));

        // 4. The same with the virtualized ListBox.
        sw.Restart();
        page.UseGrid(false);
        ms = await Frames();
        var items = page.RealizedListRows();
        steps.Add(new("list: first show", ms, items.Count > 0 && items[0].Index == 0, F($"{items.Count} rows realized, first {First(items)}")));

        sw.Restart();
        page.List.ScrollIntoView(page.Entries.Count - 1);
        ms = await Frames();
        items = page.RealizedListRows();
        steps.Add(new("list: scroll to end", ms, items.Count > 0 && items[^1].Index == page.Entries.Count - 1,
            F($"{items.Count} rows realized, last {Last(items)}")));

        sw.Restart();
        page.SortListBySize();
        page.List.ScrollIntoView(0);
        ms = await Frames();
        items = page.RealizedListRows();
        steps.Add(new("list: sort by size", ms, items.Count > 0 && items[0].SizeBytes == max && IsDescending(items),
            F($"{items.Count} rows realized, first {First(items)}")));

        // Leave the grid showing for the UI Automation checks.
        page.UseGrid(true);
        await Frames(1);
        page.ShowTiming(string.Join("  ·  ", steps.Select(s => F($"{s.Name} {s.Ms:0} ms"))));
        return steps;
    }

    private static bool IsDescending(IEnumerable<ArchiveEntry> e)
    {
        long prev = long.MaxValue;
        foreach (var x in e)
        {
            if (x.SizeBytes > prev) return false;
            prev = x.SizeBytes;
        }
        return true;
    }

    private static string First(IReadOnlyList<ArchiveEntry> r) => r.Count == 0 ? "none" : F($"#{r[0].Index} ({r[0].SizeBytes:N0} B)");
    private static string Last(IReadOnlyList<ArchiveEntry> r) => r.Count == 0 ? "none" : F($"#{r[^1].Index} ({r[^1].SizeBytes:N0} B)");
    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
