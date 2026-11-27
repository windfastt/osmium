using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests;

/// <summary>A codec's encode memory estimate covers what its encoder was measured to take, with room to spare, and is
/// not so far above it that the planner turns the codec down for nothing.</summary>
public class CodecMemoryTests
{
    private static readonly MethodRegistry Reg = MethodRegistry.Default;

    /// <summary>The encoder's own memory for <paramref name="len"/> bytes: the pipeline's estimate without the input.</summary>
    private static double EstimateMiB(string spec, long len)
    {
        var codec = PipelineSpec.Parse(spec, Reg).Codec;
        return codec.Method.EncodeMemory(codec.Params, len) / 1048576.0;
    }

    /// <summary>
    /// Peaks measured on Linux x64: the built-in encoder's resident memory above what it started with (random input,
    /// so the output is as large as it gets, and about 8 MiB of runtime), and below, the peak allocation of zstd built
    /// as a C library and told the input's size. Without the size zstd sizes its tables by the level alone, which is
    /// why level 22 takes about 650 MiB even for 1 MiB.
    /// </summary>
    [Theory]
    [InlineData(3, 24, 0, 16, 54.3)]
    [InlineData(9, 22, 0, 4, 32.9)]
    [InlineData(12, 22, 0, 4, 63.1)]
    [InlineData(16, 22, 0, 4, 51.0)]
    [InlineData(19, 22, 0, 4, 99.4)]
    [InlineData(19, 24, 0, 16, 137.0)]
    [InlineData(19, 27, 0, 72, 369.5)]
    [InlineData(19, 27, 1, 72, 369.4)]
    [InlineData(20, 22, 0, 4, 179.3)]
    [InlineData(21, 22, 0, 4, 339.3)]
    [InlineData(22, 16, 0, 1, 648.6)]
    [InlineData(22, 22, 0, 4, 659.5)]
    [InlineData(22, 24, 0, 16, 697.0)]
    [InlineData(22, 27, 0, 72, 929.5)]
    [InlineData(22, 27, 1, 72, 929.3)]
    public void ZstdEstimateCoversTheBuiltInEncoder(int level, int wlog, int ldm, int mib, double peakMiB)
    {
        double estimate = EstimateMiB($"zstd(level={level},wlog={wlog},ldm={ldm})", (long)mib << 20);
        Assert.True(estimate >= peakMiB * 1.1, $"{estimate:F1} MiB estimated, {peakMiB} MiB measured");
        Assert.True(estimate <= peakMiB * 1.5 + 16, $"{estimate:F1} MiB estimated, {peakMiB} MiB measured");
    }

    [Theory]
    [InlineData(3, 24, 0, 16, 1.4)]
    [InlineData(19, 22, 0, 4, 49.4)]
    [InlineData(19, 24, 0, 16, 81.4)]
    [InlineData(19, 27, 0, 72, 145.5)]
    [InlineData(19, 27, 1, 72, 145.5)]
    [InlineData(22, 24, 0, 16, 257.4)]
    [InlineData(22, 27, 0, 72, 705.5)]
    [InlineData(22, 27, 1, 72, 705.5)]
    public void ZstdEstimateCoversZstdToldTheInputSize(int level, int wlog, int ldm, int mib, double peakMiB)
    {
        // Such an encoder writes its output in a buffer of the input's size besides its own memory.
        long len = (long)mib << 20;
        double estimate = EstimateMiB($"zstd(level={level},wlog={wlog},ldm={ldm})", len) - mib;
        Assert.True(estimate >= peakMiB * 1.1, $"{estimate:F1} MiB estimated besides the output, {peakMiB} MiB measured");
    }
}
