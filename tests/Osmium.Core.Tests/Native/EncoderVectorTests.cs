using System.Text;
using Osmium.Core.Codecs;
using Osmium.Core.Codecs.Native;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Native;

/// <summary>Tests that change or depend on process-wide encoder state run alone.</summary>
[CollectionDefinition("Native encoders", DisableParallelization = true)]
public sealed class NativeEncodersCollection;

/// <summary>
/// Encoder vectors per generation: for a fixed grid of inputs (VectorInputs) and parameters, the SHA-256 and length
/// of the LZMA and Zstandard output, in Native/Vectors/&lt;generation&gt;.txt. managed-1 is checked in every process
/// (through the built-in encoders directly); native-1 whenever the native library is active, which CI forces with
/// OSMIUM_ENCODERS=native on Linux, Windows x64 and Windows ARM64: the same file must pass on all three. Every LZMA
/// case is encoded with one and with two threads (one line covers both), and every output must decode with the
/// built-in decoder to its input.
/// <para>Regenerate a generation that is not shipped with
/// <c>OSMIUM_ENCODERS=native OSMIUM_REGENERATE_VECTORS=native-1 dotnet test -c Release --project tests/Osmium.Core.Tests --filter-class Osmium.Core.Tests.Native.EncoderVectorTests</c>;
/// a shipped generation's file is never overwritten.</para>
/// </summary>
[Collection("Native encoders")]
public sealed class EncoderVectorTests
{
    public sealed record Case(string Id, string Codec, string Input, (string, long)[] Params);

    private const long M = 1 << 20;

    private static Case L(string id, string input, long dict = 16 * M, long lc = 3, long lp = 0, long pb = 2, long fb = 273, long mf = 1) =>
        new($"lzma.{id}", "lzma", input, [("dict", dict), ("lc", lc), ("lp", lp), ("pb", pb), ("fb", fb), ("mf", mf)]);

    private static Case Z(string id, string input, long level, long wlog = 0, long ldm = 0, long ldmmin = 0, long strategy = 0) =>
        new($"zstd.{id}", "zstd", input, [("level", level), ("wlog", wlog), ("ldm", ldm), ("ldmmin", ldmmin), ("strategy", strategy)]);

