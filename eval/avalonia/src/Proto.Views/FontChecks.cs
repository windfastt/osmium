#pragma warning disable CS0618, IL2026, IL3050 // Bitmap.Save(string); reflection JSON for a diagnostic report
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace Proto.Views;

/// <summary>
/// Fonts, in-process: the bundled static faces resolve to themselves; Segoe UI Variable (the user-data face) renders
/// at distinct weights, by FontWeight on its named families and by the wght axis (FontVariations); static Segoe UI as the
/// fallback; a CJK file name has no missing glyphs. Runs on Windows (the gate) and headless on Linux (reference).
/// </summary>
public static class FontChecks
{
    private const string Sample = "Archive 2026-11 · Packed 64.7 MB · Osmium";

    public static string Run(Window w, string dir)
    {
        Directory.CreateDirectory(dir);
        var fm = FontManager.Current;
        var bundled = new List<object>();
        bool bundledOk = true;
        foreach (var (key, weights) in new[] { ("FontTitle", new[] { 400 }), ("FontHeading", new[] { 400 }), ("FontSpec", new[] { 400 }),
                     ("FontNumber", new[] { 400 }), ("FontBody", new[] { 400, 600 }) })
        {
            Application.Current!.TryGetResource(key, null, out var v);
            var ff = v as FontFamily;
            string expected = ff?.FamilyNames.FirstOrDefault() ?? "?";
            foreach (var wt in weights)
            {
                GlyphTypeface? g = null;
                if (ff is not null) fm.TryGetGlyphTypeface(new Typeface(ff, FontStyle.Normal, (FontWeight)wt), out g);
                bool nameOk = g is not null && (g.FamilyName == expected || g.TypographicFamilyName == expected || g.FamilyNames.Values.Contains(expected));
                bool ok = nameOk && g!.FontSimulations == FontSimulations.None && (wt != 600 || (int)g.Weight >= 550);
                bundledOk &= ok;
                bundled.Add(new
                {
                    key, weight = wt, expected, family = g?.FamilyName, typographic = g?.TypographicFamilyName, faceWeight = (int?)g?.Weight,
                    simulations = g?.FontSimulations.ToString(), glyphs = g?.GlyphCount, ok,
                });
            }
        }

        var system = fm.SystemFonts.Select(f => f.Name).Where(n => n.StartsWith("Segoe", StringComparison.Ordinal)).OrderBy(n => n).ToArray();
        var rows = new StackPanel { Background = Brushes.White, Spacing = 4 };
        var weightRows = new List<object>();
        var verdict = new Dictionary<string, bool>();

        // By FontWeight on each named family (what TextBlock.FontWeight does in the app).
        foreach (var fam in new[] { "Segoe UI Variable Text", "Segoe UI Variable Display", "Segoe UI Variable", "Segoe UI" })
        {
            bool installed = system.Contains(fam);
            var widths = new List<double>();
            var inks = new List<double>();
            bool simulated = false;
            string? resolved = null;
            foreach (var wt in new[] { 300, 400, 600, 700 })
            {
                var tf = new Typeface(new FontFamily(fam), FontStyle.Normal, (FontWeight)wt);
                fm.TryGetGlyphTypeface(tf, out var g);
                resolved ??= g?.FamilyName;
                var (width, ink) = Measure(tf, null, rows, $"{fam} {wt}");
                widths.Add(width);
                inks.Add(ink);
                simulated |= wt >= 600 && g is not null && g.FontSimulations.HasFlag(FontSimulations.Bold);
                weightRows.Add(new
                {
                    how = "FontWeight", family = fam, installed, weight = wt, resolved = g?.FamilyName, faceWeight = (int?)g?.Weight,
                    simulations = g?.FontSimulations.ToString(), axes = Describe(g?.VariationAxes), instances = Count(g?.NamedInstances),
                    width = Math.Round(width, 2), ink = Math.Round(ink, 0),
                });
            }
            // Only the real face counts (Linux's fontconfig, for one, answers "Segoe UI" with a substitute).
            bool real = resolved?.StartsWith("Segoe", StringComparison.Ordinal) == true;
            verdict[fam] = installed && real && Increasing(widths, inks) && !simulated;
        }

        // By the wght axis on the variable family itself (TextBlock.FontVariations, Avalonia 12).
        foreach (var fam in new[] { "Segoe UI Variable", "Segoe UI Variable Text" })
        {
            if (!system.Contains(fam)) continue;
            var widths = new List<double>();
            var inks = new List<double>();
            foreach (var wght in new[] { 300, 400, 600, 700 })
            {
                var tf = new Typeface(new FontFamily(fam));
                var (width, ink) = Measure(tf, FontVariationSettings.Parse($"wght={wght}"), rows, $"{fam} wght={wght}");
                widths.Add(width);
                inks.Add(ink);
                weightRows.Add(new { how = "FontVariations", family = fam, wght, width = Math.Round(width, 2), ink = Math.Round(ink, 0) });
            }
            fm.TryGetGlyphTypeface(new Typeface(new FontFamily(fam)), out var vg);
            verdict[fam + " (wght axis)"] = vg?.FamilyName.StartsWith("Segoe", StringComparison.Ordinal) == true && Increasing(widths, inks);
        }

        // A CJK file name: the fallback face, and no .notdef glyphs in the Compress page's row.
        Application.Current!.TryGetResource("FontBody", null, out var body);
        bool matched = fm.TryMatchCharacter('写', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, body as FontFamily,
            CultureInfo.CurrentUICulture, out var cjkTypeface);
        var cjkBlock = w.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "写真-2025.zip");
        var runs = new List<object>();
        int notdef = -1;
        if (cjkBlock?.TextLayout is { } tl)
        {
            notdef = 0;
            foreach (var line in tl.TextLines)
                foreach (var run in line.TextRuns.OfType<ShapedTextRun>())
                {
                    int zeros = 0;
                    foreach (var gi in run.ShapedBuffer) if (gi.GlyphIndex == 0) zeros++;
                    notdef += zeros;
                    runs.Add(new { text = run.Text.ToString(), family = run.ShapedBuffer.GlyphTypeface.FamilyName, glyphs = run.ShapedBuffer.Length, notdef = zeros });
                }
        }
        rows.Children.Add(new TextBlock { Text = "CJK: 写真-2025.zip · 圧縮ファイル.osa · 压缩文件.osa · 압축.osa", FontFamily = body as FontFamily ?? FontFamily.Default, FontSize = 20, Foreground = Brushes.Black });
        bool cjkOk = matched && notdef == 0 && runs.Count > 0;

