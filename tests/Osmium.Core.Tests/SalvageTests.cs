using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;
using Osmium.Core.Container;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests;

/// <summary>
/// <c>osmium salvage</c> on damaged copies of real archives: every file whose blocks are intact comes back
/// byte-identical, everything that touches damage is reported and never written as if it were good, block files equal
/// the original block contents when the index is gone, and nothing is ever written outside the destination.
/// </summary>
public class SalvageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Password = "salvage me";

    // ------------------------------------------------------------------ fixtures

    /// <summary>Text, binary and record files of 0 B to 90 KB, so that with 24 KiB blocks some files share a block
    /// and some span several.</summary>
    private static void MakeTree(string root)
    {
        var rnd = new Random(7);
        Directory.CreateDirectory(Path.Combine(root, "docs", "notes"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "empty-dir"));
        string Lines(int n, int salt) => string.Concat(Enumerable.Range(0, n).Select(i => $"{salt}: line {i} of the salvage test, value {i * 7919 % 1013}\n"));
        File.WriteAllText(Path.Combine(root, "docs", "readme.txt"), Lines(300, 1));
        File.WriteAllText(Path.Combine(root, "docs", "notes", "long.txt"), Lines(1500, 2));
        File.WriteAllText(Path.Combine(root, "docs", "notes", "short.txt"), "a short note\n");
        var random = new byte[60_000];
        rnd.NextBytes(random);
        File.WriteAllBytes(Path.Combine(root, "data", "random.bin"), random);
        var records = new byte[40_000];
        for (int i = 0; i < records.Length / 4; i++) BinaryPrimitives.WriteInt32LittleEndian(records.AsSpan(i * 4), i * 3 + rnd.Next(5));
        File.WriteAllBytes(Path.Combine(root, "data", "records.dat"), records);
        File.WriteAllBytes(Path.Combine(root, "data", "empty.bin"), []);
        File.WriteAllText(Path.Combine(root, "ünïcödé — файл.txt"), "unicode name");
        var t0 = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        int k = 0;
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            File.SetLastWriteTimeUtc(f, t0.AddMinutes(k++));
    }

    private static async Task<string> Build(TempDir t, string? password = null, string profile = "fast", long blockCap = 24 << 10)
    {
        if (!Directory.Exists(t.Sub("in"))) MakeTree(t.Sub("in"));
        return await Compress(t.Sub("in"), t.Sub("a.osa"), password, profile, blockCap);
    }

    private static async Task<string> Compress(string source, string output, string? password = null, string profile = "fast", long blockCap = 24 << 10)
    {
        var scan = Scanner.Scan([source], ct: Ct);
        var plan = await new Planner(new PlanOptions { Profile = Profile.ByName(profile), BlockCap = blockCap, MemoryBudget = 1L << 30 }).BuildAsync(scan, Ct);
        var r = await ArchiveBuilder.BuildAsync(plan, new BuildOptions { OutputPath = output, Password = password, KdfIterations = 1000, MemoryBudget = 1L << 30 }, Ct);
        return r.Path;
    }

    /// <summary>Where everything is in an intact archive, read the normal way.</summary>
    private sealed record Layout(long HeaderLength, IReadOnlyList<BlockInfo> Blocks, IReadOnlyList<ArchiveEntry> Entries, long IndexOffset, long IndexLength, long Length)
    {
        public IEnumerable<ArchiveEntry> Files => Entries.Where(e => !e.IsDirectory);
        public bool Touches(ArchiveEntry e, int frame) => e.Segments.Any(s => s.Frame == frame);
        public long PayloadEnd(int frame) => Blocks[frame].Offset + Blocks[frame].FrameLength;
    }

    private static Layout Read(string path, string? password = null)
    {
        using var r = OsaReader.Open(path, () => password);
        using var fs = File.OpenRead(path);
        var footer = ArchiveFooter.Read(fs);
        return new Layout(r.Header.Length, r.Blocks.ToList(), r.Entries.ToList(), footer.IndexOffset, footer.IndexLength, fs.Length);
    }

    /// <summary>A block's raw content rebuilt from the source files and the index's segments (padding is zero).</summary>
    private static byte[] AssembleBlock(Layout l, int frame, string root)
    {
        var buf = new byte[l.Blocks[frame].RawSize];
        foreach (var e in l.Files)
        {
            var data = File.ReadAllBytes(Path.Combine(root, e.Path));
            long at = 0;
            foreach (var s in e.Segments)
            {
                if (s.Frame == frame) data.AsSpan((int)at, (int)s.Length).CopyTo(buf.AsSpan((int)s.Offset));
                at += s.Length;
            }
        }
        return buf;
    }

    private static string Damage(TempDir t, string archive, Action<List<byte>> damage)
    {
        var bytes = File.ReadAllBytes(archive).ToList();
        damage(bytes);
        var path = t.Sub("damaged.osa");
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    private static void Flip(List<byte> b, long from, int count)
    {
        for (int i = 0; i < count; i++) b[(int)from + i] ^= 0x5A;
    }

    /// <summary>A frame header that checks out (marker and CRC), whatever follows it.</summary>
    private static byte[] FakeFrameHeader(uint flags, ulong number, long bodyLength)
    {
        var h = new byte[Osa.FrameHeaderSize];
        Osa.FrameMagic.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(8), number);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(16), bodyLength);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), Osa.Crc(h.AsSpan(0, 24)));
        return h;
    }

    /// <summary>Salvages into t/out and checks what must hold whatever the damage: nothing appears outside the
    /// destination, no partial file is left, and every file written under an archive name is byte-identical to it.</summary>
    private static SalvageResult Salvage(TempDir t, string archive, string? password = null, SalvageOptions? options = null) =>
        Salvage(t, archive, password is null ? null : () => password, options);

    private static SalvageResult Salvage(TempDir t, string archive, Func<string?>? password, SalvageOptions? options = null)
    {
        var before = Outside(t);
        var existing = Directory.Exists(t.Sub("out")) ? Directory.GetFiles(t.Sub("out"), "*", SearchOption.AllDirectories).ToHashSet() : [];
        var res = Container.Salvage.Run(archive, t.Sub("out"), password, options, Ct);
        Assert.Equal(before, Outside(t));
        if (Directory.Exists(t.Sub("out")))
        {
            var renamed = res.Renamed.ToDictionary(r => r.WrittenAs, r => r.Path);
            foreach (var f in Directory.GetFiles(t.Sub("out"), "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(t.Sub("out"), f).Replace(Path.DirectorySeparatorChar, '/');
                Assert.DoesNotContain(".osmium-part", rel);
                if (existing.Contains(f) || rel.StartsWith("salvage-report", StringComparison.Ordinal) || rel.StartsWith("block-", StringComparison.Ordinal)) continue;
                string source = t.Sub(renamed.GetValueOrDefault(rel, rel));
                Assert.True(File.Exists(source), $"salvage wrote '{rel}', which is not an archived file");
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(f));
            }
        }
        return res;
    }

    /// <summary>Every file outside t/out, with its length and time, so that a salvage writing anything there shows.</summary>
    private static List<string> Outside(TempDir t) =>
        [.. Directory.GetFileSystemEntries(t.Path, "*", SearchOption.AllDirectories)
            .Where(p => !(p + Path.DirectorySeparatorChar).StartsWith(t.Sub("out") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(p => File.Exists(p) ? $"{p} {new FileInfo(p).Length} {File.GetLastWriteTimeUtc(p).Ticks}" : p)
            .Order(StringComparer.Ordinal)];

    /// <summary>Files touching a damaged block are lost (and named so); every other file is back, byte-identical, with its time.</summary>
    private static void AssertFilesRecovered(TempDir t, Layout l, SalvageResult res, params int[] damaged)
    {
        Assert.True(res.IndexRecovered);
        foreach (var e in l.Files)
        {
            string written = Path.Combine(t.Sub("out"), e.Path);
            if (damaged.Any(k => l.Touches(e, k)))
            {
                Assert.False(File.Exists(written), $"{e.Path} touches a damaged block but was written");
                var loss = Assert.Single(res.Lost, x => x.Path == e.Path);
                Assert.Contains(damaged.Where(k => l.Touches(e, k)), k => loss.Reason.Contains($"block {k}", StringComparison.Ordinal));
            }
            else
            {
                Assert.Equal(File.ReadAllBytes(t.Sub(e.Path)), File.ReadAllBytes(written));
                Assert.Equal(e.ModifiedUtc, File.GetLastWriteTimeUtc(written));
            }
        }
        foreach (var d in l.Entries.Where(e => e.IsDirectory)) Assert.True(Directory.Exists(Path.Combine(t.Sub("out"), d.Path)), d.Path);
        Assert.Equal(l.Files.Count(e => !damaged.Any(k => l.Touches(e, k))), res.FilesRecovered);
        Assert.Equal(l.Files.Count(e => damaged.Any(k => l.Touches(e, k))), res.Lost.Count);
    }

    /// <summary>With the index lost, each intact block is a block file equal to the block rebuilt from the sources.</summary>
    private static void AssertBlockFiles(TempDir t, Layout l, SalvageResult res, IEnumerable<int> intact)
    {
        Assert.False(res.IndexRecovered);
        Assert.DoesNotContain(Directory.GetFiles(t.Sub("out"), "*", SearchOption.AllDirectories),
            f => Path.GetFileName(f) != Container.Salvage.ReportFileName && !Path.GetFileName(f).StartsWith("block-", StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(t.Sub("out")));
        foreach (int k in intact)
        {
            Assert.Equal(AssembleBlock(l, k, t.Path), File.ReadAllBytes(t.Sub($"out/block-{k}.bin")));
            var b = Assert.Single(res.Blocks, x => x.Number == k);
            Assert.Equal(SalvageBlockState.Verified, b.State);
            Assert.Equal($"block-{k}.bin", b.Frame!.Output);
        }
        var report = File.ReadAllText(t.Sub("out/" + Container.Salvage.ReportFileName));
        Assert.Equal(t.Sub("out/" + Container.Salvage.ReportFileName), res.ReportPath);
        Assert.Contains("file names", report, StringComparison.OrdinalIgnoreCase);
        foreach (int k in intact)
        {
            var f = res.Blocks[k].Frame!;
            Assert.Contains($"block {k}: verified", report);
            Assert.Contains(f.Pipeline!, report);
            Assert.Contains($"{f.RawHash!.Value:x32}", report);
            Assert.Contains($"{f.PayloadHash!.Value:x16}", report);
            Assert.Contains($"{l.Blocks[k].RawSize:N0} bytes", report);
        }
    }

    // ------------------------------------------------------------------ the archive is fine

    [Fact]
    public async Task AnUndamagedArchiveIsRecoveredCompletely()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        Assert.True(l.Blocks.Count >= 4, $"the fixture should make several blocks, made {l.Blocks.Count}");
        Assert.Contains(l.Files, e => e.Segments.Select(s => s.Frame).Distinct().Count() > 1); // a file spans blocks

        var res = Salvage(t, path);
        Assert.Equal(0, res.ExitCode);
        Assert.True(res.Complete);
        Assert.True(res.HeaderIntact);
        Assert.Empty(res.Lost);
        Assert.Equal(l.Blocks.Count, res.BlocksVerified);
        Assert.Equal(l.Blocks.Count + 1, res.Frames.Count);
        Assert.All(res.Frames, f => Assert.Equal(SalvageFrameState.Verified, f.State));
        Assert.Equal(0, res.UnrecognizedBytes);
        Assert.Equal(l.Files.Sum(e => e.Size), res.BytesRecovered);
        AssertFilesRecovered(t, l, res);
        Assert.False(File.Exists(t.Sub("out/" + Container.Salvage.ReportFileName)));
        Assert.Equal("Everything was recovered.", res.Summary().Last());
    }

    // ------------------------------------------------------------------ (a) a damaged block

    [Fact]
    public async Task ADamagedBlockLosesOnlyTheFilesInIt()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        int k = 1;
        var bad = Damage(t, path, b => Flip(b, l.PayloadEnd(k) - 24, 8));

        var res = Salvage(t, bad);
        Assert.Equal(2, res.ExitCode);
        AssertFilesRecovered(t, l, res, k);
        var block = res.Blocks[k];
        Assert.Equal(SalvageBlockState.Damaged, block.State);
        Assert.Equal("payload hash mismatch", block.Problem);
        Assert.Equal(l.Blocks[k].Offset, block.Frame!.Offset);
        Assert.Equal(l.Blocks.Count - 1, res.BlocksVerified);
        Assert.Contains(res.Summary(), s => s.Contains($"block {k} at offset {l.Blocks[k].Offset:N0}: damaged (payload hash mismatch)", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ (b) truncation

    [Fact]
    public async Task ATruncatedArchiveYieldsItsIntactBlocksAsBlockFiles()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        int k = 2;
        var bad = Damage(t, path, b => b.RemoveRange((int)(l.Blocks[k].Offset + l.Blocks[k].FrameLength / 2), b.Count - (int)(l.Blocks[k].Offset + l.Blocks[k].FrameLength / 2)));

        var res = Salvage(t, bad);
        Assert.Equal(2, res.ExitCode);
        Assert.Contains("the end of the archive is missing", res.IndexProblem);
        AssertBlockFiles(t, l, res, Enumerable.Range(0, k));
        Assert.Equal(k + 1, res.Blocks.Count);
        Assert.Equal(SalvageBlockState.Damaged, res.Blocks[k].State);
        Assert.StartsWith("truncated", res.Blocks[k].Problem);
        Assert.False(File.Exists(t.Sub($"out/block-{k}.bin")));
        Assert.Equal(Enumerable.Range(0, k).Select(i => $"block-{i}.bin").Order(StringComparer.Ordinal), res.Written);
    }

    // ------------------------------------------------------------------ (c) the footer

    [Fact]
    public async Task AZeroedFooterLosesNothing()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        var bad = Damage(t, path, b => { for (int i = b.Count - Osa.FooterSize; i < b.Count; i++) b[i] = 0; });
        Assert.ThrowsAny<IOException>(() => OsaReader.Open(bad));

        var res = Salvage(t, bad);
        Assert.Equal(0, res.ExitCode);
        AssertFilesRecovered(t, l, res);
        Assert.Equal(Osa.FooterSize, res.UnrecognizedBytes);
        Assert.StartsWith("Everything was recovered: the archive is damaged", res.Summary().Last());
    }

    // ------------------------------------------------------------------ (d) the index

    [Fact]
    public async Task ADestroyedIndexYieldsEveryBlockAsABlockFile()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        var rnd = new Random(3);
        var bad = Damage(t, path, b => { for (long i = l.IndexOffset; i < l.IndexOffset + l.IndexLength; i++) b[(int)i] = (byte)rnd.Next(256); });

        var res = Salvage(t, bad);
        Assert.Equal(2, res.ExitCode);
        Assert.Equal("no index frame was found", res.IndexProblem);
        AssertBlockFiles(t, l, res, Enumerable.Range(0, l.Blocks.Count));
        Assert.Equal(l.Blocks.Sum(b => b.RawSize), res.BytesRecovered);
        Assert.Equal(l.IndexLength, res.UnrecognizedBytes);
    }

    // ------------------------------------------------------------------ (e) the header

    [Theory]
    [InlineData("zeroed")]
    [InlineData("archive id")]
    [InlineData("crc")]
    public async Task ACorruptHeaderOfAnUnencryptedArchiveLosesNothing(string what)
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        var bad = Damage(t, path, b =>
        {
            if (what == "zeroed") for (int i = 0; i < l.HeaderLength; i++) b[i] = 0;
            else Flip(b, what == "crc" ? l.HeaderLength - 2 : 20, 1);
        });
        Assert.ThrowsAny<IOException>(() => OsaReader.Open(bad));

        var res = Salvage(t, bad);
        Assert.Equal(0, res.ExitCode);
        Assert.False(res.HeaderIntact);
        Assert.StartsWith("damaged", res.Header);
        Assert.Contains(res.Notes, n => n.Contains("does not need it", StringComparison.Ordinal));
        AssertFilesRecovered(t, l, res);
    }

    [Fact]
    public async Task AnEncryptedArchiveWithoutItsHeaderSaysThatSalvageNeedsIt()
    {
        using var t = new TempDir();
        var path = await Build(t, Password);
        var l = Read(path, Password);
        var bad = Damage(t, path, b => { for (int i = 0; i < l.HeaderLength; i++) b[i] = 0; });

        int asked = 0;
        var res = Salvage(t, bad, () => { asked++; return Password; });
        Assert.Equal(0, asked); // there is nothing to use a password with
        Assert.Equal(2, res.ExitCode);
        Assert.True(res.Encrypted);
        Assert.Contains("encrypted salvage needs an intact header", res.KeyProblem);
        Assert.Equal(l.Blocks.Count, res.Blocks.Count);
        Assert.All(res.Blocks, b => Assert.Equal(SalvageBlockState.NotDecrypted, b.State));
        Assert.Equal("the index could not be decrypted", res.IndexProblem);
        Assert.Empty(res.Written);
        Assert.Equal([Container.Salvage.ReportFileName], Directory.GetFileSystemEntries(t.Sub("out")).Select(Path.GetFileName));
        Assert.Equal("Nothing could be recovered.", res.Summary().Last());
    }

    [Fact]
    public async Task AnEncryptedArchiveWithADamagedHeaderYieldsItsBlocksWhenTheKeyFieldsSurvive()
    {
        using var t = new TempDir();
        var path = await Build(t, Password);
        var l = Read(path, Password);
        int creator = File.ReadAllBytes(path).AsSpan(0, (int)l.HeaderLength).IndexOf("Osmium "u8);
        Assert.True(creator > 36);
        var bad = Damage(t, path, b => b[creator + 1] ^= 0x20); // the creator string, not the key fields

        var res = Salvage(t, bad, Password);
        Assert.Equal(2, res.ExitCode);
        Assert.False(res.HeaderIntact);
        Assert.Null(res.KeyProblem);
        Assert.Contains(res.Notes, n => n.Contains("authenticate the frames", StringComparison.Ordinal));
        // the index is sealed together with the exact header bytes, so it cannot be authenticated any more
        Assert.Contains("authenticated together with the archive header", res.IndexProblem);
        AssertBlockFiles(t, l, res, Enumerable.Range(0, l.Blocks.Count));
    }

    [Fact]
    public async Task AnEncryptedArchiveWhoseHeaderCrcAloneIsDamagedIsRecoveredCompletely()
    {
        using var t = new TempDir();
        var path = await Build(t, Password);
        var l = Read(path, Password);
        var bad = Damage(t, path, b => Flip(b, l.HeaderLength - 1, 1));
        Assert.ThrowsAny<IOException>(() => OsaReader.Open(bad, () => Password));

        var res = Salvage(t, bad, Password);
        Assert.Equal(0, res.ExitCode);
        Assert.False(res.HeaderIntact);
        AssertFilesRecovered(t, l, res);
    }

    // Header of the encrypted test archives (1,000 iterations, a 2-byte varint): extLen at 32; record type, length and
    // kdf id at 36-38; iterations 39-40; salt 41-72; cipher id 73; verifier 74-89; creator type and length at 90-91.
    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(32)]
    [InlineData(36)]
    [InlineData(37)]
    [InlineData(38)]
    [InlineData(73)]
    [InlineData(91)]
    public async Task AnEncryptedHeaderDamagedOutsideItsKeyFieldsIsRebuilt(int offset)
    {
        using var t = new TempDir();
        var path = await Build(t, Password);
        var l = Read(path, Password);
        using (var r = OsaReader.Open(path, () => Password)) Assert.Equal(1000, r.Header.Encryption!.Iterations);
        var res = Salvage(t, Damage(t, path, b => Flip(b, offset, 1)), Password);
        Assert.Equal(0, res.ExitCode);
        Assert.False(res.HeaderIntact);
        AssertFilesRecovered(t, l, res);
    }

    [Theory]
    [InlineData(20, true)]  // the archive id's first 8 bytes: the footer keeps them too
    [InlineData(28, false)] // the archive id's last 8 bytes
    [InlineData(50, false)] // the salt
    public async Task AnEncryptedHeaderDamagedInItsKeyFields(int offset, bool recovered)
    {
        using var t = new TempDir();
        var path = await Build(t, Password);
        var l = Read(path, Password);
        var res = Salvage(t, Damage(t, path, b => Flip(b, offset, 1)), Password);
        if (recovered)
        {
            Assert.Equal(0, res.ExitCode);
            AssertFilesRecovered(t, l, res);
        }
        else
        {
            Assert.Equal(2, res.ExitCode);
            Assert.Equal(0, res.BlocksVerified);
            Assert.NotNull(res.KeyProblem);
            Assert.Empty(res.Written);
        }
    }

    [Fact]
    public async Task DataPrependedToAnEncryptedArchiveIsSkipped()
    {
        using var t = new TempDir();
        var path = await Build(t, Password);
        var l = Read(path, Password);
        var prefix = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n\r\n");
        var bad = Damage(t, path, b => b.InsertRange(0, prefix));

        var res = Salvage(t, bad, Password);
        Assert.Equal(0, res.ExitCode);
        Assert.True(res.HeaderIntact);
        Assert.StartsWith($"intact at offset {prefix.Length}", res.Header);
        Assert.Equal(prefix.Length, res.UnrecognizedBytes);
        AssertFilesRecovered(t, l, res);
    }

    // ------------------------------------------------------------------ (f) garbage

    [Fact]
    public async Task GarbageBeforeAFrameIsSkipped()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        var garbage = new byte[1000];
        new Random(11).NextBytes(garbage);
        "OBLK"u8.CopyTo(garbage.AsSpan(100)); // a frame marker whose header CRC fails
        FakeFrameHeader(0, 1, 500).CopyTo(garbage, 300); // a header that checks out, for a body that does not
        var bad = Damage(t, path, b => b.InsertRange((int)l.Blocks[1].Offset, garbage));
        Assert.ThrowsAny<IOException>(() => OsaReader.Open(bad));

        var res = Salvage(t, bad);
        Assert.Equal(0, res.ExitCode);
        AssertFilesRecovered(t, l, res);
        Assert.Equal(garbage.Length, res.UnrecognizedBytes);
        var bogus = Assert.Single(res.Frames, f => f.State == SalvageFrameState.Damaged);
        Assert.Equal(l.Blocks[1].Offset + 300, bogus.Offset);
        Assert.Equal(SalvageBlockState.Verified, res.Blocks[1].State);
        Assert.Equal(l.Blocks[1].Offset + garbage.Length, res.Blocks[1].Frame!.Offset);
    }

    [Fact]
    public async Task WithoutTheHeaderTheFramesDecideWhetherTheArchiveIsEncrypted()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        // header gone, and an encrypted-looking frame planted where the first frame should start
        var bad = Damage(t, path, b =>
        {
            for (int i = 0; i < l.HeaderLength; i++) b[i] = 0;
            b.InsertRange((int)l.HeaderLength, [.. FakeFrameHeader(Osa.FrameFlagEncrypted, 0, 64), .. new byte[64]]);
        });

        var res = Salvage(t, bad);
        Assert.Equal(0, res.ExitCode);
        Assert.False(res.Encrypted);
        Assert.Equal(SalvageFrameState.Ignored, res.Frames[0].State);
        AssertFilesRecovered(t, l, res);
    }

    // ------------------------------------------------------------------ (g) encryption

    [Fact]
    public async Task AnEncryptedArchiveWithADamagedBlockLosesOnlyTheFilesInIt()
    {
        using var t = new TempDir();
        var path = await Build(t, Password);
        var l = Read(path, Password);
        int k = 2;
        var bad = Damage(t, path, b => Flip(b, l.Blocks[k].Offset + Osa.FrameHeaderSize + 5, 1));
        Assert.Throws<PasswordRequiredException>(() => Container.Salvage.Run(bad, t.Sub("out"), null, null, Ct));
        Assert.Throws<PasswordRequiredException>(() => Container.Salvage.Run(bad, t.Sub("out"), () => null, null, Ct));
        Assert.Throws<WrongPasswordException>(() => Container.Salvage.Run(bad, t.Sub("out"), () => "wrong", null, Ct));
        Assert.False(Directory.Exists(t.Sub("out")));

        var res = Salvage(t, bad, Password);
        Assert.Equal(2, res.ExitCode);
        Assert.True(res.Encrypted);
        AssertFilesRecovered(t, l, res, k);
        Assert.Equal("authentication failed (damaged or tampered)", res.Blocks[k].Problem);
    }

    // ------------------------------------------------------------------ missing and duplicated regions

    [Fact]
    public async Task ADuplicatedRegionIsRecognizedAsACopy()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        var b1 = l.Blocks[1];
        var bad = Damage(t, path, b => b.InsertRange((int)(b1.Offset + b1.FrameLength), b.GetRange((int)b1.Offset, (int)b1.FrameLength)));

        var res = Salvage(t, bad);
        Assert.Equal(0, res.ExitCode);
        AssertFilesRecovered(t, l, res);
        var copy = Assert.Single(res.Frames, f => f.State == SalvageFrameState.Duplicate);
        Assert.Equal((1ul, b1.Offset + b1.FrameLength), (copy.Number, copy.Offset));
        Assert.Equal(0, res.UnrecognizedBytes);
    }

    [Fact]
    public async Task AMissingRegionLosesOnlyTheBlockItCutInto()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        int k = 1;
        var b = l.Blocks[k];
        int cut = (int)Math.Min(300, b.FrameLength / 4);
        var bad = Damage(t, path, x => x.RemoveRange((int)(b.Offset + b.FrameLength / 2), cut));

        var res = Salvage(t, bad);
        Assert.Equal(2, res.ExitCode);
        AssertFilesRecovered(t, l, res, k);
        Assert.Equal(SalvageBlockState.Damaged, res.Blocks[k].State);
        // the next frame now starts inside the bytes block k claims, and is still found and used
        Assert.Equal(SalvageBlockState.Verified, res.Blocks[k + 1].State);
        Assert.Equal(b.Offset, res.Blocks[k + 1].Frame!.EnclosingOffset);
    }

    [Fact]
    public async Task AnArchiveStoredInADamagedBlockIsNotTakenForThisOne()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Sub("inner"));
        var secret = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("inner archive content. ", 150)));
        File.WriteAllBytes(t.Sub("inner/secret.txt"), secret);
        var innerPlan = await new Planner(new PlanOptions { Profile = Profile.Store, MemoryBudget = 1L << 30 }).BuildAsync(Scanner.Scan([t.Sub("inner")], ct: Ct), Ct);
        await ArchiveBuilder.BuildAsync(innerPlan, new BuildOptions { OutputPath = t.Sub("inner.osa"), MemoryBudget = 1L << 30 }, Ct);
        Directory.CreateDirectory(t.Sub("in"));
        File.Copy(t.Sub("inner.osa"), t.Sub("in/inner.osa"));
        File.WriteAllText(t.Sub("in/notes.txt"), string.Concat(Enumerable.Range(0, 400).Select(i => $"outer note {i}\n")));
        var path = await Build(t, profile: "store"); // the inner archive's frames appear verbatim in an outer block
        var l = Read(path);
        var holder = l.Files.Single(e => e.Path.EndsWith("inner.osa", StringComparison.Ordinal));
        var seg = Assert.Single(holder.Segments);
        var block = l.Blocks[seg.Frame];
        long innerAt = block.Offset + block.FrameLength - block.RawSize + seg.Offset; // stored: the payload is the raw block
        var bad = Damage(t, path, b => Flip(b, innerAt + 12, 1)); // the inner archive's header: its frames stay intact

        var res = Salvage(t, bad);
        Assert.Equal(2, res.ExitCode);
        Assert.Equal(l.IndexOffset, res.IndexFrame!.Offset);
        AssertFilesRecovered(t, l, res, seg.Frame);
        Assert.Empty(Directory.GetFiles(t.Sub("out"), "secret.txt", SearchOption.AllDirectories));
        var inside = res.Frames.Where(f => f.EnclosingOffset == block.Offset).ToList();
        Assert.Equal(2, inside.Count); // the inner archive's block and index
        Assert.All(inside, f => Assert.Equal(SalvageFrameState.Ignored, f.State));

        // Without the outer index, the inner block is kept, but never under the outer block's name.
        Directory.Delete(t.Sub("out"), recursive: true);
        var worse = Damage(t, bad, b => { for (long i = l.IndexOffset; i < l.IndexOffset + l.IndexLength; i++) b[(int)i] = 0; });
        res = Salvage(t, worse);
        Assert.False(res.IndexRecovered);
        foreach (var b in l.Blocks.Where(b => b.Frame != seg.Frame))
            Assert.Equal(AssembleBlock(l, b.Frame, t.Path), File.ReadAllBytes(t.Sub($"out/block-{b.Frame}.bin")));
        Assert.False(File.Exists(t.Sub($"out/block-{seg.Frame}.bin")));
        var innerBlock = Assert.Single(res.Frames, f => f.EnclosingOffset == block.Offset && !f.IsIndex);
        Assert.Equal($"block-0-at-{innerBlock.Offset}.bin", innerBlock.Output);
        Assert.Equal(secret, File.ReadAllBytes(t.Sub("out/" + innerBlock.Output)));
        Assert.Equal(SalvageFrameState.Ignored, res.Frames.Single(f => f.IsIndex).State);
        Assert.Contains("may belong to an archive stored in that frame", File.ReadAllText(res.ReportPath!));
    }

    // ------------------------------------------------------------------ safety

    [Fact]
    public void UnsafePathsAreNeverWrittenOutsideTheDestination()
    {
        using var t = new TempDir();
        var data = "salvaged content"u8.ToArray();
        File.WriteAllBytes(t.Sub("ok.txt"), data);
        string path = t.Sub("evil.osa");
        using (var w = new OsaWriter(path))
        {
            var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", MethodRegistry.Default), data, CodecContext.None);
            int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, data.Length);
            ArchiveEntry Entry(string p) => new()
            {
                Path = p, Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(frame, 0, data.Length)], Hash = XxHash128.HashToUInt128(data),
            };
            w.Finish([Entry("../evil.txt"), Entry("ok/../../evil2.txt"), Entry("/abs.txt"), Entry("ok.txt"), Entry("sub/CON"),
                new ArchiveEntry { Path = "../evil-dir", Kind = EntryKind.Directory }]);
            w.Commit(overwrite: false);
        }

        var res = Salvage(t, path); // checks that nothing appeared outside t/out
        Assert.Equal(2, res.ExitCode);
        Assert.Equal(["ok.txt"], res.Written);
        Assert.Equal(data, File.ReadAllBytes(t.Sub("out/ok.txt")));
        Assert.Equal(5, res.Lost.Count);
        Assert.All(res.Lost, x => Assert.StartsWith("Unsafe path", x.Reason));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(t.Path)!, "evil.txt")));
    }

    /// <summary>An archive with no blocks whose index holds these raw bytes, framed and checksummed as a writer would.</summary>
    private static string ArchiveWithIndex(TempDir t, byte[] rawIndex)
    {
        var header = new ArchiveHeader { Creator = "crafted" };
        var headerBytes = header.Serialize();
        var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", MethodRegistry.Default), rawIndex, CodecContext.None);
        var prefix = new BlockBody(desc, BlockBody.Hash128(rawIndex), payload).SerializePrefix();
        var frameHeader = new byte[Osa.FrameHeaderSize];
        Osa.FrameMagic.CopyTo(frameHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(frameHeader.AsSpan(4), Osa.FrameFlagIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(frameHeader.AsSpan(8), Osa.IndexFrameNumber);
        BinaryPrimitives.WriteInt64LittleEndian(frameHeader.AsSpan(16), prefix.Length + payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frameHeader.AsSpan(24), Osa.Crc(frameHeader.AsSpan(0, 24)));
        var footer = new ArchiveFooter(headerBytes.Length, frameHeader.Length + prefix.Length + payload.Length,
            BinaryPrimitives.ReadUInt64LittleEndian(header.ArchiveId)).Serialize();
        string path = t.Sub("crafted.osa");
        File.WriteAllBytes(path, [.. headerBytes, .. frameHeader, .. prefix, .. payload, .. footer]);
        return path;
    }

    [Fact]
    public void AnAuthenticButMalformedIndexIsSetAsideInsteadOfStoppingSalvage()
    {
        using var t = new TempDir();
        // one file whose only segment is in block 0 of an archive that has no blocks
        var entry = new BlobWriter().U(0).U(1).Raw("x"u8).B((byte)EntryKind.File).U(0).U(5).U(1).U(0).U(0).U(5).Raw(new byte[16]).ToArray();
        var path = ArchiveWithIndex(t, new BlobWriter().U(ArchiveIndex.Version).U(0).U(1).Bytes(entry).U(0).ToArray());

        var res = Salvage(t, path);
        Assert.Equal(2, res.ExitCode);
        Assert.False(res.IndexRecovered);
        Assert.Contains("the index is unreadable", res.IndexProblem);
        Assert.Empty(res.Written);
        Assert.Empty(res.Blocks);
    }

    [Fact]
    public async Task AnIntactHeaderOfANewerVersionIsRefusedAsOpeningItWouldBe()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        var bad = Damage(t, path, b =>
        {
            var h = b.GetRange(0, (int)l.HeaderLength).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 2); // a critical flag this version does not know
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(h.Length - 4), Osa.Crc(h.AsSpan(0, h.Length - 4)));
            for (int i = 0; i < h.Length; i++) b[i] = h[i];
        });
        var e = Assert.ThrowsAny<NewerVersionException>(() => Container.Salvage.Run(bad, t.Sub("out"), null, null, Ct));
        Assert.Equal("This archive needs a newer version of Osmium: it uses archive flag 0x0002.", e.Message);
        Assert.False(Directory.Exists(t.Sub("out")));
    }

    [Fact]
    public async Task TheMarkOfTheWebGoesOnThePartFileBeforeTheFileAppearsAndFailuresAreReported()
    {
        using var t = new TempDir();
        var path = await Build(t);
        int files = Read(path).Files.Count();
        const string zone = "[ZoneTransfer]\r\nZoneId=3\r\n";
        var marked = new ConcurrentQueue<(string Path, bool Appeared)>();
        string? Record(string p, string z)
        {
            int part = p.IndexOf(".osmium-part-", StringComparison.Ordinal);
            marked.Enqueue((p, z != zone || part < 0 || File.Exists(p[..part])));
            return null;
        }
        Assert.Equal(0, Salvage(t, path, options: new SalvageOptions { Zone = zone, ZoneWriter = Record }).ExitCode);
        using (var r = OsaReader.Open(path))
            Assert.Empty(Extractor.Extract(r, t.Sub("x"), null, new ExtractOptions { Zone = zone, ZoneWriter = Record }, null, Ct).Warnings);
        Assert.Equal(2 * files, marked.Count);
        Assert.All(marked, m => Assert.False(m.Appeared, $"{m.Path} was not a part file, or was marked after the file appeared"));

        // A mark that cannot be written is reported, never dropped silently.
        static string? Refuse(string p, string z) => "access is denied";
        var res = Salvage(t, path, options: new SalvageOptions { Zone = zone, ZoneWriter = Refuse, Overwrite = OverwriteMode.Overwrite });
        Assert.Equal(files, res.Warnings.Count);
        Assert.Contains(res.Summary(), s => s.StartsWith("Warning: ", StringComparison.Ordinal) && s.Contains("Mark-of-the-Web", StringComparison.Ordinal));
        using (var r = OsaReader.Open(path))
            Assert.Equal(files, Extractor.Extract(r, t.Sub("x"), null, new ExtractOptions { Zone = zone, ZoneWriter = Refuse, Overwrite = OverwriteMode.Overwrite }, null, Ct).Warnings.Count);
    }

    [Fact]
    public void TheMarkOfTheWebIsWrittenOnAReadOnlyFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Mark-of-the-Web is an NTFS alternate data stream.");
        using var t = new TempDir();
        string file = t.Sub("read-only.txt");
        File.WriteAllText(file, "content");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            Assert.Null(Extractor.WriteZoneIdentifier(file, "[ZoneTransfer]\r\nZoneId=3\r\n"));
            Assert.Contains("ZoneId=3", File.ReadAllText(file + ":Zone.Identifier"));
            Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task ReadOnlyFilesKeepTheMarkOfTheWeb()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Mark-of-the-Web is an NTFS alternate data stream.");
        using var t = new TempDir();
        try
        {
            Directory.CreateDirectory(t.Sub("in"));
            File.WriteAllText(t.Sub("in/payload.txt"), "downloaded content");
            File.SetAttributes(t.Sub("in/payload.txt"), FileAttributes.ReadOnly);
            var path = await Build(t);
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

            Assert.Equal(0, Salvage(t, path).ExitCode);
            Assert.True(File.GetAttributes(t.Sub("out/in/payload.txt")).HasFlag(FileAttributes.ReadOnly));
            Assert.Contains("ZoneId=3", File.ReadAllText(t.Sub("out/in/payload.txt") + ":Zone.Identifier"));
            using (var r = OsaReader.Open(path)) Extractor.Extract(r, t.Sub("x"), null, null, null, Ct);
            Assert.Contains("ZoneId=3", File.ReadAllText(t.Sub("x/in/payload.txt") + ":Zone.Identifier"));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(t.Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task ARenamedFileNeverTakesTheNameOfAnotherEntry()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Sub("in"));
        File.WriteAllText(t.Sub("in/report.txt"), "the report " + new string('r', 5000));
        File.WriteAllText(t.Sub("in/report (2).txt"), "the second report " + new string('s', 300));
        var path = await Build(t);
        Directory.CreateDirectory(t.Sub("out/in"));
        File.WriteAllText(t.Sub("out/in/report.txt"), "already there");

        var res = Salvage(t, path);
        Assert.Equal(0, res.ExitCode);
        Assert.Equal("already there", File.ReadAllText(t.Sub("out/in/report.txt")));
        Assert.Equal(File.ReadAllBytes(t.Sub("in/report (2).txt")), File.ReadAllBytes(t.Sub("out/in/report (2).txt")));
        Assert.Equal(File.ReadAllBytes(t.Sub("in/report.txt")), File.ReadAllBytes(t.Sub("out/in/report (3).txt")));
        Assert.Equal([new SalvageRename("in/report.txt", "in/report (3).txt")], res.Renamed);
        Assert.Contains("  renamed: in/report.txt → in/report (3).txt (the name was taken)", res.Summary());
    }

    [Fact]
    public void AFileWithoutArchiveDataIsRefusedAndNothingIsWritten()
    {
        using var t = new TempDir();
        var junk = new byte[10_000];
        new Random(1).NextBytes(junk);
        File.WriteAllBytes(t.Sub("junk.osa"), junk);
        Assert.Throws<CorruptDataException>(() => Container.Salvage.Run(t.Sub("junk.osa"), t.Sub("out"), null, null, Ct));
        Assert.False(Directory.Exists(t.Sub("out")));
    }

    [Fact]
    public void AnIndexThatExpandsTooMuchIsRefusedBeforeAnythingIsWritten()
    {
        using var t = new TempDir();
        var data = new byte[1000];
        string path = t.Sub("bomb.osa");
        using (var w = new OsaWriter(path))
        {
            var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", MethodRegistry.Default), data, CodecContext.None);
            int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, data.Length);
            w.Finish(Enumerable.Range(0, 50).Select(i => new ArchiveEntry
            {
                Path = $"copy{i:D2}.bin", Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(frame, 0, data.Length)],
                Hash = XxHash128.HashToUInt128(data),
            }));
            w.Commit(overwrite: false);
        }

        var res = Container.Salvage.Run(path, t.Sub("out"), null, new SalvageOptions { MaxExpansion = 10 }, Ct);
        Assert.Equal(1, res.ExitCode);
        Assert.Contains("expands 50×", res.Refused);
        Assert.False(Directory.Exists(t.Sub("out")));
        Assert.Equal(50, Container.Salvage.Run(path, t.Sub("out"), null, null, Ct).FilesRecovered); // no limit by default
    }

    // ------------------------------------------------------------------ a newer version is not damage

    /// <summary>A codec of a newer Osmium (method id 8; it stores its input).</summary>
    private sealed class FutureCodec : MethodBase
    {
        public override int Id => 8;
        public override string Name => "future";
        public override string DisplayName => "Future";
        public override MethodKind Kind => MethodKind.Codec;
        public override string Description => "a codec this version of Osmium does not have";
        public override byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx) => input.ToArray();
        public override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx) => input.ToArray();
    }

    private static readonly MethodRegistry Future = new(MethodRegistry.Default.All.Append(new FutureCodec()));

    /// <summary>Writes an archive the way a newer Osmium would: a.txt and c.txt in blocks this version decodes,
    /// b.bin in a block of method id 8, and d.txt split over blocks 0 and 1.</summary>
    private static string WriteFutureArchive(TempDir t, string indexPipeline = "zstd(level=19)")
    {
        var files = new[] { ("a.txt", "zstd(level=3)"), ("b.bin", "future"), ("c.txt", "store") };
        string path = t.Sub("future.osa");
        using var w = new OsaWriter(path, new OsaWriterOptions { IndexPipeline = indexPipeline });
        var entries = new List<ArchiveEntry>();
        var blocks = new List<byte[]>();
        foreach (var (name, pipeline) in files)
        {
            var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat($"{name} from a newer version. ", 80)));
            File.WriteAllBytes(t.Sub(name), data);
            var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse(pipeline, Future), data, CodecContext.None);
            int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, data.Length);
            blocks.Add(data);
            entries.Add(new ArchiveEntry { Path = name, Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(frame, 0, data.Length)], Hash = XxHash128.HashToUInt128(data) });
        }
        var d = blocks[0].AsSpan(0, 100).ToArray().Concat(blocks[1].AsSpan(0, 100).ToArray()).ToArray();
        File.WriteAllBytes(t.Sub("d.txt"), d);
        entries.Add(new ArchiveEntry { Path = "d.txt", Kind = EntryKind.File, Size = d.Length, Segments = [new Segment(0, 0, 100), new Segment(1, 0, 100)], Hash = XxHash128.HashToUInt128(d) });
        w.Finish(entries, null, Future);
        w.Commit(overwrite: false);
        return path;
    }

    private const string Newer = "needs block 1, which needs a newer version of Osmium (it uses method id 8)";

    [Fact]
    public void FilesInBlocksOfANewerVersionAreListedApartFromDamage()
    {
        using var t = new TempDir();
        var path = WriteFutureArchive(t);

        var res = Salvage(t, path);
        Assert.Equal(2, res.ExitCode);
        Assert.Empty(res.Lost);
        Assert.Equal([new SalvageLoss("b.bin", Newer), new SalvageLoss("d.txt", Newer)], res.NeedNewerVersion);
        Assert.Equal(["a.txt", "c.txt"], res.Written);
        Assert.Equal(SalvageBlockState.NeedsNewerVersion, res.Blocks[1].State);
        Assert.Equal("it uses method id 8", res.Blocks[1].Problem);
        Assert.Equal(SalvageFrameState.NeedsNewerVersion, res.Blocks[1].Frame!.State);
        Assert.DoesNotContain(res.Frames, f => f.State == SalvageFrameState.Damaged);
        Assert.Equal(0, res.UnrecognizedBytes); // intact, so accounted for
        Assert.Contains($"  needs a newer Osmium: b.bin — {Newer}", res.Summary());
        Assert.Contains("2 file(s) need a newer version of Osmium", res.Summary().Last());
    }

    [Fact]
    public void WithoutTheIndexBlocksOfANewerVersionAreStillNotDamage()
    {
        using var t = new TempDir();
        var path = WriteFutureArchive(t);
        long indexAt;
        using (var fs = File.OpenRead(path)) indexAt = ArchiveFooter.Read(fs).IndexOffset;
        var bad = Damage(t, path, b => Flip(b, indexAt + 40, 4));

        var res = Salvage(t, bad);
        Assert.Equal(2, res.ExitCode);
        Assert.False(res.IndexRecovered);
        Assert.Equal(["block-0.bin", "block-2.bin"], res.Written);
        Assert.Equal(SalvageBlockState.NeedsNewerVersion, res.Blocks[1].State);
        Assert.False(File.Exists(t.Sub("out/block-1.bin")));
        Assert.Contains("1 block(s) need a newer version of Osmium", res.Summary().Last());
        Assert.Contains("block 1: needs a newer version of Osmium (it uses method id 8)", File.ReadAllText(res.ReportPath!));
    }

    [Fact]
    public void AnIndexOfANewerVersionIsReportedAsSuch()
    {
        using var t = new TempDir();
        var res = Salvage(t, WriteFutureArchive(t, indexPipeline: "future"));
        Assert.False(res.IndexRecovered);
        Assert.Equal("the index needs a newer version of Osmium (it uses method id 8)", res.IndexProblem);
        Assert.Equal(SalvageFrameState.NeedsNewerVersion, res.Frames.Single(f => f.IsIndex).State);
        Assert.Equal(["block-0.bin", "block-2.bin"], res.Written);
    }

    [Fact]
    public async Task AFrameFlagOfANewerVersionIsNotDamage()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        int k = 1;
        var bad = Damage(t, path, b =>
        {
            int at = (int)l.Blocks[k].Offset;
            var h = b.GetRange(at, Osa.FrameHeaderSize).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)) | 4);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), Osa.Crc(h.AsSpan(0, 24)));
            for (int i = 0; i < h.Length; i++) b[at + i] = h[i];
        });

        var res = Salvage(t, bad);
        Assert.Equal(2, res.ExitCode);
        Assert.Equal(SalvageBlockState.NeedsNewerVersion, res.Blocks[k].State);
        Assert.Equal("it uses frame flag 0x4", res.Blocks[k].Problem);
        Assert.Empty(res.Lost);
        foreach (var e in l.Files)
        {
            bool newer = l.Touches(e, k);
            Assert.Equal(newer, res.NeedNewerVersion.Any(x => x.Path == e.Path));
            Assert.Equal(!newer, File.Exists(Path.Combine(t.Sub("out"), e.Path)));
        }
    }

    // ------------------------------------------------------------------ damage anywhere

    [Fact]
    public async Task CorruptionAtAnyOffsetLosesOnlyWhatItDamaged()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Sub("in/sub"));
        File.WriteAllText(t.Sub("in/a.txt"), string.Concat(Enumerable.Range(0, 120).Select(i => $"sweep line {i}\n")));
        var rnd = new byte[1500];
        new Random(5).NextBytes(rnd);
        File.WriteAllBytes(t.Sub("in/sub/b.bin"), rnd);
        File.WriteAllBytes(t.Sub("in/sub/c.dat"), [.. Enumerable.Range(0, 2000).Select(i => (byte)(i * 7 % 251))]);
        var path = await Build(t);
        var l = Read(path);
        Assert.True(l.Blocks.Count >= 2);
        var original = File.ReadAllBytes(path);
        var offsets = new SortedSet<long>(Enumerable.Range(0, original.Length / 23).Select(i => i * 23L))
        {
            0, l.HeaderLength - 1, l.IndexOffset, l.IndexOffset + 27, l.IndexOffset + l.IndexLength - 1, l.Length - Osa.FooterSize, l.Length - 1,
        };
        foreach (var b in l.Blocks) offsets.UnionWith([b.Offset, b.Offset + 27, b.Offset + 28, b.Offset + b.FrameLength - 1]);

        foreach (long at in offsets)
        {
            var bad = (byte[])original.Clone();
            bad[at] ^= 0x41;
            File.WriteAllBytes(t.Sub("damaged.osa"), bad);
            if (Directory.Exists(t.Sub("out"))) Directory.Delete(t.Sub("out"), recursive: true);
            var res = Salvage(t, t.Sub("damaged.osa"));
            int k = l.Blocks.ToList().FindIndex(b => at >= b.Offset && at < b.Offset + b.FrameLength);
            if (k >= 0)
            {
                Assert.Equal(2, res.ExitCode);
                AssertFilesRecovered(t, l, res, k);
            }
            else if (at >= l.IndexOffset && at < l.IndexOffset + l.IndexLength)
            {
                Assert.Equal(2, res.ExitCode);
                Assert.False(res.IndexRecovered);
                foreach (var b in l.Blocks) Assert.Equal(AssembleBlock(l, b.Frame, t.Path), File.ReadAllBytes(t.Sub($"out/block-{b.Frame}.bin")));
            }
            else
            {
                Assert.True(res.ExitCode == 0, $"damage at {at} (header or footer) lost data: {string.Join(" | ", res.Summary())}");
                AssertFilesRecovered(t, l, res);
            }
        }
    }

    // ------------------------------------------------------------------ an archive stored in block 0, the start damaged

    private const string InnerPassword = "inner secret";

    /// <summary>
    /// An archive whose block 0 stores three photos, then another archive verbatim (backups/2025-01.osa, holding
    /// inner_src/*) and then padding.png, and whose block 1 holds notes.txt. Already-compressed formats share one stored
    /// group, whose files are ordered by extension. Returns where the stored archive starts in the file.
    /// </summary>
    private static async Task<(Layout Outer, long InnerAt, long InnerLength)> BuildWithStoredArchive(TempDir t, string? innerPassword = null,
        bool newerInner = false, int innerFiles = 1)
    {
        var rnd = new Random(23);
        Directory.CreateDirectory(t.Sub("inner_src"));
        for (int i = 0; i < innerFiles; i++)
        {
            var data = new byte[innerFiles == 1 ? 3000 : 70_000];
            rnd.NextBytes(data);
            File.WriteAllBytes(t.Sub($"inner_src/inner{i}.bin"), data);
        }
        var inner = File.ReadAllBytes(await Compress(t.Sub("inner_src"), t.Sub("inner.osa"), innerPassword, blockCap: 64 << 10));
        if (newerInner)
        {
            int headerLength = 36 + (int)BinaryPrimitives.ReadUInt32LittleEndian(inner.AsSpan(32)) + 4;
            BinaryPrimitives.WriteUInt32LittleEndian(inner.AsSpan(12), BinaryPrimitives.ReadUInt32LittleEndian(inner.AsSpan(12)) | 2);
            BinaryPrimitives.WriteUInt32LittleEndian(inner.AsSpan(headerLength - 4), Osa.Crc(inner.AsSpan(0, headerLength - 4)));
        }
        Directory.CreateDirectory(t.Sub("in/photos"));
        Directory.CreateDirectory(t.Sub("in/backups"));
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0], png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        foreach (var (name, signature) in new[] { ("photos/img0.jpg", jpeg), ("photos/img1.jpg", jpeg), ("photos/img2.jpg", jpeg), ("photos/padding.png", png) })
        {
            var data = new byte[30_000];
            rnd.NextBytes(data);
            signature.CopyTo(data, 0);
            File.WriteAllBytes(t.Sub("in/" + name), data);
        }
        File.WriteAllBytes(t.Sub("in/backups/2025-01.osa"), inner);
        File.WriteAllText(t.Sub("in/notes.txt"), string.Concat(Enumerable.Range(0, 1200).Select(i => $"note {i}: salvage keeps what is this archive's\n")));
        var path = await Build(t, blockCap: 16 << 20);
        var l = Read(path);
        Assert.Equal(0, Assert.Single(l.Files.Single(e => e.Path.EndsWith("2025-01.osa", StringComparison.Ordinal)).Segments).Frame);
        Assert.Equal(l.HeaderLength, l.Blocks[0].Offset);
        long innerAt = File.ReadAllBytes(path).AsSpan().IndexOf(inner.AsSpan(0, 40));
        Assert.True(innerAt > 65_536, $"the stored archive starts at {innerAt}");
        return (l, innerAt, inner.Length);
    }

    private static void DamageStart(List<byte> b, string how)
    {
        var words = how.Split(' ');
        if (words[0] == "cut")
        {
            b.RemoveRange(0, int.Parse(words[1]));
            return;
        }
        int count = int.Parse(words[1]), from = words.Length > 3 ? int.Parse(words[3]) : 0;
        for (int i = from; i < from + count; i++) b[i] = 0;
    }

    /// <summary>This archive's index is used: block 0 (the photos and the stored archive) is lost, notes.txt is back,
    /// and nothing of the stored archive is written as if it were this archive's.</summary>
    private static void AssertOnlyBlock0Lost(TempDir t, Layout l, SalvageResult res)
    {
        Assert.Equal(2, res.ExitCode);
        Assert.False(res.Complete);
        Assert.Equal(l.Files.Count(), res.FilesTotal);
        AssertFilesRecovered(t, l, res, 0);
        Assert.Empty(Directory.GetFiles(t.Sub("out"), "inner*", SearchOption.AllDirectories));
        Assert.DoesNotContain("Everything was recovered", res.Summary().Last());
    }

    public static TheoryData<string> StartDamage =>
        ["zero 8 at 50", "zero 60", "zero 83", "zero 100", "zero 512", "zero 4096", "zero 65536", "cut 4096"];

    [Theory]
    [MemberData(nameof(StartDamage))]
    public async Task WithTheStartDamagedAnArchiveStoredInBlock0IsNotTakenForThisOne(string how)
    {
        using var t = new TempDir();
        var (l, _, _) = await BuildWithStoredArchive(t);
        var res = Salvage(t, Damage(t, t.Sub("a.osa"), b => DamageStart(b, how)));
        AssertOnlyBlock0Lost(t, l, res);
        Assert.False(res.HeaderIntact);
        Assert.Contains(res.Frames, f => f.IsIndex && f.State == SalvageFrameState.Ignored);
    }

    [Theory]
    [InlineData("zero 512")]
    [InlineData("zero 4096")]
    public async Task AnEncryptedArchiveStoredInBlock0NeitherAsksForAPasswordNorOutvotesThisOne(string how)
    {
        using var t = new TempDir();
        var (l, _, _) = await BuildWithStoredArchive(t, innerPassword: InnerPassword, innerFiles: 6);
        int asked = 0;
        var res = Salvage(t, Damage(t, t.Sub("a.osa"), b => DamageStart(b, how)), () => { asked++; return null; });
        Assert.Equal(0, asked);
        Assert.False(res.Encrypted);
        AssertOnlyBlock0Lost(t, l, res);
    }

    [Fact]
    public async Task AStoredArchiveOfANewerVersionDoesNotStopSalvage()
    {
        using var t = new TempDir();
        var (l, _, _) = await BuildWithStoredArchive(t, newerInner: true);
        AssertOnlyBlock0Lost(t, l, Salvage(t, Damage(t, t.Sub("a.osa"), b => DamageStart(b, "zero 512"))));
    }

    [Fact]
    public async Task WithTheStartDamagedAndTheFooterCutThisArchivesIndexStillWins()
    {
        using var t = new TempDir();
        var (l, _, _) = await BuildWithStoredArchive(t);
        var res = Salvage(t, Damage(t, t.Sub("a.osa"), b => { DamageStart(b, "zero 512"); b.RemoveRange(b.Count - 4, 4); }));
        AssertOnlyBlock0Lost(t, l, res);
        Assert.Contains(res.Frames, f => f.IsIndex && f.State == SalvageFrameState.Ignored && f.Problem!.Contains("within the blocks of the index", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithTheStartDamagedAndTheIndexGoneTheStoredArchiveNeverTakesThisArchivesBlockNames()
    {
        using var t = new TempDir();
        var (l, _, _) = await BuildWithStoredArchive(t);
        var res = Salvage(t, Damage(t, t.Sub("a.osa"), b => { DamageStart(b, "zero 512"); b.RemoveRange((int)l.IndexOffset, b.Count - (int)l.IndexOffset); }));
        Assert.Equal(2, res.ExitCode);
        Assert.False(res.IndexRecovered);
        Assert.Equal(AssembleBlock(l, 1, t.Path), File.ReadAllBytes(t.Sub("out/block-1.bin")));
        Assert.False(File.Exists(t.Sub("out/block-0.bin")));
        var innerBlock = Assert.Single(res.Frames, f => !f.IsIndex && f.ForeignIndexOffset is not null);
        Assert.Equal($"block-0-at-{innerBlock.Offset}.bin", innerBlock.Output);
        Assert.Contains("which belongs to another archive", File.ReadAllText(res.ReportPath!));
    }

    [Fact]
    public async Task WithTheHeaderDamagedAnIndexInsideADamagedBlockIsNotUsed()
    {
        using var t = new TempDir();
        var (l, innerAt, innerLength) = await BuildWithStoredArchive(t);
        int cut = (int)(innerAt + innerLength + 5000); // inside padding.png, so inside block 0
        var res = Salvage(t, Damage(t, t.Sub("a.osa"), b =>
        {
            for (int i = 0; i < l.HeaderLength; i++) b[i] = 0;
            b.RemoveRange(cut, b.Count - cut);
        }));
        Assert.Equal(2, res.ExitCode);
        Assert.False(res.IndexRecovered);
        Assert.Contains("inside the damaged frame", res.IndexProblem);
        Assert.False(File.Exists(t.Sub("out/block-0.bin")));
        Assert.Empty(Directory.GetFiles(t.Sub("out"), "inner*", SearchOption.AllDirectories));
    }

    // ------------------------------------------------------------------ crafted input: bounded work

    /// <summary>A plain frame of these raw bytes, as a writer makes it.</summary>
    private static byte[] StoreFrame(ulong number, byte[] raw, string pipeline = "store")
    {
        var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse(pipeline, MethodRegistry.Default), raw, CodecContext.None);
        var prefix = new BlockBody(desc, BlockBody.Hash128(raw), payload).SerializePrefix();
        return [.. FakeFrameHeader(0, number, prefix.Length + payload.Length), .. prefix, .. payload];
    }

    // ------------------------------------------------------------------ index lost: what happened to every block

    private static string WithoutIndex(TempDir t, string path, Layout l) =>
        Damage(t, path, b => { for (long i = l.IndexOffset; i < l.IndexOffset + l.IndexLength; i++) b[(int)i] = 0; });

    [Fact]
    public async Task ABlockFoundOnlyInsideADamagedOneIsReportedAsSuch()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        var b1 = l.Blocks[1];
        var bad = Damage(t, WithoutIndex(t, path, l), b => b.RemoveRange((int)(b1.Offset + b1.FrameLength / 2), 300));

        var res = Salvage(t, bad);
        var b2 = res.Blocks[2];
        Assert.Equal(SalvageBlockState.Missing, b2.State);
        Assert.Contains("lies inside the damaged frame", b2.Problem);
        var inside = Assert.Single(res.Frames, f => f.Number == 2 && f.Output is not null);
        Assert.Contains(inside.Output!, b2.Problem);
        Assert.Equal(AssembleBlock(l, 2, t.Path), File.ReadAllBytes(t.Sub("out/" + inside.Output)));
    }

    [Fact]
    public async Task SalvagingAgainWithSkipSaysTheBlockFilesWereAlreadyThere()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        var bad = WithoutIndex(t, path, l);
        Assert.Equal(l.Blocks.Count, Salvage(t, bad).Written.Count);

        var again = Salvage(t, bad, options: new SalvageOptions { Overwrite = OverwriteMode.Skip });
        Assert.Empty(again.Written);
        Assert.Equal(l.Blocks.Count, again.FilesSkipped);
        Assert.All(again.Blocks, b => Assert.Equal(SalvageBlockState.NotWritten, b.State));
        Assert.Contains("already in the destination", again.Blocks[0].Problem);
        Assert.DoesNotContain("Nothing could be recovered", again.Summary().Last());
        Assert.Contains("left as they were", again.Summary().Last());
    }

    [Fact]
    public async Task ABlockFileThatCannotBeWrittenIsReportedWithTheReason()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        Directory.CreateDirectory(t.Sub("out/block-1.bin")); // a folder where the block file should go

        var res = Salvage(t, WithoutIndex(t, path, l));
        Assert.Equal(SalvageBlockState.NotWritten, res.Blocks[1].State);
        var loss = Assert.Single(res.Lost);
        Assert.Equal("block-1.bin", loss.Path);
        Assert.Contains(res.Summary(), s => s.StartsWith("  not written: block-1.bin", StringComparison.Ordinal));
        Assert.Contains("Not written:  block-1.bin", File.ReadAllText(res.ReportPath!));
    }

    [Fact]
    public async Task WithTheIndexIntactSkippedFilesAreNamedInTheVerdict()
    {
        using var t = new TempDir();
        var path = await Build(t);
        Assert.Equal(0, Salvage(t, path).ExitCode);
        var again = Salvage(t, path, options: new SalvageOptions { Overwrite = OverwriteMode.Skip });
        Assert.Equal(0, again.ExitCode);
        Assert.Equal(Read(path).Files.Count(), again.FilesSkipped);
        Assert.EndsWith("file(s) already in the destination were left as they were.", again.Summary().Last());
    }

    // ------------------------------------------------------------------ size and space

    [Fact]
    public void WithoutTheIndexBlocksThatExpandTooMuchAreRefusedBeforeAnythingIsWritten()
    {
        using var t = new TempDir();
        File.WriteAllBytes(t.Sub("zeros.osa"), [.. StoreFrame(0, new byte[1 << 20], "zstd(level=3)"), .. StoreFrame(1, new byte[1 << 20], "zstd(level=3)")]);
        var res = Salvage(t, t.Sub("zeros.osa"), options: new SalvageOptions { MaxExpansion = 200 });
        Assert.Equal(1, res.ExitCode);
        Assert.Contains("the size of the archive", res.Refused);
        Assert.False(Directory.Exists(t.Sub("out")));
        Assert.Equal(2, Salvage(t, t.Sub("zeros.osa")).Written.Count(w => w.StartsWith("block-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WhatDoesNotFitInTheFreeSpaceIsRefusedBeforeAnythingIsWritten()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var full = new SalvageOptions { CheckFreeSpace = true, FreeSpaceProbe = _ => 1000 };
        var res = Salvage(t, path, options: full);
        Assert.Equal(1, res.ExitCode);
        Assert.StartsWith("not enough free space", res.Refused);
        Assert.False(Directory.Exists(t.Sub("out")));

        var l = Read(path);
        var noIndex = Damage(t, path, b => { for (long i = l.IndexOffset; i < l.IndexOffset + l.IndexLength; i++) b[(int)i] = 0; });
        Assert.StartsWith("not enough free space", Salvage(t, noIndex, options: full).Refused);
        Assert.False(Directory.Exists(t.Sub("out")));
        Assert.Equal(0, Salvage(t, path, options: full with { FreeSpaceProbe = _ => 1L << 40 }).ExitCode);
    }

    [Fact]
    public void DecodingIsChargedForTheLargestStageNotTheBlockSize()
    {
        // A 16-byte block whose zstd stage claims to produce 1 GiB for the text filter: zstd allocates that up front.
        var crafted = BlockDescriptor.Deserialize(new BlobWriter().U(2)
            .U(MethodIds.Text).U(16).Bytes(ParamValues.Empty.Serialize())
            .U(MethodIds.Zstd).U(1 << 30).Bytes(ParamValues.Empty.Serialize()).ToArray(), MethodRegistry.Default);
        Assert.Equal(16, crafted.RawSize);
        Assert.True(SalvageRun.DecodeCharge(crafted) >= 2L << 30);

        var honest = PipelineSpec.Parse("zstd(level=3)", MethodRegistry.Default);
        var (desc, _) = PipelineExecutor.Encode(honest, new byte[100_000], CodecContext.None);
        Assert.Equal(honest.DecodeMemory(100_000) + 200_000, SalvageRun.DecodeCharge(desc));
    }

    [Fact]
    public void ABlockWithAWrappedSizeNeverHidesAnExpansion()
    {
        using var t = new TempDir();
        var store = new BlobWriter().U(1).U(0).U(1000).Bytes(ParamValues.Empty.Serialize()).ToArray();
        var index = new BlobWriter().U(ArchiveIndex.Version).U(2)
            .Bytes(new BlobWriter().U(0).U(1100).U(1000).U(1).Bytes(store).ToArray())
            .Bytes(new BlobWriter().U(0).U(1100).U((1UL << 63) + 5).U(1).Bytes(store).ToArray()) // reads as a negative size
            .U(50);
        for (int i = 0; i < 50; i++)
        {
            var name = Encoding.ASCII.GetBytes($"f{i:D2}.bin");
            index.Bytes(new BlobWriter().U(0).U((ulong)name.Length).Raw(name).B((byte)EntryKind.File).U(0)
                .U(1000).U(1).U(0).U(0).U(1000).Raw(new byte[16]).ToArray());
        }
        var path = ArchiveWithIndex(t, index.U(0).ToArray());

        // The index reader refuses a size that wraps around, so that index is never used to write anything.
        var res = Salvage(t, path, options: new SalvageOptions { MaxExpansion = 10 });
        Assert.Equal(2, res.ExitCode);
        Assert.False(res.IndexRecovered);
        Assert.Contains("the index is unreadable", res.IndexProblem);
        Assert.Empty(res.Written);
    }

    /// <summary>Frame headers 80 bytes apart, each followed by a block prefix that checks out, claiming a body that runs
    /// to the end of the file and a payload hash that does not match it.</summary>
    private static byte[] StackedFakeFrames(int count)
    {
        const int spacing = 80;
        var file = new byte[count * spacing];
        var desc = new BlobWriter().U(1).U(0).U(1000).Bytes(ParamValues.Empty.Serialize()).ToArray();
        for (int i = 0; i < count; i++)
        {
            int at = i * spacing;
            long body = file.Length - at - Osa.FrameHeaderSize;
            for (int varintLength = 1; ; varintLength++)
            {
                long payload = body - (1 + desc.Length + 16 + 8 + varintLength + 4);
                var head = new BlobWriter().Bytes(desc).Raw(new byte[16]).U64(0xDEAD).U((ulong)payload).ToArray();
                if (head.Length != 1 + desc.Length + 16 + 8 + varintLength) continue;
                FakeFrameHeader(0, (ulong)i, body).CopyTo(file, at);
                head.CopyTo(file, at + Osa.FrameHeaderSize);
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + Osa.FrameHeaderSize + head.Length), Osa.Crc(head));
                break;
            }
        }
        return file;
    }

    [Fact]
    public void StackedFramesThatFailCostABoundedAmountOfWork()
    {
        using var t = new TempDir();
        var bytes = StackedFakeFrames(20_000); // 1.6 MB; checking every frame to the end would read about 16 GB
        File.WriteAllBytes(t.Sub("stacked.osa"), bytes);
        using (var src = new SalvageSource(t.Sub("stacked.osa")))
        {
            var scanner = new FrameScanner(src, MethodRegistry.Default, null, Ct);
            var frames = scanner.ScanAll(0);
            Assert.Equal(20_000, frames.Count);
            Assert.All(frames, f => Assert.False(f.Intact));
            Assert.True(scanner.BytesChecked < (64L << 20) + 3L * bytes.Length, $"{scanner.BytesChecked:N0} bytes checked");
            Assert.Contains(frames, f => f.Problem!.StartsWith("not checked", StringComparison.Ordinal));
        }
        var res = Salvage(t, t.Sub("stacked.osa"));
        Assert.Equal(2, res.ExitCode);
        Assert.Empty(res.Written);
    }

    [Fact]
    public void ManyFramesOfOneNumberAreEachWrittenOnceUnderTheirOwnName()
    {
        using var t = new TempDir();
        var file = new List<byte>();
        var expected = new Dictionary<long, byte[]>();
        for (int i = 0; i < 3000; i++)
        {
            var raw = Encoding.ASCII.GetBytes($"frame {i} of block 0");
            expected[file.Count] = raw;
            file.AddRange(StoreFrame(0, raw));
        }
        File.WriteAllBytes(t.Sub("same.osa"), [.. file]);

        var res = Salvage(t, t.Sub("same.osa"));
        Assert.Equal(3000, res.Written.Count);
        foreach (var f in res.Frames)
            Assert.Equal(expected[f.Offset], File.ReadAllBytes(t.Sub("out/" + f.Output))); // nothing mixed up between frames
        Assert.Equal("block-0.bin", res.Frames[0].Output);
    }

    [Fact]
    public void ExtraBlockFilesAreCapped()
    {
        using var t = new TempDir();
        var file = new List<byte>();
        for (int i = 0; i < 50; i++) file.AddRange(StoreFrame(0, Encoding.ASCII.GetBytes($"copy {i}")));
        File.WriteAllBytes(t.Sub("same.osa"), [.. file]);

        var res = Salvage(t, t.Sub("same.osa"), options: new SalvageOptions { MaxExtraBlockFiles = 10 });
        Assert.Equal(11, res.Written.Count);
        Assert.Equal(39, res.Frames.Count(f => f.Problem?.StartsWith("not written", StringComparison.Ordinal) == true));
        Assert.Equal(39, res.FramesSetAside);
    }

    [Fact]
    public async Task WithoutTheIndexFramesThatStopFollowingOneAnotherAreNoted()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        Directory.CreateDirectory(t.Sub("other"));
        File.WriteAllText(t.Sub("other/elsewhere.txt"), string.Concat(Enumerable.Range(0, 3000).Select(i => $"another archive, line {i}\n")));
        var otherPath = await Compress(t.Sub("other"), t.Sub("other.osa"));
        var other = Read(otherPath);
        // this archive's blocks 0 and 1, then the other archive's blocks without its index: nothing ties them to either
        var fragment = File.ReadAllBytes(otherPath)[(int)other.Blocks[0].Offset..(int)other.PayloadEnd(other.Blocks.Count - 1)];
        int cut = (int)l.Blocks[2].Offset;
        var res = Salvage(t, Damage(t, path, b => { b.RemoveRange(cut, b.Count - cut); b.AddRange(fragment); }));

        Assert.False(res.IndexRecovered);
        Assert.Contains(res.Notes, n => n.StartsWith($"At offset {cut:N0} the frames stop following one another", StringComparison.Ordinal));
        for (int k = 0; k < 2; k++) Assert.Equal(AssembleBlock(l, k, t.Path), File.ReadAllBytes(t.Sub($"out/block-{k}.bin")));
    }

    [Fact]
    public async Task AnotherArchiveAfterATruncationIsNotTakenForThisOne()
    {
        using var t = new TempDir();
        var path = await Build(t);
        var l = Read(path);
        Directory.CreateDirectory(t.Sub("other"));
        File.WriteAllText(t.Sub("other/elsewhere.txt"), string.Concat(Enumerable.Range(0, 3000).Select(i => $"another archive, line {i}\n")));
        var other = File.ReadAllBytes(await Compress(t.Sub("other"), t.Sub("other.osa")));
        int cut = (int)(l.Blocks[2].Offset + l.Blocks[2].FrameLength / 2);
        var res = Salvage(t, Damage(t, path, b => { b.RemoveRange(cut, b.Count - cut); b.AddRange(other); }));

        Assert.Equal(2, res.ExitCode);
        Assert.False(res.IndexRecovered);
        Assert.Contains("names another archive", res.IndexProblem);
        for (int k = 0; k < 2; k++) Assert.Equal(AssembleBlock(l, k, t.Path), File.ReadAllBytes(t.Sub($"out/block-{k}.bin")));
        Assert.All(res.Frames.Where(f => f.Offset >= cut && f.Output is not null), f => Assert.Contains("-at-", f.Output));
        Assert.Empty(Directory.GetFiles(t.Sub("out"), "elsewhere.txt", SearchOption.AllDirectories));
    }
}
