using System.Numerics;
using System.Text;
using Osmium.Core.Analysis;
using Osmium.Core.Planning;
using static Osmium.Core.Tests.PlannerFixtures;

namespace Osmium.Core.Tests;

/// <summary>
/// Similarity ordering inside a group: the SimHash signature and its sample, the greedy
/// nearest-neighbour chain, and how the planner applies it (which groups, units kept with their file, explain).
/// </summary>
public class SimilarityOrderTests
{
    private static ulong SimHashOf(params byte[][] parts)
    {
        var h = new SimilarityOrder.SimHash();
        foreach (var p in parts) h.Add(p);
        return h.Value;
    }

    private static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>C-header-like text: nothing in common with <see cref="Synthetic.Text"/> but the alphabet.</summary>
    private static byte[] Defines(int bytes, ulong seed)
    {
        string[] names = ["BUFFER", "LIMIT", "FLAG", "MASK", "COUNT", "OFFSET", "SHIFT", "MODE"];
        var r = new Rng(seed);
        var sb = new StringBuilder();
        for (int i = 0; sb.Length < bytes; i++)
            sb.Append("#define OSM_").Append(names[r.Below(names.Length)]).Append('_').Append(i)
              .Append("\t0x").Append((r.Next() & 0xFFFF).ToString("X4")).Append('\n');
        return Encoding.ASCII.GetBytes(sb.ToString(0, bytes));
    }

    [Theory]
    [InlineData(0L, new long[0])]
    [InlineData(3L, new long[] { 0, 3 })]
    [InlineData((long)SimilarityOrder.SampleBytes, new long[] { 0, SimilarityOrder.SampleBytes })]
    // just above the whole-file limit, and below 4 × 64 KiB, each window starts where the previous one ends
    [InlineData((long)SimilarityOrder.SampleBytes + 1, new long[] { 0, 64 << 10, 64 << 10, 16 << 10, 80 << 10, 16 << 10, 96 << 10, 16 << 10 })]
    [InlineData(200L << 10, new long[] { 0, 64 << 10, 64 << 10, 16 << 10, 100 << 10, 16 << 10, 150 << 10, 16 << 10 })]
    [InlineData(1L << 20, new long[] { 0, 64 << 10, 256 << 10, 16 << 10, 512 << 10, 16 << 10, 768 << 10, 16 << 10 })]
    public void TheSampleIsTheWholeFileOrTheHeadAndThreeWindows(long size, long[] expected)
    {
        var ranges = SimilarityOrder.SampleRanges(size);
        Assert.Equal(expected, ranges.SelectMany(r => new[] { r.Offset, r.Length }));
        for (int k = 1; k < ranges.Length; k++) Assert.True(ranges[k].Offset >= ranges[k - 1].Offset + ranges[k - 1].Length);
        Assert.All(ranges, r => Assert.True(r.Offset + r.Length <= size));
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(SimilarityOrder.SampleBytes)]
    [InlineData(SimilarityOrder.SampleBytes + 1)]
    [InlineData(777_777)]
    public void AFilesSignatureIsTheSimHashOfItsSample(int size)
    {
        using var t = new TempDir();
        var data = Synthetic.Text(size, seed: 3);
        File.WriteAllBytes(t.Sub("f.txt"), data);
        var parts = SimilarityOrder.SampleRanges(size).Select(r => data.AsSpan((int)r.Offset, r.Length).ToArray()).ToArray();
        Assert.Equal(SimHashOf(parts), SimilarityOrder.Signature(t.Sub("f.txt"), size));
        // a file that is shorter than the scan said cannot be signed
        Assert.ThrowsAny<IOException>(() => SimilarityOrder.Signature(t.Sub("f.txt"), 2L * size + 100_000));
    }

