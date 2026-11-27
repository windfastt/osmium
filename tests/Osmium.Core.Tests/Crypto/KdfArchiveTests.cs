using System.Buffers.Binary;
using System.Reflection;
using Osmium.Core.Container;
using Osmium.Core.Crypto;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Crypto;

/// <summary>Encrypted archives made and read for tests: small Argon2id settings, and edits re-sealed with their key.</summary>
internal static class EncryptedArchives
{
    public const string Password = "encrypted archives";
    public static readonly KdfParameters Small = KdfParameters.Argon2id(1, 64, 1);

    public static OsaWriterOptions Options(string kdf, SpareCopyPolicy spares = SpareCopyPolicy.Never) => new()
    {
        Password = Password, SpareCopies = spares,
        Kdf = kdf == "pbkdf2" ? KdfParameters.Pbkdf2(100_000) : kdf == "pbkdf2-weak" ? KdfParameters.Pbkdf2(1000) : Small,
    };

    public static string Write(string path, string kdf, SpareCopyPolicy spares = SpareCopyPolicy.Never, int blocks = 3, int size = 3000) =>
        WriterSafetyTests.Write(path, Options(kdf, spares), blocks, size);

    /// <summary>The contents <see cref="WriterSafetyTests.Write"/> gives file i.</summary>
    public static byte[] Content(int i, int size = 3000)
    {
        var data = new byte[size + i];
        new Random(i).NextBytes(data);
        return data;
    }

    /// <summary>
    /// Decrypts the index frame (A) of an archive without spare copies, lets <paramref name="change"/> replace its
    /// plaintext body, seals it again with the archive's key and writes it back with its frame header and the footer.
    /// </summary>
    public static void ResealIndex(string path, Func<byte[], byte[]> change, string password = Password)
    {
        var bytes = File.ReadAllBytes(path);
        var header = ArchiveHeader.Read(new MemoryStream(bytes));
        long offset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(bytes.Length - 32));
        long length = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(bytes.Length - 24));
        using var key = ArchiveKey.Derive(password, header.Encryption!);
        var frame = bytes.AsSpan((int)offset, (int)length);
        var plain = key.Open(header.ArchiveId, Osa.IndexFrameNumber, frame[Osa.FrameHeaderSize..], header.Raw);
        var body = key.Seal(header.ArchiveId, Osa.IndexFrameNumber, change(plain), header.Raw);
        var fh = frame[..Osa.FrameHeaderSize].ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(fh.AsSpan(16), body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(fh.AsSpan(24), Osa.Crc(fh.AsSpan(0, 24)));
        var footer = new ArchiveFooter(offset, Osa.FrameHeaderSize + body.Length, BinaryPrimitives.ReadUInt64LittleEndian(header.ArchiveId)).Serialize();
        File.WriteAllBytes(path, [.. bytes.AsSpan(0, (int)offset), .. fh, .. body, .. footer]);
    }

    /// <summary>Replaces the header's EncryptionInfo value with <paramref name="record"/> (of the same length) and fixes the
    /// CRC: the index no longer authenticates, but every check before the password comes first.</summary>
    public static void ReplaceRecord(string path, byte[] record)
    {
        var bytes = File.ReadAllBytes(path);
        var header = ArchiveHeader.Read(new MemoryStream(bytes));
        var records = new List<(ulong, byte[])> { (1, record) };
        if (header.Creator is { } creator) records.Add((2, System.Text.Encoding.UTF8.GetBytes(creator)));
        var changed = HeaderKdfTests.Header(header.Flags, [.. records]);
        BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(10), header.Minor);
        header.ArchiveId.CopyTo(changed, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(changed.Length - 4), Osa.Crc(changed.AsSpan(0, changed.Length - 4)));
        Assert.Equal(header.Length, changed.Length);
        File.WriteAllBytes(path, [.. changed, .. bytes.AsSpan(header.Length)]);
    }

    public static byte[] Argon2Record(int t, int m, int p) =>
        new IO.BlobWriter().B(2).U((ulong)t).U((ulong)m).U((ulong)p).Raw(new byte[32]).B(1).Raw(new byte[16]).ToArray();
}

