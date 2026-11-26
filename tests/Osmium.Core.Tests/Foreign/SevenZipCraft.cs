using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using Osmium.Core.Archives.Foreign.SevenZip;

namespace Osmium.Core.Tests.Foreign;

/// <summary>Writes small 7z archives with a plain header, coder by coder, for layouts the standard tools never write:
/// folders with their own salts, coders that are not connected, empty files with a checksum.</summary>
internal static class SevenZipCraft
{
    public static readonly byte[] CopyId = [0x00], AesId = [0x06, 0xF1, 0x07, 0x01];

    public sealed record Coder(byte[] Id, byte[]? Props = null, int NumIn = 1, int NumOut = 1);

    /// <summary>One folder: its coders, the packed streams, the size of every coder output, the files it holds (size
    /// and stored CRC), its bind pairs (coder input, coder output), when there is more than one packed stream the
    /// coder inputs they feed, and the CRC of the folder's whole result (which stands for its file's when it holds one).</summary>
    public sealed record Folder(Coder[] Coders, byte[][] Packed, long[] UnpackSizes, (long Size, uint? Crc)[] Files,
        (int In, int Out)[]? Binds = null, int[]? PackedIn = null, uint? UnpackCrc = null);

    public static (long, uint?) Checked(byte[] data) => (data.Length, Crc32.HashToUInt32(data));

    /// <summary>A Copy folder holding <paramref name="files"/>, each with its CRC.</summary>
    public static Folder Plain(params byte[][] files) =>
        new([new Coder(CopyId)], [Concat(files)], [Concat(files).Length], [.. files.Select(Checked)]);

    /// <summary>An AES folder over the plain bytes of <paramref name="files"/>, each with its CRC.</summary>
    public static Folder Encrypted(string password, byte[] props, params byte[][] files) =>
        new([new Coder(AesId, props)], [Encrypt(password, props, Concat(files))], [Concat(files).Length], [.. files.Select(Checked)]);

    /// <summary>7z AES properties: 2^<paramref name="power"/> rounds, a salt and a 16-byte IV.</summary>
    public static byte[] AesProps(int power, byte[] salt, byte[] iv)
    {
        if (iv.Length != 16 || salt.Length > 16) throw new ArgumentException("a 16-byte IV and at most 16 bytes of salt");
        byte b0 = (byte)(power | 0x40 | (salt.Length > 0 ? 0x80 : 0));
        byte b1 = (byte)(((salt.Length > 0 ? salt.Length - 1 : 0) << 4) | 15);
        return [b0, b1, .. salt, .. iv];
    }

    public static byte[] Encrypt(string password, byte[] props, byte[] plain)
    {
        var padded = new byte[(plain.Length + 15) / 16 * 16];
        plain.CopyTo(padded, 0);
        using var aes = Aes.Create();
        aes.Key = SevenZipAes.Key(props, password, CancellationToken.None);
        return aes.EncryptCbc(padded, SevenZipAes.Iv(props), PaddingMode.None);
    }

