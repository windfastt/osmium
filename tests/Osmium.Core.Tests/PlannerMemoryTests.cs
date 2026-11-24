using Osmium.Core.Pipeline;
using Osmium.Core.Planning;
using static Osmium.Core.Tests.PlannerFixtures;

namespace Osmium.Core.Tests;

/// <summary>The hill-climb checks memory against the block it will compress, as the first choice does, not the sample.</summary>
public class PlannerMemoryTests
{
    private static readonly MethodRegistry Reg = MethodRegistry.Default;

    /// <summary>zstd wins, and its long-distance-matching neighbour wins by far.</summary>
    private static long ZstdLandscape(PipelineSpec s, byte[] sample) =>
        s.Codec.Method.Name != "zstd" || s.Filters.Any() ? 10_000 : s.Codec.Params.Get("ldm", 0) != 0 ? 100 : 200;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HillClimbSkipsNeighboursThatFitTheSampleButNotTheBlock(bool tight)
    {
        var data = Synthetic.Text(1536 << 10);
        const int sampleLength = 64 << 10;
        var start = PipelineSpec.Parse("zstd(level=19)", Reg);
        var ldm = PipelineSpec.Parse("zstd(level=19,ldm=1)", Reg);
        // ldm=1 needs a little more memory than the starting point, and how much grows with the input:
        // the tight budget lies between what the neighbour needs for the block and what it needs for the sample.
        long budget = tight ? (start.EncodeMemory(data.Length) + ldm.EncodeMemory(data.Length)) / 2 : 2L << 30;
        Assert.True(start.EncodeMemory(data.Length) <= budget && start.DecodeMemory(data.Length) <= budget);
        Assert.True(ldm.EncodeMemory(sampleLength) <= budget);
        Assert.Equal(tight, ldm.EncodeMemory(data.Length) > budget);

        using var t = new TempDir();
        var trials = new ScriptedTrials(ZstdLandscape);
        var profile = Profile.Max with { Name = "memory", Rungs = [sampleLength], MaxCandidates = 14, SearchEvals = 24 };
        var plan = await PlanFile(t, "book.txt", data, Options(profile, trials, memory: budget));
        var g = Assert.Single(plan.Groups);
        long block = Assert.Single(plan.Blocks).RawSize;
        Assert.Equal(data.Length, block);
        Assert.Equal(sampleLength, trials.Calls[0].SampleLength);

        // With room to spare the climb takes ldm=1 as before; on the tight budget it must not, because the block
        // could then not be compressed within the budget.
        Assert.Equal(tight ? 0 : 1, g.Spec!.Codec.Params.Get("ldm", -1));
        Assert.Equal("zstd", g.Spec.Codec.Method.Name);
        Assert.True(g.Spec.EncodeMemory(block) <= budget && g.Spec.DecodeMemory(block) <= budget);
        var climbed = trials.Calls.Skip(1).SelectMany(c => c.Specs).Select(s => PipelineSpec.Parse(s, Reg)).ToList();
        Assert.NotEmpty(climbed);
        Assert.All(climbed, s => Assert.True(s.EncodeMemory(block) <= budget, $"{s} was tried but needs {s.EncodeMemory(block):N0} bytes"));
    }
}
