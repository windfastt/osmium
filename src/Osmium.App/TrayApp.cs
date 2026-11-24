using System.Diagnostics;
using Osmium.Core.Container;
using Osmium.Core.Distributed;
using Osmium.Core.Planning;
using Osmium.Desktop.Ipc;
using Osmium.Desktop.Jobs;
using Osmium.Desktop.Settings;
using Osmium.Desktop.Shell;
using Osmium.Desktop.Tray;
using Osmium.Desktop.Watch;

namespace Osmium.App;

/// <summary>
/// The notification-area application. Owns the NotifyIcon, the job manager, the IPC server, watch folders and
/// the optional local worker. The main window is created lazily and only hidden when "closed".
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    public static TrayApp? Current { get; private set; }

    private readonly SettingsStore _store;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly TrayIcons _icons;
    private readonly Icon _appIcon;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _coalesce = new() { Interval = 100 };
    private readonly SynchronizationContext _ui;
    private readonly IpcServer _ipc;
    private readonly InvocationCoalescer _coalescer;
    private readonly WatchFolderEngine _watch = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<JobSnapshot> _recentFinished = [];
    private CancellationTokenSource? _workerCts;
    private WorkerServer? _worker;
    private MainForm? _main;
    private string _lastTooltip = "";
    private Icon? _lastIcon;
    private bool _pausedForBattery, _exiting;

    public OsmiumSettings Settings { get; private set; }
    public JobManager Jobs { get; }
    public WorkerServer? LocalWorker => _worker;
    public RuleSet GlobalRules { get; private set; } = new();

    private readonly Font _boldMenuFont;
    private string? _lastTickError;

    public TrayApp(SettingsStore store, OsmiumSettings settings, AppCommand startup, string pipeName, string? historyFile = null)
    {
        Current = this;
        _store = store;
        Settings = settings;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _appIcon = LoadAppIcon();
        _icons = new TrayIcons(_appIcon);
        LoadRules();

        Jobs = new JobManager(() => Settings, () => GlobalRules, historyFile ?? AppPaths.HistoryFile);
        Jobs.JobFinished += j => _ui.Post(_ => OnJobFinished(j), null);
        Jobs.BusyChanged += busy => _ui.Post(_ => OnBusyChanged(busy), null);
        Jobs.PasswordPrompt = archive =>
        {
            string? pw = null;
            _ui.Send(_ => pw = AskPassword(archive), null);
            return pw;
        };

        _boldMenuFont = new Font(_menu.Font, FontStyle.Bold);
        // An empty ContextMenuStrip cancels its own opening unless the handler un-cancels it.
        _menu.Opening += (_, e) => { Guard("menu", BuildMenu); e.Cancel = false; };
        BuildMenu();
        _icon = new NotifyIcon { Icon = _appIcon, Text = "Osmium", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
        _icon.BalloonTipClicked += (_, _) => ShowMain("Jobs");

        _coalescer = new InvocationCoalescer(TimeSpan.FromMilliseconds(Settings.Integration.CoalesceMs), TimeSpan.FromSeconds(4));
        _coalesce.Tick += (_, _) => Guard("shell", () => { foreach (var b in _coalescer.Due()) HandleBatch(b); });
        _coalesce.Start();
        _refresh.Tick += (_, _) => Guard("refresh", Refresh);
        _refresh.Start();

        _ipc = new IpcServer(pipeName, m => { _ui.Post(_ => HandleMessage(m), null); return Task.CompletedTask; });
        _ipc.Start();

        ApplyRuntimeSettings();
        Application.ApplicationExit += (_, _) => HideIcon();
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => HideIcon();

        if (startup.Kind == "invoke") HandleMessage(new IpcMessage { Verb = startup.Verb, Profile = startup.Profile, Args = startup.Paths ?? [] });
        else if (startup.Kind != "tray") ShowMain();
        AppLog.Info($"Osmium started ({(AppPaths.Portable ? "portable" : "installed")}), engine fingerprint {Osmium.Desktop.AboutInfo.EngineFingerprint()}");
    }

    private static Icon LoadAppIcon()
    {
        using var s = typeof(TrayApp).Assembly.GetManifestResourceStream("Osmium.App.osmium.ico");
        return s is null ? SystemIcons.Application : new Icon(s);
    }

    public Icon AppIcon => _appIcon;

    public void LoadRules()
    {
        try { GlobalRules = File.Exists(AppPaths.RulesFile) ? RuleSet.Load(AppPaths.RulesFile, RuleSet.LayerGlobal) : new RuleSet(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            AppLog.Error("rules.json could not be read: " + e.Message);
            GlobalRules = new RuleSet();
        }
    }

    // ------------------------------------------------------------------ passwords

    private readonly Dictionary<string, string> _sessionPasswords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Asks for an archive password; with "Remember passwords until Osmium exits" the answer is kept in memory.</summary>
    public string? AskPassword(string archive)
    {
        string key = Path.GetFullPath(archive);
        if (Settings.Security.RememberForSession && _sessionPasswords.TryGetValue(key, out var cached)) return cached;
        var pw = PasswordDialog.Ask($"Password for {Path.GetFileName(archive)}", confirm: false);
        if (pw is not null && Settings.Security.RememberForSession) _sessionPasswords[key] = pw;
        return pw;
    }

    public void ForgetPassword(string archive) => _sessionPasswords.Remove(Path.GetFullPath(archive));

    // ------------------------------------------------------------------ settings

    /// <summary>
    /// Raised on the UI thread after <see cref="SaveSettings"/> stored and applied new settings. Pages that keep a
    /// copy of a setting (the Settings page, the Cluster page's key box) reload it unless they hold unsaved edits.
    /// </summary>
    public event Action? SettingsChanged;

    /// <summary>
    /// Stores <paramref name="s"/> as the complete new settings. Callers start from <c>Settings.Clone()</c> taken
    /// right before the change (the Settings page merges its edits with <see cref="SettingsEdits.Apply"/>), so a
    /// stale copy never overwrites changes made elsewhere.
    /// </summary>
    public void SaveSettings(OsmiumSettings s)
    {
        s.Cluster.ClusterKey = ClusterSettings.NormalizeClusterKey(s.Cluster.ClusterKey);
        var old = Settings;
        bool startupChanged = s.General.StartWithWindows != old.General.StartWithWindows;
        bool integrationChanged = s.Integration.ContextMenu != old.Integration.ContextMenu
            || s.Integration.FileAssociation != old.Integration.FileAssociation
            || !s.Integration.QuickProfiles.SequenceEqual(old.Integration.QuickProfiles);
        static string WorkerConfig(OsmiumSettings x) => $"{x.Cluster.ClusterKey}|{x.Cluster.WorkerPort}|{x.Cluster.WorkerSlots}|{x.Cluster.WorkerMemoryMiB}";
        bool workerChanged = WorkerConfig(s) != WorkerConfig(old);
        _store.Save(s); // first: if the file cannot be written, nothing has changed
        Settings = s;
        try
        {
            if (!s.Security.RememberForSession) _sessionPasswords.Clear();
            if (integrationChanged) ApplyIntegration(s);
            else if (startupChanged) Registration.SetStartup(s.General.StartWithWindows);
            if (workerChanged && _worker is not null) StopWorker(); // restarted below with the new configuration
            ApplyRuntimeSettings();
        }
        finally { SettingsChanged?.Invoke(); }
    }

    /// <summary>Re-registers the per-user Explorer integration from settings (also used by "Repair").</summary>
    public bool ApplyIntegration(OsmiumSettings s)
    {
        var feats = ShellFeatures.None;
        if (s.Integration.ContextMenu) feats |= ShellFeatures.ContextMenu;
        if (s.Integration.FileAssociation) feats |= ShellFeatures.Association;
        if (s.General.StartWithWindows) feats |= ShellFeatures.Startup; // Remove() also deletes the Run value
        Registration.Remove("user");
        int rc = feats == ShellFeatures.None ? 0 : Registration.Apply("user", feats, s.Integration.QuickProfiles);
        AppLog.Info($"Explorer integration updated ({feats})");
        return rc == 0;
    }

    private void ApplyRuntimeSettings()
    {
        // lend this PC as a worker
        bool want = Settings.Cluster.LendThisPc && !string.IsNullOrWhiteSpace(Settings.Cluster.ClusterKey);
        if (want && _worker is null) StartWorker();
        else if (!want && _worker is not null) StopWorker();
        if (!want) ClearWorkerProblem();
        SetupWatchFolders();
    }

    /// <summary>Why "Lend this PC" is on but no worker runs (invalid key, port in use…), or null.</summary>
    public string? WorkerError { get; private set; }
    private string? _workerWarning, _workerErrorNotified;

    /// <summary>Raised on the UI thread when the local worker stopped on its own (settings changes raise
    /// <see cref="SettingsChanged"/> instead).</summary>
    public event Action? WorkerChanged;

    private void StartWorker()
    {
        ClusterKey key;
        try { key = ClusterKey.Parse(Settings.Cluster.ClusterKey); }
        catch (FormatException e)
        {
            _worker = null;
            ReportWorkerProblem("This PC is not lending itself: the saved cluster key is invalid. " + e.Message, "Not lending this PC: invalid cluster key");
            return;
        }
        ClearWorkerProblem();
        _workerCts = new CancellationTokenSource();
        _worker = new WorkerServer(new WorkerOptions
        {
            Key = key,
            Port = Settings.Cluster.WorkerPort,
            Slots = Settings.Cluster.WorkerSlots > 0 ? Settings.Cluster.WorkerSlots : Environment.ProcessorCount,
            Memory = Settings.Cluster.WorkerMemoryMiB > 0 ? (long)Settings.Cluster.WorkerMemoryMiB << 20 : MemoryBudgets.Default() / 2,
            Log = m => AppLog.Info("worker: " + m),
        });
        var w = _worker;
        var token = _workerCts.Token;
        _workerTask = Task.Run(async () =>
        {
            try { await w.RunAsync(token); }
            catch (Exception e)
            {
                AppLog.Error("worker stopped: " + e.Message);
                if (!token.IsCancellationRequested) _ui.Post(_ => OnWorkerFailed(w, e.Message), null);
            }
        });
    }

    /// <summary>The worker ended on its own (e.g. the port is in use): show that instead of claiming it runs.</summary>
    private void OnWorkerFailed(WorkerServer w, string message)
    {
        if (!ReferenceEquals(_worker, w)) return;
        _worker = null;
        _workerTask = null;
        _workerCts = null;
        ReportWorkerProblem($"This PC is not lending itself: the worker stopped ({message}).", "Not lending this PC: the worker stopped");
        WorkerChanged?.Invoke();
    }

    /// <summary>Shown on the Cluster page and in the tray tooltip; announced once per distinct problem.</summary>
    private void ReportWorkerProblem(string message, string tooltip)
    {
        WorkerError = message;
        _workerWarning = tooltip;
        if (message == _workerErrorNotified) return; // every settings change retries; report a problem once
        _workerErrorNotified = message;
        AppLog.Error(message);
        if (Settings.General.Notifications != NotificationMode.None)
            _icon.ShowBalloonTip(8000, "Osmium is not lending this PC", TrayText.Truncate(message, TrayText.BalloonBodyMax), ToolTipIcon.Warning);
    }

    private void ClearWorkerProblem()
    {
        WorkerError = null;
        _workerWarning = null;
        _workerErrorNotified = null;
    }

    private Task? _workerTask;

    private void StopWorker()
    {
        _workerCts?.Cancel();
        // wait for the listener to close so a restart can bind the same port again
        try { _workerTask?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _worker = null;
        _workerTask = null;
    }

    private void SetupWatchFolders()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        if (!Settings.Watch.Enabled) return;
        foreach (var cfg in Settings.Watch.Folders.Where(f => f.Enabled && Directory.Exists(f.Path)))
        {
            var fsw = new FileSystemWatcher(cfg.Path)
            {
                IncludeSubdirectories = cfg.IncludeSubfolders,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            void Touch(string path)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && (fi.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Offline)) == 0)
                        _watch.Touch(cfg, path, fi.Length, fi.LastWriteTimeUtc);
                }
                catch (IOException) { }
            }
            fsw.Created += (_, e) => Touch(e.FullPath);
            fsw.Changed += (_, e) => Touch(e.FullPath);
            fsw.Renamed += (_, e) => Touch(e.FullPath);
            fsw.Error += (_, e) => AppLog.Warn($"watch folder {cfg.Path}: {e.GetException().Message}");
            fsw.EnableRaisingEvents = true;
            _watchers.Add(fsw);
        }
    }

    // ------------------------------------------------------------------ IPC / shell verbs

    private void HandleMessage(IpcMessage m)
    {
        switch (m.Type)
        {
            case "show": ShowMain(m.Page); break;
            case "quit": ExitApp(confirm: false); break;
            case "invoke": _coalescer.Add(m); break;
        }
    }

    private void HandleBatch(InvocationBatch b)
    {
        AppLog.Info($"shell: {b.Verb} ({b.Paths.Count} item(s))");
        switch (b.Verb)
        {
            case "compress-dialog" or "sendto":
                ShowMain("Compress");
                _main?.CompressPage.AddPaths(b.Paths);
                break;
            case "compress-quick":
            {
                string output = InvocationCoalescer.ArchiveNameFor(b.Paths);
                if (Settings.General.Output == OutputLocation.Folder && Directory.Exists(Settings.General.OutputFolder))
                    output = InvocationCoalescer.Unique(Path.Combine(Settings.General.OutputFolder, Path.GetFileName(output)));
                else if (Settings.General.Output == OutputLocation.Ask)
                {
                    using var d = new SaveFileDialog { Filter = "Osmium archive (*.osa)|*.osa", FileName = Path.GetFileName(output), InitialDirectory = Path.GetDirectoryName(output) };
                    if (d.ShowDialog() != DialogResult.OK) break;
                    output = d.FileName;
                }
                Jobs.Enqueue(new JobRequest { Kind = JobKind.Compress, Inputs = [.. b.Paths], Output = output, Profile = b.Profile, Origin = "shell" });
                break;
            }
            case "extract-here":
                foreach (var a in b.Paths) Jobs.Enqueue(new JobRequest { Kind = JobKind.Extract, Inputs = [a], Output = Path.GetDirectoryName(a)! });
                break;
            case "extract-to":
                foreach (var a in b.Paths)
                    Jobs.Enqueue(new JobRequest { Kind = JobKind.Extract, Inputs = [a], Output = Path.Combine(Path.GetDirectoryName(a)!, Path.GetFileNameWithoutExtension(a)) });
                break;
            case "test":
                foreach (var a in b.Paths) Jobs.Enqueue(new JobRequest { Kind = JobKind.Test, Inputs = [a] });
                break;
            case "open":
                ShowMain("Archives");
                if (b.Paths.Count > 0) _main?.ArchivesPage.OpenArchive(b.Paths[0]);
                break;
        }
    }

    // ------------------------------------------------------------------ UI

    public void ShowMain(string? page = null)
    {
        if (_main is null || _main.IsDisposed)
        {
            _main = new MainForm(this);
        }
        if (!_main.Visible) _main.Show();
        if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
        _main.Activate();
        if (page is not null) _main.Navigate(page);
    }

    public MainForm? Main => _main;

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var jobs = Jobs.Snapshot();
        // Job names are file names: '&' must be doubled in menu text (it would underline the next letter).
        var header = new ToolStripMenuItem(TrayText.MenuText(TrayText.Tooltip(jobs))) { Enabled = false };
        _menu.Items.Add(header);
        _menu.Items.Add(new ToolStripSeparator());
        var open = new ToolStripMenuItem("Open Osmium", null, (_, _) => ShowMain()) { Font = _boldMenuFont };
        _menu.Items.Add(open);
        _menu.Items.Add("Compress files…", null, (_, _) => { ShowMain("Compress"); _main?.CompressPage.BrowseFiles(); });
        _menu.Items.Add("Compress folder…", null, (_, _) => { ShowMain("Compress"); _main?.CompressPage.BrowseFolder(); });
        _menu.Items.Add("Extract archive…", null, (_, _) => { ShowMain("Archives"); _main?.ArchivesPage.Browse(); });
        _menu.Items.Add(new ToolStripSeparator());

        var profiles = new ToolStripMenuItem("Default profile");
        foreach (ProfileName p in Enum.GetValues<ProfileName>())
        {
            var item = new ToolStripMenuItem(p.ToString(), null, (_, _) =>
            {
                var s = Settings.Clone();
                s.Compression.Profile = p;
                SaveSettings(s);
            }) { Checked = Settings.Compression.Profile == p };
            profiles.DropDownItems.Add(item);
        }
        _menu.Items.Add(profiles);

        var recent = new ToolStripMenuItem("Recent jobs");
        foreach (var j in jobs.Where(j => !j.IsActive).TakeLast(6).Reverse())
        {
            var text = j.State == JobState.Completed && j.Kind == JobKind.Compress
                ? $"{j.Name} — {TrayText.Bytes(j.BytesIn)} → {TrayText.Bytes(j.BytesOut)}"
                : $"{j.Name} — {j.State}";
            var it = new ToolStripMenuItem(TrayText.MenuText(TrayText.Truncate(text, 70)));
            if (j.Output is { Length: > 0 } output) it.Click += (_, _) => Shell.Reveal(output);
            recent.DropDownItems.Add(it);
        }
        if (recent.DropDownItems.Count == 0) recent.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
        recent.DropDownItems.Add(new ToolStripSeparator());
        recent.DropDownItems.Add("Show all jobs", null, (_, _) => ShowMain("Jobs"));
        _menu.Items.Add(recent);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(Jobs.PausedAll ? "Resume all jobs" : "Pause all jobs", null, (_, _) => { if (Jobs.PausedAll) Jobs.ResumeAll(); else Jobs.PauseAll(); });
        var watch = new ToolStripMenuItem("Watch folders", null, (_, _) =>
        {
            var s = Settings.Clone();
            s.Watch.Enabled = !s.Watch.Enabled;
            SaveSettings(s);
        }) { Checked = Settings.Watch.Enabled, Enabled = Settings.Watch.Folders.Count > 0 };
        _menu.Items.Add(watch);
        // The check shows the setting; whether a worker really runs is in the text (and on the Cluster page).
        bool lending = Settings.Cluster.LendThisPc;
        var lend = new ToolStripMenuItem(lending && WorkerError is not null ? "Lend this PC to the cluster (not running)" : "Lend this PC to the cluster", null, (_, _) =>
        {
            var s = Settings.Clone();
            s.Cluster.LendThisPc = !s.Cluster.LendThisPc;
            SaveSettings(s);
        }) { Checked = lending, Enabled = lending || !string.IsNullOrWhiteSpace(Settings.Cluster.ClusterKey) };
        _menu.Items.Add(lend);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Settings…", null, (_, _) => ShowMain("Settings"));
        _menu.Items.Add("About Osmium…", null, (_, _) => ShowAbout());
        _menu.Items.Add("Exit", null, (_, _) => ExitApp(confirm: true));
    }

    private AboutDialog? _about;

    /// <summary>Version, engine fingerprint and licence (tray menu, Settings page). Only one at a time.</summary>
    public void ShowAbout()
    {
        if (_about is { IsDisposed: false }) { _about.Activate(); return; }
        using var d = new AboutDialog(_appIcon);
        _about = d;
        try { d.ShowDialog(_main is { Visible: true } owner ? owner : null); }
        finally { _about = null; }
    }

    /// <summary>Timer/menu handlers must never throw: an exception would reach Application.ThreadException and
    /// show a dialog on every tick. Errors are logged once per distinct message instead.</summary>
    private void Guard(string what, Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            string key = what + ":" + e.Message;
            if (key != _lastTickError) { _lastTickError = key; AppLog.Error($"{what}: {e}"); }
        }
    }

    private void Refresh()
    {
        var jobs = Jobs.Snapshot();
        var tip = TrayText.Tooltip(jobs, _workerWarning);
        if (tip != _lastTooltip) { _icon.Text = tip; _lastTooltip = tip; }
        var active = jobs.FirstOrDefault(j => j.State is JobState.Running or JobState.Paused);
        var icon = active is null ? _icons.Idle : _icons.Progress(active.Fraction, active.State == JobState.Paused, false);
        if (!ReferenceEquals(icon, _lastIcon)) { _icon.Icon = icon; _lastIcon = icon; }

        // pause on battery
        if (Settings.Performance.PauseOnBattery)
        {
            bool onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
            if (onBattery && !_pausedForBattery && Jobs.AnyActive && !Jobs.PausedAll) { Jobs.PauseAll(); _pausedForBattery = true; AppLog.Info("paused: running on battery"); }
            else if (!onBattery && _pausedForBattery) { Jobs.ResumeAll(); _pausedForBattery = false; AppLog.Info("resumed: back on AC power"); }
        }

        // settled watch-folder files become jobs
        foreach (var (cfg, path) in _watch.Tick(p =>
        {
            var fi = new FileInfo(p);
            return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : null;
        }))
        {
            var outDir = Path.Combine(cfg.Path, cfg.OutputSubfolder);
            Directory.CreateDirectory(outDir);
            Jobs.Enqueue(new JobRequest
            {
                Kind = JobKind.Compress, Inputs = [path], Profile = cfg.Profile.ToString().ToLowerInvariant(), Origin = "watch",
                Output = InvocationCoalescer.Unique(Path.Combine(outDir, Path.GetFileName(path) + ".osa")),
            });
        }
    }

    private void OnBusyChanged(bool busy)
    {
        if (Settings.Performance.KeepAwake)
            Native.SetThreadExecutionState(busy ? Native.EsContinuous | Native.EsSystemRequired : Native.EsContinuous);
        try
        {
            Process.GetCurrentProcess().PriorityClass = busy
                ? Settings.Performance.Priority switch { PriorityLevel.Idle => ProcessPriorityClass.Idle, PriorityLevel.BelowNormal => ProcessPriorityClass.BelowNormal, _ => ProcessPriorityClass.Normal }
                : ProcessPriorityClass.Normal;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void OnJobFinished(JobSnapshot j)
    {
        AppLog.Info($"job {j.Name}: {j.State}{(j.Error is null ? "" : " — " + j.Error)}");
        if (j.State == JobState.Failed && j.Error == new WrongPasswordException().Message) _sessionPasswords.Clear();
        if (j.State == JobState.Completed && j.Kind == JobKind.Compress && Settings.General.OpenFolderWhenDone && j.Output is not null) Shell.Reveal(j.Output);
        var mode = Settings.General.Notifications;
        if (mode == NotificationMode.None || (mode == NotificationMode.FailuresOnly && j.State != JobState.Failed)) return;
        if (_main is { Visible: true, ContainsFocus: true } && _main.CurrentPage == "Jobs") return;
        _recentFinished.Add(j);
        // aggregate completions that arrive close together
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            if (_recentFinished.Count == 0) return;
            if (_recentFinished.Count == 1)
            {
                var (title, body, error) = TrayText.Completion(_recentFinished[0]);
                _icon.ShowBalloonTip(5000, title, body, error ? ToolTipIcon.Error : ToolTipIcon.Info);
            }
            else
            {
                var (title, body) = TrayText.Aggregate(_recentFinished);
                _icon.ShowBalloonTip(5000, title, body, ToolTipIcon.Info);
            }
            _recentFinished.Clear();
        };
        t.Start();
    }

    /// <param name="confirm">Ask first when jobs are running (if "Confirm exit while jobs run" is on) or a page of
    /// the main window holds unsaved edits.</param>
    public void ExitApp(bool confirm)
    {
        if (_exiting) return;
        if (confirm && TrayText.ExitQuestion(Settings.General.ConfirmExit && Jobs.AnyActive, _main?.PagesWithUnsavedChanges() ?? []) is { } question)
        {
            var r = MessageBox.Show(question, "Osmium", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
        }
        _exiting = true;
        _main?.AllowClose();
        _main?.Close();
        ExitThread();
    }

    public void HideIcon()
    {
        try { _icon.Visible = false; } catch (ObjectDisposedException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            HideIcon();
            _refresh.Dispose();
            _coalesce.Dispose();
            StopWorker();
            foreach (var w in _watchers) w.Dispose();
            Jobs.Dispose();
            _ipc.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            _icon.Dispose();
            _icons.Dispose();
            _menu.Dispose();
            _boldMenuFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
