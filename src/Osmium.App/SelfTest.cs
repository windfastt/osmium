using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Osmium.Desktop;
using Osmium.Desktop.Jobs;
using Osmium.Desktop.Samples;
using Osmium.Desktop.Settings;
using Osmium.Desktop.Shell;

namespace Osmium.App;

/// <summary>
/// <c>Osmium.App.exe --selftest-ui &lt;dir&gt; [dark]</c>: runs the real app on a sample folder it creates (Analyze, a
/// compression job with profile max through the job manager, the archive it wrote, a Lab run, the Tune dialog) and
/// renders every page and dialog to PNG. It writes manifest.json (manifest-dark.json for the dark run) listing each
/// PNG with its size, theme and caption, and selftest.log (selftest-dark.log). A failed step, a message box, a UI
/// exception or an error in the app log makes the exit code 1. Runs in CI on windows-latest, which is how the
/// WinForms layer gets exercised although development happens on Linux; nothing in the screenshots is mocked up.
/// </summary>
internal static partial class SelfTest
{
    [LibraryImport("user32.dll")]
    private static partial uint GetGuiResources(IntPtr process, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetLongPathName(string shortPath, char* longPath, uint size);

    /// <summary>
    /// The main window's client area (navigation rail and page). Windows limits a window to the desktop plus its frame
    /// (1044 × 788 on a 1024 × 768 test machine), so a wider client area would be laid out wider than it is captured.
    /// </summary>
    private static readonly Size MainClient = new(1040, 760);
    // Measured on a 4-core Linux machine: the job ~2 s, the Lab run ~10 s (it benchmarks CM candidates too).
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(120), LabTimeout = TimeSpan.FromSeconds(120),
        WorkTimeout = TimeSpan.FromSeconds(60);

    private sealed record Shot(string File, int Width, int Height, string Theme, string Caption);

    public static int Run(string outDir)
    {
        // Before the first window: an exception in an event handler must fail a step, not open a dialog nobody closes.
        try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); }
        catch (InvalidOperationException) { }
        var problems = new List<string>();
        Application.ThreadException += (_, e) => problems.Add("UI exception: " + e.Exception);
        Ui.Unattended = m => problems.Add("message box: " + m);