    [Fact]
    public void EveryOccurrenceOfA4GramVotes()
    {
        // "AAAA" occurs three times and "AAAB" once: the signature is the hash of "AAAA". (If each distinct 4-gram
        // voted once, the two would tie and only their common bits would be set.)
        uint aaaa = 0x41414141, aaab = 0x42414141; // little-endian
        Assert.Equal(SimilarityOrder.SimHash.Hash(aaaa), SimHashOf("AAAAAAB"u8.ToArray()));
        Assert.NotEqual(SimilarityOrder.SimHash.Hash(aaaa), SimilarityOrder.SimHash.Hash(aaab));
        // a single 4-gram is its own hash; 4-grams do not span two parts, and parts shorter than 4 bytes add nothing
        Assert.Equal(SimilarityOrder.SimHash.Hash(aaab), SimHashOf("AAAB"u8.ToArray()));
        Assert.Equal(0UL, SimHashOf("AA"u8.ToArray(), "AB"u8.ToArray()));
        Assert.Equal(SimHashOf("AAAB"u8.ToArray()), SimHashOf("AAAB"u8.ToArray(), "xyz"u8.ToArray()));
        // more than 255 votes per bit (the counters' flush interval) are counted exactly
        var many = new byte[5000];
        Array.Fill(many, (byte)'A');
        many[^1] = (byte)'B';
        Assert.Equal(SimilarityOrder.SimHash.Hash(aaaa), SimHashOf(many));
    }

    [Fact]
    public void SimilarContentHasCloseSignatures()
    {
        var a = Synthetic.Text(60_000, seed: 1);
        var edited = (byte[])a.Clone();
        for (int i = 0; i < 50; i++) edited[i * 1000 + 7] = (byte)'#';
        ulong sa = SimHashOf(a), se = SimHashOf(edited), sb = SimHashOf(Synthetic.Text(60_000, seed: 2)), sd = SimHashOf(Defines(60_000, 1));
        Assert.True(Distance(sa, se) <= 3, $"edited: {Distance(sa, se)}");
        Assert.True(Distance(sa, se) < Distance(sa, sb) && Distance(sa, sb) < Distance(sa, sd), $"{Distance(sa, se)} {Distance(sa, sb)} {Distance(sa, sd)}");
        Assert.True(Distance(sa, sd) >= 16, $"unrelated: {Distance(sa, sd)}");
    }

    [Theory]
    [InlineData(new ulong[] { 0b0000, 0b1111, 0b0001, 0b0111, 0b0011 }, new[] { 0, 2, 4, 3, 1 })] // nearest each time
    [InlineData(new ulong[] { 5, 5, 5, 5 }, new[] { 0, 1, 2, 3 })]                                 // all equal: unchanged
    [InlineData(new ulong[] { 0b0000, 0b0110, 0b1000, 0b0001, 0b1110 }, new[] { 0, 2, 3, 1, 4 })] // ties: lowest index
    [InlineData(new ulong[] { ulong.MaxValue, 0, 1, ulong.MaxValue - 1 }, new[] { 0, 3, 1, 2 })]   // starts at the first
    [InlineData(new ulong[] { 42 }, new[] { 0 })]
    [InlineData(new ulong[0], new int[0])]
    public void TheChainTakesTheNearestNextBreakingTiesByTheOriginalOrder(ulong[] signatures, int[] expected) =>
        Assert.Equal(expected, SimilarityOrder.Chain(signatures));

    [Fact]
    public void TheChainIsAPermutationForManyItems()
    {
        var r = new Rng(5);
        var sig = Enumerable.Range(0, 3000).Select(_ => r.Next() & 0xFFFF_0000_0000_FFFFUL).ToArray();
        var chain = SimilarityOrder.Chain(sig);
        Assert.Equal(Enumerable.Range(0, sig.Length), chain.Order());
        Assert.Equal(0, chain[0]);
        // greedy: no item placed later is nearer to an item than the one placed right after it
        for (int k = 0; k + 1 < chain.Length; k += 97)
        {
            int next = Distance(sig[chain[k]], sig[chain[k + 1]]);
            Assert.All(chain.Skip(k + 2), j => Assert.True(Distance(sig[chain[k]], sig[j]) >= next));
        }
    }

    /// <summary>Six text files whose name order alternates between two kinds of content.</summary>
    private static string Interleaved(TempDir t, int bytes = 20_000, string name = "interleaved")
    {
        var dir = t.Sub(name);
        Directory.CreateDirectory(dir);
        for (int k = 1; k <= 6; k++)
            File.WriteAllBytes(Path.Combine(dir, $"a{k}.txt"), k % 2 == 1 ? Synthetic.Text(bytes + 100 * k, seed: 1) : Defines(bytes + 100 * k, 4));
        return dir;
    }

