using Osmium.Core.Codecs.Native;

namespace Osmium.Core.Tests.Native;

[Collection("Native encoders")]
public sealed class NativeBoundsTests
{
    [Theory]
    [InlineData(0UL, 128UL)]
    [InlineData(1UL, 129UL)]
    [InlineData(3UL, 132UL)]
    [InlineData(1UL << 30, (1UL << 30) + (1UL << 30) / 3 + 128)]
    [InlineData(1UL << 31, (1UL << 31) + (1UL << 31) / 3 + 128)]
    public void LzmaBoundIsNPlusAThirdPlus128(ulong n, ulong bound) => Assert.Equal(bound, NativeBounds.Lzma(n));

    [Theory]
    [InlineData(0UL, 64UL)]
    [InlineData(1UL, 1UL + 63)]
    [InlineData(128UL << 10, (128UL << 10) + 512)]
    [InlineData(1UL << 30, (1UL << 30) + (1UL << 22))]
    public void ZstdBoundIsZstdCompressBound(ulong n, ulong bound) => Assert.Equal(bound, NativeBounds.Zstd(n));

    [Theory]
    [InlineData((1UL << 31) + 1)]
    [InlineData(ulong.MaxValue)]
    [InlineData(ulong.MaxValue / 3)]
    public void InputsAboveTheLimitAreRefused(ulong n)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeBounds.Lzma(n));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeBounds.Zstd(n));
    }

    [Fact]
    public void ABoundAboveTheLargestArrayIsRefusedBeforeAllocating()
    {
        Assert.Equal(Array.MaxLength, NativeBounds.ArrayLength((ulong)Array.MaxLength));
        var e = Assert.Throws<InvalidOperationException>(() => NativeBounds.ArrayLength((ulong)Array.MaxLength + 1));
        Assert.Equal("This block is too large for the native encoder.", e.Message);
        // The largest block Insane makes (1 GiB) fits; an Array.MaxLength block does not.
        Assert.True(NativeBounds.ArrayLength(NativeBounds.Lzma(1UL << 30)) > 0);
        Assert.Throws<InvalidOperationException>(() => NativeBounds.ArrayLength(NativeBounds.Lzma((ulong)Array.MaxLength)));
    }

    [Fact]
    public void TheLibraryComputesTheSameBounds()
    {
        NativeTestSupport.RequireNative();
        var api = NativeEncoders.ForEncode()!;
        foreach (ulong n in new ulong[] { 0, 1, 2, 3, 127, 128, 255, 4096, (128 << 10) - 1, 128 << 10, (128 << 10) + 1, 1 << 20, 1 << 30, 1UL << 31 })
        {
            Assert.Equal(Osn.Ok, api.RawBound(zstd: false, n, out var a));
            Assert.Equal(Osn.Ok, api.RawBound(zstd: true, n, out var b));
            Assert.Equal(NativeBounds.Lzma(n), a);
            Assert.Equal(NativeBounds.Zstd(n), b);
        }
        Assert.Equal(Osn.EParam, api.RawBound(zstd: false, (1UL << 31) + 1, out var c));
        Assert.Equal(0UL, c);
        Assert.Equal(Osn.EParam, api.RawBound(zstd: true, ulong.MaxValue, out _));
        Assert.Equal(Osn.EParam, api.RawBound(zstd: false, 1, out _, nullBound: true));
    }

    [Fact]
    public void MemoryCapIsTheEstimateMinusTheOutputBufferPlusOneMiB()
    {
        Assert.Equal((ulong)(500L << 20) - 1000 + (1 << 20), NativeEncode.MemCap(500L << 20, 1000));
        Assert.Equal(1UL << 20, NativeEncode.MemCap(10, 1UL << 40));   // never below 1 MiB
    }
}
