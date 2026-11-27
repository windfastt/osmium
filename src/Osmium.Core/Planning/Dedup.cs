using System.IO.Hashing;
using System.Security.Cryptography;

namespace Osmium.Core.Planning;

/// <summary>FastCDC-style content-defined chunking with a frozen Gear table (never change: affects dedup only,
/// not correctness, but stable boundaries keep results reproducible).</summary>
public static class Cdc
{
    public const int Min = 16 << 10, Avg = 64 << 10, Max = 256 << 10;
    private static readonly ulong[] Gear = BuildGear();
    private const ulong MaskS = ((1UL << 18) - 1) << (64 - 18); // stricter before Avg
    private const ulong MaskL = ((1UL << 14) - 1) << (64 - 14); // looser after Avg

    private static ulong[] BuildGear()
    {
        var g = new ulong[256];
        ulong x = 0x4F534D49554D4344UL; // "OSMIUMCD"
        for (int i = 0; i < 256; i++)
        {
            x += 0x9E3779B97F4A7C15UL;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            g[i] = z ^ (z >> 31);
        }
        return g;
    }

    /// <summary>Length of the next chunk starting at the beginning of <paramref name="data"/>.</summary>
    public static int NextCut(ReadOnlySpan<byte> data)
    {
        int n = data.Length;
        if (n <= Min) return n;
        int end = Math.Min(n, Max);
        int normal = Math.Min(end, Avg);
        ulong h = 0;
        int i = Min;
        for (; i < normal; i++)
        {
            h = (h << 1) + Gear[data[i]];
            if ((h & MaskS) == 0) return i + 1;
        }
        for (; i < end; i++)
        {
            h = (h << 1) + Gear[data[i]];
            if ((h & MaskL) == 0) return i + 1;
        }
        return end;
    }
}

public sealed record ChunkInfo(long Offset, int Length, byte[] Sha, UInt128 Xxh);

public static class DedupScanner
{
    /// <summary>Splits a file into content-defined chunks, hashing each (SHA-256 identity + XXH128 for re-read checks).
    /// Also returns the XXH128 of the whole file. Reads the file the scan saw, or throws
    /// <see cref="SourceChangedException"/> (see <see cref="SourceIdentity.Open"/>).</summary>
    public static (List<ChunkInfo> Chunks, UInt128 FileHash) Chunk(SourceFile src, CancellationToken ct)
    {
        var chunks = new List<ChunkInfo>();
        var fileHash = new XxHash128();
        using var fs = SourceIdentity.Open(src);
        var buf = new byte[8 << 20];
        int filled = 0;
        long baseOffset = 0;
        bool eof = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            while (!eof && filled < buf.Length)
            {
                int r = fs.Read(buf, filled, buf.Length - filled);
                if (r <= 0) { eof = true; break; }
                filled += r;
            }
            if (baseOffset + filled > src.Size || eof && baseOffset + filled != src.Size) throw Changed(src);
            int pos = 0;
            while (pos < filled && (eof || filled - pos >= Cdc.Max))
            {
                int len = Cdc.NextCut(buf.AsSpan(pos, filled - pos));
                var span = buf.AsSpan(pos, len);
                chunks.Add(new ChunkInfo(baseOffset + pos, len, SHA256.HashData(span), XxHash128.HashToUInt128(span)));
                fileHash.Append(span);
                pos += len;
            }
            Buffer.BlockCopy(buf, pos, buf, 0, filled - pos);
            baseOffset += pos;
            filled -= pos;
            if (eof && filled == 0) break;
        }
        return (chunks, fileHash.GetCurrentHashAsUInt128());
    }

    /// <summary>The SHA-256 of the file the scan saw, or <see cref="SourceChangedException"/> (see
    /// <see cref="SourceIdentity.Open"/>).</summary>
    public static byte[] Sha256File(SourceFile src, CancellationToken ct)
    {
        using var fs = SourceIdentity.Open(src);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        long total = 0;
        int r;
        while ((r = fs.Read(buf)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if ((total += r) > src.Size) throw Changed(src);
            sha.AppendData(buf, 0, r);
        }
        if (total != src.Size) throw Changed(src);
        return sha.GetHashAndReset();
    }

    private static SourceChangedException Changed(SourceFile src) => new(src.FullPath, "changed while it was being read");
}
