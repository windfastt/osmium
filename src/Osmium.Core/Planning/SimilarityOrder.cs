using System.Buffers;
using System.Numerics;
using Osmium.Core.Analysis;

namespace Osmium.Core.Planning;

/// <summary>
/// Similarity ordering inside a group. Each file gets a 64-bit SimHash over the 4-grams of a fixed
/// sample of its contents; the group's files are then chained greedily, each followed by the most similar file not yet
/// placed, so that a solid block's long-range matches and context models see related data together. Integer arithmetic
/// only: the order depends on the file contents and the order the chain starts from, never on threads or timing.
/// </summary>
public static class SimilarityOrder
{
    /// <summary>Smaller groups keep their order (with two files the chain cannot change it).</summary>
    public const int MinFiles = 3;
    /// <summary>The chain is quadratic in the number of files: larger groups keep their order.</summary>
    public const int MaxFiles = 20_000;
    public const int HeadBytes = 64 << 10, WindowBytes = 16 << 10;
    /// <summary>Files up to this size are hashed whole.</summary>
    public const int SampleBytes = HeadBytes + 3 * WindowBytes;

    /// <summary>The classes whose groups are reordered. Audio and image files are their own groups, and the order of
    /// incompressible data does not matter.</summary>
    public static bool Applies(CompatClass c) => c is CompatClass.Text or CompatClass.Utf16Text or CompatClass.CodeX86
        or CompatClass.CodeArm64 or CompatClass.Binary or CompatClass.Structured;

    /// <summary>
    /// The parts of a file of <paramref name="size"/> bytes that its signature reads: the whole file up to
    /// <see cref="SampleBytes"/>, otherwise the first 64 KiB and 16 KiB windows at 1/4, 1/2 and 3/4 of the file. A window
    /// starts where the previous one ends at the earliest, so no byte is read twice.
    /// </summary>
    public static (long Offset, int Length)[] SampleRanges(long size)
    {
        if (size <= 0) return [];
        if (size <= SampleBytes) return [(0, (int)size)];
        var ranges = new (long Offset, int Length)[4];
        ranges[0] = (0, HeadBytes);
        long end = HeadBytes;
        for (int k = 1; k <= 3; k++)
        {
            long start = Math.Max(end, size * k / 4);
            ranges[k] = (start, WindowBytes);
            end = start + WindowBytes;
        }
        return ranges;
    }

    /// <summary>Reads the sample of a file (see <see cref="SampleRanges"/>) and returns its SimHash.</summary>
    /// <exception cref="SourceChangedException">The file is not the one the scan saw (<see cref="SourceIdentity.Open"/>).</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static ulong Signature(SourceFile src)
    {
        var ranges = SampleRanges(src.Size);
        var hash = new SimHash();
        if (ranges.Length == 0) return hash.Value;
        var buffer = ArrayPool<byte>.Shared.Rent(ranges.Max(r => r.Length));
        try
        {
            using var fs = SourceIdentity.Open(src);
            foreach (var (offset, length) in ranges)
            {
                var span = buffer.AsSpan(0, length);
                for (int done = 0; done < length;)
                {
                    int n = RandomAccess.Read(fs.SafeFileHandle, span[done..], offset + done);
                    if (n <= 0) throw new SourceChangedException(src.FullPath, "file became shorter while it was being archived");
                    done += n;
                }
                hash.Add(span);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return hash.Value;
    }

    /// <summary>
    /// Greedy nearest-neighbour chain: starts with item 0, then repeatedly appends the item not yet placed with the
    /// smallest Hamming distance to the last one placed; ties go to the lowest index (the order the items came in).
    /// </summary>
    /// <returns>The items' indices in chain order.</returns>
    public static int[] Chain(IReadOnlyList<ulong> signatures)
    {
        int n = signatures.Count;
        var order = new int[n];
        if (n == 0) return order;
        // The items not yet placed, ascending, so the first of several equally close ones is the lowest index.
        var index = new int[n - 1];
        var sig = new ulong[n - 1];
        for (int i = 1; i < n; i++) (index[i - 1], sig[i - 1]) = (i, signatures[i]);
        int left = n - 1;
        ulong current = signatures[0];
        for (int k = 1; k < n; k++)
        {
            int bestPos = 0, bestDistance = int.MaxValue;
            for (int p = 0; p < left; p++)
            {
                int d = BitOperations.PopCount(current ^ sig[p]);
                if (d < bestDistance)
                {
                    (bestDistance, bestPos) = (d, p);
                    if (d == 0) break;
                }
            }
            order[k] = index[bestPos];
            current = sig[bestPos];
            left--;
            Array.Copy(index, bestPos + 1, index, bestPos, left - bestPos);
            Array.Copy(sig, bestPos + 1, sig, bestPos, left - bestPos);
        }
        return order;
    }

    /// <summary>
    /// A 64-bit SimHash over 4-grams: every 4-gram (each occurrence counts once) votes with the 64 bits of a fixed
    /// integer hash, and bit <c>i</c> of the result is set when more 4-grams have bit <c>i</c> set than clear.
    /// 4-grams never span two <see cref="Add"/> calls.
    /// </summary>
    public sealed class SimHash
    {
        /// <summary>Byte <c>b</c> spread over eight byte lanes: lane <c>j</c> holds bit <c>j</c> of <c>b</c>.</summary>
        private static readonly ulong[] Spread = BuildSpread();

        private readonly long[] _ones = new long[64];
        private long _grams;

        public void Add(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4) return;
            // Eight accumulators of eight byte-wide counters, one counter per hash bit; flushed before a counter can
            // overflow (255 votes).
            Span<ulong> acc = stackalloc ulong[8];
            uint gram = (uint)data[0] << 8 | (uint)data[1] << 16 | (uint)data[2] << 24;
            int pending = 0;
            for (int i = 3; i < data.Length; i++)
            {
                gram = gram >> 8 | (uint)data[i] << 24; // bytes i-3 … i as a little-endian integer
                ulong h = Hash(gram);
                acc[0] += Spread[(int)(h & 0xFF)];
                acc[1] += Spread[(int)(h >> 8 & 0xFF)];
                acc[2] += Spread[(int)(h >> 16 & 0xFF)];
                acc[3] += Spread[(int)(h >> 24 & 0xFF)];
                acc[4] += Spread[(int)(h >> 32 & 0xFF)];
                acc[5] += Spread[(int)(h >> 40 & 0xFF)];
                acc[6] += Spread[(int)(h >> 48 & 0xFF)];
                acc[7] += Spread[(int)(h >> 56)];
                if (++pending == 255) { Flush(acc); pending = 0; }
            }
            Flush(acc);
            _grams += data.Length - 3;
        }

        public ulong Value
        {
            get
            {
                ulong v = 0;
                for (int b = 0; b < 64; b++)
                    if (2 * _ones[b] > _grams) v |= 1UL << b;
                return v;
            }
        }

        /// <summary>The 4-gram's hash: the SplitMix64 output function.</summary>
        internal static ulong Hash(uint gram)
        {
            ulong z = gram + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        private void Flush(Span<ulong> acc)
        {
            for (int k = 0; k < 8; k++)
            {
                ulong a = acc[k];
                for (int j = 0; j < 8; j++) _ones[8 * k + j] += (long)(a >> (8 * j) & 0xFF);
                acc[k] = 0;
            }
        }

        private static ulong[] BuildSpread()
        {
            var t = new ulong[256];
            for (int b = 0; b < 256; b++)
                for (int j = 0; j < 8; j++)
                    if ((b >> j & 1) != 0) t[b] |= 1UL << (8 * j);
            return t;
        }
    }
}
