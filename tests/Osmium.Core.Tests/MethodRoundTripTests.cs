using System.Text;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests;

public class MethodRoundTripTests
{
    private static readonly MethodRegistry Reg = MethodRegistry.Default;

    public static IEnumerable<(string Name, byte[] Data)> Samples()
    {
        var rnd = new Random(12345);
        byte[] R(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
        yield return ("empty", []);
        yield return ("one", [0x42]);
        yield return ("five", [0xE8, 1, 2, 3, 0]);
        yield return ("random-1023", R(1023));
        yield return ("random-4097", R(4097));
        yield return ("zeros", new byte[20000]);
        var words = "The quick brown Fox jumps over the lazy DOG while NASA engineers test iPhone apps. ".Split(' ');
        var sb = new StringBuilder();
        while (sb.Length < 60000) sb.Append(words[rnd.Next(words.Length)]).Append(rnd.Next(12) == 0 ? ",\n" : " ");
        yield return ("text", Encoding.UTF8.GetBytes(sb.ToString()));
        yield return ("utf8", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Grüße, 世界! Ünïcödé tëxt ", 900))));
        var rec = new byte[40000];
        for (int i = 0; i < rec.Length / 4; i++) BitConverter.TryWriteBytes(rec.AsSpan(i * 4), 1000 + i * 3 + rnd.Next(4));
        yield return ("int32-records", rec);
        var code = R(30000);
        for (int i = 0; i < code.Length - 5; i += rnd.Next(3, 12)) { code[i] = 0xE8; code[i + 4] = (byte)(rnd.Next(2) == 0 ? 0 : 0xFF); }
        yield return ("x86-like", code);
        var adv = new byte[30000];
        byte[] alphabet = [0xE8, 0xE9, 0x00, 0xFF, 0x01, 0xE8];
        for (int i = 0; i < adv.Length; i++) adv[i] = alphabet[rnd.Next(alphabet.Length)];
        yield return ("x86-adversarial", adv);
    }

    public static TheoryData<string> MethodNames()
    {
        var d = new TheoryData<string>();
        foreach (var m in Reg.All) d.Add(m.Name);
        return d;
    }

    private static ParamValues SmallParams(IMethod m) => m.Name switch
    {
        "cm" => ParamValues.Of(("mem", 16)),
        "lzma" => ParamValues.Of(("dict", 1 << 20)),
        "ppmd" => ParamValues.Of(("mem", 16 << 20)),
        "pcm16" => ParamValues.Of(("offset", 7), ("length", 30001), ("channels", 2)),
        "image" => ParamValues.Of(("offset", 54), ("width", 33), ("rows", 40), ("bpp", 3), ("rowbytes", 100)),
        _ => ParamValues.Empty,
    };

    [Theory]
    [MemberData(nameof(MethodNames))]
    public void EveryMethodRoundTripsEverySample(string name)
    {
        var m = Reg.Get(name);
        var spec = m.Kind == MethodKind.Codec
            ? new PipelineSpec([new StageSpec(m, SmallParams(m))])
            : new PipelineSpec([new StageSpec(m, SmallParams(m)), new StageSpec(Reg.Get("store"), ParamValues.Empty)]);
        foreach (var (sample, data) in Samples())
        {
            var (desc, payload) = PipelineExecutor.Encode(spec, data, CodecContext.None);
            var blob = desc.Serialize();
            var desc2 = BlockDescriptor.Deserialize(blob, Reg);
            var back = PipelineExecutor.Decode(desc2, payload, CodecContext.None);
            Assert.True(data.AsSpan().SequenceEqual(back), $"{name} failed on {sample}");
        }
    }

    [Fact]
    public void X86FilterSurvivesExhaustiveShortPatterns()
    {
        // Every 7-byte string over a hostile alphabet, embedded at every alignment.
        byte[] alphabet = [0xE8, 0xE9, 0x00, 0xFF, 0x7F];
        var x86 = Reg.Get("x86");
        var buf = new byte[7];
        int total = (int)Math.Pow(alphabet.Length, buf.Length);
        for (int code = 0; code < total; code++)
        {
            int c = code;
            for (int i = 0; i < buf.Length; i++) { buf[i] = alphabet[c % alphabet.Length]; c /= alphabet.Length; }
            var enc = x86.Encode(buf, ParamValues.Empty, CodecContext.None);
            var dec = x86.Decode(enc, buf.Length, ParamValues.Empty, CodecContext.None);
            Assert.True(buf.AsSpan().SequenceEqual(dec), $"x86 failed on {Convert.ToHexString(buf)}");
        }
    }

    [Fact]
    public void TextFilterSurvivesHostileInput()
    {
        var text = Reg.Get("text");
        var rnd = new Random(7);
        for (int round = 0; round < 300; round++)
        {
            int n = rnd.Next(0, 3000);
            var b = new byte[n];
            for (int i = 0; i < n; i++)
                b[i] = rnd.Next(4) switch
                {
                    0 => (byte)rnd.Next(256),
                    1 => (byte)('a' + rnd.Next(26)),
                    2 => (byte)('A' + rnd.Next(26)),
                    _ => (byte)" .,\n"[rnd.Next(4)],
                };
            var enc = text.Encode(b, ParamValues.Empty, CodecContext.None);
            var dec = text.Decode(enc, n, ParamValues.Empty, CodecContext.None);
            Assert.True(b.AsSpan().SequenceEqual(dec), $"text failed round {round}");
        }
    }

    [Fact]
    public void TextFilterShrinksRepetitiveEnglish()
    {
        var s = string.Concat(Enumerable.Repeat("The international Committee will review the Proposal and the REPORT tomorrow. ", 500));
        var data = Encoding.ASCII.GetBytes(s);
        var enc = Reg.Get("text").Encode(data, ParamValues.Empty, CodecContext.None);
        Assert.True(enc.Length < data.Length * 0.6, $"{enc.Length} vs {data.Length}");
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(2, 16, true)]
    [InlineData(6, 32, false)]
    public void Pcm16RoundTripsVariants(int channels, int lms, bool split)
    {
        var rnd = new Random(channels);
        var data = new byte[2 * channels * 5000 + 13];
        double ph = 0;
        for (int i = 0; i < 5000 * channels; i++)
        {
            ph += 0.01 + 0.001 * (i % channels);
            short v = (short)(Math.Sin(ph) * 20000 + rnd.Next(-200, 200));
            BitConverter.TryWriteBytes(data.AsSpan(2 * i), v);
        }
        var p = ParamValues.Of(("offset", 0), ("length", data.Length), ("channels", channels), ("lms", lms), ("split", split ? 1 : 0));
        var m = Reg.Get("pcm16");
        var enc = m.Encode(data, p, CodecContext.None);
        var dec = m.Decode(enc, data.Length, p, CodecContext.None);
        Assert.Equal(data, dec);
    }

    [Fact]
    public void PipelineTextFormRoundTrips()
    {
        var spec = PipelineSpec.Parse("delta(stride=4)+lzma(dict=64M,lc=0,lp=2,pb=2)", Reg);
        Assert.Equal("delta(stride=4)+lzma(dict=64M,fb=273,lc=0,lp=2,mf=1,pb=2)", spec.ToString());
        Assert.Equal(spec, PipelineSpec.Parse(spec.ToString(), Reg));
        Assert.Throws<FormatException>(() => PipelineSpec.Parse("lzma(bogus=1)", Reg));
        Assert.Throws<ArgumentException>(() => PipelineSpec.Parse("lzma+delta", Reg));
    }

    [Fact]
    public void CorruptPayloadIsDetectedNotSilentlyAccepted()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("corruption test data ", 4000)));
        foreach (var name in new[] { "zstd", "lzma", "brotli", "ppmd", "bzip2", "cm", "deflate" })
        {
            var m = Reg.Get(name);
            var spec = new PipelineSpec([new StageSpec(m, SmallParams(m))]);
            var (desc, payload) = PipelineExecutor.Encode(spec, data, CodecContext.None);
            payload[payload.Length / 2] ^= 0x55;
            // A codec may either throw a data exception or return garbage (the container's rawHash then
            // catches it). Anything else (IndexOutOfRange, NullReference, hang) is a bug.
            var ex = Record.Exception(() => PipelineExecutor.Decode(desc, payload, CodecContext.None));
            Assert.True(ex is null or InvalidDataException or IOException, $"{name} threw {ex?.GetType().Name}: {ex?.Message}");
        }
    }
}
