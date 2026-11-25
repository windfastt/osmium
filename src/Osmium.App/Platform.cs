// Layout: logical units only
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Osmium.Desktop.Settings;
using Osmium.Desktop.Shell;
using Osmium.Desktop.Theming;

namespace Osmium.App;

internal static partial class Native
{
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("kernel32.dll")]
    internal static partial uint SetThreadExecutionState(uint flags);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("shell32.dll")]
    internal static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Lets another process (<see cref="AsfwAny"/>: any process) bring its window to the front.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(int processId);

    internal const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;
    internal const int AsfwAny = -1; // ASFW_ANY

    /// <summary>Immersive dark title bar (attribute 20 on Win10 20H1+, 19 on older builds).</summary>
    internal static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref v, sizeof(int));
    }

    /// <summary>Gives a window Osmium's title bar for the current theme. Call it from the form's OnHandleCreated.</summary>
    internal static void StyleWindow(Form form)
    {
        try { SetDarkTitleBar(form.Handle, Theme.IsDark); } catch (Exception) { }
    }
}

/// <summary>
/// The colours of the current theme by role, from the product page's palette (<see cref="ColorTokens"/>). The positional
/// names are the roles' older names: <see cref="Background"/> is <see cref="Page"/>, <see cref="Surface"/> is
/// <see cref="Card"/>, <see cref="Border"/> is <see cref="Line"/>, <see cref="Experimental"/> is <see cref="Warning"/>
/// and <see cref="Hover"/> is <see cref="Subtle"/>.
/// </summary>
internal sealed record Palette(Color Background, Color Surface, Color Border, Color Text, Color Muted, Color Accent, Color AccentText, Color Danger, Color Experimental, Color Hover)
{
    public static readonly Palette Light = FromTokens(dark: false);
    public static readonly Palette Dark = FromTokens(dark: true);

    /// <summary>A token's colour, opaque.</summary>
    public static Color Of(ColorToken token, bool dark) => FromRgb(ColorTokens.Get(token, dark));

    public static Color FromRgb(int rgb) => Color.FromArgb(unchecked((int)0xFF000000) | (rgb & 0xFFFFFF));

    private static Palette FromTokens(bool dark)
    {
        Color C(ColorToken t) => Of(t, dark);
        return new Palette(C(ColorToken.Page), C(ColorToken.Card), C(ColorToken.Line), C(ColorToken.Text), C(ColorToken.Muted),
            C(ColorToken.Accent), C(ColorToken.AccentText), C(ColorToken.Danger), C(ColorToken.Warning), C(ColorToken.Subtle))
        {
            IsDark = dark,
            Sunk = C(ColorToken.Sunk),
            Code = C(ColorToken.Code),
            TextSecondary = C(ColorToken.TextSecondary),
            LineSoft = C(ColorToken.LineSoft),
            LineStrong = C(ColorToken.LineStrong),
            AccentInk = C(ColorToken.AccentInk),
            AccentHover = C(ColorToken.AccentHover),
            AccentPressed = C(ColorToken.AccentPressed),
            Mark = C(ColorToken.Mark),
            MarkRest = C(ColorToken.MarkRest),
            Live = C(ColorToken.Live),
            WarningText = C(ColorToken.WarningText),
            Success = C(ColorToken.Success),
            SuccessText = C(ColorToken.SuccessText),
            Focus = C(ColorToken.Focus),
        };
    }

    /// <summary>The dark theme's colours.</summary>
    public bool IsDark { get; init; }

