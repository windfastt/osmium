using Osmium.Core.Analysis;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;
using static Osmium.Core.Tests.PlannerFixtures;

namespace Osmium.Core.Tests;

/// <summary>
/// The planner's search, driven by a scripted trial runner: successive halving, the hill-climb and its budget, the
/// Store preference for incompressible data, the top-2 race, and independence from the thread count.
/// </summary>
public class PlannerSearchTests
{
    private static readonly byte[] Book = Synthetic.Text(400 << 10);
    private const string Lzma3 = "lzma(dict=128M,fb=273,lc=3,lp=0,mf=1,pb=0)";

    /// <summary>Sizes that depend on the rung as well as the spec, so every rung re-ranks the survivors.</summary>
    private static long Noisy(PipelineSpec s, byte[] sample) =>
        sample.Length / 2 + (long)(ScriptedTrials.StableHash($"{s}@{sample.Length}") % (ulong)(sample.Length / 4));

    /// <summary>
    /// LZMA without filters wins. From lc=3 (the text candidate) the climb finds lc=4, then lc=8, and from there no
    /// neighbour is better: changing lp, pb, fb or mf costs 1000, a filter 5000, any other codec scores 10 000.
    /// </summary>
    private static long LzmaLandscape(PipelineSpec s, byte[] sample)
    {
        if (s.Codec.Method.Name != "lzma") return 10_000;
        var p = s.Codec.Params;
        long score = 5000 - 100 * (p.Get("lc", -1) switch { 3 => 1, 4 => 2, 8 => 3, _ => 0 });
        if (p.Get("lp", 0) != 0) score += 1000;
        if (p.Get("pb", 0) != 0) score += 1000;
        if (p.Get("fb", 273) != 273) score += 1000;
        if (p.Get("mf", 1) != 1) score += 1000;
        if (s.Filters.Any()) score += 5000;
        return score;
    }

    private static IEnumerable<string> Best(ScriptedTrials.Call c, int keep) =>
        c.Specs.Zip(c.Sizes).OrderBy(x => x.Second).ThenBy(x => x.First, StringComparer.Ordinal).Take(keep).Select(x => x.First);

    [Theory]
    [InlineData(3, 14, new[] { 14, 5, 2, 2 })]
    [InlineData(2, 14, new[] { 14, 7, 4, 2 })]
    [InlineData(4, 10, new[] { 10, 3, 2, 2 })]
    [InlineData(3, 2, new[] { 2, 2, 2, 2 })]
    public async Task SuccessiveHalvingKeepsTheBestMaxOfTwoAndCeilNOverEta(int eta, int maxCandidates, int[] perRung)
    {
        using var t = new TempDir();
        var trials = new ScriptedTrials(Noisy);
        var profile = Profile.Ultra with
        {
            Name = "halving", Rungs = [16 << 10, 32 << 10, 64 << 10, 128 << 10], Eta = eta, MaxCandidates = maxCandidates,
            SearchEvals = 0, RaceTopTwo = false,
        };
        var plan = await PlanFile(t, "book.txt", Book, Options(profile, trials));
        var g = Assert.Single(plan.Groups);
        Assert.Equal(CompatClass.Text, g.Class);

        var calls = trials.Calls;
        Assert.Equal(perRung, calls.Select(c => c.Specs.Length));
        Assert.Equal(profile.Rungs.Select(r => (int)r), calls.Select(c => c.SampleLength));
        for (int k = 1; k < calls.Count; k++)
        {
            // the survivors of each rung are the best max(2, ceil(n / eta)) of the previous rung, best first
            int keep = Math.Max(2, (int)Math.Ceiling(calls[k - 1].Specs.Length / (double)eta));
            Assert.Equal(Best(calls[k - 1], keep), calls[k].Specs);
        }
        Assert.Equal(Best(calls[^1], 1).Single(), g.Spec!.ToString());
        Assert.Equal(perRung.Length, g.Explain.Count(l => l.Contains(" rung ")));
    }

