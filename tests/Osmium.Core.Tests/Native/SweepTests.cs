using Osmium.Core.Codecs;
using Osmium.Core.Codecs.Native;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Native;

/// <summary>
/// The cross-platform sweep: 2,000 seeded random cases per codec (sizes up to 1 MiB from the VectorInputs patterns,
/// random parameters within their ranges, through the same clamps as every encode) encoded with the native
/// encoders, one line per case with the output's length and SHA-256, written to OSMIUM_SWEEP_OUT (default
/// artifacts/native-sweep/sweep.txt). native.yml runs it on Linux x64, Windows x64 and Windows ARM64 and
/// native/sweep/compare.py requires the three lists to be identical: this reaches the architecture-specific paths of
/// the Zstandard compressor (SSE2, NEON and scalar match finders; BMI2 on gcc/clang x64 only) far beyond the vectors.
/// Explicit (minutes): <c>dotnet test -c Release --project tests/Osmium.Core.Tests --filter-class Osmium.Core.Tests.Native.SweepTests --explicit only</c>.
/// </summary>
[Collection("Native encoders")]
public sealed class SweepTests
{
    public const int CasesPerCodec = 2000;
    private static readonly long[] Dicts = [1 << 16, 1 << 17, 1 << 20, (3 << 20) + 1, 16 << 20, 64 << 20, 1 << 29];

    internal sealed record SweepCase(int Index, string Codec, string Input, Func<byte[]> Make, (string, long)[] Params, int Threads);

    /// <summary>The seeded cases; every platform generates the same list.</summary>
    internal static IEnumerable<SweepCase> Cases()
    {
        var r = new VectorInputs.Rng(20261126);
        foreach (var codec in new[] { "lzma", "zstd" })
            for (int i = 0; i < CasesPerCodec; i++)
            {
                int e = r.Below(21);
                int size = e == 20 ? 1 << 20 : (1 << e) + r.Below(1 << e);
                if (r.Below(20) == 0) size = r.Below(3);
                ulong seed = r.Next() % 1_000_000;
                int pattern = r.Below(5);
                string name = pattern switch { 0 => "text", 1 => "records", 2 => "random", 3 => "mixed", _ => "repeated" } + $"-{size}-{seed}";
                Func<byte[]> make = pattern switch
                {
                    0 => () => VectorInputs.Text(size, seed),
                    1 => () => VectorInputs.Records(size),
                    2 => () => VectorInputs.Random(size, seed),
                    3 => () => VectorInputs.Mixed(size, seed),
                    _ => () => VectorInputs.Repeated(size, seed),
                };
                (string, long)[] p = codec == "lzma"
                    ? [("dict", Dicts[r.Below(Dicts.Length)]), ("lc", r.Below(9)), ("lp", r.Below(5)), ("pb", r.Below(5)),
                       ("fb", 5 + r.Below(269)), ("mf", r.Below(2))]
                    : [("level", 1 + r.Below(22)), ("wlog", r.Below(32)), ("ldm", r.Below(2)),
                       ("ldmmin", r.Below(3) == 0 ? r.Below(4097) : 0), ("strategy", r.Below(10))];
                yield return new SweepCase(i, codec, name, make, p, 1 + r.Below(2));
            }
    }

    [Fact(Explicit = true)]
    public void SweepWritesTheOutputHashes()
    {
        NativeTestSupport.RequireNative();
        // A relative OSMIUM_SWEEP_OUT is relative to the checkout.
        string path = Environment.GetEnvironmentVariable("OSMIUM_SWEEP_OUT") is { Length: > 0 } o
            ? Path.GetFullPath(o, NativeTestSupport.RepoRoot())
            : Path.Combine(NativeTestSupport.RepoRoot(), "artifacts", "native-sweep", "sweep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new StreamWriter(path);
        w.WriteLine($"# osmium native sweep, {NativeEncoders.StatusMessage}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        int n = 0;
        foreach (var c in Cases())
        {
            var m = (MethodBase)MethodRegistry.Default.Get(c.Codec);
            var p = m.Normalize(ParamValues.Of(c.Params));
            var input = c.Make();
            var output = m.Encode(input, p, new CodecContext { MaxThreads = c.Threads });
            Assert.True(m.Decode(output, input.Length, p, CodecContext.None).AsSpan().SequenceEqual(input), $"{c.Codec} {c.Index}");
            w.WriteLine($"{c.Codec}.{c.Index} {c.Input} {VectorInputs.Sha256(input)[..16]} " +
                        $"{string.Join(",", m.Params.Select(d => $"{d.Name}={p.Get(d.Name, d.Default)}"))} {output.Length} {VectorInputs.Sha256(output)}");
            n++;
        }
        Assert.Equal(2 * CasesPerCodec, n);
        TestContext.Current.SendDiagnosticMessage($"wrote {n} sweep lines to {path}");
    }
}
