using System.Security.Cryptography;

namespace Osmium.Core.Tests.Native;

/// <summary>
/// The frozen inputs of the encoder vectors: integer arithmetic only, so every platform generates the same bytes.
/// Never change a generator: every vector line records its input's SHA-256, and a changed input fails as "input
/// changed". Add a new generator or name instead.
/// </summary>
public static class VectorInputs
{
    public sealed record Input(string Name, Func<byte[]> Make);

    /// <summary>The inputs of the small grid, at most 2 MiB each.</summary>
    public static readonly Input[] Small =
    [
        new("empty", () => []),
        new("one", () => [0x41]),
        new("text-4k", () => Text(4 << 10, 1)),
        new("records-64k1", () => Records((64 << 10) + 1)),
        new("mixed-256k", () => Mixed(256 << 10, 2)),
        new("random-256k", () => Random(256 << 10, 3)),
        new("mixed-1m17", () => Mixed((1 << 20) + 17, 4)),
        new("text-2m", () => Text(2 << 20, 5)),
        new("mixed-2m", () => Mixed(2 << 20, 6)),
    ];

    public static Input Get(string name) => Small.First(i => i.Name == name);

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>xorshift64*: the only source of "random" bytes here.</summary>
    public struct Rng(ulong seed)
    {
        private ulong _s = seed * 0x9E3779B97F4A7C15UL + 1;

        public ulong Next()
        {
            _s ^= _s >> 12;
            _s ^= _s << 25;
            _s ^= _s >> 27;
            return _s * 2685821657736338717UL;
        }

        public int Below(int n) => (int)(Next() % (ulong)n);
    }

    private static readonly string[] Words =
    [
        "the", "archive", "block", "of", "compression", "window", "match", "and", "literal", "Osmium", "data",
        "encoder", "a", "to", "is", "file", "folder", "level", "dictionary", "stream", "with", "for", "copy", "every",
    ];

    /// <summary>Words separated by spaces, with a line break about every 12 words.</summary>
    public static byte[] Text(int n, ulong seed)
    {
        var b = new byte[n];
        var r = new Rng(seed);
        int i = 0;
        while (i < n)
        {
            foreach (char c in Words[r.Below(Words.Length)])
            {
                if (i == n) break;
                b[i++] = (byte)c;
            }
            if (i < n) b[i++] = r.Below(12) == 0 ? (byte)'\n' : (byte)' ';
        }
        return b;
    }

    /// <summary>32-byte records: a little-endian counter, derived fields and constant padding.</summary>
    public static byte[] Records(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int rec = i / 32, k = i % 32;
            b[i] = k switch
            {
                < 4 => (byte)(rec >> (8 * k)),
                4 => (byte)(rec * 7),
                8 => (byte)(rec % 3),
                16 => 0x42,
                _ => 0,
            };
        }
        return b;
    }

    /// <summary>Bytes from xorshift64*: incompressible.</summary>
    public static byte[] Random(int n, ulong seed)
    {
        var b = new byte[n];
        var r = new Rng(seed);
        for (int i = 0; i < n; i++) b[i] = (byte)r.Next();
        return b;
    }

    /// <summary>4 KiB pieces of text, records, random bytes, zeros and a repeat of an earlier piece, in a seeded order.</summary>
    public static byte[] Mixed(int n, ulong seed)
    {
        var b = new byte[n];
        var r = new Rng(seed);
        var text = Text(64 << 10, seed + 100);
        var records = Records(64 << 10);
        int i = 0;
        while (i < n)
        {
            int len = Math.Min(4096, n - i);
            switch (r.Below(5))
            {
                case 0: Array.Copy(text, r.Below(text.Length - 4096), b, i, len); break;
                case 1: Array.Copy(records, r.Below(records.Length - 4096), b, i, len); break;
                case 2: for (int j = 0; j < len; j++) b[i + j] = (byte)r.Next(); break;
                case 3: break; // zeros
                default:
                    if (i >= 4096) Array.Copy(b, r.Below(i - 4096 + 1), b, i, len);
                    break;
            }
            i += len;
        }
        return b;
    }

    /// <summary>A 4 KiB piece of <see cref="Text"/> repeated: large and highly compressible.</summary>
    public static byte[] Repeated(int n, ulong seed)
    {
        var piece = Text(4096, seed);
        var b = new byte[n];
        for (int i = 0; i < n; i += piece.Length) Array.Copy(piece, 0, b, i, Math.Min(piece.Length, n - i));
        return b;
    }

    /// <summary>Large inputs for the large vectors and the memory tests, generated in memory: <paramref name="n"/>
    /// bytes of <see cref="Mixed"/> pieces with far repeats (every 8 MiB repeats a piece from much earlier, so long
    /// windows and long-distance matching have something to find).</summary>
    public static byte[] Large(int n, ulong seed)
    {
        var b = new byte[n];
        var r = new Rng(seed);
        const int piece = 1 << 20;
        int i = 0;
        while (i < n)
        {
            int len = Math.Min(piece, n - i);
            if (i >= 8 * piece && r.Below(4) == 0)
                Array.Copy(b, r.Below(i - 8 * piece + 1), b, i, len);
            else
                Array.Copy(Mixed(len, seed * 1000 + (ulong)(i / piece)), 0, b, i, len);
            i += len;
        }
        return b;
    }
}
