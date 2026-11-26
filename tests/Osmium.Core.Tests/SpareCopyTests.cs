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
    /// <paramref name="tail"/> says whether B and H are written at all.
    /// </summary>
    public static string Craft(string path, IReadOnlyDictionary<string, byte[]> files, Func<long, byte[]?> sparesValue, bool tail = true,
        bool newerIndex = false)
    {
        var header = new ArchiveHeader { Creator = "test" };
        var headerBytes = header.Serialize();
        using var fs = new FileStream(path, FileMode.Create);
        fs.Write(headerBytes);
        var index = new ArchiveIndex();
        foreach (var (name, data) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", Reg), data, CodecContext.None);
            long offset = fs.Position;
            var frame = Frame((ulong)index.Blocks.Count, 0, [.. new BlockBody(desc, BlockBody.Hash128(data), payload).SerializePrefix(), .. payload]);
            fs.Write(frame);
            index.Entries.Add(new ArchiveEntry { Path = name, Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(index.Blocks.Count, 0, data.Length)], Hash = XxHash128.HashToUInt128(data) });
            index.Blocks.Add(new BlockInfo(index.Blocks.Count, offset, frame.Length, data.Length, data.Length, desc));
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
    private static OsaWriterOptions Low(string? password = null) => new() { Password = password, SpareThreshold = 0 };

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
        var files = SpareArchives.Files(2, 2000);
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
