using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;

namespace Osmium.Core.Tests.Foreign;

/// <summary>Writes ZIP files byte by byte, so tests can make the ones no tool writes: odd names, lying headers,
/// entries that share data, links, unknown methods, and encrypted entries with chosen passwords.</summary>
public sealed class ZipCraft
{
    public sealed class Item
    {
        public byte[] Name = [];
        public ushort Method;
        /// <summary>The bytes stored in the file (compressed and encrypted as the method and flags say).</summary>
        public byte[] Stored = [];
        public uint Crc;
        public long Size;
        public ushort Flags;
        public uint External;
        public ushort MadeBy = 20;
        public byte[] Extra = [];
        public ushort DosTime = 0x6000, DosDate = 0x58A6;
        public uint? LocalSize, LocalCrc;
        /// <summary>Use the local header and data of this earlier item instead of writing new ones.</summary>
        public int? SameDataAs;
    }

    public List<Item> Items { get; } = [];
    public long? DeclaredCount;
    public byte[] Comment = [];
    public ushort Disk;

    public static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) d.Write(data);
        return ms.ToArray();
    }

    public Item Add(string name, byte[] data, bool deflate = true, Encoding? encoding = null, ushort flags = 0)
    {
        var item = new Item
        {
            Name = (encoding ?? Encoding.UTF8).GetBytes(name),
            Method = (ushort)(deflate ? 8 : 0),
            Stored = deflate ? Deflate(data) : data,
            Crc = Crc32.HashToUInt32(data),
            Size = data.Length,
            Flags = (ushort)(flags | (encoding is null ? 0x800 : 0)),
        };
        Items.Add(item);
        return item;
    }

    public byte[] Build()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var offsets = new long[Items.Count];
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            if (it.SameDataAs is { } other) { offsets[i] = offsets[other]; continue; }
            offsets[i] = ms.Position;
            w.Write(0x04034b50u);
            w.Write((ushort)20); w.Write(it.Flags); w.Write(it.Method); w.Write(it.DosTime); w.Write(it.DosDate);
            w.Write(it.LocalCrc ?? it.Crc); w.Write((uint)it.Stored.Length); w.Write(it.LocalSize ?? (uint)it.Size);
            w.Write((ushort)it.Name.Length); w.Write((ushort)it.Extra.Length);
            w.Write(it.Name); w.Write(it.Extra); w.Write(it.Stored);
        }
        long cdStart = ms.Position;
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            w.Write(0x02014b50u);
            w.Write(it.MadeBy); w.Write((ushort)20); w.Write(it.Flags); w.Write(it.Method); w.Write(it.DosTime); w.Write(it.DosDate);
            w.Write(it.Crc); w.Write((uint)it.Stored.Length); w.Write((uint)it.Size);
            w.Write((ushort)it.Name.Length); w.Write((ushort)it.Extra.Length); w.Write((ushort)0);
            w.Write((ushort)0); w.Write((ushort)0); w.Write(it.External); w.Write((uint)offsets[i]);
            w.Write(it.Name); w.Write(it.Extra);
        }
        long cdSize = ms.Position - cdStart;
        long declared = DeclaredCount ?? Items.Count;
        if (declared > 0xFFFF)
        {
            // ZIP64 end record and locator, which carry the full count.
            long at = ms.Position;
            w.Write(0x06064b50u); w.Write(44L); w.Write((ushort)45); w.Write((ushort)45);
            w.Write((uint)Disk); w.Write((uint)Disk); w.Write(declared); w.Write(declared); w.Write(cdSize); w.Write(cdStart);
            w.Write(0x07064b50u); w.Write(0u); w.Write(at); w.Write(1u);
        }
        ushort count = (ushort)Math.Min(0xFFFF, declared);
        w.Write(0x06054b50u);
        w.Write(Disk); w.Write(Disk); w.Write(count); w.Write(count);
        w.Write((uint)cdSize); w.Write((uint)cdStart); w.Write((ushort)Comment.Length); w.Write(Comment);
        return ms.ToArray();
    }

    // ------------------------------------------------------------ encryption, written here for tests

    /// <summary>ZipCrypto: the 12-byte header (its last byte the CRC's high byte) and the data, encrypted.</summary>
    public static byte[] ZipCryptoEncrypt(byte[] password, byte[] stored, uint crc, int seed = 1)
    {
        var keys = new ZipCryptoKeys(password);
        var header = new byte[12];
        new Random(seed).NextBytes(header);
        header[11] = (byte)(crc >> 24);
        return [.. keys.Encrypt(header), .. keys.Encrypt(stored)];
    }

    /// <summary>Whether <paramref name="password"/> passes the one-byte check of an entry encrypted as above.</summary>
    public static bool PassesCheckByte(byte[] password, byte[] encrypted, uint crc)
    {
        var keys = new ZipCryptoKeys(password);
        return keys.Decrypt(encrypted.AsSpan(0, 12).ToArray())[11] == (byte)(crc >> 24);
    }

    private sealed class ZipCryptoKeys
    {
        private uint _k0 = 0x12345678, _k1 = 0x23456789, _k2 = 0x34567890;

        public ZipCryptoKeys(byte[] password)
        {
            foreach (byte b in password) Update(b);
        }

        private static uint Crc(uint crc, byte b)
        {
            uint c = (crc ^ b) & 0xFF;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c ^ (crc >> 8);
        }

        private void Update(byte plain)
        {
            _k0 = Crc(_k0, plain);
            _k1 = (_k1 + (_k0 & 0xFF)) * 134775813 + 1;
            _k2 = Crc(_k2, (byte)(_k1 >> 24));
        }

        private byte Key()
        {
            uint t = (_k2 | 2) & 0xFFFF;
            return (byte)((t * (t ^ 1)) >> 8);
        }

        public byte[] Encrypt(byte[] data)
        {
            var o = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) { o[i] = (byte)(data[i] ^ Key()); Update(data[i]); }
            return o;
        }

        public byte[] Decrypt(byte[] data)
        {
            var o = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) { o[i] = (byte)(data[i] ^ Key()); Update(o[i]); }
            return o;
        }
    }

    /// <summary>An AES-256 entry (AE-2 unless <paramref name="version"/> is 1): salt, verifier, ciphertext, code.</summary>
    public static byte[] AesEncrypt(string password, byte[] stored, int version = 2, int seed = 1)
    {
        var salt = new byte[16];
        new Random(seed).NextBytes(salt);
        var bytes = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 1000, HashAlgorithmName.SHA1, 66);
        using var aes = Aes.Create();
        aes.Key = bytes[..32];
        var cipher = new byte[stored.Length];
        var counter = new byte[16];
        var block = new byte[16];
        for (int i = 0; i < stored.Length; i += 16)
        {
            for (int k = 0; k < 16 && ++counter[k] == 0; k++) { }
            aes.EncryptEcb(counter, block, PaddingMode.None);
            for (int j = 0; j < 16 && i + j < stored.Length; j++) cipher[i + j] = (byte)(stored[i + j] ^ block[j]);
        }
        var mac = HMACSHA1.HashData(bytes[32..64], cipher);
        return [.. salt, .. bytes[64..66], .. cipher, .. mac[..10]];
    }

    /// <summary>The 0x9901 extra field of an AES-256 entry whose real method is <paramref name="method"/>.</summary>
    public static byte[] AesExtra(ushort method, int version = 2)
    {
        var e = new byte[11];
        BinaryPrimitives.WriteUInt16LittleEndian(e, 0x9901);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(4), (ushort)version);
        e[6] = (byte)'A'; e[7] = (byte)'E'; e[8] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(9), method);
        return e;
    }

    /// <summary>Adds an AES-256 encrypted, deflated entry.</summary>
    public Item AddAes(string name, byte[] data, string password, int version = 2)
    {
        var item = new Item
        {
            Name = Encoding.UTF8.GetBytes(name),
            Method = 99,
            Stored = AesEncrypt(password, Deflate(data), version),
            Crc = version == 1 ? Crc32.HashToUInt32(data) : 0,
            Size = data.Length,
            Flags = 0x801,
            Extra = AesExtra(8, version),
        };
        Items.Add(item);
        return item;
    }

    /// <summary>Adds a ZipCrypto encrypted, deflated entry.</summary>
    public Item AddZipCrypto(string name, byte[] data, byte[] password)
    {
        uint crc = Crc32.HashToUInt32(data);
        var item = new Item
        {
            Name = Encoding.UTF8.GetBytes(name),
            Method = 8,
            Stored = ZipCryptoEncrypt(password, Deflate(data), crc),
            Crc = crc,
            Size = data.Length,
            Flags = 0x801,
        };
        Items.Add(item);
        return item;
    }
}
