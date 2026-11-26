using System.Formats.Tar;
using System.IO.Hashing;
using System.Text;
using Osmium.Core.Archives;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Foreign;

/// <summary>TAR (plain and compressed) and single compressed files: listing, extraction, checks, concatenation,
/// special TAR entries, limits and progress.</summary>
public class TarAndStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly (string, byte[])[] Tree =
    [
        ("top/", []),
        ("top/empty/", []),
        ("top/zero.bin", []),
        ("top/ünïcödé — файл.txt", ForeignData.Text(2000, 3)),
        ("top/sub/a.txt", ForeignData.Text(70_000, 4)),
        ("top/sub/b.txt", ForeignData.Text(10, 5)),
    ];

    private static IArchiveSource Open(string path, ArchiveOpenOptions? options = null) => ArchiveSources.Open(path, options, Ct);

    private static List<string> FilesUnder(string root) => !Directory.Exists(root) ? []
        : Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    private static void AssertTree(string root)
    {
        Assert.Equal(Tree.Where(t => !t.Item1.EndsWith('/')).Select(t => t.Item1).Order(StringComparer.Ordinal), FilesUnder(root));
        foreach (var (name, data) in Tree)
        {
            var full = Path.Combine([root, .. name.TrimEnd('/').Split('/')]);
            if (name.EndsWith('/')) Assert.True(Directory.Exists(full), name);
            else Assert.Equal(data, File.ReadAllBytes(full));
        }
    }

    public static TheoryData<string> Compressions() => ["tar", "tar.gz", "tar.bz2", "tar.zst"];

    private static byte[] Compress(string kind, byte[] tar) => kind switch
    {
        "tar" => tar,
        "tar.gz" => ForeignData.GZip(tar),
        "tar.bz2" => ForeignData.BZip2(tar),
        "tar.zst" => ForeignData.Zstd(tar),
        _ => throw new ArgumentException(kind),
    };

    [Theory]
    [MemberData(nameof(Compressions))]
    public void RoundTripsATreeUnderAWrongName(string kind)
    {
        using var t = new TempDir();
        string path = ForeignData.Write(t, "renamed.zip", Compress(kind, ForeignData.Tar(Tree)));
        using var s = Open(path);
        Assert.Equal(ArchiveKind.Tar, s.Kind);
        Assert.Equal(kind == "tar" ? "TAR" : "TAR + " + kind switch { "tar.gz" => "gzip", "tar.bz2" => "bzip2", _ => "Zstandard" }, s.FormatName);
        Assert.True(s.ItemsComplete);
        Assert.Equal(Tree.Select(e => e.Item1.TrimEnd('/')), s.Items.Select(i => i.Path));
        Assert.Equal(Tree.Sum(e => e.Item2.Length), s.TotalSize);
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal(4, r.FilesOk);
        Assert.Equal(4, r.Unverified);
        Assert.Equal(0, r.LeftOut);
        AssertTree(t.Sub("out"));
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(t.Sub("out"), "top", "sub", "a.txt")));
        var test = s.Test(null, null, Ct);
        Assert.Equal((4, 4, 0), (test.FilesOk, test.Unverified, test.LeftOut));
        Assert.Contains(test.Warnings, w => w.StartsWith("4 file(s) have no stored checksum; the archive's structure was read completely", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCommittedTarXzFixtureExtracts()
    {
        using var t = new TempDir();
        using var s = Open(ForeignData.Fixture("xz", "tree.tar.xz"));
        Assert.Equal("TAR + xz", s.FormatName);
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal(3, r.FilesOk);
        foreach (var (name, data) in ForeignData.FixtureTree)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine([t.Sub("out"), .. name.Split('/')])));
        Assert.True(Directory.Exists(Path.Combine(t.Sub("out"), "in", "sub", "empty")));
        Assert.Equal(DateTime.UnixEpoch, File.GetLastWriteTimeUtc(Path.Combine(t.Sub("out"), "in", "hello.txt")));
    }

    [Fact]
    public void LinksAreListedAndNeverCreated()
    {
        using var t = new TempDir();
        var ms = new MemoryStream();
        using (var w = new TarWriter(ms, TarEntryFormat.Ustar, leaveOpen: true))
        {
            var file = new UstarTarEntry(TarEntryType.RegularFile, "real.txt") { DataStream = new MemoryStream("real"u8.ToArray()) };
            w.WriteEntry(file);
            w.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "/etc" });
            w.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "link/pwn.txt") { DataStream = new MemoryStream("pwn"u8.ToArray()) });
            w.WriteEntry(new UstarTarEntry(TarEntryType.HardLink, "copy.txt") { LinkName = "real.txt" });
            w.WriteEntry(new UstarTarEntry(TarEntryType.HardLink, "evil.txt") { LinkName = "../x" });
            w.WriteEntry(new UstarTarEntry(TarEntryType.Fifo, "fifo"));
        }
        string path = ForeignData.Write(t, "links.tar", ms.ToArray());
        using var s = Open(path);
        var link = s.Items.Single(i => i.Path == "link");
        Assert.True(link.IsLink);
        Assert.Equal("/etc", link.LinkTarget);
        Assert.True(s.Items.Single(i => i.Path == "fifo").IsSpecial);
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal(3, r.FilesOk); // real.txt, link/pwn.txt (an ordinary folder), copy.txt (a copy)
        Assert.Equal(2, r.SkippedLinks);
        Assert.Equal(1, r.SkippedUnsupported);
        Assert.Equal("real", File.ReadAllText(Path.Combine(t.Sub("out"), "copy.txt")));
        Assert.Null(new DirectoryInfo(Path.Combine(t.Sub("out"), "link")).LinkTarget);
        Assert.Equal(["copy.txt", "link/pwn.txt", "real.txt"], FilesUnder(t.Sub("out")));
    }

    /// <summary>A 512-byte TAR header with a valid checksum.</summary>
    internal static byte[] Header(string name, char type, long size, string magic = "ustar\0", string version = "00", Action<byte[]>? patch = null)
    {
        var h = new byte[512];
        Encoding.UTF8.GetBytes(name).AsSpan(0, Math.Min(100, Encoding.UTF8.GetByteCount(name))).CopyTo(h);
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(h, 100);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(h, 108);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(h, 116);
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0").CopyTo(h, 124);
        Encoding.ASCII.GetBytes("14700000000\0").CopyTo(h, 136);
        h[156] = (byte)type;
        Encoding.ASCII.GetBytes(magic).CopyTo(h, 257);
        Encoding.ASCII.GetBytes(version).CopyTo(h, 263);
        patch?.Invoke(h);
        h.AsSpan(148, 8).Fill((byte)' ');
        long sum = 0;
        foreach (byte b in h) sum += b;
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(h, 148);
        return h;
    }

    internal static byte[] Padded(byte[] data) => [.. data, .. new byte[(512 - data.Length % 512) % 512]];

    internal static byte[] Pax(params (string Key, string Value)[] records)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in records)
        {
            string body = $" {k}={v}\n";
            int len = body.Length + 1;
            while ((len.ToString().Length + body.Length) != len) len = len.ToString().Length + body.Length;
            sb.Append(len).Append(body);
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] FileEntry(string name, string text) => [.. Header(name, '0', Encoding.UTF8.GetByteCount(text)), .. Padded(Encoding.UTF8.GetBytes(text))];

    [Fact]
    public void SparseMultiVolumeAndVolumeEntriesAreRefusedAndTheArchiveContinues()
    {
        using var t = new TempDir();
        var paxSparse = Pax(("GNU.sparse.major", "1"), ("GNU.sparse.minor", "0"), ("GNU.sparse.name", "real.bin"), ("GNU.sparse.realsize", "4096"));
        byte[] tar =
        [
            .. FileEntry("before.txt", "before"),
            .. Header("gnu-sparse.bin", 'S', 512, magic: "ustar  \0", version: ""), .. new byte[512],
            .. Header("PaxHeaders/x", 'x', paxSparse.Length), .. Padded(paxSparse),
            .. Header("GNUSparseFile.0/real.bin", '0', 512), .. new byte[512],
            .. Header("volume", 'V', 0),
            .. Header("continued.bin", 'M', 512), .. new byte[512],
            .. FileEntry("after.txt", "after"),
            .. new byte[1024],
        ];
        string path = ForeignData.Write(t, "special.tar", tar);
        using var s = Open(path);
        Assert.Equal(["before.txt", "gnu-sparse.bin", "real.bin", "volume", "continued.bin", "after.txt"], s.Items.Select(i => i.Path));
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal(2, r.FilesOk);
        Assert.Equal(4, r.SkippedUnsupported);
        Assert.Equal(4, r.LeftOut);
        Assert.Equal(2, r.Errors.Count(e => e.Contains("sparse TAR entries are not supported", StringComparison.Ordinal)));
        Assert.Equal(["after.txt", "before.txt"], FilesUnder(t.Sub("out")));
    }

    [Theory]
    [InlineData('x')]
    [InlineData('g')]
    [InlineData('L')]
    [InlineData('K')]
    public void OversizedMetadataRecordsAreRefusedWithoutAllocatingThem(char type)
    {
        using var t = new TempDir();
        byte[] tar = [.. Header("meta", type, 3L << 30), .. new byte[2048]];
        string path = ForeignData.Write(t, "big-header.tar", tar);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<CorruptDataException>(() => Open(path));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32 << 20);
        Assert.Contains("TAR header too large", e.Message);
    }

    [Fact]
    public void ATruncatedTarDeclaringAHugeFileFailsFast()
    {
        using var t = new TempDir();
        byte[] tar = [.. FileEntry("ok.txt", "ok"), .. Header("huge.bin", '0', 100L << 30), .. new byte[4096]];
        foreach (var (name, data) in new[] { ("huge.tar", tar), ("huge.tar.gz", ForeignData.GZip(tar)) })
        {
            string path = ForeignData.Write(t, name, data);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Throws<CorruptDataException>(() => Open(path));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32 << 20);
            // Streamed without a listing, the file before the damage is extracted and the damage is reported.
            using var s = Open(path, new ArchiveOpenOptions { ListEagerly = false });
            var r = s.Extract(t.Sub("out-" + name), new ArchiveExtractRequest(), null, Ct);
            Assert.Equal(1, r.FilesOk);
            Assert.True(r.FilesFailed >= 1);
            Assert.Contains(r.Errors, e => e.Contains("damaged data", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ListingCanBeCancelled()
    {
        using var t = new TempDir();
        var entries = Enumerable.Range(0, 400).Select(i => ($"f{i}.bin", new byte[256 << 10])).ToArray();
        string path = ForeignData.Write(t, "many.tar.gz", ForeignData.GZip(ForeignData.Tar(entries)));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new SyncProgress<long>(n => { if (n > 0) cts.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ArchiveSources.Open(path, new ArchiveOpenOptions { ListingProgress = progress, Limits = ImportLimits.Default with { AllowLargeExpansion = true } }, cts.Token));
    }

    [Fact]
    public void AStreamedTarListsAsItGoes()
    {
        using var t = new TempDir();
        string path = ForeignData.Write(t, "s.tgz", ForeignData.GZip(ForeignData.Tar(Tree)));
        using var s = Open(path, new ArchiveOpenOptions { ListEagerly = false });
        Assert.False(s.ItemsComplete);
        Assert.Empty(s.Items);
        Assert.Null(s.TotalSize);
        var reports = new List<ExtractProgress>();
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), new SyncProgress<ExtractProgress>(reports.Add), Ct);
        Assert.Equal(4, r.FilesOk);
        AssertTree(t.Sub("out"));
        Assert.NotEmpty(reports);
        Assert.All(reports, p => Assert.Equal((0L, 0), (p.BytesTotal, p.FilesTotal)));
        Assert.True(s.ItemsComplete);
        Assert.Equal(Tree.Length, s.Items.Count);
    }

    // ------------------------------------------------------------ single compressed files

    private static readonly byte[] PartA = ForeignData.Text(300_000, 11), PartB = ForeignData.Text(200_000, 12);

    public static TheoryData<string> StreamKinds() => ["gz", "bz2", "zst"];

    private static byte[] One(string kind, byte[] data) => kind switch
    {
        "gz" => ForeignData.GZip(data),
        "bz2" => ForeignData.BZip2(data),
        _ => ForeignData.Zstd(data),
    };

    private static byte[] ExtractSingle(TempDir t, string path, out ArchiveExtractResult r, string outName = "out")
    {
        using var s = Open(path);
        r = s.Extract(t.Sub(outName), new ArchiveExtractRequest(), null, Ct);
        var files = Directory.Exists(t.Sub(outName)) ? Directory.GetFiles(t.Sub(outName)) : [];
        return files.Length == 1 ? File.ReadAllBytes(files[0]) : [];
    }

    [Theory]
    [MemberData(nameof(StreamKinds))]
    public void ConcatenatedMembersDecodeToTheConcatenatedOriginal(string kind)
    {
        using var t = new TempDir();
        string path = ForeignData.Write(t, "notes.txt." + kind, [.. One(kind, PartA), .. One(kind, PartB)]);
        using (var s = Open(path))
        {
            Assert.Equal("notes.txt", s.Items.Single().Path);
            Assert.Equal(kind == "zst" ? PartA.Length + PartB.Length : null, s.Items.Single().Size);
        }
        var data = ExtractSingle(t, path, out var r);
        Assert.Equal(1, r.FilesOk);
        Assert.Equal(0, r.Unverified);
        Assert.Equal([.. PartA, .. PartB], data);
    }

    [Theory]
    [MemberData(nameof(StreamKinds))]
    public void DataAfterTheLastMemberIsAnError(string kind)
    {
        using var t = new TempDir();
        foreach (var tail in new[] { "GARBAGE!"u8.ToArray(), new byte[100], One(kind, PartB)[..20] })
        {
            string path = ForeignData.Write(t, "x." + kind, [.. One(kind, PartA), .. tail]);
            if (kind == "zst")
            {
                // The frame walk at Open already sees it.
                Assert.Contains("damaged data", Assert.Throws<CorruptDataException>(() => Open(path)).Message);
                continue;
            }
            var data = ExtractSingle(t, path, out var r, "out" + tail.Length);
            Assert.Equal(1, r.FilesFailed);
            Assert.Empty(data);
            Assert.Contains("damaged data", r.Errors.Single());
            Assert.Empty(Directory.GetFiles(t.Sub("out" + tail.Length), "*", SearchOption.AllDirectories));
        }
    }

    [Theory]
    [MemberData(nameof(StreamKinds))]
    public void ATruncatedFileIsAnError(string kind)
    {
        using var t = new TempDir();
        var whole = One(kind, PartA);
        foreach (int cut in new[] { 1, 4, 9, whole.Length / 2 })
        {
            string path = ForeignData.Write(t, "x." + kind, whole[..^cut]);
            try
            {
                var data = ExtractSingle(t, path, out var r, "out" + cut);
                Assert.Equal(1, r.FilesFailed);
                Assert.Empty(data);
            }
            catch (CorruptDataException) { } // the frame walk of a .zst can already see it at Open
        }
    }

    [Fact]
    public void FlippedChecksAreErrors()
    {
        using var t = new TempDir();
        var gz = One("gz", PartA);
        gz[^8] ^= 1; // the member's CRC-32
        var bz = One("bz2", PartA);
        bz[10] ^= 1; // the first block's CRC
        var zst = One("zst", PartA);
        zst[^2] ^= 1; // the frame checksum
        foreach (var (name, data) in new[] { ("a.gz", gz), ("a.bz2", bz), ("a.zst", zst) })
        {
            ExtractSingle(t, ForeignData.Write(t, name, data), out var r, "out-" + name);
            Assert.Equal(1, r.FilesFailed);
            Assert.Equal(0, r.FilesOk);
        }
    }

    [Fact]
    public void ZstdFramesWithoutAChecksumAreUnverified()
    {
        using var t = new TempDir();
        string path = ForeignData.Write(t, "a.zst", ForeignData.Zstd(PartA, checksum: false));
        var data = ExtractSingle(t, path, out var r);
        Assert.Equal(PartA, data);
        Assert.Equal((1, 1), (r.FilesOk, r.Unverified));
    }

    [Theory]
    [InlineData("crc32.xz")]
    [InlineData("crc64.xz")]
    [InlineData("sha256.xz")]
    public void XzChecksAreVerified(string fixture)
    {
        using var t = new TempDir();
        var hello = ForeignData.FixtureTree.Single(f => f.Path == "in/hello.txt").Data;
        string good = ForeignData.Fixture("xz", fixture);
        using (var s = Open(good)) Assert.Equal(hello.Length, s.Items.Single().Size);
        Assert.Equal(hello, ExtractSingle(t, good, out var ok));
        Assert.Equal((1, 0), (ok.FilesOk, ok.Unverified));
        // Flip a byte of the stored check (just before the index, which starts with a 0x00 indicator).
        var bytes = File.ReadAllBytes(good);
        int indexAt = bytes.Length - 12 - (BitConverter.ToInt32(bytes, bytes.Length - 8) + 1) * 4;
        bytes[indexAt - 1] ^= 0x01;
        var data = ExtractSingle(t, ForeignData.Write(t, "flipped.xz", bytes), out var bad, "bad");
        Assert.Empty(data);
        Assert.Equal(1, bad.FilesFailed);
        Assert.Contains("the block check does not match", bad.Errors.Single());
    }

    [Fact]
    public void XzWithoutACheckIsUnverified()
    {
        using var t = new TempDir();
        var data = ExtractSingle(t, ForeignData.Fixture("xz", "none.xz"), out var r);
        Assert.Equal(ForeignData.FixtureTree.Single(f => f.Path == "in/hello.txt").Data, data);
        Assert.Equal((1, 1), (r.FilesOk, r.Unverified));
    }

    [Theory]
    [InlineData("concat.xz")]
    [InlineData("multiblock.xz")]
    [InlineData("x86.xz")]
    [InlineData("arm64.xz")]
    [InlineData("delta.xz")]
    public void XzStreamsBlocksAndFiltersDecode(string fixture)
    {
        using var t = new TempDir();
        byte[] hello = ForeignData.FixtureTree.Single(f => f.Path == "in/hello.txt").Data, numbers = ForeignData.FixtureTree.Single(f => f.Path == "in/sub/numbers.txt").Data;
        byte[] expected = fixture switch
        {
            "concat.xz" => [.. hello, .. numbers],
            "x86.xz" or "arm64.xz" => ForeignData.FixtureCode(),
            _ => numbers,
        };
        Assert.Equal(expected, ExtractSingle(t, ForeignData.Fixture("xz", fixture), out var r));
        Assert.Equal((1, 0, 0), (r.FilesOk, r.Unverified, r.LeftOut));
    }

    [Fact]
    public void XzStreamPaddingIsAllowedAndOtherTrailingDataIsNot()
    {
        using var t = new TempDir();
        var xz = File.ReadAllBytes(ForeignData.Fixture("xz", "crc64.xz"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(ForeignData.Fixture("xz", "crc64.xz"))!, "crc64.xz"));
        ExtractSingle(t, ForeignData.Write(t, "padded.xz", [.. xz, 0, 0, 0, 0, 0, 0, 0, 0]), out var padded, "p");
        Assert.Equal(1, padded.FilesOk);
        Assert.Throws<CorruptDataException>(() => Open(ForeignData.Write(t, "junk.xz", [.. xz, 1, 2, 3, 4])));
        Assert.Throws<CorruptDataException>(() => Open(ForeignData.Write(t, "odd.xz", [.. xz, 0])));
    }

    [Fact]
    public void ADeclaredHugeXzDictionaryIsRefusedBeforeAllocation()
    {
        using var t = new TempDir();
        var xz = File.ReadAllBytes(ForeignData.Fixture("xz", "crc64.xz"));
        int headerSize = (xz[12] + 1) * 4;
        int propAt = Array.IndexOf(xz, (byte)0x21, 13) + 2;
        xz[propAt] = 40;
        BitConverter.GetBytes(Crc32.HashToUInt32(xz.AsSpan(12, headerSize - 4))).CopyTo(xz, 12 + headerSize - 4);
        string path = ForeignData.Write(t, "big.xz", xz);
        var limits = ImportLimits.Default with { MemoryBudgetBytes = 64 << 20 };
        using var s = Open(path, new ArchiveOpenOptions { Limits = limits });
        // The index says the block holds 1,480 bytes, so a dictionary of that size is enough: it decodes.
        Assert.Equal(1, s.Test(null, null, Ct).FilesOk);
        // Without the index's sizes (detection), the declared size is what counts, and it is never allocated.
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(ArchiveKind.Xz, ArchiveSources.Detect(path));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32 << 20);
    }

    [Fact]
    public void AGzipBombStopsAtTheExpansionLimit()
    {
        using var t = new TempDir();
        string path = ForeignData.Write(t, "zeros.gz", ForeignData.GZip(new byte[16 << 20]));
        var limits = ImportLimits.Default with { ExpansionFloorBytes = 1 << 20, MaxExpansionRatio = 100 };
        using var s = Open(path, new ArchiveOpenOptions { Limits = limits });
        Assert.Throws<ExpansionLimitException>(() => s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct));
        Assert.Empty(Directory.Exists(t.Sub("out")) ? Directory.GetFileSystemEntries(t.Sub("out")) : []);
        Assert.Throws<ExpansionLimitException>(() => s.Test(null, null, Ct));
        // The same source still works with the limit lifted (rule 5).
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest { Limits = limits with { AllowLargeExpansion = true } }, null, Ct);
        Assert.Equal(1, r.FilesOk);
    }

    [Fact]
    public void ProgressOfAGzipWithoutAKnownSizeCountsArchiveBytes()
    {
        using var t = new TempDir();
        string path = ForeignData.Write(t, "big.gz", ForeignData.GZip(ForeignData.Text(3 << 20, 9)));
        using var s = Open(path);
        Assert.Null(s.TotalSize);
        var reports = new List<ExtractProgress>();
        s.Extract(t.Sub("out"), new ArchiveExtractRequest(), new SyncProgress<ExtractProgress>(reports.Add), Ct);
        Assert.NotEmpty(reports);
        Assert.All(reports, p => Assert.Equal((0L, 0), (p.BytesTotal, p.FilesTotal)));
        Assert.All(reports, p => Assert.InRange(p.BytesDone, 0, s.FileLength));
        Assert.Equal(reports.Select(p => p.BytesDone).Order(), reports.Select(p => p.BytesDone));
    }

    [Fact]
    public void ProgressOfAnXzWithAnIndexCountsOutputAgainstItsSize()
    {
        using var t = new TempDir();
        using var s = Open(ForeignData.Fixture("xz", "concat.xz"));
        var reports = new List<ExtractProgress>();
        s.Extract(t.Sub("out"), new ArchiveExtractRequest(), new SyncProgress<ExtractProgress>(reports.Add), Ct);
        Assert.Equal(new ExtractProgress(s.TotalSize!.Value, s.TotalSize.Value, 1, 1, null), reports[^1]);
        Assert.Equal(2572, s.TotalSize);
    }

    [Theory]
    [InlineData("notes.txt.gz", "notes.txt")]
    [InlineData("x.tgz", "x.tar")]
    [InlineData("data.BZ2", "data")]
    [InlineData("file.bin", "file")]
    [InlineData("data", "data (decompressed)")]
    [InlineData("DATA", "DATA (decompressed)")]
    public void ASingleFileIsNamedAfterTheArchive(string name, string item) =>
        Assert.Equal(item, Archives.Foreign.SingleStreamSource.ItemName(Path.Combine("dir", name)));

    /// <summary>An IProgress that reports on the calling thread (Progress&lt;T&gt; posts to the thread pool).</summary>
    internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
