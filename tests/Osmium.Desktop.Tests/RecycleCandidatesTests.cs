using System.IO.Hashing;
using Osmium.Core.Container;
using Osmium.Core.Planning;
using Osmium.Desktop.Jobs;

namespace Osmium.Desktop.Tests;

/// <summary>
/// The rules that decide which originals may move to the Recycle Bin after a compression, each checked in both
/// directions, on real folders scanned by the real scanner. The drive type is given by the test.
/// </summary>
public sealed class RecycleCandidatesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "osm-recycle-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly Func<string, DriveType> Fixed = _ => DriveType.Fixed;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RecycleCandidatesTests() => Directory.CreateDirectory(_root);

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

    /// <summary>
    /// The facts of a completed, verified job that archived exactly what the scan found (the planner adds the scan's
    /// skipped items to the builder's warnings), each file with the hash of its contents at the scan. The archive is
    /// written after the scan, as a job does.
    /// </summary>
    private RecycleFacts Facts(IReadOnlyList<string> inputs, ScanOptions? options = null, string? archive = null)
    {
        var scan = Scanner.Scan(inputs, options ?? new ScanOptions());
        archive ??= Path.Combine(_root, "out.osa");
        File.WriteAllBytes(archive, new byte[1234]);
        return new RecycleFacts
        {
            Inputs = inputs, Outcome = JobState.Completed, VerifiedBlocks = true,
            Build = new BuildResult(archive, scan.Files.Sum(f => f.Size), 1234, scan.Files.Count, 1, TimeSpan.Zero, scan.Skipped, [], 0, 0),
            ArchivedFiles = [.. scan.Files.Select(f => new ArchivedFile(f, HashOf(f.FullPath)))], ArchivedDirs = scan.Dirs,
        };
    }

    private static UInt128 HashOf(string path) => XxHash128.HashToUInt128(File.ReadAllBytes(path));

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

    private static string Reason(RecycleDecision d, string path) => d.Inputs.Single(i => i.Path == path).Reason ?? "";

    private static bool TryLink(string link, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(link, target);
            else File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    [Fact]
    public void AVerifiedJobMovesItsFoldersAndFiles()
    {
        string folder = Folder("photos");
        Write("photos/a.txt");
        Write("photos/sub/b.txt", "more text");
        Folder("photos/empty");
        string file = Write("notes.txt");
        var d = RecycleCandidates.Decide(Facts([folder, file]), Fixed);
        Assert.Equal([folder, file], d.Move);
        Assert.Empty(d.Keep);
    }

    [Fact]
    public void NothingMovesWithoutBlockVerification()
    {
        string folder = Folder("in");
        Write("in/a.txt");
        var facts = Facts([folder]);
        Assert.Equal([folder], RecycleCandidates.Decide(facts, Fixed).Move);
        var d = RecycleCandidates.Decide(facts with { VerifiedBlocks = false }, Fixed);
        Assert.Empty(d.Move);
        Assert.Contains("verification was off", Assert.Single(d.Keep).Reason);
    }

    [Fact]
    public void NothingMovesAfterBuilderWarnings()
    {
        string folder = Folder("in");
        Write("in/a.txt");
        var facts = Facts([folder]);
        Assert.Equal([folder], RecycleCandidates.Decide(facts, Fixed).Move);
        var warned = facts with { Build = facts.Build! with { Warnings = ["in/b.txt: cannot read (in use)"] } };
        var d = RecycleCandidates.Decide(warned, Fixed);
        Assert.Empty(d.Move);
        Assert.Contains("1 warning", Assert.Single(d.Keep).Reason);
    }

    [Theory]
    [InlineData(JobState.Canceled, "canceled")]
    [InlineData(JobState.Failed, "did not complete")]
    public void NothingMovesWhenTheJobDidNotComplete(JobState outcome, string reason)
    {
        string folder = Folder("in");
        Write("in/a.txt");
        var facts = Facts([folder]);
        Assert.Equal([folder], RecycleCandidates.Decide(facts, Fixed).Move);
        Assert.Contains(reason, Assert.Single(RecycleCandidates.Decide(facts with { Outcome = outcome }, Fixed).Keep).Reason);
        Assert.Contains("not completed", Assert.Single(RecycleCandidates.Decide(facts with { Build = null }, Fixed).Keep).Reason);
    }

    [Fact]
    public void TheArchiveMustExistWithTheReportedSize()
    {
        string file = Write("a.txt");
        var facts = Facts([file]);
        Assert.Equal([file], RecycleCandidates.Decide(facts, Fixed).Move);
        var other = facts with { Build = facts.Build! with { OutputBytes = 99 } };
        Assert.Contains("not the 99 bytes", Assert.Single(RecycleCandidates.Decide(other, Fixed).Keep).Reason);
        File.Delete(facts.Build!.Path);
        Assert.Contains("archive is missing", Assert.Single(RecycleCandidates.Decide(facts, Fixed).Keep).Reason);
    }

    [Fact]
    public void AFolderHoldingAnExcludedOrHiddenFileStays()
    {
        string clean = Folder("clean");
        Write("clean/a.txt");
        string excluded = Folder("excluded");
        Write("excluded/a.txt");
        Write("excluded/sub/cache.tmp");
        string hidden = Folder("hidden");
        Write("hidden/a.txt");
        string secret = Write("hidden/.secret"); // hidden on Unix by its name
        if (OperatingSystem.IsWindows()) File.SetAttributes(secret, FileAttributes.Hidden);
        var d = RecycleCandidates.Decide(Facts([clean, excluded, hidden], new ScanOptions { Excludes = ["*.tmp"], IncludeHidden = false }), Fixed);
        Assert.Equal([clean], d.Move);
        Assert.Contains("not in the archive: " + Path.Combine("sub", "cache.tmp"), Reason(d, excluded));
        Assert.Contains("not in the archive: .secret (a hidden file)", Reason(d, hidden));
        // With hidden files included (the default) and no exclude pattern, the same folders move.
        Assert.Equal([excluded, hidden], RecycleCandidates.Decide(Facts([excluded, hidden]), Fixed).Move);
    }

    [Fact]
    public void AFolderHoldingASystemFileStaysOnWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("system files are a Windows attribute");
        string folder = Folder("in");
        Write("in/a.txt");
        File.SetAttributes(Write("in/desktop.cfg"), FileAttributes.System);
        var d = RecycleCandidates.Decide(Facts([folder]), Fixed); // the scanner leaves system files out
        Assert.Contains("desktop.cfg (a system file)", Assert.Single(d.Keep).Reason);
        Assert.Equal([folder], RecycleCandidates.Decide(Facts([folder], new ScanOptions { IncludeSystem = true }), Fixed).Move);
    }

    [Fact]
    public void FilesThatChangedOrAppearedAfterTheScanStay()
    {
        string same = Write("same.txt");
        string grown = Write("grown.txt");
        string touched = Write("touched.txt");
        string folder = Folder("folder");
        string inner = Write("folder/inner.txt");
        string added = Folder("added");
        Write("added/a.txt");
        string gone = Folder("gone");
        Write("gone/a.txt");
        string removed = Write("gone/b.txt");
        var facts = Facts([same, grown, touched, folder, added, gone]);
        File.AppendAllText(grown, " and more");
        File.SetLastWriteTimeUtc(touched, File.GetLastWriteTimeUtc(touched).AddSeconds(-30));
        File.SetLastWriteTimeUtc(inner, File.GetLastWriteTimeUtc(inner).AddSeconds(-30));
        Write("added/new.txt");
        File.Delete(removed);
        var d = RecycleCandidates.Decide(facts, Fixed);
        Assert.Equal([same], d.Move);
        Assert.Contains("changed after it was read", Reason(d, grown));
        Assert.Contains("changed after it was read", Reason(d, touched));
        Assert.Contains("inner.txt changed after it was read", Reason(d, folder));
        Assert.Contains("not in the archive: new.txt", Reason(d, added));
        Assert.Contains("b.txt is no longer there", Reason(d, gone));
    }

    [Fact]
    public void AFolderHoldingALinkStays()
    {
        string plain = Folder("plain");
        Write("plain/a.txt");
        string withLink = Folder("with-link");
        Write("with-link/a.txt");
        string withJunction = Folder("with-junction");
        Write("with-junction/a.txt");
        string target = Folder("elsewhere");
        Write("elsewhere/data.txt");
        var facts = Facts([plain, withLink, withJunction]);
        if (!TryLink(Path.Combine(withLink, "link.txt"), Path.Combine(target, "data.txt"), directory: false)
            || !TryLink(Path.Combine(withJunction, "linked folder"), target, directory: true))
            Assert.Skip("symbolic links cannot be created here");
        var d = RecycleCandidates.Decide(facts, Fixed);
        Assert.Equal([plain], d.Move);
        Assert.Contains("holds a link (a junction, symbolic link or cloud file): link.txt", Reason(d, withLink));
        Assert.Contains("holds a link (a junction, symbolic link or cloud file): linked folder", Reason(d, withJunction));
    }

    [Fact]
    public void AnInputThatBecameALinkStays()
    {
        string file = Write("a.txt");
        string folder = Folder("in");
        Write("in/b.txt");
        string target = Folder("elsewhere");
        Write("elsewhere/a.txt");
        Write("elsewhere/b.txt");
        var facts = Facts([file, folder]);
        Assert.Equal([file, folder], RecycleCandidates.Decide(facts, Fixed).Move);
        File.Delete(file);
        Directory.Delete(folder, recursive: true);
        if (!TryLink(file, Path.Combine(target, "a.txt"), directory: false) || !TryLink(folder, target, directory: true))
            Assert.Skip("symbolic links cannot be created here");
        var d = RecycleCandidates.Decide(facts, Fixed);
        Assert.Empty(d.Move);
        Assert.All(d.Keep, k => Assert.Contains("it is a link", k.Reason));
    }

    [Fact]
    public void AFolderHoldingTheArchiveStays()
    {
        string folder = Folder("in");
        Write("in/a.txt");
        Assert.Equal([folder], RecycleCandidates.Decide(Facts([folder]), Fixed).Move); // the archive next to it
        var inside = Facts([folder], archive: Path.Combine(folder, "in.osa"));
        Assert.Equal("the archive is inside it", Assert.Single(RecycleCandidates.Decide(inside, Fixed).Keep).Reason);
        var deeper = Facts([folder], archive: Path.Combine(Folder("in/sub"), "in.osa"));
        Assert.Equal("the archive is inside it", Assert.Single(RecycleCandidates.Decide(deeper, Fixed).Keep).Reason);
    }

    [Fact]
    public void TheArchiveItselfStays()
    {
        string file = Write("a.osa");
        var d = RecycleCandidates.Decide(Facts([file], archive: file), Fixed); // compressed onto itself with overwrite
        Assert.Equal("it is the archive", Assert.Single(d.Keep).Reason);
    }

    [Theory]
    [InlineData(DriveType.Removable, "a removable drive")]
    [InlineData(DriveType.Network, "a network drive")]
    [InlineData(DriveType.CDRom, "a CD or DVD drive")]
    [InlineData(DriveType.Ram, "a RAM disk")]
    [InlineData(DriveType.Unknown, "a drive of unknown type")]
    [InlineData(DriveType.NoRootDirectory, "a drive of unknown type")]
    public void OnlyFixedDrivesMove(DriveType type, string name)
    {
        string folder = Folder("in");
        Write("in/a.txt");
        string file = Write("b.txt");
        var facts = Facts([folder, file]);
        Assert.Equal([folder, file], RecycleCandidates.Decide(facts, Fixed).Move);
        var d = RecycleCandidates.Decide(facts, p => p == file ? type : DriveType.Fixed);
        Assert.Equal([folder], d.Move);
        Assert.Equal($"it is on {name}, which has no Recycle Bin", Reason(d, file));
    }

    [Fact]
    public void MixedInputsMoveOrStayOneByOne()
    {
        string moves = Folder("moves");
        Write("moves/a.txt");
        Write("moves/deep/b.bin", "other bytes");
        string keeps = Folder("keeps");
        Write("keeps/a.txt");
        Write("keeps/Thumbs.db");
        string movesFile = Write("one.txt");
        string excludedFile = Write("two.tmp");
        string removableFile = Write("three.txt");
        var facts = Facts([moves, keeps, movesFile, excludedFile, removableFile], new ScanOptions { Excludes = ["Thumbs.db", "*.tmp"] });
        var d = RecycleCandidates.Decide(facts, p => p == removableFile ? DriveType.Removable : DriveType.Fixed);
        Assert.Equal(facts.Inputs, d.Inputs.Select(i => i.Path)); // in the order given
        Assert.Equal([moves, movesFile], d.Move);
        Assert.Equal([keeps, excludedFile, removableFile], d.Keep.Select(k => k.Path));
        Assert.Equal("it holds a file that is not in the archive: Thumbs.db", Reason(d, keeps));
        Assert.Equal("it is not in the archive", Reason(d, excludedFile));
        Assert.Contains("removable drive", Reason(d, removableFile));
    }

    [Fact]
    public void OnlyWhatTheArchiveIndexListsCountsAsArchived()
    {
        string folder = Folder("in");
        Write("in/a.txt");
        Write("in/b.txt");
        var facts = Facts([folder]);
        var scanned = facts.ArchivedFiles.Select(f => f.Scanned).ToList();
        var index = facts.ArchivedDirs.Select(d => new ArchiveEntry { Path = d.RelPath, Kind = EntryKind.Directory })
            .Concat(facts.ArchivedFiles.Select(f => new ArchiveEntry { Path = f.Scanned.RelPath, Size = f.Scanned.Size, ModifiedUtc = f.Scanned.ModifiedUtc, Hash = f.Hash }))
            .ToList();
        var (files, dirs) = RecycleCandidates.InArchive(scanned, facts.ArchivedDirs, index);
        Assert.Equal(facts.ArchivedFiles, files); // each with the hash its entry stores
        Assert.Equal(facts.ArchivedDirs, dirs);
        Assert.Equal([folder], RecycleCandidates.Decide(facts with { ArchivedFiles = files, ArchivedDirs = dirs }, Fixed).Move);

        // An index without b.txt, or with another size or time for a.txt, does not hold them.
        foreach (var damaged in new[]
        {
            index.Where(e => e.Path != "in/b.txt").ToList(),
            index.Select(e => e.Path == "in/a.txt" ? new ArchiveEntry { Path = e.Path, Size = e.Size + 1, ModifiedUtc = e.ModifiedUtc } : e).ToList(),
            index.Select(e => e.Path == "in/a.txt" ? new ArchiveEntry { Path = e.Path, Size = e.Size, ModifiedUtc = e.ModifiedUtc!.Value.AddTicks(1) } : e).ToList(),
            index.Select(e => e.Path == "in/a.txt" ? new ArchiveEntry { Path = e.Path, Size = e.Size } : e).ToList(),
        })
        {
            var (f2, d2) = RecycleCandidates.InArchive(scanned, facts.ArchivedDirs, damaged);
            Assert.Single(f2);
            Assert.Contains("is not in the archive", Assert.Single(RecycleCandidates.Decide(facts with { ArchivedFiles = f2, ArchivedDirs = d2 }, Fixed).Keep).Reason);
        }

        // A folder the index does not list as a folder is not in the archive either, even when it is empty.
        string empty = Folder("empty");
        var emptyFacts = Facts([empty]);
        Assert.Equal([empty], RecycleCandidates.Decide(emptyFacts, Fixed).Move);
        Assert.Equal("it is not in the archive", Assert.Single(RecycleCandidates.Decide(emptyFacts with { ArchivedDirs = [] }, Fixed).Keep).Reason);
    }

    [Fact]
    public void InputsGivenTwiceOrInsideOneAnotherStay()
    {
        string folder = Folder("in");
        string inner = Write("in/a.txt");
        string other = Write("b.txt");
        var d = RecycleCandidates.Decide(Facts([folder, inner, other]), Fixed);
        Assert.Equal([other], d.Move);
        Assert.Equal($"another input is inside it: {inner}", Reason(d, folder));
        Assert.Equal($"it is inside another input, {folder}", Reason(d, inner));
        var twice = RecycleCandidates.Decide(Facts([other, other + Path.DirectorySeparatorChar]), Fixed);
        Assert.Empty(twice.Move);
        Assert.Equal(2, twice.Keep.Count(k => k.Reason == "it was given more than once"));
    }

    [Fact]
    public void InputsAreReportedAsFullPathsWithTheSystemSeparator()
    {
        string inner = Write("in/a.txt");
        string mixed = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.AltDirectorySeparatorChar + "in" + Path.AltDirectorySeparatorChar + "a.txt"
            + Path.AltDirectorySeparatorChar;
        var d = RecycleCandidates.Decide(Facts([inner]) with { Inputs = [mixed] }, Fixed);
        Assert.Equal(Path.GetFullPath(inner), Assert.Single(d.Inputs).Path);
        Assert.Equal(Path.GetFullPath(inner), RecycleCandidates.Display(mixed));
    }

    [Fact]
    public void RelativePathsAndRootsStay()
    {
        var facts = Facts([Write("a.txt")]);
        var d = RecycleCandidates.Decide(facts with { Inputs = ["a.txt", Path.GetPathRoot(_root)!] }, Fixed);
        Assert.Empty(d.Move);
        Assert.Equal(2, d.Keep.Count);
        Assert.Equal("it is not a full path", d.Keep[0].Reason);
    }

    [Fact]
    public void AnInputThatIsGoneStays()
    {
        string file = Write("a.txt");
        var facts = Facts([file]);
        File.Delete(file);
        Assert.Equal("it is no longer there", Assert.Single(RecycleCandidates.Decide(facts, Fixed).Keep).Reason);
    }

    [Fact]
    public void AFailingDriveQueryKeepsTheInput()
    {
        string file = Write("a.txt");
        var d = RecycleCandidates.Decide(Facts([file]), _ => throw new IOException("the drive did not answer"));
        Assert.Equal("it could not be checked (the drive did not answer)", Assert.Single(d.Keep).Reason);
    }

    [Fact]
    public void ItemsWithNamesOsmiumCannotExtractStay()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Windows does not allow these names");
        string colon = Folder("colon");
        Write("colon/a.txt");
        Write("colon/meeting: notes.txt");
        string dots = Folder("dots");
        Write("dots/a.txt");
        Folder("dots/trailing.");
        string reserved = Folder("reserved");
        Write("reserved/aux.txt");
        string rootDots = Folder("root.");
        Write("root./a.txt");
        string question = Write("what?.txt");
        string plain = Folder("plain");
        Write("plain/a.txt");
        var d = RecycleCandidates.Decide(Facts([colon, dots, reserved, rootDots, question, plain]), Fixed);
        Assert.Equal([plain], d.Move);
        Assert.Equal("Osmium cannot extract meeting: notes.txt under its name (invalid character U+003A)", Reason(d, colon));
        Assert.Equal("Osmium cannot extract the folder trailing. under its name (component ends with dot or space)", Reason(d, dots));
        Assert.Equal("Osmium cannot extract aux.txt under its name (reserved device name 'aux')", Reason(d, reserved));
        Assert.Equal("Osmium cannot extract it under its name (component ends with dot or space)", Reason(d, rootDots));
        Assert.Equal("Osmium cannot extract it under its name (invalid character U+003F)", Reason(d, question));
    }

    [Fact]
    public void FoldersWhoseNamesShareAPrefixAreJudgedApart()
    {
        // Archived paths that share "in" sort next to one another; each folder is judged on its own files only.
        string inFolder = Folder("in");
        Write("in/a.txt");
        string in2 = Folder("in2");
        Write("in2/a.txt");
        string gone = Write("in2/b.txt");
        string inBak = Folder("in.bak");
        Write("in.bak/a.txt");
        var facts = Facts([inFolder, in2, inBak]);
        File.Delete(gone);
        var d = RecycleCandidates.Decide(facts, Fixed);
        Assert.Equal([inFolder, inBak], d.Move);
        Assert.Equal("b.txt is no longer there", Reason(d, in2));
    }

    [Fact]
    public void AFileTheScanRecordedTwiceDifferentlyStays()
    {
        string file = Write("a.txt");
        var facts = Facts([file]);
        var first = facts.ArchivedFiles[0];
        var twice = facts with { ArchivedFiles = [first, first with { Scanned = first.Scanned with { Size = first.Scanned.Size + 1 } }] };
        Assert.Equal([file], RecycleCandidates.Decide(facts, Fixed).Move);
        Assert.Equal("it changed after it was read", Assert.Single(RecycleCandidates.Decide(twice, Fixed).Keep).Reason);
        var otherContents = facts with { ArchivedFiles = [first, first with { Hash = first.Hash + 1 }] };
        Assert.Equal("it changed after it was read", Assert.Single(RecycleCandidates.Decide(otherContents, Fixed).Keep).Reason);
    }

    [Fact]
    public void CloudPlaceholdersStayOnWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("cloud placeholders are a Windows attribute");
        string folder = Folder("in");
        Write("in/a.txt");
        string nested = Write("in/online only.txt");
        string file = Write("b.txt");
        string clean = Folder("clean");
        Write("clean/a.txt");
        var facts = Facts([folder, file, clean]);
        // Set after the scan (the scanner itself leaves placeholders out with a warning).
        File.SetAttributes(nested, FileAttributes.Offline);
        File.SetAttributes(file, FileAttributes.Offline);
        var d = RecycleCandidates.Decide(facts, Fixed);
        Assert.Equal([clean], d.Move);
        Assert.Equal("it holds a cloud placeholder whose contents are not on this PC: online only.txt", Reason(d, folder));
        Assert.Equal("it is a cloud placeholder whose contents are not on this PC", Reason(d, file));
    }

    [Fact]
    public void PathsThatDifferOnlyInCaseAreOneItemOnWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("file names ignore case on Windows only");
        string folder = Folder("Photos");
        Write("Photos/a.txt");
        string typed = Path.Combine(_root, "PHOTOS");
        Assert.Equal([typed], RecycleCandidates.Decide(Facts([typed]), Fixed).Move); // the scan used the same spelling
        var twice = RecycleCandidates.Decide(Facts([folder, typed]), Fixed);
        Assert.Empty(twice.Move);
        Assert.All(twice.Keep, k => Assert.Equal("it was given more than once", k.Reason));
        var inside = Facts([folder], archive: Path.Combine(_root, "photos", "photos.osa"));
        Assert.Equal("the archive is inside it", Assert.Single(RecycleCandidates.Decide(inside, Fixed).Keep).Reason);
    }

    [Fact]
    public void TheLastCheckAppliesTheSameRulesAgain()
    {
        string folder = Folder("in");
        Write("in/a.txt");
        string file = Write("b.txt");
        var facts = Facts([folder, file]);
        var recheck = RecycleCandidates.Recheck(facts, Fixed, Ct);
        Assert.Null(recheck(folder));
        Assert.Null(recheck(file));
        File.AppendAllText(file, " changed");
        Write("in/new.txt");
        Assert.Equal("it changed after it was read", recheck(file));
        Assert.Equal("it holds a file that is not in the archive: new.txt", recheck(folder));
        Assert.Equal("it is on a removable drive, which has no Recycle Bin", RecycleCandidates.Recheck(facts, _ => DriveType.Removable, Ct)(folder));
        File.Delete(facts.Build!.Path);
        Assert.Equal("the archive is missing", recheck(folder));
        Assert.Equal("block verification was off for this job (Settings → Compression → Verify every block)",
            RecycleCandidates.Recheck(facts with { VerifiedBlocks = false }, Fixed, Ct)(file));
    }

    [Fact]
    public void AnEditThatKeepsSizeAndTimeStaysAtTheLastCheck()
    {
        string file = Write("a.txt", "the archived text");
        string folder = Folder("in");
        Write("in/empty.txt", "");
        string inner = Write("in/sub/b.txt", "more archived text");
        string other = Folder("other");
        Write("other/c.txt", "unchanged");
        Write("other/empty.txt", "");
        var facts = Facts([file, folder, other]);
        EditKeepingSizeAndTime(file);
        EditKeepingSizeAndTime(inner);
        Assert.Equal([file, folder, other], RecycleCandidates.Decide(facts, Fixed).Move); // sizes and times cannot tell
        var recheck = RecycleCandidates.Recheck(facts, Fixed, Ct);
        Assert.Equal("its contents no longer match the archive", recheck(file));
        Assert.Equal($"the contents of {Path.Combine("sub", "b.txt")} no longer match the archive", recheck(folder));
        Assert.Null(recheck(other));
    }

    [Fact]
    public void WhatChangesWhileTheFilesAreReadKeepsTheFolder()
    {
        string folder = Folder("in");
        Write("in/a.txt");
        string b = Write("in/b.txt", "more text");
        var facts = Facts([folder]);
        // The drive is asked before each look at the folder: before its files are read, and for the last look after.
        int asked = 0;
        Func<string, DriveType> appears = _ =>
        {
            if (++asked == 2) Write("in/late.txt", "written while the files were read");
            return DriveType.Fixed;
        };
        Assert.Equal("it holds a file that is not in the archive: late.txt", RecycleCandidates.Recheck(facts, appears, Ct)(folder));
        Assert.Equal(2, asked);
        File.Delete(Path.Combine(folder, "late.txt"));
        asked = 0;
        Func<string, DriveType> grows = _ =>
        {
            if (++asked == 2) File.AppendAllText(b, " and more");
            return DriveType.Fixed;
        };
        Assert.Equal("b.txt changed after it was read", RecycleCandidates.Recheck(facts, grows, Ct)(folder));
    }

    [Fact]
    public void AFileThatCannotBeReadStays()
    {
        string file = Write("a.txt");
        string folder = Folder("in");
        string inner = Write("in/b.txt", "more text");
        var facts = Facts([file, folder]);
        var recheck = RecycleCandidates.Recheck(facts, Fixed, Ct);
        // Held open by another program that lets nobody else read them.
        using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (File.Open(inner, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.StartsWith("it could not be read to compare it with the archive (", recheck(file));
            Assert.StartsWith("b.txt could not be read to compare it with the archive (", recheck(folder));
        }
        Assert.Null(recheck(file));
        Assert.Null(recheck(folder));
    }

    [Fact]
    public void ACancelDuringTheLastCheckKeepsTheInput()
    {
        string folder = Folder("in");
        Write("in/a.txt");
        string file = Write("b.txt");
        var facts = Facts([folder, file]);
        using var cts = new CancellationTokenSource();
        // Canceled once the check has begun (the drive is asked first), before the files are read.
        Func<string, DriveType> cancels = _ =>
        {
            cts.Cancel();
            return DriveType.Fixed;
        };
        Assert.Equal("the job was canceled", RecycleCandidates.Recheck(facts, cancels, cts.Token)(folder));
        Assert.Equal("the job was canceled", RecycleCandidates.Recheck(facts, Fixed, cts.Token)(file));
        Assert.Null(RecycleCandidates.Recheck(facts, Fixed, Ct)(folder));
    }

    [Fact]
    public void DataInAnAlternateStreamKeepsTheItem()
    {
        string file = Write("a.txt");
        string marked = Write("downloaded.txt");
        string folder = Folder("in");
        string inner = Write("in/sub/b.txt");
        string tagged = Folder("tagged");
        Write("tagged/c.txt");
        string unlisted = Write("unlisted.txt");
        string plain = Folder("plain");
        Write("plain/d.txt");
        // The named streams each path has, as the file system would list them.
        var streams = new Dictionary<string, string[]>
        {
            [file] = ["notes"],
            [marked] = ["Zone.Identifier"], // the mark of the web alone: it moves
            [inner] = ["Zone.Identifier", "summary"],
            [tagged] = ["thumbnail"], // a stream on the folder itself
        };
        Func<string, IReadOnlyList<string>> probe = p =>
            p == unlisted ? throw new IOException("the drive did not answer") : streams.GetValueOrDefault(p, []);
        var facts = Facts([file, marked, folder, tagged, unlisted, plain]);
        var d = RecycleCandidates.Decide(facts, Fixed, probe);
        Assert.Equal([marked, plain], d.Move);
        Assert.Equal("it holds data in an alternate stream that the archive does not keep: a.txt:notes", Reason(d, file));
        Assert.Equal($"it holds data in an alternate stream that the archive does not keep: {Path.Combine("sub", "b.txt")}:summary", Reason(d, folder));
        Assert.Equal("it holds data in an alternate stream that the archive does not keep: tagged:thumbnail", Reason(d, tagged));
        Assert.Equal("the alternate streams of unlisted.txt could not be listed (the drive did not answer)", Reason(d, unlisted));

        // The last check applies the same rule, so a stream added since the decision keeps the item too.
        var recheck = RecycleCandidates.Recheck(facts, Fixed, Ct, probe);
        Assert.Null(recheck(marked));
        Assert.Null(recheck(plain));
        streams[Path.GetFullPath(Path.Combine(plain, "d.txt"))] = ["notes"];
        Assert.Equal("it holds data in an alternate stream that the archive does not keep: d.txt:notes", recheck(plain));
    }

    [Fact]
    public void DataInAnAlternateStreamKeepsTheItemOnWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("alternate data streams are kept by Windows file systems only");
        string file = Write("a.txt");
        string marked = Write("downloaded.txt");
        string folder = Folder("in");
        string inner = Write("in/sub/b.txt");
        string tagged = Folder("tagged");
        Write("tagged/c.txt");
        string plain = Folder("plain");
        Write("plain/d.txt");
        try
        {
            File.WriteAllText(file + ":notes", "kept in no archive");
            File.WriteAllText(marked + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            File.WriteAllText(inner + ":summary", "kept in no archive");
            File.WriteAllText(tagged + ":thumbnail", "kept in no archive");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"the drive of the temporary folder keeps no alternate streams ({e.Message})");
        }
        var facts = Facts([file, marked, folder, tagged, plain]);
        var d = RecycleCandidates.Decide(facts, Fixed);
        Assert.Equal([marked, plain], d.Move);
        Assert.Equal("it holds data in an alternate stream that the archive does not keep: a.txt:notes", Reason(d, file));
        Assert.Equal($"it holds data in an alternate stream that the archive does not keep: {Path.Combine("sub", "b.txt")}:summary", Reason(d, folder));
        Assert.Equal("it holds data in an alternate stream that the archive does not keep: tagged:thumbnail", Reason(d, tagged));
        var recheck = RecycleCandidates.Recheck(facts, Fixed, Ct);
        Assert.Null(recheck(marked));
        Assert.Equal("it holds data in an alternate stream that the archive does not keep: a.txt:notes", recheck(file));
    }

    [Fact]
    public void KeepAllKeepsEveryInputWithTheReason()
    {
        string file = Write("a.txt");
        var d = RecycleDecision.KeepAll([file + Path.DirectorySeparatorChar, "relative"], "no Recycle Bin here");
        Assert.Empty(d.Move);
        Assert.Equal([file, "relative"], d.Keep.Select(k => k.Path));
        Assert.All(d.Keep, k => Assert.Equal("no Recycle Bin here", k.Reason));
    }
}
