using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Osmium.Core.Crypto;

/// <summary>
/// BLAKE2b (RFC 7693) without a key, as Argon2id uses it: digests of 1 to 64 bytes. Incremental: <see cref="Update"/>
/// any number of times, then <see cref="Final"/> once, which wipes the state. Words are read and written little-endian
/// whatever the processor.
/// </summary>
internal sealed class Blake2b
{
    public const int BlockBytes = 128;
    public const int MaxOutputBytes = 64;

    /// <summary>The initialisation vector (RFC 7693 §2.6): the first 64 bits of the fractional parts of the square
    /// roots of the first eight primes, as for SHA-512.</summary>
    private static ReadOnlySpan<ulong> IV =>
    [
        0x6A09E667F3BCC908UL, 0xBB67AE8584CAA73BUL, 0x3C6EF372FE94F82BUL, 0xA54FF53A5F1D36F1UL,
        0x510E527FADE682D1UL, 0x9B05688C2B3E6C1FUL, 0x1F83D9ABFB41BD6BUL, 0x5BE0CD19137E2179UL,
    ];

    /// <summary>The message schedule σ (RFC 7693 §2.7), ten rows; rounds 10 and 11 use rows 0 and 1 again.</summary>
    private static ReadOnlySpan<byte> Sigma =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
        11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
        7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
        9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
        2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
        12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
        13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
        6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
        10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0,
    ];

    // Pinned, so that the garbage collector never leaves a copy behind: the buffer holds message bytes (the password,
    // when Argon2id computes H0) until Final wipes it.
    private readonly ulong[] _h = GC.AllocateArray<ulong>(8, pinned: true);
    private readonly byte[] _buffer = GC.AllocateArray<byte>(BlockBytes, pinned: true);
    private int _buffered;
    private ulong _t0, _t1; // the 128-bit count of message bytes compressed so far
    private readonly int _outputLength;
    private bool _finished;

    public Blake2b(int outputLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(outputLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(outputLength, MaxOutputBytes);
        _outputLength = outputLength;
        IV.CopyTo(_h);
        // Parameter block (RFC 7693 §2.5): digest length, key length 0, fanout 1, depth 1; everything else zero.
        _h[0] ^= 0x01010000UL ^ (uint)outputLength;
    }

    public int OutputLength => _outputLength;

    public void Update(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        while (data.Length > 0)
        {
            // The last block is compressed with the final flag, so a full buffer waits until more data arrives.
            if (_buffered == BlockBytes)
            {
                AddToCount(BlockBytes);
                Compress(_buffer, last: false);
                _buffered = 0;
            }
            int n = Math.Min(BlockBytes - _buffered, data.Length);
            data[..n].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += n;
            data = data[n..];
        }
    }

    /// <summary>A 32-bit value, little-endian (Argon2's LE32).</summary>
    public void UpdateLE32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        Update(b);
    }

    /// <summary>Writes the digest (exactly <see cref="OutputLength"/> bytes) and wipes the state.</summary>
    public void Final(Span<byte> output)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (output.Length != _outputLength) throw new ArgumentException("The output must be as long as the digest.", nameof(output));
        Span<byte> full = stackalloc byte[MaxOutputBytes];
        try
        {
            AddToCount(_buffered);
            _buffer.AsSpan(_buffered).Clear();
            Compress(_buffer, last: true);
            for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt64LittleEndian(full[(8 * i)..], _h[i]);
            full[.._outputLength].CopyTo(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(full);
            CryptographicOperations.ZeroMemory(_buffer);
            Array.Clear(_h);
            _finished = true;
        }
    }

    /// <summary>The digest of <paramref name="input"/>, as long as <paramref name="output"/> (1 to 64 bytes).</summary>
    public static void Hash(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var b = new Blake2b(output.Length);
        b.Update(input);
        b.Final(output);
    }

    private void AddToCount(int n)
    {
        _t0 += (ulong)n;
        if (_t0 < (ulong)n) _t1++;
    }

    /// <summary>The compression function F (RFC 7693 §3.2): twelve rounds of G over the state and the message block.</summary>
    private void Compress(ReadOnlySpan<byte> block, bool last)
    {
        Span<ulong> m = stackalloc ulong[16];
        Span<ulong> v = stackalloc ulong[16];
        try
        {
            for (int i = 0; i < 16; i++) m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block[(8 * i)..]);
            for (int i = 0; i < 8; i++)
            {
                v[i] = _h[i];
                v[i + 8] = IV[i];
            }
            v[12] ^= _t0;
            v[13] ^= _t1;
            if (last) v[14] = ~v[14];
            var sigma = Sigma;
            for (int round = 0; round < 12; round++)
            {
                var s = sigma.Slice(16 * (round % 10), 16);
                G(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
                G(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
                G(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
                G(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
                G(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
                G(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
                G(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
                G(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
            }
            for (int i = 0; i < 8; i++) _h[i] ^= v[i] ^ v[i + 8];
        }
        finally
        {
            m.Clear();
            v.Clear();
        }
    }

    /// <summary>The mixing function G (RFC 7693 §3.1) with the rotation constants 32, 24, 16 and 63.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void G(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }
}
