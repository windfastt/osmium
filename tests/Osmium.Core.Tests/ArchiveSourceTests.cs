using System.Collections.Concurrent;
using System.IO.Hashing;
using Osmium.Core.Archives;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests;

/// <summary>.osa archives through <see cref="ArchiveSources"/>: listing, details, extraction by selection, index and
/// folder, testing, replacing, passwords, unsafe names and limits.</summary>
public class ArchiveSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Files =
        ["in/docs/img/a.png", "in/docs/img/b.png", "in/docs/readme.txt", "in/docs/zero.bin", "in/notes.txt"];

    private static void MakeTree(string root)
    {
        var rnd = new Random(7);
        Directory.CreateDirectory(Path.Combine(root, "docs", "img"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllText(Path.Combine(root, "notes.txt"), string.Concat(Enumerable.Repeat("a note about the archive\n", 200)));
        File.WriteAllText(Path.Combine(root, "docs", "readme.txt"), "Read me first.");
        File.WriteAllBytes(Path.Combine(root, "docs", "zero.bin"), []);
        var png = new byte[5000];
        rnd.NextBytes(png);
        File.WriteAllBytes(Path.Combine(root, "docs", "img", "a.png"), png);
        rnd.NextBytes(png);
        File.WriteAllBytes(Path.Combine(root, "docs", "img", "b.png"), png);
    }

    private static async Task<string> Build(TempDir t, string? password = null)
    {
        MakeTree(t.Sub("in"));
        var scan = Scanner.Scan([t.Sub("in")]);
        var plan = await new Planner(new PlanOptions { Profile = Profile.ByName("fast"), MemoryBudget = 2L << 30 }).BuildAsync(scan, Ct);
        var r = await ArchiveBuilder.BuildAsync(plan, new BuildOptions { OutputPath = t.Sub("a.osa"), Password = password, KdfIterations = 1000, MemoryBudget = 2L << 30 }, Ct);
        return r.Path;
    }

    /// <summary>Every file under <paramref name="root"/>, relative, with '/' separators.</summary>
    private static List<string> FilesUnder(string root) => !Directory.Exists(root) ? []
        : Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    private static void AssertSameFile(string expected, string actual) => Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));

    /// <summary>An archive written entry by entry, with names <see cref="ArchiveBuilder"/> would never write.</summary>
    private static string Crafted(TempDir t, params string[] paths)
    {
        var data = "crafted content"u8.ToArray();
        string path = t.Sub("crafted.osa");
        using var w = new OsaWriter(path);
        var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", MethodRegistry.Default), data, CodecContext.None);
        int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, data.Length);
        w.Finish(paths.Select(p => p.EndsWith('/')
            ? new ArchiveEntry { Path = p.TrimEnd('/'), Kind = EntryKind.Directory }
            : new ArchiveEntry { Path = p, Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(frame, 0, data.Length)], Hash = XxHash128.HashToUInt128(data) }));
        w.Commit(overwrite: false);
        return path;
    }

    [Fact]
    public async Task ListsEveryEntryWithItsIndexSizeAndKind()
    {
        using var t = new TempDir();
        string path = await Build(t);
        using var s = ArchiveSources.Open(path, ct: Ct);

        Assert.Equal(ArchiveKind.Osa, s.Kind);
        Assert.Equal($".osa {Osa.Major}.{Osa.Minor}", s.FormatName);
        Assert.Equal(Path.GetFullPath(path), s.Path);
        Assert.True(s.ItemsComplete);
        Assert.False(s.IsEncrypted);
        Assert.Empty(s.Warnings);
        Assert.Equal(new FileInfo(path).Length, s.FileLength);
        for (int i = 0; i < s.Items.Count; i++) Assert.Equal(i, s.Items[i].Index);

        var byPath = s.Items.ToDictionary(i => i.Path);
        Assert.Equal(Files, s.Items.Where(i => !i.IsDirectory).Select(i => i.Path).Order(StringComparer.Ordinal));
        foreach (var dir in new[] { "in/docs", "in/docs/img", "in/empty" })
        {
            Assert.True(byPath[dir].IsDirectory);
            Assert.Null(byPath[dir].Size);
        }
        foreach (var file in Files)
        {
            var item = byPath[file];
            long length = new FileInfo(t.Sub(file)).Length;
            Assert.Equal(length, item.Size);
            Assert.Null(item.PackedSize);
            Assert.False(item.Encrypted);
            Assert.False(item.IsLink || item.IsSpecial);
            Assert.Equal(length == 0, item.Method is null);
            Assert.Equal(File.GetLastWriteTimeUtc(t.Sub(file)), item.ModifiedUtc!.Value, TimeSpan.FromSeconds(2));
        }
        Assert.Equal(Files.Sum(f => new FileInfo(t.Sub(f)).Length), s.TotalSize);
    }

    [Fact]
    public async Task DetailsAreListedInTheirOrder()
    {
        using var t = new TempDir();
        using var s = ArchiveSources.Open(await Build(t), ct: Ct);
        Assert.Equal(
            ["Format", "Created by", "Profile", "Files", "Folders", "Original size", "Archive size", "Ratio", "Extraction",
             "Memory per block", "Encrypted", "Experimental features"],
            s.Details.Select(d => d.Label));
        string Value(string label) => s.Details.Single(d => d.Label == label).Value;
        Assert.Equal(s.FormatName, Value("Format"));
        Assert.Equal(Files.Length.ToString("N0"), Value("Files"));
        Assert.Equal("no", Value("Encrypted"));
        Assert.Equal("no", Value("Experimental features"));
        Assert.StartsWith("~", Value("Extraction"));
        Assert.StartsWith("up to ", Value("Memory per block"));
    }

    [Fact]
    public async Task ExtractsEverythingAndListsWhatItWrote()
    {
        using var t = new TempDir();
        using var s = ArchiveSources.Open(await Build(t), ct: Ct);
        var res = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);

        Assert.Equal(Files.Length, res.FilesOk);
        Assert.Equal(0, res.LeftOut);
        Assert.Equal(0, res.Renamed);
        Assert.Empty(res.Errors);
        Assert.Equal(Files, FilesUnder(t.Sub("out")));
        foreach (var f in Files) AssertSameFile(t.Sub(f), Path.Combine(t.Sub("out"), f));
        Assert.True(Directory.Exists(t.Sub("out/in/empty")));
        // Written: every file, by index, at the path it was written to, in archive order.
        Assert.Equal(Files.Length, res.Written.Count);
        Assert.Equal(res.Written.OrderBy(w => w.Index), res.Written);
        foreach (var w in res.Written)
            Assert.Equal(Path.GetFullPath(Path.Combine(t.Sub("out"), s.Items[w.Index].Path)), w.Path);
    }

    [Fact]
    public async Task ExtractsExactEntriesByIndex()
    {
        using var t = new TempDir();
        using var s = ArchiveSources.Open(await Build(t), ct: Ct);
        int a = s.Items.Single(i => i.Path == "in/docs/img/a.png").Index, notes = s.Items.Single(i => i.Path == "in/notes.txt").Index;
        // Select is ignored when Indices is set; a folder's index selects only the folder.
        int docs = s.Items.Single(i => i.Path == "in/docs").Index;
        var res = s.Extract(t.Sub("out"), new ArchiveExtractRequest { Indices = new HashSet<int> { a, notes, docs }, Select = _ => true }, null, Ct);

        Assert.Equal(2, res.FilesOk);
        Assert.Equal(["in/docs/img/a.png", "in/notes.txt"], FilesUnder(t.Sub("out")));
        Assert.Equal([a, notes], res.Written.Select(w => w.Index).Order());
    }

    [Theory]
    [InlineData("in/docs")]
    [InlineData("in/docs/")]
    public async Task StripPrefixExtractsAFoldersContentsToTheRoot(string prefix)
    {
        using var t = new TempDir();
        using var s = ArchiveSources.Open(await Build(t), ct: Ct);
        var res = s.Extract(t.Sub("out"), new ArchiveExtractRequest { StripPrefix = prefix }, null, Ct);

        Assert.Equal(["img/a.png", "img/b.png", "readme.txt", "zero.bin"], FilesUnder(t.Sub("out")));
        Assert.Equal(4, res.FilesOk);
        Assert.Equal(0, res.LeftOut);
        AssertSameFile(t.Sub("in/docs/img/a.png"), t.Sub("out/img/a.png"));
        Assert.False(Directory.Exists(t.Sub("out/in")));
        Assert.All(res.Written, w => Assert.StartsWith(Path.GetFullPath(t.Sub("out")) + Path.DirectorySeparatorChar, w.Path));
    }

    [Fact]
    public async Task StripPrefixAndASelectionApplyTogether()
    {
        using var t = new TempDir();
        using var s = ArchiveSources.Open(await Build(t), ct: Ct);
        var res = s.Extract(t.Sub("out"), new ArchiveExtractRequest { StripPrefix = "in/docs", Select = i => i.Path.EndsWith(".png", StringComparison.Ordinal) || i.Path == "in/notes.txt" }, null, Ct);
        Assert.Equal(["img/a.png", "img/b.png"], FilesUnder(t.Sub("out")));
        Assert.Equal(2, res.FilesOk);
        Assert.Equal(0, res.LeftOut);
    }

    [Fact]
    public void AMappedPathNeverLeavesTheDestination()
    {
        using var t = new TempDir();
        string path = Crafted(t, "docs/../../evil.txt", "docs/..", "docs/ok.txt", "docs/sub/../../../evil2.txt");
        using var s = ArchiveSources.Open(path, ct: Ct);
        var res = s.Extract(t.Sub("box/out"), new ArchiveExtractRequest { StripPrefix = "docs" }, null, Ct);

        Assert.Equal(1, res.FilesOk);
        Assert.Equal(3, res.SkippedUnsafe);
        Assert.Equal(3, res.LeftOut);
        Assert.Equal(0, res.FilesFailed);
        Assert.Equal(["out/ok.txt"], FilesUnder(t.Sub("box")));
        Assert.False(File.Exists(t.Sub("evil.txt")) || File.Exists(t.Sub("box/evil.txt")) || File.Exists(t.Sub("evil2.txt")));
    }

    [Fact]
    public void UnsafeEntriesAreSkippedAndCounted()
    {
        using var t = new TempDir();
        string path = Crafted(t, "../evil.txt", "/abs.txt", "ok.txt", "sub/CON", "../evil-dir/");
        using var s = ArchiveSources.Open(path, ct: Ct);
        var res = s.Extract(t.Sub("box/out"), new ArchiveExtractRequest(), null, Ct);

        Assert.Equal(1, res.FilesOk);
        Assert.Equal(4, res.SkippedUnsafe); // three files and a folder
        Assert.Equal(0, res.FilesFailed);
        Assert.Equal(4, res.LeftOut);
        Assert.Equal(4, res.Errors.Count);
        Assert.Equal(["out/ok.txt"], FilesUnder(t.Sub("box")));
        Assert.Equal(["box"], Directory.GetDirectories(t.Path).Select(Path.GetFileName));
        Assert.Equal(["ok.txt"], res.Written.Select(w => s.Items[w.Index].Path));
    }

    [Fact]
    public async Task TestsEverythingOrASelection()
    {
        using var t = new TempDir();
        using var s = ArchiveSources.Open(await Build(t), ct: Ct);
        var all = s.Test(null, null, Ct);
        Assert.Equal(Files.Length, all.FilesOk);
        Assert.Equal(0, all.LeftOut);
        Assert.Empty(all.Written);

        var some = s.Test(i => i.Path.EndsWith(".png", StringComparison.Ordinal), null, Ct);
        Assert.Equal(2, some.FilesOk);
        Assert.Equal(0, some.LeftOut);
        Assert.False(Directory.Exists(t.Sub("out")));
    }

    [Fact]
    public async Task BeforeReplaceIsCalledForEachReplacedFileInOverwriteModeOnly()
    {
        using var t = new TempDir();
        using var s = ArchiveSources.Open(await Build(t), ct: Ct);
        string outDir = t.Sub("out");
        var calls = new ConcurrentBag<string>();
        void Record(string p) => calls.Add(p);

        var first = s.Extract(outDir, new ArchiveExtractRequest { Overwrite = OverwriteMode.Overwrite, BeforeReplace = Record }, null, Ct);
        Assert.Equal(Files.Length, first.FilesOk);
        Assert.Empty(calls); // nothing existed yet

        var renamed = s.Extract(outDir, new ArchiveExtractRequest { Overwrite = OverwriteMode.Rename, BeforeReplace = Record }, null, Ct);
        Assert.Empty(calls);
        Assert.Equal(Files.Length, renamed.Renamed);
        Assert.Equal(Files.Length, renamed.FilesOk);
        Assert.All(renamed.Written, w => Assert.Contains(" (2)", Path.GetFileName(w.Path)));

        var skipped = s.Extract(outDir, new ArchiveExtractRequest { Overwrite = OverwriteMode.Skip, BeforeReplace = Record }, null, Ct);
        Assert.Empty(calls);
        Assert.Equal(Files.Length, skipped.SkippedExisting);
        Assert.Equal(0, skipped.LeftOut);

        File.WriteAllText(Path.Combine(outDir, "in/notes.txt"), "changed");
        var replaced = s.Extract(outDir, new ArchiveExtractRequest { Overwrite = OverwriteMode.Overwrite, BeforeReplace = Record }, null, Ct);
        Assert.Equal(Files.Length, replaced.FilesOk);
        Assert.Equal(Files.Select(f => Path.GetFullPath(Path.Combine(outDir, f))).Order(StringComparer.Ordinal), calls.Order(StringComparer.Ordinal));
        AssertSameFile(t.Sub("in/notes.txt"), Path.Combine(outDir, "in/notes.txt"));
    }

    [Fact]
    public async Task AFailingBeforeReplaceKeepsThatFile()
    {
        using var t = new TempDir();
        using var s = ArchiveSources.Open(await Build(t), ct: Ct);
        string outDir = t.Sub("out");
        s.Extract(outDir, new ArchiveExtractRequest(), null, Ct);
        string notes = Path.Combine(outDir, "in/notes.txt");
        File.WriteAllText(notes, "keep me");

        var res = s.Extract(outDir, new ArchiveExtractRequest
        {
            Overwrite = OverwriteMode.Overwrite,
            BeforeReplace = p => { if (p == Path.GetFullPath(notes)) throw new IOException("The Recycle Bin refused it."); },
        }, null, Ct);

        Assert.Equal(Files.Length - 1, res.FilesOk);
        Assert.Equal(1, res.FilesFailed);
        Assert.Contains(res.Errors, e => e.Contains("The Recycle Bin refused it."));
        Assert.Equal("keep me", File.ReadAllText(notes));
        Assert.DoesNotContain(res.Written, w => w.Path == Path.GetFullPath(notes));
        Assert.Equal(Files, FilesUnder(outDir)); // no part file left behind
    }

    [Fact]
    public async Task AWrongPasswordFailsBeforeAnythingIsWritten()
    {
        using var t = new TempDir();
        string path = await Build(t, password: "correct horse");
        int asked = 0;
        Assert.Throws<WrongPasswordException>(() => ArchiveSources.Open(path, new ArchiveOpenOptions { Password = () => { asked++; return "wrong"; } }, Ct));
        Assert.Equal(1, asked);
        Assert.False(Directory.Exists(t.Sub("out")));

        asked = 0;
        using var s = ArchiveSources.Open(path, new ArchiveOpenOptions { Password = () => { asked++; return "correct horse"; } }, Ct);
        Assert.Equal(1, asked);
        Assert.True(s.IsEncrypted);
        Assert.All(s.Items, i => Assert.True(i.Encrypted));
        var res = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal(Files.Length, res.FilesOk);
        Assert.Equal(1, asked);
        Assert.Equal("yes", s.Details.Single(d => d.Label == "Encrypted").Value);
    }

    [Fact]
    public async Task AMissingPasswordIsRequired()
    {
        using var t = new TempDir();
        string path = await Build(t, password: "pw");
        Assert.Throws<PasswordRequiredException>(() => ArchiveSources.Open(path, ct: Ct));
        Assert.Throws<PasswordRequiredException>(() => ArchiveSources.Open(path, new ArchiveOpenOptions { Password = () => null }, Ct));
    }

    [Fact]
    public async Task DetectRecognisesOsaByItsContentOnly()
    {
        using var t = new TempDir();
        string osa = await Build(t);
        Assert.Equal(ArchiveKind.Osa, ArchiveSources.Detect(osa));
        string renamed = t.Sub("renamed.bin");
        File.Copy(osa, renamed);
        Assert.Equal(ArchiveKind.Osa, ArchiveSources.Detect(renamed));

        var noise = new byte[4096];
        new Random(3).NextBytes(noise);
        File.WriteAllBytes(t.Sub("noise.osa"), noise);
        File.WriteAllBytes(t.Sub("empty.osa"), []);
        File.WriteAllBytes(t.Sub("short.osa"), Osa.Magic[..4].ToArray());
        foreach (var name in new[] { "noise.osa", "empty.osa", "short.osa" })
        {
            Assert.Null(ArchiveSources.Detect(t.Sub(name)));
            var e = Assert.Throws<NotSupportedException>(() => ArchiveSources.Open(t.Sub(name), ct: Ct));
            Assert.Equal("This file is not an archive Osmium can read.", e.Message);
        }
        Assert.Equal([".osa"], ArchiveSources.Extensions);
    }

    [Theory]
    [InlineData("a.osa", "a")]
    [InlineData("photos 2026.osa", "photos 2026")]
    [InlineData("folder/b.c.osa", "b.c")]
    [InlineData("CON.osa", "_CON.osa files")]
    [InlineData("lpt1.osa", "_lpt1.osa files")]
    [InlineData(".osa", ".osa files")]
    [InlineData("x .osa", "x .osa files")]
    [InlineData("x..osa", "x..osa files")]
    [InlineData("noext", "noext")]
    public void StemOfGivesASafeFolderName(string path, string expected)
    {
        string stem = ArchiveSources.StemOf(path);
        Assert.Equal(expected, stem);
        Assert.Null(PathSafety.Validate(stem));
    }

    [Fact]
    public async Task EntryLimitsApplyWhenListing()
    {
        using var t = new TempDir();
        string path = await Build(t);
        Assert.Throws<EntryLimitException>(() => ArchiveSources.Open(path, new ArchiveOpenOptions { Limits = ImportLimits.Default with { MaxEntries = 3 } }, Ct));
        Assert.Throws<EntryLimitException>(() => ArchiveSources.Open(path, new ArchiveOpenOptions { Limits = ImportLimits.Default with { MaxPathDepth = 3 } }, Ct));
        using var s = ArchiveSources.Open(path, new ArchiveOpenOptions { Limits = ImportLimits.Default with { MaxPathDepth = 4 } }, Ct);
        Assert.NotEmpty(s.Items);
    }

    [Fact]
    public async Task TheExpansionLimitStopsExtractAndTestUntilItIsLifted()
    {
        using var t = new TempDir();
        string path = await Build(t);
        var tight = ImportLimits.Default with { ExpansionFloorBytes = 0, MaxExpansionRatio = 0.01 };
        using (var s = ArchiveSources.Open(path, new ArchiveOpenOptions { Limits = tight }, Ct))
        {
            var e = Assert.Throws<ExpansionLimitException>(() => s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct));
            Assert.Equal(0.01, e.Factor);
            Assert.Equal(s.TotalSize, e.OutputBytes);
            Assert.False(Directory.Exists(t.Sub("out")));
            Assert.Throws<ExpansionLimitException>(() => s.Test(null, null, Ct));
            // The request's own limits replace the ones given to Open.
            var res = s.Extract(t.Sub("out"), new ArchiveExtractRequest { Limits = tight with { AllowLargeExpansion = true } }, null, Ct);
            Assert.Equal(Files.Length, res.FilesOk);
        }
        using (var s = ArchiveSources.Open(path, ct: Ct))
            Assert.Throws<ExpansionLimitException>(() => s.Extract(t.Sub("out2"), new ArchiveExtractRequest { Limits = tight }, null, Ct));
        Assert.False(Directory.Exists(t.Sub("out2")));
    }

    [Fact]
    public async Task TheFreeSpaceReserveStopsExtractionBeforeAnythingIsWritten()
    {
        using var t = new TempDir();
        string path = await Build(t);
        using var s = ArchiveSources.Open(path, ct: Ct);
        var full = ImportLimits.Default with { FreeSpaceReserveBytes = long.MaxValue, AllowLargeExpansion = true };
        Assert.Throws<FreeSpaceException>(() => s.Extract(t.Sub("out"), new ArchiveExtractRequest { Limits = full }, null, Ct));
        Assert.False(Directory.Exists(t.Sub("out")));
        Assert.Equal(Files.Length, s.Test(null, null, Ct).FilesOk); // testing writes nothing
    }
}
