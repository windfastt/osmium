using System.Buffers.Binary;
using System.IO.Hashing;
using Osmium.Core.Container;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests;

/// <summary>Small archives written entry by entry, with the spare-copy threshold and the other writer test options
/// set, and the positions of everything at the end of the file.</summary>
internal static class SpareArchives
{
    public const string Password = "spare me";
    private static readonly MethodRegistry Reg = MethodRegistry.Default;

    /// <summary>"f0.bin" … with random content, one stored block per file, plus a folder.</summary>
    public static Dictionary<string, byte[]> Files(int count, int size, int seed = 5)
    {
        var rnd = new Random(seed);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            var data = new byte[size + i];
            rnd.NextBytes(data);
            files[$"docs/f{i}.bin"] = data;
        }
        return files;
    }

    public static string Write(string path, IReadOnlyDictionary<string, byte[]> files, OsaWriterOptions? options = null,
        IReadOnlyDictionary<string, byte[]>? metadata = null, Action<OsaWriter>? beforeCommit = null)
    {
        using var w = new OsaWriter(path, (options ?? new OsaWriterOptions()) with { KdfIterations = 1000 });
        var entries = new List<ArchiveEntry> { new() { Path = "docs", Kind = EntryKind.Directory, ModifiedUtc = new DateTime(2025, 1, 2, 3, 4, 6, DateTimeKind.Utc) } };
        foreach (var (name, data) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", Reg), data, CodecContext.None);
            int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, data.Length);
            entries.Add(new ArchiveEntry
            {
                Path = name, Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(frame, 0, data.Length)],
                Hash = XxHash128.HashToUInt128(data), ModifiedUtc = new DateTime(2025, 1, 2, 3, 4, 6, DateTimeKind.Utc),
            });
        }
        w.Finish(entries, metadata ?? new Dictionary<string, byte[]> { ["profile"] = "fast"u8.ToArray() });
        beforeCommit?.Invoke(w);
        w.Commit(overwrite: true);
        return path;
    }

    /// <summary>Where the parts of an archive's end are, read from its bytes (not through the reader).</summary>
    public sealed record Tail(long HeaderLength, long IndexOffset, long IndexLength, long BlocksEnd, SpareTrailer? Trailer)
    {
        public long HeaderCopyOffset => IndexOffset - Trailer!.Value.CopyLength;
        public long SpareOffset => HeaderCopyOffset - IndexLength;
        public long TrailerOffset => IndexOffset - SpareCopies.TrailerSize;
    }

    public static Tail TailOf(string path, string? password = null)
    {
        var b = File.ReadAllBytes(path);
        long indexOffset = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(b.Length - 32));
        long indexLength = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(b.Length - 24));
        using var r = OsaReader.Open(path, () => password);
        long blocksEnd = r.Blocks.Count == 0 ? r.Header.Length : r.Blocks[^1].Offset + r.Blocks[^1].FrameLength;
        var trailer = indexOffset >= 32 ? SpareTrailer.Read(b.AsSpan((int)indexOffset - 32, 32)) : null;
        return new Tail(r.Header.Length, indexOffset, indexLength, blocksEnd, trailer);
    }

    /// <summary>
    /// An unencrypted archive written byte by byte, so that its index can say anything about spare copies:
    /// <paramref name="sparesValue"/> gets B's offset and returns the value of the spares key (null: no key), and
    /// <paramref name="tail"/> says whether B and H are written at all, and <paramref name="lastBlockLonger"/> makes the
    /// index say that the last block's frame is that many bytes longer than it is.
    /// </summary>
    public static string Craft(string path, IReadOnlyDictionary<string, byte[]> files, Func<long, byte[]?> sparesValue, bool tail = true,
        bool newerIndex = false, string framePipeline = "store", Func<BlockDescriptor, BlockDescriptor>? indexDescriptor = null,
        long lastBlockLonger = 0)
    {
        var header = new ArchiveHeader { Creator = "test" };
        var headerBytes = header.Serialize();
        using var fs = new FileStream(path, FileMode.Create);
        fs.Write(headerBytes);
        var index = new ArchiveIndex();
        foreach (var (name, data) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse(framePipeline, Reg), data, CodecContext.None);
            long offset = fs.Position;
            var frame = Frame((ulong)index.Blocks.Count, 0, [.. new BlockBody(desc, BlockBody.Hash128(data), payload).SerializePrefix(), .. payload]);
            fs.Write(frame);
            index.Entries.Add(new ArchiveEntry { Path = name, Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(index.Blocks.Count, 0, data.Length)], Hash = XxHash128.HashToUInt128(data) });
            long claimed = frame.Length + (index.Blocks.Count == files.Count - 1 ? lastBlockLonger : 0);
            index.Blocks.Add(new BlockInfo(index.Blocks.Count, offset, claimed, data.Length, data.Length, indexDescriptor?.Invoke(desc) ?? desc));
        }
        long spareOffset = fs.Position;
        if (sparesValue(spareOffset) is { } v) index.Metadata[ArchiveIndex.SparesKey] = v;
        var raw = index.Serialize();
        var (idesc, ipayload) = PipelineExecutor.Encode(PipelineSpec.Parse("zstd(level=3)", Reg), raw, CodecContext.None);
        if (newerIndex)
        {
            // The index stored with method id 63, which no version knows yet: as if a newer Osmium had written it.
            var blob = new BlobWriter().U(1).U(63).U((ulong)raw.Length).Bytes([0]).ToArray();
            (idesc, ipayload) = (BlockDescriptor.DeserializeOrStandIn(blob, Reg, "the index"), raw);
        }
        var indexFrame = Frame(Osa.IndexFrameNumber, Osa.FrameFlagIndex, [.. new BlockBody(idesc, BlockBody.Hash128(raw), ipayload).SerializePrefix(), .. ipayload]);
        if (tail)
        {
            fs.Write(indexFrame);
            fs.Write(SpareCopies.HeaderCopy(headerBytes));
        }
        long indexOffset = fs.Position;
        fs.Write(indexFrame);
        fs.Write(new ArchiveFooter(indexOffset, indexFrame.Length, BinaryPrimitives.ReadUInt64LittleEndian(header.ArchiveId)).Serialize());
        return path;

        static byte[] Frame(ulong number, uint flags, byte[] body)
        {
            var f = new byte[Osa.FrameHeaderSize + body.Length];
            Osa.FrameMagic.CopyTo(f);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), flags);
            BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(8), number);
            BinaryPrimitives.WriteInt64LittleEndian(f.AsSpan(16), body.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(24), Osa.Crc(f.AsSpan(0, 24)));
            body.CopyTo(f, Osa.FrameHeaderSize);
            return f;
        }
    }

    public static string Damage(string archive, string output, Action<byte[]> damage)
    {
        var b = File.ReadAllBytes(archive);
        damage(b);
        File.WriteAllBytes(output, b);
        return output;
    }

    /// <summary>
    /// Opens an archive by the rules of container 1.0, as Osmium 0.1.x does: header (magic, major, critical flags, CRC;
    /// the minor is not looked at), footer, index location, index frame, then every block, checking every file's hash.
    /// Returns the files.
    /// </summary>
    public static Dictionary<string, byte[]> ReadAs10(string path, string? password = null)
    {
        var b = File.ReadAllBytes(path);
        Assert.True(b.AsSpan(0, 8).SequenceEqual(Osa.Magic));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12));
        Assert.Equal(0u, flags & 0xFFFF & ~Osa.FlagEncrypted);
        int ext = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32));
        int headerLength = 36 + ext + 4;
        Assert.Equal(Osa.Crc(b.AsSpan(0, 36 + ext)), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(36 + ext)));
        byte[] header = b[..headerLength], archiveId = b[16..32];
        ArchiveKey? key = null;
        if ((flags & Osa.FlagEncrypted) != 0)
        {
            // Extension record 1: type, length, kdf, iterations (varint), salt[32], cipher, verifier[16].
            var r = new BlobReader(b.AsSpan(36, ext));
            Assert.Equal(1UL, r.U());
            var enc = new BlobReader(r.Bytes(1 << 20));
            Assert.Equal(1, enc.B());
            int iterations = (int)enc.U();
            var salt = enc.Raw(32).ToArray();
            Assert.Equal(1, enc.B());
            var verifier = enc.Raw(16).ToArray();
            key = ArchiveKey.Derive(password!, salt, iterations);
            Assert.True(key.Matches(verifier));
        }
        var footer = b.AsSpan(b.Length - 32);
        Assert.True(footer[28..].SequenceEqual(Osa.FooterMagic));
        Assert.Equal(Osa.Crc(footer[..24]), BinaryPrimitives.ReadUInt32LittleEndian(footer[24..]));
        long indexOffset = BinaryPrimitives.ReadInt64LittleEndian(footer), indexLength = BinaryPrimitives.ReadInt64LittleEndian(footer[8..]);
        Assert.Equal(b.Length, indexOffset + indexLength + 32);
        var index = ArchiveIndex.Deserialize(Decode(indexOffset, indexLength, Osa.IndexFrameNumber), headerLength, Reg);
        foreach (var k in index.Blocks) Assert.True(k.Offset + k.FrameLength <= indexOffset);
        var blocks = index.Blocks.Select(k => Decode(k.Offset, k.FrameLength, (ulong)k.Frame)).ToList();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var e in index.Entries.Where(e => !e.IsDirectory))
        {
            var data = e.Segments.SelectMany(s => blocks[s.Frame].AsSpan((int)s.Offset, (int)s.Length).ToArray()).ToArray();
            Assert.Equal(e.Hash, XxHash128.HashToUInt128(data));
            files[e.Path] = data;
        }
        return files;

        byte[] Decode(long at, long length, ulong number)
        {
            var fh = b.AsSpan((int)at, Osa.FrameHeaderSize);
            Assert.True(fh[..4].SequenceEqual(Osa.FrameMagic));
            Assert.Equal(Osa.Crc(fh[..24]), BinaryPrimitives.ReadUInt32LittleEndian(fh[24..]));
            Assert.Equal(number, BinaryPrimitives.ReadUInt64LittleEndian(fh[8..]));
            var stored = b.AsSpan((int)at + Osa.FrameHeaderSize, (int)length - Osa.FrameHeaderSize).ToArray();
            var plain = key is null ? stored : key.Open(archiveId, number, stored, number == Osa.IndexFrameNumber ? header : []);
            var body = BlockBody.Parse(plain, Reg);
            var raw = PipelineExecutor.Decode(body.Descriptor, body.Payload, CodecContext.None);
            Assert.Equal(body.RawHash, BlockBody.Hash128(raw));
            return raw;
        }
    }
}

