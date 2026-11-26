using System.Buffers.Binary;
using System.Security.Cryptography;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Archives.Foreign.Zip;

/// <summary>
/// The traditional ZIP encryption ("ZipCrypto"), decryption only. Its 12-byte header ends with one check byte, which
/// a wrong password passes once in 256 tries: callers always decode the whole entry and compare its CRC-32 too.
/// </summary>
internal sealed class ZipCryptoStream : ReadOnlyStream
{
    private static readonly uint[] CrcTable = MakeTable();
    private readonly Stream _inner;
    private uint _k0 = 0x12345678, _k1 = 0x23456789, _k2 = 0x34567890;

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>Reads and decrypts the 12-byte header; <see cref="CheckByteMatches"/> says whether its last byte is
    /// <paramref name="checkByte"/>.</summary>
    public ZipCryptoStream(Stream inner, ReadOnlySpan<byte> password, byte checkByte)
    {
        _inner = inner;
        foreach (byte b in password) Update(b);
        Span<byte> header = stackalloc byte[12];
        inner.ReadExactly(header);
        Decrypt(header);
        CheckByteMatches = header[11] == checkByte;
    }

    public bool CheckByteMatches { get; }

    private static uint Crc(uint crc, byte b) => CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

    private void Update(byte plain)
    {
        _k0 = Crc(_k0, plain);
        _k1 = (_k1 + (_k0 & 0xFF)) * 134775813 + 1;
        _k2 = Crc(_k2, (byte)(_k1 >> 24));
    }

    private void Decrypt(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            uint t = (_k2 | 2) & 0xFFFF;
            byte plain = (byte)(data[i] ^ (byte)((t * (t ^ 1)) >> 8));
            Update(plain);
            data[i] = plain;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        int n = _inner.Read(buffer);
        Decrypt(buffer[..n]);
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// AES-encrypted ZIP entries (method 99 with the 0x9901 extra field, AE-1 and AE-2), decrypted and authenticated by
/// Osmium itself: SharpCompress reads the 10-byte authentication code and never compares it. Layout: salt (8, 12 or
/// 16 bytes for AES-128, 192 or 256), a 2-byte password verifier, the ciphertext (AES in counter mode, little-endian
/// counter from 1), and the first 10 bytes of HMAC-SHA1 over the ciphertext. Keys come from PBKDF2-HMAC-SHA1 with
/// 1,000 iterations over the password as UTF-8.
/// </summary>
internal sealed class WinZipAes
{
    public const int CodeLength = 10;

    public static int SaltLength(int strength) => strength switch
    {
        1 => 8,
        2 => 12,
        3 => 16,
        _ => throw new CorruptDataException("damaged data (unknown AES key length)"),
    };

    public static int KeyLength(int strength) => SaltLength(strength) * 2;

    /// <summary>The encryption key, the authentication key and the password verifier.</summary>
    public static (byte[] Key, byte[] MacKey, byte[] Verifier) Derive(string password, ReadOnlySpan<byte> salt, int strength)
    {
        int keyLength = KeyLength(strength);
        var bytes = Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(password), salt, 1000, HashAlgorithmName.SHA1, 2 * keyLength + 2);
        return (bytes[..keyLength], bytes[keyLength..(2 * keyLength)], bytes[(2 * keyLength)..]);
    }

    /// <summary>Reads the salt and verifier at the start of <paramref name="entry"/> (positioned at the entry's data);
    /// null when the verifier does not match.</summary>
    public static (byte[] Key, byte[] MacKey)? Keys(Stream entry, string password, int strength)
    {
        var salt = new byte[SaltLength(strength)];
        entry.ReadExactly(salt);
        Span<byte> verifier = stackalloc byte[2];
        entry.ReadExactly(verifier);
        var (key, mac, expected) = Derive(password, salt, strength);
        return verifier.SequenceEqual(expected) ? (key, mac) : null;
    }

    /// <summary>The overhead of an encrypted entry around its ciphertext.</summary>
    public static long Overhead(int strength) => SaltLength(strength) + 2 + CodeLength;

    /// <summary>Whether the code stored after the ciphertext matches HMAC-SHA1 over it, read once, without decrypting
    /// (the password check). <paramref name="entry"/> is positioned after the verifier.</summary>
    public static bool Authentic(Stream entry, byte[] macKey, long cipherLength, CancellationToken ct)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, macKey);
        var buf = new byte[1 << 16];
        long left = cipherLength;
        while (left > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = entry.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (n <= 0) throw new CorruptDataException("damaged data (the entry ends early)");
            hmac.AppendData(buf, 0, n);
            left -= n;
        }
        Span<byte> stored = stackalloc byte[CodeLength];
        entry.ReadExactly(stored);
        return CryptographicOperations.FixedTimeEquals(hmac.GetHashAndReset().AsSpan(0, CodeLength), stored);
    }
}

