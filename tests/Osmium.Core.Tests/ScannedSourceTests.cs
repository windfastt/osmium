using System.Diagnostics;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests;

/// <summary>What the scan takes in, and reading a scanned file again: never a pipe or a device, never through a link
/// that took a file's place, and never a file other than the one the scan saw.</summary>
public class ScannedSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlanOptions Options(DedupMode dedup) => new()
    {
        Profile = Profile.ByName("fast"),
        Dedup = dedup,
        MemoryBudget = 2L << 30,
        Threads = 2,
    };

    /// <summary>A folder with two files of the same size and different content, so that whole-file dedup reads both.</summary>
    private static string MakeTree(TempDir t)
    {
        string input = t.Sub("in");
        Directory.CreateDirectory(input);
        var rnd = new Random(5);
        var data = new byte[300_000];
        rnd.NextBytes(data);
        File.WriteAllBytes(Path.Combine(input, "a.bin"), data);
        rnd.NextBytes(data);
        File.WriteAllBytes(Path.Combine(input, "b.bin"), data);
        return input;
    }

    [Fact]
    public void APipeOrADeviceIsLeftOutOfTheScanWithAWarning()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The scan tells pipes and devices from files on Linux; elsewhere opening one for reading refuses it.");
        using var t = new TempDir();
        string input = t.Sub("in");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "keep.txt"), "a file");
        string pipe = Path.Combine(input, "pipe");
        using (var mk = Process.Start(new ProcessStartInfo("mkfifo", [pipe]) { UseShellExecute = false })!) mk.WaitForExit();
        string device = Path.Combine(Path.GetPathRoot(input)!, "dev", "null");

        var scan = Scanner.Scan([input, device], ct: Ct);

        Assert.Equal(["in/keep.txt"], scan.Files.Select(f => f.RelPath));
        Assert.Contains($"{pipe}: not a regular file (a pipe), left out", scan.Skipped);
        Assert.Contains($"{device}: not a regular file (a device), left out", scan.Skipped);
    }

    [Theory]
    [InlineData(DedupMode.Off)]
    [InlineData(DedupMode.Files)]
    [InlineData(DedupMode.Chunks)]
    public async Task AFileReplacedByALinkAfterTheScanIsNotReadThroughIt(DedupMode dedup)
    {
        using var t = new TempDir();
        string input = MakeTree(t);
        string secret = t.Sub("secret.bin");
        File.WriteAllBytes(secret, new byte[300_000]);
        var scan = Scanner.Scan([input], ct: Ct);
        string file = Path.Combine(input, "a.bin");
        File.Delete(file);
        try { File.CreateSymbolicLink(file, secret); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links cannot be created here (Windows needs Developer Mode or administrator rights).");
        }

        var e = await Assert.ThrowsAsync<SourceChangedException>(() => new Planner(Options(dedup)).BuildAsync(scan, Ct));
        Assert.Equal(file, e.SourcePath);
        Assert.Contains("became a link after it was scanned", e.Message);
    }

    [Theory]
    [InlineData(DedupMode.Off)]
    [InlineData(DedupMode.Files)]
    [InlineData(DedupMode.Chunks)]
    public async Task AFileChangedAfterTheScanStopsThePlan(DedupMode dedup)
    {
        using var t = new TempDir();
        string input = MakeTree(t);
        var scan = Scanner.Scan([input], ct: Ct);
        string file = Path.Combine(input, "a.bin");
        var seen = scan.Files.Single(f => f.FullPath == file);
        File.WriteAllBytes(file, new byte[seen.Size]);
        File.SetLastWriteTimeUtc(file, seen.ModifiedUtc.AddHours(1));

        var e = await Assert.ThrowsAsync<SourceChangedException>(() => new Planner(Options(dedup)).BuildAsync(scan, Ct));
        Assert.Equal(file, e.SourcePath);
        Assert.Contains("changed after it was scanned", e.Message);
    }

    [Theory]
    [InlineData(DedupMode.Off)]
    [InlineData(DedupMode.Files)]
    [InlineData(DedupMode.Chunks)]
    public async Task AnotherFileOfTheSameSizeAndTimeInItsPlaceStopsThePlan(DedupMode dedup)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The scan records file ids on Linux; elsewhere the creation time stands in, and Windows gives a file renamed into a name the creation time of the file it replaced.");
        using var t = new TempDir();
        string input = MakeTree(t);
        var scan = Scanner.Scan([input], ct: Ct);
        string file = Path.Combine(input, "a.bin"), other = t.Sub("other.bin");
        var seen = scan.Files.Single(f => f.FullPath == file);
        File.WriteAllBytes(other, new byte[seen.Size]);
        File.SetLastWriteTimeUtc(other, seen.ModifiedUtc);
        File.Move(other, file, overwrite: true);

        var e = await Assert.ThrowsAsync<SourceChangedException>(() => new Planner(Options(dedup)).BuildAsync(scan, Ct));
        Assert.Equal(file, e.SourcePath);
        Assert.Contains("was replaced by another file after it was scanned", e.Message);
    }

    [Theory]
    [InlineData(DedupMode.Files)]
    [InlineData(DedupMode.Chunks)]
    public async Task UnchangedFilesAndCopiesStillDedupe(DedupMode dedup)
    {
        using var t = new TempDir();
        string input = MakeTree(t);
        string copy = Path.Combine(input, "c.bin");
        File.Copy(Path.Combine(input, "a.bin"), copy);
        var scan = Scanner.Scan([input], ct: Ct);

        var plan = await new Planner(Options(dedup)).BuildAsync(scan, Ct);

        Assert.Equal(["in/a.bin", "in/b.bin", "in/c.bin"], plan.Files.Select(f => f.Src.RelPath));
        Assert.Equal(600_000, plan.StoredInput);
        Assert.Empty(plan.Warnings);
    }

    [Theory]
    [InlineData(DedupMode.Off)]
    [InlineData(DedupMode.Files)]
    public async Task ALinkSwappedInBeforeTheBlocksAreReadIsNotReadThroughIt(DedupMode dedup)
    {
        // After the plan, a.bin becomes a link to a file nobody chose, made to look like a.bin from outside: the link's
        // own size (the length of what it points to) and time, and the target's size and time, are all a.bin's. Only
        // the open refuses it, and nothing is written.
        using var t = new TempDir();
        string input = t.Sub("in");
        Directory.CreateDirectory(input);
        const int Size = 64;
        var when = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rnd = new Random(9);
        foreach (var name in new[] { "a.bin", "b.bin" })
        {
            var data = new byte[Size];
            rnd.NextBytes(data);
            File.WriteAllBytes(Path.Combine(input, name), data);
            File.SetLastWriteTimeUtc(Path.Combine(input, name), when);
        }
        var scan = Scanner.Scan([input], ct: Ct);
        var plan = await new Planner(Options(dedup)).BuildAsync(scan, Ct);

        string file = Path.Combine(input, "a.bin");
        string target = Path.Combine("..", "s".PadRight(Size - 3, 's'));
        Assert.Equal(Size, System.Text.Encoding.UTF8.GetByteCount(target));
        string secret = Path.GetFullPath(Path.Combine(input, target));
        File.WriteAllBytes(secret, Enumerable.Repeat((byte)'#', Size).ToArray());
        File.SetLastWriteTimeUtc(secret, when);
        File.Delete(file);
        try { File.CreateSymbolicLink(file, target); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links cannot be created here (Windows needs Developer Mode or administrator rights).");
        }
        if (!OperatingSystem.IsWindows())
        {
            var seconds = new DateTimeOffset(when).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var touch = Process.Start(new ProcessStartInfo("touch", ["-h", "-d", "@" + seconds, file]) { UseShellExecute = false })!;
            touch.WaitForExit();
            Assert.Equal(0, touch.ExitCode);
        }
        string output = t.Sub("a.osa");

        var e = await Assert.ThrowsAsync<SourceChangedException>(
            () => ArchiveBuilder.BuildAsync(plan, new BuildOptions { OutputPath = output, MemoryBudget = 2L << 30 }, Ct));
        Assert.Equal(file, e.SourcePath);
        Assert.Contains("became a link after it was scanned", e.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void AFileRemovedOnceItWasReadHasChanged()
    {
        using var t = new TempDir();
        string input = MakeTree(t);
        var scan = Scanner.Scan([input], ct: Ct);
        var seen = scan.Files[0];
        SourceIdentity.Verify(seen);
        File.Delete(seen.FullPath);

        var e = Assert.Throws<SourceChangedException>(() => SourceIdentity.Verify(seen));
        Assert.Contains("was removed while it was being archived", e.Message);
    }
}
