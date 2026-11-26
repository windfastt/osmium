using System.Buffers.Binary;
using Osmium.Core.Container;

namespace Osmium.Cli.Tests;

/// <summary>--spare-copies chooses whether an .osa archive gets a spare copy of its file list and header; info shows
/// the archive's own version and its spare copy, and a test that verifies every file but finds the spare copy damaged
/// ends with its own exit code instead of a plain OK.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class SpareCopyOptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osmium-cli-" + Guid.NewGuid().ToString("N")[..8]);

    public SpareCopyOptionTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "in"));
        File.WriteAllText(Path.Combine(_dir, "in", "a.txt"), "a small file that is kept twice over");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(params string[] parts) => Path.Combine([_dir, .. parts]);

    private async Task<string> Write(string name, params string[] options)
    {
        var r = await CliRunner.RunAsync(["a", P(name), P("in"), "--profile", "store", "-q", .. options]);
        Assert.True(r.ExitCode == 0, r.Error);
        return P(name);
    }

    [Theory]
    [InlineData("always", true)]
    [InlineData("never", false)]
    [InlineData("auto", false)] // a small archive: auto adds none
    public async Task TheOptionChoosesTheSpareCopy(string value, bool expected)
    {
        string path = await Write("s.osa", "--spare-copies", value);
        using (var r = OsaReader.Open(path, null)) Assert.Equal(expected, r.HasSpareCopies);
        var info = await CliRunner.RunAsync(["info", path]);
        Assert.Equal(0, info.ExitCode);
        Assert.Contains(expected ? "Spare copy:   yes, of the file list and header" : "Spare copy:   none", info.Output);
    }

    [Fact]
    public async Task AnUnknownValueIsRefusedAndWritesNothing()
    {
        var r = await CliRunner.RunAsync(["a", P("s.osa"), P("in"), "--profile", "store", "--spare-copies", "sometimes"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--spare-copies must be auto, always or never", r.Error);
        Assert.False(File.Exists(P("s.osa")));
    }

    [Fact]
    public async Task AZipFileHasNoSpareCopyToChoose()
    {
        var r = await CliRunner.RunAsync(["a", P("s.zip"), P("in"), "--spare-copies", "always"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--spare-copies applies to .osa archives", r.Error);
        Assert.False(File.Exists(P("s.zip")));
    }

    [Fact]
    public async Task InfoShowsTheVersionTheArchiveWasWrittenIn()
    {
        string path = await Write("old.osa", "--spare-copies", "never");
        // The same archive as container 1.0: the header's minor version and its checksum.
        var bytes = File.ReadAllBytes(path);
        int length;
        using (var r = OsaReader.Open(path, null)) length = r.Header.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(length - 4), Osa.Crc(bytes.AsSpan(0, length - 4)));
        File.WriteAllBytes(path, bytes);
        var info = await CliRunner.RunAsync(["info", path]);
        Assert.True(info.ExitCode == 0, info.Error);
        Assert.Contains("Format:       .osa 1.0   created by", info.Output);
    }

    [Fact]
    public async Task ATestThatFindsTheSpareCopyDamagedIsNotAPlainOk()
    {
        string path = await Write("s.osa", "--spare-copies", "always");
        var good = await CliRunner.RunAsync(["t", path]);
        Assert.Equal(0, good.ExitCode);
        Assert.StartsWith("OK — 1 file(s)", good.Output);

        long spare;
        using (var r = OsaReader.Open(path, null)) spare = (long)BinaryPrimitives.ReadUInt64LittleEndian(r.Index.Metadata[ArchiveIndex.SparesKey]);
        var bytes = File.ReadAllBytes(path);
        bytes[spare + 12] ^= 0x55; // inside the spare copy of the file list
        File.WriteAllBytes(path, bytes);

        var t = await CliRunner.RunAsync(["t", path]);
        Assert.Equal(3, t.ExitCode);
        Assert.Contains("warning: ", t.Error);
        Assert.StartsWith("OK with 1 warning(s) — 1 file(s)", t.Output);
        // Extracting is unaffected: every file verified.
        var x = await CliRunner.RunAsync(["x", path, P("out"), "-q"]);
        Assert.True(x.ExitCode == 0, x.Error);
        Assert.Equal("a small file that is kept twice over", File.ReadAllText(P("out", "in", "a.txt")));
    }
}
