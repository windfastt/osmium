using System.Buffers.Binary;
using Osmium.Core.Analysis;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests;

/// <summary>
/// cm2 and x86v2 are newer members of the cm and x86 families: rules and settings that name the family reach them, and
/// x64 and 32-bit code never share a group (their branch converters differ).
/// </summary>
public class MethodFamilyTests
{
    private static readonly MethodRegistry Reg = MethodRegistry.Default;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PipelineSpec P(string s) => PipelineSpec.Parse(s, Reg);

    private static EffectiveRules Resolve(string json) =>
        RuleSet.Combine(RuleSet.BuiltIn(), RuleSet.Parse(json)).Resolve("dir/app.exe", 100_000, null, Reg);

    [Fact]
    public void ForbiddingAFamilyForbidsItsNewerMembers()
    {
        var e = Resolve("""{ "rules": [ { "id": "no", "match": { "globs": ["**"] }, "actions": { "forbidCodecs": ["cm"], "forbidFilters": ["x86"] } } ] }""");
        Assert.False(e.Allows(P("cm2")));
        Assert.False(e.Allows(P("cm")));
        Assert.False(e.Allows(P("x86v2+lzma")));
        Assert.False(e.Allows(P("x86+lzma")));
        Assert.True(e.Allows(P("arm64+lzma")));
        Assert.True(e.Allows(P("lzma")));

        // the newer name alone means only the newer method
        var only = Resolve("""{ "rules": [ { "id": "no", "match": { "globs": ["**"] }, "actions": { "forbidCodecs": ["cm2"], "forbidFilters": ["x86v2"] } } ] }""");
        Assert.False(only.Allows(P("cm2")));
        Assert.True(only.Allows(P("cm")));
        Assert.False(only.Allows(P("x86v2+lzma")));
        Assert.True(only.Allows(P("x86+lzma")));
    }

    [Fact]
    public void FamilyParametersReachTheNewerMethodAndItsOwnNameWins()
    {
        var e = Resolve("""{ "rules": [ { "id": "p", "match": { "globs": ["**"] }, "actions": { "params": { "cm.limit": "60", "cm.mem": "26", "cm2.lr": "8", "x86.calls": "0" } } } ] }""");
        var cm2 = e.Apply(P("x86v2+cm2(mem=20)"));
        Assert.Equal(60, cm2.Codec.Params.Get("limit", -1));
        Assert.Equal(8, cm2.Codec.Params.Get("lr", -1));
        Assert.Equal(24, cm2.Codec.Params.Get("mem", -1)); // clamped to cm2's range
        Assert.Equal(0, cm2.Filters.Single().Params.Get("calls", -1));
        var cm = e.Apply(P("cm(mem=20)"));
        Assert.Equal(60, cm.Codec.Params.Get("limit", -1));
        Assert.Equal(16, cm.Codec.Params.Get("lr", -1)); // cm2.lr is not for cm

        var both = Resolve("""{ "rules": [ { "id": "p", "match": { "globs": ["**"] }, "actions": { "params": { "cm.limit": "60", "cm2.limit": "127" } } } ] }""");
        Assert.Equal(127, both.Apply(P("cm2")).Codec.Params.Get("limit", -1));
        Assert.Equal(60, both.Apply(P("cm")).Codec.Params.Get("limit", -1));
    }

    [Fact]
    public async Task ForbiddingX86KeepsBothConvertersOutOfThePlan()
    {
        using var t = new TempDir();
        var rules = RuleSet.Combine(RuleSet.BuiltIn(), RuleSet.Parse(
            """{ "rules": [ { "id": "no-x86", "match": { "globs": ["**"] }, "actions": { "forbidFilters": ["x86"] } } ] }"""));
        var trials = new ScriptedTrials((s, _) => 1000 + (long)(ScriptedTrials.StableHash(s.ToString()) % 100));
        foreach (var profile in new[] { Profile.Balanced, Profile.Ultra })
        {
            var plan = await PlannerFixtures.PlanFile(t, "app.exe", Pe(0x8664, seed: 1), PlannerFixtures.Options(profile, trials) with { Rules = rules });
            var g = plan.Groups.Single();
            Assert.Equal(CompatClass.CodeX86, g.Class);
            Assert.DoesNotContain(g.Spec!.Filters, f => MethodFamily.Of(f.Method.Name) == "x86");
        }
        Assert.DoesNotContain(trials.Calls.SelectMany(c => c.Specs), s => s.StartsWith("x86", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForbiddingCmKeepsCm2OutOfThePlanAndCmParametersReachCm2()
    {
        using var t = new TempDir();
        // cm2 always looks best, so it is chosen whenever it is allowed
        var trials = new ScriptedTrials((s, _) => MethodIds.IsContextMixing(s.Codec.Method.Id) ? 100 : 1000);
        var noCm = RuleSet.Combine(RuleSet.BuiltIn(), RuleSet.Parse(
            """{ "rules": [ { "id": "no-cm", "match": { "globs": ["**"] }, "actions": { "forbidCodecs": ["cm"] } } ] }"""));
        var plan = await PlannerFixtures.PlanFile(t, "app.exe", Pe(0x8664, seed: 2), PlannerFixtures.Options(Profile.Ultra, trials) with { Rules = noCm });
        Assert.False(MethodIds.IsContextMixing(plan.Groups.Single().Spec!.Codec.Method.Id));
        Assert.DoesNotContain(trials.Calls.SelectMany(c => c.Specs), s => s.Contains("cm2(", StringComparison.Ordinal) || s.Contains("cm(", StringComparison.Ordinal));

        var limit = RuleSet.Combine(RuleSet.BuiltIn(), RuleSet.Parse(
            """{ "rules": [ { "id": "limit", "match": { "globs": ["**"] }, "actions": { "params": { "cm.limit": "60" } } } ] }"""));
        var tuned = await PlannerFixtures.PlanFile(t, "app2.exe", Pe(0x8664, seed: 3), PlannerFixtures.Options(Profile.Ultra, trials) with { Rules = limit });
        var spec = tuned.Groups.Single().Spec!;
        Assert.Equal("cm2", spec.Codec.Method.Name);
        Assert.Equal(60, spec.Codec.Params.Get("limit", -1));
    }

    /// <summary>A minimal native PE (no CLR directory) for <paramref name="machine"/> (0x8664 x64, 0x014C i386) with an
    /// x86-like body: calls to a few targets and low-entropy filler.</summary>
    internal static byte[] Pe(ushort machine, int seed)
    {
        var rnd = new Random(seed);
        var data = new byte[120_000];
        rnd.NextBytes(data);
        data[0] = (byte)'M'; data[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(data.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x84), machine);
        bool pe64 = machine == 0x8664;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x98), pe64 ? (ushort)0x20B : (ushort)0x10B);
        data.AsSpan(0x98 + (pe64 ? 112 : 96) + 14 * 8, 8).Clear();
        for (int i = 0x400; i < data.Length - 8; i += 8)
        {
            data[i] = 0xE8;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i + 1), 0x1000 * rnd.Next(16) - i - 5);
            data[i + 5] = 0x8B; data[i + 6] = 0x45; data[i + 7] = (byte)(rnd.Next(4) * 8);
        }
        return data;
    }
}
