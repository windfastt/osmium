using Osmium.Core.Archives;
using Osmium.Core.Container;
using Osmium.Core.Crypto;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Crypto;

/// <summary>
/// "Hide exact sizes" (FORMAT §5 item 6): every block frame of an Argon2id archive padded with zeros to its Padmé
/// length, and header flag bit 3 set; only zero padding, only with bit 3, and bit 3 only in Argon2id archives.
/// </summary>
public class SizeHidingTests
{
    private const string Pw = EncryptedArchives.Password;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly int[] Sizes = [3000, 5000, 70_000];

    private static OsaWriterOptions Padded => EncryptedArchives.Options("argon2id") with { PadBlocks = true };

    /// <summary>The plaintext length of each block's body: what the frame holds less the tag.</summary>
    private static List<(long Plain, long Exact)> BodyLengths(string path)
    {
        using var r = OsaReader.Open(path, () => Pw);
        return [.. r.Blocks.Select(b =>
        {
            var body = r.ReadBody(b.Frame);
            long exact = new BlockBody(body.Descriptor, body.RawHash, body.Payload).SerializePrefix().Length + body.Payload.Length;
            return (b.FrameLength - Osa.FrameHeaderSize - 16, exact);
        })];
    }

    [Fact]
    public void EveryBlockIsPaddedToItsPadmeLengthAndTheHeaderSaysSo()
    {
        using var t = new TempDir();
        string padded = EncryptedArchives.WriteWithFolder(t.Sub("padded.osa"), Padded, Sizes);
        string exact = EncryptedArchives.WriteWithFolder(t.Sub("exact.osa"), EncryptedArchives.Options("argon2id"), Sizes);
        Assert.All(BodyLengths(padded), b => Assert.Equal(BlockBody.PaddedLength(b.Exact), b.Plain));
        Assert.Contains(BodyLengths(padded), b => b.Plain > b.Exact); // not all lengths are Padmé lengths already
        Assert.All(BodyLengths(exact), b => Assert.Equal(b.Exact, b.Plain));
        using (var r = OsaReader.Open(padded, () => Pw))
        {
            Assert.Equal(Osa.FlagEncrypted | Osa.FlagPaddedBlocks, r.Header.Flags);
            var x = Extractor.Extract(r, t.Sub("out"), null, null, null, Ct);
            Assert.Equal((4, 0), (x.FilesOk, x.FilesFailed));
            for (int i = 0; i < Sizes.Length; i++)
                Assert.Equal(EncryptedArchives.Content(i, Sizes[i] - i), File.ReadAllBytes(Path.Combine(t.Sub("out"), $"f{i}.bin")));
        }
        using (var r = OsaReader.Open(exact, () => Pw)) Assert.Equal(Osa.FlagEncrypted, r.Header.Flags);
    }

    [Theory]
    [InlineData("pbkdf2")]
    [InlineData("none")]
    public void OnlyArgon2idArchivesCanHideExactSizes(string kdf)
    {
        using var t = new TempDir();
        var options = (kdf == "none" ? new OsaWriterOptions() : EncryptedArchives.Options(kdf)) with { PadBlocks = true };
        var e = Assert.Throws<ArgumentException>(() => new OsaWriter(t.Sub("a.osa"), options));
        Assert.Equal(OsaWriter.PadBlocksNeedsArgon2id, e.Message);
        Assert.Throws<ArgumentException>(() => OsaWriter.CheckProtection(options));
        Assert.Empty(Directory.GetFileSystemEntries(t.Path)); // refused before anything was written
    }

