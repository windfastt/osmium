using Osmium.Core.Archives;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests;

/// <summary>.osa archives through <see cref="OsaArchiveSource"/>: what it says about the archive and how its extraction
/// results map onto the common interface.</summary>
public class OsaSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> Build(TempDir t)
    {
        string input = Path.Combine(t.Path, "in");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "a.txt"), string.Concat(Enumerable.Repeat("source text\n", 300)));
        File.WriteAllText(Path.Combine(input, "b.txt"), "second file");
        var plan = await new Planner(new PlanOptions { Profile = Profile.ByName("fast"), MemoryBudget = 1L << 30 }).BuildAsync(Scanner.Scan([input], ct: Ct), Ct);
        return (await ArchiveBuilder.BuildAsync(plan, new BuildOptions { OutputPath = Path.Combine(t.Path, "a.osa"), KdfIterations = 1000, MemoryBudget = 1L << 30 }, Ct)).Path;
    }

    [Fact]
    public async Task ASecondPassOverEarlierOutputIsRefusedBeforeAnythingIsWritten()
    {
        using var t = new TempDir();
        using var s = ArchiveSources.Open(await Build(t), ct: Ct);
        string outDir = Path.Combine(t.Path, "out");
        Assert.Throws<NotSupportedException>(() => s.Extract(outDir,
            new ArchiveExtractRequest { WrittenEarlier = [Path.Combine(outDir, "in", "a.txt")] }, null, Ct));
        Assert.False(Directory.Exists(outDir));
        // An empty set is no earlier pass.
        Assert.Equal(2, s.Extract(outDir, new ArchiveExtractRequest { WrittenEarlier = [] }, null, Ct).FilesOk);
    }
}
