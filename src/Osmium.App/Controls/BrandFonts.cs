// Layout: logical units only
using System.Collections;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Osmium.Desktop.Theming;

namespace Osmium.App;

/// <summary>
/// The product page's typefaces, shipped inside the program (src/Osmium.App/Fonts, built by tools/fonts). They are
/// registered for this process only: with GDI, so text drawn by Windows and by <see cref="TextRenderer"/> uses them, and
/// with a <see cref="PrivateFontCollection"/>, which is how <see cref="Font"/> objects are made from them. They cover
/// Latin only: <see cref="Covers"/> says whether a string can be shown in one.
/// </summary>
internal static partial class BrandFonts
{
    [LibraryImport("gdi32.dll")]
    private static partial IntPtr AddFontMemResourceEx(IntPtr data, uint size, IntPtr reserved, ref uint fonts);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);

    [LibraryImport("gdi32.dll", EntryPoint = "GetGlyphIndicesW")]
    private static unsafe partial uint GetGlyphIndices(IntPtr hdc, char* text, int count, ushort* glyphs, uint flags);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextFaceW")]
    private static unsafe partial int GetTextFace(IntPtr hdc, int count, char* face);

    private const uint GgiMarkNonexistingGlyphs = 1;
    private const ushort Missing = 0xFFFF;

    // Lives as long as the process: GDI+ reads the font data from this memory whenever it draws.
    private static readonly PrivateFontCollection Collection = new();
    private static readonly Dictionary<string, FontFamily> Families = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BitArray> Coverage = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    /// <summary>The bundled families that loaded, by name.</summary>
    public static IReadOnlyCollection<string> Loaded => Families.Keys;

    /// <summary>Registers the bundled fonts once. Any failure is logged as a warning and leaves the system fonts in use.</summary>
    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var problems = new List<string>();
        var asm = typeof(BrandFonts).Assembly;
        foreach (var (file, family) in TypeScale.BundledFiles)
        {
            try
            {
                using var stream = asm.GetManifestResourceStream("Osmium.App.Fonts." + file)
                    ?? throw new FileNotFoundException("not in the program", file);
                var data = new byte[stream.Length];
                stream.ReadExactly(data);
                IntPtr memory = Marshal.AllocCoTaskMem(data.Length); // never freed: see Collection
                Marshal.Copy(data, 0, memory, data.Length);
                uint count = 0;
                if (AddFontMemResourceEx(memory, (uint)data.Length, IntPtr.Zero, ref count) == IntPtr.Zero || count == 0)
                    throw new InvalidOperationException("Windows did not accept the font");
                Collection.AddMemoryFont(memory, data.Length);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or ExternalException or OutOfMemoryException or EntryPointNotFoundException or DllNotFoundException)
            {
                problems.Add($"{family}: {e.Message}");
            }
        }
        try
        {
            foreach (var f in Collection.Families) Families[f.Name] = f;
        }
        catch (ArgumentException e) { problems.Add(e.Message); }
        foreach (var (_, family) in TypeScale.BundledFiles)
            if (!Families.ContainsKey(family) && !problems.Any(p => p.StartsWith(family + ":", StringComparison.Ordinal)))
                problems.Add($"{family}: not found after loading");
        if (problems.Count > 0) AppLog.Warn("brand fonts not loaded, using system fonts instead: " + string.Join("; ", problems));
    }

    /// <summary>A bundled family at <paramref name="points"/>, or null when it did not load. Always the regular style:
    /// the weight is in the font, and Windows must not make it bolder.</summary>
    public static Font? Create(string family, float points)
    {
        if (!Families.TryGetValue(family, out var f)) return null;
        try { return new Font(f, points, FontStyle.Regular, GraphicsUnit.Point); }
        catch (ArgumentException e)
        {
            AppLog.Warn($"{family} could not be used, using a system font instead: {e.Message}");
            Families.Remove(family);
            return null;
        }
    }

    /// <summary>Whether <paramref name="font"/> is one of the bundled faces.</summary>
    public static bool IsBundled(Font font) => Families.ContainsKey(font.Name);

    /// <summary>
    /// Whether <paramref name="font"/> has a glyph for every visible character of <paramref name="text"/>. Always true for
    /// system fonts (Windows substitutes missing characters from other fonts); for the bundled fonts, which cover Latin
    /// only and get no such help, false means the text must be shown in a system font.
    /// </summary>
    public static bool Covers(Font font, string text)
    {
        if (string.IsNullOrEmpty(text) || !IsBundled(font)) return true;
        var bits = CoverageOf(font);
        if (bits is null) return true;
        foreach (char c in text)
            if (!char.IsWhiteSpace(c) && !char.IsControl(c) && !bits[c]) return false;
        return true;
    }

    /// <summary>The face GDI really uses for <paramref name="font"/> (proves a bundled font is not substituted).</summary>
    public static unsafe string ActualFace(Font font)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), hfont = font.ToHfont();
        try
        {
            IntPtr old = SelectObject(dc, hfont);
            char* face = stackalloc char[64];
            int n = GetTextFace(dc, 64, face);
            SelectObject(dc, old);
            return n > 0 ? new string(face, 0, n - 1) : "";
        }
        finally
        {
            DeleteObject(hfont);
            DeleteDC(dc);
        }
    }

    /// <summary>Which characters of the Basic Multilingual Plane a bundled font has, measured once per family.</summary>
    private static unsafe BitArray? CoverageOf(Font font)
    {
        if (Coverage.TryGetValue(font.Name, out var cached)) return cached;
        BitArray? bits = null;
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), hfont = IntPtr.Zero;
        try
        {
            hfont = font.ToHfont();
            IntPtr old = SelectObject(dc, hfont);
            const int count = 0x10000;
            var chars = new char[count];
            for (int i = 0; i < count; i++) chars[i] = i is >= 0xD800 and <= 0xDFFF ? ' ' : (char)i;
            var glyphs = new ushort[count];
            fixed (char* c = chars)
            fixed (ushort* g = glyphs)
            {
                if (GetGlyphIndices(dc, c, count, g, GgiMarkNonexistingGlyphs) != 0xFFFFFFFF)
                {
                    bits = new BitArray(count);
                    for (int i = 0; i < count; i++) bits[i] = glyphs[i] != Missing && i is < 0xD800 or > 0xDFFF;
                }
            }
            SelectObject(dc, old);
        }
        catch (Exception e) when (e is ArgumentException or ExternalException)
        {
            AppLog.Warn($"could not read which characters {font.Name} has: {e.Message}");
        }
        finally
        {
            if (hfont != IntPtr.Zero) DeleteObject(hfont);
            DeleteDC(dc);
        }
        Coverage[font.Name] = bits ?? new BitArray(0x10000, true);
        return bits;
    }
}
