using System.Buffers.Binary;
using System.IO.Hashing;
using Osmium.Core.Container;
using Osmium.Core.Crypto;
using Osmium.Core.Pipeline;

namespace Osmium.Cli.Tests;

/// <summary>
/// Password protection on the command line: the presets of new archives, the questions an archive's key derivation
/// raises (answered with --mem, --allow-weak-protection and --allow-slow-kdf, before any password is read), archives
/// that are not protected as Osmium protects them, and what the commands say about encryption.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class CliProtectionTests : IDisposable
{
    private const string Pw = "a passphrase of five long words";
    private static readonly KdfParameters Compatible = KdfParameters.Pbkdf2(100_000);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osmium-cli-" + Guid.NewGuid().ToString("N")[..8]);

    public CliProtectionTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "in"));
        File.WriteAllText(Path.Combine(_dir, "in", "a.txt"), "the contents");
    }

    public void Dispose()
    {
        Cli.StandardInputOverride = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(params string[] parts) => Path.Combine([_dir, .. parts]);

    /// <summary>An archive of two files in two stored blocks, made through the archive writer.</summary>
    private string Write(string name, KdfParameters kdf, SpareCopyPolicy spares = SpareCopyPolicy.Never, string creator = "Osmium test")
    {
        string path = P(name);
        using var w = new OsaWriter(path, new OsaWriterOptions { Password = Pw, Kdf = kdf, SpareCopies = spares, Creator = creator });
        var entries = new List<ArchiveEntry>();
        foreach (var (file, text) in new[] { ("a.txt", "the first file, long enough to be a block"), ("b.txt", "the second file, in a block of its own") })
        {
            var data = System.Text.Encoding.UTF8.GetBytes(text);
            var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", MethodRegistry.Default), data, CodecContext.None);
            int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, data.Length);
            entries.Add(new ArchiveEntry { Path = file, Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(frame, 0, data.Length)], Hash = XxHash128.HashToUInt128(data) });
        }
        w.Finish(entries);
        w.Commit(overwrite: true);
        return path;
    }

    /// <summary>Runs osmium with <paramref name="input"/> as the bytes of standard input (UTF-8).</summary>
    private static Task<CliResult> Run(string input, params string[] args)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        Cli.StandardInputOverride = () => new MemoryStream(bytes);
        return CliRunner.RunAsync(args, input);
    }

    private static Task<CliResult> WithPassword(params string[] args) => Run(Pw + "\n", [.. args, "--password-stdin"]);

    private static ArchiveHeader HeaderOf(string path)
    {
        using var fs = File.OpenRead(path);
        return ArchiveHeader.Read(fs);
    }

    private bool NothingIn(string dest) => !Directory.Exists(dest) || !Directory.EnumerateFileSystemEntries(dest).Any();

    // ------------------------------------------------------------------ new archives

    [Theory]
    [InlineData(new string[0], 2, 1 << 20)]
    [InlineData(new[] { "--protection", "standard" }, 2, 1 << 20)]
    [InlineData(new[] { "--protection", "strong" }, 3, 2 << 20)]
    [InlineData(new[] { "--protection", "maximum" }, 3, 4 << 20)]
    public void EachArgon2idPresetIsItsSettings(string[] options, int passes, int memoryKiB)
    {
        Assert.Equal(KdfParameters.Argon2id(passes, memoryKiB, 4), Cli.ProtectionFrom(new Args(options)));
    }

    [Fact]
    public void CompatibleIsPbkdf2WithTheIterationsGiven()
    {
        Assert.Equal(KdfParameters.Pbkdf2(600_000), Cli.ProtectionFrom(new Args(["--protection", "compatible"])));
        Assert.Equal(KdfParameters.Pbkdf2(250_000), Cli.ProtectionFrom(new Args(["--protection", "compatible", "--kdf-iterations", "250000"])));
        var e = Assert.Throws<ArgumentException>(() => Cli.ProtectionFrom(new Args(["--kdf-iterations", "250000"])));
        Assert.Equal(Cli.KdfIterationsNeedCompatible, e.Message);
        Assert.Throws<ArgumentException>(() => Cli.ProtectionFrom(new Args(["--protection", "extreme"])));
    }

    [Fact]
    public async Task TheDefaultProtectionIsArgon2idStandardAndHideSizesSetsItsFlag()
    {
        // Three derivations with 1 GiB each.
        if (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes < 4L << 30) Assert.Skip("needs 4 GiB of memory for Standard protection");
        var made = await WithPassword("a", P("std.osa"), P("in"), "--profile", "store", "-q");
        Assert.True(made.ExitCode == 0, made.Error);
        var h = HeaderOf(P("std.osa"));
        Assert.Equal((Osa.FlagEncrypted, KdfPresets.Standard), (h.Flags, h.Encryption!.Kdf));
        var tested = await WithPassword("t", P("std.osa"));
        Assert.True(tested.ExitCode == 0, tested.Error);
        var hidden = await WithPassword("a", P("hidden.osa"), P("in"), "--profile", "store", "-q", "--hide-sizes");
        Assert.True(hidden.ExitCode == 0, hidden.Error);
        Assert.Equal(Osa.FlagEncrypted | Osa.FlagPaddedBlocks, HeaderOf(P("hidden.osa")).Flags);
    }

    [Fact]
    public async Task CompatibleProtectionWritesPbkdf2WithItsIterations()
    {
        var r = await WithPassword("a", P("c.osa"), P("in"), "--profile", "store", "-q", "--protection", "compatible", "--kdf-iterations", "100000");
        Assert.True(r.ExitCode == 0, r.Error);
        Assert.Equal(Compatible, HeaderOf(P("c.osa")).Encryption!.Kdf);
        Assert.Contains("note: compatible protection (PBKDF2) lets Osmium 0.1 open this archive", r.Error);
    }

    [Theory]
    [InlineData(new[] { "--kdf-iterations", "200000" }, Cli.KdfIterationsNeedCompatible)]
    [InlineData(new[] { "--protection", "compatible", "--hide-sizes" }, "--hide-sizes needs Argon2id protection")]
    [InlineData(new[] { "--protection", "compatible", "--kdf-iterations", "1000" }, "weaker than any Osmium preset")]
    public async Task ProtectionThatCannotBeWrittenIsRefusedBeforeAnything(string[] options, string message)
    {
        var r = await WithPassword(["a", P("out.osa"), P("in"), "--profile", "store", "-q", .. options]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(message, r.Error);
        Assert.DoesNotContain("--allow-weak-protection", r.Error); // a question for opening, not for making
        Assert.False(File.Exists(P("out.osa")));
    }

    [Theory]
    [InlineData("--protection", "strong")]
    [InlineData("--hide-sizes", null)]
    public async Task ProtectionWithoutAPasswordIsAnError(string option, string? value)
    {
        var r = await Run("", ["a", P("out.osa"), P("in"), "--profile", "store", "-q", option, .. value is null ? Array.Empty<string>() : [value]]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("applies to encrypted archives", r.Error);
        Assert.False(File.Exists(P("out.osa")));
    }

    [Fact]
    public async Task TheWarningsGoToTheErrorStreamAndNeverHoldThePassword()
    {
        const string shortPassword = "tiny pw";
        var r = await Run(shortPassword + "\n", "a", P("w.osa"), P("in"), "--profile", "store", "-q", "--password-stdin", "--protection", "compatible");
        Assert.True(r.ExitCode == 0, r.Error);
        Assert.Contains("warning: this password is short or easy to guess.", r.Error);
        Assert.DoesNotContain("warning", r.Output);
        Assert.DoesNotContain(shortPassword, r.Output + r.Error);
        var long_ = await WithPassword("a", P("w2.osa"), P("in"), "--profile", "store", "-q", "--protection", "compatible");
        Assert.DoesNotContain("short or easy to guess", long_.Error);
    }

    // ------------------------------------------------------------------ the questions, before the password

    [Fact]
    public async Task MemTooLowIsRefusedBeforeThePasswordIsRead()
    {
        string path = Write("floor.osa", KdfPresets.Floor);
        var r = await Run("", "t", path, "--mem", "64M"); // no password: one asked for would be a different error
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("error: Checking the password of this archive needs 256 MiB of memory, more than the 64 MiB Osmium may use. "
            + "Run again with --mem 256M to allow it.", r.Error);
        var ok = await WithPassword("t", path, "--mem", "256M");
        Assert.True(ok.ExitCode == 0, ok.Error);
    }

    /// <summary>The Floor archive's record made to ask for t = 13 and m = 1 GiB: 13 GiB-passes, more than any preset.
    /// Its file list no longer authenticates, but every check of the settings comes before the password.</summary>
    private string SlowArchive()
    {
        string path = Write("slow.osa", KdfPresets.Floor);
        var b = File.ReadAllBytes(path);
        Assert.Equal((byte)KdfAlgorithm.Argon2id, b[38]);
        b[39] = 13;
        b[40] = 0x80; b[41] = 0x80; b[42] = 0x40; // 1,048,576 KiB, the same three bytes as 262,144 KiB take
        int headerLength = 40 + (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(headerLength - 4), Osa.Crc(b.AsSpan(0, headerLength - 4)));
        File.WriteAllBytes(path, b);
        Assert.Equal(KdfParameters.Argon2id(13, 1 << 20, 4), HeaderOf(path).Encryption!.Kdf);
        return path;
    }

    [Fact]
    public async Task ASlowKeyDerivationNeedsAllowSlowKdfAndIsRefusedBeforeAnyPrompt()
    {
        string path = SlowArchive();
        var r = await Run("", "t", path, "--mem", "2G");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("takes more work than any Osmium preset asks for (Argon2id, 1 GiB, 13 passes)", r.Error);
        Assert.Contains("Run again with --allow-slow-kdf to allow it.", r.Error);
        // With the answer, the checks pass and only then is a password needed (none was given).
        var allowed = await Run("", "t", path, "--mem", "2G", "--allow-slow-kdf");
        Assert.Equal(1, allowed.ExitCode);
        Assert.Contains("Use --password or --password-stdin.", allowed.Error);
        Assert.DoesNotContain("more work", allowed.Error);
    }

    /// <summary>An archive Osmium 0.1.1's command line made with --kdf-iterations 1000 (password "weak archive of
    /// 0.1.1"), holding note.txt.</summary>
    private const string ArchiveOf011With1000Iterations =
        "T1NNQRoNCgABAAAAAQAAAPcAdHemU0cdGFuUOvaN0JJEAAAAATQB6AcLTVnOZ1nOtSBa5qW6w8qgR2hVCc+dey1LBWreT5rWVgEEjAsjIgb9kKV/"
        + "e9Om6X7RAgxPc21pdW0gMC4xLjHQr7gpT0JMSwEAAAAAAAAAAAAAAF4AAAAAAAAAlHs+LvbVWrsbLrKNc/Ty/QpSUwrMdR8/R1gY0Y+sRVsBCyAm"
        + "blWn0BmgFHnCUpujwYUDWwI67Qza5OLm9FvITSgUWCvzvqewucaA/gQ+PuTvueTt8F3EUnz6LkfXBTx06iZPQkxLAwAAAP//////////IwEAAAAA"
        + "AADXD7zi4mPuAFzjJreJDR4FLBZFLGNwRPBH0QG1EHpMsU+FmRpoQ0g97bZBN+EuKaQUViHup29242jTRFbjnvgurfyhefYJprRaIOOPc7AAdlnH"
        + "vijmSq0iBuVhYzXPppzd5bbz4w2r6n+gy0/TL6/goxSKvXapvvmL5QkzdE1dksLJUSRurUZDgRdHSViXBtKEWthFPMkav2wZzUIEBIK5rmFGF7XD"
        + "5gl1WNzx5H8BMP0hQM1ALPZLYmaM3eUU6wtMgv9Bi4SiVjXc960FOu1JZjWQwgwWR2T2uMdy7LL+atblU8JUaNJ+29GXl48f6V/Lo/oU+87fw9AF"
        + "ox4GwDhgYFB88l6s8PujIL5O9Osu64zn5b4nWfyElnN9AqPk4d6J8EOy4bi+5gAAAAAAAAA/AQAAAAAAAPcAdHemU0cdlJ1ttE9TQUU=";

    [Fact]
    public async Task AllowWeakProtectionOpensAnArchiveOfOsmium01WithFewIterations()
    {
        string path = P("weak.osa");
        File.WriteAllBytes(path, Convert.FromBase64String(ArchiveOf011With1000Iterations));
        const string input = "weak archive of 0.1.1\n";
        var refused = await Run(input, "x", path, P("out"), "--password-stdin");
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("PBKDF2, 1,000 iterations", refused.Error);
        Assert.Contains("Run again with --allow-weak-protection to open it anyway.", refused.Error);
        Assert.True(NothingIn(P("out")));
        var opened = await Run(input, "x", path, P("out"), "--password-stdin", "--allow-weak-protection");
        Assert.True(opened.ExitCode == 0, opened.Error);
        Assert.Equal("made by Osmium 0.1.1 with 1,000 iterations\n", File.ReadAllText(P("out", "in", "note.txt")));
        // An archive at the minimum needs no answer.
        var normal = await WithPassword("t", Write("ok.osa", Compatible));
        Assert.True(normal.ExitCode == 0, normal.Error);
    }

    // ------------------------------------------------------------------ archives not protected as Osmium protects them

    [Fact]
    public async Task FramesThatOnlyClaimEncryptionAreRefusedWithNothingWritten()
    {
        // Every block's body replaced by random bytes: the file list authenticates, so a listing works; no block does.
        string path = Write("random.osa", Compatible);
        var b = File.ReadAllBytes(path);
        using (var r = OsaReader.Open(path, () => Pw))
            foreach (var x in r.Blocks) Random.Shared.NextBytes(b.AsSpan((int)x.Offset + Osa.FrameHeaderSize, (int)x.FrameLength - Osa.FrameHeaderSize));
        File.WriteAllBytes(path, b);
        var listed = await WithPassword("l", path);
        Assert.True(listed.ExitCode == 0, listed.Error);
        foreach (var command in new[] { "x", "t" })
        {
            var r = await WithPassword(command == "x" ? ["x", path, P("out")] : ["t", path]);
            Assert.Equal(1, r.ExitCode);
            Assert.Contains("error: " + ModifiedArchiveException.DefaultMessage, r.Error);
        }
        Assert.True(NothingIn(P("out")));
    }

    [Fact]
    public async Task ABlockFrameSwappedForAPlaintextOneIsRefusedByEveryCommand()
    {
        string path = Write("swapped.osa", Compatible);
        var b = File.ReadAllBytes(path);
        long at;
        using (var r = OsaReader.Open(path, () => Pw)) at = r.Blocks[1].Offset;
        b[at + 4] = 0; // the frame says it is not encrypted, its CRC right
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)at + 24), Osa.Crc(b.AsSpan((int)at, 24)));
        File.WriteAllBytes(path, b);
        foreach (var args in new[] { new[] { "x", path, P("out") }, ["t", path], ["l", path] })
        {
            var r = await WithPassword(args);
            Assert.Equal(1, r.ExitCode);
            Assert.Contains("error: " + ModifiedArchiveException.DefaultMessage, r.Error);
            Assert.DoesNotContain("--allow-weak-protection", r.Error); // no answer lifts this
        }
        Assert.True(NothingIn(P("out")));
    }

    [Fact]
    public async Task DataOutsideTheProtectedPartsIsRefusedUnlessTheUserAgrees()
    {
        // A marker in the zero padding of the header copy H.
        string path = Write("room.osa", Compatible, SpareCopyPolicy.Always);
        var b = File.ReadAllBytes(path);
        long indexOffset = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(b.Length - Osa.FooterSize));
        var trailer = SpareTrailer.Read(b.AsSpan((int)indexOffset - SpareCopies.TrailerSize, SpareCopies.TrailerSize))!.Value;
        long headerCopy = indexOffset - trailer.CopyLength;
        "KNOWN-PLAINTEXT-MARKER"u8.CopyTo(b.AsSpan((int)(headerCopy + trailer.HeaderLength + 8)));
        File.WriteAllBytes(path, b);
        foreach (var args in new[] { new[] { "x", path, P("out") }, ["t", path], ["l", path] })
        {
            var r = await WithPassword(args);
            Assert.Equal(1, r.ExitCode);
            Assert.Contains(UnprotectedDataException.MayBeDamageMessage, r.Error);
            Assert.Contains("Run again with --allow-weak-protection to open it anyway.", r.Error);
        }
        Assert.True(NothingIn(P("out")));
        var allowed = await WithPassword("l", path, "--allow-weak-protection");
        Assert.True(allowed.ExitCode == 0, allowed.Error);
        Assert.Contains("warning: This archive holds data outside its protected parts; Osmium ignored it.", allowed.Error);
    }

    // ------------------------------------------------------------------ what the commands say

    public static TheoryData<string, string, string> Kdfs => new()
    {
        { "pbkdf2", "PBKDF2-SHA512, 100,000 iterations", "PBKDF2-SHA512, 100,000 iterations; Osmium 0.1 can open it" },
        { "argon2id", "Argon2id, 256 MiB, 3 passes, 4 lanes", "Argon2id, 256 MiB, 3 passes, 4 lanes" },
    };

    [Theory]
    [MemberData(nameof(Kdfs))]
    public async Task InfoSaysVerifiedOnlyOnceThePasswordWasChecked(string kdf, string asks, string key)
    {
        string path = Write($"{kdf}.osa", kdf == "pbkdf2" ? Compatible : KdfPresets.Floor, creator: "Osmium cli-test");
        var peek = await Run("", "info", path);
        Assert.True(peek.ExitCode == 0, peek.Error);
        Assert.Contains($"Encrypted:    password not yet checked (the header asks for {asks})", peek.Output);
        Assert.DoesNotContain("verified", peek.Output + peek.Error);
        Assert.DoesNotContain("a.txt", peek.Output); // nothing from the file list

        var info = await WithPassword("info", path);
        Assert.True(info.ExitCode == 0, info.Error);
        Assert.Contains($"Encrypted:    yes, password verified (AES-256-GCM; key from {key})", info.Output);
        Assert.Contains("created by Osmium cli-test", info.Output); // from the encrypted file list (no header record)
        var list = await WithPassword("l", path);
        Assert.StartsWith($"Encrypted:    yes, password verified (AES-256-GCM; key from {key})", list.Output);

        foreach (var command in new[] { "info", "l", "t" })
        {
            var wrong = await Run("not the password at all\n", command, path, "--password-stdin");
            Assert.Equal(1, wrong.ExitCode);
            Assert.DoesNotContain("verified", wrong.Output + wrong.Error);
        }
    }

    /// <summary>What setting up remote workers prints and returns, the console redirected.</summary>
    private static async Task<(bool Connected, string Output, string Error)> ClusterFor(bool encrypted, params string[] options)
    {
        TextWriter output = Console.Out, error = Console.Error;
        using var o = new StringWriter();
        using var e = new StringWriter();
        try
        {
            Console.SetOut(o);
            Console.SetError(e);
            // A port nothing listens on: a connection is refused at once.
            var a = new Args(["--workers", "127.0.0.1:1", "--cluster-key", Osmium.Core.Distributed.ClusterKey.Generate().ToString(), .. options]);
            var po = new Osmium.Core.Planning.PlanOptions { Profile = Osmium.Core.Planning.Profile.ByName("store"), Threads = 1 };
            await using var cluster = await ClusterSetup.CreateAsync(a, po, encrypted, TestContext.Current.CancellationToken);
            return (cluster is not null, o.ToString(), e.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    [Fact]
    public async Task RemoteWorkersNeverSeeAnEncryptedArchiveWithoutAllowRemoteEncrypted()
    {
        // Encrypted whichever way the password came (here no password option at all: the prompt of --encrypt).
        var refused = await ClusterFor(encrypted: true);
        Assert.False(refused.Connected);
        Assert.Contains("note: remote workers are disabled for encrypted archives", refused.Error);
        Assert.DoesNotContain("cluster:", refused.Output); // no connection was tried
        var allowed = await ClusterFor(encrypted: true, "--allow-remote-encrypted");
        Assert.DoesNotContain("disabled for encrypted archives", allowed.Error);
        Assert.Contains("cluster:", allowed.Output); // a connection was tried
        var plain = await ClusterFor(encrypted: false);
        Assert.DoesNotContain("disabled for encrypted archives", plain.Error);
    }

    [Fact]
    public async Task SalvageWithAPasswordWritesNoFrameThatIsNotEncrypted()
    {
        // An unencrypted archive whose header is destroyed: with a password given, it counts as encrypted.
        var made = await Run("", "a", P("plain.osa"), P("in"), "--profile", "store", "-q", "--spare-copies", "never");
        Assert.True(made.ExitCode == 0, made.Error);
        var b = File.ReadAllBytes(P("plain.osa"));
        Array.Clear(b, 0, 40);
        File.WriteAllBytes(P("plain.osa"), b);
        var withPassword = await WithPassword("salvage", P("plain.osa"), P("out"));
        Assert.Contains("The archive counts as encrypted (a password was given)", withPassword.Output);
        Assert.False(File.Exists(P("out", "in", "a.txt")));
        var without = await Run("", "salvage", P("plain.osa"), P("out2"));
        Assert.True(without.ExitCode == 0, without.Output + without.Error);
        Assert.Equal("the contents", File.ReadAllText(P("out2", "in", "a.txt")));
    }
}
