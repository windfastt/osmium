#pragma warning disable CS0618 // Bitmap.Save(string): the default PNG encoder is what the reference needs
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Proto.Views;

// The Linux reference for the UI evaluation: renders the prototype headlessly (Skia, no display) and writes what the Windows runs
// are compared with: screenshots (1x, and the same visual tree at 144 and 192 dpi), element boxes, the UI Automation
// peers, the Archives page with its benchmark, and the notification-area menu.
// Usage: Proto.Render <out dir>
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var outDir = args.Length > 0 ? args[0] : "shots";
Directory.CreateDirectory(outDir);
var sw = Stopwatch.StartNew();
var summary = new Dictionary<string, object?>
{
    ["avalonia"] = typeof(Application).Assembly.GetName().Version?.ToString(),
    ["skiasharp"] = typeof(SkiaSharp.SKBitmap).Assembly.GetName().Version?.ToString(),
    ["os"] = Environment.OSVersion.VersionString,
    ["arch"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
};

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHarfBuzz()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();
summary["setup_ms"] = sw.ElapsedMilliseconds;

static void Pump()
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
}

static string Boxes(Window w)
{
    var boxes = new List<string>();
    foreach (var id in new[] { "Header", "Tiles", "Card", "Meter", "Ghost", "Primary" })
    {
        var c = w.GetVisualDescendants().OfType<Control>().First(x => x.Name == id);
        var p = c.TranslatePoint(new Point(0, 0), w)!.Value;
        boxes.Add(string.Create(CultureInfo.InvariantCulture, $"\"{id}\": [{p.X:0.##}, {p.Y:0.##}, {c.Bounds.Width:0.##}, {c.Bounds.Height:0.##}]"));
    }
    return "{" + string.Join(", ", boxes) + "}";
}

