using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using Osmium.Core.Container;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests;

/// <summary>
/// Archives made by a newer Osmium: every unknown method id, parameter name or parameter value gives one clear message
/// ("This archive needs a newer version of Osmium: …") instead of a corruption error or a silently clamped value, and a
/// block that cannot be decoded does not stop the archive from opening or its other blocks from extracting.
/// </summary>
public class ForwardCompatibilityTests
{
    private static readonly MethodRegistry Reg = MethodRegistry.Default;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Newer = "This archive needs a newer version of Osmium: ";

    private static byte[] Descriptor(params (int Id, ParamValues Params)[] stages)
    {
        var w = new BlobWriter().U((ulong)stages.Length);
        foreach (var (id, p) in stages) w.U((ulong)id).U(1000).Bytes(p.Serialize());
        return w.ToArray();
    }

    private static (byte[] Blob, string Feature) Case(string name) => name switch
    {
        "unknown method" => (Descriptor((8, ParamValues.Empty)), "method id 8"),
        "unknown filter" => (Descriptor((71, ParamValues.Empty), (MethodIds.Lzma, ParamValues.Empty)), "method id 71"),
        "unknown parameter" => (Descriptor((MethodIds.Lzma, ParamValues.Of(("dict", 1 << 20), ("future", 1)))), "parameter lzma.future"),
        "value above the range" => (Descriptor((MethodIds.Cm, ParamValues.Of(("models", 8191)))), "parameter cm.models=8191"),
        "value below the range" => (Descriptor((MethodIds.X86, ParamValues.Empty), (MethodIds.Cm, ParamValues.Of(("mem", 12)))), "parameter cm.mem=12"),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [InlineData("unknown method")]
    [InlineData("unknown filter")]
    [InlineData("unknown parameter")]
    [InlineData("value above the range")]
    [InlineData("value below the range")]
    public void DescriptorsOfANewerVersionGiveOneClearMessage(string name)
    {
        var (blob, feature) = Case(name);
        var e = Assert.ThrowsAny<NewerVersionException>(() => BlockDescriptor.Deserialize(blob, Reg, "block 3"));
        Assert.Equal($"{Newer}block 3 uses {feature}.", e.Message);
        Assert.Equal(feature, e.Feature);
        Assert.IsAssignableFrom<CorruptDataException>(e); // existing handlers keep catching it
        Assert.Equal(feature.StartsWith("method", StringComparison.Ordinal), e is UnknownMethodException);
        Assert.Equal($"{Newer}it uses {feature}.", Assert.ThrowsAny<NewerVersionException>(() => BlockDescriptor.Deserialize(blob, Reg)).Message);

        // the index keeps a stand-in instead, so the archive still opens; the stand-in serializes as stored
        var standIn = BlockDescriptor.DeserializeOrStandIn(blob, Reg, "block 3");
        Assert.Equal(e.Message, standIn.NewerVersion!.Message);
        Assert.Equal($"needs a newer Osmium ({feature})", standIn.Spec.ToString());
        Assert.Equal(1000, standIn.RawSize);
        Assert.Equal(blob, standIn.Serialize());
        Assert.Equal(e.Message, Assert.ThrowsAny<NewerVersionException>(() => PipelineExecutor.Decode(standIn, new byte[16], CodecContext.None)).Message);
    }

    [Fact]
    public void StoredValuesAreNeverClampedWhileUserInputStillIs()
    {
        // Pipeline text, rules and the tuner keep 0.1.1 behaviour: out-of-range values are clamped.
        Assert.Equal(4095, PipelineSpec.Parse("cm(models=8191)", Reg).Codec.Params.Get("models", -1));
        // Stored values are checked: in range they come back unchanged, absent ones take their defaults...
        var d = BlockDescriptor.Deserialize(Descriptor((MethodIds.Cm, ParamValues.Of(("models", 4095), ("mem", 16)))), Reg);
        Assert.Null(d.NewerVersion);
        Assert.Equal((4095L, 16L, 255L), (d.Spec.Codec.Params.Get("models", -1), d.Spec.Codec.Params.Get("mem", -1), d.Spec.Codec.Params.Get("limit", -1)));
        // ...and out of range they are refused by every read path, including the pipeline-only form.
        var pipelineOnly = new BlobWriter().U(1).U(MethodIds.Cm).Bytes(ParamValues.Of(("models", 8191)).Serialize()).ToArray();
        Assert.Equal($"{Newer}it uses parameter cm.models=8191.", Assert.ThrowsAny<NewerVersionException>(() => PipelineSpec.Deserialize(pipelineOnly, Reg)).Message);
        var unknown = new BlobWriter().U(1).U(8).Bytes(ParamValues.Empty.Serialize()).ToArray();
        Assert.Equal(8, Assert.Throws<UnknownMethodException>(() => PipelineSpec.Deserialize(unknown, Reg)).MethodId);
    }

    [Fact]
    public void AnImpossibleStageOrderIsCorruptionNotANewerVersion()
    {
        var blob = Descriptor((MethodIds.Lzma, ParamValues.Empty), (MethodIds.X86, ParamValues.Empty));
        var e = Assert.Throws<CorruptDataException>(() => BlockDescriptor.Deserialize(blob, Reg, "block 3"));
        Assert.StartsWith("Invalid pipeline in block 3: ", e.Message);
        Assert.Throws<CorruptDataException>(() => BlockDescriptor.DeserializeOrStandIn(blob, Reg, "block 3"));
    }

    // ------------------------------------------------------------------ whole archives

    /// <summary>A codec of "a newer Osmium" (it stores its input).</summary>
    private sealed class NewerCodec(int id, string name, params ParamDef[] defs) : MethodBase
    {
        public override int Id => id;
        public override string Name => name;
        public override string DisplayName => name;
        public override MethodKind Kind => MethodKind.Codec;
        public override string Description => "test codec of a newer version";
        public override IReadOnlyList<ParamDef> Params => defs;
        public override byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx) => input.ToArray();
        public override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx) => input.ToArray();
    }

    /// <summary>The registry of a newer Osmium: a new method id 8, more cm model bits, a new lzma parameter.</summary>
    private static readonly MethodRegistry NewerRegistry = new(Reg.All.Where(m => m.Id is not (MethodIds.Cm or MethodIds.Lzma)).Concat<IMethod>(
    [
        new NewerCodec(8, "cm2"),
        new NewerCodec(MethodIds.Cm, "cm", new ParamDef("models", "Model set", ParamType.Int, 0, 8191, 767, "13 model bits")),
        new NewerCodec(MethodIds.Lzma, "lzma", new ParamDef("future", "New parameter", ParamType.Int, 0, 1, 0, "not in 0.1")),
    ]));

    private static readonly (string Name, string Pipeline, byte[] Data)[] Files =
    [
        ("a.txt", "zstd(level=3)", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("osmium forward compatibility ", 300)))),
        ("b.bin", "cm2", Synthetic.X86(4000)),
        ("c.bin", "cm(models=8191)", Synthetic.Records(300)),
        ("d.bin", "x86+lzma(future=1)", Synthetic.X86(3000, seed: 5)),
        ("e.txt", "store", "stored as is"u8.ToArray()),
    ];

    private static readonly string[] NewerErrors =
    [
        $"{Newer}block 1 uses method id 8.",
        $"{Newer}block 2 uses parameter cm.models=8191.",
        $"{Newer}block 3 uses parameter lzma.future.",
    ];

    /// <summary>Writes an archive the way a newer Osmium would: one block per file, each with its own pipeline.</summary>
    private static string WriteNewerArchive(TempDir t, string indexPipeline = "zstd(level=19)")
    {
        string path = t.Sub("newer.osa");
        using var w = new OsaWriter(path, new OsaWriterOptions { IndexPipeline = indexPipeline });
        var entries = new List<ArchiveEntry>();
        foreach (var (name, pipeline, data) in Files)
        {
            var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse(pipeline, NewerRegistry), data, CodecContext.None);
            int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, data.Length);
            entries.Add(new ArchiveEntry
            {
                Path = name, Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(frame, 0, data.Length)],
                Hash = XxHash128.HashToUInt128(data),
            });
        }
        w.Finish(entries, null, NewerRegistry);
        w.Commit(overwrite: false);
        return path;
    }

    [Fact]
    public void ArchiveOfANewerVersionOpensListsAndExtractsEverythingItCan()
    {
        using var t = new TempDir();
        using var r = OsaReader.Open(WriteNewerArchive(t));

        // opening and listing work; the blocks that need a newer version say so
        Assert.Equal(
            [PipelineSpec.Parse("zstd(level=3)", Reg).ToString(), "needs a newer Osmium (method id 8)", "needs a newer Osmium (parameter cm.models=8191)",
                "needs a newer Osmium (parameter lzma.future)", "store"],
            r.Blocks.Select(b => b.Descriptor.Spec.ToString()));
        Assert.Equal(NewerErrors[0], r.NewerVersion!.Message);
        Assert.True(r.EstimatedDecodeSeconds >= 0 && r.MaxBlockDecodeMemory > 0);

        // reading a block: the supported ones decode, the others raise the message (keeping its type)
        Assert.Equal(Files[0].Data, r.ReadBlock(0));
        var e = Assert.Throws<UnknownMethodException>(() => r.ReadBlock(1));
        Assert.Equal((8, NewerErrors[0]), (e.MethodId, e.Message));
        Assert.Equal(NewerErrors[1], Assert.ThrowsAny<NewerVersionException>(() => r.ReadBody(2)).Message);

        // test and extract report exactly those messages, no corruption errors, and extract the rest
        var test = Extractor.Test(r, null, Ct);
        Assert.Equal(NewerErrors, test.Errors.Order(StringComparer.Ordinal));
        Assert.Equal((2, 3, 3), (test.FilesOk, test.FilesFailed, test.FilesNeedingNewerVersion));
        var x = Extractor.Extract(r, t.Sub("out"), null, null, null, Ct);
        Assert.Equal(NewerErrors, x.Errors.Order(StringComparer.Ordinal));
        Assert.Equal((2, 3, 3), (x.FilesOk, x.FilesFailed, x.FilesNeedingNewerVersion));
        Assert.Equal(["a.txt", "e.txt"], Directory.GetFiles(t.Sub("out")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(Files[0].Data, File.ReadAllBytes(t.Sub("out/a.txt")));
        Assert.Equal(Files[4].Data, File.ReadAllBytes(t.Sub("out/e.txt")));
    }

    [Fact]
    public void AnIndexCompressedByANewerMethodCannotBeOpenedAndSaysWhy()
    {
        using var t = new TempDir();
        var path = WriteNewerArchive(t, indexPipeline: "cm2");
        var e = Assert.Throws<UnknownMethodException>(() => OsaReader.Open(path));
        Assert.Equal($"{Newer}the index uses method id 8.", e.Message);
    }

    [Fact]
    public void AFrameFlagOfANewerVersionIsNamedForItsBlock()
    {
        using var t = new TempDir();
        var path = WriteNewerArchive(t);
        long offset;
        using (var r = OsaReader.Open(path)) offset = r.Blocks[4].Offset;
        var bytes = File.ReadAllBytes(path);
        var frameHeader = bytes.AsSpan((int)offset, Osa.FrameHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(frameHeader[4..], BinaryPrimitives.ReadUInt32LittleEndian(frameHeader[4..]) | 4);
        BinaryPrimitives.WriteUInt32LittleEndian(frameHeader[24..], Osa.Crc(frameHeader[..24]));
        File.WriteAllBytes(path, bytes);

        using var reader = OsaReader.Open(path);
        Assert.Equal($"{Newer}block 4 uses frame flag 0x4.", Assert.ThrowsAny<NewerVersionException>(() => reader.ReadBlock(4)).Message);
        var test = Extractor.Test(reader, null, Ct);
        Assert.Equal([.. NewerErrors, $"{Newer}block 4 uses frame flag 0x4."], test.Errors.Order(StringComparer.Ordinal));
        Assert.Equal((1, 4, 4), (test.FilesOk, test.FilesFailed, test.FilesNeedingNewerVersion)); // blocks 1-3 need it too
    }

    // ------------------------------------------------------------------ header and index features

    private static byte[] Header(uint flags, byte[] extensions, bool goodCrc = true)
    {
        var b = new byte[36 + extensions.Length + 4];
        Osa.Magic.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), Osa.Major);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), Osa.Minor);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), (uint)extensions.Length);
        extensions.CopyTo(b, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36 + extensions.Length), Osa.Crc(b.AsSpan(0, 36 + extensions.Length)) ^ (goodCrc ? 0u : 1u));
        return b;
    }

    private static ArchiveHeader ReadHeader(byte[] b) => ArchiveHeader.Read(new MemoryStream(b));

    [Fact]
    public void HeaderAndIndexFeaturesOfANewerVersionSaySo()
    {
        Assert.Equal($"{Newer}it uses archive feature #3.",
            Assert.ThrowsAny<NewerVersionException>(() => ReadHeader(Header(0, new BlobWriter().U(3).Bytes([1]).ToArray()))).Message);
        Assert.Equal($"{Newer}it uses archive flag 0x0002.", Assert.ThrowsAny<NewerVersionException>(() => ReadHeader(Header(2, []))).Message);
        Assert.Equal($"{Newer}it uses key derivation function 2.",
            Assert.ThrowsAny<NewerVersionException>(() => ReadHeader(Header(Osa.FlagEncrypted, new BlobWriter().U(1).Bytes([2]).ToArray()))).Message);
        Assert.Equal($"{Newer}it uses index version 2.",
            Assert.ThrowsAny<NewerVersionException>(() => ArchiveIndex.Deserialize(new BlobWriter().U(2).ToArray(), 0, Reg)).Message);

        // what a newer version may add without breaking older readers is still accepted
        Assert.Equal(1u << 17, ReadHeader(Header(1u << 17, [])).Flags);                                 // informational flag
        Assert.Null(ReadHeader(Header(0, new BlobWriter().U(4).Bytes([1, 2, 3]).ToArray())).Creator);   // ignorable record

        // damage is still reported as damage: the CRC is checked before any flag is interpreted
        var damaged = Assert.Throws<CorruptDataException>(() => ReadHeader(Header(2, [], goodCrc: false)));
        Assert.Equal("Archive header is corrupt (CRC mismatch).", damaged.Message);
        Assert.Throws<CorruptDataException>(() => ArchiveIndex.Deserialize(new BlobWriter().U(0).ToArray(), 0, Reg));
    }
}