    public static readonly Case[] Cases =
    [
        // LZMA: every input with the defaults, then each parameter away from them.
        .. VectorInputs.Small.Select(i => L($"default.{i.Name}", i.Name)),
        L("dict1m.mixed-256k", "mixed-256k", dict: M),
        L("dict64k.mixed-256k", "mixed-256k", dict: 64 << 10),
        L("lc0.mixed-256k", "mixed-256k", lc: 0),
        L("lc4.mixed-256k", "mixed-256k", lc: 4),
        L("lc8.mixed-256k", "mixed-256k", lc: 8),
        L("lp2.mixed-256k", "mixed-256k", lp: 2),
        L("pb0.mixed-256k", "mixed-256k", pb: 0),
        L("fb32.mixed-256k", "mixed-256k", fb: 32),
        L("bt2.mixed-256k", "mixed-256k", mf: 0),
        L("small.text-4k", "text-4k", dict: M, lc: 0, pb: 0, fb: 32, mf: 0),
        L("text.text-4k", "text-4k", lc: 4, pb: 0),
        L("max.random-256k", "random-256k", lc: 8, lp: 4, pb: 4),
        L("lc0lp2.random-256k", "random-256k", lc: 0, lp: 2),
        L("records.records-64k1", "records-64k1", lc: 0, lp: 2),
        L("dict64k.records-64k1", "records-64k1", dict: 64 << 10, fb: 32, mf: 0),
        L("bt2fb32.mixed-1m17", "mixed-1m17", dict: M, fb: 32, mf: 0),
        L("lc4lp2.mixed-1m17", "mixed-1m17", lc: 4, lp: 2),
        L("dict64k.mixed-1m17", "mixed-1m17", dict: 64 << 10),
        L("dict1m.text-2m", "text-2m", dict: M, fb: 32),
        L("text.text-2m", "text-2m", lc: 0, pb: 0),
        L("dict1m.mixed-2m", "mixed-2m", dict: M, fb: 32),
        L("max.mixed-2m", "mixed-2m", lc: 8, lp: 4, mf: 0),
        L("bt2.mixed-2m", "mixed-2m", pb: 0, mf: 0),
        L("max.empty", "empty", lc: 8, lp: 4, pb: 4),
        L("bt2.one", "one", mf: 0),
        L("lp4.text-4k", "text-4k", lc: 0, lp: 4, pb: 4),
        L("fb5.records-64k1", "records-64k1", fb: 5),

        // Zstandard: every input at levels 3 and 19, then the other levels, windows, long-distance matching and
        // strategies.
        .. VectorInputs.Small.Select(i => Z($"l3.{i.Name}", i.Name, 3)),
        .. VectorInputs.Small.Select(i => Z($"l19.{i.Name}", i.Name, 19)),
        Z("l1.mixed-256k", "mixed-256k", 1),
        Z("l9.mixed-256k", "mixed-256k", 9),
        Z("l22.mixed-256k", "mixed-256k", 22),
        Z("l1.mixed-1m17", "mixed-1m17", 1),
        Z("l9.mixed-1m17", "mixed-1m17", 9),
        Z("l22.mixed-1m17", "mixed-1m17", 22),
        Z("l1.text-2m", "text-2m", 1),
        Z("l9.text-2m", "text-2m", 9),
        Z("l22.text-2m", "text-2m", 22),
        Z("l19ldm.mixed-2m", "mixed-2m", 19, ldm: 1),
        Z("l3ldm.mixed-1m17", "mixed-1m17", 3, ldm: 1),
        Z("l9ldm32.mixed-2m", "mixed-2m", 9, ldm: 1, ldmmin: 32),
        Z("l9ldm2.mixed-2m", "mixed-2m", 9, ldm: 1, ldmmin: 2),
        Z("l9ldmmin-only.mixed-2m", "mixed-2m", 9, ldmmin: 32),
        Z("l19w24.mixed-2m", "mixed-2m", 19, wlog: 24),
        Z("l3w24.text-2m", "text-2m", 3, wlog: 24),
        Z("l3s9.text-2m", "text-2m", 3, strategy: 9),
        Z("l1s9.mixed-256k", "mixed-256k", 1, strategy: 9),
        Z("l22.random-256k", "random-256k", 22),
        Z("l22ldm.mixed-2m", "mixed-2m", 22, ldm: 1),
        Z("l19w10.mixed-256k", "mixed-256k", 19, wlog: 10),
        Z("l19w17.text-2m", "text-2m", 19, wlog: 17),
    ];

    private static readonly MethodRegistry Reg = MethodRegistry.Default;

    private static (MethodBase Codec, ParamValues P) Prepare(Case c)
    {
        var m = (MethodBase)Reg.Get(c.Codec);
        return (m, m.Normalize(ParamValues.Of(c.Params)));
    }

    private static string ParamText(ParamValues p, MethodBase m) =>
        string.Join(",", m.Params.Select(d => $"{d.Name}={p.Get(d.Name, d.Default)}"));

    /// <summary>Which independent decoder the Linux CI uses for a case: liblzma (xz) reads LZMA only with lc + lp
    /// at most 4; 7-Zip reads the rest; the zstd program reads every frame.</summary>
    internal static string IndependentDecoder(Case c) =>
        c.Codec == "zstd" ? "zstd" : c.Params.First(x => x.Item1 == "lc").Item2 + c.Params.First(x => x.Item1 == "lp").Item2 <= 4 ? "xz" : "7z";

    private static readonly Dictionary<string, byte[]> InputCache = [];

    internal static byte[] Input(string name)
    {
        lock (InputCache)
        {
            if (!InputCache.TryGetValue(name, out var b)) InputCache[name] = b = VectorInputs.Get(name).Make();
            return b;
        }
    }

