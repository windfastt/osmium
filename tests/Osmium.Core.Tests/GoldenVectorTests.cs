using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Osmium.Core.Codecs;
using Osmium.Core.Codecs.Cm;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests;

/// <summary>
/// Golden vectors: the XxHash3-64 of the encoded output of the CM codec (every model set, record model, tuned
/// parameters, the method-layer parameter mapping) and of every filter (default and non-default parameters), on
/// deterministic synthetic inputs. Recorded on the 0.1.1 engine (Linux x64; Windows and ARM64 must match). Archives
/// store only the pipeline, so a changed hash means existing archives would decode differently: that is a format
/// change, never a refactoring. Every vector also checks the round trip.
/// </summary>
public class GoldenVectorTests
{
    private static readonly MethodRegistry Reg = MethodRegistry.Default;

    public sealed record Vector(string Hash, Func<byte[]> Input, Func<byte[], byte[]> Encode, Func<byte[], int, byte[]> Decode);

    // ------------------------------------------------------------------ CM codec

    private static CmOptions Small(CmModels models) => new() { TableBits = 18, MatchBits = 16, Models = models };

    private static Vector Cm(string hash, Func<byte[]> input, CmOptions o) => new(hash, input,
        data => CmCodec.Encode(data, o, TestContext.Current.CancellationToken),
        (packed, n) => { var back = new byte[n]; CmCodec.Decode(packed, back, o, TestContext.Current.CancellationToken); return back; });

    private static readonly Dictionary<string, Vector> CmVectors = new()
    {
        ["cm models=Text (1599) on text"] = Cm("DDBF8247EF1960D2", () => Synthetic.Text(24 << 10), Small(CmModels.Text)),
        ["cm models=Binary (975) on x86 code"] = Cm("E085A47BC2CB5DF7", () => Synthetic.X86(24 << 10), Small(CmModels.Binary)),
        ["cm models=Default (767) on arm64 code"] = Cm("240C98EE9E6517BD", () => Synthetic.Arm64(6 << 10), Small(CmModels.Default)),
        ["cm models=All (4095) on a 24-bit image"] = Cm("7C8DE736EE2ED965", Synthetic.Bmp, Small(CmModels.All)),
        ["cm models=All|Record stride=12 on 12-byte records"] = Cm("BEE01F702B271414", () => Synthetic.Records(2048),
            Small(CmModels.All | CmModels.Record) with { RecordStride = 12 }),
        ["cm models=Text limit=60 lr=8 lr2=6 minlen=4 apm=6 on text"] = Cm("AB7D753B821F8469", () => Synthetic.Text(24 << 10),
            Small(CmModels.Text) with { CounterLimit = 60, MixerRate = 8, FinalMixerRate = 6, MatchMinLen = 4, ApmRate = 6 }),
        ["cm models=Binary limit=1023 lr=24 lr2=16 minlen=12 apm=9 on 16-bit audio"] = Cm("5723EB46DB589CC3", () => Synthetic.Pcm(6000, header: true),
            Small(CmModels.Binary) with { CounterLimit = 1023, MixerRate = 24, FinalMixerRate = 16, MatchMinLen = 12, ApmRate = 9 }),
    };

    // ------------------------------------------------------------------ filters

    private static Vector Filter(string hash, string method, Func<byte[]> input, ParamValues? p = null)
    {
        var m = Reg.Get(method);
        var pv = p ?? ParamValues.Empty;
        return new(hash, input,
            data => m.Encode(data, pv, CodecContext.None),
            (enc, n) => m.Decode(enc, n, pv, CodecContext.None));
    }

    private static ParamValues P(params (string, long)[] values) => ParamValues.Of(values);

    private static readonly ParamValues BmpGeometry = P(("offset", Synthetic.BmpOffset), ("width", Synthetic.BmpWidth),
        ("rows", Synthetic.BmpHeight), ("bpp", 3), ("rowbytes", Synthetic.BmpRowBytes));

