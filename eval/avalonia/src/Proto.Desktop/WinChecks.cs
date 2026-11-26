#pragma warning disable CS0618 // Bitmap.Save(string): the default PNG encoder is what the checks need
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Proto.Views;

namespace Proto.Desktop;

/// <summary>In-process checks on the live Win32 window: element boxes, captures, scaled renders, fonts, menu colours.</summary>
internal static class WinChecks
{
    private static readonly string[] BoxIds = ["Header", "Tiles", "Card", "Meter", "Ghost", "Primary"];
    private static string J(object o) => JsonSerializer.Serialize(o);
    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public static string Boxes(Window w)
    {
        double s = w.RenderScaling;
        var dips = new Dictionary<string, double[]>();
        var px = new Dictionary<string, double[]>();
        foreach (var id in BoxIds)
        {
            var c = w.GetVisualDescendants().OfType<Control>().FirstOrDefault(x => x.Name == id);
            if (c is null) continue;
            var p = c.TranslatePoint(new Point(0, 0), w)!.Value;
            dips[id] = [Math.Round(p.X, 2), Math.Round(p.Y, 2), Math.Round(c.Bounds.Width, 2), Math.Round(c.Bounds.Height, 2)];
            px[id] = [Math.Round(p.X * s, 2), Math.Round(p.Y * s, 2), Math.Round(c.Bounds.Width * s, 2), Math.Round(c.Bounds.Height * s, 2)];
        }
        return J(new { ok = dips.Count == BoxIds.Length, scaling = s, client = new[] { w.ClientSize.Width, w.ClientSize.Height }, dips, px });
    }

    /// <summary>"DIR TAG": the window as Avalonia renders it (RenderTargetBitmap at the window's scaling), and as Windows
    /// shows it (PrintWindow with PW_RENDERFULLCONTENT, and the screen under the client area).</summary>
    public static async Task<string> Capture(Window w, string arg)
    {
        var (dir, tag) = DirTag(arg, "win");
        await ArchiveBench.LiveFrame(w);
        double s = w.RenderScaling;
        var files = new Dictionary<string, object?>();
        var size = new PixelSize((int)Math.Round(w.ClientSize.Width * s), (int)Math.Round(w.ClientSize.Height * s));
        using (var rtb = new RenderTargetBitmap(size, new Vector(96 * s, 96 * s)))
        {
            rtb.Render(w);
            var path = Path.Combine(dir, tag + "-rtb.png");
            rtb.Save(path);
            files["rtb"] = new { path, size = new[] { size.Width, size.Height } };
        }
        var hwnd = w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        foreach (var (name, screen) in new[] { ("printwindow", false), ("screen", true) })
        {
            var cap = Native.Capture(hwnd, screen, clientOnly: true);
            if (cap is null) { files[name] = null; continue; }
            var path = Path.Combine(dir, $"{tag}-{name}.png");
            SavePng(cap.Value.W, cap.Value.H, cap.Value.Bgra, path);
            files[name] = new { path, size = new[] { cap.Value.W, cap.Value.H } };
        }
        return J(new { ok = true, scaling = s, files });
    }

    /// <summary>Scaled offscreen renders of the live window on the Win32/Skia backend: 100 %, 150 % and 200 %.</summary>
    public static string Scaled(Window w, string dir)
    {
        Directory.CreateDirectory(dir);
        var outFiles = new List<object>();
        foreach (var pct in new[] { 100, 150, 200 })
        {
            double dpi = 96.0 * pct / 100;
            var size = new PixelSize((int)Math.Round(w.ClientSize.Width * pct / 100.0), (int)Math.Round(w.ClientSize.Height * pct / 100.0));
            using var rtb = new RenderTargetBitmap(size, new Vector(dpi, dpi));
            rtb.Render(w);
            var path = Path.Combine(dir, $"scaled-{pct}.png");
            rtb.Save(path);
            outFiles.Add(new { pct, path, size = new[] { size.Width, size.Height } });
        }
        return J(new { ok = true, files = outFiles });
    }

    /// <summary>Median-free check of a menu window's surface: the most common colour inside it, against the Card token.</summary>
    public static object MenuColours(Window menu, string dir)
    {
        bool dark = menu.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        int expected = Osmium.Desktop.Theming.ColorTokens.Get(Osmium.Desktop.Theming.ColorToken.Card, dark);
        var result = new Dictionary<string, object?> { ["theme"] = dark ? "Dark" : "Light", ["expectedCard"] = Hex(expected) };
        var hwnd = menu.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var cap = Native.Capture(hwnd, fromScreen: false, clientOnly: true);
        if (cap is { } c)
        {
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                SavePng(c.W, c.H, c.Bgra, Path.Combine(dir, $"traymenu-{DesktopShell.TrayRoute}-{(dark ? "dark" : "light")}.png"));
            }
            int mode = ModeColour(c.W, c.H, c.Bgra, inset: 6);
            result["printwindowMode"] = Hex(mode);
            result["match"] = Close(mode, expected, 6);
            result["size"] = new[] { c.W, c.H };
        }
        var items = menu.GetVisualDescendants().OfType<MenuItem>().Select(i => i.Header?.ToString()).ToArray();
        result["items"] = items;
        return result;
    }

    public static string Fonts(Window w, string dir) => FontChecks.Run(w, dir);

    public static (string Dir, string Tag) DirTag(string arg, string defTag)
    {
        var p = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var dir = p.Length > 0 ? p[0] : Path.GetTempPath();
        Directory.CreateDirectory(dir);
        return (dir, p.Length > 1 ? p[1] : defTag);
    }

    public static void SavePng(int w, int h, byte[] bgra, string path)
    {
        using var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = wb.Lock())
            for (int y = 0; y < h; y++)
                Marshal.Copy(bgra, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
        wb.Save(path);
    }

    public static int ModeColour(int w, int h, byte[] bgra, int inset)
    {
        var counts = new Dictionary<int, int>();
        for (int y = inset; y < h - inset; y++)
            for (int x = inset; x < w - inset; x++)
            {
                int i = (y * w + x) * 4;
                int c = (bgra[i + 2] << 16) | (bgra[i + 1] << 8) | bgra[i];
                counts[c] = counts.GetValueOrDefault(c) + 1;
            }
        return counts.Count == 0 ? -1 : counts.MaxBy(kv => kv.Value).Key;
    }

    public static bool Close(int a, int b, int tol) =>
        a >= 0 && Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) <= tol && Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= tol && Math.Abs((a & 255) - (b & 255)) <= tol;

    public static string Hex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
}