    internal static string Line(Case c, byte[] input, byte[] output, ParamValues p, MethodBase m) =>
        $"{c.Id} {c.Input} {input.Length} {VectorInputs.Sha256(input)} {ParamText(p, m)} {output.Length} {VectorInputs.Sha256(output)} {IndependentDecoder(c)}";

    private static void CheckDecodes(Case c, MethodBase m, ParamValues p, byte[] input, byte[] output)
    {
        var back = m.Decode(output, input.Length, p, CodecContext.None);
        Assert.True(back.AsSpan().SequenceEqual(input), $"{c.Id}: the built-in decoder did not give the input back");
    }

    [Fact]
    public void Managed1VectorsMatch()
    {
        var lines = new List<string>();
        foreach (var c in Cases)
        {
            var (m, p) = Prepare(c);
            var input = Input(c.Input);
            byte[] output = m switch
            {
                LzmaCodec l => l.EncodeManaged(input, p, CodecContext.None),
                ZstdCodec z => z.EncodeManaged(input, p, CodecContext.None),
                _ => throw new InvalidOperationException(c.Codec),
            };
            CheckDecodes(c, m, p, input, output);
            lines.Add(Line(c, input, output, p, m));
        }
        VectorFile.Check(EncoderGeneration.Managed1, lines);
    }

    [Fact]
    public void Native1VectorsMatch()
    {
        NativeTestSupport.RequireNative();
        Assert.Equal(EncoderGeneration.Native1, EncoderGeneration.Active);
        var lines = new List<string>();
        foreach (var c in Cases)
        {
            var (m, p) = Prepare(c);
            var input = Input(c.Input);
            var one = m.Encode(input, p, new CodecContext { MaxThreads = 2 });
            Assert.Equal(1u, NativeEncode.LastStats.ThreadsUsed);   // Osmium never uses the helper thread
            if (c.Codec == "lzma")
            {
                // The library's two-thread mode, which Osmium does not use, writes the same bytes on these inputs
                // (not on every larger one; see NativeEncode.TestAllowHelperThread).
                NativeEncode.TestAllowHelperThread = true;
                try
                {
                    var two = m.Encode(input, p, new CodecContext { MaxThreads = 2 });
                    Assert.Equal(input.Length >= 1 << 20 ? 2u : 1u, NativeEncode.LastStats.ThreadsUsed);
                    Assert.True(one.AsSpan().SequenceEqual(two), $"{c.Id}: one and two threads wrote different bytes");
                }
                finally
                {
                    NativeEncode.TestAllowHelperThread = false;
                }
            }
            CheckDecodes(c, m, p, input, one);
            lines.Add(Line(c, input, one, p, m));
        }
        VectorFile.Check(EncoderGeneration.Native1, lines);
    }

    [Fact]
    public void ZstdOutputDoesNotDependOnTheChunkSize()
    {
        NativeTestSupport.RequireNative();
        var api = NativeEncoders.ForEncode()!;
        var input = Input("mixed-2m");
        foreach (var level in new[] { 1, 3, 9, 19, 22 })
        {
            var s = new ZstdSettings(level, 21, level >= 19, 0, 0);
            byte[]? reference = null;
            foreach (uint chunk in new uint[] { 0, 64 << 10, 128 << 10, 1 << 20, 4 << 20, 16 << 20 })
            {
                var output = NativeEncode.Zstd(api, input, s, 1L << 30, CodecContext.None, chunk);
                if (reference == null) reference = output;
                else Assert.True(reference.AsSpan().SequenceEqual(output), $"level {level}: chunks of {chunk} bytes change the output");
            }
        }
    }

    [Fact]
    public void EveryCaseHasItsOwnId() => Assert.Equal(Cases.Length, Cases.Select(c => c.Id).Distinct().Count());

    [Fact]
    public void TheGenerationIdsMatchTheLibrarysGenerationFile()
    {
        Assert.Equal(NativeExpected.Generation, EncoderGeneration.Native1.Id);
        Assert.Equal("lzma-sdk 26.06; zstd 1.5.7", NativeExpected.Versions);
    }

