using Osmium.Core.Container;
using Osmium.Core.Planning;
using Osmium.Desktop.Jobs;
using Osmium.Desktop.Settings;
using Osmium.Desktop.Tray;

namespace Osmium.Desktop.Tests;

/// <summary>
/// "After a verified compression, move the originals to the Recycle Bin" through real compress jobs: what moves, what
/// stays, what the job log, the summary and the notification say. A fake recycler moves items into a folder of the
/// test's own instead of the Recycle Bin.
/// </summary>
public sealed class RecycleJobTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "osm-recycle-jobs-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeRecycler _bin;
    private readonly string _photos, _notes;

    public RecycleJobTests()
    {
        Directory.CreateDirectory(_root);
        _bin = new FakeRecycler(Path.Combine(_root, "bin"));
        _photos = Folder("photos");
        Write("photos/a.txt", string.Concat(Enumerable.Repeat("a line of text that compresses well\n", 300)));
        Write("photos/sub/b.txt", "a second file");
        Folder("photos/empty");
        _notes = Write("notes.txt", "a single file");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string Write(string rel, string text = "some text")
    {
        // Full path in the spelling the product uses: Path.Combine keeps a '/' in rel, GetFullPath turns it into '\' on Windows.
        var path = Path.GetFullPath(Path.Combine(_root, rel));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Folder(string rel) => Directory.CreateDirectory(Path.Combine(_root, rel)).FullName;

    private static OsmiumSettings Settings(bool recycle = true)
    {
        var s = new OsmiumSettings();
        s.Compression.Profile = ProfileName.Store; // the rules do not depend on the codec, and the jobs stay fast
        s.Compression.RecycleOriginals = recycle;
        s.Performance.Threads = 2;
        return s;
    }

    private JobManager Manager(OsmiumSettings s) => new(() => s, () => new RuleSet(), recycler: _bin);

    private static Task<JobSnapshot> NextFinished(JobManager jm)
    {
        var done = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        jm.JobFinished += j => done.TrySetResult(j);
        return done.Task;
    }

    private async Task<JobSnapshot> Compress(JobManager jm, IEnumerable<string> inputs, string? output = null, List<Rule>? patches = null, string? password = null)
    {
        var finished = NextFinished(jm);
        jm.Enqueue(new JobRequest
        {
            Kind = JobKind.Compress, Inputs = [.. inputs], Output = output ?? Path.Combine(_root, "out.osa"), Patches = patches ?? [], Password = password,
        });
        return await finished.WaitAsync(Timeout, Ct);
    }

    [Fact]
    public async Task OnlyWhatTheArchiveItselfListsCountsAsArchived()
    {
        using var jm = Manager(Settings());
        var real = jm.ReadIndex;
        // The written archive read back without sub/b.txt, and with notes.txt at another size.
        jm.ReadIndex = (path, password) => [.. real(path, password)
            .Where(e => e.Path != "photos/sub/b.txt")
            .Select(e => e.Path != "notes.txt" ? e : new ArchiveEntry { Path = e.Path, Kind = e.Kind, ModifiedUtc = e.ModifiedUtc, Size = e.Size + 1 })];
        var job = await Compress(jm, [_photos, _notes]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(0, 2), job.Recycle);
        Assert.Empty(_bin.Calls);
        var log = jm.GetLog(job.Id);
        Assert.Contains($"kept {_photos}: it holds a file that is not in the archive: {Path.Combine("sub", "b.txt")}", log);
        Assert.Contains($"kept {_notes}: it is not in the archive", log);
    }

    [Fact]
    public async Task AnArchiveThatCannotBeReadBackKeepsEverything()
    {
        using var jm = Manager(Settings());
        jm.ReadIndex = (_, _) => throw new WrongPasswordException();
        var job = await Compress(jm, [_photos, _notes]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(0, 2), job.Recycle);
        Assert.Empty(_bin.Calls);
        var log = jm.GetLog(job.Id);
        Assert.Contains("warning: the originals could not be checked, so none were moved: Wrong password.", log);
        Assert.Contains($"kept {_photos}: it could not be checked", log);
    }

    [Fact]
    public async Task AnEncryptedJobReadsItsArchiveWithItsPassword()
    {
        var s = Settings();
        s.Security.KdfIterations = 100_000;
        using var jm = Manager(s);
        var job = await Compress(jm, [_photos, _notes], password: "correct horse battery staple");
        Assert.Equal(new RecycleSummary(2, 0), job.Recycle);
        Assert.Throws<PasswordRequiredException>(() => OsaReader.Open(job.Output!).Dispose());
    }

    private static void AssertSameTree(string expected, string actual)
    {
        static List<string> Entries(string root) =>
            [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p)).Order(StringComparer.Ordinal)];
        Assert.Equal(Entries(expected), Entries(actual));
        foreach (var f in Directory.EnumerateFiles(expected, "*", SearchOption.AllDirectories))
            Assert.Equal(File.ReadAllBytes(f), File.ReadAllBytes(Path.Combine(actual, Path.GetRelativePath(expected, f))));
    }

    [Fact]
    public async Task AVerifiedJobMovesItsOriginalsInOneOperation()
    {
        using var jm = Manager(Settings());
        var job = await Compress(jm, [_photos, _notes]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(2, 0), job.Recycle);
        Assert.False(Directory.Exists(_photos));
        Assert.False(File.Exists(_notes));
        Assert.Equal([_photos, _notes], Assert.Single(_bin.Calls));
        var log = jm.GetLog(job.Id);
        Assert.Contains("moved to the Recycle Bin: " + _photos, log);
        Assert.Contains("moved to the Recycle Bin: " + _notes, log);
        Assert.Contains("originals: 2 moved to the Recycle Bin, 0 kept", log);

        // The archive gives back exactly what went to the Recycle Bin.
        var finished = NextFinished(jm);
        jm.Enqueue(new JobRequest { Kind = JobKind.Extract, Inputs = [job.Output!], Output = Path.Combine(_root, "out") });
        Assert.Equal(JobState.Completed, (await finished.WaitAsync(Timeout, Ct)).State);
        AssertSameTree(_bin.Moved[_photos], Path.Combine(_root, "out", "photos"));
        Assert.Equal(File.ReadAllBytes(_bin.Moved[_notes]), File.ReadAllBytes(Path.Combine(_root, "out", "notes.txt")));
    }

    [Fact]
    public async Task NothingMovesWhenTheSettingIsOff()
    {
        using var jm = Manager(Settings(recycle: false));
        var job = await Compress(jm, [_photos, _notes]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Null(job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.True(File.Exists(_notes));
        Assert.Empty(_bin.Calls);
        Assert.DoesNotContain(jm.GetLog(job.Id), l => l.Contains("Recycle Bin") || l.StartsWith("kept ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingMovesWithoutBlockVerification()
    {
        var s = Settings();
        s.Compression.VerifyBlocks = false;
        using var jm = Manager(s);
        var job = await Compress(jm, [_photos, _notes]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(0, 2), job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.True(File.Exists(_notes));
        Assert.Empty(_bin.Calls);
        Assert.Contains($"kept {_photos}: block verification was off for this job (Settings → Compression → Verify every block)", jm.GetLog(job.Id));
    }

    [Fact]
    public async Task NothingMovesAfterBuilderWarnings()
    {
        string missing = Path.Combine(_root, "missing.txt"); // the scan reports it, and the builder passes the warning on
        using var jm = Manager(Settings());
        var job = await Compress(jm, [_photos, missing]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(0, 2), job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.Empty(_bin.Calls);
        var log = jm.GetLog(job.Id);
        Assert.Contains(log, l => l.StartsWith("warning: ", StringComparison.Ordinal) && l.Contains("not found"));
        Assert.Contains($"kept {_photos}: the archive builder reported 1 warning (unreadable, skipped or changed files)", log);
    }

    [Fact]
    public async Task FoldersHoldingFilesTheArchiveLeftOutStay()
    {
        string thumbs = Folder("thumbs");
        Write("thumbs/a.txt");
        Write("thumbs/Thumbs.db", "a thumbnail cache"); // a default exclude pattern
        string logs = Folder("logs");
        Write("logs/a.txt");
        Write("logs/debug.log", "a trace"); // left out by a rule of the job
        var noLogs = new Rule { Id = "no-logs", Match = new RuleMatch { Extensions = ["log"] }, Actions = new RuleActions { Exclude = true } };
        using var jm = Manager(Settings());
        var job = await Compress(jm, [thumbs, logs, _photos], patches: [noLogs]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(1, 2), job.Recycle);
        Assert.True(Directory.Exists(thumbs));
        Assert.True(Directory.Exists(logs));
        Assert.False(Directory.Exists(_photos));
        Assert.Equal([_photos], Assert.Single(_bin.Calls));
        var log = jm.GetLog(job.Id);
        Assert.Contains($"kept {thumbs}: it holds a file that is not in the archive: Thumbs.db", log);
        Assert.Contains($"kept {logs}: it holds a file that is not in the archive: debug.log", log);
    }

    [Fact]
    public async Task AFolderHoldingANameOsmiumCannotExtractStays()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Windows does not allow this name");
        Write("photos/meeting: notes.txt", "archived, but the extractor refuses the name");
        using var jm = Manager(Settings());
        var job = await Compress(jm, [_photos, _notes]);
        Assert.Equal(new RecycleSummary(1, 1), job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.Contains($"kept {_photos}: Osmium cannot extract meeting: notes.txt under its name (invalid character U+003A)", jm.GetLog(job.Id));
    }

    [Fact]
    public async Task AFolderHoldingTheArchiveStays()
    {
        using var jm = Manager(Settings());
        var job = await Compress(jm, [_photos], output: Path.Combine(_photos, "photos.osa"));
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(0, 1), job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.Empty(_bin.Calls);
        Assert.Contains($"kept {_photos}: the archive is inside it", jm.GetLog(job.Id));
    }

    [Fact]
    public async Task AWatchedFileMovesOnceItsArchiveIsWritten()
    {
        string report = Write("watched/report.txt", "a report");
        string output = Path.Combine(Folder("watched/_osmium"), "report.txt.osa");
        using var jm = Manager(Settings());
        var job = await Compress(jm, [report], output);
        Assert.Equal(new RecycleSummary(1, 0), job.Recycle);
        Assert.False(File.Exists(report));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task OnlyFixedDrivesMove()
    {
        _bin.Drives = p => p == _notes ? DriveType.Removable : DriveType.Fixed;
        using var jm = Manager(Settings());
        var job = await Compress(jm, [_photos, _notes]);
        Assert.Equal(new RecycleSummary(1, 1), job.Recycle);
        Assert.False(Directory.Exists(_photos));
        Assert.True(File.Exists(_notes));
        Assert.Contains($"kept {_notes}: it is on a removable drive, which has no Recycle Bin", jm.GetLog(job.Id));
    }

    [Fact]
    public async Task ACanceledJobMovesNothing()
    {
        using var started = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var s = Settings();
        using var jm = new JobManager(() => { started.Set(); proceed.Wait(); return s; }, () => new RuleSet(), recycler: _bin);
        var finished = NextFinished(jm);
        var id = jm.Enqueue(new JobRequest { Kind = JobKind.Compress, Inputs = [_photos, _notes], Output = Path.Combine(_root, "out.osa") });
        Assert.True(started.Wait(Timeout, Ct));
        jm.Cancel(id);
        proceed.Set();
        var job = await finished.WaitAsync(Timeout, Ct);
        Assert.Equal(JobState.Canceled, job.State);
        Assert.Null(job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.True(File.Exists(_notes));
        Assert.Empty(_bin.Calls);
    }

    [Fact]
    public async Task AFailedJobMovesNothing()
    {
        string output = Write("out.osa", "a file that is not to be overwritten");
        using var jm = Manager(Settings());
        var job = await Compress(jm, [_photos, _notes], output);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Null(job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.True(File.Exists(_notes));
        Assert.Empty(_bin.Calls);
    }

    [Fact]
    public async Task ACancelWhileTheOriginalsAreCheckedMovesNothing()
    {
        JobManager? manager = null;
        _bin.Drives = _ =>
        {
            // Asked while the job decides what may move, as when the app exits then.
            foreach (var active in manager!.Snapshot().Where(j => j.IsActive)) manager.Cancel(active.Id);
            return DriveType.Fixed;
        };
        using var jm = Manager(Settings());
        manager = jm;
        var job = await Compress(jm, [_photos, _notes]);
        Assert.Equal(JobState.Completed, job.State); // the archive is complete and good
        Assert.Equal(new RecycleSummary(0, 2), job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.True(File.Exists(_notes));
        Assert.Empty(_bin.Calls); // the decision stopped at once: nothing was handed over to be moved
        var log = jm.GetLog(job.Id);
        Assert.Contains($"kept {_photos}: the job was canceled", log);
        Assert.Contains($"kept {_notes}: the job was canceled", log);
        Assert.DoesNotContain(log, l => l.Contains("could not be moved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailingRecyclerKeepsTheFilesAndTheJobCompletes()
    {
        _bin.Fail = true;
        using var jm = Manager(Settings());
        var job = await Compress(jm, [_photos, _notes]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(0, 2), job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.True(File.Exists(_notes));
        var log = jm.GetLog(job.Id);
        Assert.Contains("warning: moving the originals to the Recycle Bin failed: the Recycle Bin is not available", log);
        Assert.Contains($"kept {_photos}: the Recycle Bin did not take it", log);
    }

    [Fact]
    public async Task AnItemTheRecycleBinRefusesStays()
    {
        _bin.Refuse = p => p == _photos ? "it is larger than the Recycle Bin" : null;
        using var jm = Manager(Settings());
        var job = await Compress(jm, [_photos, _notes]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(1, 1), job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.False(File.Exists(_notes));
        var log = jm.GetLog(job.Id);
        Assert.Contains($"kept {_photos}: it is larger than the Recycle Bin", log);
        Assert.Contains("warning: 1 of 2 items could not be moved to the Recycle Bin", log);
    }

    [Fact]
    public async Task WithoutARecyclerNothingMoves()
    {
        var s = Settings();
        using var jm = new JobManager(() => s, () => new RuleSet());
        var job = await Compress(jm, [_photos, _notes]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(0, 2), job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.Contains($"kept {_photos}: Osmium has no Recycle Bin to use on this system", jm.GetLog(job.Id));
    }

    [Fact]
    public async Task TheSummaryIsKeptInTheJobHistory()
    {
        string history = Path.Combine(_root, "history.json");
        var s = Settings();
        using (var jm = new JobManager(() => s, () => new RuleSet(), history, _bin))
            Assert.Equal(new RecycleSummary(2, 0), (await Compress(jm, [_photos, _notes])).Recycle);
        using var again = new JobManager(() => s, () => new RuleSet(), history, _bin);
        Assert.Equal(new RecycleSummary(2, 0), Assert.Single(again.Snapshot()).Recycle);
    }

    [Fact]
    public void TheSettingIsOffByDefaultAndAsksBeforeTurningOn()
    {
        var d = SettingReflector.Describe(new OsmiumSettings()).Single(x => x.Key == "Compression.RecycleOriginals");
        Assert.Equal("Compression", d.Category);
        Assert.Equal("After a verified compression, move the originals to the Recycle Bin", d.Label);
        Assert.False(Assert.IsType<bool>(d.Default));
        Assert.True(d.NeedsConfirmation(true));
        Assert.False(d.NeedsConfirmation(false));
        Assert.Equal("Osmium moves the files it compressed to the Recycle Bin after the archive is written and every block is verified. "
            + "Files that were not compressed, and drives without a Recycle Bin, are never touched.", d.WarningText);
    }

    [Fact]
    public void NotificationsSayHowManyOriginalsMovedAndStayed()
    {
        var j = new JobSnapshot(Guid.NewGuid(), new string('x', 500), JobKind.Compress, JobState.Completed, "Done", 1, 1000, 100, 1000,
            TimeSpan.FromSeconds(3), null, null, null, null, DateTime.Now, []) { Recycle = new RecycleSummary(2, 1) };
        var (_, body, error) = TrayText.Completion(j);
        Assert.False(error);
        Assert.True(body.Length <= TrayText.BalloonBodyMax);
        Assert.EndsWith("\nOriginals: 2 moved to the Recycle Bin, 1 kept", body); // whole, although the name is cut
        Assert.Equal("Done. Originals: 2 moved to the Recycle Bin, 1 kept", TrayText.FinishedStatus(j));
        var (_, aggregate) = TrayText.Aggregate([j, j with { Recycle = new RecycleSummary(0, 3) }, j with { Recycle = null }]);
        Assert.EndsWith("Originals: 2 moved to the Recycle Bin, 4 kept", aggregate);
        Assert.DoesNotContain("Originals", TrayText.Completion(j with { Recycle = null }).Body);
        Assert.DoesNotContain("Originals", TrayText.Aggregate([j with { Recycle = null }]).Body);
        Assert.Equal("Done", TrayText.FinishedStatus(j with { Recycle = null }));

        // Inputs no longer in place without a confirmed Recycle Bin copy are counted, and said.
        var gone = j with { Recycle = new RecycleSummary(1, 0, 1) };
        Assert.EndsWith("\nOriginals: 1 moved to the Recycle Bin, 0 kept, 1 no longer in place and not confirmed in the Recycle Bin", TrayText.Completion(gone).Body);
        Assert.EndsWith("Originals: 3 moved to the Recycle Bin, 1 kept, 1 no longer in place and not confirmed in the Recycle Bin", TrayText.Aggregate([j, gone]).Body);
    }

    [Fact]
    public async Task AnInputThatWasToStayButLeftIsReported()
    {
        string page = Write("site/page.html", "<html>a saved page</html>");
        string pageFiles = Folder("site/page_files");
        Write("site/page_files/style.css", "body {}");
        Write("site/page_files/Thumbs.db", "left out by the default exclude patterns, so this folder stays");
        _bin.AlsoTakes = p => p == page ? pageFiles : null; // as a shell would that takes connected elements along, unreported
        using var jm = Manager(Settings());
        var job = await Compress(jm, [page, pageFiles]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(1, 0, 1), job.Recycle);
        var log = jm.GetLog(job.Id);
        Assert.Contains("moved to the Recycle Bin: " + page, log);
        Assert.Contains($"warning: {pageFiles} was to stay (it holds a file that is not in the archive: Thumbs.db), but it is no longer in place", log);
        Assert.DoesNotContain(log, l => l.StartsWith($"kept {pageFiles}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnythingElseTheOperationAffectedIsReported()
    {
        string page = Write("site/page.html", "<html>a saved page</html>");
        string pageFiles = Folder("site/page_files");
        Write("site/page_files/style.css", "body {}");
        _bin.AlsoTakes = p => p == page ? pageFiles : null;
        _bin.ReportsWhatElseItTakes = true;
        using var jm = Manager(Settings());
        var job = await Compress(jm, [page]);
        Assert.Equal(new RecycleSummary(1, 0), job.Recycle);
        Assert.Contains($"warning: the Recycle Bin operation also affected {pageFiles}, which was not to move: it went along with {page}", jm.GetLog(job.Id));
    }

    [Fact]
    public async Task AnItemGoneWithoutConfirmationIsCountedAndReported()
    {
        _bin.Lies = p => p == _notes ? Lie.MovesButDoesNotConfirm : p == _photos ? Lie.ConfirmsButDoesNotMove : Lie.None;
        using var jm = Manager(Settings());
        var job = await Compress(jm, [_photos, _notes]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(0, 1, 1), job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.False(File.Exists(_notes));
        var log = jm.GetLog(job.Id);
        Assert.Contains($"kept {_photos}: it is still in place, although the Recycle Bin reported it as moved", log);
        Assert.Contains($"warning: {_notes} is no longer in place, but the Recycle Bin did not confirm that it holds it (no confirmation)", log);
        Assert.Contains("warning: 2 of 2 items could not be moved to the Recycle Bin", log);
        Assert.Contains("originals: 0 moved to the Recycle Bin, 1 kept, 1 no longer in place and not confirmed in the Recycle Bin", log);
    }

    [Fact]
    public async Task AnInputThatChangesJustBeforeItMovesStays()
    {
        string late = Path.Combine(_photos, "late.txt");
        _bin.BeforeMove = p =>
        {
            if (p == _notes) File.AppendAllText(_notes, " and a change"); // after the rules passed it, before it moves
            if (p == _photos) File.WriteAllText(late, "a file that appeared after the archive was written");
        };
        string other = Write("other.txt", "unchanged");
        using var jm = Manager(Settings());
        var job = await Compress(jm, [_photos, _notes, other]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(1, 2), job.Recycle);
        Assert.True(File.Exists(late));
        Assert.True(File.Exists(_notes));
        Assert.False(File.Exists(other));
        var log = jm.GetLog(job.Id);
        Assert.Contains($"kept {_photos}: checked again just before it moved: it holds a file that is not in the archive: late.txt", log);
        Assert.Contains($"kept {_notes}: checked again just before it moved: it changed after it was read", log);
        Assert.Contains("moved to the Recycle Bin: " + other, log);
    }

    /// <summary>Changes the first byte of a file and gives it back its last-write time: its size and time stay the same.</summary>
    private static void EditKeepingSizeAndTime(string path)
    {
        var time = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0x20;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, time);
        Assert.Equal(time, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task AnEditThatKeepsSizeAndTimeStays()
    {
        string other = Folder("other");
        Write("other/c.txt", "unchanged");
        Write("other/empty.txt", "");
        _bin.BeforeMove = p =>
        {
            // After the archive was written and the rules passed it, just before it moves.
            if (p == _notes) EditKeepingSizeAndTime(_notes);
            if (p == _photos) EditKeepingSizeAndTime(Path.Combine(_photos, "sub", "b.txt"));
        };
        using var jm = Manager(Settings());
        var job = await Compress(jm, [_photos, _notes, other]);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(new RecycleSummary(1, 2), job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.True(File.Exists(_notes));
        Assert.False(Directory.Exists(other));
        var log = jm.GetLog(job.Id);
        Assert.Contains($"kept {_photos}: checked again just before it moved: the contents of {Path.Combine("sub", "b.txt")} no longer match the archive", log);
        Assert.Contains($"kept {_notes}: checked again just before it moved: its contents no longer match the archive", log);
        Assert.Contains("moved to the Recycle Bin: " + other, log);
    }

    [Fact]
    public async Task AnEditedFileTheArchiveStoresOnceForTwoStays()
    {
        string twins = Folder("twins");
        string text = string.Concat(Enumerable.Repeat("the same contents in two files\n", 50));
        Write("twins/a.txt", text);
        string copy = Write("twins/b.txt", text); // stored as a.txt's contents: the archive is built without reading it
        var s = Settings();
        s.Compression.Dedup = DedupSetting.Files;
        _bin.BeforeMove = p =>
        {
            if (p == twins) EditKeepingSizeAndTime(copy);
        };
        using var jm = Manager(s);
        var job = await Compress(jm, [twins, _notes]);
        Assert.Equal(JobState.Completed, job.State);
        using (var archive = OsaReader.Open(job.Output!))
            Assert.Equal(archive.Entries.Single(e => e.Path == "twins/a.txt").Segments, archive.Entries.Single(e => e.Path == "twins/b.txt").Segments);
        Assert.Equal(new RecycleSummary(1, 1), job.Recycle);
        Assert.True(File.Exists(copy));
        Assert.False(File.Exists(_notes));
        Assert.Contains($"kept {twins}: checked again just before it moved: the contents of b.txt no longer match the archive", jm.GetLog(job.Id));
    }

    [Fact]
    public async Task ACancelDuringTheLastCheckMovesNothing()
    {
        JobManager? manager = null;
        _bin.BeforeMove = _ =>
        {
            foreach (var active in manager!.Snapshot().Where(j => j.IsActive)) manager.Cancel(active.Id);
        };
        using var jm = Manager(Settings());
        manager = jm;
        var job = await Compress(jm, [_photos, _notes]);
        Assert.Equal(JobState.Completed, job.State); // the archive is complete and good
        Assert.Equal(new RecycleSummary(0, 2), job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.True(File.Exists(_notes));
        Assert.Empty(_bin.Moved);
        var log = jm.GetLog(job.Id);
        // The check itself stopped for the cancel: the job handed it its cancellation.
        Assert.Contains($"kept {_photos}: checked again just before it moved: the job was canceled", log);
        Assert.Contains($"kept {_notes}: the job was canceled", log); // not reached once the job was canceled
    }

    [Fact]
    public async Task APausedJobMovesNothingUntilItIsResumed()
    {
        using var jm = Manager(Settings());
        var real = jm.ReadIndex;
        using var paused = new ManualResetEventSlim();
        var id = Guid.Empty;
        jm.ReadIndex = (path, password) =>
        {
            jm.Pause(id); // once the archive is written, before the originals are checked
            paused.Set();
            return real(path, password);
        };
        var finished = NextFinished(jm);
        id = jm.Enqueue(new JobRequest { Kind = JobKind.Compress, Inputs = [_photos, _notes], Output = Path.Combine(_root, "out.osa") });
        Assert.True(paused.Wait(Timeout, Ct));
        await Task.Delay(300, Ct); // time enough to go on, if it did
        Assert.Equal(JobState.Paused, jm.Snapshot().Single(j => j.Id == id).State);
        Assert.Empty(_bin.Calls);
        Assert.True(Directory.Exists(_photos));
        jm.Resume(id);
        var job = await finished.WaitAsync(Timeout, Ct);
        Assert.Equal(new RecycleSummary(2, 0), job.Recycle);
    }

    [Fact]
    public async Task APauseWhileTheOriginalsMoveHoldsTheNextOne()
    {
        using var paused = new ManualResetEventSlim();
        JobManager? manager = null;
        _bin.BeforeMove = p =>
        {
            if (p != _photos) return;
            foreach (var active in manager!.Snapshot().Where(j => j.IsActive)) manager.Pause(active.Id);
            paused.Set();
        };
        using var jm = Manager(Settings());
        manager = jm;
        var finished = NextFinished(jm);
        var id = jm.Enqueue(new JobRequest { Kind = JobKind.Compress, Inputs = [_photos, _notes], Output = Path.Combine(_root, "out.osa") });
        Assert.True(paused.Wait(Timeout, Ct));
        await Task.Delay(300, Ct);
        Assert.Empty(_bin.Moved); // held before its last check
        Assert.True(Directory.Exists(_photos));
        jm.Resume(id);
        var job = await finished.WaitAsync(Timeout, Ct);
        Assert.Equal(new RecycleSummary(2, 0), job.Recycle);
    }

    [Fact]
    public async Task NothingMovesWhenTheArchiveChangesBeforeTheMove()
    {
        string output = Path.Combine(_root, "out.osa");
        _bin.BeforeMove = _ => File.SetLastWriteTimeUtc(output, File.GetLastWriteTimeUtc(output).AddSeconds(5));
        using var jm = Manager(Settings());
        var job = await Compress(jm, [_photos, _notes], output);
        Assert.Equal(new RecycleSummary(0, 2), job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.Contains($"kept {_photos}: checked again just before it moved: the archive changed after it was checked", jm.GetLog(job.Id));
    }

    [Fact]
    public async Task NothingMovesWhenTheArchiveGoesBeforeTheMove()
    {
        string output = Path.Combine(_root, "out.osa");
        _bin.BeforeMove = _ => File.Delete(output);
        using var jm = Manager(Settings());
        var job = await Compress(jm, [_photos, _notes], output);
        Assert.Equal(new RecycleSummary(0, 2), job.Recycle);
        Assert.True(Directory.Exists(_photos));
        Assert.Contains($"kept {_notes}: checked again just before it moved: the archive is missing", jm.GetLog(job.Id));
    }

    private enum Lie { None, MovesButDoesNotConfirm, ConfirmsButDoesNotMove }

    /// <summary>
    /// Moves items into a folder of its own, one subfolder per item, and records every call. It can also misbehave like
    /// a broken system would, to check that the job reports what really happened.
    /// </summary>
    private sealed class FakeRecycler(string bin) : IRecycler
    {
        public Func<string, DriveType> Drives { get; set; } = _ => DriveType.Fixed;
        public Func<string, string?> Refuse { get; set; } = _ => null;
        public bool Fail { get; set; }
        /// <summary>Something it moves along with an item, without being asked to.</summary>
        public Func<string, string?> AlsoTakes { get; set; } = _ => null;
        /// <summary>Whether it reports what it took along (as an outcome for a path it was not handed).</summary>
        public bool ReportsWhatElseItTakes { get; set; }
        public Func<string, Lie> Lies { get; set; } = _ => Lie.None;
        /// <summary>Runs just before an item would move, before the last check (to change it at the last moment).</summary>
        public Action<string> BeforeMove { get; set; } = _ => { };
        public List<List<string>> Calls { get; } = [];
        public Dictionary<string, string> Moved { get; } = [];

        public DriveType DriveTypeOf(string path) => Drives(path);

        public IReadOnlyList<RecycleOutcome> Recycle(IReadOnlyList<string> paths, Func<string, string?> recheck, CancellationToken ct)
        {
            Calls.Add([.. paths]);
            if (Fail) throw new IOException("the Recycle Bin is not available");
            var outcomes = new List<RecycleOutcome>();
            var others = new List<RecycleOutcome>();
            foreach (var p in paths)
            {
                if (ct.IsCancellationRequested) { outcomes.Add(new RecycleOutcome(p, false, "the job was canceled")); continue; }
                if (Refuse(p) is { } why) { outcomes.Add(new RecycleOutcome(p, false, why)); continue; }
                BeforeMove(p);
                // Only the last check itself keeps an item here: what the system's recycler adds to it is tested on
                // RecycleGate, and these tests check what the job hands over to that check.
                if (recheck(p) is { } changed) { outcomes.Add(new RecycleOutcome(p, false, "checked again just before it moved: " + changed)); continue; }
                var lie = Lies(p);
                if (lie == Lie.ConfirmsButDoesNotMove) { outcomes.Add(new RecycleOutcome(p, true)); continue; }
                string target = Move(p);
                if (AlsoTakes(p) is { } along && Path.Exists(along))
                {
                    string where = Move(along);
                    if (ReportsWhatElseItTakes) others.Add(new RecycleOutcome(along, true, "it went along with " + p, where));
                }
                outcomes.Add(lie == Lie.MovesButDoesNotConfirm ? new RecycleOutcome(p, false, "no confirmation") : new RecycleOutcome(p, true, BinPath: target));
            }
            return [.. outcomes, .. others];
        }

        private string Move(string p)
        {
            string target = Path.Combine(bin, (Moved.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(p));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(p)) Directory.Move(p, target);
            else File.Move(p, target);
            Moved[p] = target;
            return target;
        }
    }
}