    [Theory]
    [InlineData("pbkdf2")]
    [InlineData("none")]
    public void Bit3InAnArchiveThatIsNotArgon2idIsDamage(string kdf)
    {
        using var t = new TempDir();
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), kdf == "none" ? new OsaWriterOptions() : EncryptedArchives.Options(kdf), 3000);
        string changed = ReaderPolicyTests.Variant(t, path, "bit3", b => EncryptedArchives.EditHeader(b, h => h[12] |= (byte)Osa.FlagPaddedBlocks));
        int asked = 0;
        var e = Assert.Throws<CorruptDataException>(() => OsaReader.Open(changed, () => { asked++; return Pw; }));
        Assert.Equal(ArchiveHeader.PaddedBlocksNeedArgon2id, e.Message);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void AFlagAfterBit3IsStillOneOfANewerVersion()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), Padded, 3000);
        string changed = ReaderPolicyTests.Variant(t, path, "bit4", b => EncryptedArchives.EditHeader(b, h => h[12] |= 0x10));
        var e = Assert.ThrowsAny<NewerVersionException>(() => OsaReader.Open(changed, () => Pw));
        Assert.Equal("This archive needs a newer version of Osmium, or it was damaged or changed (unknown archive flag 0x0010).", e.Message);
    }

    [Fact]
    public void PaddedBlocksWithoutBit3AreDamage()
    {
        // The header loses bit 3 and the file list is sealed again over it, so it authenticates; its blocks are padded.
        using var t = new TempDir();
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), Padded, Sizes);
        EncryptedArchives.RebindHeader(path, h => h[12] &= unchecked((byte)~Osa.FlagPaddedBlocks));
        using var r = OsaReader.Open(path, () => Pw);
        Assert.Equal(Osa.FlagEncrypted, r.Header.Flags);
        var x = Extractor.Test(r, null, Ct);
        Assert.Equal((1, 3), (x.FilesOk, x.FilesFailed)); // the empty file only
        Assert.Contains("block 0: Block payload length mismatch.", x.Errors);
    }

    [Fact]
    public void APaddingByteThatIsNotZeroIsDamageOfItsBlock()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), Padded, Sizes);
        EncryptedArchives.ResealBlock(path, 1, plain => plain[^1] = 1); // the last padding byte, sealed with the archive's key
        using var r = OsaReader.Open(path, () => Pw);
        var x = Extractor.Extract(r, t.Sub("out"), null, null, null, Ct);
        Assert.Equal((3, 1), (x.FilesOk, x.FilesFailed));
        Assert.Contains("block 1: Block padding is not zero.", x.Errors);
        Assert.False(File.Exists(Path.Combine(t.Sub("out"), "f1.bin")));
        // salvage reports the block as damaged and does not write it
        var s = SalvageKdfTests.Salvage(path, t.Sub("salvaged"), () => Pw);
        Assert.Equal(SalvageBlockState.Damaged, s.Blocks[1].State);
        Assert.False(File.Exists(Path.Combine(t.Sub("salvaged"), "f1.bin")));
        Assert.True(File.Exists(Path.Combine(t.Sub("salvaged"), "f0.bin")));
    }

    [Theory]
    [InlineData(SpareCopyPolicy.Never)]
    [InlineData(SpareCopyPolicy.Always)]
    public void APaddedArchiveSalvagesCompletelyAndWithItsHeaderDamaged(SpareCopyPolicy spares)
    {
        using var t = new TempDir();
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), Padded with { SpareCopies = spares }, Sizes);
        var whole = SalvageKdfTests.Salvage(path, t.Sub("whole"), () => Pw);
        Assert.Equal(0, whole.ExitCode);
        // the marker damaged, outside the key fields: the header is rebuilt with flag bit 3 (0x0009), or read from its copy
        string damaged = SalvageKdfTests.Damage(path, b => b[1] ^= 0x20);
        var r = SalvageKdfTests.Salvage(damaged, t.Sub("out"), () => Pw);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(4, r.FilesRecovered);
        for (int i = 0; i < Sizes.Length; i++)
            Assert.Equal(EncryptedArchives.Content(i, Sizes[i] - i), File.ReadAllBytes(Path.Combine(t.Sub("out"), $"f{i}.bin")));
    }

    [Fact]
    public void TheArchiveSourceAndTheBuilderPassItOn()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.WriteWithFolder(t.Sub("a.osa"), Padded, Sizes);
        using var s = ArchiveSources.Open(path, new ArchiveOpenOptions { Password = () => Pw }, Ct);
        Assert.Equal("yes", s.Details.Single(d => d.Label == "Exact sizes hidden").Value);
        Assert.Throws<ArgumentException>(() => Planning.ArchiveBuilder.CheckProtection(new Planning.BuildOptions
        {
            OutputPath = t.Sub("b.osa"), Password = Pw, Kdf = KdfParameters.Pbkdf2(100_000), PadBlocks = true,
        }));
    }
}
