namespace Osmium.Core.Codecs.Native;

/// <summary>
/// The largest output each native encoder can write for an input, in 64-bit checked arithmetic, the same formulas
/// as osn_lzma_bound and osn_zstd_bound (the encoders compare the two before every call). Osmium.Core builds with
/// unchecked arithmetic, so every step here is explicitly checked.
/// </summary>
internal static class NativeBounds
{
    /// <summary>Raw LZMA: n + n/3 + 128 (the figure LzmaLib.h gives).</summary>
    public static ulong Lzma(ulong n)
    {
        if (n > Osn.MaxInput) throw new ArgumentOutOfRangeException(nameof(n), "The block is larger than the native encoder accepts.");
        return checked(n + n / 3 + 128);
    }

    /// <summary>ZSTD_compressBound: n + n/256, plus (128 KiB - n) / 2048 below 128 KiB.</summary>
    public static ulong Zstd(ulong n)
    {
        if (n > Osn.MaxInput) throw new ArgumentOutOfRangeException(nameof(n), "The block is larger than the native encoder accepts.");
        const ulong blockMax = 128 << 10;
        return checked(n + (n >> 8) + (n < blockMax ? (blockMax - n) >> 11 : 0));
    }

    /// <summary>The bound as an array length, or an exception before anything is allocated when the bound does not
    /// fit in a .NET array.</summary>
    public static int ArrayLength(ulong bound)
    {
        if (bound > (ulong)Array.MaxLength)
            throw new InvalidOperationException("This block is too large for the native encoder.");
        return checked((int)bound);
    }
}
