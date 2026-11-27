using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Osmium.Core.Crypto;

/// <summary>
/// Argon2id, version 0x13 (RFC 9106 §3), bit for bit as the specification defines it. The work area (m KiB) is one
/// native allocation, made only after every argument has been checked; it is wiped and freed before <see cref="Hash"/>
/// returns or throws, also on cancellation. The limits an archive may ask for are not checked here but in
/// <see cref="KdfLimits"/>, before this is called.
/// </summary>
internal static unsafe class Argon2id
{
    public const int Version = 0x13;
    /// <summary>The type y: 0 Argon2d, 1 Argon2i, 2 Argon2id.</summary>
    private const int Type = 2;
    public const int MaxLanes = (1 << 24) - 1;
    private const int BlockWords = 128;           // a block is 1 KiB: 128 64-bit words
    private const int BlockBytes = BlockWords * 8;
    private const int SyncPoints = 4;             // slices per pass
    /// <summary>The largest span the work area is wiped in at once (a span's length is an int).</summary>
    private const long WipeChunk = 1L << 30;

    private static readonly AsyncLocal<Action<long>?> s_allocating = new();
    private static readonly AsyncLocal<AreaInspector?> s_beforeFree = new();

    /// <summary>Tests: called with the size in bytes of the work area just before it is allocated.</summary>
    internal static Action<long>? Allocating { get => s_allocating.Value; set => s_allocating.Value = value; }

    /// <summary>The work area's address and its length in 64-bit words.</summary>
    internal delegate void AreaInspector(nint area, long words);

    /// <summary>Tests: called with the work area after it was wiped and before it is freed.</summary>
    internal static AreaInspector? BeforeFree { get => s_beforeFree.Value; set => s_beforeFree.Value = value; }

    /// <summary>The bytes the work area of these parameters takes: m′ = 4·p·⌊m / 4p⌋ blocks of 1 KiB.</summary>
    public static long WorkAreaBytes(int memoryKiB, int lanes) => memoryKiB / (SyncPoints * (long)lanes) * SyncPoints * lanes * BlockBytes;