    private static List<string> FileOrder(ArchivePlan plan, PlanGroup g) =>
        g.Units.Select(u => plan.Files[plan.Units[u].File].Src.RelPath).Distinct().Select(Path.GetFileName).ToList()!;

    private static async Task<ArchivePlan> PlanFolder(string dir, Profile profile, int threads = 2) =>
        await new Planner(Options(profile, new ScriptedTrials((s, sample) => sample.Length), threads: threads)).BuildAsync(Scanner.Scan([dir], ct: Ct), Ct);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SimilarFilesBecomeNeighbours(bool on)
    {
        using var t = new TempDir();
        var plan = await PlanFolder(Interleaved(t), Profile.Max with { SimilarityOrder = on });
        var g = Assert.Single(plan.Groups);
        Assert.Equal(CompatClass.Text, g.Class);
        var order = FileOrder(plan, g);
        if (on)
        {
            Assert.Equal(["a1.txt", "a3.txt", "a5.txt"], order.Take(3).Order());
            Assert.Equal(["a2.txt", "a4.txt", "a6.txt"], order.Skip(3).Order());
            Assert.Equal("a1.txt", order[0]); // the chain starts with the first file in name order
            Assert.Contains("  order: similarity (SimHash), 6 files", g.Explain);
        }
        else
        {
            Assert.Equal(Enumerable.Range(1, 6).Select(k => $"a{k}.txt"), order);
            Assert.DoesNotContain(g.Explain, l => l.Contains("order:"));
        }
        // blocks follow the group's unit order
        Assert.Equal(g.Units, plan.Blocks.Where(b => b.Group == g.Id).SelectMany(b => b.Parts).Select(p => p.Unit).Distinct());
    }

    [Fact]
    public async Task ChunkDedupUnitsStayWithTheirFileAndInOrder()
    {
        // Insane splits files into content-defined chunks, and a chunk already seen belongs to the first file that had
        // it. Reordering moves whole files: every file's own units stay together and in file order.
        using var t = new TempDir();
        var dir = t.Sub("in");
        Directory.CreateDirectory(dir);
        var shared = Synthetic.Text(200_000, seed: 9);
        for (int k = 1; k <= 6; k++)
        {
            byte[] own = k % 2 == 1 ? Synthetic.Text(300_000 + 1000 * k, seed: 1) : Defines(300_000 + 1000 * k, 4);
            File.WriteAllBytes(Path.Combine(dir, $"a{k}.txt"), k == 2 ? [.. own, .. shared] : k == 5 ? [.. shared, .. own] : own);
        }
        var plan = await PlanFolder(dir, Profile.Insane with { Rungs = [64 << 10], SearchEvals = 0, RaceTopTwo = false, SimilarityOrder = true }, threads: 3);
        var g = Assert.Single(plan.Groups);
        Assert.Contains("  order: similarity (SimHash), 6 files", g.Explain);
        Assert.Contains(plan.Files, f => f.Units.Count(u => plan.Units[u].File == plan.Files.IndexOf(f)) >= 4);
        Assert.Contains(plan.Files, f => f.Units.Any(u => plan.Units[u].File != plan.Files.IndexOf(f))); // shared chunks

        var runs = new List<int>(); // the file of each run of consecutive units
        foreach (int u in g.Units)
            if (runs.Count == 0 || runs[^1] != plan.Units[u].File) runs.Add(plan.Units[u].File);
        Assert.Equal(runs.Count, runs.Distinct().Count());
        foreach (int file in runs)
        {
            int fileIndex = file;
            var ownInGroup = g.Units.Where(u => plan.Units[u].File == fileIndex).ToList();
            Assert.Equal(plan.Files[fileIndex].Units.Where(u => plan.Units[u].File == fileIndex), ownInGroup);
            Assert.Equal(ownInGroup.Select(u => plan.Units[u].Offset).Order(), ownInGroup.Select(u => plan.Units[u].Offset));
        }
        Assert.NotEqual(runs.Order(), runs); // the files were reordered
        Assert.Equal(plan.StoredInput, g.Units.Sum(u => plan.Units[u].Length));
    }

