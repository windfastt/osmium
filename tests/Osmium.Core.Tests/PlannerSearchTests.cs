using Osmium.Core.Analysis;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;
using static Osmium.Core.Tests.PlannerFixtures;

namespace Osmium.Core.Tests;

/// <summary>
/// The planner's search, driven by a scripted trial runner: successive halving, the hill-climb and its budget, the
/// Store preference for incompressible data, the top-2 race, near ties going to the faster decoder, search steps
/// screened on the smaller sample, and independence from the thread count.
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

    // ------------------------------------------------------------------ near ties go to the faster decoder

    private static Profile Tie(bool on, int evals = 0) => Profile.Ultra with
    {
        Name = "tie", Rungs = [64 << 10], SearchEvals = evals, RaceTopTwo = true, PreferFasterDecoder = on,
    };

    /// <summary>The first text candidate: context mixing with the text models, which decodes at about 0.6 MB/s.</summary>
    private static readonly string TextCm = Candidates.For(CompatClass.Text, null, Tie(true), MethodRegistry.Default)[0].ToString();

    /// <summary>The CM text candidate scores 100 000 on text, the LZMA text candidate <paramref name="gap"/> more
    /// (LZMA decodes at about 40 MB/s), everything else twice as much.</summary>
    private static Func<PipelineSpec, byte[], long> CmVersusLzma(double gap) => (s, sample) =>
        s.ToString() == TextCm ? 100_000
        : s.ToString() == Lzma3 ? (long)Math.Round(100_000 * (1 + gap))
        : 200_000;

    [Fact]
    public void TheTieLandscapeStartsWithContextMixing()
    {
        var cm = PipelineSpec.Parse(TextCm, MethodRegistry.Default);
        Assert.True(MethodIds.IsContextMixing(cm.Codec.Method.Id), TextCm);
        Assert.Empty(cm.Filters);
    }

    [Theory]
    [InlineData(0.0004, true, true)]   // 0.04 %: a tie, and LZMA decodes much faster than CM
    [InlineData(0.0009, true, true)]
    [InlineData(0.0011, true, false)]  // 0.11 %: not a tie
    [InlineData(0.0004, false, false)] // the switch is off
    public async Task NearTiesGoToTheFasterDecoder(double gap, bool on, bool lzmaWins)
    {
        using var t = new TempDir();
        var trials = new ScriptedTrials(CmVersusLzma(gap));
        var plan = await PlanFile(t, "book.txt", Book, Options(Tie(on), trials));
        var g = Assert.Single(plan.Groups);
        string cm = TextCm;
        Assert.Equal(cm, trials.Calls[0].Specs[0]);
        Assert.Equal(lzmaWins ? Lzma3 : cm, g.Spec!.ToString());
        // the top-two race gets the best remaining candidate: CM when LZMA won the tie
        Assert.Equal([lzmaWins ? cm : Lzma3], g.Alternates.Select(s => s.ToString()));
        var tie = g.Explain.Where(l => l.Contains("tie:")).ToList();
        if (lzmaWins) Assert.Equal([$"  tie: chose {Lzma3} (decodes faster) over {cm} ({gap * 100:0.00} % smaller)"], tie);
        else Assert.Empty(tie);
    }

    [Fact]
    public async Task TheSearchStartsFromTheTieBreakWinner()
    {
        using var t = new TempDir();
        var trials = new ScriptedTrials(CmVersusLzma(0.0005));
        var plan = await PlanFile(t, "book.txt", Book, Options(Tie(true, evals: 24), trials));
        Assert.True(trials.Calls.Count > 1, "no search");
        Assert.All(trials.Calls.Skip(1).SelectMany(c => c.Specs), s => Assert.Contains("lzma(", s)); // neighbours of LZMA
        Assert.Equal(Lzma3, Assert.Single(plan.Groups).Spec!.ToString()); // none of them is better
    }

    [Fact]
    public async Task TheFastestOfSeveralNearTiesWins()
    {
        // Four candidates within 0.1 % of CM: LZMA alone decodes fastest, ahead of the text filter + LZMA and PPMd,
        // although both of those are smaller.
        const string Ppmd8 = "ppmd(mem=256M,order=8)";
        using var t = new TempDir();
        var trials = new ScriptedTrials((s, sample) => s.ToString() switch
        {
            var x when x == TextCm => 100_000,
            Ppmd8 => 100_020,
            var x when x == "text+" + Lzma3 => 100_050,
            Lzma3 => 100_080,
            _ => 200_000,
        });
        var plan = await PlanFile(t, "book.txt", Book, Options(Tie(true), trials));
        var g = Assert.Single(plan.Groups);
        Assert.Equal(Lzma3, g.Spec!.ToString());
        double Decode(string spec) => PipelineSpec.Parse(spec, MethodRegistry.Default).EstimateDecodeSeconds(g.Size);
        Assert.True(Decode(Lzma3) < Decode("text+" + Lzma3) && Decode("text+" + Lzma3) < Decode(Ppmd8));
        Assert.Contains($"  tie: chose {Lzma3} (decodes faster) over {TextCm} ({0.08:0.00} % smaller)", g.Explain);
    }

    [Fact]
    public async Task WhenEveryTrialFailsTheFirstCandidateIsUsed()
    {
        // Every trial of the first rung fails: no search can start from a score, so the first candidate is taken.
        using var t = new TempDir();
        var trials = new ScriptedTrials((s, sample) => -1);
        var plan = await PlanFile(t, "book.txt", Book, Options(Profile.Ultra with { Rungs = [64 << 10] }, trials));
        var g = Assert.Single(plan.Groups);
        Assert.Equal(trials.Calls[0].Specs[0], g.Spec!.ToString());
        Assert.Contains(g.Explain, l => l.StartsWith("  all trials failed; using ", StringComparison.Ordinal));
        Assert.Single(trials.Calls);
    }

    // ------------------------------------------------------------------ search steps screened on the smaller sample

    private const int Small = 16 << 10, Full = 64 << 10;

    private static Profile Screened(int evals = 100, bool on = true) => Profile.Ultra with
    {
        Name = "screen", Rungs = [Small, Full], SearchEvals = evals, RaceTopTwo = false, ScreenSearch = on, PreferFasterDecoder = false,
    };

    /// <summary>
    /// Checks every call of a screened climb against the rule: each step tries all its neighbours on the smaller
    /// sample, promotes the best max(2, ⌈n/η⌉) of those within +0.5 % of the incumbent there, and moves only to a
    /// promoted neighbour that is more than 0.01 % better on the full sample. Returns the climb's steps.
    /// </summary>
    /// <summary>A step of the search: its call on the smaller sample (none when the step was not screened) and its call
    /// on the full sample (none when no neighbour was promising).</summary>
    private sealed record Step(ScriptedTrials.Call? Screen, ScriptedTrials.Call? Full)
    {
        public int Neighbours => (Screen ?? Full)!.Specs.Length;
    }

    /// <summary>
    /// Checks every call of a screened search against the rule. A step tries all its n neighbours on the smaller sample
    /// first when that can cut its work, promotes the best max(2, ⌈n/η⌉) of those within +0.5 % of the incumbent there
    /// (a failed trial ranks last), and moves only to the neighbour that is best on the full sample if it is more than
    /// 0.01 % better than the incumbent. A step that screening cannot shorten goes straight to the full sample, and
    /// after it moves the search is no longer screened. Returns the steps and the pipeline the search ends with.
    /// </summary>
    private static (List<Step> Steps, string Final) AssertScreenedClimb(ScriptedTrials trials, string start, int small = Small,
        int full = Full, int rungs = 2, int eta = 3)
    {
        var calls = trials.Calls;
        static long Rank(long size) => size < 0 ? long.MaxValue : size;
        long Size(ScriptedTrials.Call c, string spec) => c.Sizes[Array.IndexOf(c.Specs, spec)];
        Assert.Equal(full, calls[rungs - 1].SampleLength);
        string incumbent = start;
        long smallScore = Size(calls.Take(rungs).Single(c => c.SampleLength == small), start), fullScore = Size(calls[rungs - 1], start);
        bool screening = true;
        var steps = new List<Step>();
        for (int i = rungs; i < calls.Count; i++)
        {
            var call = calls[i];
            int n = call.Specs.Length, keep = Math.Max(2, (int)Math.Ceiling(n / (double)eta));
            Assert.DoesNotContain(incumbent, call.Specs);
            ScriptedTrials.Call? screen = null, tried = call;
            if (screening && Planner.ScreeningPays(n, keep, small, full))
            {
                Assert.Equal(small, call.SampleLength);
                screen = call;
                var expected = call.Specs.Zip(call.Sizes).OrderBy(x => Rank(x.Second)).ThenBy(x => x.First, StringComparer.Ordinal)
                    .Take(keep).Where(x => x.Second >= 0 && x.Second <= smallScore * 1.005).Select(x => x.First).ToArray();
                if (expected.Length == 0)
                {
                    Assert.Equal(calls.Count - 1, i); // nothing promising: the search ends
                    steps.Add(new Step(screen, null));
                    break;
                }
                tried = calls[++i];
                Assert.Equal(expected, tried.Specs);
            }
            Assert.Equal(full, tried.SampleLength);
            steps.Add(new Step(screen, tried));
            var top = tried.Specs.Zip(tried.Sizes).OrderBy(x => Rank(x.Second)).ThenBy(x => x.First, StringComparer.Ordinal).First();
            if (top.Second < 0 || top.Second >= fullScore * (1 - 1e-4))
            {
                Assert.Equal(calls.Count - 1, i); // no better neighbour: the search ends
                break;
            }
            (incumbent, fullScore) = (top.First, top.Second);
            if (screen is not null) smallScore = Size(screen, top.First);
            else screening = false;
        }
        return (steps, incumbent);
    }

    [Fact]
    public async Task ScreeningTriesTheNeighboursOnTheSmallerSampleFirst()
    {
        // The LZMA landscape is the same on both samples: only the move to lc=4, then to lc=8, is within 0.5 %.
        using var t = new TempDir();
        var trials = new ScriptedTrials(LzmaLandscape);
        var plan = await PlanFile(t, "book.txt", Book, Options(Screened(), trials));
        var g = Assert.Single(plan.Groups);
        var (steps, final) = AssertScreenedClimb(trials, Lzma3);
        Assert.Equal([7, 6, 5], steps.Select(s => s.Screen!.Specs.Length)); // the same neighbours as without screening
        Assert.Equal([1, 1], steps.Where(s => s.Full is not null).Select(s => s.Full!.Specs.Length));
        Assert.Equal(Lzma3.Replace("lc=3", "lc=8"), g.Spec!.ToString());
        Assert.Equal(final, g.Spec.ToString());
        Assert.Equal(2, g.Explain.Count(l => l.Contains("search:")));
        Assert.Contains($"  screening: 18 neighbour(s) on {Small:N0} B, 2 promoted to {Full:N0} B", g.Explain);
    }

    [Fact]
    public async Task ScreeningPromotesTheBestMaxOfTwoAndCeilNOverEta()
    {
        // On the smaller sample every LZMA variant is within 0.3 % of every other, so the cap decides how many are
        // promoted; on the full sample the LZMA landscape decides which move is taken.
        using var t = new TempDir();
        var trials = new ScriptedTrials((s, sample) => sample.Length == Small
            ? s.Codec.Method.Name == "lzma" ? 100_000 + (long)(ScriptedTrials.StableHash(s.ToString()) % 300) : 200_000
            : LzmaLandscape(s, sample));
        var plan = await PlanFile(t, "book.txt", Book, Options(Screened(), trials));
        var (steps, final) = AssertScreenedClimb(trials, Lzma3);
        // lc=4 is among the first step's three promoted neighbours and wins on the full sample; the second step's two do
        // not include lc=8, so the search ends at lc=4.
        Assert.Equal(2, steps.Count);
        Assert.Equal(Lzma3.Replace("lc=3", "lc=4"), final);
        Assert.Contains(steps, s => s.Full is { Specs.Length: > 1 });
        Assert.All(steps.Where(s => s.Screen is not null && s.Full is not null), s =>
            Assert.Equal(Math.Max(2, (int)Math.Ceiling(s.Screen!.Specs.Length / 3.0)), s.Full!.Specs.Length));
        Assert.Equal(final, Assert.Single(plan.Groups).Spec!.ToString());
    }

    [Fact]
    public async Task ScreeningComparesWithTheIncumbentOfEachStep()
    {
        // After the move to lc=4 (4 800 on both samples), lc=8 scores 4 850 on the smaller sample: within 0.5 % of the
        // starting point (4 900) but 1 % worse than the new incumbent, so it is not promoted although the full sample
        // would have taken it.
        using var t = new TempDir();
        string lc8 = Lzma3.Replace("lc=3", "lc=8");
        var trials = new ScriptedTrials((s, sample) => s.ToString() == lc8 && sample.Length == Small ? 4850 : LzmaLandscape(s, sample));
        var plan = await PlanFile(t, "book.txt", Book, Options(Screened(), trials));
        var (steps, final) = AssertScreenedClimb(trials, Lzma3);
        Assert.Equal([1], steps.Where(s => s.Full is not null).Select(s => s.Full!.Specs.Length));
        Assert.Contains(lc8, steps[1].Screen!.Specs);
        Assert.Equal(Lzma3.Replace("lc=3", "lc=4"), Assert.Single(plan.Groups).Spec!.ToString());
        Assert.Equal(final, Assert.Single(plan.Groups).Spec!.ToString());
    }

    [Theory]
    [InlineData(5_000_000)] // worse on the full sample
    [InlineData(4_899_800)] // better by 0.004 % only
    public async Task ScreeningNeverAcceptsAMoveThatIsNotBetterOnTheFullSample(long lc4OnTheFullSample)
    {
        // lc=4 looks 18 % better on the smaller sample and is promoted, but the full sample decides.
        using var t = new TempDir();
        string lc4 = Lzma3.Replace("lc=3", "lc=4");
        var trials = new ScriptedTrials((s, sample) => s.ToString() != lc4 ? LzmaLandscape(s, sample) * 1000
            : sample.Length == Small ? 4_000_000 : lc4OnTheFullSample);
        var plan = await PlanFile(t, "book.txt", Book, Options(Screened(), trials));
        var g = Assert.Single(plan.Groups);
        var (steps, final) = AssertScreenedClimb(trials, Lzma3);
        Assert.Equal([lc4], Assert.Single(steps).Full!.Specs);
        Assert.Equal(Lzma3, g.Spec!.ToString());
        Assert.Equal(Lzma3, final);
        Assert.DoesNotContain(g.Explain, l => l.Contains("search:"));
    }

    [Theory]
    [InlineData(100, new[] { 7, 6, 5 })]
    [InlineData(13, new[] { 7, 6 })]
    [InlineData(9, new[] { 7, 2 })] // the second step has 2 neighbours: it goes straight to the full sample
    [InlineData(3, new[] { 3 })]
    public async Task ScreeningStaysWithinTheBudget(int evals, int[] rounds)
    {
        // The budget counts neighbours generated, as without screening: the same rounds are tried, and only the
        // promoted neighbours of a screened step are compressed on the full sample as well.
        using var t = new TempDir();
        var trials = new ScriptedTrials(LzmaLandscape);
        var plan = await PlanFile(t, "book.txt", Book, Options(Screened(evals), trials));
        var (steps, final) = AssertScreenedClimb(trials, Lzma3);
        Assert.Equal(rounds, steps.Select(s => s.Neighbours));
        Assert.True(steps.Sum(s => s.Neighbours) <= evals);
        Assert.All(steps.Where(s => s.Screen is not null && s.Full is not null),
            s => Assert.Subset(s.Screen!.Specs.ToHashSet(), s.Full!.Specs.ToHashSet()));
        var tried = steps.SelectMany(s => (s.Screen ?? s.Full)!.Specs).ToList();
        Assert.Equal(tried.Count, tried.Distinct().Count()); // no neighbour is tried twice
        Assert.Equal(final, Assert.Single(plan.Groups).Spec!.ToString());
    }

    /// <summary>Scripted sizes, and a weak reference to every sample it is given: at the search's first call it collects
    /// garbage and notes which samples are still alive.</summary>
    private sealed class SampleWatcher(Func<PipelineSpec, byte[], long> size, int rungs) : ITrialRunner
    {
        private readonly Dictionary<int, WeakReference<byte[]>> _samples = [];
        private int _calls;

        public HashSet<int> AliveDuringSearch { get; } = [];

        public Task<long[]> RunAsync(IReadOnlyList<PipelineSpec> specs, byte[] sample, CancellationToken ct)
        {
            var sizes = specs.Select(s => size(s, sample)).ToArray();
            lock (_samples)
            {
                _samples.TryAdd(sample.Length, new WeakReference<byte[]>(sample));
                if (++_calls == rungs + 1)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    foreach (var (length, w) in _samples)
                        if (w.TryGetTarget(out _)) AliveDuringSearch.Add(length);
                }
            }
            return Task.FromResult(sizes);
        }
    }

    [Theory]
    [InlineData(true, new[] { Small, Full })] // the search screens on 16 KiB: the 8 KiB sample is not needed
    [InlineData(false, new[] { Full })]       // without screening the search needs only the last sample
    public async Task OnlyTheSamplesTheSearchUsesOutliveTheRungs(bool screen, int[] alive)
    {
        using var t = new TempDir();
        var watcher = new SampleWatcher(LzmaLandscape, rungs: 3);
        var profile = Screened(on: screen) with { Rungs = [8 << 10, Small, Full] };
        var plan = await PlanFile(t, "book.txt", Book, Options(profile, watcher));
        Assert.Equal(Lzma3.Replace("lc=3", "lc=8"), Assert.Single(plan.Groups).Spec!.ToString());
        Assert.Equal(alive, watcher.AliveDuringSearch.Order());
    }

    [Theory]
    [InlineData(2, Small, false)]    // 2 neighbours: each promising one would be compressed on both samples
    [InlineData(3, Small, true)]     // 3 on a quarter-size sample: 3/4 + 2 full trials, less than 3
    [InlineData(3, 32 << 10, false)] // 3 on a half-size sample: 3/2 + 2 full trials, more than 3
    public async Task AStepIsScreenedOnlyWhenThatCutsItsWork(int evals, int small, bool screened)
    {
        using var t = new TempDir();
        var trials = new ScriptedTrials(LzmaLandscape);
        var plan = await PlanFile(t, "book.txt", Book, Options(Screened(evals) with { Rungs = [small, Full] }, trials));
        var g = Assert.Single(plan.Groups);
        var first = trials.Calls[2];
        Assert.Equal(evals, first.Specs.Length);
        Assert.Equal(screened ? small : Full, first.SampleLength);
        var (steps, final) = AssertScreenedClimb(trials, Lzma3, small: small);
        Assert.Equal(screened, steps[0].Screen is not null);
        Assert.Equal(final, g.Spec!.ToString());
        Assert.Equal(screened, g.Explain.Any(l => l.Contains("screening:")));
    }

    [Fact]
    public async Task TheFullSampleChoosesAmongThePromotedNeighbours()
    {
        // On the smaller sample pb=1 looks best and lc=4 second; on the full sample lc=4 is the better one. Both are
        // promoted, and the search moves to lc=4.
        using var t = new TempDir();
        string pb1 = Lzma3.Replace("pb=0", "pb=1"), lc4 = Lzma3.Replace("lc=3", "lc=4");
        var trials = new ScriptedTrials((s, sample) => (s.ToString(), sample.Length) switch
        {
            (var x, Small) when x == pb1 => 4850,
            (var x, Small) when x == lc4 => 4880,
            (var x, _) when x == pb1 => 4890,
            _ => LzmaLandscape(s, sample),
        });
        var plan = await PlanFile(t, "book.txt", Book, Options(Screened(), trials));
        var g = Assert.Single(plan.Groups);
        var (steps, final) = AssertScreenedClimb(trials, Lzma3);
        Assert.Equal([pb1, lc4], steps[0].Full!.Specs);
        Assert.StartsWith($"  search: {lc4} improves", g.Explain.First(l => l.Contains("search:")));
        Assert.Equal(Lzma3.Replace("lc=3", "lc=8"), g.Spec!.ToString());
        Assert.Equal(final, g.Spec.ToString());
    }

    [Fact]
    public async Task FailedScreeningTrialsAreNeverPromoted()
    {
        // fb=128 and mf=0 fail on the smaller sample; every other LZMA variant scores the same there. The three
        // promotion places go to working neighbours, and no failure is compressed again on the full sample.
        using var t = new TempDir();
        static bool Fails(PipelineSpec s) => s.Codec.Params.Get("fb", 273) != 273 || s.Codec.Params.Get("mf", 1) != 1;
        var trials = new ScriptedTrials((s, sample) => sample.Length == Small
            ? s.Codec.Method.Name != "lzma" ? 200_000 : Fails(s) ? -1 : 4900
            : LzmaLandscape(s, sample));
        var plan = await PlanFile(t, "book.txt", Book, Options(Screened(evals: 7), trials));
        var (steps, final) = AssertScreenedClimb(trials, Lzma3);
        var step = Assert.Single(steps);
        Assert.Equal(7, step.Screen!.Specs.Length);
        Assert.Equal(2, step.Screen.Sizes.Count(size => size < 0));
        Assert.Equal(3, step.Full!.Specs.Length);
        Assert.DoesNotContain(step.Full.Specs, s => Fails(PipelineSpec.Parse(s, MethodRegistry.Default)));
        Assert.Equal(final, Assert.Single(plan.Groups).Spec!.ToString());
    }

    [Fact]
    public async Task AtLeastTwoNeighboursArePromoted()
    {
        // Three neighbours: ⌈3/3⌉ is 1, but two are promoted when both are within 0.5 % on the smaller sample.
        using var t = new TempDir();
        var trials = new ScriptedTrials((s, sample) => sample.Length == Small
            ? s.Codec.Method.Name == "lzma" ? 100_000 + (long)(ScriptedTrials.StableHash(s.ToString()) % 300) : 200_000
            : LzmaLandscape(s, sample));
        var plan = await PlanFile(t, "book.txt", Book, Options(Screened(evals: 3), trials));
        var (steps, final) = AssertScreenedClimb(trials, Lzma3);
        var step = Assert.Single(steps);
        Assert.Equal(3, step.Screen!.Specs.Length);
        Assert.Equal(2, step.Full!.Specs.Length);
        Assert.Equal(final, Assert.Single(plan.Groups).Spec!.ToString());
    }

    [Fact]
    public async Task WhenEveryTrialOfALaterRungFailsTheSearchUsesTheLastRungThatWorked()
    {
        // Every trial on 64 KiB fails. The search runs on the 16 KiB sample, scored against that rung, and still finds
        // lc=8; it is not screened, since no earlier sample is left.
        using var t = new TempDir();
        var trials = new ScriptedTrials((s, sample) => sample.Length == Small ? LzmaLandscape(s, sample) : -1);
        var plan = await PlanFile(t, "book.txt", Book, Options(Screened(), trials));
        var g = Assert.Single(plan.Groups);
        Assert.Equal([Small, Full], trials.Calls.Take(2).Select(c => c.SampleLength));
        var search = trials.Calls.Skip(2).ToList();
        Assert.NotEmpty(search);
        Assert.All(search, c => Assert.Equal(Small, c.SampleLength));
        Assert.Equal(Lzma3.Replace("lc=3", "lc=8"), g.Spec!.ToString());
        Assert.Contains($"  rung {Full:N0} B: ", g.Explain);
        Assert.DoesNotContain(g.Explain, l => l.Contains("screening:"));
    }

    [Fact]
    public void TheBuiltInProfilesScreenUltraAndInsaneAndKeepTheOtherSwitchesOff()
    {
        Assert.All(Profile.All, p =>
        {
            Assert.Equal(p.Level is ProfileLevel.Ultra or ProfileLevel.Insane, p.ScreenSearch);
            Assert.False(p.PreferFasterDecoder, p.Name);
            Assert.False(p.SimilarityOrder, p.Name);
        });
    }

    [Theory]
    [InlineData(false, new[] { Small, Full })]          // the switch is off
    [InlineData(true, new[] { 512 << 10, 1 << 20 })]    // the first rung already covers the 400 KiB group
    [InlineData(true, new[] { 256 << 10, 384 << 10 })]  // the earlier sample is more than half the last one
    public async Task WithoutASmallEnoughSampleTheClimbIsNotScreened(bool on, int[] rungs)
    {
        // Screening the neighbours on a sample more than half the size of the last one, and the promoted ones on the
        // last as well, would cost about as much as trying them all on the last sample.
        using var t = new TempDir();
        var trials = new ScriptedTrials(LzmaLandscape);
        var plan = await PlanFile(t, "book.txt", Book, Options(Screened(on: on) with { Rungs = [.. rungs.Select(r => (long)r)] }, trials));
        var g = Assert.Single(plan.Groups);
        int halving = trials.Calls.TakeWhile(c => c.Specs.Length == 10 || c.Specs.Length == 4).Count();
        Assert.Equal(rungs[0] >= Book.Length ? 1 : 2, halving);
        int full = trials.Calls[halving - 1].SampleLength;
        var climb = trials.Calls.Skip(halving).ToList();
        Assert.Equal([7, 6, 5], climb.Select(c => c.Specs.Length));
        Assert.All(climb, c => Assert.Equal(full, c.SampleLength));
        Assert.DoesNotContain(g.Explain, l => l.Contains("screening:"));
        Assert.Equal(Lzma3.Replace("lc=3", "lc=8"), g.Spec!.ToString());
    }

    [Theory]
    [InlineData(new[] { 8 << 10, Small, Full }, Small)]  // 8 and 16 KiB both qualify: the larger is used
    [InlineData(new[] { Small, 48 << 10, Full }, Small)] // 48 KiB is more than half of 64
    [InlineData(new[] { 32 << 10, Full }, 32 << 10)]     // exactly half qualifies
    public async Task ScreeningUsesTheLargestEarlierSampleAtMostHalfTheLast(int[] rungs, int small)
    {
        using var t = new TempDir();
        var trials = new ScriptedTrials(LzmaLandscape);
        var plan = await PlanFile(t, "book.txt", Book, Options(Screened() with { Rungs = [.. rungs.Select(r => (long)r)] }, trials));
        var g = Assert.Single(plan.Groups);
        Assert.Equal(rungs, trials.Calls.Take(rungs.Length).Select(c => c.SampleLength));
        var climb = trials.Calls.Skip(rungs.Length).ToList();
        Assert.Equal([small, Full, small, Full, small], climb.Select(c => c.SampleLength));
        Assert.Equal([7, 1, 6, 1, 5], climb.Select(c => c.Specs.Length));
        var (_, final) = AssertScreenedClimb(trials, Lzma3, small: small, rungs: rungs.Length);
        Assert.Contains($"  screening: 18 neighbour(s) on {small:N0} B, 2 promoted to {Full:N0} B", g.Explain);
        Assert.Equal(Lzma3.Replace("lc=3", "lc=8"), g.Spec!.ToString());
        Assert.Equal(final, g.Spec.ToString());
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
        // four text files: enough for the similarity order
        File.WriteAllBytes(Path.Combine(dir, "sub", "draft.txt"), Synthetic.Text(30 << 10, seed: 3));
        File.WriteAllBytes(Path.Combine(dir, "odd.txt"), Synthetic.HostileText(20 << 10));
        File.WriteAllBytes(Path.Combine(dir, "records.bin"), Synthetic.Records(20_000));
        File.WriteAllBytes(Path.Combine(dir, "code.bin"), Synthetic.X86(120 << 10));
        File.WriteAllBytes(Path.Combine(dir, "tone.wav"), Synthetic.Pcm(30_000, header: true));
        File.WriteAllBytes(Path.Combine(dir, "image.bmp"), Synthetic.Bmp());
        File.WriteAllBytes(Path.Combine(dir, "noise.bin"), noise);
        return dir;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanDoesNotDependOnTheThreadCount(bool search)
    {
        // Real trials (local runner): one trial at a time versus eight at once, with max, and with a short screened
        // search over two small rungs (no CM, so it stays quick).
        using var t = new TempDir();
        var dir = MixedFolder(t);
        var profile = search
            ? Profile.Ultra with
            {
                Name = "screened", Rungs = [16 << 10, 64 << 10], MaxCandidates = 4, SearchEvals = 6, AllowCm = false,
                SimilarityOrder = true, PreferFasterDecoder = true, ScreenSearch = true,
            }
            : Profile.Max with { SimilarityOrder = true };
        async Task<string> Plan(int threads) =>
            Fingerprint(await new Planner(Options(profile, null, threads: threads)).BuildAsync(Scanner.Scan([dir]), Ct));
        string one = await Plan(1);
        Assert.Equal(one, await Plan(8));
        Assert.True(one.Split('\n').Count(l => l.StartsWith('G')) >= 5, one);
        Assert.Contains("order: similarity (SimHash)", one);
        if (search) Assert.Contains("screening:", one);
    }

    [Fact]
    public async Task PlanDoesNotDependOnTheOrderGroupsFinishIn()
    {
        // Scripted trials with a hill-climb, delayed by varying amounts so that concurrently planned groups interleave
        // differently in every run. The LZMA landscape guarantees climbing steps on any sample; the noise, smaller than
        // a step, re-ranks the other candidates on every rung.
        using var t = new TempDir();
        var dir = MixedFolder(t);
        var profile = Profile.Ultra with
        {
            Name = "jitter", Rungs = [16 << 10, 64 << 10], SimilarityOrder = true, PreferFasterDecoder = true, ScreenSearch = true,
        };
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
        Assert.Contains("screening:", first);
        Assert.Contains("order: similarity (SimHash)", first);
    }
}
