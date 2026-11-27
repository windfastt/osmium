using Osmium.Core.Crypto;

namespace Osmium.Core.Tests.Crypto;

public class Argon2idTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="cancel">Null: the test's own token.</param>
    internal static string Tag(Argon2Case c, int threads = 4, CancellationToken? cancel = null, Action<double>? progress = null)
    {
        var tag = new byte[c.TagLength];
        Argon2id.Hash(Convert.FromHexString(c.Password), Convert.FromHexString(c.Salt), Convert.FromHexString(c.Secret),
            Convert.FromHexString(c.Data), c.TimeCost, c.MemoryKiB, c.Lanes, tag, threads, cancel ?? Ct, progress);
        return Convert.ToHexStringLower(tag);
    }

    [Fact]
    public void TheTestVectorOfRfc9106Matches()
    {
        // RFC 9106 §5.3: t = 3, m = 32 KiB, p = 4, T = 32, P = 32 × 01, S = 16 × 02, K = 8 × 03, X = 12 × 04.
        var c = new Argon2Case(string.Concat(Enumerable.Repeat("01", 32)), string.Concat(Enumerable.Repeat("02", 16)),
            string.Concat(Enumerable.Repeat("03", 8)), string.Concat(Enumerable.Repeat("04", 12)), 3, 32, 4, 32,
            "0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659");
        Assert.Equal(c.Tag, Tag(c));
    }

    public static TheoryData<int> CrossCases => [.. Enumerable.Range(0, Argon2idVectors.Cross.Length)];

    [Theory]
    [MemberData(nameof(CrossCases))]
    public void TheReferenceVectorsMatch(int index)
    {
        var c = Argon2idVectors.Cross[index];
        Assert.Equal(c.Tag, Tag(c));
    }

    [Fact]
    public void TheNumberOfThreadsDoesNotChangeTheTag()
    {
        var c = Argon2idVectors.Cross.First(v => v.Lanes == 4 && v.MemoryKiB >= 1024);
        foreach (int threads in new[] { 1, 2, 3, 4, 8, 0, -1 }) Assert.Equal(c.Tag, Tag(c, threads));
    }

    [Theory]
    [InlineData(1, 512, 64)]  // the most lanes a reader accepts with the least memory: segments of two blocks
    [InlineData(3, 512, 64)]
    [InlineData(2, 8, 1)]
    public void TheSmallestSegmentsWork(int t, int m, int p)
    {
        // Two blocks per segment: the first segment of each lane computes nothing after its first two blocks, and the
        // reference set of every later block is still never empty. Threads do not change the result.
        var c = new Argon2Case("70617373", "00112233445566778899aabbccddeeff", "", "", t, m, p, 32, "");
        string one = Tag(c, 1);
        Assert.Equal(one, Tag(c, 8));
        Assert.Equal(64, one.Length);
    }

    [Fact]
    public void ArgumentsOutsideTheAlgorithmsRangeAreRefusedBeforeAnyAllocation()
    {
        using var seams = new KdfSeams();
        var tag = new byte[32];
        Assert.Throws<ArgumentOutOfRangeException>(() => Argon2id.Hash([], [], [], [], 0, 64, 1, tag, 1, Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => Argon2id.Hash([], [], [], [], 1, 64, 0, tag, 1, Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => Argon2id.Hash([], [], [], [], 1, 1 << 30, 1 << 24, tag, 1, Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => Argon2id.Hash([], [], [], [], 1, 31, 4, tag, 1, Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => Argon2id.Hash([], [], [], [], 1, 64, 1, new byte[3], 1, Ct));
        Assert.Equal(0, seams.Allocations);
        Argon2id.Hash([], [], [], [], 1, 32, 4, new byte[4], 1, Ct); // the smallest that is allowed
        Assert.Equal((1, 32 * 1024), (seams.Allocations, seams.AllocatedBytes));
    }

    [Fact]
    public void TheWorkAreaIsWipedBeforeItIsFreed()
    {
        using var seams = new KdfSeams();
        var c = Argon2idVectors.Cross.First(v => v.MemoryKiB == 4100);
        Assert.Equal(c.Tag, Tag(c));
        // m′ = 4·p·⌊m / 4p⌋ blocks of 1 KiB
        long blocks = c.MemoryKiB / (4 * c.Lanes) * 4 * c.Lanes;
        Assert.Equal((1, blocks * 1024, 1), (seams.Allocations, seams.AllocatedBytes, seams.Frees));
        Assert.Equal(0, seams.NonZeroWordsAtFree);
    }

    [Theory]
    [InlineData(0)]   // before the first slice
    [InlineData(1)]   // after the first slice
    [InlineData(5)]   // in the second pass
    public void CancellationStopsBetweenSlicesAndStillWipesAndFrees(int slicesBeforeCancel)
    {
        using var seams = new KdfSeams();
        using var cts = new CancellationTokenSource();
        int slices = 0;
        if (slicesBeforeCancel == 0) cts.Cancel();
        var c = new Argon2Case("70617373", "00112233445566778899aabbccddeeff", "", "", 3, 2048, 2, 32, "");
        Assert.ThrowsAny<OperationCanceledException>(() => Tag(c, 2, cts.Token, _ => { if (++slices == slicesBeforeCancel) cts.Cancel(); }));
        if (slicesBeforeCancel == 0)
        {
            Assert.Equal((0, 0), (seams.Allocations, seams.Frees)); // nothing was allocated
            return;
        }
        Assert.Equal(slicesBeforeCancel, slices);
        Assert.Equal((1, 1), (seams.Allocations, seams.Frees)); // allocated once, freed once
        Assert.Equal(0, seams.NonZeroWordsAtFree);
    }

    [Fact]
    public void ProgressRisesToOne()
    {
        var seen = new List<double>();
        var c = Argon2idVectors.Cross.First(v => v.TimeCost == 3);
        Assert.Equal(c.Tag, Tag(c, 1, Ct, seen.Add));
        Assert.Equal(4 * c.TimeCost, seen.Count);
        Assert.True(seen.Zip(seen.Skip(1)).All(p => p.First < p.Second));
        Assert.Equal(1.0, seen[^1]);
    }
}

/// <summary>
/// The vectors at the sizes archives use (256 MiB to 4 GiB), alone in their collection so that at most one large work
/// area exists at a time. A case that needs more than half of the memory the runtime may use is skipped.
/// </summary>
[Collection(HeavyKeyDerivation.Name)]
public class Argon2idHeavyTests
{
    public static TheoryData<int> HeavyCases => [.. Enumerable.Range(0, Argon2idVectors.Heavy.Length)];

    /// <summary>Skips the test when <paramref name="bytes"/> are more than half of the memory this run may use, or more
    /// than is free on the machine now (with a margin), after giving back what earlier tests left on the heap.</summary>
    internal static void SkipUnlessMemoryFor(long bytes)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var info = GC.GetGCMemoryInfo();
        long available = info.TotalAvailableMemoryBytes;
        if (bytes > available / 2)
            Assert.Skip($"Needs {bytes >> 20:N0} MiB for the work area, more than half of the {available >> 20:N0} MiB this test run may use.");
        if (FreeNow() is { } free && bytes + (1L << 30) > free)
            Assert.Skip($"Needs {bytes >> 20:N0} MiB for the work area and 1 GiB to spare, but only {free >> 20:N0} MiB are free on this machine now.");
    }

    /// <summary>Memory the machine can give now: MemAvailable on Linux (the runtime's memory load counts the page
    /// cache there), the runtime's figure elsewhere; null when unknown.</summary>
    private static long? FreeNow()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                    if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                        return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], System.Globalization.CultureInfo.InvariantCulture) * 1024;
            }
            catch (IOException) { }
            return null;
        }
        var info = GC.GetGCMemoryInfo();
        long free = info.TotalAvailableMemoryBytes - info.MemoryLoadBytes;
        return free > 0 ? free : null;
    }

    [Theory]
    [MemberData(nameof(HeavyCases))]
    public void TheReferenceVectorsMatchAtTheSizesArchivesUse(int index)
    {
        var c = Argon2idVectors.Heavy[index];
        SkipUnlessMemoryFor(c.MemoryKiB * 1024L);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(c.Tag, Argon2idTests.Tag(c, Environment.ProcessorCount));
        double seconds = sw.Elapsed.TotalSeconds;
        TestContext.Current.TestOutputHelper?.WriteLine($"{c}: {seconds:F2} s on {Environment.ProcessorCount} processors");
        var p = KdfParameters.Argon2id(c.TimeCost, c.MemoryKiB, c.Lanes);
        string name = p == KdfPresets.Floor ? "Floor" : p == KdfPresets.Standard ? "Standard" : p == KdfPresets.Strong ? "Strong"
            : p == KdfPresets.Maximum ? "Maximum" : "one lane";
        KdfTimingReport.Add("vector", name, p, seconds, 1);
    }
}