    [Fact]
    public async Task OnlyGroupsOfThreeOrMoreFilesOfTheRightClassesAreReordered()
    {
        using var t = new TempDir();
        var dir = t.Sub("in");
        Directory.CreateDirectory(dir);
        var r = new Rng(3);
        for (int k = 1; k <= 3; k++)
        {
            var noise = new byte[30_000];
            for (int i = 0; i < noise.Length; i++) noise[i] = (byte)r.Next();
            File.WriteAllBytes(Path.Combine(dir, $"n{k}.bin"), noise);          // incompressible: not reordered
        }
        File.WriteAllBytes(Path.Combine(dir, "t1.txt"), Synthetic.Text(30_000, seed: 1)); // two text files: too few
        File.WriteAllBytes(Path.Combine(dir, "t2.txt"), Defines(30_000, 2));
        for (int k = 1; k <= 3; k++)
            File.WriteAllBytes(Path.Combine(dir, $"c{k}.bin"), Synthetic.X86(40_000 + 1000 * k, seed: (ulong)k)); // binary: reordered
        var plan = await PlanFolder(dir, Profile.Ultra with { Rungs = [64 << 10], SearchEvals = 0, SimilarityOrder = true });
        var reordered = Assert.Single(plan.Groups, g => g.Explain.Any(l => l.Contains("order: similarity")));
        Assert.Equal(["c1.bin", "c2.bin", "c3.bin"], FileOrder(plan, reordered).Order());
        Assert.Equal(CompatClass.Binary, reordered.Class); // raw code without an executable header
        Assert.Contains(plan.Groups, g => g.Class == CompatClass.Incompressible && plan.Files.Count(f => f.Group == g.Id) == 3);
        Assert.Contains(plan.Groups, g => g.Class == CompatClass.Text && plan.Files.Count(f => f.Group == g.Id) == 2);

        // profiles below Max keep the name order everywhere
        var balanced = await PlanFolder(Interleaved(t, name: "balanced"), Profile.Balanced);
        Assert.False(Profile.Balanced.SimilarityOrder);
        Assert.Equal(Enumerable.Range(1, 6).Select(k => $"a{k}.txt"), FileOrder(balanced, Assert.Single(balanced.Groups)));
    }

    [Fact]
    public async Task AGroupForcedToStoreKeepsTheNameOrder()
    {
        // Store gains nothing from a better order, so the files are not even read for it.
        using var t = new TempDir();
        var dir = Interleaved(t);
        var options = Options(Profile.Max with { SimilarityOrder = true }, new ScriptedTrials((s, sample) => sample.Length)) with
        {
            ForcedPipeline = "store",
        };
        var plan = await new Planner(options).BuildAsync(Scanner.Scan([dir], ct: Ct), Ct);
        var g = Assert.Single(plan.Groups);
        Assert.True(g.Forced);
        Assert.Equal(Enumerable.Range(1, 6).Select(k => $"a{k}.txt"), FileOrder(plan, g));
        Assert.DoesNotContain(g.Explain, l => l.Contains("order:"));
    }

    [Fact]
    public async Task AGroupWithAFileThatCannotBeReadKeepsTheNameOrder()
    {
        // a2.txt shrinks after the scan, so its signature cannot be computed: the group keeps the name order and says why.
        // Balanced takes no samples, so nothing else in planning reads the missing bytes.
        using var t = new TempDir();
        var dir = Interleaved(t);
        var scan = Scanner.Scan([dir], ct: Ct);
        var shrunk = Path.Combine(dir, "a2.txt");
        File.WriteAllBytes(shrunk, File.ReadAllBytes(shrunk)[..1000]);
        var plan = await new Planner(Options(Profile.Balanced with { SimilarityOrder = true }, null)).BuildAsync(scan, Ct);
        var g = Assert.Single(plan.Groups);
        Assert.Equal(Enumerable.Range(1, 6).Select(k => $"a{k}.txt"), FileOrder(plan, g));
        Assert.Contains(g.Explain, l => l.StartsWith("  order: by extension, name and path (", StringComparison.Ordinal)
            && l.Contains("a2.txt") && l.EndsWith(" could not be read)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheOrderDoesNotDependOnTheThreadCount()
    {
        using var t = new TempDir();
        var dir = Interleaved(t, 40_000);
        var max = Profile.Max with { SimilarityOrder = true };
        string one = Fingerprint(await PlanFolder(dir, max, threads: 1));
        Assert.Equal(one, Fingerprint(await PlanFolder(dir, max, threads: 8)));
        Assert.Contains("order: similarity", one);
    }
}