    private static readonly Dictionary<string, Vector> FilterVectors = new()
    {
        // x86, arm64 and text have no parameters: the second vector of each uses a harder input instead.
        ["x86 on x86 code"] = Filter("F1575BF331A30200", "x86", () => Synthetic.X86(24 << 10)),
        ["x86 on dense E8/E9/00/FF bytes"] = Filter("E28290435B4F43E3", "x86", () => Synthetic.BranchNoise(8 << 10)),
        ["x86v2 on x86 code"] = Filter("9D85BC6F6915AAF2", "x86v2", () => Synthetic.X86(24 << 10)),
        ["x86v2 calls=0 on x64 code with RIP-relative operands"] = Filter("53BDB330DAC62327", "x86v2", () => X64Synthetic.Code(24 << 10), P(("calls", 0))),
        ["arm64 on arm64 code"] = Filter("F7A7D505DDA01C5C", "arm64", () => Synthetic.Arm64(6 << 10)),
        ["arm64 on misaligned arm64 code with an odd tail"] = Filter("5B96B292C5389150", "arm64", () => Synthetic.Misaligned(Synthetic.Arm64(2 << 10))),
        ["text on English-like lines"] = Filter("540DE11C062E23EB", "text", () => Synthetic.Text(24 << 10)),
        ["text on mixed case, long words, control and UTF-8 bytes"] = Filter("C31BBBB239DE38CA", "text", () => Synthetic.HostileText(16 << 10)),

        ["delta default (stride=1) on a 24-bit image"] = Filter("33F9C8288091D893", "delta", Synthetic.Bmp),
        ["delta stride=4 on 16-bit stereo audio"] = Filter("4AA256D59401AAAC", "delta", () => Synthetic.Pcm(6000, header: true), P(("stride", 4))),
        ["delta stride=12 on 12-byte records"] = Filter("5AF4F63BA0D58CCE", "delta", () => Synthetic.Records(2048), P(("stride", 12))),

        ["transpose default (stride=4) on 16-bit stereo audio"] = Filter("1A44118924BA1A34", "transpose", () => Synthetic.Pcm(6000, header: false)),
        ["transpose stride=12 on 12-byte records"] = Filter("E3DC2B5534C11F69", "transpose", () => Synthetic.Records(2048), P(("stride", 12))),
        ["transpose stride=3 on a 24-bit image"] = Filter("DDCA5424D8EB4F0C", "transpose", Synthetic.Bmp, P(("stride", 3))),

        ["pcm16 default (whole block, 2 ch, lms=16, split=1) on raw stereo"] = Filter("1E7115855554EDB9", "pcm16", () => Synthetic.Pcm(6000, header: false)),
        ["pcm16 offset=44 channels=2 on a WAV"] = Filter("DF29CDC241C33249", "pcm16", () => Synthetic.Pcm(6000, header: true),
            P(("offset", 44), ("length", 6000 * 4), ("channels", 2))),
        ["pcm16 offset=44 short region lms=32 split=0 on a WAV"] = Filter("0E52C8F33FFACB6C", "pcm16", () => Synthetic.Pcm(6000, header: true),
            P(("offset", 44), ("length", 6000 * 4 - 6), ("channels", 2), ("lms", 32), ("split", 0))),
        ["pcm16 offset=44 channels=1 lms=0 on a WAV"] = Filter("7D22FDA32CC77545", "pcm16", () => Synthetic.Pcm(6000, header: true),
            P(("offset", 44), ("length", 6000 * 4), ("channels", 1), ("lms", 0))),

        ["image default (1x1 at offset 0) on a 24-bit image"] = Filter("B50DA116E94CF04A", "image", Synthetic.Bmp),
        ["image 101x64 bpp=3 green=1 on a 24-bit image"] = Filter("F45AA51697211D8E", "image", Synthetic.Bmp, BmpGeometry),
        ["image 101x64 bpp=3 green=0 on a 24-bit image"] = Filter("81F3971D44F7A2BB", "image", Synthetic.Bmp, BmpGeometry.With("green", 0)),
        ["image 304x64 bpp=1 on a 24-bit image"] = Filter("78DD7F4949D2AFFE", "image", Synthetic.Bmp,
            P(("offset", Synthetic.BmpOffset), ("width", Synthetic.BmpRowBytes), ("rows", Synthetic.BmpHeight), ("bpp", 1), ("rowbytes", Synthetic.BmpRowBytes))),
    };

