using Osmium.Core.Archives;
using Osmium.Core.Container;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests;

/// <summary>.osa archives through <see cref="OsaArchiveSource"/>: what it says about the archive, how its extraction
/// results map onto the common interface, and the interface's rules on cancellation, progress and passwords.</summary>
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

    /// <summary>Reports collected on the calling thread, in order (Progress&lt;T&gt; would post them elsewhere).</summary>
    private sealed class Reports(Action<ExtractProgress>? also = null) : IProgress<ExtractProgress>
    {
        public List<ExtractProgress> All { get; } = [];
        public void Report(ExtractProgress value)
        {
            lock (All) All.Add(value);
            also?.Invoke(value);
        }
    }

    private static string Detail(IArchiveSource s, string label) => s.Details.Single(d => d.Label == label).Value;

    [Fact]
    public void DetailsShowTheArchivesOwnVersionAndItsSpareCopy()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(3, 2000);
        using (var s = ArchiveSources.Open(SpareArchives.Write(t.Sub("new.osa"), files, new OsaWriterOptions { SpareThreshold = 0 }), ct: Ct))
        {
            Assert.Equal(".osa 1.1", s.FormatName);
            Assert.Equal(".osa 1.1", Detail(s, "Format"));
            Assert.Equal("yes", Detail(s, "Spare copy of the file list"));
            Assert.Equal(["Format", "Spare copy of the file list", "Created by"], s.Details.Take(3).Select(d => d.Label));
        }
        using (var s = ArchiveSources.Open(SpareArchives.Write(t.Sub("small.osa"), files, new OsaWriterOptions { SpareThreshold = long.MaxValue }), ct: Ct))
        {
            Assert.Equal(".osa 1.1", Detail(s, "Format"));
            Assert.DoesNotContain(s.Details, d => d.Label == "Spare copy of the file list"); // small archives never have one
        }
        using (var s = ArchiveSources.Open(SpareArchives.Write(t.Sub("old.osa"), files, new OsaWriterOptions { HeaderMinor = 0, SpareThreshold = long.MaxValue }), ct: Ct))
        {
            Assert.Equal(".osa 1.0", s.FormatName);
            Assert.StartsWith("no", Detail(s, "Spare copy of the file list"));
        }
    }

    [Fact]
    public void WarningsOfTheReaderAreForwardedAndTestComparesTheSpareCopies()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(3, 2000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, new OsaWriterOptions { SpareThreshold = 0 });
        var tail = SpareArchives.TailOf(path);

        string badIndex = SpareArchives.Damage(path, t.Sub("bad-index.osa"), b => b[tail.IndexOffset + 30] ^= 1);
        using (var s = ArchiveSources.Open(badIndex, ct: Ct))
        {
            Assert.Equal([OsaReader.UsedSpareIndex], s.Warnings);
            Assert.Equal(files.Count, s.Test(null, null, Ct).FilesOk);
        }

        string badTrailer = SpareArchives.Damage(path, t.Sub("bad-trailer.osa"), b => b[tail.TrailerOffset + 9] ^= 1);
        using (var s = ArchiveSources.Open(badTrailer, ct: Ct))
        {
            Assert.Equal([OsaReader.SpareDamaged], s.Warnings);
            Assert.StartsWith("damaged", Detail(s, "Spare copy of the file list"));
        }

        string badCopy = SpareArchives.Damage(path, t.Sub("bad-copy.osa"), b => b[tail.SpareOffset + 40] ^= 1);
        using (var s = ArchiveSources.Open(badCopy, ct: Ct))
        {
            Assert.Empty(s.Warnings); // opening does not read the copy …
            var test = s.Test(null, null, Ct);
            Assert.Equal(files.Count, test.FilesOk);
            Assert.Equal([OsaReader.SpareDamaged], test.Warnings); // … Test does
        }
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

    [Fact]
    public void TheSourceListsAndExtractsAgainAfterAnExtractCanceledHalfWay()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(12, 3000);
        using var s = ArchiveSources.Open(SpareArchives.Write(t.Sub("a.osa"), files), ct: Ct);
        int items = s.Items.Count;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var halfWay = new Reports(p => { if (p.FilesDone >= 3) cts.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() => s.Extract(t.Sub("first"), new ArchiveExtractRequest(), halfWay, cts.Token));

        Assert.Equal(items, s.Items.Count);
        var again = s.Extract(t.Sub("second"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal(files.Count, again.FilesOk);
        foreach (var (name, data) in files) Assert.Equal(data, File.ReadAllBytes(Path.Combine([t.Sub("second"), .. name.Split('/')])));
        Assert.Equal(files.Count, s.Test(null, null, Ct).FilesOk);
    }

    [Fact]
    public void ProgressCountsTheSelectionAndEndsAtItsTotal()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(6, 3000);
        using var s = ArchiveSources.Open(SpareArchives.Write(t.Sub("a.osa"), files), ct: Ct);

        var all = new Reports();
        s.Extract(t.Sub("all"), new ArchiveExtractRequest(), all, Ct);
        long total = files.Values.Sum(v => v.Length);
        Assert.All(all.All, p => Assert.Equal(total, p.BytesTotal));
        Assert.Equal((total, files.Count, files.Count), (all.All[^1].BytesDone, all.All[^1].FilesDone, all.All[^1].FilesTotal));

        var picked = s.Items.Where(i => !i.IsDirectory).Take(2).ToList();
        var some = new Reports();
        s.Test(i => picked.Contains(i), some, Ct);
        long selected = picked.Sum(i => i.Size!.Value);
        Assert.All(some.All, p => Assert.Equal(selected, p.BytesTotal));
        Assert.Equal(selected, some.All[^1].BytesDone);

        var none = new Reports();
        s.Extract(t.Sub("none"), new ArchiveExtractRequest { Indices = new HashSet<int>() }, none, Ct);
        Assert.Equal(new ExtractProgress(0, 0, 0, 0, null), Assert.Single(none.All));
    }

    [Fact]
    public void ThePasswordIsAskedOnceInOpenForAnEncryptedArchiveAndNeverOtherwise()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(2, 2000);
        int asked = 0;
        string? Ask() { asked++; return SpareArchives.Password; }

        using (var s = ArchiveSources.Open(SpareArchives.Write(t.Sub("e.osa"), files, new OsaWriterOptions { Password = SpareArchives.Password }), new ArchiveOpenOptions { Password = Ask }, Ct))
        {
            Assert.Equal(1, asked);
            Assert.Equal(files.Count, s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct).FilesOk);
            Assert.Equal(files.Count, s.Test(null, null, Ct).FilesOk);
            Assert.Equal(1, asked);
        }
        asked = 0;
        using (var s = ArchiveSources.Open(SpareArchives.Write(t.Sub("plain.osa"), files), new ArchiveOpenOptions { Password = Ask }, Ct))
        {
            s.Extract(t.Sub("out2"), new ArchiveExtractRequest(), null, Ct);
            s.Test(null, null, Ct);
        }
        Assert.Equal(0, asked);
    }
}
