using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Osmium.Desktop.Settings;
using Osmium.Desktop.Shell;

namespace Osmium.App;

/// <summary>
/// <c>Osmium.App.exe --selftest-ui &lt;dir&gt; [dark]</c>: builds every window and page, renders each to PNG, renders the
/// tray icon frames and reports GDI/USER handle counts. Runs in CI on a real Windows desktop, which is how the
/// WinForms layer gets exercised even though development happens on Linux.
/// </summary>
internal static partial class SelfTest
{
    [LibraryImport("user32.dll")]
    private static partial uint GetGuiResources(IntPtr process, uint flags);

    public static int Run(string outDir)
    {
        var log = new List<string>();
        int failures = 0;
        Directory.CreateDirectory(outDir);
        bool dark = Environment.GetCommandLineArgs().Any(a => a.Equals("dark", StringComparison.OrdinalIgnoreCase));
        var tmp = Path.Combine(Path.GetTempPath(), "osmium-selftest-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmp);
        try
        {
            Theme.Initialize(dark ? ThemeMode.Dark : ThemeMode.Light);
            var store = new SettingsStore(Path.Combine(tmp, "settings.json"));
            var settings = new OsmiumSettings();
            settings.Experimental.Enabled = true;
            using var app = new TrayApp(store, settings, new AppCommand("tray"), "Osmium.SelfTest." + Guid.NewGuid().ToString("N")[..6],
                historyFile: Path.Combine(tmp, "history.json"));
            string suffix = dark ? "-dark" : "";

            void Step(string name, Action a)
            {
                try { a(); log.Add("ok    " + name); }
                catch (Exception e) { failures++; log.Add($"FAIL  {name}: {e}"); }
            }

            Step("main window", () => app.ShowMain());
            var main = app.Main!;
            main.Location = new Point(0, 0);
            main.Size = new Size(1180, 760);
            foreach (var page in new[] { "Compress", "Jobs", "Archives", "Lab", "Rules", "Cluster", "Settings", "Log" })
                Step("page " + page, () =>
                {
                    main.Navigate(page);
                    Pump();
                    Save(main, Path.Combine(outDir, $"page-{page.ToLowerInvariant()}{suffix}.png"));
                });

            // every settings category
            Step("settings categories", () =>
            {
                main.Navigate("Settings");
                var sp = FindPage<SettingsPage>(main);
                foreach (var cat in sp!.CategoryNames.ToList())
                {
                    sp.ShowCategory(cat);
                    Pump();
                    Save(main, Path.Combine(outDir, $"settings-{cat.ToLowerInvariant().Replace(' ', '-')}{suffix}.png"));
                }
            });

            // compress page with content, then the tune dialog
            var sample = Path.Combine(tmp, "sample.txt");
            File.WriteAllText(sample, string.Concat(Enumerable.Repeat("Osmium self-test sample text. ", 2000)));
            Step("compress page with files", () =>
            {
                main.Navigate("Compress");
                main.CompressPage.AddPaths([sample, tmp]);
                Pump();
                Save(main, Path.Combine(outDir, $"page-compress-filled{suffix}.png"));
            });
            Step("tune dialog", () =>
            {
                using var d = new TuneDialog(app, sample, null, "max", startDeep: false);
                d.StartPosition = FormStartPosition.Manual;
                d.Location = new Point(0, 0);
                d.Show();
                for (int i = 0; i < 20; i++) { Pump(); Thread.Sleep(50); }
                Save(d, Path.Combine(outDir, $"dialog-tune{suffix}.png"));
                d.Close();
            });

            // tray icon frames
            Step("tray icons", () =>
            {
                using var icons = new TrayIcons(app.AppIcon);
                using var strip = new Bitmap(16 * 30, 32);
                using (var g = Graphics.FromImage(strip))
                {
                    g.Clear(dark ? Color.FromArgb(32, 32, 32) : Color.White);
                    for (int i = 0; i <= 25; i++) g.DrawIcon(icons.Progress(i / 25.0, false, false), i * 18, 0);
                    g.DrawIcon(icons.Progress(0.5, true, false), 0, 16);
                    g.DrawIcon(icons.Progress(0.5, false, true), 18, 16);
                }
                strip.Save(Path.Combine(outDir, $"tray-frames{suffix}.png"), ImageFormat.Png);
            });

            Step("handle counts", () =>
            {
                var h = System.Diagnostics.Process.GetCurrentProcess().Handle;
                uint gdi = GetGuiResources(h, 0), user = GetGuiResources(h, 1);
                log.Add($"      GDI objects: {gdi}, USER objects: {user}");
                if (gdi > 5000 || user > 5000) throw new InvalidOperationException("handle leak suspected");
            });

            app.ExitApp(confirm: false);
        }
        catch (Exception e)
        {
            failures++;
            log.Add("FAIL  startup: " + e);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { }
        }
        log.Add(failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failures})");
        File.WriteAllLines(Path.Combine(outDir, "selftest.log"), log);
        return failures == 0 ? 0 : 1;
    }

    private static T? FindPage<T>(Control root) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T t) return t;
            var found = FindPage<T>(c);
            if (found is not null) return found;
        }
        return null;
    }

    private static void Pump()
    {
        for (int i = 0; i < 5; i++) Application.DoEvents();
    }

    private static void Save(Control c, string path)
    {
        using var bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height));
        c.DrawToBitmap(bmp, new Rectangle(Point.Empty, c.Size));
        bmp.Save(path, ImageFormat.Png);
    }
}
