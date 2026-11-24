using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Osmium.Desktop.Settings;
using Osmium.Desktop.Shell;

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

    internal const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    /// <summary>Immersive dark title bar (attribute 20 on Win10 20H1+, 19 on older builds).</summary>
    internal static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref v, sizeof(int));
    }
}

/// <summary>Colours for custom-drawn parts; standard controls use WinForms' own colour mode.</summary>
internal sealed record Palette(Color Background, Color Surface, Color Border, Color Text, Color Muted, Color Accent, Color AccentText, Color Danger, Color Experimental, Color Hover)
{
    public static readonly Palette Dark = new(Color.FromArgb(32, 32, 32), Color.FromArgb(43, 43, 43), Color.FromArgb(60, 60, 60),
        Color.FromArgb(240, 240, 240), Color.FromArgb(160, 160, 160), Color.FromArgb(96, 140, 255), Color.White,
        Color.FromArgb(255, 99, 99), Color.FromArgb(232, 89, 12), Color.FromArgb(55, 55, 55));

    public static readonly Palette Light = new(Color.FromArgb(243, 243, 243), Color.White, Color.FromArgb(229, 229, 229),
        Color.FromArgb(27, 27, 27), Color.FromArgb(95, 95, 95), Color.FromArgb(58, 42, 150), Color.White,
        Color.FromArgb(196, 43, 28), Color.FromArgb(232, 89, 12), Color.FromArgb(232, 232, 240));
}

internal static class Theme
{
    public static bool IsDark { get; private set; }
    public static Palette P { get; private set; } = Palette.Light;
    public static Font Body { get; private set; } = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
    public static Font Heading { get; private set; } = Control.DefaultFont;
    public static Font Small { get; private set; } = Control.DefaultFont;
    public static Font Bold { get; private set; } = Control.DefaultFont;
    public static Font Glyph { get; private set; } = Control.DefaultFont;
    public static Font Mono { get; private set; } = Control.DefaultFont;

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
        string family = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
        Body = new Font(family, 9f);
        Small = new Font(family, 8f);
        Bold = new Font(family, 9f, FontStyle.Bold);
        Heading = new Font(FontExists("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI", 15f, FontStyle.Bold);
        Glyph = new Font(FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 12f);
        Mono = new Font(FontExists("Cascadia Mono") ? "Cascadia Mono" : "Consolas", 9f);
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

    /// <summary>Applies palette colours to a control tree (colour mode handles most native controls).</summary>
    public static void Apply(Control root)
    {
        root.BackColor = P.Background;
        root.ForeColor = P.Text;
        foreach (Control c in root.Controls) ApplyChild(c);
    }

    private static void ApplyChild(Control c)
    {
        switch (c)
        {
            case Button b when b.Tag as string == "accent":
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = P.Accent;
                b.ForeColor = P.AccentText;
                b.FlatAppearance.BorderSize = 0;
                break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = P.Surface;
                b.ForeColor = P.Text;
                b.FlatAppearance.BorderColor = P.Border;
                break;
            case TextBox or ListView or ListBox or ComboBox or NumericUpDown or TreeView or RichTextBox:
                c.BackColor = P.Surface;
                c.ForeColor = P.Text;
                break;
            case Label l when l.Tag as string == "muted":
                l.ForeColor = P.Muted;
                break;
            case Label l when l.Tag as string == "experimental":
                l.ForeColor = P.Experimental;
                break;
            case Panel or TableLayoutPanel or FlowLayoutPanel or UserControl or SplitContainer or GroupBox or CheckBox or RadioButton or Label:
                if (c.Tag as string != "keep") { c.BackColor = c.Tag as string == "surface" ? P.Surface : P.Background; c.ForeColor = P.Text; }
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