/// <summary>Decrypts an AES-encrypted ZIP entry and, once the ciphertext has been read to its end, compares the
/// authentication code in constant time; a mismatch throws before the reader sees the end of the data.</summary>
internal sealed class WinZipAesStream : ReadOnlyStream
{
    private readonly Stream _inner;
    private readonly Aes _aes;
    private readonly IncrementalHash _hmac;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _keystream = new byte[16 * 1024];
    private int _keyPos = 16 * 1024;
    private long _left;
    private bool _checked;

    /// <param name="inner">The entry's data, positioned after the salt and verifier.</param>
    public WinZipAesStream(Stream inner, byte[] key, byte[] macKey, long cipherLength)
    {
        _inner = inner;
        _aes = Aes.Create();
        _aes.Key = key;
        _hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, macKey);
        _left = cipherLength;
    }

    public override int Read(Span<byte> buffer)
    {
        if (_left == 0)
        {
            Finish();
            return 0;
        }
        if (buffer.Length > _left) buffer = buffer[..(int)_left];
        int n = _inner.Read(buffer);
        if (n <= 0) throw new CorruptDataException("damaged data (the entry ends early)");
        _hmac.AppendData(buffer[..n]);
        for (int i = 0; i < n; i++)
        {
            if (_keyPos == _keystream.Length) NextKeystream();
            buffer[i] ^= _keystream[_keyPos++];
        }
        _left -= n;
        return n;
    }

    /// <summary>Reads what is left of the ciphertext and checks the authentication code (also when a decoder stopped
    /// before the end).</summary>
    public void Finish()
    {
        if (_checked) return;
        var scratch = new byte[1 << 16];
        while (_left > 0)
        {
            int n = _inner.Read(scratch, 0, (int)Math.Min(scratch.Length, _left));
            if (n <= 0) throw new CorruptDataException("damaged data (the entry ends early)");
            _hmac.AppendData(scratch, 0, n);
            _left -= n;
        }
        Span<byte> stored = stackalloc byte[WinZipAes.CodeLength];
        _inner.ReadExactly(stored);
        _checked = true;
        if (!CryptographicOperations.FixedTimeEquals(_hmac.GetHashAndReset().AsSpan(0, WinZipAes.CodeLength), stored))
            throw new CorruptDataException("damaged data (wrong password or damaged file: the authentication code does not match)");
    }

    private void NextKeystream()
    {
        // Counter blocks for 1,024 AES blocks at once, then one ECB pass over them.
        for (int b = 0; b < _keystream.Length; b += 16)
        {
            for (int i = 0; i < 16 && ++_counter[i] == 0; i++) { }
            _counter.CopyTo(_keystream, b);
        }
        _aes.EncryptEcb(_keystream, _keystream, PaddingMode.None);
        _keyPos = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _aes.Dispose();
            _hmac.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>A stream that runs <paramref name="atEnd"/> when <paramref name="inner"/> ends (the AES code check after
    /// the decoder's end).</summary>
    public static Stream CheckAtEnd(Stream inner, Action atEnd) => new EndCheck(inner, atEnd);

    private sealed class EndCheck(Stream inner, Action atEnd) : ReadOnlyStream
    {
        private bool _done;

        public override int Read(Span<byte> buffer)
        {
            if (_done) return 0;
            int n = inner.Read(buffer);
            if (n == 0 && !buffer.IsEmpty)
            {
                atEnd();
                _done = true;
            }
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>Little helpers for ZIP's fixed-size fields.</summary>
internal static class Le
{
    public static ushort U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
    public static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    public static ulong U64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b[at..]);
}