foreach (var (name, variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
{
    var t0 = sw.ElapsedMilliseconds;
    var w = new MainWindow { RequestedThemeVariant = variant, Width = 1040, Height = 760 };
    w.Show();
    Pump();
    using (var frame = w.CaptureRenderedFrame())
    {
        frame!.Save(Path.Combine(outDir, $"{name}.png"));
        summary[$"{name}_first_frame_ms"] = sw.ElapsedMilliseconds - t0;
    }

    // Scaled offscreen renders: the same visual tree at 144 dpi (150 %) and 192 dpi (200 %): vector text and shapes,
    // no bitmap scaling. The Windows run makes the same renders on the Win32 backend and, where the display allows,
    // real 150 % / 200 % render scaling.
    foreach (var (suffix, dpi) in new[] { ("@1.5x", 144.0), ("@2x", 192.0) })
    {
        using var rtb = new RenderTargetBitmap(new PixelSize((int)(1040 * dpi / 96), (int)(760 * dpi / 96)), new Vector(dpi, dpi));
        rtb.Render(w);
        rtb.Save(Path.Combine(outDir, $"{name}{suffix}.png"));
    }

    File.WriteAllText(Path.Combine(outDir, $"{name}-boxes.json"), Boxes(w));

    // States: the hover tip (two-layer shadow) and keyboard focus on the primary button (:focus-visible ring).
    var tip = w.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Tip");
    tip.IsVisible = true;
    var primary = w.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Compress");
    primary.Focus(Avalonia.Input.NavigationMethod.Tab);
    Pump();
    using (var frame = w.CaptureRenderedFrame())
        frame!.Save(Path.Combine(outDir, $"{name}-states.png"));
    tip.IsVisible = false;

    if (name == "light")
    {
        // Font reference: the same font checks the Windows run makes (no Segoe UI here; the bundled faces and CJK).
        File.WriteAllText(Path.Combine(outDir, "fonts-headless.json"), FontChecks.Run(w, Path.Combine(outDir, "fonts")));
    }

    if (name == "light")
    {
        // UI Automation: what a screen reader gets from the peers of the Compress page (the 22 named peers).
        var lines = new List<string>();
        foreach (var c in w.GetVisualDescendants().OfType<Control>())
        {
            var peer = ControlAutomationPeer.CreatePeerForElement(c);
            if (peer.IsControlElement() && (c is Button or ListBoxItem or TextBox or ProgressBar or ListBox))
                lines.Add($"{peer.GetAutomationControlType(),-12} name='{peer.GetName()}' focusable={peer.IsKeyboardFocusable()}");
        }
        File.WriteAllLines(Path.Combine(outDir, "automation-tree.txt"), lines);
        summary["automation_peers"] = lines.Count;
    }

    // The Archives page: 100,000 entries, TreeView + DataGrid, then the list variant (headless timings for reference).
    var steps = ArchiveBench.Run(w, _ => { Pump(); return Task.CompletedTask; }).GetAwaiter().GetResult();
    Pump();
    using (var frame = w.CaptureRenderedFrame())
        frame!.Save(Path.Combine(outDir, $"archives-{name}.png"));
    if (name == "light")
    {
        File.WriteAllText(Path.Combine(outDir, "archives-bench-headless.json"), JsonSerializer.Serialize(steps, new JsonSerializerOptions { WriteIndented = true }));
        var page = w.Archives!;
        var lines = new List<string>();
        void Peer(Control c, string what)
        {
            var p = ControlAutomationPeer.CreatePeerForElement(c);
            lines.Add($"{what,-16} type={p.GetAutomationControlType()} name='{p.GetName()}' children={p.GetChildren().Count} class={p.GetClassName()}");
        }
        Peer(page.Grid, "DataGrid");
        foreach (var r in page.Grid.GetVisualDescendants().OfType<DataGridRow>().Take(2)) Peer(r, "DataGridRow");
        foreach (var h in page.Grid.GetVisualDescendants().OfType<DataGridColumnHeader>().Take(5)) Peer(h, "ColumnHeader");
        var tree = page.GetVisualDescendants().OfType<TreeView>().First();
        Peer(tree, "TreeView");
        foreach (var i in tree.GetVisualDescendants().OfType<TreeViewItem>().Take(3)) Peer(i, "TreeViewItem");
        page.UseGrid(false);
        Pump();
        Peer(page.List, "ListBox");
        foreach (var i in page.List.GetRealizedContainers().Take(2)) Peer(i, "ListBoxItem");
        page.UseGrid(true);
        File.WriteAllLines(Path.Combine(outDir, "archives-automation.txt"), lines);
    }
    w.Close();

    // The notification-area menu (the same control the Windows tray uses), in this variant.
    var menu = new TrayMenuWindow(
        [
            new("Osmium is idle", null, Enabled: false), TrayMenuEntry.Separator,
            new("Open Osmium", () => { }, Bold: true), new("Show a test notification", () => { }),
            TrayMenuEntry.Separator, new("Exit", () => { }),
        ], new PixelPoint(400, 400)) { RequestedThemeVariant = variant };
    menu.Show();
    Pump();
    using (var frame = menu.CaptureRenderedFrame())
        frame!.Save(Path.Combine(outDir, $"traymenu-{name}.png"));
    menu.Close();
}

// Variable-font axes through TextBlock.FontVariations, using the product page's own variable fonts.
{
    var anybody = new Avalonia.Media.FontFamily("avares://Proto.Render/Assets/Var#Anybody");
    var instrument = new Avalonia.Media.FontFamily("avares://Proto.Render/Assets/Var#Instrument Sans");
    var stack = new StackPanel { Background = Avalonia.Media.Brushes.White, Spacing = 2 };
    void Row(Avalonia.Media.FontFamily f, double size, string axes, string text) =>
        stack.Children.Add(new TextBlock { Text = text + "   [" + axes + "]", FontFamily = f, FontSize = size,
            FontVariations = Avalonia.Media.FontVariationSettings.Parse(axes), Foreground = Avalonia.Media.Brushes.Black });
    Row(anybody, 44, "wght=800, wdth=150", "Smaller archives");
    Row(anybody, 44, "wght=800, wdth=100", "Smaller archives");
    Row(anybody, 44, "wght=800, wdth=62", "Smaller archives");
    Row(anybody, 40, "wght=760, wdth=70", "Pack it as small as you have time for");
    Row(instrument, 17, "wght=400, wdth=100", "Osmium starts with Windows and waits in the notification area.");
    Row(instrument, 17, "wght=650, wdth=100", "Osmium starts with Windows and waits in the notification area.");
    var vw = new Window { Width = 1100, Height = 330, Content = stack };
    vw.Show();
    Pump();
    using var f = vw.CaptureRenderedFrame();
    f!.Save(Path.Combine(outDir, "variable-font-test-v12.png"));
    vw.Close();
}

summary["total_ms"] = sw.ElapsedMilliseconds;
summary["working_set_mib"] = Environment.WorkingSet / 1048576;
File.WriteAllText(Path.Combine(outDir, "render-summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(summary));
