using System.Buffers.Binary;
using System.Text;
using Osmium.Core.Analysis;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests;

/// <summary>
/// PLAN.md phase-3 exit criteria: on synthetic fixtures the planner must detect the data class and pick the kind of
/// pipeline that is known to work for it (filters for records/code/audio/images, Store for random data, rules win).
/// </summary>
public class PlannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ArchivePlan> PlanSingle(TempDir t, string name, byte[] data, string profile, RuleSet? rules = null)
    {
        var path = t.Sub(name);
        File.WriteAllBytes(path, data);
        var plan = await new Planner(new PlanOptions
        {
            Profile = Profile.ByName(profile),
            Rules = rules ?? RuleSet.BuiltIn(),
            MemoryBudget = 2L << 30,
            Threads = 2,
        }).BuildAsync(Scanner.Scan([path]), Ct);
        Assert.Single(plan.Groups);
        Assert.NotNull(plan.Groups[0].Spec);
        return plan;
    }

    private static byte[] EnglishText(int bytes)
    {
        var words = "the of and to in is that it was for on are with as his they be at one have this from by hot word but what some we can out other were all there when up use your how said an each she which do their time if will way about many then them write would like so these her long make thing see him two has look more day could go come did number sound no most people my over know water than call first who may down side been now find".Split(' ');
        var rnd = new Random(5);
        var sb = new StringBuilder();
        while (sb.Length < bytes)
        {
            sb.Append(char.ToUpperInvariant(words[rnd.Next(words.Length)][0])).Append(words[rnd.Next(words.Length)][1..]);
            for (int i = rnd.Next(4, 14); i > 0; i--) sb.Append(' ').Append(words[rnd.Next(words.Length)]);
            sb.Append(rnd.Next(5) == 0 ? ".\n" : ". ");
        }
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public async Task RandomDataIsStored()
    {
        using var t = new TempDir();
        var data = new byte[300_000];
        new Random(1).NextBytes(data);
        var plan = await PlanSingle(t, "noise.bin", data, "balanced");
        Assert.Equal(CompatClass.Incompressible, plan.Groups[0].Class);
        Assert.True(plan.Groups[0].Spec!.IsStore, plan.Groups[0].Spec!.ToString());
    }

    [Fact]
    public async Task TextGetsATextCodec()
    {
        using var t = new TempDir();
        var plan = await PlanSingle(t, "book.txt", EnglishText(400_000), "max");
        var g = plan.Groups[0];
        Assert.Equal(CompatClass.Text, g.Class);
        Assert.Contains(g.Spec!.Codec.Method.Name, new[] { "ppmd", "lzma", "brotli" });
        Assert.Contains(g.Explain, l => l.Contains("rung"));
    }

    [Fact]
    public async Task RecordsGetDeltaOrTranspose()
    {
        using var t = new TempDir();
        var rnd = new Random(2);
        var data = new byte[400_000];
        for (int i = 0; i < data.Length / 4; i++) BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 4), 5_000_000 + i * 13 + rnd.Next(8));
        var plan = await PlanSingle(t, "samples.dat", data, "max");
        var g = plan.Groups[0];
        Assert.Equal(CompatClass.Structured, g.Class);
        Assert.Equal(4, g.Rep!.Stats.Stride);
        Assert.Contains(g.Spec!.Filters, f => f.Method.Name is "delta" or "transpose");
    }

    [Fact]
    public void Utf16DetectionDoesNotMistakeIntegerTablesForText()
    {
        var rec = new byte[8192];
        for (int i = 0; i < rec.Length / 4; i++) BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(i * 4), 5_000_000 + i * 13);
        using var t = new TempDir();
        File.WriteAllBytes(t.Sub("table.bin"), rec);
        Assert.NotEqual(FileKind.Utf16Text, Sniffer.Analyze(t.Sub("table.bin")).Kind);
        File.WriteAllBytes(t.Sub("text.txt"), Encoding.Unicode.GetBytes(string.Concat(Enumerable.Repeat("Plain UTF-16 text without a BOM. ", 200))));
        Assert.Equal(FileKind.Utf16Text, Sniffer.Analyze(t.Sub("text.txt")).Kind);
    }

    [Fact]
    public async Task X64ExecutableGetsTheBranchConverter()
    {
        using var t = new TempDir();
        var rnd = new Random(3);
        var data = new byte[200_000];
        rnd.NextBytes(data);
        // minimal PE32+ header: MZ, e_lfanew, "PE\0\0", machine x64, optional-header magic, no CLR directory
        data[0] = (byte)'M'; data[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(data.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x84), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x98), 0x20B);
        data.AsSpan(0x98 + 112 + 14 * 8, 8).Clear();
        // x86-like body: calls to a small set of targets, low-entropy filler
        for (int i = 0x400; i < data.Length - 8; i += 8)
        {
            data[i] = 0xE8;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i + 1), 0x1000 * rnd.Next(16) - i - 5);
            data[i + 5] = 0x48; data[i + 6] = 0x8B; data[i + 7] = (byte)rnd.Next(4);
        }
        var plan = await PlanSingle(t, "app.exe", data, "balanced");
        var g = plan.Groups[0];
        Assert.Equal(CompatClass.CodeX86, g.Class);
        Assert.Equal("x86v2", g.Spec!.Filters.First().Method.Name); // x64 code: the converter that also handles RIP-relative operands
    }

    [Fact]
    public async Task WavGetsThePcmPredictorWithParsedGeometry()
    {
        using var t = new TempDir();
        const int frames = 40_000, ch = 2;
        var wav = new byte[44 + frames * ch * 2];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), ch);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 44100);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 44100 * ch * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), ch * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), frames * ch * 2);
        var rnd = new Random(4);
        for (int i = 0; i < frames * ch; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + 2 * i), (short)(12000 * Math.Sin(i * 0.013) + rnd.Next(-60, 60)));
        var plan = await PlanSingle(t, "tone.wav", wav, "balanced");
        var g = plan.Groups[0];
        Assert.Equal(CompatClass.Audio, g.Class);
        var pcm = g.Spec!.Filters.Single(f => f.Method.Name == "pcm16");
        Assert.Equal(44, pcm.Params.Get("offset", -1));
        Assert.Equal(ch, pcm.Params.Get("channels", -1));
    }

    [Theory]
    [InlineData(2L << 30, true)]    // 40 MiB is more than Balanced's 32 MiB blocks, but a song keeps its audio pipeline
    [InlineData(192L << 20, false)] // unless the memory budget cannot hold it in one block
    public async Task LargeWavKeepsItsAudioPipelineAboveTheBlockSize(long memory, bool audio)
    {
        using var t = new TempDir();
        var path = t.Sub("song.wav");
        File.WriteAllBytes(path, Synthetic.Pcm(10_500_000, header: true));
        var plan = await new Planner(new PlanOptions { Profile = Profile.Balanced, MemoryBudget = memory, Threads = 2 })
            .BuildAsync(Scanner.Scan([path], ct: Ct), Ct);
        var g = Assert.Single(plan.Groups);
        Assert.True(g.Size > Profile.Balanced.BlockCap && g.Size < 64L << 20, $"{g.Size:N0} bytes");
        Assert.Equal(audio ? CompatClass.Audio : CompatClass.Structured, g.Class);
        if (audio)
        {
            Assert.Contains(g.Spec!.Filters, f => f.Method.Name == "pcm16");
            Assert.Equal(g.Size, Assert.Single(plan.Blocks).RawSize);
        }
        else Assert.DoesNotContain(g.Spec!.Filters, f => f.Method.Name == "pcm16");
    }

    [Fact]
    public async Task BmpGetsTheImagePredictorWithParsedGeometry()
    {
        using var t = new TempDir();
        const int w = 160, h = 120, rowBytes = (w * 24 + 31) / 32 * 4;
        var bmp = new byte[54 + rowBytes * h];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), h);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = 54 + y * rowBytes + x * 3;
                bmp[o] = (byte)(x * 255 / w); bmp[o + 1] = (byte)(y * 255 / h); bmp[o + 2] = (byte)((x + y) / 2);
            }
        var plan = await PlanSingle(t, "gradient.bmp", bmp, "balanced");
        var g = plan.Groups[0];
        Assert.Equal(CompatClass.Image, g.Class);
        var img = g.Spec!.Filters.Single(f => f.Method.Name == "image");
        Assert.Equal(w, img.Params.Get("width", -1));
        Assert.Equal(h, img.Params.Get("rows", -1));
        Assert.Equal(rowBytes, img.Params.Get("rowbytes", -1));
    }

    [Fact]
    public async Task RuleForcedPipelineWins()
    {
        using var t = new TempDir();
        var rules = RuleSet.Combine(RuleSet.BuiltIn(), RuleSet.Parse("""
            { "rules": [ { "id": "fast-txt", "match": { "extensions": ["txt"] }, "actions": { "pipeline": "zstd(level=3)" } } ] }
            """));
        var plan = await PlanSingle(t, "notes.txt", EnglishText(50_000), "ultra", rules);
        var g = plan.Groups[0];
        Assert.True(g.Forced);
        Assert.Equal("zstd", g.Spec!.Codec.Method.Name);
        Assert.Equal(3, g.Spec.Codec.Params.Get("level", -1));
    }
}