/// <summary>
/// Container 1.1: archives whose blocks total 1 MiB or more keep a byte copy of the index frame and a copy of the header
/// at their end, between the last block and the index frame, where readers of 1.0 never look.
/// </summary>
public class SpareCopyTests
{
    private static OsaWriterOptions Low(string? password = null) => new() { Password = password, SpareCopies = SpareCopyPolicy.Always };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSpareCopiesSitBetweenTheLastBlockAndTheIndex(bool encrypted)
    {
        using var t = new TempDir();
        string? pw = encrypted ? SpareArchives.Password : null;
        var files = SpareArchives.Files(3, 3000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low(pw));
        var tail = SpareArchives.TailOf(path, pw);
        var b = File.ReadAllBytes(path);

        var trailer = Assert.NotNull(tail.Trailer);
        Assert.Equal(0u, trailer.Flags);
        Assert.Equal(tail.HeaderLength, trailer.HeaderLength);
        Assert.Equal(Math.Max(SpareCopies.MinHeaderCopyLength, tail.HeaderLength + 32), trailer.CopyLength);
        Assert.True(trailer.CopyLength >= 4096);
        // B starts where the last block ends, and is A byte for byte (an encrypted archive repeats the same ciphertext).
        Assert.Equal(tail.BlocksEnd, tail.SpareOffset);
        Assert.Equal(b.AsSpan((int)tail.IndexOffset, (int)tail.IndexLength).ToArray(), b.AsSpan((int)tail.SpareOffset, (int)tail.IndexLength).ToArray());
        // H: the header exactly as at offset 0, then zeros, then the trailer.
        Assert.Equal(b.AsSpan(0, (int)tail.HeaderLength).ToArray(), b.AsSpan((int)tail.HeaderCopyOffset, (int)tail.HeaderLength).ToArray());
        Assert.All(b.AsSpan((int)(tail.HeaderCopyOffset + tail.HeaderLength), (int)trailer.PaddingLength).ToArray(), x => Assert.Equal(0, x));
        Assert.True(b.AsSpan((int)tail.TrailerOffset, 4).SequenceEqual("OHDR"u8));
        // The footer still points at A, the last thing before it; the index says where B is.
        Assert.Equal(b.Length, tail.IndexOffset + tail.IndexLength + Osa.FooterSize);
        using var r = OsaReader.Open(path, () => pw);
        Assert.Equal((ulong)tail.SpareOffset, BinaryPrimitives.ReadUInt64LittleEndian(r.Index.Metadata[ArchiveIndex.SparesKey]));
        Assert.Equal(1, r.Header.Minor);
    }

