using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Osmium.Cli.Tests;

/// <summary>l, x, t and info on archives other than .osa, exit codes, and what `a` refuses to write.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class ForeignFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osmium-cli-" + Guid.NewGuid().ToString("N")[..8]);

    public ForeignFormatTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(params string[] parts) => Path.Combine([_dir, .. parts]);

    private static readonly (string Name, string Text)[] Tree =
    [
        ("docs/readme.txt", "Read me first.\n"),
        ("docs/notes.txt", string.Concat(Enumerable.Repeat("a note\n", 500))),
        ("top.txt", "top level\n"),
    ];

    private string Zip(string name, IEnumerable<(string Name, string Text)> entries)
    {
        string path = P(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (n, text) in entries)
        {
            using var s = zip.CreateEntry(n).Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }
        return path;
    }

    private string TarGz(string name)
    {
        string path = P(name);
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        using var w = new TarWriter(gz, TarEntryFormat.Pax);
        foreach (var (n, text) in Tree)
            w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, n) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        return path;
    }

    [Fact]
    public async Task ListsZipAndTarGz()
    {
        foreach (var path in new[] { Zip("a.zip", Tree), TarGz("a.tar.gz") })
        {
            var r = await CliRunner.RunAsync(["l", path]);
            Assert.Equal(0, r.ExitCode);
            foreach (var (n, _) in Tree) Assert.Contains(n, r.Output);
            Assert.Contains("3 file(s)", r.Output);
            var v = await CliRunner.RunAsync(["l", path, "-v"]);
            Assert.Contains(path.EndsWith(".zip") ? "Deflate" : "TAR", v.Output);
        }
    }

    [Fact]
    public async Task ExtractsToAFolderNamedAfterTheArchive()
    {
        foreach (var (path, stem) in new[] { (Zip("photos.zip", Tree), "photos"), (TarGz("photos2.tar.gz"), "photos2") })
        {
            var r = await CliRunner.RunAsync(["x", path]);
            Assert.Equal(0, r.ExitCode);
            foreach (var (n, text) in Tree) Assert.Equal(text, File.ReadAllText(Path.Combine([_dir, stem, .. n.Split('/')])));
            Assert.Contains("Extracted 3 file(s)", r.Output);
        }
    }

    [Fact]
    public async Task ASingleCompressedFileIsExtractedNextToIt()
    {
        string path = P("notes.txt.gz");
        using (var fs = File.Create(path))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            gz.Write(Encoding.UTF8.GetBytes("the notes\n"));
        var r = await CliRunner.RunAsync(["x", path, "-q"]);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("the notes\n", File.ReadAllText(P("notes.txt")));
        Assert.False(Directory.Exists(P("notes.txt")));
        // Again: the name is taken, so the file gets a free one; nothing is replaced.
        var again = await CliRunner.RunAsync(["x", path, "-q"]);
        Assert.Equal(0, again.ExitCode);
        Assert.Equal("the notes\n", File.ReadAllText(P("notes (2).txt")));
        Assert.Equal(["notes (2).txt", "notes.txt", "notes.txt.gz"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ACompressedFileWithoutAnExtensionNeverReplacesItself()
    {
        string path = P("data");
        using (var fs = File.Create(path))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            gz.Write(Encoding.UTF8.GetBytes("the original text\n"));
        var before = File.ReadAllBytes(path);
        var r = await CliRunner.RunAsync(["x", path, "--overwrite", "-q"]);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("the original text\n", File.ReadAllText(P("data (decompressed)")));
        // Into the archive's folder under its own name, as an archive that holds a file of its name would put it.
        var into = await CliRunner.RunAsync(["x", Zip("backup.zip", [("backup.zip", "an entry named like the archive")]), _dir, "--overwrite", "-q"]);
        Assert.Equal(0, into.ExitCode);
        Assert.Equal("an entry named like the archive", File.ReadAllText(P("backup (2).zip")));
        using var zip = ZipFile.OpenRead(P("backup.zip"));
        using var entry = new StreamReader(zip.Entries[0].Open());
        Assert.Equal("an entry named like the archive", entry.ReadToEnd());
    }

    [Fact]
    public async Task ExtractsIntoAGivenFolderWithAnIncludeFilter()
    {
        string path = Zip("a.zip", Tree);
        var r = await CliRunner.RunAsync(["x", path, P("out"), "--include", "docs/*"]);
        Assert.Equal(0, r.ExitCode);
        Assert.True(File.Exists(P("out", "docs", "readme.txt")));
        Assert.False(File.Exists(P("out", "top.txt")));
    }

    [Fact]
    public async Task TestsAndSaysWhatIsNotChecked()
    {
        var zip = await CliRunner.RunAsync(["t", Zip("a.zip", Tree)]);
        Assert.Equal(0, zip.ExitCode);
        Assert.Contains("OK — 3 file(s) read.", zip.Output);
        var tar = await CliRunner.RunAsync(["t", TarGz("a.tgz")]);
        Assert.Equal(0, tar.ExitCode);
        Assert.Contains("3 of them have no stored checksum", tar.Output);
        Assert.Contains("warning: 3 file(s) have no stored checksum; the archive's structure was read completely", tar.Error);
    }

    [Fact]
    public async Task InfoShowsTheDetails()
    {
        var r = await CliRunner.RunAsync(["info", Zip("a.zip", Tree)]);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Format:       ZIP", r.Output);
        Assert.Contains("Methods:      Deflate", r.Output);
        Assert.Contains("Encrypted:    no", r.Output);
    }

    [Fact]
    public async Task ControlCharactersInNamesAreShownEscaped()
    {
        string path = Zip("esc.zip", [("bad\u001b[2Jname.txt", "x"), ("‮txt.exe", "y")]);
        var r = await CliRunner.RunAsync(["l", path]);
        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain('\u001b', r.Output);
        Assert.DoesNotContain('‮', r.Output);
        Assert.Contains("bad\\u001b[2Jname.txt", r.Output);
        Assert.Contains("<U+202E>txt.exe", r.Output);
    }

    [Fact]
    public async Task UnsafeNamesExitWithTwo()
    {
        string path = Zip("evil.zip", [("good.txt", "good"), ("../evil.txt", "evil"), ("CON", "device")]);
        var r = await CliRunner.RunAsync(["x", path, P("out")]);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("../evil.txt: not extracted", r.Error);
        Assert.Contains("2 item(s) were not extracted", r.Output);
        Assert.True(File.Exists(P("out", "good.txt")));
        Assert.False(File.Exists(P("evil.txt")));
    }

    [Fact]
    public async Task AFolderThatCannotBeMadeEndsTheRunWithTwo()
    {
        // A folder 22 levels of 200 characters deep is past what Linux takes in one path: it cannot be made, and
        // although no file of the archive fails, the error makes the run fail.
        if (OperatingSystem.IsWindows()) Assert.Skip("Windows makes folders of any length (long paths), so this folder can be made there.");
        string deep = string.Join('/', Enumerable.Repeat(new string('d', 200), 22)) + "/";
        string path = Zip("folder.zip", [("ok.txt", "fine"), (deep, "")]);
        var r = await CliRunner.RunAsync(["x", path, P("out")]);
        Assert.Contains("error: ddd", r.Error);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("1 error(s) above", r.Output);
        Assert.Equal("fine", File.ReadAllText(P("out", "ok.txt")));
    }

    [Fact]
    public async Task AFileInTheDestinationWhereTheArchiveHasAFolderGivesTheFolderAFreeName()
    {
        string path = Zip("folder.zip", [("ok.txt", "fine"), ("x/sub/f.txt", "below")]);
        Directory.CreateDirectory(P("out"));
        File.WriteAllText(P("out", "x"), "a file where the archive has a folder");
        var r = await CliRunner.RunAsync(["x", path, P("out")]);
        Assert.True(r.ExitCode == 0, r.Error);
        Assert.Contains("was saved as 'x (2)/sub/f.txt'", r.Error);
        Assert.Equal("below", File.ReadAllText(P("out", "x (2)", "sub", "f.txt")));
        Assert.Equal("a file where the archive has a folder", File.ReadAllText(P("out", "x")));
    }

    [Fact]
    public async Task AnArchiveThatWouldExpandTooMuchNeedsForce()
    {
        string path = Zip("bomb.zip", [("big.bin", "small really")]);
        // Make both headers claim 2 GiB, far past 1,000 times the file's size.
        var bytes = File.ReadAllBytes(path);
        for (int i = 0; i + 4 <= bytes.Length; i++)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
            if (sig == 0x04034b50) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 22), 0x7FFFFFFF);
            if (sig == 0x02014b50) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), 0x7FFFFFFF);
        }
        File.WriteAllBytes(path, bytes);
        // The message formats its number for the user's culture; this test reads it in a fixed one.
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        CliResult r;
        try { r = await CliRunner.RunAsync(["x", path, P("out")]); }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("This archive expands to more than 1,000 times its size. Use --force to extract it anyway.", r.Error);
        Assert.False(Directory.Exists(P("out")));
        // --force lifts only that: the file then fails its size check (it is not 2 GiB), so nothing is kept.
        var forced = await CliRunner.RunAsync(["x", path, P("out"), "--force"]);
        Assert.Equal(2, forced.ExitCode);
        Assert.False(File.Exists(P("out", "big.bin")));
    }

    [Fact]
    public async Task DamagedAndUnsupportedArchivesExitWithOne()
    {
        string zip = Zip("cut.zip", Tree);
        File.WriteAllBytes(zip, File.ReadAllBytes(zip)[..^30]);
        Assert.Equal(1, (await CliRunner.RunAsync(["l", zip])).ExitCode);
        File.WriteAllBytes(P("a.rar"), [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00, 1, 2, 3, 4]);
        var rar = await CliRunner.RunAsync(["x", P("a.rar")]);
        Assert.Equal(1, rar.ExitCode);
        Assert.Contains("RAR archives are not supported by this version of Osmium.", rar.Error);
    }

    [Theory]
    [InlineData("x.rar", "Osmium cannot create RAR archives.")]
    [InlineData("x.7z", "Osmium cannot write 7z archives; use .osa or .zip.")]
    [InlineData("x.tar", "Osmium cannot write TAR archives; use .osa or .zip.")]
    [InlineData("x.tar.gz", "Osmium cannot write TAR archives; use .osa or .zip.")]
    [InlineData("x.tgz", "Osmium cannot write TAR archives; use .osa or .zip.")]
    [InlineData("x.gz", "Osmium cannot write gzip archives; use .osa or .zip.")]
    [InlineData("x.bz2", "Osmium cannot write bzip2 archives; use .osa or .zip.")]
    [InlineData("x.xz", "Osmium cannot write xz archives; use .osa or .zip.")]
    [InlineData("x.zst", "Osmium cannot write Zstandard archives; use .osa or .zip.")]
    public async Task NamesOfTypesOsmiumDoesNotWriteAreRefused(string name, string message)
    {
        File.WriteAllText(P("in.txt"), "input");
        var r = await CliRunner.RunAsync(["a", P(name), P("in.txt")]);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("error: " + message, r.Error.Trim());
        Assert.Equal(["in.txt"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("--password", "pw", "ZIP files made by Osmium are not encrypted; use an .osa archive with --password.")]
    [InlineData("--profile", "max", "--profile does not apply to ZIP files")]
    [InlineData("--pipeline", "store", "--pipeline does not apply to ZIP files")]
    public async Task OptionsThatDoNotApplyToZipAreRefused(string option, string value, string message)
    {
        File.WriteAllText(P("in.txt"), "input");
        var r = await CliRunner.RunAsync(["a", P("x.zip"), P("in.txt"), option, value]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(message, r.Error);
        Assert.False(File.Exists(P("x.zip")));
    }

    [Fact]
    public async Task OtherNamesStillGetAnOsaArchive()
    {
        File.WriteAllText(P("in.txt"), "input");
        var r = await CliRunner.RunAsync(["a", P("backup.2024"), P("in.txt"), "--profile", "store", "-q"]);
        Assert.Equal(0, r.ExitCode);
        Assert.True(File.Exists(P("backup.2024.osa")));
    }

    [Fact]
    public async Task WritesAZipThatOtherReadersOpen()
    {
        Directory.CreateDirectory(P("in", "sub"));
        File.WriteAllText(P("in", "a.txt"), string.Concat(Enumerable.Repeat("text to deflate\n", 1000)));
        File.WriteAllText(P("in", "sub", "b.txt"), "small");
        var r = await CliRunner.RunAsync(["a", P("x.zip"), P("in")]);
        Assert.True(r.ExitCode == 0, r.Error);
        Assert.Contains("Created ", r.Output);
        using var zip = ZipFile.OpenRead(P("x.zip"));
        var a = zip.Entries.Single(e => e.FullName.EndsWith("a.txt", StringComparison.Ordinal));
        using var reader = new StreamReader(a.Open());
        Assert.Equal(File.ReadAllText(P("in", "a.txt")), reader.ReadToEnd());
        var t = await CliRunner.RunAsync(["t", P("x.zip")]);
        Assert.Equal(0, t.ExitCode);
    }
}
