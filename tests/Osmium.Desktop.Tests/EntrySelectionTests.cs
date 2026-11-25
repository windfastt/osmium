using Osmium.Core.Planning;
using Osmium.Desktop.Jobs;
using Osmium.Desktop.Settings;

namespace Osmium.Desktop.Tests;

/// <summary>Extracting a selection of an archive's entries: which entries a list of paths selects, where they go below a
/// base folder, and extract jobs that use both.</summary>
public sealed class EntrySelectionTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "osm-selection-" + Guid.NewGuid().ToString("N")[..8]);

    public EntrySelectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void AnExactFileSelectsOnlyThatFile()
    {
        var take = EntrySelection.Build(["docs/a.txt"]);
        Assert.True(take("docs/a.txt"));
        Assert.False(take("docs/b.txt"));
        Assert.False(take("docs"));
        Assert.False(take("docs/a.txt.bak"));
    }

    [Fact]
    public void AFolderSelectsEverythingBelowIt()
    {
        var take = EntrySelection.Build(["docs/img"]);
        Assert.True(take("docs/img"));
        Assert.True(take("docs/img/a.png"));
        Assert.True(take("docs/img/deep/er/b.png"));
        Assert.False(take("docs"));
        Assert.False(take("docs/readme.txt"));
    }

    [Theory]
    [InlineData("doc", "docs/x")]
    [InlineData("doc", "docs")]
    [InlineData("docs", "docs2/a.txt")]
    [InlineData("docs", "docsx")]
    [InlineData("docs", "my/docs/a.txt")]
    [InlineData("Docs", "docs/a.txt")] // compared as stored
    public void APrefixThatIsNotAFolderSelectsNothing(string include, string path) =>
        Assert.False(EntrySelection.Build([include])(path));

    [Fact]
    public void ATrailingSlashIsIgnored()
    {
        var take = EntrySelection.Build(["docs/"]);
        Assert.True(take("docs"));
        Assert.True(take("docs/a.txt"));
        Assert.False(take("docs2/a.txt"));
    }

    [Fact]
    public void SeveralPathsSelectTheirUnion()
    {
        var take = EntrySelection.Build(["a.txt", "img", "deep/er"]);
        Assert.True(take("a.txt"));
        Assert.True(take("img/x.png"));
        Assert.True(take("deep/er/y"));
        Assert.False(take("deep/z"));
        Assert.False(take("b.txt"));
    }

    [Fact]
    public void NoListOrAnEmptyListOrTheRootSelectsEverything()
    {
        foreach (var take in new[] { EntrySelection.Build(null), EntrySelection.Build([]), EntrySelection.Build([""]), EntrySelection.Build(["/"]) })
        {
            Assert.True(take("a.txt"));
            Assert.True(take("docs/img/a.png"));
        }
    }

    [Theory]
    [InlineData("docs/img/a.png", "docs", "img/a.png")]
    [InlineData("docs/a.txt", "docs", "a.txt")]
    [InlineData("docs/a.txt", "docs/", "a.txt")]
    [InlineData("docs/img/a.png", "docs/img", "a.png")]
    [InlineData("docs", "docs", null)]        // the base itself: its contents go to the output root
    [InlineData("docs2/a.txt", "docs", null)]  // outside, though the name starts the same
    [InlineData("docsa.txt", "docs", null)]
    [InlineData("other/a.txt", "docs", null)]
    [InlineData("a.txt", "docs", null)]
    [InlineData("Docs/a.txt", "docs", null)]
    [InlineData("docs/a.txt", null, "docs/a.txt")] // no base: unchanged
    [InlineData("docs/a.txt", "", "docs/a.txt")]
    [InlineData("docs/a.txt", "/", "docs/a.txt")]
    public void MapPathKeepsTheStructureBelowTheBase(string path, string? includeBase, string? expected) =>
        Assert.Equal(expected, EntrySelection.MapPath(path, includeBase));

    // ------------------------------------------------------------------ extract jobs

    private string Write(string rel, string text)
    {
        var path = Path.GetFullPath(Path.Combine(_root, rel));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>An archive of in/notes.txt, in/docs/readme.txt, in/docs/img/a.png and in/docs/img/b.png.</summary>
    private async Task<string> Archive()
    {
        Write("in/notes.txt", "notes");
        Write("in/docs/readme.txt", "read me");
        Write("in/docs/img/a.png", "picture a");
        Write("in/docs/img/b.png", "picture b");
        var scan = Scanner.Scan([Path.Combine(_root, "in")]);
        var plan = await new Planner(new PlanOptions { Profile = Profile.ByName("store"), MemoryBudget = 1L << 30 }).BuildAsync(scan, Ct);
        return (await ArchiveBuilder.BuildAsync(plan, new BuildOptions { OutputPath = Path.Combine(_root, "a.osa"), MemoryBudget = 1L << 30 }, Ct)).Path;
    }

    private async Task<JobSnapshot> Extract(string archive, string output, List<string>? include, string? includeBase)
    {
        var s = new OsmiumSettings();
        s.Performance.Threads = 2;
        using var jm = new JobManager(() => s, () => new RuleSet());
        var done = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        jm.JobFinished += j => done.TrySetResult(j);
        jm.Enqueue(new JobRequest { Kind = JobKind.Extract, Inputs = [archive], Output = output, Include = include, IncludeBase = includeBase });
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
    }

    private static List<string> FilesUnder(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task AnExtractJobWithoutASelectionExtractsEverything()
    {
        string archive = await Archive();
        var job = await Extract(archive, Path.Combine(_root, "out"), null, null);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(["in/docs/img/a.png", "in/docs/img/b.png", "in/docs/readme.txt", "in/notes.txt"], FilesUnder(Path.Combine(_root, "out")));
        Assert.Equal(0, job.LeftOut);
        Assert.Empty(job.LeftOutDetails);
    }

    [Fact]
    public async Task AnExtractJobTakesTheIncludedEntriesOnly()
    {
        string archive = await Archive();
        var job = await Extract(archive, Path.Combine(_root, "out"), ["in/notes.txt", "in/docs/img"], null);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(["in/docs/img/a.png", "in/docs/img/b.png", "in/notes.txt"], FilesUnder(Path.Combine(_root, "out")));
    }

    [Fact]
    public async Task AnExtractJobKeepsTheStructureBelowItsBase()
    {
        string archive = await Archive();
        var job = await Extract(archive, Path.Combine(_root, "out"), ["in/docs/img", "in/docs/readme.txt"], "in/docs");
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(["img/a.png", "img/b.png", "readme.txt"], FilesUnder(Path.Combine(_root, "out")));
        Assert.Equal("picture a", File.ReadAllText(Path.Combine(_root, "out", "img", "a.png")));
    }

    [Fact]
    public async Task ABaseAloneExtractsThatFoldersContents()
    {
        string archive = await Archive();
        var job = await Extract(archive, Path.Combine(_root, "out"), null, "in/docs/img");
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(["a.png", "b.png"], FilesUnder(Path.Combine(_root, "out")));
    }
}
