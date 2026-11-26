using System.Diagnostics;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;

namespace Osmium.Core.Tests;

/// <summary>
/// Blocks are written in plan order, so a compressed block may wait for an earlier, slower one. Its payload stays
/// charged to the memory budget while it waits.
/// </summary>
public class ArchiveBuilderMemoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Compresses like the local compressor, but holds the block that starts with <paramref name="first"/> until
    /// released, and counts the other blocks it is asked to compress meanwhile.</summary>
    private sealed class FirstBlockHeld(byte[] first) : IBlockCompressor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _others;

        public int OthersStarted => Volatile.Read(ref _others);

        public void Release() => _release.TrySetResult();

        public async Task<CompressedBlock> CompressAsync(PipelineSpec spec, byte[] raw, CodecContext ctx)
        {
            if (raw.AsSpan(0, first.Length).SequenceEqual(first)) await _release.Task.WaitAsync(ctx.Token);
            else Interlocked.Increment(ref _others);
            return await LocalBlockCompressor.Instance.CompressAsync(spec, raw, ctx);
        }
    }

    [Fact]
    public async Task BlocksWaitingToBeWrittenStayWithinTheMemoryBudget()
    {
        // 16 blocks of 256 KiB, stored. Each needs 1 MiB while it is compressed and keeps its 256 KiB payload until it is
        // written. With a 3 MiB budget and the first block held back, at most (3 - 1) / 0.25 = 8 others can be done
        // before the first is written; without the charge for waiting payloads all 15 would be.
        const int blockSize = 256 << 10, blocks = 16;
        const long budget = 3L << 20, compressing = 4L * blockSize;
        using var t = new TempDir();
        var data = new byte[blockSize * blocks];
        new Random(5).NextBytes(data);
        var input = t.Sub("data.bin");
        File.WriteAllBytes(input, data);
        var plan = await new Planner(new PlanOptions { Profile = Profile.Store, BlockCap = blockSize, MemoryBudget = 2L << 30, Threads = 2 })
            .BuildAsync(Scanner.Scan([input], ct: Ct), Ct);
        Assert.Equal(blocks, plan.Blocks.Count);

        var compressor = new FirstBlockHeld(data[..64]);
        // The builder waits for memory before it starts a block, so it runs on its own thread while this one watches.
        var build = Task.Run(() => ArchiveBuilder.BuildAsync(plan, new BuildOptions
        {
            OutputPath = t.Sub("a.osa"), MemoryBudget = budget, Threads = 8, Compressor = compressor,
        }, Ct), Ct);
        // Wait until all other blocks have started, or none has started for a second.
        int seen = -1;
        var quiet = Stopwatch.StartNew();
        while (compressor.OthersStarted < blocks - 1 && quiet.Elapsed < TimeSpan.FromSeconds(1))
        {
            await Task.Delay(50, Ct);
            if (compressor.OthersStarted != seen) (seen, quiet) = (compressor.OthersStarted, Stopwatch.StartNew());
        }
        int started = compressor.OthersStarted;
        compressor.Release();
        var result = await build;

        Assert.True(started <= (budget - compressing) / blockSize, $"{started} blocks compressed while the first one waited");
        Assert.True(started > 0, "no block was compressed while the first one waited");
        using var reader = OsaReader.Open(result.Path);
        var test = Extractor.Test(reader, null, Ct);
        Assert.Equal(0, test.FilesFailed);
        Assert.Empty(test.Errors);
    }
}
