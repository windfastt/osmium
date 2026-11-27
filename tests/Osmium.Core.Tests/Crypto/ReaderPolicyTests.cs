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

    // ------------------------------------------------------------------ item 1: fake-encrypted archives are refused

    private const string Pw = EncryptedArchives.Password;
    private static readonly string M1 = ModifiedArchiveException.DefaultMessage;

    private static OsaWriterOptions Options(string kdf, SpareCopyPolicy spares = SpareCopyPolicy.Never) => EncryptedArchives.Options(kdf, spares);

    /// <summary>Nothing but a salvage report (which describes, and holds nothing of the archive) is in <paramref name="dest"/>.</summary>
    private static void AssertNothingWritten(string dest)
    {
        if (!Directory.Exists(dest)) return;
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(dest), p => Path.GetFileName(p) != Container.Salvage.ReportFileName);
    }

    /// <summary>Refused with M1 by the reader and the archive source, and salvage writes nothing from it (it refuses it,
    /// or a wrong password).</summary>
    private static void AssertRefusedEverywhere(TempDir t, string path, string password = Pw, bool salvage = true)
    {
        Assert.Equal(M1, Assert.ThrowsAny<ModifiedArchiveException>(() => OsaReader.Open(path, () => password)).Message);
        Assert.ThrowsAny<ModifiedArchiveException>(() => ArchiveSources.Open(path, new ArchiveOpenOptions { Password = () => password }, Ct));
        if (!salvage) return;
        string dest = t.Sub("salvaged-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var r = SalvageKdfTests.Salvage(path, dest, () => password);
            Assert.Empty(r.Written);
        }
        catch (Exception e) when (e is ModifiedArchiveException or WrongPasswordException) { }
        AssertNothingWritten(dest);
    }

    [Theory]
    [InlineData("pbkdf2")]
    [InlineData("argon2id")]
    public void AHeaderThatClaimsEncryptionOverPlaintextFramesIsRefused(string kdf)
    {
        // (a): a valid EncryptionInfo record for the user's password, but frames and index written in plaintext
        using var t = new TempDir();
        var plain = File.ReadAllBytes(EncryptedArchives.WriteWithFolder(t.Sub("plain.osa"), new OsaWriterOptions(), 3000, 3000));
        var donor = File.ReadAllBytes(EncryptedArchives.WriteWithFolder(t.Sub("donor.osa"), Options(kdf), 10));
        var donorHeader = ArchiveHeader.Read(new MemoryStream(donor));
        string path = t.Sub("a.osa");
        File.WriteAllBytes(path, EncryptedArchives.Graft(donorHeader.Raw, plain));
        int asked = 0;
        Assert.Throws<ModifiedArchiveException>(() => OsaReader.Open(path, () => { asked++; return Pw; }));
        Assert.Equal(0, asked); // the index frame says so before the password is needed
        AssertRefusedEverywhere(t, path);
        AssertRefusedEverywhere(t, path, "another password"); // (d): whatever the password
        using (TestDefaults.AsOsmiumDoes())
            Assert.Throws<ModifiedArchiveException>(() => OsaReader.Open(path, new OsaOpenOptions { Password = () => Pw, Protection = ProtectionPolicy.AllowWeak }));
    }

    [Theory]
    [InlineData("pbkdf2")]
    [InlineData("argon2id")]
    public void FramesFlaggedEncryptedThatHoldRandomBytesAreRefusedWithNothingWritten(string kdf)
    {
        // (b): the file list is genuine and authenticates, so listing works; no block does
        using var t = new TempDir();
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), Options(kdf), 3000, 3000);
        var b = File.ReadAllBytes(path);
        foreach (var (offset, length) in EncryptedArchives.Frames(path))
            Random.Shared.NextBytes(b.AsSpan((int)offset + Osa.FrameHeaderSize, (int)length - Osa.FrameHeaderSize));
        File.WriteAllBytes(path, b);
        using var r = OsaReader.Open(path, () => Pw);
        Assert.Equal(4, r.Entries.Count);
        Assert.Equal(M1, Assert.Throws<ModifiedArchiveException>(() => Extractor.Extract(r, t.Sub("out"), null, null, null, Ct)).Message);
        AssertNothingWritten(t.Sub("out"));
        Assert.Throws<ModifiedArchiveException>(() => Extractor.Test(r, null, Ct));
        using var s = ArchiveSources.Open(path, new ArchiveOpenOptions { Password = () => Pw }, Ct);
        Assert.Throws<ModifiedArchiveException>(() => s.Extract(t.Sub("out2"), new ArchiveExtractRequest(), null, Ct));
        AssertNothingWritten(t.Sub("out2"));
        Assert.Throws<ModifiedArchiveException>(() => SalvageKdfTests.Salvage(path, t.Sub("salvaged"), () => Pw));
        AssertNothingWritten(t.Sub("salvaged"));
    }

    [Theory]
    [InlineData("pbkdf2")]
    [InlineData("argon2id")]
    public void OneBlockOfRandomBytesCostsOnlyItsOwnFilesAsDamage(string kdf)
    {
        using var t = new TempDir();
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), Options(kdf), 3000, 3000);
        var b = File.ReadAllBytes(path);
        var (offset, length) = EncryptedArchives.Frames(path)[1];
        Random.Shared.NextBytes(b.AsSpan((int)offset + Osa.FrameHeaderSize, (int)length - Osa.FrameHeaderSize));
        File.WriteAllBytes(path, b);
        using var r = OsaReader.Open(path, () => Pw);
        var x = Extractor.Extract(r, t.Sub("out"), null, null, null, Ct);
        Assert.Equal((2, 1), (x.FilesOk, x.FilesFailed)); // f0.bin and the empty file; not f1.bin
        Assert.Contains("1 file was not extracted: its data failed authentication (damaged or changed).", x.Errors);
        Assert.True(File.Exists(Path.Combine(t.Sub("out"), "f0.bin")));
        Assert.False(File.Exists(Path.Combine(t.Sub("out"), "f1.bin")));
    }

    [Theory]
    [InlineData("pbkdf2")]
    [InlineData("argon2id")]
    public void ABlockFrameSwappedForAPlaintextFrameIsRefusedAtOpenEvenForAListing(string kdf)
    {
        // (c)
        using var t = new TempDir();
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), Options(kdf), 3000, 3000);
        var b = File.ReadAllBytes(path);
        EncryptedArchives.EditFrameHeader(b, EncryptedArchives.Frames(path)[1].Offset, fh => fh[4] = 0); // flags: not encrypted
        File.WriteAllBytes(path, b);
        AssertRefusedEverywhere(t, path);
        using (TestDefaults.AsOsmiumDoes())
            Assert.Throws<ModifiedArchiveException>(() => OsaReader.Open(path, new OsaOpenOptions { Password = () => Pw, Protection = ProtectionPolicy.AllowWeak }));
    }

    [Theory]
    [InlineData("pbkdf2")]
    [InlineData("argon2id")]
    public void FrameHeadersThatDisagreeWithTheFileListAreRefusedAtOpen(string kdf)
    {
        using var t = new TempDir();
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), Options(kdf), 3000, 3000, 3000);
        var frames = EncryptedArchives.Frames(path);
        var original = File.ReadAllBytes(path);
        // two whole frames swapped: their numbers are no longer their places
        var swapped = (byte[])original.Clone();
        original.AsSpan((int)frames[0].Offset, (int)frames[0].Length).CopyTo(swapped.AsSpan((int)frames[1].Offset));
        original.AsSpan((int)frames[1].Offset, (int)frames[1].Length).CopyTo(swapped.AsSpan((int)frames[0].Offset));
        File.WriteAllBytes(path, swapped);
        // (salvage finds both frames by their numbers, and each authenticates: it restores them, as it should)
        AssertRefusedEverywhere(t, path, salvage: false);
        // a frame header that claims another length, or the index flag
        foreach (Action<byte[]> change in new Action<byte[]>[] { fh => fh[16]++, fh => fh[4] |= (byte)Osa.FrameFlagIndex })
        {
            var b = (byte[])original.Clone();
            EncryptedArchives.EditFrameHeader(b, frames[2].Offset, change);
            File.WriteAllBytes(path, b);
            Assert.Throws<ModifiedArchiveException>(() => OsaReader.Open(path, () => Pw));
        }
    }

    [Theory]
    [InlineData("pbkdf2")]
    [InlineData("argon2id")]
    public void AttacksThatGcmCatchesAreDamageOfTheirBlocks(string kdf)
    {
        using var t = new TempDir();
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), Options(kdf), 3000, 3000, 3000);
        var frames = EncryptedArchives.Frames(path);
        var original = File.ReadAllBytes(path);
        // two bodies swapped under their own headers: the nonce and AAD carry the frame number from the index
        var swapped = (byte[])original.Clone();
        int body = (int)frames[0].Length - Osa.FrameHeaderSize;
        original.AsSpan((int)frames[0].Offset + Osa.FrameHeaderSize, body).CopyTo(swapped.AsSpan((int)frames[1].Offset + Osa.FrameHeaderSize));
        original.AsSpan((int)frames[1].Offset + Osa.FrameHeaderSize, body).CopyTo(swapped.AsSpan((int)frames[0].Offset + Osa.FrameHeaderSize));
        File.WriteAllBytes(path, swapped);
        using (var r = OsaReader.Open(path, () => Pw))
        {
            var x = Extractor.Extract(r, t.Sub("out"), null, null, null, Ct);
            Assert.Equal((2, 2), (x.FilesOk, x.FilesFailed)); // f2.bin and the empty file
            Assert.Contains("2 files were not extracted: their data failed authentication (damaged or changed).", x.Errors);
        }
        // one byte flipped where the block descriptor (with the method id) is encrypted
        var flipped = (byte[])original.Clone();
        flipped[frames[0].Offset + Osa.FrameHeaderSize + 1] ^= 0x01;
        File.WriteAllBytes(path, flipped);
        using (var r = OsaReader.Open(path, () => Pw))
        {
            var x = Extractor.Test(r, null, Ct);
            Assert.Equal((3, 1), (x.FilesOk, x.FilesFailed));
            Assert.Contains("1 file did not verify: its data failed authentication (damaged or changed).", x.Errors);
        }
    }

    [Theory]
    [InlineData("pbkdf2")]
    [InlineData("argon2id")]
    public void AHeaderByteTheFileListBindsCannotBeChanged(string kdf)
    {
        using var t = new TempDir();
        // (with a header copy, the reader would find that the two differ: that case has its own tests below)
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), Options(kdf), 3000, 3000);
        var b = File.ReadAllBytes(path);
        EncryptedArchives.EditHeader(b, h => h[14] ^= 0x01); // the experimental flag (bit 16), CRC fixed
        File.WriteAllBytes(path, b);
        var e = Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(path, () => Pw));
        Assert.Contains("failed authentication", e.Message);
        Assert.IsNotAssignableFrom<ModifiedArchiveException>(e); // damage, and the spare copy cannot help: it is bound the same way
        Assert.ThrowsAny<CorruptDataException>(() => ArchiveSources.Open(path, new ArchiveOpenOptions { Password = () => Pw }, Ct));
    }

    [Fact]
    public void ACreatorByteOfAnEarlierArchiveIsBoundToo()
    {
        using var t = new TempDir();
        string path = t.Sub("kept.osa");
        var b = Convert.FromBase64String(KdfArchiveTests.KeptPbkdf2Archive);
        int creator = b.AsSpan(0, 120).IndexOf("Osmium 0.2.0"u8);
        EncryptedArchives.EditHeader(b, h => h[creator + 7] = (byte)'9');
        File.WriteAllBytes(path, b);
        Assert.Contains("failed authentication", Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(path, () => "kept as bytes")).Message);
    }

    [Theory]
    [InlineData("pbkdf2")]
    [InlineData("argon2id")]
    public void AHeaderThatClaimsEncryptionWithoutItsSettingsIsRefusedAndNotTakenForDamage(string kdf)
    {
        using var t = new TempDir();
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), Options(kdf, SpareCopyPolicy.Always), 3000, 3000);
        var b = File.ReadAllBytes(path);
        EncryptedArchives.EditHeader(b, h => h[36] = 4); // record 1 becomes an ignorable record 4: the flag stays, the settings go
        File.WriteAllBytes(path, b);
        // the header copy at the end is intact, but this is no damage it may stand in for
        Assert.Equal(M1, Assert.ThrowsAny<ModifiedArchiveException>(() => ArchiveHeader.Read(new MemoryStream(b))).Message);
        AssertRefusedEverywhere(t, path);
    }

    [Fact]
    public void TheSameBytesInAnUnencryptedArchiveStillOpen()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), new OsaWriterOptions(), 3000, 3000);
        var b = File.ReadAllBytes(path);
        EncryptedArchives.EditFrameHeader(b, EncryptedArchives.Frames(path)[1].Offset, fh => fh[4] = 0x04); // a frame flag of a newer version
        File.WriteAllBytes(path, b);
        using var r = OsaReader.Open(path);
        Assert.Throws<NewerVersionException>(() => r.ReadBlock(1));
    }

    // ------------------------------------------------------------------ item 2: the minimum protection

    private static OsaOpenOptions Enforced(Func<string?> password) => new() { Password = password, Protection = ProtectionPolicy.Enforce };

    [Theory]
    [InlineData(99_999, false)]
    [InlineData(100_000, true)]
    public void Pbkdf2IsRefusedBelowOneHundredThousandIterations(int iterations, bool opens)
    {
        using var t = new TempDir();
        string path = WriterSafetyTests.Write(t.Sub("a.osa"), new OsaWriterOptions { Password = Pw, KdfIterations = iterations });
        int asked = 0;
        string Ask() { asked++; return Pw; }
        if (opens)
        {
            using (OsaReader.Open(path, Enforced(Ask))) { }
            Assert.Equal(1, asked);
            return;
        }
        var e = Assert.Throws<WeakProtectionException>(() => OsaReader.Open(path, Enforced(Ask)));
        Assert.Equal(0, asked); // nothing was derived either: the password comes after the check
        Assert.Contains("(PBKDF2, 99,999 iterations)", e.Message);
        Assert.Contains("Osmium 0.1's command line could make such archives with --kdf-iterations", e.Message);
    }

    [Theory]
    [InlineData(2, 262_144, 4)]  // 512 MiB-passes: less work than the minimum
    [InlineData(3, 262_143, 4)]  // less memory than the minimum
    [InlineData(1, 64 << 10, 4)]
    public void Argon2idBelowTheLightestProtectionIsRefused(int t, int m, int p)
    {
        using var dir = new TempDir();
        // written with settings whose varints have the same lengths, then given these (the refusal comes first)
        var written = KdfParameters.Argon2id(1, m >= 1 << 14 ? 1 << 14 : 1 << 7, 1);
        string path = WriterSafetyTests.Write(dir.Sub("a.osa"), new OsaWriterOptions { Password = Pw, Kdf = written });
        EncryptedArchives.ReplaceRecord(path, EncryptedArchives.Argon2Record(t, m, p));
        using var seams = new KdfSeams();
        int asked = 0;
        var e = Assert.Throws<WeakProtectionException>(() => OsaReader.Open(path, Enforced(() => { asked++; return Pw; })));
        Assert.Equal((0, 0L), (asked, seams.Allocations));
        Assert.Equal(KdfParameters.Argon2id(t, m, p), e.Parameters);
        Assert.Equal($"This archive is protected more weakly than any Osmium preset ({e.Parameters.CostText()}). It may have been made by modified software.", e.Message);
    }

    [Theory]
    [InlineData("pbkdf2-weak")]
    [InlineData("argon2id")]
    public void WeakProtectionOpensWhenTheUserAgreed(string kdf)
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), kdf);
        Assert.Throws<WeakProtectionException>(() => OsaReader.Open(path, Enforced(() => Pw)));
        using var r = OsaReader.Open(path, new OsaOpenOptions { Password = () => Pw, Protection = ProtectionPolicy.AllowWeak });
        Assert.Equal((3, 0), (Extractor.Test(r, null, Ct).FilesOk, 0));
    }

    [Fact]
    public void SalvageRefusesWeakProtectionUnlessAllowed()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), "pbkdf2-weak");
        int asked = 0;
        Assert.Throws<WeakProtectionException>(() => SalvageKdfTests.Salvage(path, t.Sub("out"), () => { asked++; return Pw; },
            new SalvageOptions { Protection = ProtectionPolicy.Enforce, MemoryBudget = 1L << 30 }));
        Assert.Equal(0, asked);
        Assert.False(Directory.Exists(t.Sub("out")));
        var r = SalvageKdfTests.Salvage(path, t.Sub("out"), () => Pw, new SalvageOptions { Protection = ProtectionPolicy.AllowWeak, MemoryBudget = 1L << 30 });
        SalvageKdfTests.AssertRestored(r, t.Sub("out"));
    }

    // ------------------------------------------------------------------ item 3: no room for plaintext beside the ciphertext

    private static OsaOpenOptions Allowed(string? password = Pw) => new() { Password = () => password, Protection = ProtectionPolicy.AllowWeak };

    /// <summary>Refused under Osmium's own policy (M1 or M3 as given); opens with a warning when the user agreed.</summary>
    internal static void AssertRoomRefusedUnlessAllowed(string path, bool mayBeDamage, string? message = null)
    {
        using (TestDefaults.AsOsmiumDoes())
        {
            var e = Assert.Throws<UnprotectedDataException>(() => OsaReader.Open(path, () => Pw));
            Assert.Equal(mayBeDamage, e.MayBeDamage);
            Assert.Equal(message ?? (mayBeDamage ? UnprotectedDataException.MayBeDamageMessage : ModifiedArchiveException.DefaultMessage), e.Message);
            Assert.Throws<UnprotectedDataException>(() => ArchiveSources.Open(path, new ArchiveOpenOptions { Password = () => Pw }, Ct));
            using var r = OsaReader.Open(path, Allowed());
            Assert.Contains(OsaReader.IgnoredUnprotectedData, r.Warnings);
            using var s = ArchiveSources.Open(path, new ArchiveOpenOptions { Password = () => Pw, AllowWeakProtection = true }, Ct);
            Assert.Contains(OsaReader.IgnoredUnprotectedData, s.Warnings);
        }
    }

    /// <param name="kdf">"pbkdf2" (100,000 iterations), "argon2id" (small: weak, so refused under Osmium's own policy
    /// before any check after the password), "floor" (the lightest Argon2id Osmium accepts), or "none".</param>
    internal static string WriteRoom(TempDir t, string kdf, OsaWriterOptions? extra = null, SpareCopyPolicy spares = SpareCopyPolicy.Never)
    {
        var o = kdf == "none" ? new OsaWriterOptions { SpareCopies = spares }
            : kdf == "floor" ? Options("argon2id", spares) with { Kdf = KdfPresets.Floor }
            : Options(kdf, spares);
        if (extra is not null) o = o with { TestExtraHeaderRecords = extra.TestExtraHeaderRecords, TestEncryptionRecordSuffix = extra.TestEncryptionRecordSuffix };
        return EncryptedArchives.WriteWithFolder(t.Sub($"{kdf}-{Guid.NewGuid():N}.osa"), o, 3000, 3000);
    }

    public static TheoryData<string> Kdfs => ["pbkdf2", "argon2id"];

    [Theory]
    [MemberData(nameof(Kdfs))]
    public void AnIgnorableHeaderRecordOfAnUnknownTypeIsRoomInAnEncryptedArchiveOnly(string kdf)
    {
        using var t = new TempDir();
        var record = new OsaWriterOptions { TestExtraHeaderRecords = [(4, "hello, anyone"u8.ToArray())] };
        AssertRoomRefusedUnlessAllowed(WriteRoom(t, kdf, record), mayBeDamage: false);
        using var plain = OsaReader.Open(WriteRoom(t, "none", record)); // FORMAT §9: unencrypted archives keep accepting it
        Assert.Empty(plain.Warnings);
    }

    [Theory]
    [MemberData(nameof(Kdfs))]
    public void AnUnusualCreatorRecordIsRoom(string kdf)
    {
        using var t = new TempDir();
        AssertRoomRefusedUnlessAllowed(WriteRoom(t, kdf, new OsaWriterOptions { TestExtraHeaderRecords = [(2, new byte[65].Select(_ => (byte)'x').ToArray())] }), mayBeDamage: false);
        AssertRoomRefusedUnlessAllowed(WriteRoom(t, kdf, new OsaWriterOptions { TestExtraHeaderRecords = [(2, "Osmium\n0.2.0"u8.ToArray())] }), mayBeDamage: false);
        using var plain = OsaReader.Open(WriteRoom(t, "none", new OsaWriterOptions { TestExtraHeaderRecords = [(2, "Osmium\n0.2.0"u8.ToArray())] }));
        Assert.Empty(plain.Warnings);
    }

    [Fact]
    public void BytesAfterThePbkdf2VerifierAreRoom()
    {
        using var t = new TempDir();
        AssertRoomRefusedUnlessAllowed(WriteRoom(t, "pbkdf2", new OsaWriterOptions { TestEncryptionRecordSuffix = [0x42] }), mayBeDamage: false);
    }

    [Theory]
    [MemberData(nameof(Kdfs))]
    public void ARecordInBothHeadersIsRoomWhenTheHeaderCopyStandsIn(string kdf)
    {
        using var t = new TempDir();
        string path = WriteRoom(t, kdf, new OsaWriterOptions { TestExtraHeaderRecords = [(4, "room"u8.ToArray())] }, SpareCopyPolicy.Always);
        var b = File.ReadAllBytes(path);
        b[12] ^= 0x04; // header 0 damaged (its CRC fails): the copy, which holds the record too, stands in
        File.WriteAllBytes(path, b);
        AssertRoomRefusedUnlessAllowed(path, mayBeDamage: false);
        using var r = OsaReader.Open(path, Allowed());
        Assert.Contains(OsaReader.UsedSpareHeader, r.Warnings);
    }

    [Theory]
    [MemberData(nameof(Kdfs))]
    public void ARecordInHeaderZeroWithACleanCopyIsRefusedAndNotTakenForDamage(string kdf)
    {
        using var t = new TempDir();
        string clean = WriteRoom(t, kdf, spares: SpareCopyPolicy.Always);
        // header 0 replaced by one with a record more (its CRC is right); H is the original, clean header
        var original = ArchiveHeader.Read(new MemoryStream(File.ReadAllBytes(clean)));
        var records = new List<(ulong, byte[])> { (1, original.Encryption!.RecordBytes) };
        if (original.Creator is { } c) records.Add((2, System.Text.Encoding.UTF8.GetBytes(c)));
        records.Add((4, "room"u8.ToArray()));
        var changed = HeaderKdfTests.Header(original.Flags, [.. records]);
        original.ArchiveId.CopyTo(changed, 16);
        EncryptedArchives.EditHeader(changed, _ => { });
        string path = t.Sub("changed.osa");
        File.WriteAllBytes(path, EncryptedArchives.Graft(changed, File.ReadAllBytes(clean)));
        using (TestDefaults.AsOsmiumDoes())
        {
            var e = Assert.Throws<UnprotectedDataException>(() => OsaReader.Open(path, () => Pw));
            Assert.False(e.MayBeDamage); // the record is reported first, not "used its spare copy"
        }
    }

    /// <summary>A copy of <paramref name="path"/> with <paramref name="change"/> applied to its bytes.</summary>
    internal static string Variant(TempDir t, string path, string name, Action<byte[]> change)
    {
        var b = File.ReadAllBytes(path);
        change(b);
        string p = t.Sub(name + "-" + Guid.NewGuid().ToString("N")[..6] + ".osa");
        File.WriteAllBytes(p, b);
        return p;
    }

    internal static readonly byte[] Marker = "KNOWN-PLAINTEXT-MARKER"u8.ToArray();

    /// <summary>The marker in the body of B, the spare copy of the index frame (its frame header still checks out).</summary>
    internal static string MarkerInSpareFileList(TempDir t, string path)
    {
        var tail = SpareArchives.TailOf(path, Pw);
        return Variant(t, path, "spare", b => Marker.CopyTo(b, tail.SpareOffset + Osa.FrameHeaderSize + 40));
    }

    [Fact]
    public void BytesInTheSpareFileListAreRoom() =>
        BytesInTheSpareFileListAreRoomFor("pbkdf2"); // Argon2id: in the heavy collection (it needs settings above the minimum)

    internal static void BytesInTheSpareFileListAreRoomFor(string kdf)
    {
        using var t = new TempDir();
        string path = WriteRoom(t, kdf, spares: SpareCopyPolicy.Always);
        AssertRoomRefusedUnlessAllowed(MarkerInSpareFileList(t, path), mayBeDamage: true);
    }

    [Theory]
    [MemberData(nameof(Kdfs))]
    public void BytesInTheHeaderCopysPaddingOrANewerTrailerAreRoom(string kdf)
    {
        using var t = new TempDir();
        string path = WriteRoom(t, kdf, spares: SpareCopyPolicy.Always);
        var tail = SpareArchives.TailOf(path, Pw);
        var marker = Marker;
        string Variant(string name, Action<byte[]> change) => ReaderPolicyTests.Variant(t, path, name, change);
        // in H's padding (after the header bytes, before the trailer)
        AssertRoomRefusedUnlessAllowed(Variant("padding", b => marker.CopyTo(b, tail.HeaderCopyOffset + tail.HeaderLength + 8)), mayBeDamage: true);
        // a trailer flag set, its CRC right: a newer writer's H, which in an encrypted archive is room
        AssertRoomRefusedUnlessAllowed(Variant("trailer", b =>
        {
            var trailer = b.AsSpan((int)tail.TrailerOffset, SpareCopies.TrailerSize);
            trailer[4] = 1;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(trailer[28..], Osa.Crc(trailer[..28]));
        }), mayBeDamage: true);
        // the same bytes in an unencrypted archive: it opens, as before (Test reports the damaged copies)
        string plainPath = WriteRoom(t, "none", spares: SpareCopyPolicy.Always);
        var plainTail = SpareArchives.TailOf(plainPath, null);
        var pb = File.ReadAllBytes(plainPath);
        marker.CopyTo(pb, plainTail.HeaderCopyOffset + plainTail.HeaderLength + 8);
        File.WriteAllBytes(plainPath, pb);
        using var plain = OsaReader.Open(plainPath);
        Assert.Empty(plain.Warnings);
    }

    /// <summary>Re-encodes and re-seals the file list after <paramref name="change"/> edits its raw bytes (padded again
    /// for Argon2id archives).</summary>
    private static void RewriteIndex(string path, Func<byte[], byte[]> change)
    {
        var header = ArchiveHeader.Read(new MemoryStream(File.ReadAllBytes(path)));
        bool pad = header.Encryption!.Kdf.Algorithm == KdfAlgorithm.Argon2id;
        EncryptedArchives.ResealIndex(path, plain =>
        {
            var body = BlockBody.Parse(plain, MethodRegistry.Default, "the index", paddingAllowed: pad);
            var raw = change(PipelineExecutor.Decode(body.Descriptor, body.Payload, CodecContext.None));
            var (desc, payload) = PipelineExecutor.Encode(body.Descriptor.Spec, raw, CodecContext.None);
            var prefix = new BlockBody(desc, BlockBody.Hash128(raw), payload).SerializePrefix();
            var result = new byte[pad ? BlockBody.PaddedLength(prefix.Length + payload.Length) : prefix.Length + payload.Length];
            prefix.CopyTo(result, 0);
            payload.CopyTo(result, prefix.Length);
            return result;
        });
    }

    [Fact]
    public void AGapBeforeTheFirstBlockIsRoom() =>
        AGapBeforeTheFirstBlockIsRoomFor("pbkdf2"); // Argon2id: in the heavy collection (it needs settings above the minimum)

    internal static void AGapBeforeTheFirstBlockIsRoomFor(string kdf)
    {
        using var t = new TempDir();
        string written = WriteRoom(t, kdf);
        var b = File.ReadAllBytes(written);
        var header = ArchiveHeader.Read(new MemoryStream(b));
        // one byte after the header, and an index (re-sealed with the key) whose first block says gapBefore = 1
        var shifted = (byte[])[.. b.AsSpan(0, header.Length), 0x7A, .. b.AsSpan(header.Length)];
        long offset = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(shifted.AsSpan(shifted.Length - 32));
        long length = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(shifted.AsSpan(shifted.Length - 24));
        new ArchiveFooter(offset + 1, length, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(header.ArchiveId)).Serialize().CopyTo(shifted, shifted.Length - 32);
        string path = t.Sub("gap.osa");
        File.WriteAllBytes(path, shifted);
        RewriteIndex(path, raw =>
        {
            var r = new IO.BlobReader(raw);
            r.U(); // index version
            r.U(); // block count
            r.U(); // length of the first block record
            Assert.Equal(0, raw[r.Position]); // its gapBefore
            raw[r.Position] = 1;
            return raw;
        });
        AssertRoomRefusedUnlessAllowed(path, mayBeDamage: false);
        using var opened = OsaReader.Open(path, Allowed());
        Assert.Equal(header.Length + 1, opened.Blocks[0].Offset);
        Assert.Equal((3, 0), (Extractor.Test(opened, null, Ct).FilesOk, 0));
    }

    [Theory]
    [MemberData(nameof(Kdfs))]
    public void BytesAfterTheFooterAreStillRefused(string kdf)
    {
        using var t = new TempDir();
        string path = WriteRoom(t, kdf);
        var original = File.ReadAllBytes(path);
        // appended: the end of the file is no footer
        File.WriteAllBytes(path, [.. original, .. "after the end"u8]);
        Assert.Equal(OsaReader.EndDamaged, Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(path, Allowed())).Message);
        // appended before a copy of the footer: the footer no longer places the index frame at the end
        File.WriteAllBytes(path, [.. original, .. "after the end"u8, .. original.AsSpan(original.Length - Osa.FooterSize)]);
        Assert.Contains("Index location is invalid", Assert.ThrowsAny<CorruptDataException>(() => OsaReader.Open(path, Allowed())).Message);
    }

    [Theory]
    [MemberData(nameof(Kdfs))]
    public void ChangedKeyDerivationSettingsWithAnIntactCopySayTheCopiesDifferNotWrongPassword(string kdf)
    {
        using var t = new TempDir();
        string path = WriteRoom(t, kdf, spares: SpareCopyPolicy.Always);
        var b = File.ReadAllBytes(path);
        // one more iteration (PBKDF2: 100,000 is three bytes either way), or one more KiB (Argon2id: 64 is one byte)
        EncryptedArchives.EditHeader(b, h => h[kdf == "pbkdf2" ? 39 : 40]++);
        File.WriteAllBytes(path, b);
        using (TestDefaults.AsOsmiumDoes())
        {
            int asked = 0;
            var e = Assert.Throws<UnprotectedDataException>(() => OsaReader.Open(path, () => { asked++; return Pw; }));
            Assert.Equal(UnprotectedDataException.CopiesDifferMessage, e.Message);
            Assert.True(e.MayBeDamage);
            Assert.Equal(0, asked);
        }
        // with the user's agreement, the copy's settings are tried too, and the header that authenticates the file list wins
        int calls = 0;
        using var r = OsaReader.Open(path, new OsaOpenOptions { Password = () => { calls++; return Pw; }, Protection = ProtectionPolicy.AllowWeak });
        Assert.Equal(1, calls); // the password is asked for once
        Assert.Contains(OsaReader.UsedTheHeaderThatAuthenticates, r.Warnings);
        Assert.Equal((3, 0), (Extractor.Test(r, null, Ct).FilesOk, 0));
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

/// <summary>The lightest protection a reader accepts without asking (256 MiB): in the heavy collection.</summary>
[Collection(HeavyKeyDerivation.Name)]
public class MinimumProtectionHeavyTests
{
    [Fact]
    public void BytesInTheSpareFileListOfAnArgon2idArchiveAreRoom()
    {
        Argon2idHeavyTests.SkipUnlessMemoryFor(KdfPresets.Floor.MemoryBytes);
        ReaderPolicyTests.BytesInTheSpareFileListAreRoomFor("floor");
    }

    [Fact]
    public void AGapBeforeTheFirstBlockOfAnArgon2idArchiveIsRoom()
    {
        Argon2idHeavyTests.SkipUnlessMemoryFor(KdfPresets.Floor.MemoryBytes);
        ReaderPolicyTests.AGapBeforeTheFirstBlockIsRoomFor("floor");
    }

    [Fact]
    public void TheLightestPresetOpensUnderOsmiumsOwnPolicy()
    {
        Argon2idHeavyTests.SkipUnlessMemoryFor(KdfPresets.Floor.MemoryBytes);
        using var t = new TempDir();
        using (TestDefaults.AsOsmiumDoes())
        {
            string path = WriterSafetyTests.Write(t.Sub("a.osa"), new OsaWriterOptions { Password = EncryptedArchives.Password, Kdf = KdfPresets.Floor });
            using var r = OsaReader.Open(path, new OsaOpenOptions { Password = () => EncryptedArchives.Password, KdfMemoryLimit = 1L << 30 });
            Assert.Equal(KdfPresets.Floor, r.Header.Encryption!.Kdf);
        }
    }
}