    /// <summary>The page behind cards and lists.</summary>
    public Color Page => Background;
    /// <summary>Cards, lists and editors.</summary>
    public Color Card { get; init; } = Surface;
    /// <summary>Tracks, a pressed ghost button, the selected segment: a step below the card.</summary>
    public Color Sunk { get; init; } = Hover;
    /// <summary>Behind monospaced text (pipelines, logs, rules).</summary>
    public Color Code { get; init; } = Surface;
    /// <summary>Second-level text: ledes, labels, column headers.</summary>
    public Color TextSecondary { get; init; } = Text;
    /// <summary>Card borders and dividers.</summary>
    public Color Line { get; init; } = Border;
    /// <summary>Row separators, the rail divider, the status line's rule.</summary>
    public Color LineSoft { get; init; } = Border;
    /// <summary>A ghost button's border under the pointer.</summary>
    public Color LineStrong { get; init; } = Border;
    /// <summary>Accent-coloured text and glyphs on the page or a card: links, Accent tags.</summary>
    public Color AccentInk { get; init; } = Accent;
    public Color AccentHover { get; init; } = Accent;
    public Color AccentPressed { get; init; } = Accent;
    /// <summary>Filled part of meters and bars, selection markers.</summary>
    public Color Mark { get; init; } = Accent;
    /// <summary>Marks at rest (unselected, not yet started); on cards only.</summary>
    public Color MarkRest { get; init; } = Muted;
    /// <summary>Something happening now (a running job's dot). Never text on a light surface.</summary>
    public Color Live { get; init; } = Accent;
    /// <summary>Paused meters, warning dots, tag and banner borders.</summary>
    public Color Warning { get; init; } = Experimental;
    /// <summary>Warning text on any surface.</summary>
    public Color WarningText { get; init; } = Experimental;
    public Color Success { get; init; } = Color.FromArgb(0x0F, 0x7A, 0x3D);
    /// <summary>Success text on any surface.</summary>
    public Color SuccessText { get; init; } = Color.FromArgb(0x0D, 0x6E, 0x37);
    /// <summary>Quiet backgrounds: hover in the rail, menus and owner-drawn lists.</summary>
    public Color Subtle { get; init; } = Hover;
    /// <summary>Keyboard focus rings.</summary>
    public Color Focus { get; init; } = Accent;
}

internal static class Theme
{
    public static bool IsDark { get; private set; }
    public static Palette P { get; private set; } = Palette.Light;
    public static Font Body { get; private set; } = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
    /// <summary>The title of a page or dialog (the same as <see cref="Title"/>).</summary>
    public static Font Heading { get; private set; } = Control.DefaultFont;
    /// <summary>Small text (the same as <see cref="Caption"/>).</summary>
    public static Font Small { get; private set; } = Control.DefaultFont;
    /// <summary>Emphasised body text (the same as <see cref="BodyStrong"/>).</summary>
    public static Font Bold { get; private set; } = Control.DefaultFont;
    public static Font Glyph { get; private set; } = Control.DefaultFont;
    public static Font Mono { get; private set; } = Control.DefaultFont;

    // Fonts by role (TypeScale has every role's face and size). Title, CardHeading, Brand, Spec, Tag and Number use the
    // product page's typefaces, for Osmium's own words only: file names, paths and messages stay in Body or Mono.

    /// <summary>A page's title.</summary>
    public static Font Title { get; private set; } = Control.DefaultFont;
    public static Font CardHeading { get; private set; } = Control.DefaultFont;
    /// <summary>The "Osmium" wordmark in the navigation rail.</summary>
    public static Font Brand { get; private set; } = Control.DefaultFont;
    /// <summary>The fact line above a page's title (shown in capitals).</summary>
    public static Font Spec { get; private set; } = Control.DefaultFont;
    /// <summary>State labels (shown in capitals).</summary>
    public static Font Tag { get; private set; } = Control.DefaultFont;
    /// <summary>The sentence below a page's title.</summary>
    public static Font Lede { get; private set; } = Control.DefaultFont;
    public static Font BodyStrong { get; private set; } = Control.DefaultFont;
    public static Font Caption { get; private set; } = Control.DefaultFont;
    /// <summary>Big numbers that do not change (result tiles).</summary>
    public static Font Number { get; private set; } = Control.DefaultFont;
    /// <summary>Numbers that change while they are shown (progress, speed): digits of equal width.</summary>
    public static Font NumberLive { get; private set; } = Control.DefaultFont;
    /// <summary>Icon glyphs of empty states.</summary>
    public static Font GlyphLarge { get; private set; } = Control.DefaultFont;

    private static readonly Dictionary<TypeRole, Font> Roles = [];
    private static readonly Dictionary<TypeRole, Font> Fallbacks = [];

    /// <summary>A role's font.</summary>
    public static Font Role(TypeRole role) => Roles.TryGetValue(role, out var f) ? f : Body;

    /// <summary>The system font that stands in for a role's bundled face (the same font for roles without one).</summary>
    public static Font Fallback(TypeRole role) => Fallbacks.TryGetValue(role, out var f) ? f : Role(role);

