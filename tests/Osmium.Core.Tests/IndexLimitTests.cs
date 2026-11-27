using Osmium.Core.Archives;
using Osmium.Core.Container;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests;

/// <summary>A file list is checked as it is decoded: every count against the bytes that are left and the limits, and
/// every path against the limits, before anything is allocated for it.</summary>
public class IndexLimitTests
{
    private static readonly MethodRegistry Reg = MethodRegistry.Default;

    /// <summary>A raw index with no blocks, these entry records (already wrapped) and no metadata.</summary>
    private static byte[] Index(ulong declaredEntries, IEnumerable<byte[]> entries, ulong blocks = 0)
    {
        var w = new BlobWriter().U(ArchiveIndex.Version).U(blocks).U(declaredEntries);
        foreach (var e in entries) w.Bytes(e);
        return w.U(0).ToArray();
    }

    /// <summary>A directory entry: front-coded path (shared bytes of the previous path, then a suffix), no times.</summary>
    private static byte[] Folder(int shared, ReadOnlySpan<byte> suffix) =>
        new BlobWriter().U((ulong)shared).U((ulong)suffix.Length).Raw(suffix).B((byte)EntryKind.Directory).U(0).ToArray();

    [Fact]
    public void ACountThatTheBytesCannotHoldIsRefusedBeforeTheLoop()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<CorruptDataException>(() => ArchiveIndex.Deserialize(Index(1 << 26, [Folder(0, "a"u8)]), 0, Reg));
        Assert.Equal("The file list declares more entries than it holds.", e.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
        Assert.Equal("The file list declares more blocks than it holds.",
            Assert.Throws<CorruptDataException>(() => ArchiveIndex.Deserialize(Index(0, [], blocks: 1 << 24), 0, Reg)).Message);
    }

    [Fact]
    public void PathsThatShareLongPrefixesCannotMakeUnboundedText()
    {
        // One path of 32,767 bytes, then entries of a few bytes each that keep all but its last four bytes: every one is
        // a new path of 32 KiB. Decoded in full, 10,000 of them would be 330 MB of path text (twice that as strings)
        // from 110 KB of file list.
        static byte[] Tail(int i) => [(byte)('a' + i / 17_576 % 26), (byte)('a' + i / 676 % 26), (byte)('a' + i / 26 % 26), (byte)('a' + i % 26)];
        var entries = new List<byte[]> { Folder(0, [.. Enumerable.Repeat((byte)'d', 32_763), .. Tail(0)]) };
        for (int i = 1; i < 10_000; i++) entries.Add(Folder(32_763, Tail(i)));
        var raw = Index((ulong)entries.Count, entries);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<CorruptDataException>(() => ArchiveIndex.Deserialize(raw, 0, Reg));
        Assert.Equal("The file list holds more path text than Osmium accepts.", e.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 400L << 20, "decoding stopped too late");
    }

    [Fact]
    public void TheEntryLimitIsCheckedBeforeTheEntriesAreRead()
    {
        var raw = Index(1000, Enumerable.Range(0, 1000).Select(i => Folder(0, System.Text.Encoding.UTF8.GetBytes($"f{i:D4}"))));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<EntryLimitException>(() => ArchiveIndex.Deserialize(raw, 0, Reg, new IndexLimits { MaxEntries = 10 }));
        Assert.Equal("This archive holds 1,000 entries, more than the 10 Osmium accepts.", e.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 << 10);
        Assert.Equal(1000, ArchiveIndex.Deserialize(raw, 0, Reg).Entries.Count); // within the format's own limits
    }

    [Fact]
    public void ThePathDepthIsCheckedAsEachPathIsRead()
    {
        var deep = System.Text.Encoding.UTF8.GetBytes(string.Join("/", Enumerable.Repeat("d", 70)));
        var e = Assert.Throws<EntryLimitException>(() => ArchiveIndex.Deserialize(Index(1, [Folder(0, deep)]), 0, Reg, new IndexLimits { MaxPathDepth = 64 }));
        Assert.Equal("A path in this archive is 70 folders deep, more than the 64 Osmium accepts.", e.Message);
    }

    [Fact]
    public void TheArchiveSourceAppliesItsLimitsWhileTheFileListIsRead()
    {
        using var t = new TempDir();
        string path = SpareArchives.Write(t.Sub("a.osa"), SpareArchives.Files(30, 10));
        var limits = ImportLimits.Default with { MaxEntries = 5 };
        var e = Assert.Throws<EntryLimitException>(() => ArchiveSources.Open(path, new ArchiveOpenOptions { Limits = limits }, TestContext.Current.CancellationToken));
        Assert.Equal("This archive holds 31 entries, more than the 5 Osmium accepts.", e.Message);
        Assert.Throws<EntryLimitException>(() => OsaReader.Open(path, new OsaOpenOptions { IndexLimits = new IndexLimits { MaxEntries = 5 } }));
        using (OsaReader.Open(path, new OsaOpenOptions())) { } // the format's own limits
    }
}