    public static TheoryData<string> CmNames() => [.. CmVectors.Keys];
    public static TheoryData<string> FilterNames() => [.. FilterVectors.Keys];

    [Theory]
    [MemberData(nameof(CmNames))]
    public void CmOutputMatchesGoldenHash(string name) => Check(name, CmVectors[name]);

    [Theory]
    [MemberData(nameof(FilterNames))]
    public void FilterOutputMatchesGoldenHash(string name) => Check(name, FilterVectors[name]);

    [Fact]
    public void EveryFilterHasAGoldenVector()
    {
        foreach (var f in Reg.Filters)
            Assert.True(FilterVectors.Keys.Count(k => k.StartsWith(f.Name + " ", StringComparison.Ordinal)) >= 2, $"filter '{f.Name}' needs two golden vectors");
    }

    private static void Check(string name, Vector v)
    {
        var input = v.Input();
        var encoded = v.Encode(input);
        Assert.Equal(input, v.Decode(encoded, input.Length));
        Pinned(v.Hash, Hash(encoded), $"{name} ({input.Length} → {encoded.Length} bytes)");
    }

    internal static string Hash(byte[] data) => XxHash3.HashToUInt64(data).ToString("X16", CultureInfo.InvariantCulture);

    /// <summary>Like Assert.Equal, but the message always carries the full actual value (it is what gets recorded).</summary>
    private static void Pinned(string expected, string actual, string what) =>
        Assert.True(actual == expected, $"{what}: expected {expected}, got {actual}");

    // ------------------------------------------------------------------ method layer (parameters → options)

    [Fact]
    public void CmMethodMapsParametersAndFitsTablesToTheBlock()
    {
        // 20 KiB input: FitTo caps the table at log2(32 KiB) + 4 = 19 bits (asked: 20) and the match table at 16 bits.
        var cm = (CmMethod)Reg.Get("cm");
        var input = Synthetic.Text(20 << 10);
        var p = P(("mem", 20), ("models", (long)CmModels.Text));
        var expected = new CmOptions
        {
            TableBits = 19, MatchBits = 16, Models = CmModels.Text, CounterLimit = 255, MixerRate = 16, FinalMixerRate = 10,
            MatchMinLen = 6, RecordStride = 0, ApmRate = 7,
        };
        Assert.Equal(expected, cm.ToOptions(p, input.Length));
        var packed = cm.Encode(input, p, CodecContext.None);
        Assert.Equal(CmCodec.Encode(input, expected, TestContext.Current.CancellationToken), packed);
        Assert.Equal(input, cm.Decode(packed, input.Length, p, CodecContext.None));
        Pinned("72835F8CFC237DAD", Hash(packed), "cm(mem=20,models=1599) on 20 KiB of text");
    }

    [Fact]
    public void CmPipelineWithStrideAddsTheRecordModel()
    {
        // Text form → normalized spec → descriptor → options: stride > 0 switches the record model on.
        var spec = PipelineSpec.Parse("cm(mem=18,models=4095,stride=12,limit=127,lr=12,lr2=16,minlen=8,apm=6)", Reg);
        Assert.Equal("cm(apm=6,limit=127,lr=12,lr2=16,mem=18,minlen=8,models=4095,stride=12)", spec.ToString());
        var input = Synthetic.Records(2048);
        var (desc, payload) = PipelineExecutor.Encode(spec, input, CodecContext.None);
        var expected = new CmOptions
        {
            TableBits = 18, MatchBits = 16, Models = CmModels.All | CmModels.Record, CounterLimit = 127, MixerRate = 12,
            FinalMixerRate = 16, MatchMinLen = 8, RecordStride = 12, ApmRate = 6,
        };
        Assert.Equal(CmCodec.Encode(input, expected, TestContext.Current.CancellationToken), payload);
        Pinned("BF7CA2B4A9281D4A", Hash(payload), "payload");
        Pinned("010780C00135080361706D0C056C696D6974FE01026C7218036C723220036D656D24066D696E6C656E10066D6F64656C73FE3F0673747269646518",
            Convert.ToHexString(desc.Serialize()), "descriptor");
        var back = PipelineExecutor.Decode(BlockDescriptor.Deserialize(desc.Serialize(), Reg), payload, CodecContext.None);
        Assert.Equal(input, back);
    }