public class KdfArchiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OsaOpenOptions Open(Func<string?> password, long limit = 1L << 30, ProtectionPolicy policy = ProtectionPolicy.Default) =>
        new() { Password = password, KdfMemoryLimit = limit, Protection = policy, Cancellation = Ct };

    [Theory]
    [InlineData("pbkdf2", SpareCopyPolicy.Never)]
    [InlineData("pbkdf2", SpareCopyPolicy.Always)]
    [InlineData("argon2id", SpareCopyPolicy.Never)]
    [InlineData("argon2id", SpareCopyPolicy.Always)]
    public void BothFunctionsRoundTrip(string kdf, SpareCopyPolicy spares)
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), kdf, spares);
        using var r = OsaReader.Open(path, Open(() => EncryptedArchives.Password));
        Assert.Equal(kdf == "pbkdf2" ? KdfAlgorithm.Pbkdf2Sha512 : KdfAlgorithm.Argon2id, r.Header.Encryption!.Kdf.Algorithm);
        Assert.Equal(spares == SpareCopyPolicy.Always, r.HasSpareCopies);
        Assert.Empty(r.Warnings);
        var x = Extractor.Extract(r, t.Sub("out"), null, null, null, Ct);
        Assert.Equal((3, 0), (x.FilesOk, x.FilesFailed));
        for (int i = 0; i < 3; i++) Assert.Equal(EncryptedArchives.Content(i), File.ReadAllBytes(Path.Combine(t.Sub("out"), $"f{i}.bin")));
        var test = Extractor.Test(r, null, Ct);
        Assert.Equal((3, 0), (test.FilesOk, test.FilesFailed));
        Assert.Empty(test.Warnings);
    }

    [Fact]
    public void AWrongPasswordCostsOneDerivationAndReadsNoFrame()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), "argon2id");
        using var seams = new KdfSeams();
        int frames = 0;
        OsaReader.FrameRead = _ => Interlocked.Increment(ref frames);
        try
        {
            Assert.Throws<WrongPasswordException>(() => OsaReader.Open(path, Open(() => "not it")));
        }
        finally { OsaReader.FrameRead = null; }
        Assert.Equal((1, 0), (seams.Allocations, frames));
    }

    [Theory]
    [InlineData("")]
    [InlineData("surrogate")]
    public void APasswordThatCannotBeTextIsWrongWithoutADerivation(string which)
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), "argon2id");
        using var seams = new KdfSeams();
        string password = which == "" ? "" : "x" + "\uDC00";
        Assert.Throws<WrongPasswordException>(() => OsaReader.Open(path, Open(() => password)));
        Assert.Equal(0, seams.Allocations);
    }

    [Fact]
    public void AMemoryRefusalNeverAsksForThePasswordAndARaisedLimitAsksOnce()
    {
        using var t = new TempDir();
        string path = WriterSafetyTests.Write(t.Sub("a.osa"), new OsaWriterOptions { Password = EncryptedArchives.Password, Kdf = KdfParameters.Argon2id(1, 8 << 10, 1) });
        using var seams = new KdfSeams();
        int asked = 0;
        string Ask() { asked++; return EncryptedArchives.Password; }
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<KdfMemoryLimitException>(() => OsaReader.Open(path, Open(Ask, limit: 4L << 20)));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
        Assert.Equal((8L << 20, 4L << 20), (e.NeededBytes, e.LimitBytes));
        Assert.Equal((0, 0L), (asked, seams.Allocations));
        using (OsaReader.Open(path, Open(Ask, limit: e.NeededBytes))) { }
        Assert.Equal((1, 1L), (asked, seams.Allocations));
    }

    [Fact]
    public void ACostRefusalNeverAsksForThePassword()
    {
        using var t = new TempDir();
        // 16 MiB and 1 GiB both take three bytes as varints, so the header keeps its length
        string path = WriterSafetyTests.Write(t.Sub("a.osa"), new OsaWriterOptions { Password = EncryptedArchives.Password, Kdf = KdfParameters.Argon2id(1, 16 << 10, 1) });
        EncryptedArchives.ReplaceRecord(path, EncryptedArchives.Argon2Record(16, 1 << 20, 4)); // 16 GiB-passes
        int asked = 0;
        string Ask() { asked++; return EncryptedArchives.Password; }
        var e = Assert.Throws<KdfCostException>(() => OsaReader.Open(path, Open(Ask, limit: 2L << 30)));
        Assert.Equal(KdfParameters.Argon2id(16, 1 << 20, 4), e.Parameters);
        // allowing the slow derivation lifts only that question: the memory limit still applies first
        Assert.Throws<KdfMemoryLimitException>(() => OsaReader.Open(path, Open(Ask, limit: 512L << 20) with { AllowSlowKdf = true }));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void WeakProtectionIsRefusedBeforeThePassword()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), "pbkdf2-weak");
        int asked = 0;
        string Ask() { asked++; return EncryptedArchives.Password; }
        var e = Assert.Throws<WeakProtectionException>(() => OsaReader.Open(path, Open(Ask, policy: ProtectionPolicy.Enforce)));
        Assert.Equal(KdfParameters.Pbkdf2(1000), e.Parameters);
        Assert.Equal(0, asked);
        using (OsaReader.Open(path, Open(Ask, policy: ProtectionPolicy.AllowWeak))) { }
        Assert.Equal(1, asked);
    }

    [Fact]
    public void ACancelledDerivationOpensNothing()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), "argon2id");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => OsaReader.Open(path, Open(() => EncryptedArchives.Password) with { Cancellation = cts.Token }));
        File.Delete(path); // not held open
    }

    [Fact]
    public void TheReaderZeroesItsKey()
    {
        using var t = new TempDir();
        string path = EncryptedArchives.Write(t.Sub("a.osa"), "argon2id");
        var r = OsaReader.Open(path, Open(() => EncryptedArchives.Password));
        var key = (ArchiveKey)typeof(OsaReader).GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r)!;
        Assert.False(key.IsDisposed);
        r.Dispose();
        Assert.True(key.IsDisposed);
    }

    [Fact]
    public void PaddingIsReadOnlyWhereTheFormatHasIt()
    {
        using var t = new TempDir();
        // Argon2id: a padding byte that is not zero is damage.
        string argon = EncryptedArchives.Write(t.Sub("a.osa"), "argon2id");
        EncryptedArchives.ResealIndex(argon, plain =>
        {
            long exact = WriterSafetyTests.ExactLength(plain);
            Assert.True(plain.Length > exact);
            plain[^1] = 1;
            return plain;
        });
        Assert.Equal("Block padding is not zero.", Assert.Throws<CorruptDataException>(() => OsaReader.Open(argon, Open(() => EncryptedArchives.Password))).Message);
        // PBKDF2: an index that is padded at all is damage.
        string pbkdf2 = EncryptedArchives.Write(t.Sub("b.osa"), "pbkdf2");
        EncryptedArchives.ResealIndex(pbkdf2, plain => [.. plain, 0, 0, 0, 0]);
        Assert.Equal("Block payload length mismatch.", Assert.Throws<CorruptDataException>(() => OsaReader.Open(pbkdf2, Open(() => EncryptedArchives.Password))).Message);
    }

    /// <summary>
    /// An archive as the writer made it before Argon2id: PBKDF2 (100,000 iterations), a Creator record in the header
    /// and none in the file list, fixed salt and archive id, two files of 40 and 41 bytes.
    /// </summary>
    private const string KeptPbkdf2Archive =
        "T1NNQRoNCgABAAEAAQAAAGBhYmNkZWZnaGlqa2xtbm9FAAAAATUBoI0GgIGCg4SFhoeIiYqLjI2Oj5CRkpOUlZaXmJmam5ydnp8BotqaFXh4ImTp"
        + "iel+7wmbwwIMT3NtaXVtIDAuMi4wK/GZsk9CTEsBAAAAAAAAAAAAAABbAAAAAAAAAPB13mbqmalzV27tv/RCaXQNFFsDJvcopmbTmqUQycXOop0J"
        + "aj9sSc24AgfLiGG+UGzicvt5BtPeEHJlF2I8U+TC2zqZv1XcvR2ovUeChVQDuxLbBeCzuxOrseiU0WhpT0JMSwEAAAABAAAAAAAAAFwAAAAAAAAA"
        + "eO1zwk0aVkfT1yDVL0SOKAzBzW58vEsiCdZ/7JF9fkLKYotEgAcqvvyupF98LZE+3i7h7IAsvvzz4pOfEQRZCUJ1+Ea8WgehKjwuBKhXuoELMk3A"
        + "HevGdHx7ljhLnFJfT0JMSwMAAAD//////////84AAAAAAAAA0zQeXI/zaHtNtXWeXyrFoL8n1XsRgYwIuJEJF6CrSp2YnF63T20j51G5+dt/8VLk"
        + "+Ig5+IMFL8hg9/nkOkxbHXBkvjo8zBbbXwQ1HgaCwME82wsEYbJoUV1uN4EH4UUppdqpE+Z3lOwSSdHFwjWtWmkKG49aLRNmzcuwwdFbN2Mbg4oD"
        + "+NzdaRzAvT55Iyv6QqwD6bCGvrdquWIpM6L7ne6pNn4T/pPqM6H2yWasBTti3TTVG3/CbJ+p2fr5dAYQNqtX4cqxWT+DAdwk2wmsXUAsXAEAAAAA"
        + "AADqAAAAAAAAAGBhYmNkZWZnocisN09TQUU=";

    [Fact]
    public void APbkdf2ArchiveOfTheEarlierWriterKeepsOpening()
    {
        using var t = new TempDir();
        string path = t.Sub("kept.osa");
        File.WriteAllBytes(path, Convert.FromBase64String(KeptPbkdf2Archive));
        using var r = OsaReader.Open(path, Open(() => "kept as bytes", policy: ProtectionPolicy.Enforce));
        Assert.Equal(KdfParameters.Pbkdf2(100_000), r.Header.Encryption!.Kdf);
        Assert.Equal("Osmium 0.2.0", r.Header.Creator);
        Assert.Empty(r.Warnings);
        var x = Extractor.Extract(r, t.Sub("out"), null, null, null, Ct);
        Assert.Equal((2, 0), (x.FilesOk, x.FilesFailed));
        for (int i = 0; i < 2; i++) Assert.Equal(EncryptedArchives.Content(i, 40), File.ReadAllBytes(Path.Combine(t.Sub("out"), $"f{i}.bin")));
        Assert.Throws<WrongPasswordException>(() => OsaReader.Open(path, Open(() => "kept as byte")));
    }
}

/// <summary>The default protection at its real size: one derivation of 1 GiB, so alone in its collection.</summary>
[Collection(HeavyKeyDerivation.Name)]
public class DefaultProtectionTests
{
    [Fact]
    public void ANewEncryptedArchiveIsStandardArgon2idWithAPaddedFileListAndNoNewFlag()
    {
        Argon2idHeavyTests.SkipUnlessMemoryFor(KdfPresets.Standard.MemoryBytes);
        using var t = new TempDir();
        string path = WriterSafetyTests.Write(t.Sub("a.osa"), new OsaWriterOptions { Password = EncryptedArchives.Password, SpareCopies = SpareCopyPolicy.Always });
        var (header, plain, a, b) = WriterSafetyTests.IndexFrames(path, EncryptedArchives.Password);
        Assert.Equal(KdfPresets.Standard, header.Encryption!.Kdf);
        Assert.Equal(0x0001u, header.Flags);
        Assert.Equal(BlockBody.PaddedLength(WriterSafetyTests.ExactLength(plain)), plain.Length);
        Assert.True(a.AsSpan().SequenceEqual(b));
    }
}