        rows.Measure(Size.Infinity);
        rows.Arrange(new Rect(rows.DesiredSize));
        double s = w.RenderScaling;
        using (var rtb = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(rows.DesiredSize.Width * s) + 8, (int)Math.Ceiling(rows.DesiredSize.Height * s) + 8), new Vector(96 * s, 96 * s)))
        {
            rtb.Render(rows);
            rtb.Save(Path.Combine(dir, "fonts-test.png"));
        }

        bool variableOk = verdict.GetValueOrDefault("Segoe UI Variable Text") || verdict.GetValueOrDefault("Segoe UI Variable (wght axis)");
        bool staticOk = verdict.GetValueOrDefault("Segoe UI");
        return JsonSerializer.Serialize(new
        {
            ok = bundledOk && (variableOk || staticOk) && cjkOk,
            bundledOk,
            segoeVariableOk = variableOk,
            segoeStaticOk = staticOk,
            verdicts = verdict,
            cjkOk,
            systemSegoe = system,
            bundled,
            weights = weightRows,
            cjk = new { matched, typeface = matched ? cjkTypeface.FontFamily.Name : null, notdef, runs },
            image = Path.Combine(dir, "fonts-test.png"),
        });
    }

    /// <summary>Right weights look like static Segoe UI does: from 400 to 700 the line gets at least 3 % wider
    /// (bold advances) and at least 25 % darker (bold strokes), both steadily. A face whose strokes thicken while its
    /// advances stay at 400 (or the reverse) fails.</summary>
    private static bool Increasing(List<double> widths, List<double> inks) =>
        widths[1] < widths[2] && widths[2] < widths[3] && inks[1] < inks[2] && inks[2] < inks[3]
        && widths[3] >= widths[1] * 1.03 && inks[3] >= inks[1] * 1.25;

    private static int? Count(object? o) => o is System.Collections.ICollection c ? c.Count : o is System.Collections.IEnumerable e ? e.Cast<object?>().Count() : null;

    private static string? Describe(object? o) => o switch
    {
        null => null,
        string str => str,
        System.Collections.IEnumerable e => string.Join("; ", e.Cast<object?>().Select(x => x?.ToString())),
        _ => o.ToString(),
    };

    /// <summary>Advance width of the sample line and its total darkness rendered black on white; adds the line to the
    /// test image.</summary>
    private static (double Width, double Ink) Measure(Typeface tf, FontVariationSettings? axes, StackPanel rows, string label)
    {
        TextBlock Block(string text) => new()
        {
            Text = text, FontFamily = tf.FontFamily, FontWeight = tf.Weight, FontSize = 20, Foreground = Brushes.Black, Background = Brushes.White,
            FontVariations = axes,
        };
        rows.Children.Add(Block($"{label}: {Sample}"));
        var tb = Block(Sample);
        tb.Measure(Size.Infinity);
        tb.Arrange(new Rect(tb.DesiredSize));
        var size = new PixelSize((int)Math.Ceiling(tb.DesiredSize.Width) + 2, (int)Math.Ceiling(tb.DesiredSize.Height) + 2);
        using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        rtb.Render(tb);
        var buf = new byte[size.Width * size.Height * 4];
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { rtb.CopyPixels(new PixelRect(size), h.AddrOfPinnedObject(), buf.Length, size.Width * 4); }
        finally { h.Free(); }
        double ink = 0;
        for (int i = 0; i < buf.Length; i += 4) ink += 255 - (buf[i] + buf[i + 1] + buf[i + 2]) / 3.0;
        return (tb.DesiredSize.Width, ink);
    }
}