    [Theory]
    [InlineData(100, new[] { 7, 6, 5 }, 8)] // lc 3 → 4 → 8, then no neighbour is better
    [InlineData(13, new[] { 7, 6 }, 8)]     // the budget is used up exactly by the second step
    [InlineData(9, new[] { 7, 2 }, 8)]      // the second round only gets what is left of the budget
    [InlineData(3, new[] { 3 }, 4)]
    public async Task HillClimbTakesStrictlyBetterNeighboursWithinTheBudget(int evals, int[] rounds, int finalLc)
    {
        using var t = new TempDir();
        var trials = new ScriptedTrials(LzmaLandscape);
        var profile = Profile.Ultra with { Name = "climb", Rungs = [64 << 10], SearchEvals = evals, RaceTopTwo = false };
        var plan = await PlanFile(t, "book.txt", Book, Options(profile, trials));
        var g = Assert.Single(plan.Groups);

        var climb = trials.Calls.Skip(1).ToList(); // call 0 is the only rung
        Assert.Equal(rounds, climb.Select(c => c.Specs.Length));
        Assert.True(climb.Sum(c => c.Specs.Length) <= evals);
        Assert.All(climb, c => Assert.Equal(64 << 10, c.SampleLength));
        var evaluated = climb.SelectMany(c => c.Specs).ToList();
        Assert.Equal(evaluated.Count, evaluated.Distinct().Count()); // no neighbour is evaluated twice
        Assert.Equal(Lzma3.Replace("lc=3", "lc=" + finalLc), g.Spec!.ToString());
        Assert.Equal(finalLc == 8 ? 2 : 1, g.Explain.Count(l => l.Contains("search:")));
    }

    [Fact]
    public async Task HillClimbIgnoresGainsBelowOneInTenThousand()
    {
        // lc=4 is better by 0.005 % only: not an improvement, so the climb stops after its first round.
        using var t = new TempDir();
        var trials = new ScriptedTrials((s, sample) =>
            s.ToString() == Lzma3 ? 100_000 : s.ToString() == Lzma3.Replace("lc=3", "lc=4") ? 99_995 : 200_000);
        var profile = Profile.Ultra with { Name = "flat", Rungs = [64 << 10], SearchEvals = 100, RaceTopTwo = false };
        var plan = await PlanFile(t, "book.txt", Book, Options(profile, trials));
        var g = Assert.Single(plan.Groups);
        Assert.Equal(Lzma3, g.Spec!.ToString());
        Assert.Equal(2, trials.Calls.Count);
        Assert.DoesNotContain(g.Explain, l => l.Contains("search:"));
    }