    [Fact]
    public void FilterPipelineDescriptorBytesArePinned()
    {
        // FORMAT §2.2: stage count, then per stage method id, input size and the parameters (names ascending, svarint).
        var spec = PipelineSpec.Parse("pcm16(offset=44,length=24000,channels=2)+delta(stride=4)+lzma(dict=1M,lc=0,lp=2,pb=2)", Reg);
        var desc = new BlockDescriptor(spec, [24044, 24044, 24044]);
        Pinned("0344ECBB012905086368616E6E656C7304066C656E67746880F702036C6D7320066F6666736574580573706C69740242ECBB0109010673"
            + "74726964650804ECBB011F06046469637480808001026662A204026C6300026C7004026D660202706204",
            Convert.ToHexString(desc.Serialize()), "descriptor");
    }
}

/// <summary>Small deterministic generator (SplitMix64): unlike <see cref="Random"/>, its sequence is fixed forever.</summary>
internal sealed class Rng(ulong seed)
{
    private ulong _s = seed;

    public ulong Next()
    {
        ulong z = _s += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public int Below(int n) => (int)(Next() % (ulong)n);
}

/// <summary>Deterministic synthetic inputs (integer arithmetic only, so they are identical on every platform).</summary>
internal static class Synthetic
{
    public const int BmpWidth = 101, BmpHeight = 64, BmpRowBytes = 304, BmpOffset = 54;

    private static readonly string[] Words =
    [
        "the", "of", "and", "to", "in", "is", "osmium", "archive", "block", "filter", "model", "context", "mixing",
        "compress", "data", "table", "record", "stream", "value", "order", "match", "word", "line", "file", "size",
        "golden", "vector", "test", "with", "from", "into", "each", "every", "small", "large", "number",
    ];

