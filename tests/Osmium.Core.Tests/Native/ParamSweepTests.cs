using Osmium.Core.Codecs;
using Osmium.Core.Codecs.Native;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Native;

/// <summary>
/// Every legal value of every LZMA and Zstandard parameter (the whole range where it is small; the edges, the
/// search values and a few inner values where it is large) goes through the built-in encoder and, when active, the
/// native one, with the others at their defaults: no exception, and the built-in decoder gives the input back. This
/// is where a range the interface would refuse, but ParamDef accepts, would show (as ldmmin 1 to 3 would).
/// </summary>
[Collection("Native encoders")]
public sealed class ParamSweepTests
{
    private static readonly byte[] Input = VectorInputs.Mixed(96 << 10, 31);

    private static IEnumerable<long> Values(ParamDef d)
    {
        if (d.Max - d.Min <= 32) return Enumerable.Range(0, (int)(d.Max - d.Min + 1)).Select(i => d.Min + i);
        var v = new SortedSet<long> { d.Min, d.Min + 1, d.Max - 1, d.Max, d.Default };
        foreach (var s in d.SearchValues ?? []) v.Add(s);
        if (d.Type == ParamType.Size) { v.Add(3L << 20); v.Add((1L << 20) + 4097); }   // not powers of two
        else { v.Add((d.Min + d.Max) / 2); v.Add(d.Min + 3); }
        return v;
    }

    public static TheoryData<string, string, long, long> Cases()
    {
        var data = new TheoryData<string, string, long, long>();
        foreach (var codec in new[] { "lzma", "zstd" })
            foreach (var d in MethodRegistry.Default.Get(codec).Params)
                foreach (var v in Values(d))
                {
                    if (d.Name == "ldmmin")
                    {
                        data.Add(codec, d.Name, v, 0);   // without long-distance matching it is ignored
                        data.Add(codec, d.Name, v, 1);
                    }
                    else data.Add(codec, d.Name, v, -1);
                }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryLegalValueEncodesAndDecodes(string codec, string param, long value, long ldm)
    {
        var m = (MethodBase)MethodRegistry.Default.Get(codec);
        var raw = new List<(string, long)> { (param, value) };
        if (ldm >= 0) raw.Add(("ldm", ldm));
        var p = m.Normalize(ParamValues.Of([.. raw]));
        byte[] managed = m switch
        {
            LzmaCodec l => l.EncodeManaged(Input, p, CodecContext.None),
            ZstdCodec z => z.EncodeManaged(Input, p, CodecContext.None),
            _ => throw new InvalidOperationException(codec),
        };
        Assert.Equal(Input, m.Decode(managed, Input.Length, p, CodecContext.None));
        if (NativeEncoders.Status != NativeStatus.Active && NativeEncoders.Mode != EncoderMode.Native) return;
        var native = m.Encode(Input, p, new CodecContext { MaxThreads = 2 });
        Assert.Equal(Input, m.Decode(native, Input.Length, p, CodecContext.None));
    }
}
