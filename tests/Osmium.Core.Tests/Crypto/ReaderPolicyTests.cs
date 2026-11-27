using Osmium.Core.Archives;
using Osmium.Core.Container;
using Osmium.Core.Crypto;

namespace Osmium.Core.Tests.Crypto;

/// <summary>What a reader refuses or asks about before it lists or writes anything (FORMAT §5), through every reader.</summary>
public class ReaderPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Detail(IArchiveSource s, string label) => s.Details.Single(d => d.Label == label).Value;

    // ------------------------------------------------------------------ the archive source

    [Theory]
    [InlineData("argon2id", "AES-256-GCM, key from Argon2id (64 KiB, 1 pass, 1 lane)")]
    [InlineData("pbkdf2", "AES-256-GCM, key from PBKDF2-SHA512 (100,000 iterations; Osmium 0.1 can open it)")]
    public void TheSourceSaysHowTheKeyIsMadeAndThatThePasswordWasVerified(string kdf, string encryption)
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), kdf);
        using var s = ArchiveSources.Open(path, new ArchiveOpenOptions { Password = () => EncryptedArchives.Password }, Ct);
        Assert.Equal("yes", Detail(s, "Encrypted"));
        Assert.Equal(encryption, Detail(s, "Encryption"));
        Assert.Equal("verified", Detail(s, "Password"));
        using var plain = ArchiveSources.Open(WriterSafetyTests.Write(t.Sub("b.osa"), new OsaWriterOptions()), new ArchiveOpenOptions(), Ct);
        Assert.DoesNotContain(plain.Details, d => d.Label is "Encryption" or "Password");
    }

    [Fact]
    public void TheSourceUsesItsMemoryLimitForTheKeyDerivation()
    {
        using var t = new TempDir();
        string path = WriterSafetyTests.Write(t.Sub("a.osa"), new OsaWriterOptions { Password = EncryptedArchives.Password, Kdf = KdfParameters.Argon2id(1, 8 << 10, 1) });
        int asked = 0;
        string Ask() { asked++; return EncryptedArchives.Password; }
        var e = Assert.Throws<KdfMemoryLimitException>(() =>
            ArchiveSources.Open(path, new ArchiveOpenOptions { Password = Ask, Limits = ImportLimits.Default with { MemoryBudgetBytes = 4L << 20 } }, Ct));
        Assert.Equal(0, asked);
        // the caller asks the user and opens again with the memory the archive needs
        using (ArchiveSources.Open(path, new ArchiveOpenOptions { Password = Ask, Limits = ImportLimits.Default with { MemoryBudgetBytes = e.NeededBytes } }, Ct)) { }
        Assert.Equal(1, asked);
    }

    [Fact]
    public void TheSourceOpensWeakProtectionOnlyWhenAllowed()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), "pbkdf2-weak");
        int asked = 0;
        string Ask() { asked++; return EncryptedArchives.Password; }
        using (TestDefaults.AsOsmiumDoes())
        {
            Assert.Throws<WeakProtectionException>(() => ArchiveSources.Open(path, new ArchiveOpenOptions { Password = Ask }, Ct));
            Assert.Equal(0, asked);
            using var s = ArchiveSources.Open(path, new ArchiveOpenOptions { Password = Ask, AllowWeakProtection = true }, Ct);
            Assert.Equal("AES-256-GCM, key from PBKDF2-SHA512 (1,000 iterations; Osmium 0.1 can open it)", Detail(s, "Encryption"));
        }
        Assert.Equal(1, asked);
    }

    [Fact]
    public void TheSourceGivesTheCostQuestionToItsCaller()
    {
        using var t = new TempDir();
        string path = WriterSafetyTests.Write(t.Sub("a.osa"), new OsaWriterOptions { Password = EncryptedArchives.Password, Kdf = KdfParameters.Argon2id(1, 16 << 10, 1) });
        EncryptedArchives.ReplaceRecord(path, EncryptedArchives.Argon2Record(16, 1 << 20, 4));
        Assert.Throws<KdfCostException>(() => ArchiveSources.Open(path,
            new ArchiveOpenOptions { Password = () => EncryptedArchives.Password, Limits = ImportLimits.Default with { MemoryBudgetBytes = 2L << 30 } }, Ct));
    }
}
