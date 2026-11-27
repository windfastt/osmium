using Osmium.Core.Codecs.Native;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests.Native;

/// <summary>
/// Frozen archives per encoder generation (Native/Fixtures/encoders-&lt;generation&gt;.osa, at most 64 KiB each): one
/// block per file, covering LZMA with bt2 and bt4, several lc/lp/pb, a block of more than 1 MiB (large enough for the
/// helper thread) and Zstandard 3, 19, 22 and with long-distance matching. Every build, with or without the native
/// library, must open, test and extract them to the inputs generated here; they stay for as long as Osmium reads
/// .osa, so an archive of any generation can never become unreadable. Written once per generation with
/// OSMIUM_REGENERATE_FIXTURES (see Native/Fixtures/README.md); a shipped generation's fixture is never overwritten.
/// </summary>
[Collection("Native encoders")]
public sealed class GenerationFixtureTests
{
    public const string RegenerateVariable = "OSMIUM_REGENERATE_FIXTURES";
    public const int MaxFixtureSize = 64 << 10;

    public static readonly (string Name, string Pipeline, Func<byte[]> Make)[] Files =
    [
        ("text-lzma-bt4.txt", "lzma(dict=1M)", () => VectorInputs.Text(20 << 10, 71)),
        ("text-lzma-bt2-lc0-pb0.txt", "lzma(dict=1M,lc=0,pb=0,fb=32,mf=0)", () => VectorInputs.Text(12 << 10, 72)),
        ("records-lzma-lp2.dat", "lzma(dict=1M,lc=0,lp=2,pb=2)", () => VectorInputs.Records(32 << 10)),
        ("records-lzma-lc8-lp4.dat", "lzma(dict=1M,lc=8,lp=4,pb=4)", () => VectorInputs.Records(16 << 10)),
        ("mixed-lzma-lc4.bin", "lzma(dict=1M,lc=4,pb=0)", () => VectorInputs.Mixed(12 << 10, 73)),
        ("repeated-lzma-1m.txt", "lzma(dict=4M)", () => VectorInputs.Repeated((1 << 20) + 333, 74)),
        ("text-zstd3.txt", "zstd(level=3)", () => VectorInputs.Text(20 << 10, 75)),
        ("text-zstd19.txt", "zstd(level=19)", () => VectorInputs.Text(20 << 10, 76)),
        ("repeated-zstd19-ldm.txt", "zstd(level=19,ldm=1)", () => VectorInputs.Repeated(1 << 20, 77)),
        ("records-zstd22.dat", "zstd(level=22)", () => VectorInputs.Records(16 << 10)),
    ];

    public static string PathOf(EncoderGeneration g) => NativeTestSupport.SourceFolder("Native", "Fixtures", $"encoders-{g.Id}.osa");

    private static string Normalized(string pipeline) => PipelineSpec.Parse(pipeline, MethodRegistry.Default).ToString();

    public static TheoryData<string> Generations => new(EncoderGeneration.All.Select(g => g.Id));

    [Theory]
    [MemberData(nameof(Generations))]
    public async Task TheFixtureOpensTestsAndExtractsToItsInputs(string id)
    {
        var g = EncoderGeneration.Find(id)!;
        if ((Environment.GetEnvironmentVariable(RegenerateVariable) ?? "").Split(',').Contains(id))
            await Write(g);
        string path = PathOf(g);
        Assert.True(File.Exists(path), $"{path} is missing (Native/Fixtures/README.md says how it is written)");
        Assert.True(new FileInfo(path).Length <= MaxFixtureSize, $"{path} is larger than {MaxFixtureSize} bytes");
        using var reader = OsaReader.Open(path);
        var expected = Files.Select(f => Normalized(f.Pipeline)).Order().ToList();
        var actual = reader.Blocks.Select(b => b.Descriptor.Spec.ToString()).Order().ToList();
        Assert.Equal(expected, actual);
        var test = Extractor.Test(reader, null, TestContext.Current.CancellationToken);
        Assert.Equal(0, test.FilesFailed);
        Assert.Empty(test.Errors);
        using var t = new NativeTestSupport.TempFolder();
        var res = Extractor.Extract(reader, t.Path, null, null, null, TestContext.Current.CancellationToken);
        Assert.Empty(res.Errors);
        foreach (var (name, _, make) in Files)
        {
            var file = Directory.GetFiles(t.Path, name, SearchOption.AllDirectories).Single();
            Assert.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(make()), name);
        }
    }

    /// <summary>Writes the generation's fixture with the encoders of this process, which must be that generation.</summary>
    private static async Task Write(EncoderGeneration g)
    {
        string path = PathOf(g);
        if (g.Shipped && File.Exists(path))
            throw new InvalidOperationException($"{g.Id} is shipped: its fixture is never rewritten.");
        Assert.Equal(g, EncoderGeneration.Active);
        using var t = new NativeTestSupport.TempFolder();
        string src = Path.Combine(t.Path, "encoders");
        Directory.CreateDirectory(src);
        var rules = new List<string>();
        for (int i = 0; i < Files.Length; i++)
        {
            File.WriteAllBytes(Path.Combine(src, Files[i].Name), Files[i].Make());
            rules.Add($$"""{ "id": "f{{i}}", "match": { "globs": ["{{Files[i].Name}}"] }, "actions": { "pipeline": "{{Files[i].Pipeline}}", "solid": "own" } }""");
        }
        var ruleSet = RuleSet.Combine(RuleSet.BuiltIn(), RuleSet.Parse($$"""{ "rules": [ {{string.Join(",\n", rules)}} ] }"""));
        var scan = Scanner.Scan([src]);
        var plan = await new Planner(new PlanOptions { Profile = Profile.ByName("balanced"), Rules = ruleSet, Dedup = DedupMode.Off, Threads = 2, MemoryBudget = 2L << 30 })
            .BuildAsync(scan, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".new";
        File.Delete(tmp);
        await ArchiveBuilder.BuildAsync(plan, new BuildOptions { OutputPath = tmp, Threads = 2, MemoryBudget = 2L << 30 }, TestContext.Current.CancellationToken);
        File.Move(tmp, path, overwrite: true);
        TestContext.Current.SendDiagnosticMessage($"wrote {path} ({new FileInfo(path).Length} bytes) with {g.Id}");
    }
}