        bool dark = Environment.GetCommandLineArgs().Skip(1).Any(a => a.Equals("dark", StringComparison.OrdinalIgnoreCase));
        string theme = dark ? "dark" : "light", suffix = dark ? "-dark" : "";
        var log = new List<string>();
        var shots = new List<Shot>();
        int failures = 0;
        Directory.CreateDirectory(outDir);
        var tmp = Path.Combine(Path.GetTempPath(), "osmium-selftest-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmp);

        bool Step(string name, Action action)
        {
            problems.Clear();
            int logged = AppLog.Snapshot().Count;
            var clock = Stopwatch.StartNew();
            try { action(); }
            catch (Exception e) { problems.Add(e.ToString()); }
            problems.AddRange(AppLog.Snapshot().Skip(logged).Where(e => e.Level == "error").Select(e => "app log: " + e.Message));
            if (problems.Count == 0)
            {
                log.Add($"ok    {name} ({clock.Elapsed.TotalSeconds:F1} s)");
                return true;
            }
            failures++;
            log.Add($"FAIL  {name}: " + string.Join(Environment.NewLine + "      ", problems));
            return false;
        }

        void Skip(string name, string why) => log.Add($"skip  {name}: {why}");

        void Shoot(Control c, string id, string caption)
        {
            Pump();
            using var bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height), PixelFormat.Format24bppRgb);
            c.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
            string file = id + suffix + ".png";
            bmp.Save(Path.Combine(outDir, file), ImageFormat.Png);
            shots.Add(new Shot(file, bmp.Width, bmp.Height, theme, caption));
        }

        Stage? stage = null;
        try
        {
            Theme.Initialize(dark ? ThemeMode.Dark : ThemeMode.Light);
            var store = new SettingsStore(Path.Combine(tmp, "settings.json"));
            var settings = new OsmiumSettings();
            settings.Experimental.Enabled = true; // the Experimental page then shows its switches enabled
            using var app = new TrayApp(store, settings, new AppCommand("tray"), "Osmium.SelfTest." + Guid.NewGuid().ToString("N")[..6],
                historyFile: Path.Combine(tmp, "history.json"));
            stage = new Stage();
            stage.Show();

            List<string> files = [];
            string tone = "";
            Step("sample folder", () =>
            {
                files = CreateSampleFolder(Path.Combine(LongPath(Path.GetTempPath()), "Osmium sample"));
                tone = files.Single(f => Path.GetFileName(f) == SampleFiles.Tone);
            });

            MainForm? main = null;
            Step("main window", () =>
            {
                var m = app.EnsureMain();
                stage.Host(m, MainClient);
                m.Navigate("Compress");
                main = m;
                Shoot(m, "page-compress", "Compress page before any files are added");
            });

            Guid? jobId = null;
            string? archive = null;
            if (main is null || files.Count == 0) Skip("pages", "no main window or no sample files");
            else
            {
                Step("Compress page: Analyze", () =>
                {
                    var page = main.CompressPage;
                    page.AddPaths(files);
                    page.SelectProfile("max");
                    Await(page.AnalyzeAsync(), WorkTimeout, "Analyze");
                    Shoot(main, "page-compress-filled",
                        $"Compress page: the {files.Count} sample files after Analyze, with their detected types and the plan of profile max");
                });

                Step("compression job, profile max", () =>
                {
                    // The Compress button's own action: queues the job with the page's files and profile, shows Jobs.
                    var id = main.CompressPage.StartCompression() ?? throw new InvalidOperationException("the Compress button started no job");
                    WaitFor(() => app.Jobs.Snapshot().FirstOrDefault(j => j.Id == id) is { IsActive: false }, JobTimeout, "The compression job (profile max)");
                    var job = app.Jobs.Snapshot().First(j => j.Id == id);
                    if (job.State != JobState.Completed) throw new InvalidOperationException($"the job ended {job.State}: {job.Error}");
                    jobId = id;
                    archive = job.Output;
                    main.Navigate("Jobs");
                    main.Page<JobsPage>("Jobs").SelectJob(id);
                    Shoot(main, "page-jobs",
                        $"Jobs page: the sample files ({Ui.Bytes(job.BytesIn)}) compressed with profile max to {Ui.Bytes(job.BytesOut)}, with the job's planner log");
                });

                if (archive is null) Skip("Archives page", "no archive was written");
                else
                    Step("Archives page", () =>
                    {
                        main.Navigate("Archives");
                        main.ArchivesPage.OpenArchive(archive);
                        Shoot(main, "page-archives", $"Archives page: {Path.GetFileName(archive)} with its blocks, their pipelines and its files");
                    });

                Step("Lab page: tone.wav, profile max", () =>
                {
                    main.Navigate("Lab");
                    var lab = main.Page<LabPage>("Lab");
                    lab.SetFile(tone);
                    lab.SelectProfile("max");
                    Await(lab.RunAsync(), LabTimeout, "The Lab run");
                    Shoot(main, "page-lab", "Lab page: every candidate pipeline of profile max benchmarked on tone.wav, smallest first");
                });

                Step("Rules page with the example rule", () =>
                {
                    main.Navigate("Rules");
                    main.Page<RulesPage>("Rules").AddExample();
                    Shoot(main, "page-rules", "Rules page with the example rule added, not saved yet");
                });

                Step("Cluster page with a new key", () =>
                {
                    main.Navigate("Cluster");
                    main.Page<ClusterPage>("Cluster").GenerateKey();
                    Shoot(main, "page-cluster", "Cluster page with a newly generated cluster key, shown and not saved; no workers");
                });

                Step("Settings pages", () =>
                {
                    main.Navigate("Settings");
                    var settingsPage = main.Page<SettingsPage>("Settings");
                    foreach (var category in settingsPage.CategoryNames.ToList())
                    {
                        settingsPage.ShowCategory(category); // every category must render
                        Pump();
                    }
                    settingsPage.ShowCategory("General");
                    Shoot(main, "settings-general", "Settings: General, with the default values");
                    settingsPage.ShowCategory("Experimental");
                    Shoot(main, "settings-experimental", "Settings: Experimental, with the master switch turned on");
                });

                Step("Log page", () =>
                {
                    main.Navigate("Log");
                    Shoot(main, "page-log", "Log page after the self-test's run");
                });
            }

            if (tone.Length == 0) Skip("Tune dialog", "no sample files");
            else
                Step("Tune dialog", () =>
                {
                    using var d = new TuneDialog(app, tone, null, "max", startDeep: false);
                    stage.Host(d, d.ClientSize); // the client area it has as a window of its own
                    Await(d.Analysis, WorkTimeout, "The Tune dialog's analysis");
                    Shoot(d, "dialog-tune", "Tune dialog for tone.wav after its analysis, with the suggested pipeline and its parameters");
                    d.Close();
                });

            Step("tray menu", () =>
            {
                var menu = app.TrayMenu;
                menu.AutoClose = false; // stays open while it is rendered
                try
                {
                    menu.Show(new Point(-10_000, -10_000)); // WinForms moves it onto the screen; only the rendering is used
                    Pump();
                    if (!menu.Visible) throw new InvalidOperationException("the tray menu did not open");
                    Shoot(menu, "tray-menu", jobId is null ? "Notification-area menu" : "Notification-area menu after the compression job finished");
                }
                finally
                {
                    menu.Close();
                    menu.AutoClose = true;
                }
            });

            Step("password dialog", () =>
            {
                using var d = new PasswordDialog("Password for the new archive", confirm: true);
                stage.Host(d, null);
                Shoot(d, "dialog-password", "Password dialog for a new archive, before anything is typed");
                d.Close();
            });

            Step("About dialog", () =>
            {
                using var d = new AboutDialog(app.AppIcon);
                stage.Host(d, null);
                Shoot(d, "dialog-about", "About dialog: version, engine fingerprint and licence");
                d.Close();
            });

            Step("tray icon frames", () =>
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
                string file = "tray-frames" + suffix + ".png";
                strip.Save(Path.Combine(outDir, file), ImageFormat.Png);
                shots.Add(new Shot(file, strip.Width, strip.Height, theme, "Notification-area icon: progress from 0 to 100 %, then paused and failed at 50 %"));
            });

            Step("handle counts", () =>
            {
                var h = Process.GetCurrentProcess().Handle;
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
            try { stage?.Dispose(); } catch (Exception e) { log.Add("note  closing the stage: " + e.Message); }
            try { Directory.Delete(tmp, true); } catch (IOException) { }
            try { WriteManifest(Path.Combine(outDir, dark ? "manifest-dark.json" : "manifest.json"), theme, shots); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failures++; log.Add("FAIL  manifest: " + e.Message); }
        }
        log.Add($"{shots.Count} screenshot(s)");
        log.Add(failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failures})");
        File.WriteAllLines(Path.Combine(outDir, $"selftest{suffix}.log"), log);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Recreates <paramref name="dir"/> with the generated sample files, a copy of Osmium.Core.dll from the program
    /// folder and chart.png (a GDI+ chart of their sizes). Returns the files sorted by name.
    /// </summary>
    private static List<string> CreateSampleFolder(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        string license = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        var files = SampleFiles.Write(dir, File.Exists(license) ? File.ReadAllText(license) : null).ToList();
        string dll = Path.Combine(dir, "Osmium.Core.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Osmium.Core.dll"), dll);
        files.Add(dll);
        string chart = Path.Combine(dir, "chart.png");
        SaveChart(chart, files);
        files.Add(chart);
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>A bar chart of the other sample files' sizes, saved as PNG (data that is already compressed).</summary>
    private static void SaveChart(string path, IReadOnlyList<string> files)
    {
        const int width = 640, height = 300, top = 56, left = 150, right = width - 100;
        var sizes = files.Select(f => (Name: Path.GetFileName(f), KiB: new FileInfo(f).Length / 1024.0)).ToList();
        double largest = Math.Max(1, sizes.Max(s => s.KiB));
        int row = (height - top - 12) / Math.Max(1, sizes.Count);
        using var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.White);
            using var font = new Font("Segoe UI", 10f);
            using var title = new Font("Segoe UI", 12f, FontStyle.Bold);
            using var ink = new SolidBrush(Color.FromArgb(27, 27, 27));
            using var bar = new SolidBrush(Color.FromArgb(58, 42, 150));
            g.DrawString("Osmium sample: file sizes in KiB", title, ink, 16, 14);
            for (int i = 0; i < sizes.Count; i++)
            {
                int y = top + i * row;
                float w = (float)((right - left) * sizes[i].KiB / largest);
                g.DrawString(sizes[i].Name, font, ink, 16, y + 2);
                g.FillRectangle(bar, left, y + 4, Math.Max(1f, w), row - 14);
                g.DrawString(sizes[i].KiB.ToString("N0", CultureInfo.InvariantCulture), font, ink, left + w + 6, y + 2);
            }
        }
        bmp.Save(path, ImageFormat.Png);
    }

    private static void WriteManifest(string path, string theme, IReadOnlyList<Shot> shots)
    {
        var asm = typeof(SelfTest).Assembly;
        var manifest = new
        {
            theme,
            version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            engine = AboutInfo.EngineFingerprint(),
            screenshots = shots.Select(s => new { file = s.File, width = s.Width, height = s.Height, theme = s.Theme, caption = s.Caption }),
        };
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, options) + "\n");
    }

    /// <summary>The long form of a path (GitHub runners set TEMP to an 8.3 name such as RUNNER~1).</summary>
    private static unsafe string LongPath(string path)
    {
        var buffer = new char[1024];
        fixed (char* p = buffer)
        {
            uint n = GetLongPathName(path, p, (uint)buffer.Length);
            return n > 0 && n < buffer.Length ? new string(p, 0, (int)n) : path;
        }
    }

    /// <summary>Processes window messages (including await continuations) until <paramref name="done"/>.</summary>
    private static void WaitFor(Func<bool> done, TimeSpan timeout, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > timeout) throw new TimeoutException($"{what} did not finish within {timeout.TotalSeconds:0} s.");
            Application.DoEvents();
            Thread.Sleep(15);
        }
        Pump();
    }

    private static void Await(Task task, TimeSpan timeout, string what)
    {
        WaitFor(() => task.IsCompleted, timeout, what);
        task.GetAwaiter().GetResult();
    }

    private static void Pump()
    {
        for (int i = 0; i < 10; i++)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// A borderless window that holds the main window and the dialogs as child windows: top-level windows are
    /// clamped to the runner's 1024 × 768 desktop, child windows are not, and a child window has no OS title bar.
    /// </summary>
    private sealed class Stage : Form
    {
        public Stage()
        {
            Text = "Osmium UI self-test";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = Point.Empty;
            Size = new Size(1280, 900); // the desktop may make it smaller; the windows it holds keep their size
            BackColor = Theme.P.Background;
        }

        /// <summary>Shows <paramref name="form"/> inside the stage with the given client size (null: it sizes itself).</summary>
        public void Host(Form form, Size? clientSize)
        {
            form.TopLevel = false;
            form.FormBorderStyle = FormBorderStyle.None;
            form.Location = Point.Empty;
            if (clientSize is { } size) form.ClientSize = size;
            Controls.Add(form);
            form.Show();
            form.BringToFront();
            Pump();
        }
    }
}