    /// <summary>
    /// Computes the Argon2id tag of <paramref name="password"/> into <paramref name="tag"/> (its length is the tag
    /// length T). Lanes of a slice run on up to <paramref name="threads"/> threads; the result does not depend on how
    /// many. <paramref name="ct"/> is checked between slices; <paramref name="progress"/> gets the share done after
    /// each slice.
    /// </summary>
    public static void Hash(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> associatedData,
        int timeCost, int memoryKiB, int lanes, Span<byte> tag, int threads, CancellationToken ct, Action<double>? progress = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeCost, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lanes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lanes, MaxLanes);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryKiB, 8 * lanes);
        if (tag.Length < 4) throw new ArgumentOutOfRangeException(nameof(tag), "The tag is at least 4 bytes long.");
        ct.ThrowIfCancellationRequested();

        long segmentLength = memoryKiB / (SyncPoints * (long)lanes);
        long laneLength = segmentLength * SyncPoints;
        long words = laneLength * lanes * BlockWords;
        long bytes = words * 8;
        threads = Math.Clamp(threads, 1, lanes);

        Span<byte> h0 = stackalloc byte[64 + 8]; // H0, then LE32(column) and LE32(lane) for H′ of the first blocks
        Span<byte> blockBytes = stackalloc byte[BlockBytes];
        Span<ulong> final = stackalloc ulong[BlockWords];
        ulong* area = null;
        try
        {
            // H0 (RFC 9106 §3.2, step 1): every parameter and input, each length as LE32, in this order.
            var b = new Blake2b(64);
            b.UpdateLE32((uint)lanes);
            b.UpdateLE32((uint)tag.Length);
            b.UpdateLE32((uint)memoryKiB);
            b.UpdateLE32((uint)timeCost);
            b.UpdateLE32(Version);
            b.UpdateLE32(Type);
            b.UpdateLE32((uint)password.Length);
            b.Update(password);
            b.UpdateLE32((uint)salt.Length);
            b.Update(salt);
            b.UpdateLE32((uint)secret.Length);
            b.Update(secret);
            b.UpdateLE32((uint)associatedData.Length);
            b.Update(associatedData);
            b.Final(h0[..64]);

            Allocating?.Invoke(bytes);
            try
            {
                area = (ulong*)NativeMemory.AlignedAlloc(checked((nuint)bytes), 64);
            }
            catch (Exception e) when (e is OutOfMemoryException or OverflowException)
            {
                throw new WorkAreaUnavailableException(bytes);
            }

            // Steps 3 and 4: the first two blocks of every lane, B[i][0] = H′(H0 ‖ LE32(0) ‖ LE32(i)), B[i][1] likewise.
            for (int lane = 0; lane < lanes; lane++)
                for (int column = 0; column < 2; column++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(h0[64..], (uint)column);
                    BinaryPrimitives.WriteUInt32LittleEndian(h0[68..], (uint)lane);
                    HPrime(h0, blockBytes);
                    ulong* dst = area + (lane * laneLength + column) * BlockWords;
                    for (int w = 0; w < BlockWords; w++) dst[w] = BinaryPrimitives.ReadUInt64LittleEndian(blockBytes[(8 * w)..]);
                }

            // Steps 5 and 6: the passes, slice by slice; the lanes of one slice are independent of each other.
            nint address = (nint)area;
            for (int pass = 0; pass < timeCost; pass++)
                for (int slice = 0; slice < SyncPoints; slice++)
                {
                    ct.ThrowIfCancellationRequested();
                    var s = new Segment(pass, slice, lanes, laneLength, segmentLength, timeCost);
                    if (threads == 1)
                        for (int lane = 0; lane < lanes; lane++) FillSegment((ulong*)address, s, lane);
                    else
                        Parallel.For(0, lanes, new ParallelOptions { MaxDegreeOfParallelism = threads }, lane => FillSegment((ulong*)address, s, lane));
                    progress?.Invoke((pass * SyncPoints + slice + 1) / (double)(SyncPoints * timeCost));
                }

            // Step 7: C = XOR of the last block of every lane; the tag is H′^T(C).
            new ReadOnlySpan<ulong>(area + (laneLength - 1) * BlockWords, BlockWords).CopyTo(final);
            for (int lane = 1; lane < lanes; lane++)
            {
                ulong* last = area + (lane * laneLength + laneLength - 1) * BlockWords;
                for (int w = 0; w < BlockWords; w++) final[w] ^= last[w];
            }
            for (int w = 0; w < BlockWords; w++) BinaryPrimitives.WriteUInt64LittleEndian(blockBytes[(8 * w)..], final[w]);
            HPrime(blockBytes, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(h0);
            CryptographicOperations.ZeroMemory(blockBytes);
            final.Clear();
            if (area is not null)
            {
                for (long done = 0; done < bytes; done += WipeChunk)
                    CryptographicOperations.ZeroMemory(new Span<byte>((byte*)area + done, (int)Math.Min(WipeChunk, bytes - done)));
                BeforeFree?.Invoke((nint)area, words);
                NativeMemory.AlignedFree(area);
            }
        }
    }

    /// <summary>One segment: a slice of one pass, in one lane.</summary>
    private readonly record struct Segment(int Pass, int Slice, int Lanes, long LaneLength, long SegmentLength, int Passes);

    /// <summary>
    /// Computes the blocks of one segment (RFC 9106 §3.4). Argon2id takes reference indexes from an address block
    /// (data-independent) in the first half of pass 0 and from the previous block (data-dependent) after that.
    /// Compiled fully optimised at once: a derivation runs once per open, too briefly for the runtime to tier it up.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void FillSegment(ulong* memory, Segment s, int lane)
    {
        bool independent = s.Pass == 0 && s.Slice < SyncPoints / 2;
        ulong* addresses = stackalloc ulong[BlockWords];
        ulong* input = stackalloc ulong[BlockWords];
        ulong* zero = stackalloc ulong[BlockWords];
        ulong* r = stackalloc ulong[BlockWords];
        ulong* t = stackalloc ulong[BlockWords];
        try
        {
            long start = 0;
            if (independent)
            {
                new Span<ulong>(input, BlockWords).Clear();
                new Span<ulong>(zero, BlockWords).Clear();
                // The input block of the address generator (§3.4.1.2): r, l, sl, m′, t, y, and a counter i.
                input[0] = (ulong)s.Pass;
                input[1] = (ulong)lane;
                input[2] = (ulong)s.Slice;
                input[3] = (ulong)(s.LaneLength * s.Lanes);
                input[4] = (ulong)s.Passes;
                input[5] = Type;
            }
            if (s.Pass == 0 && s.Slice == 0)
            {
                start = 2; // the first two blocks of a lane come from H0
                if (independent) NextAddresses(addresses, input, zero, r, t);
            }
            long laneStart = lane * s.LaneLength;
            for (long i = start; i < s.SegmentLength; i++)
            {
                long column = s.Slice * s.SegmentLength + i;
                ulong* current = memory + (laneStart + column) * BlockWords;
                ulong* previous = memory + (laneStart + (column == 0 ? s.LaneLength - 1 : column - 1)) * BlockWords;
                ulong pseudoRandom;
                if (independent)
                {
                    if (i % BlockWords == 0) NextAddresses(addresses, input, zero, r, t);
                    pseudoRandom = addresses[i % BlockWords];
                }
                else pseudoRandom = previous[0];
                // J2 picks the lane (§3.4.1.1): the lane itself in the first slice of the first pass.
                int refLane = s.Pass == 0 && s.Slice == 0 ? lane : (int)((pseudoRandom >> 32) % (ulong)s.Lanes);
                long refColumn = ReferenceColumn(s, i, (uint)pseudoRandom, refLane == lane);
                ulong* reference = memory + (refLane * s.LaneLength + refColumn) * BlockWords;
                Compress(previous, reference, current, xorInto: s.Pass > 0, r, t);
            }
        }
        finally
        {
            new Span<ulong>(addresses, BlockWords).Clear();
            new Span<ulong>(input, BlockWords).Clear();
            new Span<ulong>(r, BlockWords).Clear();
            new Span<ulong>(t, BlockWords).Clear();
        }
    }

    /// <summary>The next 128 pseudo-random values: G(0, G(0, input)) with the counter in input[6] raised first.</summary>
    private static void NextAddresses(ulong* addresses, ulong* input, ulong* zero, ulong* r, ulong* t)
    {
        input[6]++;
        Compress(zero, input, addresses, xorInto: false, r, t);
        Compress(zero, addresses, addresses, xorInto: false, r, t);
    }

    /// <summary>
    /// The column of the reference block in its lane (§3.4.1.3, §3.4.2): the reference set W is every block of the
    /// lane computed so far that may be used (the last three finished segments, and in the current lane the blocks of
    /// the current segment before the previous one; a block that starts a segment cannot refer to the last block of
    /// another lane), and J1 is mapped onto it non-uniformly, favouring recent blocks.
    /// </summary>
    private static long ReferenceColumn(in Segment s, long index, uint j1, bool sameLane)
    {
        long size;
        if (s.Pass == 0)
            size = s.Slice == 0 ? index - 1
                : sameLane ? s.Slice * s.SegmentLength + index - 1
                : s.Slice * s.SegmentLength - (index == 0 ? 1 : 0);
        else
            size = sameLane ? s.LaneLength - s.SegmentLength + index - 1
                : s.LaneLength - s.SegmentLength - (index == 0 ? 1 : 0);
        ulong x = (ulong)j1 * j1 >> 32;
        ulong y = (ulong)size * x >> 32;
        ulong z = (ulong)size - 1 - y;
        // W starts after the current segment in later passes (the lane wraps around), at column 0 in pass 0.
        long first = s.Pass == 0 || s.Slice == SyncPoints - 1 ? 0 : (s.Slice + 1) * s.SegmentLength;
        return (long)(((ulong)first + z) % (ulong)s.LaneLength);
    }

    /// <summary>
    /// The compression function G (§3.5): R = X ⊕ Y; Q = P applied to the rows of R, then to its columns; the result
    /// is Q ⊕ R, XORed into the block that is there from the second pass on (version 0x13).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Compress(ulong* x, ulong* y, ulong* result, bool xorInto, ulong* r, ulong* t)
    {
        for (int i = 0; i < BlockWords; i++)
        {
            ulong v = x[i] ^ y[i];
            r[i] = v;
            t[i] = xorInto ? v ^ result[i] : v;
        }
        for (int i = 0; i < 8; i++) Permute(r + 16 * i, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);       // rows
        for (int i = 0; i < 8; i++) Permute(r + 2 * i, 0, 1, 16, 17, 32, 33, 48, 49, 64, 65, 80, 81, 96, 97, 112, 113); // columns
        for (int i = 0; i < BlockWords; i++) result[i] = t[i] ^ r[i];
    }

    /// <summary>The permutation P (§3.6) on sixteen 64-bit words, given by their offsets from <paramref name="b"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Permute(ulong* b, int i0, int i1, int i2, int i3, int i4, int i5, int i6, int i7,
        int i8, int i9, int i10, int i11, int i12, int i13, int i14, int i15)
    {
        ulong v0 = b[i0], v1 = b[i1], v2 = b[i2], v3 = b[i3], v4 = b[i4], v5 = b[i5], v6 = b[i6], v7 = b[i7];
        ulong v8 = b[i8], v9 = b[i9], v10 = b[i10], v11 = b[i11], v12 = b[i12], v13 = b[i13], v14 = b[i14], v15 = b[i15];
        GB(ref v0, ref v4, ref v8, ref v12);
        GB(ref v1, ref v5, ref v9, ref v13);
        GB(ref v2, ref v6, ref v10, ref v14);
        GB(ref v3, ref v7, ref v11, ref v15);
        GB(ref v0, ref v5, ref v10, ref v15);
        GB(ref v1, ref v6, ref v11, ref v12);
        GB(ref v2, ref v7, ref v8, ref v13);
        GB(ref v3, ref v4, ref v9, ref v14);
        b[i0] = v0; b[i1] = v1; b[i2] = v2; b[i3] = v3; b[i4] = v4; b[i5] = v5; b[i6] = v6; b[i7] = v7;
        b[i8] = v8; b[i9] = v9; b[i10] = v10; b[i11] = v11; b[i12] = v12; b[i13] = v13; b[i14] = v14; b[i15] = v15;
    }

    /// <summary>GB (§3.6): BLAKE2b's G with each addition a + b replaced by a + b + 2·trunc(a)·trunc(b).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GB(ref ulong a, ref ulong b, ref ulong c, ref ulong d)
    {
        a = a + b + 2 * ((a & 0xFFFFFFFFUL) * (b & 0xFFFFFFFFUL));
        d = BitOperations.RotateRight(d ^ a, 32);
        c = c + d + 2 * ((c & 0xFFFFFFFFUL) * (d & 0xFFFFFFFFUL));
        b = BitOperations.RotateRight(b ^ c, 24);
        a = a + b + 2 * ((a & 0xFFFFFFFFUL) * (b & 0xFFFFFFFFUL));
        d = BitOperations.RotateRight(d ^ a, 16);
        c = c + d + 2 * ((c & 0xFFFFFFFFUL) * (d & 0xFFFFFFFFUL));
        b = BitOperations.RotateRight(b ^ c, 63);
    }

    /// <summary>
    /// The variable-length hash H′ (§3.3): BLAKE2b of LE32(T) ‖ X for T ≤ 64 bytes; for longer outputs, a chain of
    /// 64-byte digests of which the first 32 bytes each are used, and a last digest of the remaining length.
    /// </summary>
    private static void HPrime(ReadOnlySpan<byte> x, Span<byte> output)
    {
        int length = output.Length;
        if (length <= Blake2b.MaxOutputBytes)
        {
            var b = new Blake2b(length);
            b.UpdateLE32((uint)length);
            b.Update(x);
            b.Final(output);
            return;
        }
        Span<byte> v = stackalloc byte[64];
        try
        {
            var first = new Blake2b(64);
            first.UpdateLE32((uint)length);
            first.Update(x);
            first.Final(v);
            v[..32].CopyTo(output);
            int at = 32;
            while (length - at > 64)
            {
                Blake2b.Hash(v, v);
                v[..32].CopyTo(output[at..]);
                at += 32;
            }
            Blake2b.Hash(v, output[at..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(v);
        }
    }
}

/// <summary>The system could not give the work area (<see cref="Bytes"/> long); thrown only by its allocation.</summary>
internal sealed class WorkAreaUnavailableException(long bytes) : OutOfMemoryException($"The key derivation could not get {bytes} bytes of memory.")
{
    public long Bytes { get; } = bytes;
}
