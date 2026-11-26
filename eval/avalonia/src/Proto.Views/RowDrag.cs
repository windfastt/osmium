using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Proto.Views;

/// <summary>
/// Drag a list row out of the app as a file (Windows: CF_HDROP through Avalonia's OLE data object). The preview writes
/// a small temporary text file for the row and drags that; the real app would extract the entry first.
/// </summary>
public static class RowDrag
{
    /// <summary>Raised when a drag ends: the file that was offered and the effect the drop target chose.</summary>
    public static event Action<string, DragDropEffects>? Dragged;

    /// <summary>Raised when a drag starts (for the log).</summary>
    public static event Action<string>? Started;

    public static void Attach(Control list)
    {
        PointerPressedEventArgs? press = null;
        object? row = null;
        bool dragging = false;
        list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
            row = RowOf(e.Source as Visual);
            press = row is null ? null : e;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerMovedEvent, async (_, e) =>
        {
            if (press is null || row is null || dragging || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
            var d = e.GetPosition(list) - press.GetPosition(list);
            if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
            dragging = true;
            try
            {
                var path = WriteTempFile(row);
                var top = TopLevel.GetTopLevel(list);
                var file = top is null ? null : await top.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
                if (file is null) return;
                Started?.Invoke(path);
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateFile(file));
                var effect = await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
                Dragged?.Invoke(path, effect);
            }
            finally
            {
                dragging = false;
                press = null;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerReleasedEvent, (_, _) => press = null, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static object? RowOf(Visual? v)
    {
        for (; v is not null; v = v.GetVisualParent())
            if (v is ListBoxItem or DataGridRow) return (v as StyledElement)!.DataContext;
        return null;
    }

    /// <summary>Writes the stand-in file for a row: %TEMP%\OsmiumUiPreview\&lt;row name&gt; (preview placeholder).txt</summary>
    public static string WriteTempFile(object row)
    {
        string name = row switch
        {
            FileRow f => f.Name,
            ArchiveEntry a => a.Name,
            _ => row.ToString() ?? "row",
        };
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim(' ', '_', '.');
        var dir = Path.Combine(Path.GetTempPath(), "OsmiumUiPreview");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + " (preview placeholder).txt");
        File.WriteAllText(path,
            $"Osmium UI preview: a placeholder for \"{row}\".{Environment.NewLine}" +
            $"The preview does no real compression or extraction; the real app extracts the entry here.{Environment.NewLine}");
        return path;
    }
}
