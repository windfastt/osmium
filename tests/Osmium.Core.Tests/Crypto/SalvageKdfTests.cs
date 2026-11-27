using Osmium.Core.Container;
using Osmium.Core.Crypto;
using Osmium.Core.IO;

namespace Osmium.Core.Tests.Crypto;

/// <summary>Salvage of encrypted archives whose header is damaged: the key fields in both layouts (FORMAT §8).</summary>
public class SalvageKdfTests
{
    private static readonly byte[] Salt = [.. Enumerable.Range(0x40, 32).Select(i => (byte)i)];
    private static readonly byte[] Verifier = [.. Enumerable.Range(0xA0, 16).Select(i => (byte)i)];

    private static List<KeyFields> FieldsOf(TempDir t, byte[] header, Action<byte[]>? damage = null, bool pad = true)
    {
        var bytes = (byte[])header.Clone();
        damage?.Invoke(bytes);
        string path = t.Sub("h.osa");
        File.WriteAllBytes(path, pad ? [.. bytes, .. new byte[256]] : bytes); // something after the header, as frames would be
        using var src = new SalvageSource(path);
        return KeyFields.Read(src);
    }

    private static byte[] HeaderWith(EncryptionInfo info, string? creator = null) =>
        new ArchiveHeader { Flags = Osa.FlagEncrypted, Encryption = info, Creator = creator }.Serialize();

    [Fact]
    public void AnIntactKdfByteGivesItsOwnLayoutOnly()
    {
        using var t = new TempDir();
        var info = new EncryptionInfo(KdfPresets.Standard, Salt, Verifier);
        var f = Assert.Single(FieldsOf(t, HeaderWith(info)));
        Assert.True(f.KdfByteIntact);
        Assert.Equal(KdfPresets.Standard, f.Encryption.Kdf);
        Assert.Equal(info.RecordBytes, f.Encryption.RecordBytes);
        Assert.Equal(Salt, f.Encryption.Salt);
        Assert.Equal(Verifier, f.Encryption.Verifier);
        Assert.Equal(36 + 2 + 55, f.EncryptionRecordEnd);
    }

    [Fact]
    public void ADamagedKdfByteOfAnArgon2idHeaderGivesArgon2idFirst()
    {
        using var t = new TempDir();
        var info = new EncryptionInfo(KdfPresets.Standard, Salt, Verifier);
        var found = FieldsOf(t, HeaderWith(info), b => b[38] = 0x42);
        // Read as PBKDF2, "02 80 80 40 04 …" gives 2 iterations: within the limits, so a (weak) candidate, after Argon2id.
        Assert.Equal([KdfAlgorithm.Argon2id, KdfAlgorithm.Pbkdf2Sha512], found.Select(f => f.Encryption.Kdf.Algorithm));
        Assert.All(found, f => Assert.False(f.KdfByteIntact));
        Assert.Equal(info.RecordBytes, found[0].Encryption.RecordBytes); // the original record, with the kdf id it had
        Assert.Equal(2, found[1].Encryption.Iterations);
        Assert.Equal(KdfVerdictKind.Weak, found[1].Encryption.Kdf.Check().Kind);
        Assert.Equal((byte)KdfAlgorithm.Pbkdf2Sha512, found[1].Encryption.RecordBytes[0]);
    }

    [Fact]
    public void ADamagedKdfByteOfAPbkdf2HeaderDropsTheArgon2idReading()
    {
        using var t = new TempDir();
        var info = new EncryptionInfo(1000, Salt, Verifier);
        var found = FieldsOf(t, HeaderWith(info, "Osmium 0.1.1"), b => b[38] = 0xFF);
        // As Argon2id, 1,000 passes are outside the limits: no candidate.
        var f = Assert.Single(found);
        Assert.Equal(1000, f.Encryption.Iterations);
        Assert.Equal(info.RecordBytes, f.Encryption.RecordBytes);
    }

    [Fact]
    public void ADamagedCipherIdIsWrittenBackAsTheOnlyCipher()
    {
        using var t = new TempDir();
        var info = new EncryptionInfo(KdfPresets.Standard, Salt, Verifier);
        int cipherAt = 36 + 2 + 1 + 1 + 3 + 1 + 32; // t, m (3 bytes), p, salt
        var f = Assert.Single(FieldsOf(t, HeaderWith(info), b => b[cipherAt] = 7));
        Assert.Equal(info.RecordBytes, f.Encryption.RecordBytes);
    }

    [Fact]
    public void ReadingsOutsideTheLimitsAreNoCandidates()
    {
        using var t = new TempDir();
        var info = new EncryptionInfo(KdfPresets.Standard, Salt, Verifier);
        // m damaged to more than 8 GiB, with the kdf byte intact: nothing to try
        var record = new BlobWriter().B(2).U(2).U(16UL << 20).U(4).Raw(Salt).B(1).Raw(Verifier).ToArray();
        var header = HeaderWith(info);
        var damaged = header[..36].Concat(new BlobWriter().U(1).Bytes(record).ToArray()).Concat(new byte[4]).ToArray();
        Assert.Empty(FieldsOf(t, damaged));
        Assert.Empty(FieldsOf(t, header[..60], pad: false)); // the file ends inside the record
    }
}
