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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static SalvageResult Salvage(string path, string dest, Func<string?> password, SalvageOptions? options = null) =>
        Container.Salvage.Run(path, dest, password, options ?? new SalvageOptions { MemoryBudget = 1L << 30 }, Ct);

    internal static string Damage(string path, Action<byte[]> change)
    {
        var b = File.ReadAllBytes(path);
        change(b);
        string bad = path + ".damaged.osa";
        File.WriteAllBytes(bad, b);
        return bad;
    }

    internal static void AssertRestored(SalvageResult r, string dest, int files = 3)
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(files, r.FilesRecovered);
        for (int i = 0; i < files; i++) Assert.Equal(EncryptedArchives.Content(i), File.ReadAllBytes(Path.Combine(dest, $"f{i}.bin")));
    }

    [Fact]
    public void AnIntactArgon2idArchiveSalvagesCompletely()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), "argon2id");
        using var seams = new KdfSeams();
        var r = Salvage(path, t.Sub("out"), () => EncryptedArchives.Password);
        AssertRestored(r, t.Sub("out"));
        Assert.True(r.HeaderIntact);
        Assert.Equal(1, seams.Allocations);
    }

    [Theory]
    [InlineData(12)]   // the flags
    [InlineData(10)]   // the minor version
    [InlineData(36)]   // the record type
    [InlineData(37)]   // the record length
    public void AnArgon2idHeaderDamagedOutsideItsKeyFieldsIsRebuilt(int offset)
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), "argon2id");
        var r = Salvage(Damage(path, b => b[offset] ^= 0x04), t.Sub("out"), () => EncryptedArchives.Password);
        Assert.False(r.HeaderIntact);
        AssertRestored(r, t.Sub("out"));
    }

    [Fact]
    public void ADamagedKdfByteOfAPbkdf2ArchiveIsRestored()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), "pbkdf2");
        var r = Salvage(Damage(path, b => b[38] = 0x77), t.Sub("out"), () => EncryptedArchives.Password);
        AssertRestored(r, t.Sub("out"));
    }

    [Fact]
    public void DamagedVerifierBytesAreConfirmedByTheFramesAndAWrongPasswordWritesNothing()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), "argon2id");
        int verifierAt = 36 + 2 + 1 + 3 + 32 + 1; // type, length, kdf, t m p, salt, cipher id
        string bad = Damage(path, b => b[verifierAt + 3] ^= 0xFF);
        var r = Salvage(bad, t.Sub("out"), () => EncryptedArchives.Password);
        Assert.Null(r.KeyProblem);
        Assert.Contains(r.Notes, n => n.Contains("authenticate the frames", StringComparison.Ordinal));
        Assert.Equal(3, r.BlocksVerified); // the index binds the header bytes, which are damaged: block files
        var wrong = Salvage(bad, t.Sub("out2"), () => "not the password");
        Assert.Contains("could not be confirmed", wrong.KeyProblem);
        Assert.Empty(wrong.Written);
    }

    [Fact]
    public void DamagedKeyFieldsCannotBeDecryptedAndNothingIsWritten()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), "argon2id");
        var r = Salvage(Damage(path, b => b[50] ^= 0xFF), t.Sub("out"), () => EncryptedArchives.Password); // the salt
        Assert.NotNull(r.KeyProblem);
        Assert.Empty(r.Written);
        Assert.All(r.Blocks, b => Assert.Equal(SalvageBlockState.NotDecrypted, b.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MemoryOverTheBudgetIsRefusedBeforeThePasswordAndNothingIsWritten(bool headerDamaged)
    {
        using var t = new TempDir();
        string path = WriterSafetyTests.Write(t.Sub("a.osa"), new OsaWriterOptions { Password = EncryptedArchives.Password, Kdf = KdfParameters.Argon2id(1, 8 << 10, 1) });
        if (headerDamaged) path = Damage(path, b => b[12] ^= 0x04); // the kdf byte still reads 2
        using var seams = new KdfSeams();
        int asked = 0;
        Assert.Throws<KdfMemoryLimitException>(() =>
            Salvage(path, t.Sub("out"), () => { asked++; return EncryptedArchives.Password; }, new SalvageOptions { MemoryBudget = 4L << 20 }));
        Assert.Equal((0, 0L), (asked, seams.Allocations));
        Assert.False(Directory.Exists(t.Sub("out")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkOverTheStrongestPresetIsRefusedBeforeThePassword(bool headerDamaged)
    {
        using var t = new TempDir();
        string path = WriterSafetyTests.Write(t.Sub("a.osa"), new OsaWriterOptions { Password = EncryptedArchives.Password, Kdf = KdfParameters.Argon2id(1, 16 << 10, 1) });
        EncryptedArchives.ReplaceRecord(path, EncryptedArchives.Argon2Record(16, 1 << 20, 4));
        if (headerDamaged) path = Damage(path, b => b[12] ^= 0x04);
        int asked = 0;
        Assert.Throws<KdfCostException>(() =>
            Salvage(path, t.Sub("out"), () => { asked++; return EncryptedArchives.Password; }, new SalvageOptions { MemoryBudget = 2L << 30 }));
        Assert.Equal(0, asked);
        Assert.False(Directory.Exists(t.Sub("out")));
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

/// <summary>Salvage under the policy Osmium itself applies, with protection at the minimum (256 MiB): in the heavy collection.</summary>
[Collection(HeavyKeyDerivation.Name)]
public class SalvageKdfHeavyTests
{
    [Fact]
    public void ADamagedKdfByteOfAnArgon2idArchiveTriesArgon2idFirstAndSkipsTheWeakReading()
    {
        Argon2idHeavyTests.SkipUnlessMemoryFor(KdfPresets.Floor.MemoryBytes);
        using var t = new TempDir();
        string path = WriterSafetyTests.Write(t.Sub("a.osa"), new OsaWriterOptions { Password = EncryptedArchives.Password, Kdf = KdfPresets.Floor });
        string bad = SalvageKdfTests.Damage(path, b => b[38] = 0x42);
        using var seams = new KdfSeams(inspectWipe: false);
        using (TestDefaults.AsOsmiumDoes())
        {
            // Read as PBKDF2 the bytes say 2 iterations: weak, so that reading counts as one that does not authenticate.
            var r = SalvageKdfTests.Salvage(bad, t.Sub("out"), () => EncryptedArchives.Password);
            SalvageKdfTests.AssertRestored(r, t.Sub("out"));
        }
        Assert.Equal(1, seams.Allocations); // Argon2id first, and it was the one
    }
}
