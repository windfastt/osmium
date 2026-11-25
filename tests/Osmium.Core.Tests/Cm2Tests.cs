using System.Text;
using Osmium.Core.Codecs.Cm2;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests;

public class Cm2Tests
{
    private static readonly MethodRegistry Reg = MethodRegistry.Default;

    private static IEnumerable<(string Name, byte[] Data)> Samples()
    {
        var rnd = new Random(4242);
        byte[] R(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
        yield return ("empty", []);
        yield return ("one", [0x42]);
        yield return ("random-3000", R(3000));
        yield return ("zeros", new byte[9000]);
        var sb = new StringBuilder();
        while (sb.Length < 30000) sb.Append("call the function with arguments; return value ").Append(rnd.Next(1000)).Append('\n');
        yield return ("text", Encoding.ASCII.GetBytes(sb.ToString()));
        var code = R(20000);
        for (int i = 0; i < code.Length - 8; i += rnd.Next(2, 9))
        {
            code[i] = (byte)(rnd.Next(4) switch { 0 => 0xE8, 1 => 0x48, 2 => 0x8B, _ => 0x0F });
            if (code[i] == 0x0F) code[i + 1] = (byte)(0x80 + rnd.Next(16));
        }
        yield return ("x86-like", code);
        var rec = new byte[24000];
        for (int i = 0; i < rec.Length / 12; i++)
        {
            BitConverter.TryWriteBytes(rec.AsSpan(i * 12), 5000 + i);
            BitConverter.TryWriteBytes(rec.AsSpan(i * 12 + 4), (float)Math.Sin(i * 0.01));
            BitConverter.TryWriteBytes(rec.AsSpan(i * 12 + 8), rnd.Next(100));
        }
        yield return ("records-12", rec);
    }

    public static TheoryData<int> ModelSets() => [(int)Cm2Models.Default, (int)Cm2Models.Text, (int)Cm2Models.Binary, (int)Cm2Models.All, 0];

    [Theory]
    [MemberData(nameof(ModelSets))]
    public void Cm2RoundTripsEveryModelSet(int models)
    {
        foreach (var stride in new[] { 0, 12 })
        {
            var spec = PipelineSpec.Parse($"cm2(mem=16,models={models},stride={stride})", Reg);
            foreach (var (name, data) in Samples())
            {
                var (desc, payload) = PipelineExecutor.Encode(spec, data, CodecContext.None);
                var back = PipelineExecutor.Decode(BlockDescriptor.Deserialize(desc.Serialize(), Reg), payload, CodecContext.None);
                Assert.True(data.AsSpan().SequenceEqual(back), $"cm2 models={models} stride={stride} failed on {name}");
            }
        }
    }

    [Fact]
    public void Cm2CorruptPayloadIsDetectedNotSilentlyAccepted()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("corruption test data for cm2 ", 3000)));
        var spec = PipelineSpec.Parse("cm2(mem=16)", Reg);
        var (desc, payload) = PipelineExecutor.Encode(spec, data, CodecContext.None);
        for (int k = 1; k <= 3; k++)
        {
            var bad = (byte[])payload.Clone();
            bad[bad.Length * k / 4] ^= 0x55;
            var ex = Record.Exception(() => PipelineExecutor.Decode(desc, bad, CodecContext.None));
            Assert.True(ex is null or InvalidDataException or IOException, $"cm2 threw {ex?.GetType().Name}: {ex?.Message}");
        }
    }

    // ------------------------------------------------------------------ golden vectors

    // XxHash3-64 of cm2's output on deterministic inputs, recorded on Linux x64; every platform must match. They
    // freeze cm2: a changed hash means existing archives would decode differently, which is a format change.
    private static Cm2Options Small(Cm2Models models) => new() { TableBits = 18, MatchBits = 16, Models = models };

    private static readonly Dictionary<string, (string Hash, Func<byte[]> Input, Cm2Options Options)> Golden = new()
    {
        ["cm2 models=Text (1599) on text"] = ("AF921A9FDBEC1129", () => Synthetic.Text(24 << 10), Small(Cm2Models.Text)),
        ["cm2 models=Binary (975) on x86 code"] = ("7EB8DA01ED117EF1", () => Synthetic.X86(24 << 10), Small(Cm2Models.Binary)),
        ["cm2 models=Default (767) on arm64 code"] = ("C0D5F12C30C3EDD1", () => Synthetic.Arm64(6 << 10), Small(Cm2Models.Default)),
        ["cm2 models=Exe (9167) on x64 code"] = ("84E58774449401E2", () => X64Synthetic.Code(24 << 10), Small(Cm2Models.Exe)),
        ["cm2 models=All (12287) on x64 code"] = ("9E3923B58AF27E09", () => X64Synthetic.Code(24 << 10, seed: 5), Small(Cm2Models.All)),
        ["cm2 models=All (12287) on a 24-bit image"] = ("B16E00803713DB41", Synthetic.Bmp, Small(Cm2Models.All)),
        ["cm2 models=All|Record stride=12 on 12-byte records"] = ("3482FA6605F4D9F0", () => Synthetic.Records(2048),
            Small(Cm2Models.All | Cm2Models.Record) with { RecordStride = 12 }),
        ["cm2 models=None on text"] = ("BA4FA9B060694074", () => Synthetic.Text(8 << 10, seed: 3), Small(Cm2Models.None)),
        ["cm2 models=Text limit=60 lr=8 lr2=6 minlen=4 apm=6 on text"] = ("EF08E6FB9DCE2CE3", () => Synthetic.Text(24 << 10),
            Small(Cm2Models.Text) with { CounterLimit = 60, MixerRate = 8, FinalMixerRate = 6, MatchMinLen = 4, ApmRate = 6 }),
        ["cm2 models=Binary limit=1023 lr=24 lr2=16 minlen=12 apm=9 on 16-bit audio"] = ("C923661447585066", () => Synthetic.Pcm(6000, header: true),
            Small(Cm2Models.Binary) with { CounterLimit = 1023, MixerRate = 24, FinalMixerRate = 16, MatchMinLen = 12, ApmRate = 9 }),
    };

    public static TheoryData<string> GoldenNames() => [.. Golden.Keys];

    [Theory]
    [MemberData(nameof(GoldenNames))]
    public void Cm2OutputMatchesGoldenHash(string name)
    {
        var (hash, input, options) = Golden[name];
        var data = input();
        var packed = Cm2Codec.Encode(data, options, TestContext.Current.CancellationToken);
        var back = new byte[data.Length];
        Cm2Codec.Decode(packed, back, options, TestContext.Current.CancellationToken);
        Assert.Equal(data, back);
        string actual = GoldenVectorTests.Hash(packed);
        Assert.True(actual == hash, $"{name} ({data.Length} → {packed.Length} bytes): expected {hash}, got {actual}");
    }

    [Fact]
    public void Cm2MethodMapsParametersAndFitsTablesToTheBlock()
    {
        // 20 KiB input: FitTo caps the table at log2(32 KiB) + 4 = 19 bits (asked: 20) and the match table at 16 bits.
        var cm2 = (Cm2Method)Reg.Get("cm2");
        var input = Synthetic.Text(20 << 10);
        var p = ParamValues.Of(("mem", 20), ("models", (long)Cm2Models.Text));
        var expected = new Cm2Options
        {
            TableBits = 19, MatchBits = 16, Models = Cm2Models.Text, CounterLimit = 255, MixerRate = 16, FinalMixerRate = 10,
            MatchMinLen = 6, RecordStride = 0, ApmRate = 7,
        };
        Assert.Equal(expected, cm2.ToOptions(p, input.Length));
        var packed = cm2.Encode(input, p, CodecContext.None);
        Assert.Equal(Cm2Codec.Encode(input, expected, TestContext.Current.CancellationToken), packed);
        Assert.Equal(input, cm2.Decode(packed, input.Length, p, CodecContext.None));
        Assert.Equal("7CFF655FF59255FE", GoldenVectorTests.Hash(packed));
    }

    [Fact]
    public void Cm2PipelineWithStrideAndX86V2IsPinned()
    {
        // Text form → normalized spec → descriptor → options: stride > 0 switches the record model on.
        var spec = PipelineSpec.Parse("x86v2(calls=0)+cm2(mem=18,models=12287,stride=12,limit=127,lr=12,lr2=16,minlen=8,apm=6)", Reg);
        Assert.Equal("x86v2(calls=0)+cm2(apm=6,limit=127,lr=12,lr2=16,mem=18,minlen=8,models=12287,stride=12)", spec.ToString());
        var input = Synthetic.Records(2048);
        var (desc, payload) = PipelineExecutor.Encode(spec, input, CodecContext.None);
        Assert.Equal("09B857FE130BE2BE", GoldenVectorTests.Hash(payload));
        string descriptor = Convert.ToHexString(desc.Serialize());
        // FORMAT §2.2: 2 stages; x86v2 (id 71) with calls=0; cm2 (id 8) with its eight parameters, names ascending
        const string ExpectedDescriptor = "024780C00108010563616C6C73000880C00136080361706D0C056C696D6974FE01026C7218036C723220036D656D24"
            + "066D696E6C656E10066D6F64656C73FEBF010673747269646518";
        Assert.True(descriptor == ExpectedDescriptor, $"descriptor: expected {ExpectedDescriptor}, got {descriptor}");
        var back = PipelineExecutor.Decode(BlockDescriptor.Deserialize(desc.Serialize(), Reg), payload, CodecContext.None);
        Assert.Equal(input, back);
    }

    [Fact]
    public void Cm2MemoryIsCappedAt1GiB()
    {
        // Pipeline text (user input) is clamped: mem=26 becomes 24, and a block large enough to keep the full table
        // (FitTo leaves 24 for 3 MiB) round-trips.
        var spec = PipelineSpec.Parse("cm2(mem=26,models=1)", Reg);
        Assert.Equal(24, spec.Codec.Params.Get("mem", -1));
        var data = new byte[3 << 20];
        var r = new Rng(17);
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251 < 200 ? (i >> 9) & 0x3F : r.Below(256));
        var (desc, payload) = PipelineExecutor.Encode(spec, data, CodecContext.None);
        Assert.Equal(24, ((Cm2Method)Reg.Get("cm2")).ToOptions(spec.Codec.Params, data.Length).TableBits);
        Assert.Equal(data, PipelineExecutor.Decode(BlockDescriptor.Deserialize(desc.Serialize(), Reg), payload, CodecContext.None));

        // Stored values above the cap are never clamped: they are a feature this version does not have.
        foreach (int mem in new[] { 25, 26 })
        {
            var blob = new Osmium.Core.IO.BlobWriter().U(1).U(MethodIds.Cm2).U(1000).Bytes(ParamValues.Of(("mem", mem)).Serialize()).ToArray();
            var e = Assert.ThrowsAny<NewerVersionException>(() => BlockDescriptor.Deserialize(blob, Reg, "block 0"));
            Assert.Equal($"parameter cm2.mem={mem}", e.Feature);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new Cm2Options { TableBits = 25 }.Validate());
    }

    [Fact]
    public void Cm2IsRegisteredAsContextMixing()
    {
        var m = Reg.Get("cm2");
        Assert.Equal(MethodIds.Cm2, m.Id);
        Assert.Equal(8, m.Id);
        Assert.True(MethodIds.IsContextMixing(m.Id));
        Assert.True(MethodIds.IsContextMixing(MethodIds.Cm));
        Assert.False(MethodIds.IsContextMixing(MethodIds.Lzma));
    }
}
