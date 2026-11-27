using System.Text;
using Osmium.Core.Codecs;
using Osmium.Core.Codecs.Native;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Native;

/// <summary>
/// The archive index is compressed by the same codecs, with the active generation, and is checked only by the
/// re-read after writing (not by "Verify every block"): a generated index of many entries with long names goes
/// through both index pipelines (zstd(level=19), and lzma(dict=16M) for Ultra and above) with each generation and
/// must decode to itself. The test output records each generation's allocation for the index.
/// </summary>
[Collection("Native encoders")]
public sealed class IndexGenerationTests
{
    private readonly ITestOutputHelper _out;
    public IndexGenerationTests(ITestOutputHelper output) => _out = output;

    /// <summary>Index-like bytes: per entry a long UTF-8 path, size, time, attributes and a 16-byte hash.</summary>
    internal static byte[] Index(int entries, ulong seed)
    {
        var r = new VectorInputs.Rng(seed);
        string[] dirs = ["Projects", "Photos 2026", "Documents", "Program Files", "src", "node_modules", "Übersicht", "データ"];
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.UTF8);
        for (int i = 0; i < entries; i++)
        {
            var sb = new StringBuilder();
            int depth = 2 + r.Below(6);
            for (int d = 0; d < depth; d++) sb.Append(dirs[r.Below(dirs.Length)]).Append('/');
            sb.Append("a rather long file name for an index entry ").Append(i).Append(r.Below(3) == 0 ? ".txt" : ".bin");
            w.Write(sb.ToString());
            w.Write((long)r.Below(1 << 30));
            w.Write(638_000_000_000_000_000L + (long)r.Below(1 << 30));
            w.Write((short)r.Below(64));
            w.Write(r.Next());
            w.Write(r.Next());
        }
        w.Flush();
        return ms.ToArray();
    }

    [Theory]
    [InlineData("zstd(level=19)")]
    [InlineData("lzma(dict=16M)")]
    public void TheIndexEncodesWithEachGenerationAndDecodesBack(string pipeline)
    {
        var spec = PipelineSpec.Parse(pipeline, MethodRegistry.Default);
        var m = (MethodBase)spec.Codec.Method;
        var p = spec.Codec.Params;
        var index = Index(20_000, 7);

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] managed = m switch
        {
            LzmaCodec l => l.EncodeManaged(index, p, CodecContext.None),
            ZstdCodec z => z.EncodeManaged(index, p, CodecContext.None),
            _ => throw new InvalidOperationException(pipeline),
        };
        long managedAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(index, m.Decode(managed, index.Length, p, CodecContext.None));
        _out.WriteLine($"{pipeline} on a {index.Length >> 10} KiB index: managed-1 {managed.Length} bytes, " +
                       $"{managedAllocated >> 10} KiB allocated by the managed encoder");

        if (NativeEncoders.Status != NativeStatus.Active && NativeEncoders.Mode != EncoderMode.Native) return;
        before = GC.GetAllocatedBytesForCurrentThread();
        var native = m.Encode(index, p, CodecContext.None);
        long nativeManaged = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(index, m.Decode(native, index.Length, p, CodecContext.None));
        _out.WriteLine($"{pipeline}: native-1 {native.Length} bytes, native peak {NativeEncode.LastStats.PeakBytes >> 10} KiB " +
                       $"plus {nativeManaged >> 10} KiB of managed buffers");
    }
}
