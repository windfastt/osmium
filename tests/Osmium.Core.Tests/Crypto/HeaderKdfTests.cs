using System.Buffers.Binary;
using System.Reflection;
using Osmium.Core.Container;
using Osmium.Core.Crypto;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Crypto;

/// <summary>Header record 1, EncryptionInfo (FORMAT §1, §5): its two layouts, its reading order and its limits.</summary>
public class HeaderKdfTests
{
    private static readonly byte[] Salt = [.. Enumerable.Range(0x40, 32).Select(i => (byte)i)];
    private static readonly byte[] Verifier = [.. Enumerable.Range(0xA0, 16).Select(i => (byte)i)];
    private static readonly byte[] Id = [.. Enumerable.Range(0x10, 16).Select(i => (byte)i)];

    /// <summary>A header with these extension records (type, value) and a correct CRC.</summary>
    internal static byte[] Header(uint flags, params (ulong Type, byte[] Value)[] records)
    {
        var ext = new BlobWriter();
        foreach (var (type, value) in records) ext.U(type).Bytes(value);
        var e = ext.ToArray();
        var b = new byte[36 + e.Length + 4];
        Osa.Magic.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), Osa.Major);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), Osa.Minor);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), flags);
        Id.CopyTo(b, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), (uint)e.Length);
        e.CopyTo(b, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36 + e.Length), Osa.Crc(b.AsSpan(0, 36 + e.Length)));
        return b;
    }

    private static ArchiveHeader Read(byte[] header) => ArchiveHeader.Read(new MemoryStream(header));

    private static byte[] Argon2Record(ulong t = 2, ulong m = 1 << 20, ulong p = 4, byte cipher = 1) =>
        new BlobWriter().B(2).U(t).U(m).U(p).Raw(Salt).B(cipher).Raw(Verifier).ToArray();

    private static byte[] Pbkdf2Record(ulong iterations) => new BlobWriter().B(1).U(iterations).Raw(Salt).B(1).Raw(Verifier).ToArray();

    [Fact]
    public void TheDefaultRecordIsPinned()
    {
        var header = new ArchiveHeader { Flags = Osa.FlagEncrypted, ArchiveId = Id, Encryption = new EncryptionInfo(KdfPresets.Standard, Salt, Verifier) };
        var bytes = header.Serialize();
        // type 1, length 55, kdf 2, t = 2, m = 1 048 576 (80 80 40), p = 4, salt, aead 1, verifier; nothing else
        Assert.Equal("0137" + "0202808040" + "04" + Convert.ToHexString(Salt) + "01" + Convert.ToHexString(Verifier), Convert.ToHexString(bytes.AsSpan(36, 57)));
        Assert.Equal(57u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32)));
        Assert.Equal(0x0001u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12))); // no flag of its own: padding is part of kdf 2

        var back = Read(bytes);
        Assert.Equal(KdfPresets.Standard, back.Encryption!.Kdf);
        Assert.Equal(Salt, back.Encryption.Salt);
        Assert.Equal(Verifier, back.Encryption.Verifier);
        Assert.Equal(0, back.Encryption.Iterations);
        Assert.Equal(bytes, back.Raw);
    }

    [Fact]
    public void Pbkdf2KeepsItsLayout()
    {
        var header = new ArchiveHeader { Flags = Osa.FlagEncrypted, ArchiveId = Id, Encryption = new EncryptionInfo(600_000, Salt, Verifier) };
        var bytes = header.Serialize();
        // type 1, length 53, kdf 1, 600 000 = c0 cf 24, salt, aead 1, verifier
        Assert.Equal("0135" + "01C0CF24" + Convert.ToHexString(Salt) + "01" + Convert.ToHexString(Verifier), Convert.ToHexString(bytes.AsSpan(36, 55)));
        var back = Read(bytes).Encryption!;
        Assert.Equal((KdfAlgorithm.Pbkdf2Sha512, 600_000), (back.Kdf.Algorithm, back.Iterations));
    }

    [Fact]
    public void EveryTruncationOfAnArgon2idRecordIsDamage()
    {
        var full = Argon2Record();
        for (int n = 0; n < full.Length; n++)
        {
            var e = Assert.Throws<CorruptDataException>(() => Read(Header(Osa.FlagEncrypted, (1, full[..n]))));
            Assert.IsNotType<NewerVersionException>(e);
            if (n > 0) Assert.Equal("The encryption settings in this archive's header are invalid.", e.Message);
        }
    }

    [Fact]
    public void NothingMayFollowTheVerifierOfAnArgon2idRecord()
    {
        var e = Assert.Throws<CorruptDataException>(() => Read(Header(Osa.FlagEncrypted, (1, [.. Argon2Record(), 0]))));
        Assert.Equal("The encryption settings in this archive's header are invalid.", e.Message);
        // PBKDF2 readers always ignored such bytes, and this one still reads them (the reader policy refuses them later)
        var pbkdf2 = Read(Header(Osa.FlagEncrypted, (1, [.. Pbkdf2Record(1000), 7, 8]))).Encryption!;
        Assert.Equal((1000, 2), (pbkdf2.Iterations, pbkdf2.ExtraBytes));
    }

    [Fact]
    public void TwoEncryptionRecordsAreDamage()
    {
        var e = Assert.Throws<CorruptDataException>(() => Read(Header(Osa.FlagEncrypted, (1, Argon2Record()), (1, Argon2Record(t: 3)))));
        Assert.Equal("The encryption settings in this archive's header are invalid.", e.Message);
        Assert.Throws<CorruptDataException>(() => Read(Header(Osa.FlagEncrypted, (1, Pbkdf2Record(100_000)), (2, "Osmium"u8.ToArray()), (1, Pbkdf2Record(1)))));
    }

    [Fact]
    public void UnknownFunctionsAndCiphersNeedANewerVersion()
    {
        var kdf3 = Assert.ThrowsAny<NewerVersionException>(() => Read(Header(Osa.FlagEncrypted, (1, [3, .. Argon2Record()[1..]]))));
        // Read before anything can be authenticated, so the message names both causes.
        Assert.Equal("This archive needs a newer version of Osmium, or it was damaged or changed (unknown key-derivation method 3).", kdf3.Message);
        Assert.Equal("key derivation function 3", kdf3.Feature);
        var aead2 = Assert.ThrowsAny<NewerVersionException>(() => Read(Header(Osa.FlagEncrypted, (1, Argon2Record(cipher: 2)))));
        Assert.Equal("This archive needs a newer version of Osmium, or it was damaged or changed (unknown cipher 2).", aead2.Message);
        // the cipher is known before the fields after it: a newer cipher may have a longer verifier
        Assert.ThrowsAny<NewerVersionException>(() => Read(Header(Osa.FlagEncrypted, (1, Argon2Record(cipher: 2)[..^3]))));
    }

    [Theory]
    [InlineData(0UL, 1UL << 20, 4UL)]
    [InlineData(17UL, 1UL << 20, 4UL)]
    [InlineData(2UL, 1UL << 20, 0UL)]
    [InlineData(2UL, 1UL << 20, 65UL)]
    [InlineData(2UL, 31UL, 4UL)]               // m = 8·p − 1
    [InlineData(1UL, (8UL << 20) + 1, 4UL)]    // 8 GiB + 1 KiB
    [InlineData(16UL, (2UL << 20) + 1, 4UL)]   // t·m one over 32 GiB-passes
    [InlineData(1UL << 40, 1UL << 20, 4UL)]    // beyond an int
    [InlineData(2UL, ulong.MaxValue, 4UL)]
    public void Argon2idOutsideTheLimitsIsDamage(ulong t, ulong m, ulong p)
    {
        var header = Header(Osa.FlagEncrypted, (1, Argon2Record(t, m, p)));
        long built = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<CorruptDataException>(() => Read(header));
        Assert.IsNotType<NewerVersionException>(e);
        Assert.StartsWith("Key derivation cost (Argon2id, ", e.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - built < 64 << 10, "reading a hostile record allocated more than 64 KiB");
    }

    [Theory]
    [InlineData(0UL, "Key derivation cost 0 is invalid.")]
    [InlineData(20_000_001UL, "Key derivation cost 20000001 exceeds the safety limit.")]
    [InlineData(1UL << 35, "Key derivation cost 2147483647 exceeds the safety limit.")]
    public void Pbkdf2OutsideTheLimitsIsDamage(ulong iterations, string message)
    {
        var header = Header(Osa.FlagEncrypted, (1, Pbkdf2Record(iterations)));
        long built = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(message, Assert.Throws<CorruptDataException>(() => Read(header)).Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - built < 64 << 10);
    }

    [Fact]
    public void AMalformedVarintIsDamage()
    {
        // eleven continuation bytes, and a 10-byte varint that overflows 64 bits
        byte[] tooLong = [2, .. Enumerable.Repeat((byte)0x80, 11), 1, 0x80, 0x80, 0x40, 4, .. Salt, 1, .. Verifier];
        byte[] overflow = [2, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0x80, 0x80, 0x40, 4, .. Salt, 1, .. Verifier];
        foreach (var record in new[] { tooLong, overflow })
            Assert.Equal("The encryption settings in this archive's header are invalid.",
                Assert.Throws<CorruptDataException>(() => Read(Header(Osa.FlagEncrypted, (1, record)))).Message);
    }

    [Fact]
    public void TheWeakestAndStrongestParametersWithinTheLimitsAreRead()
    {
        // weak protection is read (the reader decides about it before deriving), and so is work above the presets
        Assert.Equal(KdfParameters.Argon2id(1, 8, 1), Read(Header(Osa.FlagEncrypted, (1, Argon2Record(1, 8, 1)))).Encryption!.Kdf);
        Assert.Equal(KdfParameters.Argon2id(4, 8 << 20, 64), Read(Header(Osa.FlagEncrypted, (1, Argon2Record(4, 8UL << 20, 64)))).Encryption!.Kdf);
        Assert.Equal(1, Read(Header(Osa.FlagEncrypted, (1, Pbkdf2Record(1)))).Encryption!.Iterations);
    }

    [Fact]
    public void TheRecordIsWrittenAgainExactlyAsItWasRead()
    {
        // a kdf 1 record with bytes after the verifier (a varint written with a spare byte, too) keeps them
        byte[] odd = [1, 0xE8, 0x87, 0x00, .. Salt, 1, .. Verifier, 9];
        var header = Read(Header(Osa.FlagEncrypted, (1, odd)));
        Assert.Equal(odd, header.Encryption!.RecordBytes);
        var again = new ArchiveHeader { Flags = header.Flags, ArchiveId = header.ArchiveId, Encryption = header.Encryption }.Serialize();
        Assert.Equal(header.Raw, again);
        // a copy with other values writes its own
        var changed = header.Encryption with { Salt = new byte[32] };
        Assert.NotEqual(odd, changed.RecordBytes);
        Assert.Equal(1 + 2 + 32 + 1 + 16, changed.RecordBytes.Length);
    }

    [Fact]
    public void ARecordDescribesItsCostWithoutItsBytes()
    {
        var info = new EncryptionInfo(KdfPresets.Standard, Salt, Verifier);
        Assert.Equal("EncryptionInfo { Argon2id, 1 GiB, 2 passes, 4 lanes }", info.ToString());
    }

    [Fact]
    public void AKeyIsZeroedWhenDisposed()
    {
        var info = new EncryptionInfo(KdfParameters.Argon2id(1, 64, 1), Salt, Verifier);
        var key = ArchiveKey.Derive("password", info);
        var sealedBody = key.Seal(Id, 3, "hello"u8);
        Assert.Equal("hello"u8.ToArray(), key.Open(Id, 3, sealedBody));
        var kenc = (byte[])typeof(ArchiveKey).GetField("_kenc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(key)!;
        Assert.Contains(kenc, b => b != 0);
        key.Dispose();
        Assert.True(key.IsDisposed);
        Assert.All(kenc, b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => key.Seal(Id, 4, "x"u8));
        Assert.Throws<ObjectDisposedException>(() => key.Open(Id, 3, sealedBody));
        key.Dispose(); // twice is fine
    }

    [Fact]
    public void KeysFromBothFunctionsAreTheSameAsBefore()
    {
        // The bare salt and iteration count of Osmium 0.1's API and the record give the same key.
        var a = ArchiveKey.Derive("pässwörd", Salt, 1000);
        var b = ArchiveKey.Derive("pässwörd", new EncryptionInfo(1000, Salt, Verifier));
        Assert.Equal(a.Verifier, b.Verifier);
        Assert.True(a.Matches(b.Verifier));
        Assert.False(a.Matches(Verifier));
    }
}
