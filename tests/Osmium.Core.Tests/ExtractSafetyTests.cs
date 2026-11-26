using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests;

/// <summary>
/// .osa extraction plans every target before it writes: no two entries share a name, nothing is written through a
/// link already in the destination, only folders this run created get the archive's times and attributes, a cancel
/// leaves no partial file and no empty new folder, memory is reserved for the largest stage of a block, and the
/// Mark-of-the-Web goes on the part file before the file appears.
/// </summary>
public class ExtractSafetyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static readonly DateTime When = new(2020, 2, 2, 2, 2, 2, DateTimeKind.Utc);

    private static string P(TempDir t, params string[] parts) => Path.Combine([t.Path, .. parts]);

    /// <summary>Path in the archive → bytes (null: a folder). Files are laid end to end and cut into stored blocks of
    /// <paramref name="blockSize"/> bytes, so a file can span blocks.</summary>
    internal static string Write(string path, IEnumerable<(string Path, byte[]? Data)> items, int blockSize = 1 << 20,
        FileAttributes? folderAttributes = null, FileAttributes? fileAttributes = null)
    {
        var list = items.ToList();
        var all = new MemoryStream();
        var at = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (p, data) in list.Where(i => i.Data is not null))
        {
            at[p] = all.Length;
            all.Write(data);
        }
        var bytes = all.ToArray();
        using var w = new OsaWriter(path, new OsaWriterOptions { SpareThreshold = long.MaxValue });
        var blocks = new List<(long Start, int Length)>();
        for (long s = 0; s < bytes.Length; s += blockSize)
        {
            var chunk = bytes.AsSpan((int)s, (int)Math.Min(blockSize, bytes.Length - s)).ToArray();
            var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", MethodRegistry.Default), chunk, CodecContext.None);
            w.WriteBlock(desc, BlockBody.Hash128(chunk), payload, chunk.Length);
            blocks.Add((s, chunk.Length));
        }
        var entries = new List<ArchiveEntry>();
        foreach (var (p, data) in list)
        {
            if (data is null)
            {
                entries.Add(new ArchiveEntry { Path = p, Kind = EntryKind.Directory, ModifiedUtc = When, Attributes = folderAttributes });
                continue;
            }
            var segments = new List<Segment>();
            long start = at[p], end = start + data.Length;
            for (int k = 0; k < blocks.Count; k++)
            {
                long s = Math.Max(start, blocks[k].Start), e = Math.Min(end, blocks[k].Start + blocks[k].Length);
                if (e > s) segments.Add(new Segment(k, s - blocks[k].Start, e - s));
            }
            entries.Add(new ArchiveEntry
            {
                Path = p, Kind = EntryKind.File, Size = data.Length, Segments = segments, Hash = XxHash128.HashToUInt128(data),
                ModifiedUtc = When, Attributes = fileAttributes,
            });
        }
        w.Finish(entries);
        w.Commit(overwrite: false);
        return path;
    }

    internal static byte[] Data(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static ExtractResult Extract(string archive, string destination, ExtractOptions? options = null)
    {
        using var r = OsaReader.Open(archive);
        return Extractor.Extract(r, destination, null, options, null, Ct);
    }

    private static string[] Parts(string root) => Directory.Exists(root) ? Directory.GetFiles(root, "*.osmium-part-*", SearchOption.AllDirectories) : [];

    /// <summary>Every file below <paramref name="root"/>, relative with '/', and its content as text.</summary>
    private static Dictionary<string, string> Tree(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText, StringComparer.Ordinal);

    // ------------------------------------------------------------------ names

    [Fact]
    public void NamesThatDifferOnlyInCaseNeverReplaceEachOther()
    {
        using var t = new TempDir();
        string archive = Write(P(t, "a.osa"), [("a.txt", Data("lower")), ("A.txt", Data("upper"))]);
        string dest = P(t, "out");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "a.txt"), "already there");

        var res = Extract(archive, dest, new ExtractOptions { NameComparer = StringComparer.OrdinalIgnoreCase });
        Assert.Equal(2, res.FilesOk);
        Assert.Equal(2, res.Renamed);
        var tree = Tree(dest);
        Assert.Equal(3, tree.Count);
        Assert.Equal("already there", tree["a.txt"]);
        // "A.txt" sorts first: it takes "A (2).txt"; "a.txt" then finds "(2)" taken, whatever the case, and takes "(3)".
        Assert.Equal("upper", tree["A (2).txt"]);
        Assert.Equal("lower", tree["a (3).txt"]);
        Assert.Equal([("A.txt", Path.Combine(dest, "A (2).txt")), ("a.txt", Path.Combine(dest, "a (3).txt"))],
            res.Renames.Select(r => (r.ArchivePath, r.WrittenPath)));
    }

    [Fact]
    public void ARenamedFileNeverTakesTheNameOfAnotherEntry()
    {
        using var t = new TempDir();
        string archive = Write(P(t, "a.osa"), [("report (2).txt", Data("second report")), ("report.txt", Data("the report"))]);
        string dest = P(t, "out");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "report.txt"), "already there");

        var res = Extract(archive, dest);
        Assert.Equal(1, res.Renamed);
        var tree = Tree(dest);
        Assert.Equal("already there", tree["report.txt"]);
        Assert.Equal("second report", tree["report (2).txt"]);
        Assert.Equal("the report", tree["report (3).txt"]);
    }

    [Theory]
    [InlineData(OverwriteMode.Overwrite)]
    [InlineData(OverwriteMode.Skip)]
    [InlineData(OverwriteMode.Fail)]
    [InlineData(OverwriteMode.Rename)]
    public void AnotherEntryOfTheSameArchiveIsNeverReplacedWhateverTheMode(OverwriteMode mode)
    {
        using var t = new TempDir();
        string archive = Write(P(t, "a.osa"), [("Notes.txt", Data("first")), ("notes.txt", Data("second"))]);
        var replaced = new List<string>();
        var res = Extract(archive, P(t, "out"), new ExtractOptions { Overwrite = mode, NameComparer = StringComparer.OrdinalIgnoreCase, BeforeReplace = replaced.Add });
        Assert.Equal((2, 0, 1), (res.FilesOk, res.FilesFailed, res.Renamed));
        Assert.Empty(replaced);
        Assert.Equal(new Dictionary<string, string> { ["Notes.txt"] = "first", ["notes (2).txt"] = "second" }, Tree(P(t, "out")));
    }

    [Fact]
    public void AFileAndAFolderOfTheSameNameBothGetAPlace()
    {
        using var t = new TempDir();
        // A file "x" and a file inside a folder "x"; then a folder "D" (made for D/z) and a file "d" (case-insensitive).
        string archive = Write(P(t, "a.osa"), [("D/z.txt", Data("in D")), ("d", Data("file d")), ("x", Data("file x")), ("x/y.txt", Data("in x"))]);
        var res = Extract(archive, P(t, "out"), new ExtractOptions { NameComparer = StringComparer.OrdinalIgnoreCase });
        Assert.Equal(4, res.FilesOk);
        Assert.Equal(2, res.Renamed);
        Assert.Equal(new Dictionary<string, string>
        {
            ["D/z.txt"] = "in D", ["d (2)"] = "file d", ["x"] = "file x", ["x (2)/y.txt"] = "in x",
        }, Tree(P(t, "out")));
    }

    [Theory]
    [InlineData(OverwriteMode.Skip)]
    [InlineData(OverwriteMode.Rename)]
    [InlineData(OverwriteMode.Overwrite)]
    public void AFileAnEarlierPassWroteIsNeverReplacedOrSkipped(OverwriteMode mode)
    {
        using var t = new TempDir();
        string archive = Write(P(t, "a.osa"), [("docs", null), ("docs/f.txt", Data("from the archive"))]);
        string dest = P(t, "out");
        Directory.CreateDirectory(Path.Combine(dest, "docs"));
        File.WriteAllText(Path.Combine(dest, "docs", "f.txt"), "the first pass wrote this");
        var replaced = new List<string>();
        // The earlier pass's path in another case: the run's name comparison decides that it is the same name.
        var res = Extract(archive, dest, new ExtractOptions
        {
            Overwrite = mode, NameComparer = StringComparer.OrdinalIgnoreCase, BeforeReplace = replaced.Add,
            WrittenEarlier = [Path.Combine(dest, "DOCS", "F.TXT")],
        });
        Assert.Equal((1, 0, 1), (res.FilesOk, res.FilesSkipped, res.Renamed));
        Assert.Empty(replaced);
        Assert.Equal("the first pass wrote this", File.ReadAllText(Path.Combine(dest, "docs", "f.txt")));
        Assert.Equal("from the archive", File.ReadAllText(Path.Combine(dest, "docs", "f (2).txt")));
    }

    [Fact]
    public void AFileReachedUnderAnotherSpellingIsNeverReplaced()
    {
        using var t = new TempDir();
        string dest = P(t, "out");
        Directory.CreateDirectory(dest);
        string existing = Path.Combine(dest, "x.txt");
        File.WriteAllText(existing, "the user's");
        Assert.False(TargetNames.SpelledOtherwise(dest, "x.txt", existing)); // spelled as resolved

        var plain = new TargetNames(dest, StringComparer.Ordinal, [existing], null).Plan(existing, false, OverwriteMode.Overwrite);
        Assert.Equal((existing, true), (plain.Path, plain.Replaces));
        // Reached under another spelling (as a short 8.3 name resolves to the long one): a free name, never the file.
        var other = new TargetNames(dest, StringComparer.Ordinal, [existing], null).Plan(existing, false, OverwriteMode.Overwrite, otherSpelling: true);
        Assert.Equal((Path.Combine(dest, "x (2).txt"), false, true), (other.Path, other.Replaces, other.Renamed));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetShortPathNameW(string longPath, char[] shortPath, int size);

    [Fact]
    public void AnEntryNamedByAShortNameNeverReplacesTheFileUnderItsLongName()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Short 8.3 names exist on Windows only.");
        using var t = new TempDir();
        string dest = P(t, "out"), folder = Path.Combine(dest, "Long Folder Name");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "x.txt"), "the user's");
        var buffer = new char[1024];
        int n = GetShortPathNameW(folder, buffer, buffer.Length);
        string shortName = Path.GetFileName(new string(buffer, 0, n));
        Assert.SkipWhen(n == 0 || shortName == "Long Folder Name", "This volume makes no short 8.3 names.");

        string archive = Write(P(t, "a.osa"), [($"{shortName}/x.txt", Data("from the archive"))]);
        var replaced = new List<string>();
        var res = Extract(archive, dest, new ExtractOptions { Overwrite = OverwriteMode.Overwrite, BeforeReplace = replaced.Add });
        Assert.Equal(1, res.FilesOk);
        Assert.Empty(replaced);
        Assert.Equal("the user's", File.ReadAllText(Path.Combine(folder, "x.txt")));
        Assert.Equal("from the archive", File.ReadAllText(Path.Combine(folder, "x (2).txt")));
    }

    [Fact]
    public void ThousandsOfNamesThatDifferOnlyInCaseEachGetTheirOwnFreeName()
    {
        using var t = new TempDir();
        const string word = "abcdefghijkl";
        var names = Enumerable.Range(0, 1 << word.Length)
            .Select(m => new string([.. word.Select((c, i) => (m & (1 << i)) != 0 ? char.ToUpperInvariant(c) : c)]) + ".txt")
            .Order(StringComparer.Ordinal).ToList();
        string archive = Write(P(t, "a.osa"), names.Select(n => (n, (byte[]?)Data(n))));
        var sw = Stopwatch.StartNew();
        var res = Extract(archive, P(t, "out"), new ExtractOptions { NameComparer = StringComparer.OrdinalIgnoreCase });
        Assert.Equal((names.Count, names.Count - 1), (res.FilesOk, res.Renamed));
        var tree = Tree(P(t, "out"));
        Assert.Equal(names.Count, tree.Count);
        Assert.Equal(names[0], tree[names[0]]);
        for (int i = 1; i < names.Count; i++) Assert.Equal(names[i], tree[$"{Path.GetFileNameWithoutExtension(names[i])} ({i + 1}).txt"]);
        TestContext.Current.TestOutputHelper?.WriteLine($"{names.Count:N0} names planned and written in {sw.ElapsedMilliseconds:N0} ms");
    }

    // ------------------------------------------------------------------ replacing

    [Fact]
    public void BeforeReplaceIsCalledBeforeEachReplacementAndAFailureKeepsTheOriginal()
    {
        using var t = new TempDir();
        string archive = Write(P(t, "a.osa"), [("keep.txt", Data("new keep")), ("swap.txt", Data("new swap"))]);
        string dest = P(t, "out");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "old keep");
        File.WriteAllText(Path.Combine(dest, "swap.txt"), "old swap");
        var seen = new ConcurrentDictionary<string, string>();
        var res = Extract(archive, dest, new ExtractOptions
        {
            Overwrite = OverwriteMode.Overwrite,
            BeforeReplace = p =>
            {
                seen[Path.GetFileName(p)] = File.ReadAllText(p); // still the old file when asked
                if (p.EndsWith("keep.txt", StringComparison.Ordinal)) throw new IOException("The Recycle Bin refused it.");
            },
        });
        Assert.Equal(new Dictionary<string, string> { ["keep.txt"] = "old keep", ["swap.txt"] = "old swap" }, seen.OrderBy(kv => kv.Key).ToDictionary());
        Assert.Equal((1, 1), (res.FilesOk, res.FilesFailed));
        Assert.Contains(res.Errors, e => e.Contains("The Recycle Bin refused it.", StringComparison.Ordinal));
        Assert.Equal(new Dictionary<string, string> { ["keep.txt"] = "old keep", ["swap.txt"] = "new swap" }, Tree(dest));
    }

    // ------------------------------------------------------------------ links in the destination

    internal static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void NothingIsWrittenThroughASymbolicLinkAlreadyInTheDestination()
    {
        using var t = new TempDir();
        string archive = Write(P(t, "a.osa"), [("docs", null), ("docs/a.txt", Data("a")), ("docs/sub/b.txt", Data("b")), ("ok.txt", Data("ok"))]);
        string dest = P(t, "out"), outside = P(t, "outside");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(outside);
        if (!TryLink(Path.Combine(dest, "docs"), outside)) Assert.Skip("Symbolic links cannot be created here (Windows needs Developer Mode or administrator rights).");

        var res = Extract(archive, dest);
        Assert.Equal(1, res.FilesOk);
        Assert.Equal(3, res.SkippedUnsafe); // the folder and both files below it
        Assert.Equal(0, res.FilesFailed);
        Assert.All(res.Errors, e => Assert.EndsWith("a link in the destination points elsewhere", e));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.Equal("ok", File.ReadAllText(Path.Combine(dest, "ok.txt")));
    }

    [Fact]
    public void ALinkDeeperInTheDestinationIsFoundToo()
    {
        using var t = new TempDir();
        string archive = Write(P(t, "a.osa"), [("docs/a.txt", Data("a")), ("docs/sub/b.txt", Data("b"))]);
        string dest = P(t, "out"), outside = P(t, "outside");
        Directory.CreateDirectory(Path.Combine(dest, "docs"));
        Directory.CreateDirectory(outside);
        if (!TryLink(Path.Combine(dest, "docs", "sub"), outside)) Assert.Skip("Symbolic links cannot be created here (Windows needs Developer Mode or administrator rights).");

        var res = Extract(archive, dest);
        Assert.Equal((1, 1), (res.FilesOk, res.SkippedUnsafe));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    private static bool TryFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void APartFileReplacedByALinkWhileItIsWrittenIsNeverWrittenThrough()
    {
        using var t = new TempDir();
        var big = new byte[150_000];
        new Random(5).NextBytes(big);
        string archive = Write(P(t, "a.osa"), [("big.bin", big)], blockSize: 64 << 10); // three blocks
        string dest = P(t, "out"), victim = P(t, "victim.txt");
        File.WriteAllText(victim, "precious");
        bool swapped = false;
        var progress = new SyncProgress(_ =>
        {
            if (swapped) return;
            swapped = true;
            string part = Directory.GetFiles(dest, "*.osmium-part-*").Single(); // after the first block
            File.Delete(part);
            if (!TryFileLink(part, victim)) Assert.Skip("Symbolic links cannot be created here (Windows needs Developer Mode or administrator rights).");
        });
        ExtractResult res;
        using (var r = OsaReader.Open(archive))
            res = Extractor.Extract(r, dest, null, new ExtractOptions { MaxParallelBlocks = 1 }, progress, Ct);
        Assert.True(swapped);
        Assert.Equal("precious", File.ReadAllText(victim));
        Assert.Equal((0, 1), (res.FilesOk, res.FilesFailed));
        Assert.Contains(res.Errors, e => e.Contains("replaced by a link", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(dest, "big.bin")));
        Assert.Empty(Directory.GetFileSystemEntries(dest));
    }

    [Fact]
    public void AFolderThatBecomesALinkWhileFilesAreWrittenIsNeverWrittenThrough()
    {
        using var t = new TempDir();
        var big = new byte[150_000];
        new Random(6).NextBytes(big);
        // "a-big.bin" fills the first blocks; "docs/late.txt" comes in the last one.
        string archive = Write(P(t, "a.osa"), [("a-big.bin", big), ("docs/late.txt", Data("late"))], blockSize: 64 << 10);
        string dest = P(t, "out"), outside = P(t, "outside");
        Directory.CreateDirectory(Path.Combine(dest, "docs"));
        Directory.CreateDirectory(outside);
        bool swapped = false;
        var progress = new SyncProgress(_ =>
        {
            if (swapped) return;
            swapped = true;
            Directory.Delete(Path.Combine(dest, "docs"));
            if (!TryLink(Path.Combine(dest, "docs"), outside)) Assert.Skip("Symbolic links cannot be created here (Windows needs Developer Mode or administrator rights).");
        });
        ExtractResult res;
        using (var r = OsaReader.Open(archive))
            res = Extractor.Extract(r, dest, null, new ExtractOptions { MaxParallelBlocks = 1 }, progress, Ct);
        Assert.True(swapped);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.Equal((1, 1), (res.FilesOk, res.FilesFailed));
        Assert.Contains(res.Errors, e => e.StartsWith("docs/late.txt: ", StringComparison.Ordinal) && e.Contains(TargetNames.LinkRefused, StringComparison.Ordinal));
    }

    [Fact]
    public void TheDestinationItselfMayBeALinkTheUserChose()
    {
        using var t = new TempDir();
        string archive = Write(P(t, "a.osa"), [("docs/a.txt", Data("a"))]);
        string real = P(t, "real"), dest = P(t, "via-link");
        Directory.CreateDirectory(real);
        if (!TryLink(dest, real)) Assert.Skip("Symbolic links cannot be created here (Windows needs Developer Mode or administrator rights).");
        Assert.Equal(1, Extract(archive, dest).FilesOk);
        Assert.Equal("a", File.ReadAllText(Path.Combine(real, "docs", "a.txt")));
    }

    [Fact]
    public void NothingIsWrittenThroughAJunctionAlreadyInTheDestination()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions exist on Windows only; the symbolic link tests cover the other systems.");
        using var t = new TempDir();
        string archive = Write(P(t, "a.osa"), [("docs/a.txt", Data("a")), ("ok.txt", Data("ok"))]);
        string dest = P(t, "out"), outside = P(t, "outside");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(outside);
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", Path.Combine(dest, "docs"), outside }, UseShellExecute = false, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);

        var res = Extract(archive, dest);
        Assert.Equal((1, 1), (res.FilesOk, res.SkippedUnsafe));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    // ------------------------------------------------------------------ folders

    [Fact]
    public void AFolderThatWasAlreadyThereKeepsItsTimeAndAttributes()
    {
        using var t = new TempDir();
        string archive = Write(P(t, "a.osa"), [("old", null), ("old/a.txt", Data("a")), ("new", null), ("new/b.txt", Data("b"))],
            folderAttributes: FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
        string dest = P(t, "out");
        Directory.CreateDirectory(Path.Combine(dest, "old"));
        var before = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(Path.Combine(dest, "old"), before);
        var attributesBefore = File.GetAttributes(Path.Combine(dest, "old"));

        Assert.Equal(2, Extract(archive, dest).FilesOk);
        // Writing a file into it moves its time to now (the file system does that); the archive's time is never set.
        Assert.NotEqual(When, Directory.GetLastWriteTimeUtc(Path.Combine(dest, "old")));
        Assert.True(Directory.GetLastWriteTimeUtc(Path.Combine(dest, "old")) >= before);
        Assert.Equal(attributesBefore, File.GetAttributes(Path.Combine(dest, "old")));
        Assert.Equal(When, Directory.GetLastWriteTimeUtc(Path.Combine(dest, "new"))); // created by this run: the archive's time
        if (OperatingSystem.IsWindows())
        {
            var made = File.GetAttributes(Path.Combine(dest, "new"));
            Assert.True(made.HasFlag(FileAttributes.Hidden));
            Assert.False(made.HasFlag(FileAttributes.System) || made.HasFlag(FileAttributes.ReadOnly), "System and ReadOnly are never set on a folder");
        }
    }

    // ------------------------------------------------------------------ cancel

    [Fact]
    public void CancelingLeavesNoPartialFileAndNoEmptyNewFolder()
    {
        using var t = new TempDir();
        var rnd = new Random(9);
        byte[] Random(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
        // 64 KiB blocks: "first" fits in block 0, "spans" runs over blocks 0-2, the rest come later.
        string archive = Write(P(t, "a.osa"),
            [("done/first.bin", Random(20_000)), ("spans/big.bin", Random(150_000)), ("late/x.bin", Random(70_000)), ("late/deep/y.bin", Random(70_000))],
            blockSize: 64 << 10);
        string dest = P(t, "out");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "mine.txt"), "the user's own file");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new SyncProgress(p => { if (p.FilesDone >= 1) cts.Cancel(); });
        using (var r = OsaReader.Open(archive))
            Assert.ThrowsAny<OperationCanceledException>(() => Extractor.Extract(r, dest, null, new ExtractOptions { MaxParallelBlocks = 1 }, progress, cts.Token));

        Assert.Empty(Parts(dest));
        Assert.Equal("the user's own file", File.ReadAllText(Path.Combine(dest, "mine.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "done", "first.bin")), "a finished file stays");
        Assert.DoesNotContain(Directory.GetDirectories(dest, "*", SearchOption.AllDirectories), d => !Directory.EnumerateFileSystemEntries(d).Any());
        foreach (var (name, text) in Tree(dest).Where(kv => kv.Key != "mine.txt")) Assert.NotEmpty(text);
    }

    [Fact]
    public void ADestinationTheRunCreatedIsRemovedWhenCanceledBeforeAnythingFinished()
    {
        using var t = new TempDir();
        string archive = Write(P(t, "a.osa"), [("a/b.bin", new byte[100_000])], blockSize: 32 << 10);
        string dest = P(t, "new", "out");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new SyncProgress(_ => cts.Cancel());
        using (var r = OsaReader.Open(archive))
            Assert.ThrowsAny<OperationCanceledException>(() => Extractor.Extract(r, dest, null, new ExtractOptions { MaxParallelBlocks = 1 }, progress, cts.Token));
        Assert.False(Directory.Exists(P(t, "new")));
    }

    private sealed class SyncProgress(Action<ExtractProgress> report) : IProgress<ExtractProgress>
    {
        public void Report(ExtractProgress value) => report(value);
    }

    // ------------------------------------------------------------------ memory

    [Fact]
    public void MemoryIsReservedForTheLargestStageOfABlock()
    {
        using var t = new TempDir();
        string path = P(t, "a.osa");
        // A crafted descriptor: the block is 100 bytes, but its inner stage claims 50,000 (as an expanding filter could).
        var payload = new byte[50_000];
        var desc = new BlockDescriptor(PipelineSpec.Parse("x86+store", MethodRegistry.Default), [100, 50_000]);
        var data = new byte[100];
        using (var w = new OsaWriter(path, new OsaWriterOptions { SpareThreshold = long.MaxValue }))
        {
            int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, decodeMemory: 0);
            w.Finish([new ArchiveEntry { Path = "x.bin", Kind = EntryKind.File, Size = 100, Segments = [new Segment(frame, 0, 100)], Hash = XxHash128.HashToUInt128(data) }]);
            w.Commit(overwrite: false);
        }
        var reserved = new ConcurrentDictionary<int, long>();
        var res = Extract(path, P(t, "out"), new ExtractOptions { OnReserve = (f, n) => reserved[f] = n });
        Assert.True(reserved[0] >= 2 * 50_000, $"reserved {reserved[0]:N0} bytes for a 50,000-byte stage");
        Assert.Equal(1, res.FilesFailed); // the block does not decode, as its descriptor lies
        Assert.Empty(Parts(P(t, "out")));

        // More than the budget: the reservation is the whole budget, so the block decodes alone.
        var small = new ConcurrentDictionary<int, long>();
        Extract(path, P(t, "out2"), new ExtractOptions { MemoryBudget = 60_000, OnReserve = (f, n) => small[f] = n });
        Assert.Equal(60_000, small[0]);
    }

    [Fact]
    public void ABlockWhosePipelineIsNotTheOneTheFileListGivesIsRefusedBeforeItIsDecoded()
    {
        using var t = new TempDir();
        var files = new Dictionary<string, byte[]> { ["x.bin"] = new byte[5000] };
        // The file list says "store" (little memory); the frame itself is zstd: memory would be reserved for the one
        // and spent on the other.
        string path = SpareArchives.Craft(t.Sub("a.osa"), files, _ => null, tail: false, framePipeline: "zstd(level=3)",
            indexDescriptor: d => new BlockDescriptor(PipelineSpec.Parse("store", MethodRegistry.Default), [d.RawSize]));
        var res = Extract(path, P(t, "out"));
        Assert.Equal((0, 1), (res.FilesOk, res.FilesFailed));
        Assert.Contains(res.Errors, e => e.Contains("does not match the file list", StringComparison.Ordinal));
        using var r = OsaReader.Open(path);
        Assert.Contains(Extractor.Test(r, null, Ct).Errors, e => e.Contains("does not match the file list", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ Mark-of-the-Web

    private const string Zone = "[ZoneTransfer]\r\nZoneId=3\r\n";

    [Fact]
    public void TheMarkOfTheWebGoesOnThePartFileBeforeTheFileAppearsOrGetsItsAttributes()
    {
        using var t = new TempDir();
        string archive = Write(P(t, "a.osa"), [("setup.exe", Data("MZ not really a program")), ("readme.txt", Data("read me"))],
            fileAttributes: FileAttributes.ReadOnly);
        var calls = new ConcurrentQueue<(string Part, bool FinalExists, bool ReadOnly)>();
        string? Record(string p, string z)
        {
            Assert.Equal(Zone, z);
            int at = p.IndexOf(".osmium-part-", StringComparison.Ordinal);
            calls.Enqueue((p, at < 0 || File.Exists(p[..at]), File.GetAttributes(p).HasFlag(FileAttributes.ReadOnly)));
            return null;
        }
        var res = Extract(archive, P(t, "out"), new ExtractOptions { Zone = Zone, ZoneWriter = Record });
        try
        {
            Assert.Empty(res.Warnings);
            Assert.Equal(2, calls.Count);
            Assert.All(calls, c => Assert.Contains(".osmium-part-", c.Part));
            Assert.All(calls, c => Assert.False(c.FinalExists, "the file appeared before it was marked"));
            Assert.All(calls, c => Assert.False(c.ReadOnly, "ReadOnly was applied before the mark"));
            if (OperatingSystem.IsWindows()) Assert.True(File.GetAttributes(P(t, "out", "setup.exe")).HasFlag(FileAttributes.ReadOnly));

            // A mark that cannot be written is a warning for that file, never a silent success.
            var refused = Extract(archive, P(t, "out2"), new ExtractOptions { Zone = Zone, ZoneWriter = (_, _) => "access is denied" });
            Assert.Equal(2, refused.FilesOk);
            Assert.Equal(2, refused.Warnings.Count);
            Assert.All(refused.Warnings, w => Assert.Contains("Mark-of-the-Web", w));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(t.Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        }
    }

    /// <summary>A zoned archive holding a read-only program, built the normal way (Windows, NTFS).</summary>
    internal static async Task<string> ZonedArchiveWithAReadOnlyProgram(TempDir t, CancellationToken ct)
    {
        string input = Path.Combine(t.Path, "in");
        Directory.CreateDirectory(input);
        string exe = Path.Combine(input, "setup.exe");
        File.WriteAllText(exe, "MZ: text standing in for a downloaded program");
        File.SetAttributes(exe, FileAttributes.ReadOnly);
        var plan = await new Planner(new PlanOptions { Profile = Profile.ByName("fast"), MemoryBudget = 1L << 30 }).BuildAsync(Scanner.Scan([input], ct: ct), ct);
        string archive = (await ArchiveBuilder.BuildAsync(plan, new BuildOptions { OutputPath = Path.Combine(t.Path, "zoned.osa"), MemoryBudget = 1L << 30 }, ct)).Path;
        File.WriteAllText(archive + ":Zone.Identifier", Zone);
        return archive;
    }

    [Fact]
    public async Task AReadOnlyProgramFromADownloadedArchiveKeepsTheMarkOnARealStream()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "needs NTFS alternate data streams");
        using var t = new TempDir();
        try
        {
            string archive = await ZonedArchiveWithAReadOnlyProgram(t, Ct);
            using var r = OsaReader.Open(archive);
            var res = Extractor.Extract(r, t.Sub("out"), null, null, null, Ct);
            Assert.Empty(res.Warnings);
            string exe = Path.Combine(t.Sub("out"), "in", "setup.exe");
            Assert.True(File.GetAttributes(exe).HasFlag(FileAttributes.ReadOnly));
            Assert.Contains("ZoneId=3", File.ReadAllText(exe + ":Zone.Identifier"));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(t.Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        }
    }
}