    /// <summary>A role's font for <paramref name="text"/>: its own, or the system stand-in when the bundled face lacks a
    /// character of the text (it covers Latin only).</summary>
    public static Font FontFor(TypeRole role, string text) => BrandFonts.Covers(Role(role), text) ? Role(role) : Fallback(role);

    /// <summary>A role's text colour in the current theme.</summary>
    public static Color ColorOf(TypeRole role) => TypeScale.Get(role).Color switch
    {
        TypeColor.TextSecondary => P.TextSecondary,
        TypeColor.Muted => P.Muted,
        _ => P.Text,
    };

    public static void Initialize(ThemeMode mode)
    {
        IsDark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemUsesDarkApps(),
        };
        P = IsDark ? Palette.Dark : Palette.Light;
#pragma warning disable WFO5001 // dark mode API marked experimental in some .NET versions
        Application.SetColorMode(IsDark ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001
        BrandFonts.Load();
        MakeRoleFonts(1f);
    }

    /// <summary>Creates every role's font at <paramref name="scale"/> times its size and points the properties at them.</summary>
    private static void MakeRoleFonts(float scale)
    {
        foreach (var spec in TypeScale.All)
        {
            float size = spec.Size * scale;
            var system = SystemFont(spec, size);
            Fallbacks[spec.Role] = system;
            Roles[spec.Role] = spec.Bundled ? BrandFonts.Create(spec.Face, size) ?? system : system;
        }
        MakeRoleProperties();
    }

    private static Dictionary<TypeRole, Font>? _unscaledRoles, _unscaledFallbacks;

    /// <summary>
    /// Makes the text of windows built from now on <paramref name="factor"/> times larger, as Windows' text size setting
    /// does (1 goes back to normal and frees the larger fonts): the self-test builds dialogs with larger text to see that
    /// nothing is cut. Windows built while the text is larger must be closed before going back.
    /// </summary>
    internal static void ScaleText(float factor)
    {
        if (factor == 1f)
        {
            if (_unscaledRoles is null || _unscaledFallbacks is null) return;
            var scaled = Roles.Values.Concat(Fallbacks.Values).Distinct().ToList();
            foreach (var (role, font) in _unscaledRoles) Roles[role] = font;
            foreach (var (role, font) in _unscaledFallbacks) Fallbacks[role] = font;
            _unscaledRoles = _unscaledFallbacks = null;
            MakeRoleProperties();
            foreach (var font in scaled) font.Dispose();
            return;
        }
        if (_unscaledRoles is null)
        {
            _unscaledRoles = new(Roles);
            _unscaledFallbacks = new(Fallbacks);
        }
        MakeRoleFonts(factor);
    }

    private static void MakeRoleProperties()
    {
        Body = Role(TypeRole.Body);
        BodyStrong = Bold = Role(TypeRole.BodyStrong);
        Caption = Small = Role(TypeRole.Caption);
        Title = Heading = Role(TypeRole.Title);
        CardHeading = Role(TypeRole.CardHeading);
        Brand = Role(TypeRole.Brand);
        Spec = Role(TypeRole.Spec);
        Tag = Role(TypeRole.Tag);
        Lede = Role(TypeRole.Lede);
        Number = Role(TypeRole.Number);
        NumberLive = Role(TypeRole.NumberLive);
        Mono = Role(TypeRole.Mono);
        Glyph = Role(TypeRole.Glyph);
        GlyphLarge = Role(TypeRole.GlyphLarge);
    }

    /// <summary>The first installed system face of a role (its own face unless it is bundled, then its fallbacks), in
    /// the regular style; plain Segoe UI is made bold for heavy roles.</summary>
    private static Font SystemFont(TypeSpec spec, float size)
    {
        var faces = spec.Bundled ? spec.Fallbacks : [spec.Face, .. spec.Fallbacks];
        foreach (var face in faces)
            if (FontExists(face))
                return new Font(face, size, face == TypeScale.Segoe && spec.Weight >= 600 ? FontStyle.Bold : FontStyle.Regular);
        return new Font(Control.DefaultFont.FontFamily, size, spec.Weight >= 600 ? FontStyle.Bold : FontStyle.Regular);
    }