    [Fact]
    public void BlocksOfAtLeastTheThresholdGetSpareCopiesAndSmallerOnesDoNot()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 200_000); // a file list far under 2 % of the blocks
        string probe = SpareArchives.Write(t.Sub("probe.osa"), files, new OsaWriterOptions { SpareThreshold = long.MaxValue });
        long frames;
        using (var r = OsaReader.Open(probe)) frames = r.Blocks.Sum(k => k.FrameLength);

        string at = SpareArchives.Write(t.Sub("at.osa"), files, new OsaWriterOptions { SpareThreshold = frames });
        Assert.NotNull(SpareArchives.TailOf(at).Trailer);
        string under = SpareArchives.Write(t.Sub("under.osa"), files, new OsaWriterOptions { SpareThreshold = frames + 1 });
        var tail = SpareArchives.TailOf(under);
        Assert.Null(tail.Trailer);
        Assert.Equal(tail.BlocksEnd, tail.IndexOffset); // [header] [blocks] [index] [footer], as in 1.0
        using var u = OsaReader.Open(under);
        Assert.False(u.Index.Metadata.ContainsKey(ArchiveIndex.SparesKey));
        Assert.Equal(1, u.Header.Minor); // minor 1 with or without spare copies
    }

    [Fact]
    public void TheDefaultThresholdIsOneMebibyteOfBlockFrames()
    {
        Assert.Equal(1L << 20, new OsaWriterOptions().SpareThreshold);
        using var t = new TempDir();
        Assert.Null(SpareArchives.TailOf(SpareArchives.Write(t.Sub("small.osa"), SpareArchives.Files(1, 1_000_000))).Trailer);
        Assert.NotNull(SpareArchives.TailOf(SpareArchives.Write(t.Sub("big.osa"), SpareArchives.Files(1, 1 << 20))).Trailer);
    }

    [Fact]
    public void NeverMeansNoSpareCopiesAndAlwaysMeansSpareCopiesWheneverThereIsABlock()
    {
        using var t = new TempDir();
        var big = SpareArchives.Files(1, 1 << 20);
        Assert.Null(SpareArchives.TailOf(SpareArchives.Write(t.Sub("never.osa"), big, new OsaWriterOptions { SpareCopies = SpareCopyPolicy.Never })).Trailer);
        Assert.NotNull(SpareArchives.TailOf(SpareArchives.Write(t.Sub("always.osa"), SpareArchives.Files(1, 10), Low())).Trailer);
        using var w = new OsaWriter(t.Sub("empty.osa"), Low());
        w.Finish([new ArchiveEntry { Path = "only-a-folder", Kind = EntryKind.Directory }]);
        Assert.False(w.HasSpareCopies); // no block: nothing to keep a spare file list for
    }

    /// <summary>10,000 text files of 100–2,000 bytes, as many small files are: their file list is large next to them.</summary>
    private static async Task<(string Path, Dictionary<string, byte[]> Files)> ManySmallFiles(TempDir t, string name, Planning.BuildOptions options)
    {
        string input = t.Sub("small-" + name);
        var rnd = new Random(20261125);
        string[] words = ["archive", "block", "frame", "index", "header", "copy", "spare", "file", "folder", "name", "time", "size"];
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int i = 0; i < 10_000; i++)
        {
            var text = new System.Text.StringBuilder();
            int target = rnd.Next(100, 2001);
            while (text.Length < target) text.Append(words[rnd.Next(words.Length)]).Append(rnd.Next(100_000)).Append(i % 7 == 0 ? '\n' : ' ');
            var data = System.Text.Encoding.ASCII.GetBytes(text.ToString(0, target));
            string dir = Path.Combine(input, $"dir{i / 100:D2}");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"note-{i:D5}.txt"), data);
            files[$"small-{name}/dir{i / 100:D2}/note-{i:D5}.txt"] = data;
        }
        var plan = await new Planning.Planner(new Planning.PlanOptions { Profile = Planning.Profile.ByName("fast"), MemoryBudget = 1L << 30 })
            .BuildAsync(Planning.Scanner.Scan([input], ct: Ct), Ct);
        var r = await Planning.ArchiveBuilder.BuildAsync(plan, options with { OutputPath = t.Sub(name + ".osa"), MemoryBudget = 1L << 30 }, Ct);
        return (r.Path, files);
    }

    [Fact]
    public async Task ManySmallFilesGetNoSpareCopiesUnlessAskedFor()
    {
        using var t = new TempDir();
        var (auto, files) = await ManySmallFiles(t, "auto", new Planning.BuildOptions { OutputPath = "" });
        var tail = SpareArchives.TailOf(auto);
        Assert.True(tail.IndexOffset - tail.HeaderLength >= 1 << 20, "the fixture should have at least 1 MiB of blocks");
        Assert.True(tail.IndexLength * 50 > tail.IndexOffset - tail.HeaderLength, "the fixture should have a file list over 2 % of its blocks");
        Assert.Null(tail.Trailer);
        using (var r = OsaReader.Open(auto)) Assert.False(r.HasSpareCopies || r.Index.Metadata.ContainsKey(ArchiveIndex.SparesKey));

        var (always, alwaysFiles) = await ManySmallFiles(t, "always", new Planning.BuildOptions { OutputPath = "", SpareCopies = SpareCopyPolicy.Always });
        Assert.NotNull(SpareArchives.TailOf(always).Trailer);
        var read = SpareArchives.ReadAs10(always); // what 0.1.x reads, file by file
        Assert.Equal(alwaysFiles.Count, read.Count);
        foreach (var (name, data) in alwaysFiles) Assert.Equal(data, read[name]);
        Assert.Equal(files.Count, SpareArchives.ReadAs10(auto).Count);
    }

    [Fact]
    public async Task AFewLargeFilesGetSpareCopiesByDefault()
    {
        using var t = new TempDir();
        string input = t.Sub("mixed");
        Directory.CreateDirectory(Path.Combine(input, "sub"));
        var random = new byte[3 << 20];
        new Random(1).NextBytes(random);
        File.WriteAllBytes(Path.Combine(input, "random.bin"), random);
        File.WriteAllText(Path.Combine(input, "sub", "notes.txt"), string.Concat(Enumerable.Range(0, 2000).Select(i => $"line {i}\n")));
        var plan = await new Planning.Planner(new Planning.PlanOptions { Profile = Planning.Profile.ByName("fast"), MemoryBudget = 1L << 30 })
            .BuildAsync(Planning.Scanner.Scan([input], ct: Ct), Ct);
        var r = await Planning.ArchiveBuilder.BuildAsync(plan, new Planning.BuildOptions { OutputPath = t.Sub("mixed.osa"), MemoryBudget = 1L << 30 }, Ct);
        using var reader = OsaReader.Open(r.Path);
        Assert.True(reader.HasSpareCopies);
    }

    [Fact]
    public void TheWriterOwnsTheSparesKey()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(1, 500);
        var stale = new Dictionary<string, byte[]> { [ArchiveIndex.SparesKey] = [1, 2, 3] };
        string without = SpareArchives.Write(t.Sub("without.osa"), files, new OsaWriterOptions { SpareThreshold = long.MaxValue }, stale);
        using (var r = OsaReader.Open(without)) Assert.False(r.Index.Metadata.ContainsKey(ArchiveIndex.SparesKey));
        string with = SpareArchives.Write(t.Sub("with.osa"), files, Low(), stale);
        var tail = SpareArchives.TailOf(with);
        using (var r = OsaReader.Open(with))
            Assert.Equal((ulong)tail.SpareOffset, BinaryPrimitives.ReadUInt64LittleEndian(r.Index.Metadata[ArchiveIndex.SparesKey]));
    }

    [Fact]
    public void NothingAppearsAtTheDestinationBeforeCommit()
    {
        using var t = new TempDir();
        string path = t.Sub("a.osa");
        using (var w = new OsaWriter(path, Low()))
        {
            var data = new byte[5000];
            var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", MethodRegistry.Default), data, CodecContext.None);
            int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, data.Length);
            w.Finish([new ArchiveEntry { Path = "x", Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(frame, 0, data.Length)], Hash = XxHash128.HashToUInt128(data) }]);
            Assert.True(w.HasSpareCopies);
            Assert.True(File.Exists(w.TempPath));
            Assert.False(File.Exists(path));
        } // a crash or cancel before Commit: only the temporary file existed, and it is gone
        Assert.Empty(Directory.GetFileSystemEntries(t.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A10ReaderListsAndExtractsAnArchiveWithSpareCopies(bool encrypted)
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(4, 2500);
        string? pw = encrypted ? SpareArchives.Password : null;
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low(pw));
        Assert.NotNull(SpareArchives.TailOf(path, pw).Trailer);
        var read = SpareArchives.ReadAs10(path, pw);
        Assert.Equal(files.Keys.Order(StringComparer.Ordinal), read.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, data) in files) Assert.Equal(data, read[name]);
    }

    // ------------------------------------------------------------------ the reader: trailer, warnings, Test

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Opens, tests and extracts; every file must verify. Returns the reader's and Test's warnings.</summary>
    private static (IReadOnlyList<string> Open, IReadOnlyList<string> Test, bool Spares) Check(string path, IReadOnlyDictionary<string, byte[]> files,
        string? password = null)
    {
        using var r = OsaReader.Open(path, () => password);
        var test = Extractor.Test(r, null, Ct);
        Assert.Equal((files.Count, 0), (test.FilesOk, test.FilesFailed));
        Assert.Empty(test.Errors);
        string dest = path + ".out";
        var x = Extractor.Extract(r, dest, null, null, null, Ct);
        Assert.Equal(files.Count, x.FilesOk);
        foreach (var (name, data) in files) Assert.Equal(data, File.ReadAllBytes(Path.Combine([dest, .. name.Split('/')])));
        Directory.Delete(dest, recursive: true);
        return (r.Warnings, test.Warnings, r.HasSpareCopies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnIntactArchiveWithSpareCopiesOpensAndTestsWithoutWarnings(bool encrypted)
    {
        using var t = new TempDir();
        string? pw = encrypted ? SpareArchives.Password : null;
        var files = SpareArchives.Files(3, 3000);
        var (open, test, spares) = Check(SpareArchives.Write(t.Sub("a.osa"), files, Low(pw)), files, pw);
        Assert.True(spares);
        Assert.Empty(open);
        Assert.Empty(test);

        var small = Check(SpareArchives.Write(t.Sub("small.osa"), files, new OsaWriterOptions { Password = pw, SpareThreshold = long.MaxValue }), files, pw);
        Assert.Equal((false, 0, 0), (small.Spares, small.Open.Count, small.Test.Count));
    }

    [Fact]
    public void ADamagedTrailerIsReportedAndTheArchiveStillOpens()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 2000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        var tail = SpareArchives.TailOf(path);
        for (int i = 0; i < SpareCopies.TrailerSize; i++)
        {
            string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b => b[tail.TrailerOffset + i] ^= 0x10);
            var (open, test, spares) = Check(bad, files);
            Assert.False(spares);
            Assert.Equal([OsaReader.SpareDamaged], open);
            Assert.Equal([OsaReader.SpareDamaged], test);
        }
    }

    [Fact]
    public void ATrailerOfANewerWriterIsIgnoredWithoutAWarning()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 2000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        var tail = SpareArchives.TailOf(path);
        foreach (var newer in new[] { tail.Trailer!.Value with { Flags = 1 }, tail.Trailer!.Value with { Reserved = 7 }, tail.Trailer!.Value with { Flags = 4, PaddingLength = 1 << 40 } })
        {
            string later = SpareArchives.Damage(path, t.Sub("newer.osa"), b => newer.Write(b.AsSpan((int)tail.TrailerOffset)));
            var (open, test, spares) = Check(later, files);
            Assert.False(spares);
            Assert.Empty(open);
            Assert.Empty(test);
        }
    }

    [Theory]
    [InlineData("missing tail")]
    [InlineData("three bytes")]
    [InlineData("nine bytes")]
    [InlineData("zero")]
    [InlineData("one off")]
    [InlineData("largest")]
    [InlineData("past the end")]
    public void ASparesKeyThatDoesNotMatchTheFileGivesAWarningNeverAnError(string how)
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 1500);
        string path = SpareArchives.Craft(t.Sub("crafted.osa"), files, at => how switch
        {
            "missing tail" => SpareCopies.OffsetValue(at),
            "three bytes" => [1, 2, 3],
            "nine bytes" => [.. SpareCopies.OffsetValue(at), 0],
            "zero" => SpareCopies.OffsetValue(0),
            "one off" => SpareCopies.OffsetValue(at + 1),
            "largest" => [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            _ => SpareCopies.OffsetValue(long.MaxValue - 5),
        }, tail: how != "missing tail");
        var (open, test, spares) = Check(path, files);
        Assert.False(spares);
        Assert.Equal([OsaReader.SpareDamaged], open);
        Assert.Equal([OsaReader.SpareDamaged], test);

        var good = Check(SpareArchives.Craft(t.Sub("good.osa"), files, SpareCopies.OffsetValue), files);
        Assert.Equal((true, 0, 0), (good.Spares, good.Open.Count, good.Test.Count));
    }

    [Fact]
    public void TestFindsDamageInTheSpareCopiesThatOpeningDoesNotLookAt()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 2000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        var tail = SpareArchives.TailOf(path);
        var cases = new (long At, string Warning)[]
        {
            (tail.SpareOffset, OsaReader.SpareDamaged),                                  // B's frame header
            (tail.SpareOffset + tail.IndexLength / 2, OsaReader.SpareDamaged),           // B's body
            (tail.HeaderCopyOffset + 3, OsaReader.SpareHeaderDamaged),                   // H's header bytes
            (tail.HeaderCopyOffset + tail.HeaderLength + 100, OsaReader.SpareHeaderDamaged), // H's padding
        };
        foreach (var (at, warning) in cases)
        {
            string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b => b[at] ^= 0x01);
            var (open, test, spares) = Check(bad, files);
            Assert.True(spares);
            Assert.Empty(open);
            Assert.Equal([warning], test);
        }
    }

    [Fact]
    public void EverySingleByteChangeIsFoundByOpeningTestingOrAWarning()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(1, 300);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        var original = File.ReadAllBytes(path);
        var unnoticed = new List<int>();
        for (int i = 0; i < original.Length; i++)
        {
            var bad = (byte[])original.Clone();
            bad[i] ^= 0x41;
            File.WriteAllBytes(t.Sub("bad.osa"), bad);
            try
            {
                using var r = OsaReader.Open(t.Sub("bad.osa"));
                var res = Extractor.Test(r, null, Ct);
                if (res.FilesFailed == 0 && res.Errors.Count == 0 && r.Warnings.Count == 0 && res.Warnings.Count == 0) unnoticed.Add(i);
            }
            catch (Exception e) when (e is IOException or InvalidDataException) { }
        }
        Assert.Empty(unnoticed);
    }

    // ------------------------------------------------------------------ the reader: falling back to the spare copies

    private static void Flip(byte[] b, long at, byte mask = 0x5A) => b[at] ^= mask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADamagedFileListOpensThroughItsSpareCopy(bool encrypted)
    {
        using var t = new TempDir();
        string? pw = encrypted ? SpareArchives.Password : null;
        var files = SpareArchives.Files(3, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low(pw));
        var tail = SpareArchives.TailOf(path, pw);
        for (int k = 0; k < 64; k++)
        {
            long at = tail.IndexOffset + k * tail.IndexLength / 64;
            string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b => Flip(b, at));
            var (open, test, spares) = Check(bad, files, pw);
            Assert.True(spares);
            Assert.Equal([OsaReader.UsedSpareIndex], open);
            Assert.Equal([OsaReader.UsedSpareIndex], test); // Test says so too, so that osmium t never just says OK
        }
    }

    [Fact]
    public void WithItsTrailerDamagedTooTheSpareFileListIsFoundByItsFrameHeader()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(3, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        var tail = SpareArchives.TailOf(path);
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b =>
        {
            Flip(b, tail.IndexOffset + tail.IndexLength / 2);
            Flip(b, tail.TrailerOffset + 1);
        });
        var (open, _, spares) = Check(bad, files);
        Assert.False(spares);
        Assert.Equal([OsaReader.UsedSpareIndex], open);
    }

    [Theory]
    [InlineData("magic", false)]
    [InlineData("major", false)]
    [InlineData("crc", false)]
    [InlineData("magic", true)]
    [InlineData("major", true)]
    [InlineData("crc", true)]
    [InlineData("zeroed", true)]
    public void ADamagedHeaderOpensThroughItsSpareCopy(string what, bool encrypted)
    {
        using var t = new TempDir();
        string? pw = encrypted ? SpareArchives.Password : null;
        var files = SpareArchives.Files(3, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low(pw));
        var tail = SpareArchives.TailOf(path, pw);
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b =>
        {
            switch (what)
            {
                case "magic": Flip(b, 2); break;
                case "major": b[8] = 2; break;
                case "crc": Flip(b, tail.HeaderLength - 1); break;
                default: Array.Clear(b, 0, (int)tail.HeaderLength); break;
            }
        });
        var (open, test, spares) = Check(bad, files, pw);
        Assert.True(spares);
        Assert.Equal([OsaReader.UsedSpareHeader], open);
        Assert.Equal([OsaReader.UsedSpareHeader], test);
    }

    [Fact]
    public void ADamagedHeaderAndFileListBothOpenThroughTheirSpareCopies()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(3, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low(SpareArchives.Password));
        var tail = SpareArchives.TailOf(path, SpareArchives.Password);
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b => { Flip(b, 5); Flip(b, tail.IndexOffset + 40); });
        var (open, _, _) = Check(bad, files, SpareArchives.Password);
        Assert.Equal([OsaReader.UsedSpareHeader, OsaReader.UsedSpareIndex], open);
    }

    [Fact]
    public void WhenBothCopiesOfTheFileListAreDamagedTheErrorIsTheFirstCopys()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(3, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        var tail = SpareArchives.TailOf(path);
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b =>
        {
            Flip(b, tail.IndexOffset + tail.IndexLength - 3); // A's payload
            Flip(b, tail.SpareOffset + 2);                      // B's frame marker
        });
        var e = Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(bad));
        Assert.Equal("Block payload is corrupt (hash mismatch).", e.Message);
    }

    /// <summary>An archive whose one file is another archive, stored, whose index frame is exactly as long as the
    /// outer one's: so that the stored archive's index frame sits where a spare copy of the outer one's could.</summary>
    private static string ArchiveHoldingAnArchiveWithAnIndexFrameAsLong(TempDir t, SpareCopyPolicy outer)
    {
        var rnd = new Random(8);
        byte[] Pad(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
        int innerPad = 0, outerPad = 0;
        for (int round = 0; round < 50; round++)
        {
            string inner = SpareArchives.Write(t.Sub("inner.osa"), SpareArchives.Files(2, 1500, seed: 3),
                new OsaWriterOptions { SpareCopies = SpareCopyPolicy.Never }, new Dictionary<string, byte[]> { ["pad"] = Pad(innerPad) });
            string path = SpareArchives.Write(t.Sub("outer.osa"), new Dictionary<string, byte[]> { ["docs/inner.osa"] = File.ReadAllBytes(inner) },
                new OsaWriterOptions { SpareCopies = outer }, new Dictionary<string, byte[]> { ["pad"] = Pad(outerPad) });
            long difference = SpareArchives.TailOf(path).IndexLength - SpareArchives.TailOf(inner).IndexLength;
            if (difference == 0) return path;
            if (difference > 0) innerPad += (int)difference;
            else outerPad -= (int)difference;
        }
        throw new InvalidOperationException("The two index frames could not be made as long as each other.");
    }

    [Theory]
    [InlineData("stored archive, no spare copies")]
    [InlineData("stored archive, spare copies damaged")]
    [InlineData("spare copy naming another offset")]
    public void AFileListIsTakenFromASpareCopyOnlyWhenItNamesItsOwnOffset(string how)
    {
        using var t = new TempDir();
        string path = how switch
        {
            "stored archive, no spare copies" => ArchiveHoldingAnArchiveWithAnIndexFrameAsLong(t, SpareCopyPolicy.Never),
            "stored archive, spare copies damaged" => ArchiveHoldingAnArchiveWithAnIndexFrameAsLong(t, SpareCopyPolicy.Always),
            _ => SpareArchives.Craft(t.Sub("crafted.osa"), SpareArchives.Files(2, 1500), at => SpareCopies.OffsetValue(at + 1)),
        };
        var tail = SpareArchives.TailOf(path);
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b =>
        {
            Flip(b, tail.IndexOffset + tail.IndexLength - 3); // A's payload
            if (tail.Trailer is null) return;
            Flip(b, tail.TrailerOffset + 1);
            if (how == "stored archive, spare copies damaged") Flip(b, tail.SpareOffset + tail.IndexLength - 3); // B's payload
        });
        if (how.StartsWith("stored", StringComparison.Ordinal))
            Assert.Equal(tail.IndexLength, SpareArchives.TailOf(t.Sub("inner.osa")).IndexLength);

        var e = Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(bad));
        Assert.Equal("Block payload is corrupt (hash mismatch).", e.Message);
    }

    [Fact]
    public void AHeaderCopyOfANewerWriterNeverStandsInForADamagedHeader()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        var tail = SpareArchives.TailOf(path);
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b =>
        {
            Flip(b, 2);
            (tail.Trailer!.Value with { Flags = 1 }).Write(b.AsSpan((int)tail.TrailerOffset)); // a valid CRC, a flag this version does not know
        });
        Assert.Equal("Not an Osmium archive (bad magic).", Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(bad)).Message);
    }

    [Fact]
    public void SpareCopiesThatOverlapTheBlocksAreDamaged()
    {
        // The offsets add up, but the index says that its last block reaches into B.
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 1500);
        string path = SpareArchives.Craft(t.Sub("crafted.osa"), files, SpareCopies.OffsetValue, lastBlockLonger: 10);
        using (var r = OsaReader.Open(path))
        {
            Assert.Equal([OsaReader.SpareDamaged], r.Warnings);
            Assert.False(r.HasSpareCopies);
            Assert.Equal(SpareCopyStatus.Damaged, r.SpareCopyStatus);
        }
        // B, which would stand in for a damaged A, is held to its own end: its blocks must end before it starts.
        var tail = SpareArchives.TailOf(path);
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b => Flip(b, tail.IndexOffset + tail.IndexLength - 3));
        Assert.Equal("Block payload is corrupt (hash mismatch).", Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(bad)).Message);
    }

    [Fact]
    public void AnArchiveWithoutSpareCopiesStillFailsWithItsOwnError()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(3, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, new OsaWriterOptions { SpareThreshold = long.MaxValue });
        var tail = SpareArchives.TailOf(path);
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b => Flip(b, tail.IndexOffset + tail.IndexLength - 3));
        Assert.Equal("Block payload is corrupt (hash mismatch).", Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(bad)).Message);
        string badHeader = SpareArchives.Damage(path, t.Sub("bad-header.osa"), b => Flip(b, 2));
        Assert.Equal("Not an Osmium archive (bad magic).", Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(badHeader)).Message);
    }

    [Fact]
    public void AWrongPasswordIsNeverTakenForDamage()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low(SpareArchives.Password));
        var tail = SpareArchives.TailOf(path, SpareArchives.Password);
        Assert.Throws<WrongPasswordException>(() => OsaReader.Open(path, () => "wrong"));
        Assert.Throws<PasswordRequiredException>(() => OsaReader.Open(path));
        // With the header damaged, its copy is used, and the password is still checked against it.
        string badHeader = SpareArchives.Damage(path, t.Sub("bad-header.osa"), b => Flip(b, 3));
        Assert.Throws<WrongPasswordException>(() => OsaReader.Open(badHeader, () => "wrong"));
        // With the file list damaged, a wrong password is still a wrong password, found before the file list is read.
        string badIndex = SpareArchives.Damage(path, t.Sub("bad-index.osa"), b => Flip(b, tail.IndexOffset + 50));
        Assert.Throws<WrongPasswordException>(() => OsaReader.Open(badIndex, () => "wrong"));
    }

    [Fact]
    public void AFileListOfANewerVersionIsNeverTakenForDamage()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 1500);
        string path = SpareArchives.Craft(t.Sub("newer.osa"), files, SpareCopies.OffsetValue, newerIndex: true);
        var e = Assert.ThrowsAny<NewerVersionException>(() => OsaReader.Open(path));
        Assert.Equal("This archive needs a newer version of Osmium: the index uses method id 63.", e.Message);
    }

    [Fact]
    public void AHeaderOfANewerVersionIsNeverTakenForDamage()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        var tail = SpareArchives.TailOf(path);
        // A critical flag this version does not know, in an intact header: the spare copy (without it) must not be used.
        string newer = SpareArchives.Damage(path, t.Sub("newer.osa"), b =>
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)) | 2);
            int crcAt = (int)tail.HeaderLength - 4;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(crcAt), Osa.Crc(b.AsSpan(0, crcAt)));
        });
        Assert.ThrowsAny<NewerVersionException>(() => OsaReader.Open(newer));
    }

    [Fact]
    public void TheHeaderCopyMustBelongToTheArchiveTheFooterNames()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        var tail = SpareArchives.TailOf(path);
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b =>
        {
            Flip(b, 2); // the header is damaged …
            long h = tail.HeaderCopyOffset;
            Flip(b, h + 17); // … and its copy names another archive (with a CRC that checks out)
            int crcAt = (int)(h + tail.HeaderLength - 4);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(crcAt), Osa.Crc(b.AsSpan((int)h, (int)tail.HeaderLength - 4)));
        });
        Assert.Equal("Not an Osmium archive (bad magic).", Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(bad)).Message);
    }

    [Fact]
    public void DataBeforeOrAfterAnArchiveIsNotMistakenForDamageTheSpareCopiesRepair()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(t.Sub("prefixed.osa"), [.. new byte[1000], .. bytes]);
        Assert.Equal("Not an Osmium archive (bad magic).", Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(t.Sub("prefixed.osa"))).Message);
        File.WriteAllBytes(t.Sub("followed.osa"), [.. bytes, .. "trailing data"u8]);
        Assert.Equal(OsaReader.EndDamaged, Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(t.Sub("followed.osa"))).Message);
    }

    [Fact]
    public void ADamagedFooterPointsToRecovery()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        long length = new FileInfo(path).Length;
        foreach (long at in new[] { length - 32, length - 20, length - 6, length - 1 })
        {
            string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b => Flip(b, at));
            Assert.Equal(OsaReader.EndDamaged, Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(bad)).Message);
        }
    }

    [Fact]
    public void ExtractAndTestResultsCarryWhatTheReaderFoundWhenOpening()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(3, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        var tail = SpareArchives.TailOf(path);
        // The header's CRC (its marker stays, so that the archive is still recognised) and the file list are damaged.
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b => { b[tail.HeaderLength - 1] ^= 1; b[tail.IndexOffset + 40] ^= 1; });
        using var r = OsaReader.Open(bad);
        string[] expected = [OsaReader.UsedSpareHeader, OsaReader.UsedSpareIndex];
        Assert.Equal(expected, Extractor.Extract(r, t.Sub("out"), null, null, null, Ct).Warnings);
        Assert.Equal(expected, Extractor.Test(r, null, Ct).Warnings); // once each, though Test also compares the copies
        using var s = Archives.ArchiveSources.Open(bad, ct: Ct);
        Assert.Equal(expected, s.Extract(t.Sub("out2"), new Archives.ArchiveExtractRequest(), null, Ct).Warnings);
    }

    /// <summary>The same archive with <paramref name="extra"/> more bytes of padding in its header copy (as a writer that
    /// aligns the index frame might write it): the index frame moves, the footer follows.</summary>
    private static string WithMorePadding(string path, string output, int extra)
    {
        var b = File.ReadAllBytes(path);
        var tail = SpareArchives.TailOf(path);
        var t = tail.Trailer!.Value;
        var trailer = new byte[SpareCopies.TrailerSize];
        (t with { PaddingLength = t.PaddingLength + extra }).Write(trailer);
        long h = tail.HeaderCopyOffset;
        var footer = new ArchiveFooter(tail.IndexOffset + extra, tail.IndexLength, BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(16))).Serialize();
        File.WriteAllBytes(output, [.. b.AsSpan(0, (int)(h + t.HeaderLength)), .. new byte[t.PaddingLength + extra], .. trailer,
            .. b.AsSpan((int)tail.IndexOffset, (int)tail.IndexLength), .. footer]);
        return output;
    }

    [Fact]
    public void MorePaddingThanTheLeastIsAcceptedUpToTheLimit()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(3, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        string padded = WithMorePadding(path, t.Sub("padded.osa"), 8192);
        var (open, test, spares) = Check(padded, files);
        Assert.Equal((true, 0, 0), (spares, open.Count, test.Count));

        // Damaged file list and trailer: the spare file list is found by its frame header across the longer padding.
        var tail = SpareArchives.TailOf(padded);
        string bad = SpareArchives.Damage(padded, t.Sub("bad.osa"), b => { b[tail.IndexOffset + 40] ^= 1; b[tail.TrailerOffset + 1] ^= 1; });
        Assert.Equal([OsaReader.UsedSpareIndex], Check(bad, files).Open);

        // At the limit and beyond: taken as damaged, the archive still opens.
        string over = WithMorePadding(path, t.Sub("over.osa"), SpareCopies.MaxPadding);
        Assert.Equal([OsaReader.SpareDamaged], Check(over, files).Open);
    }

    // ------------------------------------------------------------------ read errors (bad sectors)

    /// <summary>Opens with the bytes in [from, to) unreadable, as a bad sector is; extracts every file and returns the
    /// reader's warnings.</summary>
    private static IReadOnlyList<string> OpenWithBadSector(string path, IReadOnlyDictionary<string, byte[]> files, long from, long to, string? password = null)
    {
        bool Unreadable(long offset, int count) => offset < to && offset + count > from;
        using var r = OsaReader.Open(path, () => password, null, Unreadable);
        string dest = path + ".out";
        var x = Extractor.Extract(r, dest, null, null, null, Ct);
        Assert.Equal(files.Count, x.FilesOk);
        foreach (var (name, data) in files) Assert.Equal(data, File.ReadAllBytes(Path.Combine([dest, .. name.Split('/')])));
        Directory.Delete(dest, recursive: true);
        return r.Warnings;
    }

    [Fact]
    public void AnUnreadableTrailerNeverStopsACompleteArchiveFromOpening()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 2000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        var tail = SpareArchives.TailOf(path);
        Assert.Equal([OsaReader.SpareDamaged], OpenWithBadSector(path, files, tail.TrailerOffset, tail.IndexOffset));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnreadableFileListOpensThroughItsSpareCopy(bool encrypted)
    {
        using var t = new TempDir();
        string? pw = encrypted ? SpareArchives.Password : null;
        var files = SpareArchives.Files(3, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low(pw));
        var tail = SpareArchives.TailOf(path, pw);
        Assert.Equal([OsaReader.UsedSpareIndex], OpenWithBadSector(path, files, tail.IndexOffset, tail.IndexOffset + 100, pw));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnreadableHeaderOpensThroughItsSpareCopy(bool encrypted)
    {
        using var t = new TempDir();
        string? pw = encrypted ? SpareArchives.Password : null;
        var files = SpareArchives.Files(3, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low(pw));
        Assert.Equal([OsaReader.UsedSpareHeader], OpenWithBadSector(path, files, 0, 40, pw));
    }

    [Fact]
    public void AnUnreadablePieceOfTheSearchDoesNotHideTheSpareFileList()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(3, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        var tail = SpareArchives.TailOf(path);
        // The file list and the trailer are damaged, so the spare copy is searched for; a piece of the header copy's
        // padding, which the search reads, cannot be read.
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b => { b[tail.IndexOffset + 40] ^= 1; b[tail.TrailerOffset + 1] ^= 1; });
        long padding = tail.HeaderCopyOffset + tail.HeaderLength + tail.Trailer!.Value.PaddingLength / 2;
        Assert.Equal([OsaReader.UsedSpareIndex], OpenWithBadSector(bad, files, padding, padding + 200));
    }

    [Fact]
    public void AnOpenArchiveCanBeRenamedAndStillBeRead()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 2500);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, Low());
        using var r = OsaReader.Open(path);
        File.Move(path, t.Sub("renamed.osa")); // as Explorer would while the Archives page shows the archive
        Assert.Equal(files.Count, Extractor.Test(r, null, Ct).FilesOk);
    }

    // ------------------------------------------------------------------ the builder

    private static async Task<(string Path, Dictionary<string, byte[]> Files)> Build(TempDir t, string name, Planning.BuildOptions options)
    {
        string input = t.Sub("in-" + name);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        Directory.CreateDirectory(input);
        var rnd = new Random(3);
        for (int i = 0; i < 4; i++)
        {
            var data = new byte[20_000];
            rnd.NextBytes(data);
            File.WriteAllBytes(Path.Combine(input, $"r{i}.bin"), data);
            files[$"in-{name}/r{i}.bin"] = data;
        }
        var plan = await new Planning.Planner(new Planning.PlanOptions { Profile = Planning.Profile.ByName("fast"), MemoryBudget = 1L << 30 })
            .BuildAsync(Planning.Scanner.Scan([input], ct: Ct), Ct);
        var r = await Planning.ArchiveBuilder.BuildAsync(plan, options with { OutputPath = t.Sub(name + ".osa"), KdfIterations = 1000, MemoryBudget = 1L << 30 }, Ct);
        return (r.Path, files);
    }

    [Theory]
    [InlineData("the spare file list")]
    [InlineData("the header copy")]
    [InlineData("the padding of the header copy")]
    [InlineData("the trailer")]
    public async Task ABuildWhoseSpareCopiesDoNotCheckOutWritesNothing(string what)
    {
        using var t = new TempDir();
        var options = new Planning.BuildOptions
        {
            OutputPath = "", SpareCopies = SpareCopyPolicy.Always,
            TestBeforeCheck = partial =>
            {
                var tail = SpareArchives.TailOf(partial);
                long at = what switch
                {
                    "the spare file list" => tail.SpareOffset + tail.IndexLength / 2,
                    "the header copy" => tail.HeaderCopyOffset + 9,
                    "the padding of the header copy" => tail.HeaderCopyOffset + tail.HeaderLength + 7,
                    _ => tail.TrailerOffset + 3,
                };
                using var f = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite);
                f.Position = at;
                int old = f.ReadByte();
                f.Position = at;
                f.WriteByte((byte)(old ^ 0x04));
            },
        };
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Build(t, "bad", options));
        Assert.EndsWith("The archive was not written.", e.Message);
        Assert.False(File.Exists(t.Sub("bad.osa")));
        Assert.Empty(Directory.GetFiles(t.Path, "*.partial-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheBuilderWritesSpareCopiesThatPassItsOwnCheck(bool encrypted)
    {
        using var t = new TempDir();
        string? pw = encrypted ? SpareArchives.Password : null;
        var (path, files) = await Build(t, "low", new Planning.BuildOptions { OutputPath = "", Password = pw, SpareCopies = SpareCopyPolicy.Always, TestFixedArchiveId = new byte[16] });
        var (open, test, spares) = Check(path, files, pw);
        Assert.True(spares);
        Assert.Empty(open);
        Assert.Empty(test);
        using (var r = OsaReader.Open(path, () => pw)) Assert.Equal(new byte[16], r.Header.ArchiveId);

        var small = await Build(t, "default", new Planning.BuildOptions { OutputPath = "", Password = pw });
        Assert.False(Check(small.Path, small.Files, pw).Spares); // 80 KB of blocks: under the 1 MiB threshold
    }

    [Fact]
    public void ArchivesWrittenAsByOsmium01HaveMinor0()
    {
        using var t = new TempDir();
        string path = SpareArchives.Write(t.Sub("old.osa"), SpareArchives.Files(1, 100), new OsaWriterOptions { HeaderMinor = 0, SpareThreshold = long.MaxValue });
        using var r = OsaReader.Open(path);
        Assert.Equal(0, r.Header.Minor);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(File.ReadAllBytes(path).AsSpan(10)));
    }
}