    [Fact]
    public void RegenerationRefusesAShippedGeneration()
    {
        Assert.True(EncoderGeneration.Managed1.Shipped);
        var e = Assert.Throws<InvalidOperationException>(() => VectorFile.Write(EncoderGeneration.Managed1, ["x"], force: false));
        Assert.Contains("shipped", e.Message);
    }
}

/// <summary>Reads, compares and (only for a generation that is not shipped) writes Native/Vectors/&lt;id&gt;.txt.</summary>
internal static class VectorFile
{
    public const string RegenerateVariable = "OSMIUM_REGENERATE_VECTORS";

    /// <summary>Native/Vectors in the checkout: where the vectors are read from and regenerated to.</summary>
    public static string Folder() => NativeTestSupport.SourceFolder("Native", "Vectors");

    public static string PathOf(EncoderGeneration g, string suffix = "") => Path.Combine(Folder(), g.Id + suffix + ".txt");

    private static bool RegenerationRequested(EncoderGeneration g, string suffix) =>
        (Environment.GetEnvironmentVariable(RegenerateVariable) ?? "").Split(',', StringSplitOptions.TrimEntries)
            .Contains(g.Id + suffix);

    public static void Check(EncoderGeneration g, IReadOnlyList<string> lines, string suffix = "")
    {
        string path = PathOf(g, suffix);
        if (RegenerationRequested(g, suffix))
        {
            Write(g, lines, force: !File.Exists(path), suffix);
            TestContext.Current.SendDiagnosticMessage($"wrote {lines.Count} vectors to {path}");
            return;
        }
        Assert.True(File.Exists(path), $"{path} is missing. Computed vectors:\n{string.Join("\n", lines)}");
        var expected = File.ReadAllLines(path).Where(l => l.Length > 0 && l[0] != '#')
            .ToDictionary(l => l.Split(' ')[0], l => l);
        var problems = new StringBuilder();
        foreach (var line in lines)
        {
            var f = line.Split(' ');
            if (!expected.TryGetValue(f[0], out var want)) { problems.AppendLine($"no vector for {line}"); continue; }
            var w = want.Split(' ');
            if (w[1] != f[1] || w[2] != f[2] || w[3] != f[3])
                problems.AppendLine($"{f[0]}: input changed (expected {w[1]} {w[2]} {w[3]}, generated {f[1]} {f[2]} {f[3]})");
            else if (w[4] != f[4])
                problems.AppendLine($"{f[0]}: parameters changed (expected {w[4]}, now {f[4]})");
            else if (w[5] != f[5] || w[6] != f[6])
                problems.AppendLine($"{f[0]}: output differs: expected {w[5]} bytes {w[6]}, got {f[5]} bytes {f[6]}");
        }
        foreach (var id in expected.Keys.Except(lines.Select(l => l.Split(' ')[0])))
            problems.AppendLine($"{id}: in the vector file but no longer generated");
        Assert.True(problems.Length == 0, $"{g.Id}{suffix} vectors ({path}):\n{problems}");
    }

    /// <summary>Writes the file; refuses to overwrite the vectors of a shipped generation.</summary>
    public static void Write(EncoderGeneration g, IReadOnlyList<string> lines, bool force, string suffix = "")
    {
        if (g.Shipped && !force)
            throw new InvalidOperationException($"{g.Id} is shipped: its vectors are never regenerated (add a new generation instead).");
        var header = new[]
        {
            $"# Encoder vectors of generation {g.Id}{suffix}: {g.Description}.",
            "# Fields: case input input-length input-sha256 parameters output-length output-sha256 independent-decoder",
            $"# Written by EncoderVectorTests with {VectorFile.RegenerateVariable}={g.Id}{suffix}; never edited by hand.",
        };
        Directory.CreateDirectory(Folder());
        File.WriteAllLines(PathOf(g, suffix), header.Concat(lines));
    }
}