    public static bool SystemUsesDarkApps()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0; }
        catch (Exception) { return false; }
    }

    /// <summary>The taskbar can be dark while apps are light: tray glyphs follow the taskbar setting.</summary>
    public static bool TaskbarIsLight()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1; }
        catch (Exception) { return false; }
    }

    private static bool FontExists(string name)
    {
        try { using var f = new FontFamily(name); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Applies the palette to a control tree. The root gets the colour of the surface it sits on (a card's, or the
    /// page's); panels and labels inside take their container's colour, so text inside a card sits on the card colour.
    /// Inputs and lists are card-coloured (the code colour with <c>Tag = "code"</c>), labels tagged "muted",
    /// "secondary", "danger", "experimental", "warning" or "success" take that role's colour, containers tagged
    /// "surface" are card-coloured, and anything tagged "keep" keeps its own colours. Native controls follow the
    /// application's colour mode.
    /// </summary>
    public static void Apply(Control root)
    {
        if (root.Tag as string != "keep")
        {
            root.BackColor = SurfaceBehind(root);
            root.ForeColor = P.Text;
        }
        foreach (Control c in root.Controls) ApplyChild(c);
    }

    /// <summary>The colour of the surface <paramref name="c"/> sits on: the nearest card, or the page.</summary>
    private static Color SurfaceBehind(Control c)
    {
        for (var p = c.Parent; p is not null; p = p.Parent)
        {
            if (p is CardPanel or Banner or EmptyState || p.Tag as string == "surface") return P.Card;
            if (p is Form) break;
        }
        return P.Page;
    }

    private static void ApplyChild(Control c)
    {
        string? tag = c.Tag as string;
        if (tag == "keep")
        {
            if (c is CardPanel or Banner or EmptyState or StatTiles or Meter or SegmentedChoice) c.Invalidate();
        }
        else switch (c)
        {
            case OsmiumButton or NavButton:
                c.Invalidate(); // they paint themselves from the palette
                break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = tag == "accent" ? P.Accent : P.Card;
                b.ForeColor = tag == "accent" ? P.AccentText : P.Text;
                b.FlatAppearance.BorderColor = tag == "accent" ? P.Accent : P.Line;
                b.FlatAppearance.MouseOverBackColor = tag == "accent" ? P.AccentHover : P.Subtle;
                b.FlatAppearance.MouseDownBackColor = tag == "accent" ? P.AccentPressed : P.Sunk;
                break;
            case TextBoxBase or ListView or ListBox or ComboBox or NumericUpDown or TreeView:
                c.BackColor = tag == "code" ? P.Code : P.Card;
                c.ForeColor = P.Text;
                break;
            case Label l:
                Color? fore = tag switch
                {
                    "muted" => P.Muted,
                    "secondary" => P.TextSecondary,
                    "danger" => P.Danger,
                    "experimental" or "warning" => P.WarningText,
                    "success" => P.SuccessText,
                    _ => null,
                };
                if (fore is { } f) l.ForeColor = f;
                if (l is LinkLabel link) link.LinkColor = link.ActiveLinkColor = P.AccentInk;
                break;
            case Panel or TableLayoutPanel or FlowLayoutPanel or UserControl or SplitContainer or GroupBox or CheckBox or RadioButton:
                if (tag == "surface") c.BackColor = P.Card;
                else if (tag == "code") c.BackColor = P.Code;
                break;
        }
        foreach (Control child in c.Controls) ApplyChild(child);
    }
}

/// <summary>Pre-rendered progress-ring tray icons (no per-tick GDI allocation, handles destroyed correctly).</summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<string, Icon> _cache = [];
    private readonly Icon _app;

    public TrayIcons(Icon app) => _app = app;

    public Icon Idle => _app;

    public Icon Progress(double fraction, bool paused, bool error)
    {
        int bucket = Osmium.Desktop.Tray.TrayText.Bucket(fraction);
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        bool light = Theme.TaskbarIsLight();
        string key = $"{bucket}|{paused}|{error}|{size}|{light}";
        if (_cache.TryGetValue(key, out var icon)) return icon;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float pen = Math.Max(2f, size / 7f);
            var rect = new RectangleF(pen / 2 + 0.5f, pen / 2 + 0.5f, size - pen - 1, size - pen - 1);
            var track = light ? Color.FromArgb(70, 0, 0, 0) : Color.FromArgb(90, 255, 255, 255);
            var fill = error ? Color.FromArgb(232, 70, 60) : paused ? Color.FromArgb(240, 170, 40) : Color.FromArgb(40, 170, 230);
            using (var p = new Pen(track, pen)) g.DrawEllipse(p, rect);
            using (var p = new Pen(fill, pen) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                if (bucket > 0) g.DrawArc(p, rect, -90, 360f * bucket / 25f);
            // dense core
            float c = size * 0.22f;
            using var core = new SolidBrush(light ? Color.FromArgb(58, 42, 150) : Color.FromArgb(220, 228, 255));
            g.FillRectangle(core, size / 2f - c / 2, size / 2f - c / 2, c, c);
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            icon = (Icon)tmp.Clone();
        }
        finally { Native.DestroyIcon(h); }
        _cache[key] = icon;
        return icon;
    }

    public void Dispose()
    {
        foreach (var i in _cache.Values) i.Dispose();
        _cache.Clear();
    }
}

/// <summary>Applies or removes the Explorer integration plan (HKCU for per-user installs, HKLM for all-users).</summary>
internal static class Registration
{
    private static RegistryKey Root(string? scope) => scope == "machine" ? Registry.LocalMachine : Registry.CurrentUser;

    public static int Apply(string? scope, ShellFeatures features, IEnumerable<string> quickProfiles)
    {
        try
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            string icon = Path.Combine(AppContext.BaseDirectory, "icons", "archive.ico");
            var root = Root(scope);
            foreach (var v in ShellRegistrationPlan.Build(exe, icon, features, quickProfiles))
            {
                using var k = root.CreateSubKey(v.Key, writable: true);
                k.SetValue(v.Name ?? "", v.Value);
            }
            Native.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
            return 0;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            AppLog.Error("registration failed: " + e.Message);
            return 3;
        }
    }

    public static int Remove(string? scope)
    {
        var root = Root(scope);
        foreach (var key in ShellRegistrationPlan.OwnedKeys)
            try { root.DeleteSubKeyTree(key, throwOnMissingSubKey: false); } catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
        try
        {
            using (var osa = root.OpenSubKey($@"{ShellRegistrationPlan.Classes}\.osa", writable: true))
                if (osa?.GetValue("") as string == ShellRegistrationPlan.ProgId) osa.DeleteValue("", throwOnMissingValue: false);
            using var run = root.OpenSubKey(ShellRegistrationPlan.RunKey, writable: true);
            run?.DeleteValue(ShellRegistrationPlan.RunValue, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
        Native.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        return 0;
    }

    public static void SetStartup(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(ShellRegistrationPlan.RunKey, writable: true);
            if (enabled) run.SetValue(ShellRegistrationPlan.RunValue, $"\"{Environment.ProcessPath}\" --tray");
            else run.DeleteValue(ShellRegistrationPlan.RunValue, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { AppLog.Error("startup setting: " + e.Message); }
    }
}

/// <summary>In-memory ring-buffer log shown on the Log page and mirrored to %LOCALAPPDATA%\Osmium\logs.</summary>
internal static class AppLog
{
    public sealed record Entry(DateTime Time, string Level, string Message);
    private static readonly LinkedList<Entry> Entries = new();
    private static readonly object Lock = new();
    public static event Action? Changed;

    public static void Info(string m) => Add("info", m);
    public static void Warn(string m) => Add("warn", m);
    public static void Error(string m) => Add("error", m);

    private static void Add(string level, string m)
    {
        var e = new Entry(DateTime.Now, level, m);
        lock (Lock)
        {
            Entries.AddLast(e);
            while (Entries.Count > 10_000) Entries.RemoveFirst();
        }
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            File.AppendAllText(Path.Combine(AppPaths.LogDir, $"osmium-{DateTime.Now:yyyyMMdd}.log"), $"{e.Time:HH:mm:ss.fff} [{level}] {m}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Changed?.Invoke();
    }

    public static List<Entry> Snapshot() { lock (Lock) return Entries.ToList(); }

    public static void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDir}\"") { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

internal static class Shell
{
    public static void Reveal(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { AppLog.Warn(e.Message); }
    }
}
