// The UI self-test drives the app through these members. It relies on their signatures and on this behaviour: each one
// returns, or its task completes, only once the page or dialog shows the result.
//   TrayApp: new TrayApp(SettingsStore, OsmiumSettings, AppCommand, string pipeName, string? historyFile), EnsureMain(),
//     Jobs, TrayMenu, AppIcon, ExitApp(bool confirm), Settings
//   MainForm: Navigate(string), CompressPage, ArchivesPage, Page<T>(string)
//   CompressPage: AddPaths(IEnumerable<string>), SelectProfile(string), AnalyzeAsync(), StartCompression()
//   JobsPage: SelectJob(Guid)
//   ArchivesPage: OpenArchiveAsync(string)
//   LabPage: SetFile(string), SelectProfile(string), RunAsync()
//   RulesPage: AddExample()
//   ClusterPage: GenerateKey()
//   SettingsPage: CategoryNames, ShowCategory(string)
//   new TuneDialog(TrayApp, string path, string? current, string profile, bool startDeep) and its Analysis task
//   new PasswordDialog(string prompt, bool confirm); new AboutDialog(Icon)
//   TrayIcons.Progress(double, bool, bool); Ui.Unattended
//   Recycle Bin: ShellRecycler (TSF_DELETE_RECYCLE_IF_POSSIBLE, Refuse, DriveTypeOf, Recycle, LastPreDeletes),
//     RecycleGate(Func<string, string?>, CancellationToken).PreDelete, RecycleSummary, Settings.Compression.RecycleOriginals
// Further steps go in SelfTest.Compress.cs, SelfTest.Archives.cs, SelfTest.Pages.cs and SelfTest.Layout.cs, which
// implement the partial methods below through SelfTestContext.

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
/// renders every page and dialog to PNG. It also moves folders it creates in its own temporary folder to the Recycle
/// Bin, directly and through a job with "move the originals to the Recycle Bin" on, and checks what stays. It writes manifest.json (manifest-dark.json for the dark run) listing each
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
    internal static readonly Size MainClient = new(1040, 760);
    // Measured on a 4-core Linux machine: the job ~2 s, the Lab run ~10 s (it benchmarks CM candidates too).
    internal static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(120), LabTimeout = TimeSpan.FromSeconds(120),
        WorkTimeout = TimeSpan.FromSeconds(60);

    internal sealed record Shot(string File, int Width, int Height, string Theme, string Caption);

    // Further steps, each leaving the main window hosted at MainClient on the Compress page (SelfTestContext.RestoreMain).
    /// <summary>Compress page steps; run after the "Archives page" step.</summary>
    static partial void CompressSteps(SelfTestContext ctx);
    /// <summary>Archives page steps; run right after <see cref="CompressSteps"/>.</summary>
    static partial void ArchiveSteps(SelfTestContext ctx);
    /// <summary>Steps for the other pages; run after the "Log page" step.</summary>
    static partial void PageSteps(SelfTestContext ctx);
    /// <summary>Layout checks; run after the "About dialog" step.</summary>
    static partial void LayoutSteps(SelfTestContext ctx);

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

        Stage? stage = null;
        SelfTestContext? context = null;
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
            var ctx = context = new SelfTestContext(app, stage, dark, tmp, outDir, log, shots, problems);

            string tone = "";
            ctx.Step("sample folder", () =>
            {
                string folder = Path.Combine(LongPath(Path.GetTempPath()), "Osmium sample");
                ctx.SampleFiles = CreateSampleFolder(folder);
                ctx.SampleFolder = folder;
                tone = ctx.SampleFiles.Single(f => Path.GetFileName(f) == SampleFiles.Tone);
            });

            MainForm? main = null;
            ctx.Step("main window", () =>
            {
                var m = app.EnsureMain();
                stage.Host(m, MainClient);
                m.Navigate("Compress");
                main = m;
                ctx.Main = m;
                ctx.Shoot(m, "page-compress", "Compress page before any files are added");
            });

            var files = ctx.SampleFiles;
            if (main is null || files.Count == 0) ctx.Skip("pages", "no main window or no sample files");
            else
            {
                ctx.Step("Compress page: Analyze", () =>
                {
                    var page = main.CompressPage;
                    page.AddPaths(files);
                    page.SelectProfile("max");
                    ctx.Await(page.AnalyzeAsync(), WorkTimeout, "Analyze");
                    ctx.Shoot(main, "page-compress-filled",
                        $"Compress page: the {files.Count} sample files after Analyze, with their detected types and the plan of profile max");
                });

                ctx.Step("compression job, profile max", () =>
                {
                    // The Compress button's own action: queues the job with the page's files and profile, shows Jobs.
                    var id = main.CompressPage.StartCompression() ?? throw new InvalidOperationException("the Compress button started no job");
                    ctx.WaitFor(() => app.Jobs.Snapshot().FirstOrDefault(j => j.Id == id) is { IsActive: false }, JobTimeout, "The compression job (profile max)");
                    var job = app.Jobs.Snapshot().First(j => j.Id == id);
                    if (job.State != JobState.Completed) throw new InvalidOperationException($"the job ended {job.State}: {job.Error}");
                    ctx.JobId = id;
                    ctx.JobArchive = job.Output;
                    main.Navigate("Jobs");
                    main.Page<JobsPage>("Jobs").SelectJob(id);
                    ctx.Shoot(main, "page-jobs",
                        $"Jobs page: the sample files ({Ui.Bytes(job.BytesIn)}) compressed with profile max to {Ui.Bytes(job.BytesOut)}, with the job's planner log");
                });

                if (ctx.JobArchive is not { } archive) ctx.Skip("Archives page", "no archive was written");
                else
                    ctx.Step("Archives page", () =>
                    {
                        main.Navigate("Archives");
                        ctx.Await(main.ArchivesPage.OpenArchiveAsync(archive), WorkTimeout, "Opening the archive");
                        ctx.Shoot(main, "page-archives", $"Archives page: {Path.GetFileName(archive)} with its blocks, their pipelines and its files");
                    });

                CompressSteps(ctx);
                ArchiveSteps(ctx);

                ctx.Step("Lab page: tone.wav, profile max", () =>
                {
                    main.Navigate("Lab");
                    var lab = main.Page<LabPage>("Lab");
                    lab.SetFile(tone);
                    lab.SelectProfile("max");
                    ctx.Await(lab.RunAsync(), LabTimeout, "The Lab run");
                    ctx.Shoot(main, "page-lab", "Lab page: every candidate pipeline of profile max benchmarked on tone.wav, smallest first");
                });

                ctx.Step("Rules page with the example rule", () =>
                {
                    main.Navigate("Rules");
                    main.Page<RulesPage>("Rules").AddExample();
                    ctx.Shoot(main, "page-rules", "Rules page with the example rule added, not saved yet");
                });

                ctx.Step("Cluster page with a new key", () =>
                {
                    main.Navigate("Cluster");
                    main.Page<ClusterPage>("Cluster").GenerateKey();
                    ctx.Shoot(main, "page-cluster", "Cluster page with a newly generated cluster key, shown and not saved; no workers");
                });

                ctx.Step("Settings pages", () =>
                {
                    main.Navigate("Settings");
                    var settingsPage = main.Page<SettingsPage>("Settings");
                    foreach (var category in settingsPage.CategoryNames.ToList())
                    {
                        settingsPage.ShowCategory(category); // every category must render
                        ctx.Pump();
                    }
                    settingsPage.ShowCategory("General");
                    ctx.Shoot(main, "settings-general", "Settings: General, with the default values");
                    settingsPage.ShowCategory("Experimental");
                    ctx.Shoot(main, "settings-experimental", "Settings: Experimental, with the master switch turned on");
                });

                ctx.Step("Log page", () =>
                {
                    main.Navigate("Log");
                    ctx.Shoot(main, "page-log", "Log page after the self-test's run");
                });

                PageSteps(ctx);
            }

            if (tone.Length == 0) ctx.Skip("Tune dialog", "no sample files");
            else
                ctx.Step("Tune dialog", () =>
                {
                    using var d = new TuneDialog(app, tone, null, "max", startDeep: false);
                    ctx.Host(d, d.ClientSize); // the client area it has as a window of its own
                    ctx.Await(d.Analysis, WorkTimeout, "The Tune dialog's analysis");
                    ctx.Shoot(d, "dialog-tune", "Tune dialog for tone.wav after its analysis, with the suggested pipeline and its parameters");
                    d.Close();
                });

            ctx.Step("tray menu", () =>
            {
                var menu = app.TrayMenu;
                menu.AutoClose = false; // stays open while it is rendered
                try
                {
                    menu.Show(new Point(-10_000, -10_000)); // WinForms moves it onto the screen; only the rendering is used
                    ctx.Pump();
                    if (!menu.Visible) throw new InvalidOperationException("the tray menu did not open");
                    ctx.Shoot(menu, "tray-menu", ctx.JobId is null ? "Notification-area menu" : "Notification-area menu after the compression job finished");
                }
                finally
                {
                    menu.Close();
                    menu.AutoClose = true;
                }
            });

            ctx.Step("password dialog", () =>
            {
                using var d = new PasswordDialog("Password for the new archive", confirm: true);
                ctx.Host(d, null);
                ctx.Shoot(d, "dialog-password", "Password dialog for a new archive, before anything is typed");
                d.Close();
            });

            ctx.Step("About dialog", () =>
            {
                using var d = new AboutDialog(app.AppIcon);
                ctx.Host(d, null);
                ctx.Shoot(d, "dialog-about", "About dialog: version, engine fingerprint and licence");
                d.Close();
            });

            LayoutSteps(ctx);

            ctx.Step("tray icon frames", () =>
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

            // Everything the Recycle Bin steps move or keep lies in the self-test's own temporary folder (deleted at the
            // end); what they move goes to the Recycle Bin of the machine running the test.
            string recycleRoot = Path.Combine(LongPath(tmp), "recycle");
            string Own(string path) => Path.GetFullPath(path).StartsWith(recycleRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? path : throw new InvalidOperationException($"{path} is outside the self-test's folder");
            // A page saved with "Webpage, complete": the shell treats the page and its "_files" folder as connected.
            string SavedPage(string folder, string name)
            {
                Directory.CreateDirectory(Path.Combine(folder, name + "_files"));
                File.WriteAllText(Path.Combine(folder, name + "_files", "style.css"), "body { margin: 0 }");
                string page = Own(Path.Combine(folder, name + ".html"));
                File.WriteAllText(page, $"<html><head><link rel=\"stylesheet\" href=\"{name}_files/style.css\"></head><body>A saved page.</body></html>");
                return page;
            }

            ctx.Step("Recycle Bin: the shell recycler moves a folder and nothing else", () =>
            {
                // Before the shell deletes anything: no Recycle Bin, no path, or a canceled job means the item stays.
                const uint recycle = ShellRecycler.TSF_DELETE_RECYCLE_IF_POSSIBLE;
                Func<string, string?> unchanged = _ => null;
                if (new RecycleGate(unchanged, CancellationToken.None).PreDelete(0, null, 0) != ShellRecycler.Refuse
                    || new RecycleGate(unchanged, CancellationToken.None).PreDelete(recycle, null, 0) != ShellRecycler.Refuse
                    || new RecycleGate(unchanged, new CancellationToken(canceled: true)).PreDelete(recycle, null, 0) != ShellRecycler.Refuse)
                    throw new InvalidOperationException("the Recycle Bin gate would allow a delete it must refuse");
                string folder = Own(Path.Combine(recycleRoot, "direct", "a folder"));
                Directory.CreateDirectory(Path.Combine(folder, "sub"));
                File.WriteAllText(Path.Combine(folder, "sub", "file.txt"), "Moved to the Recycle Bin by the Osmium UI self-test.");
                var recycler = new ShellRecycler();
                if (recycler.DriveTypeOf(folder) is var drive and not DriveType.Fixed)
                    throw new InvalidOperationException($"the temporary folder is on a {drive} drive, not a fixed one");

                // Refused at the last moment (as for an item that changed): the shell must leave it where it is.
                string refused = Own(Path.Combine(recycleRoot, "direct", "refused"));
                Directory.CreateDirectory(refused);
                File.WriteAllText(Path.Combine(refused, "file.txt"), "Must stay where it is.");
                var refusal = recycler.Recycle([refused], _ => "it changed after it was read", CancellationToken.None).Single();
                if (refusal.Recycled || !File.Exists(Path.Combine(refused, "file.txt")))
                    throw new InvalidOperationException("the shell moved an item that the progress sink refused: " + refusal.Reason);
                if (refusal.Reason != "checked again just before it moved: it changed after it was read")
                    throw new InvalidOperationException("the refused item was not refused by the last check before it moved: " + refusal.Reason);

                var outcome = recycler.Recycle([folder], unchanged, CancellationToken.None).Single();
                if (!outcome.Recycled) throw new InvalidOperationException("the folder was not moved to the Recycle Bin: " + outcome.Reason);
                if (Directory.Exists(folder)) throw new InvalidOperationException("the folder is still in place");
                if (recycler.LastPreDeletes == 0) throw new InvalidOperationException("the shell moved the folder without asking the progress sinks first");
                if (outcome.BinPath is not { } binPath || !Directory.Exists(binPath))
                    throw new InvalidOperationException($"the shell named no Recycle Bin item that exists ({outcome.BinPath ?? "no path"})");
                ctx.Detail($"in the Recycle Bin as {binPath}; the shell asked {recycler.LastPreDeletes} time(s) before deleting");

                // A page and its connected "_files" folder: each moves alone, and the other one stays.
                string page = SavedPage(Path.Combine(recycleRoot, "direct", "page"), "page");
                string pageFiles = Path.Combine(Path.GetDirectoryName(page)!, "page_files");
                var pageOutcome = recycler.Recycle([page], unchanged, CancellationToken.None).Single();
                if (!pageOutcome.Recycled || File.Exists(page)) throw new InvalidOperationException("the page was not moved to the Recycle Bin: " + pageOutcome.Reason);
                if (!File.Exists(Path.Combine(pageFiles, "style.css"))) throw new InvalidOperationException("the page's \"_files\" folder went along with the page");
                string other = SavedPage(Path.Combine(recycleRoot, "direct", "other"), "other");
                string otherFiles = Own(Path.Combine(Path.GetDirectoryName(other)!, "other_files"));
                var filesOutcome = recycler.Recycle([otherFiles], unchanged, CancellationToken.None).Single();
                if (!filesOutcome.Recycled || Directory.Exists(otherFiles)) throw new InvalidOperationException("the \"_files\" folder was not moved to the Recycle Bin: " + filesOutcome.Reason);
                if (!File.Exists(other)) throw new InvalidOperationException("the page went along with its \"_files\" folder");
            });

            ctx.Step("Recycle Bin option: a compression job", () =>
            {
                string moved = Own(Path.Combine(recycleRoot, "job", "originals"));
                string kept = Own(Path.Combine(recycleRoot, "job", "kept"));
                string archive = Own(Path.Combine(recycleRoot, "job", "originals.osa"));
                Directory.CreateDirectory(Path.Combine(moved, "sub"));
                File.WriteAllText(Path.Combine(moved, "a.txt"), string.Concat(Enumerable.Repeat("Compressed, then moved to the Recycle Bin.\n", 200)));
                File.WriteAllText(Path.Combine(moved, "sub", "b.txt"), "A second file.");
                Directory.CreateDirectory(kept);
                File.WriteAllText(Path.Combine(kept, "a.txt"), "Compressed, and kept with its folder.");
                File.WriteAllText(Path.Combine(kept, "Thumbs.db"), "Left out by the default exclude patterns."); // so the archive lacks a file of the folder
                string page = SavedPage(Path.Combine(recycleRoot, "job"), "saved page"); // compressed alone: its "_files" folder must stay
                string pageFiles = Path.Combine(recycleRoot, "job", "saved page_files");
                app.Settings.Compression.RecycleOriginals = true;
                try
                {
                    var id = app.Jobs.Enqueue(new JobRequest { Kind = JobKind.Compress, Inputs = [moved, kept, page], Output = archive, Profile = "store" });
                    ctx.WaitFor(() => app.Jobs.Snapshot().FirstOrDefault(j => j.Id == id) is { IsActive: false }, JobTimeout, "The compression job with the Recycle Bin option");
                    var job = app.Jobs.Snapshot().First(j => j.Id == id);
                    var jobLog = app.Jobs.GetLog(id);
                    string why = string.Join(" | ", jobLog.Where(l => l.StartsWith("kept ", StringComparison.Ordinal) || l.StartsWith("warning: ", StringComparison.Ordinal)));
                    if (job.State != JobState.Completed) throw new InvalidOperationException($"the job ended {job.State}: {job.Error}");
                    if (!File.Exists(archive)) throw new InvalidOperationException("the archive was not written");
                    if (Directory.Exists(moved) || !jobLog.Contains("moved to the Recycle Bin: " + moved))
                        throw new InvalidOperationException("the compressed folder was not moved to the Recycle Bin: " + why);
                    if (!File.Exists(Path.Combine(kept, "Thumbs.db")) || !jobLog.Any(l => l.StartsWith($"kept {kept}: ", StringComparison.Ordinal) && l.Contains("Thumbs.db")))
                        throw new InvalidOperationException("the folder holding an excluded file was not kept: " + why);
                    if (File.Exists(page) || !jobLog.Contains("moved to the Recycle Bin: " + page))
                        throw new InvalidOperationException("the compressed page was not moved to the Recycle Bin: " + why);
                    if (!File.Exists(Path.Combine(pageFiles, "style.css")))
                        throw new InvalidOperationException("the page's \"_files\" folder, which was not compressed, went along with the page");
                    if (job.Recycle != new RecycleSummary(2, 1)) throw new InvalidOperationException($"the job reported \"{job.Recycle}\" instead of 2 moved, 1 kept");
                    ctx.Detail($"originals: {job.Recycle}");
                }
                finally { app.Settings.Compression.RecycleOriginals = false; }
            });

            ctx.Step("handle counts", () =>
            {
                var h = Process.GetCurrentProcess().Handle;
                uint gdi = GetGuiResources(h, 0), user = GetGuiResources(h, 1);
                ctx.Detail($"GDI objects: {gdi}, USER objects: {user}");
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
        failures += context?.Failures ?? 0;
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
    internal static void WaitFor(Func<bool> done, TimeSpan timeout, string what)
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

    internal static void Await(Task task, TimeSpan timeout, string what)
    {
        WaitFor(() => task.IsCompleted, timeout, what);
        task.GetAwaiter().GetResult();
    }

    internal static void Pump()
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
    internal sealed class Stage : Form
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

/// <summary>
/// What a self-test step works with: the running app and its main window, the sample files, the archive the compression
/// job wrote, and the means to run steps, take screenshots, host windows and wait for work.
/// </summary>
internal sealed class SelfTestContext
{
    private readonly SelfTest.Stage _stage;
    private readonly string _outDir;
    private readonly List<string> _log, _problems;
    private readonly List<SelfTest.Shot> _shots;
    private MainForm? _main;
    private Size? _mainMinimum;

    internal SelfTestContext(TrayApp app, SelfTest.Stage stage, bool dark, string tempDir, string outDir,
        List<string> log, List<SelfTest.Shot> shots, List<string> problems)
    {
        App = app;
        _stage = stage;
        Dark = dark;
        TempDir = tempDir;
        _outDir = outDir;
        _log = log;
        _shots = shots;
        _problems = problems;
    }

    public TrayApp App { get; }

    /// <summary>The main window, hosted in the stage at 1040 × 760. Throws when it could not be created (the step that
    /// asks for it then fails).</summary>
    public MainForm Main
    {
        get => _main ?? throw new InvalidOperationException("the main window was not created");
        internal set => _main = value;
    }

    public bool Dark { get; }
    /// <summary>"light" or "dark", as the manifest names it.</summary>
    public string Theme => Dark ? "dark" : "light";
    /// <summary>The self-test's own temporary folder, deleted at the end.</summary>
    public string TempDir { get; }
    /// <summary>The generated sample folder ("Osmium sample" in the temporary folder).</summary>
    public string SampleFolder { get; internal set; } = "";
    /// <summary>The files in <see cref="SampleFolder"/>, sorted by name.</summary>
    public IReadOnlyList<string> SampleFiles { get; internal set; } = [];
    /// <summary>The archive the compression job (profile max) wrote; null when the job did not complete.</summary>
    public string? JobArchive { get; internal set; }
    public Guid? JobId { get; internal set; }
    /// <summary>Steps that failed so far.</summary>
    public int Failures { get; private set; }

    /// <summary>Runs one step: "ok" in the log, or "FAIL" with what went wrong (an exception, a message box, a UI
    /// exception or an error in the app log). True when it passed.</summary>
    public bool Step(string name, Action action)
    {
        _problems.Clear();
        int logged = AppLog.Snapshot().Count;
        var clock = Stopwatch.StartNew();
        try { action(); }
        catch (Exception e) { _problems.Add(e.ToString()); }
        _problems.AddRange(AppLog.Snapshot().Skip(logged).Where(e => e.Level == "error").Select(e => "app log: " + e.Message));
        if (_problems.Count == 0)
        {
            _log.Add($"ok    {name} ({clock.Elapsed.TotalSeconds:F1} s)");
            return true;
        }
        Failures++;
        _log.Add($"FAIL  {name}: " + string.Join(Environment.NewLine + "      ", _problems));
        return false;
    }

    /// <summary>A step that cannot run here, and why (not a failure).</summary>
    public void Skip(string name, string why) => _log.Add($"skip  {name}: {why}");

    /// <summary>A remark in the log (never a failure).</summary>
    public void Note(string text) => _log.Add($"note  {text}");

    /// <summary>A detail of the step being run, indented under it in the log.</summary>
    internal void Detail(string text) => _log.Add("      " + text);

    /// <summary>Renders <paramref name="c"/> to &lt;id&gt;.png (&lt;id&gt;-dark.png in the dark run) and lists it in the manifest.</summary>
    public void Shoot(Control c, string id, string caption)
    {
        Pump();
        using var bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height), PixelFormat.Format24bppRgb);
        c.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
        string file = id + (Dark ? "-dark" : "") + ".png";
        bmp.Save(Path.Combine(_outDir, file), ImageFormat.Png);
        _shots.Add(new SelfTest.Shot(file, bmp.Width, bmp.Height, Theme, caption));
    }

    /// <summary>Shows <paramref name="form"/> inside the stage with the given client size (null: it sizes itself).</summary>
    public void Host(Form form, Size? clientSize) => _stage.Host(form, clientSize);

    /// <summary>Hosts the main window again at <paramref name="clientSize"/>, without its minimum size, to see the
    /// pages at a smaller size. <see cref="RestoreMain"/> undoes it.</summary>
    public void HostMain(Size clientSize)
    {
        var main = Main;
        _mainMinimum ??= main.MinimumSize;
        main.MinimumSize = Size.Empty;
        _stage.Host(main, clientSize);
    }

    /// <summary>The main window back at 1040 × 760, with its minimum size, on the Compress page.</summary>
    public void RestoreMain()
    {
        var main = Main;
        _stage.Host(main, SelfTest.MainClient);
        if (_mainMinimum is { } minimum)
        {
            main.MinimumSize = minimum;
            _mainMinimum = null;
        }
        main.Navigate("Compress");
        Pump();
    }

    /// <summary>Processes window messages until <paramref name="t"/> completes, then throws what it threw.</summary>
    public void Await(Task t, TimeSpan timeout, string what) => SelfTest.Await(t, timeout, what);

    /// <summary>Processes window messages (including await continuations) until <paramref name="done"/>.</summary>
    public void WaitFor(Func<bool> done, TimeSpan timeout, string what) => SelfTest.WaitFor(done, timeout, what);

    /// <summary>Lets the window messages that are waiting run (layout, painting, continuations).</summary>
    public void Pump() => SelfTest.Pump();
}
