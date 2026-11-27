using Osmium.Core.Codecs;
using Osmium.Core.Codecs.Native;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Native;

/// <summary>
/// Encoder vectors at the sizes Osmium ships, where the dictionary and the window are not shrunk to a small block:
/// LZMA with 32 and 64 MiB dictionaries on 40 and 72 MiB, Zstandard 19 at window 27 (where long-distance matching
/// turns itself on) and with it on, Zstandard 22 on 16 MiB, and the two index pipelines on a large index.
/// Native/Vectors/&lt;generation&gt;-large.txt pins them; Linux x64, Windows x64 and Windows ARM64 must agree.
/// Explicit tests (minutes, not seconds): run by the large-vectors job of native.yml, or with
/// <c>dotnet test -c Release --project tests/Osmium.Core.Tests --filter-class Osmium.Core.Tests.Native.LargeVectorTests --explicit only</c>.
/// </summary>
[Collection("Native encoders")]
public sealed class LargeVectorTests
{
    private const long M = 1 << 20;

    public sealed record LargeCase(string Id, string Codec, string Input, int Length, ulong Seed, (string, long)[] Params);

    public static readonly LargeCase[] Cases =
    [
        new("lzma.d32m.large-40m", "lzma", "large-40m", 40 << 20, 61, [("dict", 32 * M)]),
        new("lzma.d64m.large-72m", "lzma", "large-72m", 72 << 20, 62, [("dict", 64 * M)]),
        new("zstd.l19w27.large-72m", "zstd", "large-72m", 72 << 20, 62, [("level", 19), ("wlog", 27)]),
        new("zstd.l19w27ldm.large-72m", "zstd", "large-72m", 72 << 20, 62, [("level", 19), ("wlog", 27), ("ldm", 1)]),
        new("zstd.l22.large-16m", "zstd", "large-16m", 16 << 20, 63, [("level", 22)]),
        new("index.zstd19.index-200k", "zstd", "index-200k", 0, 64, [("level", 19)]),
        new("index.lzma16m.index-200k", "lzma", "index-200k", 0, 64, [("dict", 16 * M)]),
    ];

    private static readonly Dictionary<string, byte[]> Inputs = [];

    private static byte[] Input(LargeCase c)
    {
        lock (Inputs)
        {
            if (!Inputs.TryGetValue(c.Input, out var b))
                Inputs[c.Input] = b = c.Input.StartsWith("index", StringComparison.Ordinal)
                    ? IndexGenerationTests.Index(200_000, c.Seed)
                    : VectorInputs.Large(c.Length, c.Seed);
            return b;
        }
    }

    private static string Line(LargeCase c, MethodBase m, ParamValues p, byte[] input, byte[] output) =>
        $"{c.Id} {c.Input} {input.Length} {VectorInputs.Sha256(input)} " +
        $"{string.Join(",", m.Params.Select(d => $"{d.Name}={p.Get(d.Name, d.Default)}"))} {output.Length} {VectorInputs.Sha256(output)} " +
        (c.Codec == "zstd" ? "zstd" : "xz");

    [Fact(Explicit = true)]
    public void Managed1LargeVectorsMatch()
    {
        var lines = new List<string>();
        foreach (var c in Cases)
        {
            var m = (MethodBase)MethodRegistry.Default.Get(c.Codec);
            var p = m.Normalize(ParamValues.Of(c.Params));
            var input = Input(c);
            var output = m is LzmaCodec l ? l.EncodeManaged(input, p, CodecContext.None) : ((ZstdCodec)m).EncodeManaged(input, p, CodecContext.None);
            Assert.True(m.Decode(output, input.Length, p, CodecContext.None).AsSpan().SequenceEqual(input), c.Id);
            lines.Add(Line(c, m, p, input, output));
        }
        VectorFile.Check(EncoderGeneration.Managed1, lines, "-large");
    }

    [Fact(Explicit = true)]
    public void Native1LargeVectorsMatch()
    {
        NativeTestSupport.RequireNative();
        var lines = new List<string>();
        foreach (var c in Cases)
        {
            var m = (MethodBase)MethodRegistry.Default.Get(c.Codec);
            var p = m.Normalize(ParamValues.Of(c.Params));
            var input = Input(c);
            // Zstandard 22 needs more than its memory estimate allows (NativeMemoryTests): these vectors pin the output,
            // so its cap is lifted here.
            NativeEncode.TestMemCap = c.Id == "zstd.l22.large-16m" ? 4UL << 30 : null;
            try
            {
                // Osmium encodes with one thread even when two are allowed: on these inputs the LZMA SDK's helper
                // thread writes other bytes (lzma.d32m.large-40m), so it is never used (NativeEncode).
                var output = m.Encode(input, p, new CodecContext { MaxThreads = 2 });
                Assert.Equal(1u, NativeEncode.LastStats.ThreadsUsed);
                Assert.True(m.Decode(output, input.Length, p, CodecContext.None).AsSpan().SequenceEqual(input), c.Id);
                lines.Add(Line(c, m, p, input, output));
            }
            finally
            {
                NativeEncode.TestMemCap = null;
            }
        }
        VectorFile.Check(EncoderGeneration.Native1, lines, "-large");
    }
}
