using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using Osmium.Core.Archives;
using Osmium.Core.Archives.Foreign;
using Osmium.Core.Archives.Foreign.Zip;
using Osmium.Core.Container;
using static Osmium.Core.Tests.Foreign.TarAndStreamTests;

namespace Osmium.Core.Tests.Foreign;

/// <summary>ZIP: methods, names, encryption and the password rule, integrity, links, and the bomb shapes.</summary>
public class ZipTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IArchiveSource Open(string path, ArchiveOpenOptions? options = null) => ArchiveSources.Open(path, options, Ct);

    private static ArchiveExtractResult Extract(IArchiveSource s, string dest, ArchiveExtractRequest? request = null) =>
        s.Extract(dest, request ?? new ArchiveExtractRequest(), null, Ct);

    private static List<string> FilesUnder(string root) => !Directory.Exists(root) ? []
        : Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    private static void AssertFixtureTree(string root)
    {
        foreach (var (name, data) in ForeignData.FixtureTree)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine([root, .. name.Split('/')])));
        Assert.True(Directory.Exists(Path.Combine(root, "in", "sub", "empty")));
    }

    [Fact]
    public void RoundTripsAGeneratedZip()
    {
        using var t = new TempDir();
        (string, byte[])[] entries =
        [
            ("top/", []), ("top/empty/", []), ("top/zero.bin", []), ("top/ünïcödé — файл.txt", ForeignData.Text(3000)),
            ("top/sub/big.txt", ForeignData.Text(200_000, 2)), ("top/sub/stored.bin", RandomNumberGenerator.GetBytes(5000)),
        ];
        string path = ForeignData.Write(t, "gen.zip", ForeignData.Zip(entries));
        using var s = Open(path);
        Assert.Equal("ZIP", s.FormatName);
        Assert.False(s.IsEncrypted);
        Assert.Equal(entries.Select(e => e.Item1.TrimEnd('/')), s.Items.Select(i => i.Path));
        Assert.Equal("Deflate", s.Items.Single(i => i.Path == "top/sub/big.txt").Method);
        var r = Extract(s, t.Sub("out"));
        Assert.Equal((4, 0, 0), (r.FilesOk, r.Unverified, r.LeftOut));
        foreach (var (name, data) in entries.Where(e => !e.Item1.EndsWith('/')))
            Assert.Equal(data, File.ReadAllBytes(Path.Combine([t.Sub("out"), .. name.Split('/')])));
        Assert.True(Directory.Exists(Path.Combine(t.Sub("out"), "top", "empty")));
        // System.IO.Compression stores only the DOS date and time, a wall-clock time without a zone: read as local time.
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2024, 5, 6, 7, 8, 10), TimeZoneInfo.Local), File.GetLastWriteTimeUtc(Path.Combine(t.Sub("out"), "top", "sub", "big.txt")));
        var test = s.Test(null, null, Ct);
        Assert.Equal((4, 0, 0), (test.FilesOk, test.Unverified, test.LeftOut));
    }

    [Theory]
    [InlineData("deflate64.zip", "Deflate (64 KiB window)")]
    [InlineData("lzma.zip", "LZMA")]
    [InlineData("ppmd.zip", "PPMd")]
    [InlineData("bzip2.zip", "BZip2")]
    [InlineData("infozip.zip", "Deflate")]
    public void ReadsEveryMethodItSupports(string fixture, string method)
    {
        using var t = new TempDir();
        using var s = Open(ForeignData.Fixture("zip", fixture));
        Assert.Contains(method, s.Items.Where(i => i.Size > 0).Select(i => i.Method));
        Assert.Contains(s.Details, d => d.Label == "Methods" && d.Value.Contains(method, StringComparison.Ordinal));
        var r = Extract(s, t.Sub("out"));
        Assert.Equal((3, 0), (r.FilesOk, r.LeftOut));
        AssertFixtureTree(t.Sub("out"));
        Assert.Equal(3, s.Test(null, null, Ct).FilesOk);
    }

    [Theory]
    [InlineData("aes128.zip", "secret", "AES-128")]
    [InlineData("aes192.zip", "secret", "AES-192")]
    [InlineData("aes256.zip", "secret", "AES-256")]
    [InlineData("zipcrypto.zip", "secret", "ZipCrypto")]
    [InlineData("zipcrypto-nonascii.zip", "Pässwörd ü", "ZipCrypto")]
    public void EncryptedFixturesOpenWithTheRightPassword(string fixture, string password, string kind)
    {
        using var t = new TempDir();
        int asked = 0;
        using var s = Open(ForeignData.Fixture("zip", fixture), new ArchiveOpenOptions { Password = () => { asked++; return password; } });
        Assert.Equal(0, asked); // entry encryption: never asked in Open
        Assert.True(s.IsEncrypted);
        Assert.Contains(s.Details, d => d.Label == "Encrypted" && d.Value == $"yes ({kind})");
        var r = Extract(s, t.Sub("out"));
        Assert.Equal((3, 0, 0), (r.FilesOk, r.Unverified, r.LeftOut));
        AssertFixtureTree(t.Sub("out"));
        Assert.Equal(3, s.Test(null, null, Ct).FilesOk);
        Assert.Equal(1, asked);
    }

    [Theory]
    [InlineData("aes256.zip")]
    [InlineData("zipcrypto.zip")]
    [InlineData("zipcrypto-nonascii.zip")]
    public void AWrongPasswordWritesNothingAndStaysSpent(string fixture)
    {
        using var t = new TempDir();
        int asked = 0;
        using var s = Open(ForeignData.Fixture("zip", fixture), new ArchiveOpenOptions { Password = () => { asked++; return "not it"; } });
        Assert.Throws<WrongPasswordException>(() => Extract(s, t.Sub("out")));
        Assert.False(Directory.Exists(t.Sub("out")));
        Assert.Throws<WrongPasswordException>(() => s.Test(null, null, Ct));
        Assert.Throws<WrongPasswordException>(() => Extract(s, t.Sub("out")));
        Assert.Equal(1, asked);
        // The source stays usable: its listing and the folders, which hold no encrypted data.
        Assert.NotEmpty(s.Items);
        var folders = Extract(s, t.Sub("folders"), new ArchiveExtractRequest { Select = i => i.IsDirectory });
        Assert.Equal(0, folders.LeftOut);
        Assert.True(Directory.Exists(Path.Combine(t.Sub("folders"), "in", "sub", "empty")));
    }

    [Fact]
    public void ARefusedPasswordStaysRefused()
    {
        using var t = new TempDir();
        int asked = 0;
        using var s = Open(ForeignData.Fixture("zip", "aes256.zip"), new ArchiveOpenOptions { Password = () => { asked++; return null; } });
        Assert.Throws<PasswordRequiredException>(() => Extract(s, t.Sub("out")));
        Assert.Throws<PasswordRequiredException>(() => s.Test(null, null, Ct));
        Assert.Equal(1, asked);
        Assert.False(Directory.Exists(t.Sub("out")));
    }

    private static string Mixed(TempDir t, string password)
    {
        var z = new ZipCraft();
        z.Add("plain.txt", ForeignData.Text(5000));
        z.AddAes("secret.txt", ForeignData.Text(7000, 2), password);
        z.AddZipCrypto("old.txt", ForeignData.Text(6000, 3), Encoding.UTF8.GetBytes(password));
        return ForeignData.Write(t, "mixed.zip", z.Build());
    }

    [Fact]
    public void ThePasswordIsAskedLazilyAndOnce()
    {
        using var t = new TempDir();
        string path = Mixed(t, "right");
        int asked = 0;
        using (var s = Open(path, new ArchiveOpenOptions { Password = () => { asked++; return "wrong"; } }))
        {
            Assert.Equal(0, asked);
            Assert.Equal(1, Extract(s, t.Sub("plain"), new ArchiveExtractRequest { Indices = new HashSet<int> { 0 } }).FilesOk);
            Assert.Equal(1, s.Test(i => i.Index == 0, null, Ct).FilesOk);
            Assert.Equal(0, asked);
            Assert.Throws<WrongPasswordException>(() => Extract(s, t.Sub("all")));
            Assert.Equal(1, asked);
            Assert.False(Directory.Exists(t.Sub("all")));
            // Unencrypted entries still extract; encrypted ones fail again without asking.
            Assert.Equal(1, Extract(s, t.Sub("plain2"), new ArchiveExtractRequest { Indices = new HashSet<int> { 0 } }).FilesOk);
            Assert.Throws<WrongPasswordException>(() => Extract(s, t.Sub("all"), new ArchiveExtractRequest { Indices = new HashSet<int> { 1 } }));
            Assert.Equal(1, asked);
        }
        asked = 0;
        using (var s = Open(path, new ArchiveOpenOptions { Password = () => { asked++; return "right"; } }))
        {
            Assert.Equal(3, Extract(s, t.Sub("all")).FilesOk);
            Assert.Equal(3, Extract(s, t.Sub("again")).FilesOk);
            Assert.Equal(1, asked);
        }
        Assert.Equal(ForeignData.Text(7000, 2), File.ReadAllBytes(Path.Combine(t.Sub("all"), "secret.txt")));
    }

    [Fact]
    public void TamperedAesCiphertextIsRefusedAndNoFileIsKept()
    {
        using var t = new TempDir();
        var z = new ZipCraft();
        z.AddAes("first.txt", ForeignData.Text(4000), "pw");
        var bad = z.AddAes("second.txt", ForeignData.Text(4000, 2), "pw");
        bad.Stored[40] ^= 1; // a ciphertext byte: decrypts to wrong data, and the authentication code no longer matches
        using var s = Open(ForeignData.Write(t, "t.zip", z.Build()), new ArchiveOpenOptions { Password = () => "pw" });
        var r = Extract(s, t.Sub("out"));
        Assert.Equal((1, 1), (r.FilesOk, r.FilesFailed));
        Assert.Contains(r.Errors, e => e.StartsWith("second.txt: wrong password or damaged file", StringComparison.Ordinal));
        Assert.Equal(["first.txt"], FilesUnder(t.Sub("out")));
        Assert.Equal(1, s.Test(null, null, Ct).FilesFailed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADamagedFirstEncryptedFileIsDamageNotAWrongPassword(bool aes)
    {
        // The first entry's check value matches and its data fails; the second proves the password, so only the
        // first fails, as damaged, and the password stays usable.
        using var t = new TempDir();
        var z = new ZipCraft();
        var pw = Encoding.UTF8.GetBytes("pw");
        var first = aes ? z.AddAes("first.txt", ForeignData.Text(4000), "pw") : z.AddZipCrypto("first.txt", ForeignData.Text(4000), pw);
        _ = aes ? z.AddAes("second.txt", ForeignData.Text(4000, 2), "pw") : z.AddZipCrypto("second.txt", ForeignData.Text(4000, 2), pw);
        first.Stored[40] ^= 1; // past the salt and verifier (AES) or the 12-byte header (ZipCrypto)
        string path = ForeignData.Write(t, "damaged-first.zip", z.Build());
        int asked = 0;
        using var s = Open(path, new ArchiveOpenOptions { Password = () => { asked++; return "pw"; } });
        var r = Extract(s, t.Sub("out"));
        Assert.Equal((1, 1), (r.FilesOk, r.FilesFailed));
        Assert.Contains(r.Errors, e => e.StartsWith("first.txt: wrong password or damaged file", StringComparison.Ordinal));
        Assert.Equal(["second.txt"], FilesUnder(t.Sub("out")));
        var test = s.Test(null, null, Ct);
        Assert.Equal((1, 1), (test.FilesOk, test.FilesFailed));
        var second = s.Test(i => i.Path == "second.txt", null, Ct);
        Assert.Equal((1, 0), (second.FilesOk, second.FilesFailed));
        Assert.Equal(1, asked);
    }

    [Fact]
    public void WhenEveryEncryptedFileFailsAfterItsCheckValueThePasswordIsWrong()
    {
        // One AES entry: its verifier matches (the right password) but its data is damaged. Nothing else can prove
        // the password, and a wrong one passes the verifier once in 65,536 times, so nothing is written.
        using var t = new TempDir();
        var z = new ZipCraft();
        z.AddAes("only.txt", ForeignData.Text(4000), "pw").Stored[40] ^= 1;
        using var s = Open(ForeignData.Write(t, "only.zip", z.Build()), new ArchiveOpenOptions { Password = () => "pw" });
        Assert.Throws<WrongPasswordException>(() => Extract(s, t.Sub("out")));
        Assert.False(Directory.Exists(t.Sub("out")));
    }

    [Fact]
    public void TheAesMethodWithoutTheEncryptionFlagIsDamaged()
    {
        // Read as it stands it would be plain bytes that no CRC (AE-2 stores none) and no authentication code covers.
        using var t = new TempDir();
        var data = "bytes nothing checks"u8.ToArray();
        var z = new ZipCraft();
        z.Items.Add(new ZipCraft.Item { Name = "x.txt"u8.ToArray(), Method = 99, Flags = 0x0800, Extra = ZipCraft.AesExtra(0), Stored = data, Crc = 0x12345678, Size = data.Length });
        z.Add("fine.txt", "fine"u8.ToArray());
        using var s = Open(ForeignData.Write(t, "flagless.zip", z.Build()));
        var r = Extract(s, t.Sub("out"));
        Assert.Equal((1, 1, 0), (r.FilesOk, r.FilesFailed, r.Unverified));
        Assert.Contains(r.Errors, e => e == "x.txt: damaged data (it names AES encryption but is not marked as encrypted)");
        Assert.Equal(["fine.txt"], FilesUnder(t.Sub("out")));
        var test = s.Test(null, null, Ct);
        Assert.Equal((1, 1), (test.FilesOk, test.FilesFailed));
    }

    [Theory]
    [InlineData(false, 40)] // stored inside AES: the data decrypts to other bytes, and only the code can tell
    [InlineData(true, -1)]  // deflated: the last byte of the stored code, which inflating never reads
    public void TheAuthenticationCodeChecksEveryAesFile(bool deflate, int flip)
    {
        // AE-2 stores no CRC: the code over the encrypted bytes is the only check of the second file, whose password
        // the first one already proved.
        using var t = new TempDir();
        var z = new ZipCraft();
        z.AddAes("first.txt", ForeignData.Text(4000), "pw");
        var data = ForeignData.Text(4000, 2);
        var second = new ZipCraft.Item
        {
            Name = "second.txt"u8.ToArray(), Method = 99, Flags = 0x801, Size = data.Length,
            Stored = ZipCraft.AesEncrypt("pw", deflate ? ZipCraft.Deflate(data) : data, seed: 2), Extra = ZipCraft.AesExtra(deflate ? (ushort)8 : (ushort)0),
        };
        z.Items.Add(second);
        second.Stored[flip >= 0 ? flip : second.Stored.Length - 1] ^= 1;
        using var s = Open(ForeignData.Write(t, "code.zip", z.Build()), new ArchiveOpenOptions { Password = () => "pw" });
        var r = Extract(s, t.Sub("out"));
        Assert.Equal((1, 1, 0), (r.FilesOk, r.FilesFailed, r.Unverified));
        Assert.Contains(r.Errors, e => e.StartsWith("second.txt: wrong password or damaged file", StringComparison.Ordinal));
        Assert.Equal(["first.txt"], FilesUnder(t.Sub("out")));
        Assert.Empty(Directory.GetFiles(t.Sub("out"), "*.osmium-part-*"));
        var test = s.Test(null, null, Ct);
        Assert.Equal((1, 1), (test.FilesOk, test.FilesFailed));
    }

    [Fact]
    public void AnAesPasswordThatPassesTheVerifierIsStillCheckedAgainstTheAuthenticationCode()
    {
        using var t = new TempDir();
        var bytes = File.ReadAllBytes(ForeignData.Fixture("zip", "aes256.zip"));
        // Make the first file's stored verifier the one a wrong password derives.
        using (var slice = OpenSlice(ForeignData.Fixture("zip", "aes256.zip")))
        {
            var dir = ZipDirectory.Read(slice, ImportLimits.Default, 437);
            var first = dir.Records.First(r => r.Encrypted && r.CompressedSize > 0);
            var salt = bytes.AsSpan((int)first.DataStart, 16).ToArray();
            var (_, _, verifier) = WinZipAes.Derive("wrong", salt, 3);
            verifier.CopyTo(bytes, (int)first.DataStart + 16);
            int index = first.Index;
            string path = ForeignData.Write(t, "patched.zip", bytes);
            using var s = Open(path, new ArchiveOpenOptions { Password = () => "wrong" });
            Assert.Throws<WrongPasswordException>(() => Extract(s, t.Sub("out"), new ArchiveExtractRequest { Indices = new HashSet<int> { index } }));
            Assert.False(Directory.Exists(t.Sub("out")));
        }
    }

    private static FileSlice OpenSlice(string path)
    {
        var h = File.OpenHandle(path);
        return new FileSlice(h, 0, RandomAccess.GetLength(h));
    }

    [Fact]
    public void Ae1EntriesCheckBothTheCodeAndTheCrc()
    {
        using var t = new TempDir();
        var z = new ZipCraft();
        z.AddAes("good.txt", ForeignData.Text(3000), "pw", version: 1);
        var bad = z.AddAes("bad-crc.txt", ForeignData.Text(3000, 2), "pw", version: 1);
        bad.Crc ^= 1;
        bad.LocalCrc = bad.Crc;
        using var s = Open(ForeignData.Write(t, "ae1.zip", z.Build()), new ArchiveOpenOptions { Password = () => "pw" });
        var r = Extract(s, t.Sub("out"));
        Assert.Equal((1, 1), (r.FilesOk, r.FilesFailed));
        Assert.Contains(r.Errors, e => e.Contains("CRC mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void AZipCryptoPasswordThatPassesTheCheckByteIsCaughtByTheCrcWithoutBufferingTheEntry()
    {
        using var t = new TempDir();
        var data = new byte[64 << 20];
        var line = Encoding.ASCII.GetBytes("a line of very compressible text for the password check\n");
        for (int i = 0; i < data.Length; i++) data[i] = line[i % line.Length];
        var right = Encoding.UTF8.GetBytes("right password");
        var z = new ZipCraft();
        var item = z.AddZipCrypto("big.txt", data, right);
        string path = ForeignData.Write(t, "big.zip", z.Build());
        // Find a wrong password that passes the one-byte check (about 1 in 256).
        string wrong = Enumerable.Range(0, 100_000).Select(i => "wrong " + i).First(p => ZipCraft.PassesCheckByte(Encoding.UTF8.GetBytes(p), item.Stored, item.Crc));
        data = [];

        var reports = new List<ExtractProgress>();
        using (var s = Open(path, new ArchiveOpenOptions { Password = () => wrong }))
        {
            // Everything the check allocates, on this thread, stays far below the entry's 64 MiB: it is never buffered.
            // (The process-wide counters also see the tests running beside this one, so they are not used.)
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<WrongPasswordException>(() => s.Extract(t.Sub("out"), new ArchiveExtractRequest(), new SyncProgress<ExtractProgress>(reports.Add), Ct));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 16 << 20, "the check buffered the entry");
        }
        Assert.False(Directory.Exists(t.Sub("out")));
        Assert.Contains(reports, p => p.Current == "Checking the password");

    }

    [Fact]
    public void AStoredZipCryptoFileCatchesAWrongPasswordByItsCrc()
    {
        // Stored data decrypts to the right length under any password: once a wrong one passes the one-byte check,
        // only the CRC can refuse it.
        using var t = new TempDir();
        var data = ForeignData.Text(5000);
        uint crc = System.IO.Hashing.Crc32.HashToUInt32(data);
        var right = Encoding.UTF8.GetBytes("right password");
        var z = new ZipCraft();
        var item = new ZipCraft.Item { Name = "stored.txt"u8.ToArray(), Method = 0, Flags = 0x801, Crc = crc, Size = data.Length, Stored = ZipCraft.ZipCryptoEncrypt(right, data, crc) };
        z.Items.Add(item);
        string path = ForeignData.Write(t, "stored-zipcrypto.zip", z.Build());
        string wrong = Enumerable.Range(0, 100_000).Select(i => "wrong " + i).First(p => ZipCraft.PassesCheckByte(Encoding.UTF8.GetBytes(p), item.Stored, crc));
        int asked = 0;
        using (var s = Open(path, new ArchiveOpenOptions { Password = () => { asked++; return wrong; } }))
        {
            Assert.Throws<WrongPasswordException>(() => Extract(s, t.Sub("out")));
            Assert.False(Directory.Exists(t.Sub("out")));
            Assert.Throws<WrongPasswordException>(() => s.Test(null, null, Ct));
            Assert.Equal(1, asked);
        }
        using (var s = Open(path, new ArchiveOpenOptions { Password = () => "right password" }))
            Assert.Equal((1, 0), (Extract(s, t.Sub("right")).FilesOk, Extract(s, t.Sub("right2")).FilesFailed));
    }

    [Fact]
    public void TheZipCryptoCheckIsBoundedByTheLimits()
    {
        using var t = new TempDir();
        var data = new byte[8 << 20];
        var z = new ZipCraft();
        var item = z.AddZipCrypto("zeros.bin", data, "pw"u8.ToArray());
        item.Size = 1000; // a lie only the counters see
        item.LocalSize = 1000;
        var limits = ImportLimits.Default with { ExpansionFloorBytes = 1 << 20, MaxExpansionRatio = 10 };
        using var s = Open(ForeignData.Write(t, "bomb.zip", z.Build()), new ArchiveOpenOptions { Password = () => "pw", Limits = limits });
        // The check never decodes more than one byte past the declared size, so the lie makes it fail; an honest
        // size past the limits is refused before the check decodes anything.
        Assert.Throws<WrongPasswordException>(() => Extract(s, t.Sub("out")));
        var honest = new ZipCraft();
        honest.AddZipCrypto("zeros.bin", data, "pw"u8.ToArray());
        using var h = Open(ForeignData.Write(t, "honest.zip", honest.Build()), new ArchiveOpenOptions { Password = () => "pw", Limits = limits with { ExpansionFloorBytes = 1 << 20 } });
        Assert.Throws<ExpansionLimitException>(() => Extract(h, t.Sub("out")));
        Assert.False(Directory.Exists(t.Sub("out")));
    }

    [Fact]
    public void OverlappingEntriesAreRefused()
    {
        using var t = new TempDir();
        var kernel = new byte[1 << 20];
        var z = new ZipCraft();
        z.Add("kernel.bin", kernel);
        for (int i = 0; i < 50; i++)
        {
            var copy = z.Add($"copy{i}.bin", kernel);
            copy.SameDataAs = 0;
            copy.Name = Encoding.UTF8.GetBytes("kernel.bin"); // the shared local header names it so
        }
        string path = ForeignData.Write(t, "overlap.zip", z.Build());
        using var s = Open(path);
        Assert.Contains(s.Details, d => d.Label == "Overlapping entries" && d.Value.StartsWith("51", StringComparison.Ordinal));
        var r = Extract(s, t.Sub("out"));
        Assert.Equal((0, 51), (r.FilesOk, r.SkippedUnsafe));
        Assert.Contains("overlaps another entry", r.Errors[0]);
        Assert.Empty(FilesUnder(t.Sub("out")));
        Assert.Equal(51, s.Test(null, null, Ct).SkippedUnsafe);
    }

    [Fact]
    public void LocalAndCentralSizesThatDisagreeAreDamage()
    {
        using var t = new TempDir();
        var z = new ZipCraft();
        var small = z.Add("declares-small.bin", new byte[1 << 20]);
        small.LocalSize = 1024; // local header: 1 KB; central directory: 1 MB
        var big = z.Add("inflates-more.bin", new byte[1 << 20]);
        big.Size = 1024; // both headers say 1 KB, the data inflates to 1 MB
        big.LocalSize = 1024;
        z.Add("fine.txt", "fine"u8.ToArray());
        using var s = Open(ForeignData.Write(t, "lies.zip", z.Build()));
        var r = Extract(s, t.Sub("out"));
        Assert.Equal((1, 2), (r.FilesOk, r.FilesFailed));
        Assert.Contains(r.Errors, e => e.Contains("disagrees with the central directory", StringComparison.Ordinal));
        Assert.Contains(r.Errors, e => e.Contains("more data than the archive says", StringComparison.Ordinal));
        Assert.Equal(["fine.txt"], FilesUnder(t.Sub("out")));
    }

    [Fact]
    public void TheEntryCountIsCheckedBeforeEntriesAreRead()
    {
        using var t = new TempDir();
        var z = new ZipCraft();
        z.Add("a.txt", "a"u8.ToArray());
        z.DeclaredCount = 1_000_001; // in the ZIP64 end record; the central directory holds one entry
        var e = Assert.Throws<EntryLimitException>(() => Open(ForeignData.Write(t, "count.zip", z.Build())));
        Assert.Contains("1,000,001 entries", e.Message);
        var limits = ImportLimits.Default with { MaxEntries = 1000 };
        var real = new ZipCraft();
        for (int i = 0; i < 1001; i++) real.Add($"f{i}", [], deflate: false);
        Assert.Throws<EntryLimitException>(() => Open(ForeignData.Write(t, "many.zip", real.Build()), new ArchiveOpenOptions { Limits = limits }));
        Assert.Equal(1001, Open(ForeignData.Write(t, "many2.zip", real.Build())).Items.Count);
    }

    [Fact]
    public void TheDeclaredOutputIsCheckedBeforeWriting()
    {
        using var t = new TempDir();
        string path = ForeignData.Write(t, "zeros.zip", ForeignData.Zip([("zeros.bin", new byte[8 << 20])]));
        var limits = ImportLimits.Default with { ExpansionFloorBytes = 1 << 20, MaxExpansionRatio = 10 };
        using var s = Open(path, new ArchiveOpenOptions { Limits = limits });
        var e = Assert.Throws<ExpansionLimitException>(() => Extract(s, t.Sub("out")));
        Assert.Equal(8 << 20, e.OutputBytes);
        Assert.False(Directory.Exists(t.Sub("out")));
        Assert.Equal(1, Extract(s, t.Sub("out"), new ArchiveExtractRequest { Limits = limits with { AllowLargeExpansion = true } }).FilesOk);
    }

    [Fact]
    public void HugeDeclaredDecoderMemoryIsRefusedOrNeverAllocated()
    {
        using var t = new TempDir();
        var budget = ImportLimits.Default with { MemoryBudgetBytes = 64 << 20 };
        // PPMd declaring 256 MB: refused per entry before the model is allocated.
        var ppmd = File.ReadAllBytes(ForeignData.Fixture("zip", "ppmd.zip"));
        using (var slice = OpenSlice(ForeignData.Fixture("zip", "ppmd.zip")))
            foreach (var r in ZipDirectory.Read(slice, ImportLimits.Default, 437).Records.Where(r => r.CompressedSize > 0))
                ppmd[r.DataStart + 1] |= 0x0F; // memory field: (props >> 4) & 0xFF = 0xFF → 256 MB
        using (var s = Open(ForeignData.Write(t, "ppmd-big.zip", ppmd), new ArchiveOpenOptions { Limits = budget }))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var r = Extract(s, t.Sub("ppmd"));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32 << 20);
            Assert.Equal(2, r.SkippedUnsupported);
            Assert.Contains(r.Errors, e => e.Contains("needs more memory than allowed", StringComparison.Ordinal));
        }
        // LZMA declaring a 4 GiB dictionary for a small file: the file's size is enough, so it decodes in little memory.
        var lzma = File.ReadAllBytes(ForeignData.Fixture("zip", "lzma.zip"));
        using (var slice = OpenSlice(ForeignData.Fixture("zip", "lzma.zip")))
            foreach (var r in ZipDirectory.Read(slice, ImportLimits.Default, 437).Records.Where(r => r.CompressedSize > 0))
                BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(lzma, r.DataStart + 5);
        using (var s = Open(ForeignData.Write(t, "lzma-big.zip", lzma), new ArchiveOpenOptions { Limits = budget }))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(3, Extract(s, t.Sub("lzma")).FilesOk);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32 << 20);
        }
    }

    /// <summary>A Zstandard frame of ten 'A's in one RLE block, declaring a window of 2^<paramref name="windowLog"/>.</summary>
    private static byte[] ZstdFrame(int windowLog) =>
        [0x28, 0xB5, 0x2F, 0xFD, 0x00, (byte)((windowLog - 10) << 3), 0x53, 0x00, 0x00, (byte)'A'];

    [Fact]
    public void ZstandardEntriesHaveTheirWindowCheckedBeforeDecoding()
    {
        using var t = new TempDir();
        var ten = Encoding.ASCII.GetBytes("AAAAAAAAAA");
        var z = new ZipCraft();
        foreach (var (name, frame) in new[] { ("huge-window.txt", ZstdFrame(30)), ("small-window.txt", ZstdFrame(20)), ("made.txt", ForeignData.Zstd(ten)) })
        {
            var item = z.Add(name, ten, deflate: false);
            item.Method = 93;
            item.Stored = frame;
        }
        using var s = Open(ForeignData.Write(t, "zstd.zip", z.Build()), new ArchiveOpenOptions { Limits = ImportLimits.Default with { MemoryBudgetBytes = 600 << 20 } });
        Assert.All(s.Items, i => Assert.Equal("Zstandard", i.Method));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var r = Extract(s, t.Sub("out"));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 << 20, "a declared 1 GiB window was allocated");
        Assert.Equal((2, 1), (r.FilesOk, r.SkippedUnsupported));
        Assert.Contains(r.Errors, e => e.StartsWith("huge-window.txt", StringComparison.Ordinal) && e.Contains("needs more memory than allowed", StringComparison.Ordinal));
        Assert.Equal(ten, File.ReadAllBytes(Path.Combine(t.Sub("out"), "small-window.txt")));
        Assert.Equal(ten, File.ReadAllBytes(Path.Combine(t.Sub("out"), "made.txt")));
    }

    [Fact]
    public void XzEntriesAreReadFromTheirIndex()
    {
        using var t = new TempDir();
        var hello = ForeignData.FixtureTree.Single(f => f.Path == "in/hello.txt").Data;
        var z = new ZipCraft();
        var item = z.Add("hello.txt", hello, deflate: false);
        item.Method = 95;
        item.Stored = File.ReadAllBytes(ForeignData.Fixture("xz", "crc64.xz"));
        using var s = Open(ForeignData.Write(t, "xz.zip", z.Build()));
        Assert.Equal("xz", s.Items.Single().Method);
        Assert.Equal((1, 0), (s.Test(null, null, Ct).FilesOk, s.Test(null, null, Ct).LeftOut));
        Assert.Equal(1, Extract(s, t.Sub("out")).FilesOk);
        Assert.Equal(hello, File.ReadAllBytes(Path.Combine(t.Sub("out"), "hello.txt")));
    }

    [Fact]
    public void LinksDevicesAndUnknownMethodsAreRefused()
    {
        using var t = new TempDir();
        var z = new ZipCraft();
        var link = z.Add("link", "/etc"u8.ToArray(), deflate: false);
        link.MadeBy = 3 << 8; link.External = 0xA1FFu << 16;
        var fifo = z.Add("fifo", [], deflate: false);
        fifo.MadeBy = 3 << 8; fifo.External = 0x11A4u << 16;
        var imploded = z.Add("old.txt", "x"u8.ToArray(), deflate: false);
        imploded.Method = 6;
        var strong = z.Add("strong.txt", "x"u8.ToArray(), deflate: false, flags: 0x41);
        var reparse = z.Add("junction", [], deflate: false);
        reparse.External = 0x410;
        z.Add("ok.txt", "ok"u8.ToArray());
        using var s = Open(ForeignData.Write(t, "odd.zip", z.Build()));
        Assert.True(s.Items[0].IsLink);
        var r = Extract(s, t.Sub("out"));
        Assert.Equal(1, r.FilesOk);
        Assert.Equal(2, r.SkippedLinks);
        Assert.Equal(3, r.SkippedUnsupported);
        Assert.Contains(r.Errors, e => e.Contains("compression method 6 (Implode), which Osmium cannot read", StringComparison.Ordinal));
        Assert.Contains(r.Errors, e => e.Contains("a strong-encryption method Osmium cannot read", StringComparison.Ordinal));
        Assert.Equal(["ok.txt"], FilesUnder(t.Sub("out")));
    }

    [Fact]
    public void CentralDirectoryEncryptionAndMultipleDisksAreNotSupported()
    {
        using var t = new TempDir();
        var z = new ZipCraft();
        z.Add("a.txt", "a"u8.ToArray(), flags: 0x2000);
        Assert.Contains("strong-encryption method", Assert.Throws<NotSupportedException>(() => Open(ForeignData.Write(t, "cd.zip", z.Build()))).Message);
        var d = new ZipCraft { Disk = 1 };
        d.Add("a.txt", "a"u8.ToArray());
        Assert.Equal("Multi-volume ZIP archives are not supported.", Assert.Throws<NotSupportedException>(() => Open(ForeignData.Write(t, "disk.zip", d.Build()))).Message);
    }

    [Fact]
    public void NamesWithoutTheUtf8FlagUseTheOemCodePage()
    {
        using var t = new TempDir();
        ArchiveSources.EnsureCodePages();
        var z = new ZipCraft();
        z.Add("über.txt", "1"u8.ToArray(), encoding: Encoding.GetEncoding(437));
        z.Add("dir\\file.txt", "2"u8.ToArray(), encoding: Encoding.ASCII);
        var unicode = z.Add("ignored.txt", "3"u8.ToArray(), encoding: Encoding.ASCII);
        var name = Encoding.UTF8.GetBytes("ünïcödé.txt");
        unicode.Extra = [0x75, 0x70, (byte)(5 + name.Length), 0, 1, .. BitConverter.GetBytes(Crc32.HashToUInt32(unicode.Name)), .. name];
        var stale = z.Add("stale.txt", "4"u8.ToArray(), encoding: Encoding.ASCII);
        stale.Extra = [0x75, 0x70, (byte)(5 + name.Length), 0, 1, 0, 0, 0, 0, .. name];
        string path = ForeignData.Write(t, "oem.zip", z.Build());
        ZipSource.OemCodePageOverride = 437;
        try
        {
            using var s = Open(path);
            Assert.Equal(["über.txt", "dir/file.txt", "ünïcödé.txt", "stale.txt"], s.Items.Select(i => i.Path));
            Assert.Equal(4, Extract(s, t.Sub("oem")).FilesOk);
            Assert.Equal(["dir/file.txt", "stale.txt", "über.txt", "ünïcödé.txt"], FilesUnder(t.Sub("oem")));
        }
        finally { ZipSource.OemCodePageOverride = null; }
        var sjis = new ZipCraft();
        sjis.Add("日本語.txt", "5"u8.ToArray(), encoding: Encoding.GetEncoding(932));
        string jp = ForeignData.Write(t, "sjis.zip", sjis.Build());
        ZipSource.OemCodePageOverride = 932;
        try
        {
            using var s = Open(jp);
            Assert.Equal("日本語.txt", s.Items.Single().Path);
            Assert.Equal(1, Extract(s, t.Sub("out")).FilesOk);
            Assert.True(File.Exists(Path.Combine(t.Sub("out"), "日本語.txt")));
        }
        finally { ZipSource.OemCodePageOverride = null; }
    }

    [Fact]
    public void NamesFromUnixWithoutTheFlagAreUtf8WhenTheyAreValidUtf8()
    {
        using var t = new TempDir();
        var z = new ZipCraft();
        var unix = z.Add("ünïcödé.txt", "1"u8.ToArray(), encoding: Encoding.UTF8);
        unix.Flags = 0; // Info-ZIP on Linux: UTF-8 bytes, no flag, no Unicode path field
        unix.MadeBy = 3 << 8 | 30;
        var dos = z.Add("ünïcödé.txt", "2"u8.ToArray(), encoding: Encoding.UTF8);
        dos.Flags = 0; // the same bytes from a DOS or Windows tool are read in the OEM code page
        ZipSource.OemCodePageOverride = 437;
        try
        {
            using var s = Open(ForeignData.Write(t, "unix.zip", z.Build()));
            Assert.Equal("ünïcödé.txt", s.Items[0].Path);
            Assert.NotEqual("ünïcödé.txt", s.Items[1].Path);
        }
        finally { ZipSource.OemCodePageOverride = null; }
    }

    [Theory]
    [InlineData(-4.0, 16, 0)]
    [InlineData(5.5, 6, 30)]
    public void DosTimesAreTheComputersLocalTimeInAnyZone(double offsetHours, int utcHour, int utcMinute)
    {
        // The test runners keep UTC, where local time and UTC are the same; a fixed zone tells them apart everywhere.
        using var t = new TempDir();
        var z = new ZipCraft();
        z.Add("dos.txt", "1"u8.ToArray()); // DOS fields only: 2024-05-06 12:00:00 on the writer's clock
        ZipDirectory.TimeZoneOverride = TimeZoneInfo.CreateCustomTimeZone("fixed", TimeSpan.FromHours(offsetHours), "fixed", "fixed");
        try
        {
            using var s = Open(ForeignData.Write(t, "dos.zip", z.Build()));
            Assert.Equal(new DateTime(2024, 5, 6, utcHour, utcMinute, 0, DateTimeKind.Utc), s.Items[0].ModifiedUtc);
        }
        finally { ZipDirectory.TimeZoneOverride = null; }
    }

    [Fact]
    public void TimesAreUtcFromTheExtraFieldsAndLocalFromTheDosFields()
    {
        using var t = new TempDir();
        var z = new ZipCraft();
        z.Add("dos.txt", "1"u8.ToArray()); // DOS fields only: 2024-05-06 12:00:00 on the writer's clock
        var ut = z.Add("ut.txt", "2"u8.ToArray());
        ut.Extra = [0x55, 0x54, 5, 0, 1, .. BitConverter.GetBytes(1_000_000_000)]; // extended timestamp: UTC seconds
        var ntfs = z.Add("ntfs.txt", "3"u8.ToArray());
        long ft = new DateTime(2010, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc();
        ntfs.Extra = [0x0A, 0x00, 32, 0, 0, 0, 0, 0, 1, 0, 24, 0, .. BitConverter.GetBytes(ft), .. BitConverter.GetBytes(ft), .. BitConverter.GetBytes(ft)];
        using var s = Open(ForeignData.Write(t, "times.zip", z.Build()));
        var local = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2024, 5, 6, 12, 0, 0), TimeZoneInfo.Local);
        Assert.Equal(local, s.Items[0].ModifiedUtc);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1_000_000_000), s.Items[1].ModifiedUtc);
        Assert.Equal(new DateTime(2010, 1, 2, 3, 4, 5, DateTimeKind.Utc), s.Items[2].ModifiedUtc);
        Assert.All(s.Items, i => Assert.Equal(DateTimeKind.Utc, i.ModifiedUtc!.Value.Kind));
        Assert.Equal(3, Extract(s, t.Sub("out")).FilesOk);
        Assert.Equal(local, File.GetLastWriteTimeUtc(Path.Combine(t.Sub("out"), "dos.txt")));
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1_000_000_000), File.GetLastWriteTimeUtc(Path.Combine(t.Sub("out"), "ut.txt")));
        Assert.Equal(new DateTime(2010, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(t.Sub("out"), "ntfs.txt")));
    }

    [Fact]
    public void AFlippedCrcFailsTheFile()
    {
        using var t = new TempDir();
        var z = new ZipCraft();
        var bad = z.Add("bad.txt", ForeignData.Text(1000));
        bad.Crc ^= 0x10;
        bad.LocalCrc = bad.Crc;
        z.Add("good.txt", ForeignData.Text(1000, 2));
        using var s = Open(ForeignData.Write(t, "crc.zip", z.Build()));
        var r = Extract(s, t.Sub("out"));
        Assert.Equal((1, 1), (r.FilesOk, r.FilesFailed));
        Assert.Contains(r.Errors, e => e.StartsWith("bad.txt: damaged data (the file: CRC mismatch)", StringComparison.Ordinal));
        Assert.Equal(["good.txt"], FilesUnder(t.Sub("out")));
        Assert.Empty(Directory.GetFiles(t.Sub("out"), "*.osmium-part-*"));
    }

    [Fact]
    public void TheCommentIsShownEscaped()
    {
        using var t = new TempDir();
        var z = new ZipCraft { Comment = Encoding.ASCII.GetBytes("made \u001b[31mred\u001b[0m " + new string('x', 600)) };
        z.Add("a.txt", "a"u8.ToArray());
        using var s = Open(ForeignData.Write(t, "c.zip", z.Build()));
        var comment = s.Details.Single(d => d.Label == "Comment").Value;
        Assert.StartsWith("made \\u001b[31mred", comment);
        Assert.DoesNotContain('\u001b', comment);
        Assert.True(comment.Length < 560);
    }

    [Fact]
    public void Zip64EndRecordsAreRead()
    {
        using var t = new TempDir();
        // More than 65,535 entries make System.IO.Compression write the ZIP64 end records.
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            for (int i = 0; i < 70_000; i++) zip.CreateEntry($"d/{i}", CompressionLevel.NoCompression);
        using var s = Open(ForeignData.Write(t, "z64.zip", ms.ToArray()));
        Assert.Equal(70_000, s.Items.Count);
        Assert.Equal(70_000, s.Test(null, null, Ct).FilesOk);
    }
}