    /// <summary>English-like lines: numbered, capitalised sentence starts, a few UPPER words and numbers.</summary>
    public static byte[] Text(int bytes, ulong seed = 1)
    {
        var r = new Rng(seed);
        var sb = new StringBuilder(bytes + 128);
        for (int line = 0; sb.Length < bytes; line++)
        {
            sb.Append("Line ").Append((line * 7919 % 10000).ToString("D4", CultureInfo.InvariantCulture)).Append(':');
            int n = 5 + r.Below(9);
            for (int i = 0; i < n; i++)
            {
                string w = Words[r.Below(Words.Length)];
                if (i == 0) w = char.ToUpperInvariant(w[0]) + w[1..];
                else if (r.Below(23) == 0) w = w.ToUpperInvariant();
                sb.Append(' ').Append(w);
                if (r.Below(11) == 0) sb.Append(' ').Append(r.Below(1000).ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(r.Below(4) == 0 ? "!\n" : ".\n");
        }
        return Encoding.ASCII.GetBytes(sb.ToString(0, bytes));
    }

    /// <summary>Text the text filter must escape: mixed-case and over-long words, control bytes, UTF-8, punctuation runs.</summary>
    public static byte[] HostileText(int bytes, ulong seed = 8)
    {
        string[] odd = ["iPhone", "McDonald", "NASA", "x", "A", "Pneumonoultramicroscopicsilicovolcanoconiosis", "aB", "OK"];
        var r = new Rng(seed);
        var ms = new MemoryStream(bytes + 64);
        while (ms.Length < bytes)
        {
            switch (r.Below(6))
            {
                case 0: ms.Write(Encoding.ASCII.GetBytes(odd[r.Below(odd.Length)])); break;
                case 1: ms.WriteByte((byte)r.Below(32)); break;                       // control bytes (reserved candidates)
                case 2: ms.Write("Grüße 世界"u8); break;                               // UTF-8 high bytes
                case 3: ms.Write(Encoding.ASCII.GetBytes(" .,;:!?-"[..(1 + r.Below(8))])); break;
                default: ms.Write(Encoding.ASCII.GetBytes(Words[r.Below(Words.Length)] + " ")); break;
            }
        }
        return ms.ToArray()[..bytes];
    }

    /// <summary>x86-64-like code: CALL/JMP rel32 (E8/E9) to a few functions, so the operand's top byte is 00/FF,
    /// E8 bytes inside data that are not branches, and common opcodes with ret/int3 padding.</summary>
    public static byte[] X86(int bytes, ulong seed = 2)
    {
        var r = new Rng(seed);
        var d = new byte[bytes];
        var functions = Enumerable.Range(0, 24).Select(k => 64 + k * (bytes / 24)).ToArray();
        int i = 0;
        while (i < bytes - 8)
        {
            switch (r.Below(8))
            {
                case 0:
                case 1:
                    d[i] = r.Below(4) == 0 ? (byte)0xE9 : (byte)0xE8;
                    BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(i + 1), functions[r.Below(functions.Length)] - (i + 5));
                    i += 5;
                    break;
                case 2: // mov rax, [rbp+disp8]
                    d[i] = 0x48; d[i + 1] = 0x8B; d[i + 2] = 0x45; d[i + 3] = (byte)(r.Below(16) * 8);
                    i += 4;
                    break;
                case 3: // push rbp; mov rbp, rsp
                    d[i] = 0x55; d[i + 1] = 0x48; d[i + 2] = 0x89; d[i + 3] = 0xE5;
                    i += 4;
                    break;
                case 4: // an E8 byte in data: the 4th operand byte is neither 00 nor FF
                    d[i] = 0xE8; d[i + 1] = (byte)r.Below(256); d[i + 2] = (byte)r.Below(256); d[i + 3] = (byte)r.Below(256);
                    d[i + 4] = (byte)(1 + r.Below(254));
                    i += 5;
                    break;
                case 5: // ret, then int3 padding to 16 bytes
                    d[i++] = 0xC3;
                    while (i % 16 != 0 && i < bytes) d[i++] = 0xCC;
                    break;
                default: // add/sub r64, imm8
                    d[i] = 0x48; d[i + 1] = 0x83; d[i + 2] = (byte)(0xC0 + r.Below(8)); d[i + 3] = (byte)r.Below(128);
                    i += 4;
                    break;
            }
        }
        return d;
    }

    /// <summary>Bytes drawn from E8, E9, 00, FF, 01 and 7F only: every position is a candidate branch.</summary>
    public static byte[] BranchNoise(int bytes, ulong seed = 9)
    {
        byte[] alphabet = [0xE8, 0xE9, 0x00, 0xFF, 0x01, 0x7F];
        var r = new Rng(seed);
        var d = new byte[bytes];
        for (int i = 0; i < d.Length; i++) d[i] = alphabet[r.Below(alphabet.Length)];
        return d;
    }

    /// <summary>ARM64-like code: little-endian words, about one in five a BL (0x94000000 | imm26) to one of 16
    /// functions, the rest ADD/LDR/MOV/NOP encodings.</summary>
    public static byte[] Arm64(int words, ulong seed = 6)
    {
        var r = new Rng(seed);
        var d = new byte[words * 4];
        var functions = Enumerable.Range(0, 16).Select(k => k * (words / 16)).ToArray();
        for (int k = 0; k < words; k++)
        {
            uint w = r.Below(5) switch
            {
                0 => 0x9400_0000u | ((uint)(functions[r.Below(functions.Length)] - k) & 0x03FF_FFFFu),
                1 => 0x9100_0000u | ((uint)r.Below(4096) << 10) | ((uint)r.Below(32) << 5) | (uint)r.Below(32),
                2 => 0xF940_0000u | ((uint)r.Below(512) << 10) | ((uint)r.Below(32) << 5) | (uint)r.Below(32),
                3 => 0xAA00_03E0u | ((uint)r.Below(32) << 16) | (uint)r.Below(32),
                _ => 0xD503_201Fu,
            };
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(k * 4), w);
        }
        return d;
    }

