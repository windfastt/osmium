using System.Diagnostics;
using Osmium.Core.Container;
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
    public async Task ADeviceSwappedInBeforeTheBlocksAreReadIsNotRead()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Device nodes are made with mknod here on Linux; Windows has none.");
        using var t = new TempDir();
        string input = MakeTree(t);
        var scan = Scanner.Scan([input], ct: Ct);
        var plan = await new Planner(Options(DedupMode.Off)).BuildAsync(scan, Ct);
        string file = Path.Combine(input, "a.bin");
        File.Delete(file);
        // A character device like /dev/zero (1, 5).
        using (var mk = Process.Start(new ProcessStartInfo("mknod", [file, "c", "1", "5"]) { UseShellExecute = false, RedirectStandardError = true })!)
        {
            mk.WaitForExit();
            if (mk.ExitCode != 0) Assert.Skip("Making a device node needs administrator rights.");
        }
        string output = t.Sub("a.osa");

        var e = await Assert.ThrowsAsync<SourceChangedException>(
            () => ArchiveBuilder.BuildAsync(plan, new BuildOptions { OutputPath = output, MemoryBudget = 2L << 30 }, Ct));
        Assert.Equal(file, e.SourcePath);
        Assert.Contains("is not a regular file", e.Message);
        Assert.False(File.Exists(output));
    }

    /// <summary>in/sub/a.bin, scanned; then sub moves to a new name and a link to <paramref name="target"/> takes its place.</summary>
    private static (ScanResult Scan, string File, byte[] Data) ScanThenLinkTheFolder(TempDir t, Func<string, string> target)
    {
        string input = t.Sub("in"), sub = Path.Combine(input, "sub"), file = Path.Combine(sub, "a.bin");
        Directory.CreateDirectory(sub);
        var data = new byte[100_000];
        new Random(3).NextBytes(data);
        File.WriteAllBytes(file, data);
        var scan = Scanner.Scan([input], ct: Ct);
        string moved = Path.Combine(input, "moved");
        Directory.Move(sub, moved);
        Directory.CreateSymbolicLink(sub, target(moved));
        return (scan, file, data);
    }

    [Fact]
    public async Task AFolderMovedBehindALinkStillGivesTheFilesTheScanSaw()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The scan records file ids on Linux only; elsewhere a folder on the way that became a link is refused.");
        using var t = new TempDir();
        // The link leads to the folder itself under its new name: a.bin behind it is the very file the scan saw.
        var (scan, _, data) = ScanThenLinkTheFolder(t, moved => Path.GetFileName(moved));

        var plan = await new Planner(Options(DedupMode.Files)).BuildAsync(scan, Ct);
        var r = await ArchiveBuilder.BuildAsync(plan, new BuildOptions { OutputPath = t.Sub("a.osa"), MemoryBudget = 2L << 30 }, Ct);

        using var reader = OsaReader.Open(r.Path);
        var res = Extractor.Extract(reader, t.Sub("out"), null, null, null, Ct);
        Assert.Empty(res.Errors);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(t.Sub("out"), "in", "sub", "a.bin")));
    }

    [Fact]
    public async Task AnotherFileBehindALinkInAFoldersPlaceIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The scan records file ids on Linux only; elsewhere every folder on the way is checked.");
        using var t = new TempDir();
        // The link leads to a copy of the folder: a.bin there has the same name, size, content and time, but is another
        // file.
        var (scan, file, _) = ScanThenLinkTheFolder(t, moved =>
        {
            string copy = t.Sub("copy");
            Directory.CreateDirectory(copy);
            File.Copy(Path.Combine(moved, "a.bin"), Path.Combine(copy, "a.bin"));
            File.SetLastWriteTimeUtc(Path.Combine(copy, "a.bin"), File.GetLastWriteTimeUtc(Path.Combine(moved, "a.bin")));
            return copy;
        });

        var e = await Assert.ThrowsAsync<SourceChangedException>(() => new Planner(Options(DedupMode.Files)).BuildAsync(scan, Ct));
        Assert.Equal(file, e.SourcePath);
        Assert.Contains("is below a folder that became a link after it was scanned", e.Message);
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

    [Theory]
    [InlineData("pipe")]
    [InlineData("device")]
    public async Task APipeOrADeviceBehindALinkInAFoldersPlaceIsNeverOpenedForReading(string kind)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The open by file id and inotify are Linux's; elsewhere the folder check refuses the link before any open.");
        using var t = new TempDir();
        string elsewhere = t.Sub("elsewhere");
        Directory.CreateDirectory(elsewhere);
        var (scan, file, _) = ScanThenLinkTheFolder(t, _ => elsewhere);
        // Behind the link, a.bin is a pipe that a read would wait on for ever, or a device like /dev/zero (1, 5).
        string special = Path.Combine(elsewhere, "a.bin");
        string[] make = kind == "pipe" ? ["mkfifo", special] : ["mknod", special, "c", "1", "5"];
        using (var mk = Process.Start(new ProcessStartInfo(make[0], make[1..]) { UseShellExecute = false, RedirectStandardError = true })!)
        {
            mk.WaitForExit();
            if (mk.ExitCode != 0) Assert.Skip("Making a device node needs administrator rights; the pipe case shows the same.");
        }
        using var watch = new OpenWatch(elsewhere);
        // Kernels that report a path-only open as an open cannot show the difference; the pipe's timeout still does.
        bool pathOnlyOpensShow = watch.PathOnlyOpenShows(special);
        var seen = scan.Files.Single(f => f.FullPath == file);

        var open = Task.Run(() => SourceIdentity.Open(seen), Ct);
        bool finished = await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(30), Ct)) == open;
        if (!finished) OpenWatch.GiveAWaitingReaderAWriter(special);
        Assert.True(finished, $"{kind} was opened for reading and waited on");

        if (!pathOnlyOpensShow) Assert.DoesNotContain("a.bin", watch.Opened());
        var e = await Assert.ThrowsAsync<SourceChangedException>(() => open);
        Assert.Equal(file, e.SourcePath);
        Assert.Contains("is below a folder that became a link after it was scanned", e.Message);
    }

    /// <summary>The names of the files in a folder that were opened (for reading or writing, not as a path only), as
    /// inotify reports them (Linux).</summary>
    private sealed class OpenWatch : IDisposable
    {
        private const int NonBlocking = 0x800, PathOnly = 0x200000, CloseOnExec = 0x80000, WriteOnly = 0x1;
        private const uint InOpen = 0x20;
        private readonly int fd;

        public OpenWatch(string folder)
        {
            fd = inotify_init1(NonBlocking);
            Assert.True(fd >= 0, "inotify is not available");
            Assert.True(inotify_add_watch(fd, folder, InOpen) >= 0, "the folder cannot be watched");
        }

        /// <summary>Whether opening <paramref name="path"/> as a path only shows up as an open.</summary>
        public bool PathOnlyOpenShows(string path)
        {
            int p = open(path, PathOnly | Osmium.Core.IO.FileStat.LinuxNoFollow() | CloseOnExec);
            Assert.True(p >= 0, "a path-only open failed");
            close(p);
            return Opened().Count > 0;
        }

        public List<string> Opened()
        {
            var names = new List<string>();
            var buf = new byte[4096];
            for (nint n; (n = read(fd, buf, buf.Length)) > 0;)
                for (int i = 0; i + 16 <= n;)
                {
                    int len = BitConverter.ToInt32(buf, i + 12);
                    names.Add(System.Text.Encoding.UTF8.GetString(buf, i + 16, len).TrimEnd('\0'));
                    i += 16 + len;
                }
            return names;
        }

        /// <summary>Ends an open of the pipe at <paramref name="path"/> for reading that waits for a writer.</summary>
        public static void GiveAWaitingReaderAWriter(string path)
        {
            int w = open(path, WriteOnly | NonBlocking | CloseOnExec);
            if (w >= 0) close(w);
        }

        public void Dispose() => close(fd);

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int inotify_init1(int flags);

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int inotify_add_watch(int fd, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, uint mask);

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern nint read(int fd, byte[] buf, nint count);

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int open([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags);

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);
    }
}