    /// <summary>The archive: the packed streams after the start header, then the header; files are named f0.bin, f1.bin…
    /// in folder order. With <paramref name="packHeader"/>, the header is stored packed: the folder it returns for the
    /// header's bytes (its packed streams follow the files' ones) and an encoded header that names it.</summary>
    public static byte[] Build(IReadOnlyList<Folder> folders, Func<byte[], Folder>? packHeader = null)
    {
        var packs = folders.SelectMany(f => f.Packed).ToList();
        long dataLength = packs.Sum(p => (long)p.Length);
        var h = new List<byte> { 0x01, 0x04 };
        PackAndUnpackInfo(h, folders, 0);
        h.AddRange([0x08, 0x0D]);
        foreach (var f in folders) Num(h, f.Files.Length);
        h.Add(0x09);
        foreach (var f in folders) foreach (var (size, _) in f.Files.SkipLast(1)) Num(h, size);
        // A folder of one file with a folder CRC needs no file CRC here.
        h.Add(0x0A);
        Digests(h, [.. folders.Where(f => !(f.Files.Length == 1 && f.UnpackCrc is not null)).SelectMany(f => f.Files).Select(f => f.Crc)]);
        h.AddRange([0x00, 0x00]);
        int files = folders.Sum(f => f.Files.Length);
        h.Add(0x05);
        Num(h, files);
        var names = new List<byte> { 0x00 };
        for (int i = 0; i < files; i++) names.AddRange(Encoding.Unicode.GetBytes($"f{i}.bin\0"));
        h.Add(0x11);
        Num(h, names.Count);
        h.AddRange(names);
        h.AddRange([0x00, 0x00]);
        byte[] header = [.. h];

        if (packHeader is not null)
        {
            var folder = packHeader(header);
            packs.AddRange(folder.Packed);
            var encoded = new List<byte> { 0x17 };
            PackAndUnpackInfo(encoded, [folder], dataLength);
            encoded.Add(0x00);
            header = [.. encoded];
        }
        var sig = new byte[32];
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 }.CopyTo(sig, 0);
        BinaryPrimitives.WriteInt64LittleEndian(sig.AsSpan(12), packs.Sum(p => (long)p.Length));
        BinaryPrimitives.WriteInt64LittleEndian(sig.AsSpan(20), header.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(28), Crc32.HashToUInt32(header));
        BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(8), Crc32.HashToUInt32(sig.AsSpan(12, 20)));
        return [.. sig, .. packs.SelectMany(p => p), .. header];
    }

    /// <summary>PackInfo and UnpackInfo (coders, bind pairs, sizes, folder CRCs) of <paramref name="folders"/>.</summary>
    private static void PackAndUnpackInfo(List<byte> h, IReadOnlyList<Folder> folders, long packPos)
    {
        var packs = folders.SelectMany(f => f.Packed).ToList();
        h.Add(0x06);
        Num(h, packPos);
        Num(h, packs.Count);
        h.Add(0x09);
        foreach (var p in packs) Num(h, p.Length);
        h.Add(0x00);
        h.AddRange([0x07, 0x0B]);
        Num(h, folders.Count);
        h.Add(0x00);
        foreach (var f in folders)
        {
            Num(h, f.Coders.Length);
            foreach (var c in f.Coders)
            {
                bool complex = c.NumIn != 1 || c.NumOut != 1;
                h.Add((byte)(c.Id.Length | (complex ? 0x10 : 0) | (c.Props is not null ? 0x20 : 0)));
                h.AddRange(c.Id);
                if (complex) { Num(h, c.NumIn); Num(h, c.NumOut); }
                if (c.Props is not null) { Num(h, c.Props.Length); h.AddRange(c.Props); }
            }
            foreach (var (i, o) in f.Binds ?? []) { Num(h, i); Num(h, o); }
            if (f.PackedIn is { Length: > 1 } fed) foreach (int i in fed) Num(h, i);
        }
        h.Add(0x0C);
        foreach (var f in folders) foreach (long s in f.UnpackSizes) Num(h, s);
        if (folders.Any(f => f.UnpackCrc is not null))
        {
            h.Add(0x0A);
            Digests(h, [.. folders.Select(f => f.UnpackCrc)]);
        }
        h.Add(0x00);
    }

    private static byte[] Concat(byte[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>7z digests: "all defined", or a bit vector of the defined ones; then their CRCs.</summary>
    private static void Digests(List<byte> h, List<uint?> crcs)
    {
        if (crcs.All(c => c is not null)) h.Add(1);
        else
        {
            h.Add(0);
            var bits = new byte[(crcs.Count + 7) / 8];
            for (int i = 0; i < crcs.Count; i++) if (crcs[i] is not null) bits[i / 8] |= (byte)(0x80 >> (i % 8));
            h.AddRange(bits);
        }
        foreach (var c in crcs) if (c is { } v) h.AddRange(BitConverter.GetBytes(v));
    }

    /// <summary>7z's variable-length number.</summary>
    private static void Num(List<byte> h, long v)
    {
        for (int n = 0; n < 8; n++)
            if ((ulong)v < 1UL << (7 * (n + 1)))
            {
                h.Add((byte)((0xFF << (8 - n)) & 0xFF | (int)(v >> (8 * n))));
                for (int i = 0; i < n; i++) h.Add((byte)(v >> (8 * i)));
                return;
            }
        h.Add(0xFF);
        h.AddRange(BitConverter.GetBytes(v));
    }
}