    /// <summary>Shifts <paramref name="data"/> by two bytes and appends a 3-byte tail (length not a multiple of 4).</summary>
    public static byte[] Misaligned(byte[] data) => [0xAB, 0xCD, .. data, 0x97, 0x00, 0x00];

    /// <summary>12-byte records: int32 id (increasing), float32 reading (a random walk of exact binary fractions),
    /// int32 category.</summary>
    public static byte[] Records(int count, ulong seed = 3)
    {
        var r = new Rng(seed);
        var d = new byte[count * 12];
        int level = 1000;
        for (int k = 0; k < count; k++)
        {
            level += r.Below(9) - 4;
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(k * 12), 100_000 + k);
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(k * 12 + 4), level * 0.125f); // exact: |level| < 2^24
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(k * 12 + 8), r.Below(6));
        }
        return d;
    }

    /// <summary>16-bit stereo tones (an integer parabolic sine, bit-exact everywhere) with a little noise, optionally
    /// behind a 44-byte WAV header.</summary>
    public static byte[] Pcm(int frames, bool header, ulong seed = 4)
    {
        var r = new Rng(seed);
        int hdr = header ? 44 : 0;
        var d = new byte[hdr + frames * 4];
        if (header)
        {
            "RIFF"u8.CopyTo(d);
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), d.Length - 8);
            "WAVEfmt "u8.CopyTo(d.AsSpan(8));
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(20), 1);
            BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(22), 2);
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(24), 44100);
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(28), 44100 * 4);
            BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(32), 4);
            BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(34), 16);
            "data"u8.CopyTo(d.AsSpan(36));
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(40), frames * 4);
        }
        uint left = 0, right = 0x4000_0000;
        for (int f = 0; f < frames; f++)
        {
            left += 0x0123_4567;
            right += 0x00F0_F0F1;
            int l = Parabola(left, 12_000) + r.Below(121) - 60;
            int rr = Parabola(right, 9_000) + r.Below(81) - 40;
            BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(hdr + f * 4), (short)l);
            BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(hdr + f * 4 + 2), (short)rr);
        }
        return d;
    }

    /// <summary>amplitude · sin(2π · phase / 2^32), approximated by parabolic half-waves.</summary>
    private static int Parabola(uint phase, int amplitude)
    {
        long x = (phase & 0x7FFF_FFFFu) >> 15;          // position in the half period: 0..65535
        int v = (int)(x * (65536 - x) * amplitude >> 30); // 0..amplitude
        return (phase & 0x8000_0000u) != 0 ? -v : v;
    }

    /// <summary>A 24-bit BMP-like file: 54-byte header, then 101×64 BGR gradients with a little noise and one byte of
    /// row padding (304-byte rows).</summary>
    public static byte[] Bmp() => Bmp(5);

    public static byte[] Bmp(ulong seed)
    {
        var r = new Rng(seed);
        var d = new byte[BmpOffset + BmpRowBytes * BmpHeight];
        d[0] = (byte)'B'; d[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(2), d.Length);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(10), BmpOffset);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(18), BmpWidth);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(22), BmpHeight);
        BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(28), 24);
        for (int y = 0; y < BmpHeight; y++)
            for (int x = 0; x < BmpWidth; x++)
            {
                int o = BmpOffset + y * BmpRowBytes + x * 3;
                d[o] = (byte)(x * 255 / BmpWidth + r.Below(3));
                d[o + 1] = (byte)(y * 255 / BmpHeight + r.Below(3));
                d[o + 2] = (byte)((x + y) * 2 + r.Below(3));
            }
        return d;
    }
}
