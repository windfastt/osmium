using Osmium.Core.Analysis;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests;

public class CodeGroupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task X64And32BitCodeArePlannedInSeparateGroups()
    {
        // x86v2 applies 64-bit rules (RIP-relative operands), so 32-bit code must not share its group.
        using var t = new TempDir();
        File.WriteAllBytes(t.Sub("a64.exe"), MethodFamilyTests.Pe(0x8664, seed: 4));
        File.WriteAllBytes(t.Sub("b32.exe"), MethodFamilyTests.Pe(0x014C, seed: 5));
        var plan = await new Planner(PlannerFixtures.Options(Profile.Balanced, null))
            .BuildAsync(Scanner.Scan([t.Sub("a64.exe"), t.Sub("b32.exe")], ct: Ct), Ct);
        Assert.Equal(2, plan.Groups.Count);
        Assert.All(plan.Groups, g => Assert.Equal(CompatClass.CodeX86, g.Class));
        var byArch = plan.Groups.ToDictionary(g => g.Rep!.Arch);
        Assert.Equal("x86v2", byArch[CpuArch.X64].Spec!.Filters.First().Method.Name);
        Assert.Equal("x86", byArch[CpuArch.X86].Spec!.Filters.First().Method.Name);
        Assert.Contains("|X64|", byArch[CpuArch.X64].Key);
        Assert.Contains("|X86|", byArch[CpuArch.X86].Key);
    }
}
