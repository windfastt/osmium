using System.Buffers.Binary;
using System.IO.Hashing;
using Osmium.Core.Archives;
using Osmium.Core.Archives.Foreign.SevenZip;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;
using static Osmium.Core.Tests.Foreign.TarAndStreamTests;

namespace Osmium.Core.Tests.Foreign;

/// <summary>7z: every coder and filter, solid and non-solid folders, encryption (data and file names), the password
/// rule for an archive without a verifier, damage, and memory declared in the header.</summary>
public class SevenZipTests
{
    private const string Password = "Pässwörd ü";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IArchiveSource Open(string path, ArchiveOpenOptions? options = null) => ArchiveSources.Open(path, options, Ct);

    private static List<string> FilesUnder(string root) => !Directory.Exists(root) ? []
        : Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    private static void AssertFixtureTree(string root)
    {
        foreach (var (name, data) in ForeignData.FixtureTree)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine([root, .. name.Split('/')])));
        Assert.True(Directory.Exists(Path.Combine(root, "in", "sub", "empty")));
    }

    [Theory]
    [InlineData("lzma2-solid.7z", "LZMA2", true)]
    [InlineData("lzma.7z", "LZMA", false)]
    [InlineData("ppmd.7z", "PPMd", true)]
    [InlineData("bzip2.7z", "BZip2", true)]
    [InlineData("deflate.7z", "Deflate", true)]
    [InlineData("copy.7z", "Copy", false)]
    public void ReadsEveryCoder(string fixture, string method, bool solid)
    {
        using var t = new TempDir();
        using var s = Open(ForeignData.Fixture("7z", fixture));
        Assert.Equal("7z", s.FormatName);
        Assert.Equal(method, s.Items.Single(i => i.Path == "in/hello.txt").Method);
        Assert.Contains(s.Details, d => d.Label == "Solid" && d.Value.StartsWith(solid ? "yes" : "no", StringComparison.Ordinal));
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal((3, 0, 0), (r.FilesOk, r.Unverified, r.LeftOut));
        AssertFixtureTree(t.Sub("out"));
        Assert.Equal(DateTime.UnixEpoch, File.GetLastWriteTimeUtc(Path.Combine(t.Sub("out"), "in", "hello.txt")));
        Assert.Equal((3, 0), (s.Test(null, null, Ct).FilesOk, s.Test(null, null, Ct).LeftOut));
    }

    [Theory]
    [InlineData("bcj.7z", "LZMA2, BCJ")]
    [InlineData("bcj2.7z", "LZMA, LZMA, LZMA, BCJ2")]
    [InlineData("arm.7z", "LZMA2, ARM")]
    [InlineData("armt.7z", "LZMA2, ARMT")]
    [InlineData("arm64.7z", "LZMA2, ARM64")]
    [InlineData("ppc.7z", "LZMA2, PPC")]
    [InlineData("sparc.7z", "LZMA2, SPARC")]
    public void ReadsEveryFilter(string fixture, string methods)
    {
        using var t = new TempDir();
        using var s = Open(ForeignData.Fixture("7z", fixture));
        Assert.Equal(methods, s.Items.Single().Method);
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal((1, 0), (r.FilesOk, r.LeftOut));
        Assert.Equal(ForeignData.FixtureCode(), File.ReadAllBytes(Path.Combine(t.Sub("out"), "code.bin")));
    }

    [Fact]
    public void ReadsTheDeltaFilter()
    {
        using var t = new TempDir();
        using var s = Open(ForeignData.Fixture("7z", "delta.7z"));
        Assert.Equal(1, s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct).FilesOk);
        Assert.Equal(ForeignData.FixtureTree.Single(f => f.Path == "in/sub/numbers.txt").Data, File.ReadAllBytes(Directory.GetFiles(t.Sub("out"), "numbers.txt", SearchOption.AllDirectories).Single()));
    }

    [Fact]
    public void SelectingOneFileOfASolidBlockReadsThroughTheOthers()
    {
        using var t = new TempDir();
        using var s = Open(ForeignData.Fixture("7z", "lzma2-solid.7z"));
        int numbers = s.Items.Single(i => i.Path == "in/sub/numbers.txt").Index;
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest { Indices = new HashSet<int> { numbers } }, null, Ct);
        Assert.Equal(1, r.FilesOk);
        Assert.Equal(["in/sub/numbers.txt"], FilesUnder(t.Sub("out")));
    }

    [Fact]
    public void EncryptedDataAsksForThePasswordLazilyAndChecksIt()
    {
        using var t = new TempDir();
        int asked = 0;
        using (var s = Open(ForeignData.Fixture("7z", "aes.7z"), new ArchiveOpenOptions { Password = () => { asked++; return Password; } }))
        {
            Assert.Equal(0, asked);
            Assert.True(s.IsEncrypted);
            Assert.Equal("yes (AES-256)", s.Details.Single(d => d.Label == "Encrypted").Value);
            // Folders and the empty file hold no encrypted data.
            Assert.Equal(0, s.Extract(t.Sub("dirs"), new ArchiveExtractRequest { Select = i => i.IsDirectory || i.Size == 0 }, null, Ct).LeftOut);
            Assert.Equal(0, asked);
            var reports = new List<ExtractProgress>();
            var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), new SyncProgress<ExtractProgress>(reports.Add), Ct);
            Assert.Equal((3, 0), (r.FilesOk, r.LeftOut));
            AssertFixtureTree(t.Sub("out"));
            // The check stopped at the end of the first file with a stored CRC, before the end of the solid block.
            var check = reports.Where(p => p.Current == "Checking the password").ToList();
            Assert.NotEmpty(check);
            long folder = s.Items.Where(i => !i.IsDirectory).Sum(i => i.Size ?? 0);
            Assert.All(check, p => Assert.True(p.BytesDone < folder));
            Assert.Equal(1, asked);
        }
        asked = 0;
        using (var s = Open(ForeignData.Fixture("7z", "aes.7z"), new ArchiveOpenOptions { Password = () => { asked++; return "Passwörd ü"; } }))
        {
            Assert.Throws<WrongPasswordException>(() => s.Extract(t.Sub("wrong"), new ArchiveExtractRequest(), null, Ct));
            Assert.False(Directory.Exists(t.Sub("wrong")));
            Assert.Throws<WrongPasswordException>(() => s.Test(null, null, Ct));
            Assert.Equal(1, asked);
            Assert.Equal(6, s.Items.Count);
        }
    }

    [Fact]
    public void EncryptedFileNamesAskForThePasswordInOpen()
    {
        int asked = 0;
        using (var s = Open(ForeignData.Fixture("7z", "aes-headers.7z"), new ArchiveOpenOptions { Password = () => { asked++; return Password; } }))
        {
            Assert.Equal(1, asked);
            Assert.Equal("yes (AES-256, file names too)", s.Details.Single(d => d.Label == "Encrypted").Value);
            using var t = new TempDir();
            Assert.Equal(3, s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct).FilesOk);
            Assert.Equal(3, s.Test(null, null, Ct).FilesOk);
            Assert.Equal(1, asked);
            AssertFixtureTree(t.Sub("out"));
        }
        Assert.Throws<WrongPasswordException>(() => Open(ForeignData.Fixture("7z", "aes-headers.7z"), new ArchiveOpenOptions { Password = () => "wrong" }));
        Assert.Throws<PasswordRequiredException>(() => Open(ForeignData.Fixture("7z", "aes-headers.7z"), new ArchiveOpenOptions { Password = () => null }));
        Assert.Throws<PasswordRequiredException>(() => Open(ForeignData.Fixture("7z", "aes-headers.7z")));
    }

    [Fact]
    public void EncryptedDataWithoutAnyChecksumIsRefusedUnlessAnotherFolderProvedThePassword()
    {
        using var t = new TempDir();
        string path = ForeignData.Fixture("7z", "aes-nonsolid.7z");
        SevenZipSource.DropChecksumsForTests = _ => true;
        try
        {
            using var s = Open(path, new ArchiveOpenOptions { Password = () => Password });
            var r = s.Extract(t.Sub("none"), new ArchiveExtractRequest(), null, Ct);
            Assert.Equal(2, r.SkippedUnsupported);
            Assert.Equal(2, r.LeftOut);
            Assert.Contains(r.Errors, e => e.Contains("encrypted without a stored checksum, so Osmium cannot check the password", StringComparison.Ordinal));
            Assert.Equal(["in/empty.txt"], FilesUnder(t.Sub("none")));
        }
        finally { SevenZipSource.DropChecksumsForTests = null; }

        SevenZipSource.DropChecksumsForTests = folder => folder == 1;
        try
        {
            using var s = Open(path, new ArchiveOpenOptions { Password = () => Password });
            var r = s.Extract(t.Sub("one"), new ArchiveExtractRequest(), null, Ct);
            Assert.Equal((3, 1, 0), (r.FilesOk, r.Unverified, r.LeftOut));
            Assert.Contains(r.Warnings, w => w.Contains("no stored checksum; the contents were not checked", StringComparison.Ordinal));
            AssertFixtureTree(t.Sub("one"));
        }
        finally { SevenZipSource.DropChecksumsForTests = null; }
    }

    [Fact]
    public void TheKeyIsDerivedOncePerSaltNotOncePerFolder()
    {
        // 7z writers give every folder its own IV; the key does not depend on it.
        using (var s = (SevenZipSource)Open(ForeignData.Fixture("7z", "aes-nonsolid.7z"), new ArchiveOpenOptions { Password = () => Password }))
        {
            var r = s.Test(null, null, Ct);
            Assert.Equal((3, 0), (r.FilesOk, r.FilesFailed));
            Assert.Equal(1L << 19, s.KeyRoundsSpent);
        }
        using var t = new TempDir();
        byte[] Iv(byte b) => [.. Enumerable.Repeat(b, 16)];
        var a = "first"u8.ToArray();
        var b = "second"u8.ToArray();
        var c = "third"u8.ToArray();
        string path = ForeignData.Write(t, "salts.7z", SevenZipCraft.Build(
        [
            SevenZipCraft.Encrypted("pw", SevenZipCraft.AesProps(10, [1, 2], Iv(1)), a),
            SevenZipCraft.Encrypted("pw", SevenZipCraft.AesProps(10, [1, 2], Iv(2)), b),
            SevenZipCraft.Encrypted("pw", SevenZipCraft.AesProps(10, [3, 4], Iv(3)), c),
        ]));
        using (var s = (SevenZipSource)Open(path, new ArchiveOpenOptions { Password = () => "pw" }))
        {
            var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
            Assert.Equal((3, 0), (r.FilesOk, r.FilesFailed));
            Assert.Equal("third", File.ReadAllText(Path.Combine(t.Sub("out"), "f2.bin")));
            Assert.Equal(2L << 10, s.KeyRoundsSpent);
        }
    }

    [Fact]
    public void ADamagedFirstEncryptedFolderIsDamageNotAWrongPassword()
    {
        using var t = new TempDir();
        byte[] props = SevenZipCraft.AesProps(10, [], [.. Enumerable.Repeat((byte)7, 16)]);
        var bytes = SevenZipCraft.Build([SevenZipCraft.Encrypted("pw", props, "the first file"u8.ToArray()), SevenZipCraft.Encrypted("pw", props, "the second file"u8.ToArray())]);
        bytes[32 + 3] ^= 1; // the first folder's ciphertext: Copy passes the garbage on, and its CRC fails
        string path = ForeignData.Write(t, "damaged-first.7z", bytes);
        int asked = 0;
        using (var s = Open(path, new ArchiveOpenOptions { Password = () => { asked++; return "pw"; } }))
        {
            var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
            Assert.Equal((1, 1), (r.FilesOk, r.FilesFailed));
            Assert.Contains(r.Errors, e => e.StartsWith("f0.bin: wrong password or damaged file", StringComparison.Ordinal));
            Assert.Equal(["f1.bin"], FilesUnder(t.Sub("out")));
            Assert.Equal((1, 1), (s.Test(null, null, Ct).FilesOk, s.Test(null, null, Ct).FilesFailed));
            Assert.Equal(1, asked);
        }
        // With a wrong password every folder fails: nothing is written and the password stays spent.
        using (var s = Open(path, new ArchiveOpenOptions { Password = () => "wrong" }))
        {
            Assert.Throws<WrongPasswordException>(() => s.Extract(t.Sub("wrong"), new ArchiveExtractRequest(), null, Ct));
            Assert.False(Directory.Exists(t.Sub("wrong")));
            Assert.Throws<WrongPasswordException>(() => s.Test(i => i.Path == "f1.bin", null, Ct));
        }
    }

    private static readonly byte[] SomeIv = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];

    [Fact]
    public void AFolderWithACoderOffTheWayToItsResultIsDamaged()
    {
        // Copy, AES and Copy: the AES coder reads its own output, so nothing it would decrypt reaches the result, and
        // a CRC over that result would "prove" any password.
        using var t = new TempDir();
        var plain = "plain bytes with a checksum"u8.ToArray();
        var folder = new SevenZipCraft.Folder(
            [new(SevenZipCraft.CopyId), new(SevenZipCraft.AesId, SevenZipCraft.AesProps(10, [], SomeIv)), new(SevenZipCraft.CopyId)],
            [plain], [plain.Length, plain.Length, plain.Length], [SevenZipCraft.Checked(plain)], Binds: [(1, 1), (2, 0)]);
        string path = ForeignData.Write(t, "loop.7z", SevenZipCraft.Build([folder]));
        var e = Assert.ThrowsAny<CorruptDataException>(() => Open(path, new ArchiveOpenOptions { Password = () => "wrong" }));
        Assert.Contains("a folder whose coders are not all connected to its result", e.Message);
    }

    [Fact]
    public void AnEmptyFileWithAChecksumProvesNoPassword()
    {
        // The empty file's CRC matches under every password, so it cannot vouch for the file after it.
        using var t = new TempDir();
        var secret = "data that only the right password decrypts"u8.ToArray();
        var props = SevenZipCraft.AesProps(10, [], SomeIv);
        var folder = new SevenZipCraft.Folder([new(SevenZipCraft.AesId, props)], [SevenZipCraft.Encrypt("right", props, secret)], [secret.Length],
            [(0, 0u), (secret.Length, null)]);
        string path = ForeignData.Write(t, "empty-first.7z", SevenZipCraft.Build([folder]));
        using var s = Open(path, new ArchiveOpenOptions { Password = () => "wrong" });
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal(0, r.FilesOk);
        Assert.Contains(r.Errors, e => e.Contains("encrypted without a stored checksum, so Osmium cannot check the password", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(t.Sub("out"), "f1.bin")));
    }

    [Fact]
    public void AnAesCoderWhoseOutputNothingReadsProvesNoPassword()
    {
        // BCJ2 reads its call stream only after a branch opcode; plain text has none, so the AES coder feeding that
        // stream decrypts nothing and the text's CRC matches under every password. It must not open the second folder,
        // which has no checksum, to a wrong password.
        using var t = new TempDir();
        var text = "plain text without branch opcodes"u8.ToArray();
        var decoy = new SevenZipCraft.Folder(
            [new([0x03, 0x03, 0x01, 0x1B], null, NumIn: 4), new(SevenZipCraft.AesId, SevenZipCraft.AesProps(10, [], SomeIv))],
            [text, [], new byte[5], []], [text.Length, 0], [SevenZipCraft.Checked(text)], Binds: [(1, 1)], PackedIn: [0, 2, 3, 4]);
        var secret = "data that only the right password decrypts"u8.ToArray();
        var real = SevenZipCraft.Encrypted("right", SevenZipCraft.AesProps(10, [], SomeIv), secret) with { Files = [(secret.Length, null)] };
        string path = ForeignData.Write(t, "decoy.7z", SevenZipCraft.Build([decoy, real]));
        using var s = Open(path, new ArchiveOpenOptions { Password = () => "wrong" });
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal((1, 1, 0), (r.FilesOk, r.SkippedUnsupported, r.Unverified));
        Assert.Equal("plain text without branch opcodes", File.ReadAllText(Path.Combine(t.Sub("out"), "f0.bin")));
        Assert.False(File.Exists(Path.Combine(t.Sub("out"), "f1.bin")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCrcCatchesAWrongPasswordWhereCopyPassesTheBytesOn(bool folderCrc)
    {
        // AES over Copy: a wrong password decrypts to bytes that no decoder refuses. The file's CRC, or the folder's
        // when only the folder has one, is what refuses the password before anything is written.
        using var t = new TempDir();
        var data = "a file that only the right password decrypts"u8.ToArray();
        var props = SevenZipCraft.AesProps(10, [], SomeIv);
        var folder = SevenZipCraft.Encrypted("right", props, data);
        if (folderCrc) folder = folder with { Files = [(data.Length, null)], UnpackCrc = System.IO.Hashing.Crc32.HashToUInt32(data) };
        string path = ForeignData.Write(t, "aes-copy.7z", SevenZipCraft.Build([folder]));
        int asked = 0;
        using (var s = Open(path, new ArchiveOpenOptions { Password = () => { asked++; return "wrong"; } }))
        {
            Assert.Throws<WrongPasswordException>(() => s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct));
            Assert.False(Directory.Exists(t.Sub("out")));
            Assert.Throws<WrongPasswordException>(() => s.Test(null, null, Ct));
            Assert.Equal(1, asked);
        }
        using (var s = Open(path, new ArchiveOpenOptions { Password = () => "right" }))
        {
            var r = s.Extract(t.Sub("right"), new ArchiveExtractRequest(), null, Ct);
            Assert.Equal((1, 0), (r.FilesOk, r.Unverified));
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(t.Sub("right"), "f0.bin")));
        }
    }

    private static readonly byte[] Bcj2Id = [0x03, 0x03, 0x01, 0x1B];

    /// <summary>A BCJ2 folder that passes <paramref name="main"/> through (no branch instructions in it) and reads its
    /// range coder from an AES coder, whose output of <paramref name="aesOut"/> bytes thus never reaches the result.</summary>
    private static SevenZipCraft.Folder RangeCoderDecoy(byte[] main, int aesOut, uint? fileCrc, uint? folderCrc = null) =>
        new([new(Bcj2Id, null, NumIn: 4), new(SevenZipCraft.AesId, SevenZipCraft.AesProps(10, [], SomeIv))],
            [main, [], [], new byte[16]], [main.Length, aesOut], [(main.Length, fileCrc)], Binds: [(3, 1)], PackedIn: [0, 1, 2, 4],
            UnpackCrc: folderCrc);

    [Theory]
    [InlineData(16)]
    [InlineData(5)] // exactly what BCJ2 reads at its start: all of it is read, none of it used
    public void AnAesCoderOnBcj2sRangeCoderProvesNoPassword(int aesOut)
    {
        using var t = new TempDir();
        var text = "plain text without branch opcodes"u8.ToArray();
        var secret = "data that only the right password decrypts"u8.ToArray();
        var real = SevenZipCraft.Encrypted("right", SevenZipCraft.AesProps(10, [], SomeIv), secret) with { Files = [(secret.Length, null)] };
        string path = ForeignData.Write(t, "decoy-rc.7z", SevenZipCraft.Build([RangeCoderDecoy(text, aesOut, System.IO.Hashing.Crc32.HashToUInt32(text)), real]));
        using var s = Open(path, new ArchiveOpenOptions { Password = () => "wrong" });
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal((1, 1, 0), (r.FilesOk, r.SkippedUnsupported, r.Unverified));
        Assert.Contains(r.Errors, e => e.EndsWith(SevenZipSource.Unprovable, StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(t.Sub("out"), "f1.bin")));
    }

    [Fact]
    public void WhenNothingProvesThePasswordAFileWithoutAChecksumIsNotWrittenUnderIt()
    {
        // One encrypted folder: its first file has a CRC and passes, but nothing it holds was decrypted, so the second,
        // which has no CRC, is refused rather than written under a password nothing checked.
        using var t = new TempDir();
        var first = "plain text without branch opcodes"u8.ToArray();
        var second = "a second file with no checksum"u8.ToArray();
        byte[] main = [.. first, .. second];
        var folder = RangeCoderDecoy(main, 16, null) with { Files = [SevenZipCraft.Checked(first), (second.Length, null)] };
        string path = ForeignData.Write(t, "decoy-two.7z", SevenZipCraft.Build([folder]));
        using var s = Open(path, new ArchiveOpenOptions { Password = () => "wrong" });
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal((1, 1, 0), (r.FilesOk, r.SkippedUnsupported, r.Unverified));
        Assert.Contains(r.Errors, e => e.StartsWith("f1.bin: not extracted: " + SevenZipSource.Unprovable, StringComparison.Ordinal));
        Assert.Equal(["f0.bin"], FilesUnder(t.Sub("out")));
    }

    [Fact]
    public void AnEncryptedFileListProvesThePasswordOnlyThroughDecryptedBytes()
    {
        // The file list comes out of a BCJ2 folder whose only AES coder feeds the range coder: it matches its CRC under
        // any password, so it must not vouch for the data folder, which has no checksum.
        using var t = new TempDir();
        var secret = "data that only the right password decrypts"u8.ToArray();
        var real = SevenZipCraft.Encrypted("right", SevenZipCraft.AesProps(10, [], SomeIv), secret) with { Files = [(secret.Length, null)] };
        var bytes = SevenZipCraft.Build([real], header =>
        {
            for (int i = 1; i < header.Length; i++)
                Assert.False((header[i] & 0xFE) == 0xE8 || (header[i - 1] == 0x0F && (header[i] & 0xF0) == 0x80), "a branch instruction in the header");
            return RangeCoderDecoy(header, 16, null, System.IO.Hashing.Crc32.HashToUInt32(header));
        });
        string path = ForeignData.Write(t, "decoy-header.7z", bytes);
        int asked = 0;
        using var s = Open(path, new ArchiveOpenOptions { Password = () => { asked++; return "wrong"; } });
        Assert.Equal(1, asked);
        Assert.Equal("yes (AES-256, file names too)", s.Details.Single(d => d.Label == "Encrypted").Value);
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal((0, 1, 0), (r.FilesOk, r.SkippedUnsupported, r.Unverified));
        Assert.False(File.Exists(Path.Combine(t.Sub("out"), "f0.bin")));
    }

    [Fact]
    public void AFolderCrcAloneCatchesAWrongPassword()
    {
        // Two files, neither with its own CRC: only the folder's CRC can refuse a wrong password.
        using var t = new TempDir();
        var a = "first file of the folder"u8.ToArray();
        var b = "second file of the folder"u8.ToArray();
        byte[] all = [.. a, .. b];
        var props = SevenZipCraft.AesProps(10, [], SomeIv);
        var folder = new SevenZipCraft.Folder([new(SevenZipCraft.AesId, props)], [SevenZipCraft.Encrypt("right", props, all)], [all.Length],
            [(a.Length, null), (b.Length, null)], UnpackCrc: System.IO.Hashing.Crc32.HashToUInt32(all));
        string path = ForeignData.Write(t, "folder-crc.7z", SevenZipCraft.Build([folder]));
        using (var s = Open(path, new ArchiveOpenOptions { Password = () => "wrong" }))
        {
            Assert.Throws<WrongPasswordException>(() => s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct));
            Assert.False(Directory.Exists(t.Sub("out")));
        }
        using (var s = Open(path, new ArchiveOpenOptions { Password = () => "right" }))
        {
            var r = s.Extract(t.Sub("right"), new ArchiveExtractRequest(), null, Ct);
            Assert.Equal(2, r.FilesOk);
            Assert.Equal(b, File.ReadAllBytes(Path.Combine(t.Sub("right"), "f1.bin")));
        }
    }

    /// <summary>Folders of AES over Copy, one file each with its CRC, of the given sizes, all under "right".</summary>
    private static string CopyFolders(TempDir t, params int[] sizes)
    {
        var props = SevenZipCraft.AesProps(10, [], SomeIv);
        var folders = sizes.Select((size, i) => SevenZipCraft.Encrypted("right", props, ForeignData.Text(size, i + 1))).ToList();
        return ForeignData.Write(t, "many.7z", SevenZipCraft.Build(folders));
    }

    [Fact]
    public void AWrongPasswordIsFoundWithoutDecryptingEveryFolder()
    {
        // Twenty folders, the largest first: the search reads the three cheapest, then calls the password wrong.
        using var t = new TempDir();
        int[] sizes = [.. Enumerable.Range(0, 20).Select(i => (20 - i) << 14)];
        string path = CopyFolders(t, sizes);
        using (var s = (SevenZipSource)Open(path, new ArchiveOpenOptions { Password = () => "wrong" }))
        {
            Assert.Throws<WrongPasswordException>(() => s.Test(null, null, Ct));
            Assert.Equal((long)sizes.Order().Take(SevenZipSource.MaxProofFolders).Sum(), s.ProofBytesRead);
        }
        // Within a smaller budget the search stops after the first failure: the next would pass it.
        SevenZipSource.MaxProofBytesOverride = 2 << 14;
        try
        {
            using var s = (SevenZipSource)Open(path, new ArchiveOpenOptions { Password = () => "wrong" });
            Assert.Throws<WrongPasswordException>(() => s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct));
            Assert.False(Directory.Exists(t.Sub("out")));
            Assert.Equal(1L << 14, s.ProofBytesRead);
        }
        finally { SevenZipSource.MaxProofBytesOverride = null; }
        // The right password: the cheapest folder proves it, and everything extracts.
        using (var s = (SevenZipSource)Open(path, new ArchiveOpenOptions { Password = () => "right" }))
        {
            var r = s.Extract(t.Sub("right"), new ArchiveExtractRequest(), null, Ct);
            Assert.Equal((20, 0), (r.FilesOk, r.FilesFailed));
            Assert.Equal(1L << 14, s.ProofBytesRead);
        }
    }

    [Fact]
    public void KeyDerivationForOneArchiveIsBounded()
    {
        // Six folders, each with its own salt: within a budget of four keys, four are read and two are refused before
        // their keys are derived.
        using var t = new TempDir();
        var folders = Enumerable.Range(0, 6).Select(i => SevenZipCraft.Encrypted("pw",
            SevenZipCraft.AesProps(10, [(byte)i], [.. Enumerable.Repeat((byte)i, 16)]), System.Text.Encoding.ASCII.GetBytes($"file {i}"))).ToList();
        string path = ForeignData.Write(t, "salts.7z", SevenZipCraft.Build(folders));
        SevenZipSource.MaxKeyRoundsOverride = 4 << 10;
        try
        {
            using var s = (SevenZipSource)Open(path, new ArchiveOpenOptions { Password = () => "pw" });
            var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
            Assert.Equal((4, 2, 2), (r.FilesOk, r.SkippedUnsupported, r.LeftOut));
            Assert.Equal(2, r.Errors.Count(e => e.EndsWith(SevenZipSource.KeyBudgetRefusal, StringComparison.Ordinal)));
            Assert.Equal(4L << 10, s.KeyRoundsSpent);
            var test = s.Test(null, null, Ct);
            Assert.Equal((4, 2), (test.FilesOk, test.SkippedUnsupported));
            Assert.Equal(4L << 10, s.KeyRoundsSpent);
        }
        finally { SevenZipSource.MaxKeyRoundsOverride = null; }
        Assert.Equal(SevenZipAes.MaxCyclesPower + 3, (int)Math.Log2(SevenZipSource.MaxKeyRounds));
    }

    [Fact]
    public void DamageFailsTheFileAndTheRestOfItsSolidBlockOnly()
    {
        using var t = new TempDir();
        foreach (var fixture in new[] { "lzma.7z", "lzma2-solid.7z" })
        {
            var bytes = File.ReadAllBytes(ForeignData.Fixture("7z", fixture));
            bytes[40] ^= 0x55; // inside the first packed stream
            string path = ForeignData.Write(t, "damaged-" + fixture, bytes);
            using var s = Open(path);
            var r = s.Extract(t.Sub("out-" + fixture), new ArchiveExtractRequest(), null, Ct);
            Assert.True(r.FilesFailed >= 1, fixture);
            Assert.Equal(3, r.FilesOk + r.FilesFailed);
            Assert.Empty(Directory.GetFiles(t.Sub("out-" + fixture), "*.osmium-part-*", SearchOption.AllDirectories));
            if (fixture == "lzma.7z") Assert.Equal(2, r.FilesOk); // separate folders: only one file is damaged
        }
    }

    [Fact]
    public void TheCrcFailsAFileThatACopyFolderPassesOnDamaged()
    {
        // Copy never fails on damage: the file's CRC is the only thing that can.
        using var t = new TempDir();
        var bytes = File.ReadAllBytes(ForeignData.Fixture("7z", "copy.7z"));
        bytes[37] ^= 0x55; // inside the first packed stream: a plain byte of the first file
        string path = ForeignData.Write(t, "damaged-copy.7z", bytes);
        using var s = Open(path);
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal((2, 1, 0), (r.FilesOk, r.FilesFailed, r.Unverified));
        Assert.Contains(r.Errors, e => e.Contains("CRC mismatch", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(t.Sub("out"), "*.osmium-part-*", SearchOption.AllDirectories));
        Assert.Equal(r.FilesOk, FilesUnder(t.Sub("out")).Count); // the damaged file is not kept
        var test = s.Test(null, null, Ct);
        Assert.Equal((2, 1), (test.FilesOk, test.FilesFailed));
    }

    /// <summary>Changes a coder's properties in a fixture whose header is not compressed, fixing up the CRCs.</summary>
    private static byte[] PatchCoder(string fixture, byte[] coder, Action<byte[], int> patch)
    {
        var bytes = File.ReadAllBytes(ForeignData.Fixture("7z", fixture));
        long next = 32 + BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(12));
        int size = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(20));
        int at = bytes.AsSpan((int)next, size).IndexOf(coder);
        Assert.True(at >= 0);
        patch(bytes, (int)next + at + coder.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), Crc32.HashToUInt32(bytes.AsSpan((int)next, size)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), Crc32.HashToUInt32(bytes.AsSpan(12, 20)));
        return bytes;
    }

    [Fact]
    public void PpmdDeclaringHugeMemoryIsRefusedBeforeAllocation()
    {
        using var t = new TempDir();
        var bytes = PatchCoder("ppmd-plain-header.7z", [0x23, 0x03, 0x04, 0x01, 0x05], (b, props) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(props + 1), 0xFFFFFF00));
        using var s = Open(ForeignData.Write(t, "big.7z", bytes), new ArchiveOpenOptions { Limits = ImportLimits.Default with { MemoryBudgetBytes = 256 << 20 } });
        long before = GC.GetAllocatedBytesForCurrentThread();
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32 << 20);
        Assert.Equal(2, r.FilesFailed);
        Assert.Contains(r.Errors, e => e.Contains("needs more memory than allowed", StringComparison.Ordinal));
    }

    [Fact]
    public void LzmaDeclaringAHugeDictionaryForSmallFilesDecodesInLittleMemory()
    {
        using var t = new TempDir();
        var bytes = PatchCoder("lzma-plain-header.7z", [0x23, 0x03, 0x01, 0x01, 0x05], (b, props) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(props + 1), 0xFFFFFFFF));
        using var s = Open(ForeignData.Write(t, "big.7z", bytes), new ArchiveOpenOptions { Limits = ImportLimits.Default with { MemoryBudgetBytes = 64 << 20 } });
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(3, s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct).FilesOk);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32 << 20);
        AssertFixtureTree(t.Sub("out"));
    }

    [Fact]
    public void AHeaderThatDeclaresAHugeDecodedSizeIsRefused()
    {
        using var t = new TempDir();
        // A packed header whose folder says it decodes to 64 GiB.
        var bytes = File.ReadAllBytes(ForeignData.Fixture("7z", "lzma.7z"));
        long next = 32 + BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(12));
        int size = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(20));
        Assert.Equal(0x17, bytes[next]);
        int unpack = bytes.AsSpan((int)next, size).IndexOf((byte)0x0C) + 1; // kCodersUnpackSize, then the size
        var patched = bytes.Take((int)next + unpack).Concat(new byte[] { 0xFE, 0, 0, 0, 0, 0, 0x10 }).Concat(bytes.Skip((int)next + unpack + 2)).ToArray();
        int newSize = size + 5;
        BinaryPrimitives.WriteInt64LittleEndian(patched.AsSpan(20), newSize);
        BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(28), Crc32.HashToUInt32(patched.AsSpan((int)next, newSize)));
        BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(8), Crc32.HashToUInt32(patched.AsSpan(12, 20)));
        var e = Assert.ThrowsAny<Exception>(() => Open(ForeignData.Write(t, "huge-header.7z", patched)));
        Assert.True(e is EntryLimitException or CorruptDataException, e.ToString());
    }

    [Fact]
    public void AnEmptyArchiveOpens()
    {
        using var t = new TempDir();
        var sig = new byte[32];
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 }.CopyTo(sig, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(8), Crc32.HashToUInt32(sig.AsSpan(12, 20)));
        using var s = Open(ForeignData.Write(t, "empty.7z", sig));
        Assert.Empty(s.Items);
        Assert.Equal(0, s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct).LeftOut);
    }
}
