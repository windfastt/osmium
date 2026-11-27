using Osmium.Core.Archives;
using Osmium.Core.Container;
using Osmium.Core.Crypto;
using Osmium.Core.Pipeline;

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

    // ------------------------------------------------------------------ item 8: allocations before authentication are capped

    /// <summary>Rewrites the footer with another index length (CRC fixed); the index offset follows unless given.</summary>
    internal static void SetFooter(string path, long indexLength, long? indexOffset = null)
    {
        var b = File.ReadAllBytes(path);
        long offset = indexOffset ?? System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(b.Length - 32));
        ulong id = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(b.Length - 16));
        new ArchiveFooter(offset, indexLength, id).Serialize().CopyTo(b, b.Length - 32);
        File.WriteAllBytes(path, b);
    }

    [Theory]
    [InlineData("argon2id")]
    [InlineData("none")]
    public void AFooterThatClaimsAHugeFileListIsRefusedBeforeItIsRead(string kdf)
    {
        using var t = new TempDir();
        string path = kdf == "none" ? WriterSafetyTests.Write(t.Sub("a.osa"), new OsaWriterOptions()) : EncryptedArchives.Write(t.Sub("a.osa"), kdf);
        SetFooter(path, 3L << 30);
        int asked = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<CorruptDataException>(() => OsaReader.Open(path, new OsaOpenOptions { Password = () => { asked++; return EncryptedArchives.Password; } }));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
        Assert.Equal("The file list of this archive is larger than Osmium accepts (more than 256 MiB).", e.Message);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void AHugeFileListInASparseFileIsRefusedToo()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Needs a sparse file of 3.1 GiB, which this test makes on Linux only.");
        using var t = new TempDir();
        var header = File.ReadAllBytes(EncryptedArchives.Write(t.Sub("a.osa"), "argon2id")).AsSpan(0, 200).ToArray();
        var h = ArchiveHeader.Read(new MemoryStream(header));
        string path = t.Sub("sparse.osa");
        long length = (31L << 30) / 10, indexLength = 3L << 30;
        using (var fs = new FileStream(path, FileMode.CreateNew))
        {
            fs.Write(h.Raw);
            fs.SetLength(length);
            fs.Position = length - 32;
            fs.Write(new ArchiveFooter(length - 32 - indexLength, indexLength, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(h.ArchiveId)).Serialize());
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(OsaReader.IndexTooLarge, Assert.Throws<CorruptDataException>(() => OsaReader.Open(path, () => EncryptedArchives.Password)).Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
    }

    [Fact]
    public void SalvageSkipsAFrameHeaderThatClaimsTwoGibibytes()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Needs a sparse file of more than 2 GiB, which this test makes on Linux only.");
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), "argon2id");
        path = SalvageKdfTests.Damage(path, b => b[10] ^= 0x04); // the header is damaged, so the key is confirmed on frames
        var fh = new byte[Osa.FrameHeaderSize];
        Osa.FrameMagic.CopyTo(fh);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fh.AsSpan(4), Osa.FrameFlagEncrypted);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(fh.AsSpan(16), 1L << 31);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fh.AsSpan(24), Osa.Crc(fh.AsSpan(0, 24)));
        long at;
        using (var fs = new FileStream(path, FileMode.Open))
        {
            at = fs.Length;
            fs.Position = at;
            fs.Write(fh);
            fs.SetLength(at + Osa.FrameHeaderSize + (1L << 31) + 1);
        }
        var r = SalvageKdfTests.Salvage(path, t.Sub("out"), () => EncryptedArchives.Password);
        var bogus = r.Frames.Single(f => f.Offset == at);
        Assert.Equal(SalvageFrameState.Damaged, bogus.State);
        Assert.Equal(3, r.FilesRecovered);
    }

    [Fact]
    public void TheWriterRefusesAFileListAboveTheCapAndLeavesNoFile()
    {
        using var t = new TempDir();
        var e = Assert.Throws<IOException>(() => WriterSafetyTests.Write(t.Sub("a.osa"),
            new OsaWriterOptions { Password = EncryptedArchives.Password, Kdf = EncryptedArchives.Small, MaxIndexFrame = 4 << 10 }, blocks: 200, size: 1));
        Assert.Equal("The file list of this archive is too large (more than 4 KiB compressed).", e.Message);
        Assert.Empty(Directory.GetFileSystemEntries(t.Path));
    }

    [Fact]
    public async Task TheFileListOfTenThousandSmallFilesIsFarBelowTheCap()
    {
        using var t = new TempDir();
        string input = t.Sub("in");
        for (int d = 0; d < 100; d++)
        {
            string dir = Path.Combine(input, $"folder {d:D2}");
            Directory.CreateDirectory(dir);
            for (int f = 0; f < 100; f++) File.WriteAllText(Path.Combine(dir, $"note {d:D2}-{f:D3}.txt"), $"small file {d} {f}\n");
        }
        var plan = await new Planning.Planner(new Planning.PlanOptions { Profile = Planning.Profile.ByName("balanced"), MemoryBudget = 1L << 30 })
            .BuildAsync(Planning.Scanner.Scan([input], ct: Ct), Ct);
        await Planning.ArchiveBuilder.BuildAsync(plan, new Planning.BuildOptions
        {
            OutputPath = t.Sub("a.osa"), Password = EncryptedArchives.Password, Kdf = EncryptedArchives.Small, MemoryBudget = 1L << 30,
        }, Ct);
        var b = File.ReadAllBytes(t.Sub("a.osa"));
        long indexLength = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(b.Length - 24));
        double perEntry = indexLength / 10_100.0; // 10,000 files and 101 folders
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"index frame of 10,000 files: {indexLength:N0} bytes, {perEntry:F1} bytes per entry; 256 MiB holds about {Osa.MaxIndexFrameBytes / perEntry:N0} entries");
        Assert.True(indexLength * 100 < Osa.MaxIndexFrameBytes);
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
