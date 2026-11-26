using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using Osmium.Core.Archives;
using Osmium.Core.Archives.Foreign;
using Osmium.Core.Archives.Foreign.SevenZip;

namespace Osmium.Core.Tests.Foreign;

/// <summary>Small 7z files whose headers declare huge numbers of folders or substreams, or a huge decoded header:
/// refused quickly, in little memory, with a limit exception (never an out-of-memory crash), and cancellable.</summary>
public class SevenZipHostileHeaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>7z's variable-length number.</summary>
    private static byte[] Num(long v)
    {
        for (int n = 0; n < 8; n++)
            if ((ulong)v < 1UL << (7 * (n + 1)))
            {
                byte first = (byte)((0xFF << (8 - n)) & 0xFF | (int)(v >> (8 * n)));
                var b = new byte[n + 1];
                b[0] = first;
                for (int i = 0; i < n; i++) b[1 + i] = (byte)(v >> (8 * i));
                return b;
            }
        return [0xFF, .. BitConverter.GetBytes(v)];
    }

    /// <summary>A header with <paramref name="folders"/> Copy folders (the shape that cost 600 bytes per folder).</summary>
    private static byte[] FoldersHeader(int folders)
    {
        var h = new List<byte> { 0x01, 0x04, 0x07, 0x0B };
        h.AddRange(Num(folders));
        h.Add(0x00);
        for (int i = 0; i < folders; i++) { h.Add(0x01); h.Add(0x00); }
        h.Add(0x0C);
        h.AddRange(new byte[folders]);
        h.AddRange([0x00, 0x00, 0x00]);
        return [.. h];
    }

    /// <summary>A header with one folder of 64 chained Copy coders and <paramref name="subStreams"/> empty substreams.</summary>
    private static byte[] SubStreamsHeader(int subStreams, int coders = 64)
    {
        var h = new List<byte> { 0x01, 0x04, 0x06 };
        h.AddRange(Num(0)); h.AddRange(Num(1)); h.Add(0x09); h.AddRange(Num(0)); h.Add(0x00);
        h.AddRange([0x07, 0x0B]); h.AddRange(Num(1)); h.Add(0x00);
        h.AddRange(Num(coders));
        for (int i = 0; i < coders; i++) { h.Add(0x01); h.Add(0x00); }
        for (int k = 0; k < coders - 1; k++) { h.AddRange(Num(k + 1)); h.AddRange(Num(k)); }
        h.Add(0x0C);
        for (int i = 0; i < coders; i++) h.AddRange(Num(0));
        h.Add(0x00);
        h.AddRange([0x08, 0x0D]); h.AddRange(Num(subStreams)); h.Add(0x09);
        h.AddRange(new byte[subStreams - 1]);
        h.AddRange([0x00, 0x00, 0x00]);
        return [.. h];
    }

    private static byte[] Signature(long nextOffset, byte[] next)
    {
        var sig = new byte[32];
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 }.CopyTo(sig, 0);
        BinaryPrimitives.WriteInt64LittleEndian(sig.AsSpan(12), nextOffset);
        BinaryPrimitives.WriteInt64LittleEndian(sig.AsSpan(20), next.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(28), Crc32.HashToUInt32(next));
        BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(8), Crc32.HashToUInt32(sig.AsSpan(12, 20)));
        return sig;
    }

    /// <summary>The header stored as it is.</summary>
    private static byte[] Raw(byte[] header) => [.. Signature(0, header), .. header];

    /// <summary>The header packed with LZMA (a small file that decodes to it), or a declared size with no real data.</summary>
    private static byte[] Packed(byte[] header, long? declaredSize = null)
    {
        byte[] props, comp;
        if (declaredSize is null)
        {
            var enc = new SevenZip.Compression.LZMA.Encoder();
            enc.SetCoderProperties([SevenZip.CoderPropID.DictionarySize], [1 << 20]);
            var ps = new MemoryStream();
            enc.WriteCoderProperties(ps);
            props = ps.ToArray();
            var o = new MemoryStream();
            enc.Code(new MemoryStream(header), o, header.Length, -1, null);
            comp = o.ToArray();
        }
        else
        {
            props = [0x5D, 0, 0, 0x10, 0];
            comp = new byte[16];
        }
        var enc7 = new List<byte> { 0x17, 0x06 };
        enc7.AddRange(Num(0)); enc7.AddRange(Num(1)); enc7.Add(0x09); enc7.AddRange(Num(comp.Length)); enc7.Add(0x00);
        enc7.AddRange([0x07, 0x0B]); enc7.AddRange(Num(1)); enc7.Add(0x00);
        enc7.AddRange([0x01, 0x23, 0x03, 0x01, 0x01]); enc7.AddRange(Num(5)); enc7.AddRange(props);
        enc7.Add(0x0C); enc7.AddRange(Num(declaredSize ?? header.Length));
        enc7.AddRange([0x0A, 0x01]); enc7.AddRange(BitConverter.GetBytes(Crc32.HashToUInt32(header)));
        enc7.AddRange([0x00, 0x00]);
        byte[] next = [.. enc7];
        return [.. Signature(comp.Length, next), .. comp, .. next];
    }

    private static Exception Refused(string path, ImportLimits limits, TimeSpan within)
    {
        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.ThrowsAny<Exception>(() => ArchiveSources.Open(path, new ArchiveOpenOptions { Limits = limits }, Ct).Dispose());
        Assert.True(sw.Elapsed < within, $"took {sw.Elapsed}");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 200L << 20, $"allocated {allocated:N0} bytes");
        Assert.True(e is EntryLimitException or Osmium.Core.Pipeline.CorruptDataException, e.ToString());
        return e;
    }

    [Fact]
    public void ManyFoldersInASmallPackedHeaderAreChargedBeforeTheyAreMade()
    {
        using var t = new TempDir();
        var file = Packed(FoldersHeader(300_000));
        Assert.True(file.Length < 16 << 10, $"{file.Length} bytes");
        var e = Refused(ForeignData.Write(t, "folders.7z", file), ImportLimits.Default with { MemoryBudgetBytes = 128 << 20 }, TimeSpan.FromSeconds(10));
        Assert.IsType<EntryLimitException>(e);
    }

    [Fact]
    public void MoreFoldersThanEntriesAreRefused()
    {
        using var t = new TempDir();
        var e = Refused(ForeignData.Write(t, "folders.7z", Packed(FoldersHeader(20_000))), ImportLimits.Default with { MaxEntries = 10_000 }, TimeSpan.FromSeconds(10));
        Assert.IsType<EntryLimitException>(e);
    }

    [Theory]
    [InlineData(16_776_928L)]
    [InlineData(400L << 20)]
    public void APackedHeaderDeclaringFarMoreThanItsDataAllocatesOnlyWhatComes(long declared)
    {
        // Within the cap the header is decoded; it is grown as it comes, so the small data fails before much is made.
        using var t = new TempDir();
        var e = Refused(ForeignData.Write(t, "big-header.7z", Packed([0x01, 0x00], declared)), ImportLimits.Default with { MemoryBudgetBytes = 4L << 30 }, TimeSpan.FromSeconds(2));
        Assert.IsType<Osmium.Core.Pipeline.CorruptDataException>(e);
    }

    [Fact]
    public void APackedHeaderPastAQuarterOfTheBudgetIsRefusedBeforeDecoding()
    {
        using var t = new TempDir();
        var e = Refused(ForeignData.Write(t, "big-header.7z", Packed([0x01, 0x00], 17L << 20)), ImportLimits.Default with { MemoryBudgetBytes = 64L << 20 }, TimeSpan.FromSeconds(2));
        Assert.IsType<EntryLimitException>(e);
        Assert.Equal(1L << 30, SevenZipHeader.MaxHeader(ImportLimits.Default with { MemoryBudgetBytes = 16L << 30 }));
    }

    [Fact]
    public void AFileListOfManyEmptyFilesPackedFarSmallerThanItselfOpens()
    {
        // As 7-Zip writes them: 10,000 empty files at ordinary paths pack into a few KB, far less than 1/256 of the list.
        using var t = new TempDir();
        static string Name(int i) => $"project/module_{i / 100:D3}/src/main/resources/placeholder_file_number_{i:D5}.txt";
        byte[] archive = SevenZipCraft.Build([], SevenZipCraft.Lzma, emptyFiles: 10_000, name: Name);
        string path = ForeignData.Write(t, "empty-files.7z", archive);
        long listing = 10_000L * 2 * (Name(0).Length + 1);
        Assert.True(listing > 256 * archive.Length, $"{archive.Length:N0} bytes for a {listing:N0}-byte list");
        using var s = ArchiveSources.Open(path, new ArchiveOpenOptions { Limits = ImportLimits.Default with { MemoryBudgetBytes = 512L << 20 } }, Ct);
        Assert.Equal(10_000, s.Items.Count(i => !i.IsDirectory));
        Assert.Equal(Name(9_999), s.Items[^1].Path);
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal((10_000, 0), (r.FilesOk, r.LeftOut));
        Assert.True(File.Exists(Path.Combine([t.Sub("out"), .. Name(4_321).Split('/')])));
    }

    [Fact]
    public void MoreSubstreamsThanEntriesAreRefusedBeforeTheyAreRead()
    {
        using var t = new TempDir();
        var e = Refused(ForeignData.Write(t, "subs.7z", Packed(SubStreamsHeader(20_000, coders: 1))), ImportLimits.Default with { MaxEntries = 10_000 }, TimeSpan.FromSeconds(10));
        Assert.IsType<EntryLimitException>(e);
        var raw = Refused(ForeignData.Write(t, "subs-raw.7z", Raw(SubStreamsHeader(1_200_000, coders: 1))), ImportLimits.Default, TimeSpan.FromSeconds(10));
        Assert.IsType<EntryLimitException>(raw);
    }

    [Fact]
    public void ManySubstreamsOfAFolderWithManyCodersReadInLinearTime()
    {
        using var t = new TempDir();
        // The folder's result stream used to be worked out again for every substream (64 coders: about 4,000 steps).
        var e = Refused(ForeignData.Write(t, "subs64.7z", Raw(SubStreamsHeader(300_000))), ImportLimits.Default, TimeSpan.FromSeconds(5));
        Assert.Contains("more data streams than files", e.Message);
    }

    [Fact]
    public void TheHeaderReadObservesCancellation()
    {
        using var t = new TempDir();
        string path = ForeignData.Write(t, "folders.7z", Raw(FoldersHeader(300_000)));
        using var handle = File.OpenHandle(path);
        using var file = new FileSlice(handle, 0, RandomAccess.GetLength(handle));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SevenZipHeader.Read(file, ImportLimits.Default, cts.Token, (_, _, _) => []));
    }
}
