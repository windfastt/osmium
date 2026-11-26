using System.Text;
using Osmium.Core.Archives.Foreign.Compression;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Archives.Foreign;

/// <summary>An archive's container and, for TAR and single files, the compression around it.</summary>
internal readonly record struct SniffResult(ArchiveKind Kind, StreamCompression Compression);

/// <summary>
/// Recognises an archive by its content: the first 4 KiB and the last 32 bytes, and for a compressed file the first
/// 512 decoded bytes (decoded in memory, after the memory pre-checks). An .osa is recognised by its magic or by a valid
/// footer (so one with a damaged header still opens from its header copy); the file name only breaks one tie, a TAR
/// in the old format without the "ustar" mark.
/// </summary>
internal static class FormatSniffer
{
    public const int HeadBytes = 4096;

    public static SniffResult? Sniff(string path, ImportLimits limits)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        long length = RandomAccess.GetLength(handle);
        var head = new byte[(int)Math.Min(HeadBytes, length)];
        int n = head.Length == 0 ? 0 : RandomAccess.Read(handle, head, 0);
        var h = head.AsSpan(0, n);
        string name = Path.GetFileName(path);

        // A valid footer also routes an .osa whose header is damaged to OsaReader, which falls back to the header copy.
        if (h.StartsWith(Osa.Magic) || HasOsaFooter(handle, length))
            return new SniffResult(ArchiveKind.Osa, StreamCompression.None);
        if (h.StartsWith((ReadOnlySpan<byte>)[0x50, 0x4B, 0x03, 0x04]) || h.StartsWith((ReadOnlySpan<byte>)[0x50, 0x4B, 0x05, 0x06]))
            return new SniffResult(ArchiveKind.Zip, StreamCompression.None);
        if (h.StartsWith((ReadOnlySpan<byte>)[0x50, 0x4B, 0x07, 0x08]))
            throw new NotSupportedException("Multi-volume ZIP archives are not supported.");
        if (h.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
            return new SniffResult(ArchiveKind.SevenZip, StreamCompression.None);
        if (h.StartsWith((ReadOnlySpan<byte>)[0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]) || h.StartsWith((ReadOnlySpan<byte>)[0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]))
            throw new NotSupportedException("RAR archives are not supported by this version of Osmium.");

        StreamCompression? compression =
            h.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]) ? StreamCompression.GZip
            : h.Length >= 4 && h.StartsWith("BZh"u8) && h[3] is >= (byte)'1' and <= (byte)'9' ? StreamCompression.BZip2
            : h.StartsWith((ReadOnlySpan<byte>)[0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]) ? StreamCompression.Xz
            : h.StartsWith((ReadOnlySpan<byte>)[0x28, 0xB5, 0x2F, 0xFD]) ? StreamCompression.Zstd
            : null;
        if (compression is { } c)
        {
            byte[] decoded;
            try
            {
                using var slice = new FileSlice(handle, 0, length);
                decoded = StreamCodecs.Peek(c, slice, 512, limits, name);
            }
            catch (Exception e) when (ForeignErrors.IsDecoderFailure(e) || e is NotSupportedException)
            {
                decoded = []; // undecodable or too large to sniff: opened as a single compressed file, which says why
            }
            return new SniffResult(IsTarHeader(decoded, name, requireMagic: true) ? ArchiveKind.Tar : KindOf(c), c);
        }
        if (IsTarHeader(h, name, requireMagic: !name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)))
            return new SniffResult(ArchiveKind.Tar, StreamCompression.None);
        return null;
    }

    private static ArchiveKind KindOf(StreamCompression c) => c switch
    {
        StreamCompression.GZip => ArchiveKind.GZip,
        StreamCompression.BZip2 => ArchiveKind.BZip2,
        StreamCompression.Xz => ArchiveKind.Xz,
        _ => ArchiveKind.Zstd,
    };

    private static bool HasOsaFooter(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long length)
    {
        if (length < Osa.FooterSize + 36) return false;
        try
        {
            using var slice = new FileSlice(handle, 0, length, 64);
            ArchiveFooter.Read(slice);
            return true;
        }
        catch (CorruptDataException) { return false; }
    }

    /// <summary>A TAR header: "ustar" at offset 257 (POSIX and GNU), or, when <paramref name="requireMagic"/> is
    /// false, an old-style header whose checksum adds up.</summary>
    internal static bool IsTarHeader(ReadOnlySpan<byte> block, string name, bool requireMagic)
    {
        if (block.Length < 512) return false;
        if (block.Slice(257, 5).SequenceEqual("ustar"u8)) return true;
        if (requireMagic) return false;
        if (block[0] == 0) return false;
        return Tar.TarHeaderReader.ChecksumMatches(block[..512]);
    }
}
