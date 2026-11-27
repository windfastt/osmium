using System.Buffers.Binary;
using System.Diagnostics;
using Osmium.Core.Codecs;
using Osmium.Core.Codecs.Native;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Native;

/// <summary>
/// Decoded equality across encoders and decoders: every vector output of both generations decodes with programs
/// that share no code with Osmium (Linux: liblzma's xz for LZMA with lc + lp at most 4, 7-Zip for the rest, the zstd
/// program for Zstandard), and the same folder compressed with each generation extracts to the same files.
/// </summary>
[Collection("Native encoders")]
public sealed class CrossDecodeTests
{
    private readonly ITestOutputHelper _out;
    public CrossDecodeTests(ITestOutputHelper output) => _out = output;

    private static bool InCi => Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

    private static string? Tool(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static byte[] RunTool(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        var err = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), $"{exe} did not finish");
        copy.Wait();
        Assert.True(p.ExitCode == 0, $"{Path.GetFileName(exe)} {string.Join(' ', args)} failed: {err.Result}");
        return ms.ToArray();
    }

    /// <summary>A .lzma file: properties byte, dictionary size and the known length, then the raw stream.</summary>
    private static byte[] LzmaFile(byte[] raw, ParamValues p, int length)
    {
        int lc = (int)p.Get("lc", 3), lp = (int)p.Get("lp", 0), pb = (int)p.Get("pb", 2);
        int dict = LzmaCodec.EffectiveDict(p.Get("dict", 64 << 20), length);
        var header = new byte[13];
        header[0] = (byte)((pb * 5 + lp) * 9 + lc);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), dict);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(5), length);
        return [.. header, .. raw];
    }

    [Fact]
    public void EveryVectorDecodesWithAnIndependentDecoder()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("the independent decoders run on Linux");
        string? xz = Tool("xz"), sevenZip = Tool("7z") ?? Tool("7zz") ?? Tool("7za"), zstd = Tool("zstd");
        if (xz == null || sevenZip == null || zstd == null)
        {
            if (InCi) Assert.Fail("xz, 7z and zstd are required in CI");
            Assert.Skip("xz, 7z or zstd is not installed");
        }
        using var t = new NativeTestSupport.TempFolder();
        bool native = NativeEncoders.Status == NativeStatus.Active;
        var counts = new Dictionary<string, int>();
        int n = 0;
        foreach (var c in EncoderVectorTests.Cases)
        {
            var m = (MethodBase)MethodRegistry.Default.Get(c.Codec);
            var p = m.Normalize(ParamValues.Of(c.Params));
            var input = EncoderVectorTests.Input(c.Input);
            var outputs = new List<(string Gen, byte[] Data)>
            {
                ("managed-1", m is LzmaCodec l ? l.EncodeManaged(input, p, CodecContext.None) : ((ZstdCodec)m).EncodeManaged(input, p, CodecContext.None)),
            };
            if (native) outputs.Add(("native-1", m.Encode(input, p, CodecContext.None)));
            foreach (var (gen, data) in outputs)
            {
                string decoder = EncoderVectorTests.IndependentDecoder(c);
                string file = Path.Combine(t.Path, $"{n++}.{(c.Codec == "zstd" ? "zst" : "lzma")}");
                File.WriteAllBytes(file, c.Codec == "zstd" ? data : LzmaFile(data, p, input.Length));
                byte[] back = decoder switch
                {
                    "xz" => RunTool(xz, ["--format=lzma", "--decompress", "--stdout", file]),
                    "7z" => RunTool(sevenZip, ["e", "-so", "-tlzma", file]),
                    _ => RunTool(zstd, ["--decompress", "--stdout", "--quiet", file]),
                };
                Assert.True(back.AsSpan().SequenceEqual(input), $"{c.Id} ({gen}): {decoder} decoded something else");
                counts[$"{gen} {decoder}"] = counts.GetValueOrDefault($"{gen} {decoder}") + 1;
            }
        }
        _out.WriteLine(string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}: {k.Value}")));
        if (!native) _out.WriteLine("native-1 not active here: its outputs were not checked");
    }

    private static void MakeFolder(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "docs", "deep"));
        File.WriteAllBytes(Path.Combine(root, "docs", "notes.txt"), VectorInputs.Text(300 << 10, 51));
        File.WriteAllBytes(Path.Combine(root, "docs", "deep", "records.dat"), VectorInputs.Records(400 << 10));
        File.WriteAllBytes(Path.Combine(root, "mixed.bin"), VectorInputs.Mixed(1 << 20, 52));
        File.WriteAllBytes(Path.Combine(root, "random.bin"), VectorInputs.Random(200 << 10, 53));
        File.WriteAllBytes(Path.Combine(root, "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(root, "ünïcødé ファイル.txt"), VectorInputs.Text(5000, 54));
    }

    private static void AssertSameTree(string a, string b)
    {
        var fa = Directory.GetFiles(a, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(a, f)).Order().ToList();
        var fb = Directory.GetFiles(b, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(b, f)).Order().ToList();
        Assert.Equal(fa, fb);
        foreach (var f in fa) Assert.True(File.ReadAllBytes(Path.Combine(a, f)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(b, f))), f);
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("balanced")]
    public void AFolderCompressedWithEachGenerationExtractsToTheSameFiles(string profile)
    {
        var exe = LoadProbe.Require();
        bool native = File.Exists(Path.Combine(LoadProbe.BinDirectory(), NativeTestSupport.LibraryFileName));
        if (!native && NativeEncoders.Mode == EncoderMode.Native) Assert.Fail("the native library is not beside the probe");
        using var t = new NativeTestSupport.TempFolder();
        string src = Path.Combine(t.Path, "src");
        MakeFolder(src);
        foreach (var mode in native ? new[] { "managed", "native" } : ["managed"])
        {
            string archive = Path.Combine(t.Path, $"{mode}.osa");
            var r = LoadProbe.Run(exe, ["compress", src, archive, profile], new Dictionary<string, string?> { ["OSMIUM_ENCODERS"] = mode });
            _out.WriteLine($"{mode}: {r.Output}");
            Assert.Equal(0, r.ExitCode);
            Assert.Equal(mode == "managed" ? "managed-1" : "native-1", r["generation"]);
            using var reader = OsaReader.Open(archive);
            var test = Extractor.Test(reader, null, TestContext.Current.CancellationToken);
            Assert.Equal(0, test.FilesFailed);
            Assert.Empty(test.Errors);
            string outDir = Path.Combine(t.Path, $"out-{mode}");
            var res = Extractor.Extract(reader, outDir, null, null, null, TestContext.Current.CancellationToken);
            Assert.Empty(res.Errors);
            AssertSameTree(src, Path.Combine(outDir, "src"));
        }
    }
}