    [Theory]
    [InlineData(0.995, 0, true, 1)]   // 0.5 % smaller is not worth it: Store
    [InlineData(0.995, 24, true, 1)]  // ... and Store is never hill-climbed
    [InlineData(0.985, 0, false, 1)]  // 1.5 % smaller: zstd
    [InlineData(0.985, 24, false, 2)] // ... which is then hill-climbed
    public async Task IncompressibleGroupsKeepStoreUnlessACodecSavesOnePercent(double ratio, int evals, bool store, int calls)
    {
        using var t = new TempDir();
        var r = new Rng(11);
        var noise = new byte[300_000];
        for (int i = 0; i < noise.Length; i++) noise[i] = (byte)r.Next();
        var trials = new ScriptedTrials((s, sample) => s.IsStore ? sample.Length : (long)(sample.Length * ratio));
        var profile = Profile.Ultra with { Name = "noise", Rungs = [64 << 10], SearchEvals = evals, RaceTopTwo = false };
        var plan = await PlanFile(t, "noise.bin", noise, Options(profile, trials));
        var g = Assert.Single(plan.Groups);
        Assert.Equal(CompatClass.Incompressible, g.Class);
        Assert.Equal(["store", "zstd(ldm=0,ldmmin=0,level=19,strategy=0,wlog=0)"], trials.Calls[0].Specs.Order(StringComparer.Ordinal));
        Assert.Equal(store, g.Spec!.IsStore);
        Assert.Equal(store ? "store" : "zstd", g.Spec.Codec.Method.Name);
        Assert.Equal(calls, trials.Calls.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TopTwoRaceAddsTheRunnerUpOfTheLastRung(bool race)
    {
        using var t = new TempDir();
        var profile = Profile.Ultra with { Name = "race", Rungs = [64 << 10], SearchEvals = 0, RaceTopTwo = race };
        var plan = await PlanFile(t, "book.txt", Book, Options(profile, new ScriptedTrials(LzmaLandscape)));
        var g = Assert.Single(plan.Groups);
        Assert.Equal(Lzma3, g.Spec!.ToString());
        if (!race) { Assert.Empty(g.Alternates); return; }
        Assert.Equal(["text+" + Lzma3], g.Alternates.Select(s => s.ToString()));
        Assert.Contains(g.Explain, l => l.EndsWith($"(race vs text+{Lzma3})", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InsaneRacesTheClimbedPipelineAgainstTheBestOfTheLastRung()
    {
        using var t = new TempDir();
        var trials = new ScriptedTrials(LzmaLandscape);
        var plan = await PlanFile(t, "book.txt", Synthetic.Text(600 << 10), Options(Profile.Insane, trials, memory: 8L << 30));
        var g = Assert.Single(plan.Groups);
        // rung 256 KiB samples 16 slices; rung 1 MiB covers the whole 600 KiB group, which ends the halving
        var rungs = trials.Calls.Take(2).ToList();
        Assert.Equal([256 << 10, (int)g.Size], rungs.Select(c => c.SampleLength));
        Assert.Equal([14, 5], rungs.Select(c => c.Specs.Length)); // every text candidate (CM included), then max(2, ⌈14/3⌉)
        Assert.True(trials.Calls.Count > 2, "no hill-climb"); // how the climb spends its evaluations is tested above
        Assert.Equal("lzma(dict=256M,fb=273,lc=8,lp=0,mf=1,pb=0)", g.Spec!.ToString());
        Assert.Equal(["lzma(dict=256M,fb=273,lc=3,lp=0,mf=1,pb=0)"], g.Alternates.Select(s => s.ToString()));
    }

    private static string MixedFolder(TempDir t)
    {
        var dir = t.Sub("in");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        var r = new Rng(12);
        var noise = new byte[100_000];
        for (int i = 0; i < noise.Length; i++) noise[i] = (byte)r.Next();
        File.WriteAllBytes(Path.Combine(dir, "book.txt"), Synthetic.Text(300 << 10));
        File.WriteAllBytes(Path.Combine(dir, "sub", "notes.txt"), Synthetic.Text(40 << 10, seed: 2));
        File.WriteAllBytes(Path.Combine(dir, "records.bin"), Synthetic.Records(20_000));
        File.WriteAllBytes(Path.Combine(dir, "code.bin"), Synthetic.X86(120 << 10));
        File.WriteAllBytes(Path.Combine(dir, "tone.wav"), Synthetic.Pcm(30_000, header: true));
        File.WriteAllBytes(Path.Combine(dir, "image.bmp"), Synthetic.Bmp());
        File.WriteAllBytes(Path.Combine(dir, "noise.bin"), noise);
        return dir;
    }

    [Fact]
    public async Task PlanDoesNotDependOnTheThreadCount()
    {
        // Real trials (local runner, max profile): one trial at a time versus eight at once.
        using var t = new TempDir();
        var dir = MixedFolder(t);
        async Task<string> Plan(int threads) =>
            Fingerprint(await new Planner(Options(Profile.Max, null, threads: threads)).BuildAsync(Scanner.Scan([dir]), Ct));
        string one = await Plan(1);
        Assert.Equal(one, await Plan(8));
        Assert.True(one.Split('\n').Count(l => l.StartsWith('G')) >= 5, one);
    }

    [Fact]
    public async Task PlanDoesNotDependOnTheOrderGroupsFinishIn()
    {
        // Scripted trials with a hill-climb, delayed by varying amounts so that concurrently planned groups interleave
        // differently in every run. The LZMA landscape guarantees climbing steps on any sample; the noise, smaller than
        // a step, re-ranks the other candidates on every rung.
        using var t = new TempDir();
        var dir = MixedFolder(t);
        var profile = Profile.Ultra with { Name = "jitter", Rungs = [16 << 10, 64 << 10] };
        static long Landscape(PipelineSpec s, byte[] sample) =>
            LzmaLandscape(s, sample) * 1000 + (long)(ScriptedTrials.StableHash($"{s}@{sample.Length}") % 1000);
        async Task<string> Plan(int threads, int salt) => Fingerprint(await new Planner(Options(profile, new ScriptedTrials(Landscape)
        {
            Delay = n => TimeSpan.FromMilliseconds(ScriptedTrials.StableHash($"{salt}:{n}") % 12),
        }, threads: threads)).BuildAsync(Scanner.Scan([dir]), Ct));
        string first = await Plan(1, 0);
        Assert.Equal(first, await Plan(8, 1));
        Assert.Equal(first, await Plan(3, 2));
        Assert.Contains("search:", first);
    }
}
