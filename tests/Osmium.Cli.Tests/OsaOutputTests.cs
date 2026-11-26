using System.Globalization;
using System.IO.Hashing;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;

namespace Osmium.Cli.Tests;

/// <summary>The output of l and info for an .osa archive stays what it was before other formats were added; info also
/// shows the archive's own container version and whether it has a spare copy of its file list.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class OsaOutputTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osmium-cli-" + Guid.NewGuid().ToString("N")[..8]);

    public OsaOutputTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>An archive written entry by entry, without times, so its listing does not depend on the clock.</summary>
    internal static string Deterministic(string dir)
    {
        string path = Path.Combine(dir, "fixed.osa");
        var data = "deterministic content for the listing\n"u8.ToArray();
        using var w = new OsaWriter(path);
        var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", MethodRegistry.Default), data, CodecContext.None);
        int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, data.Length);
        w.Finish(
        [
            new ArchiveEntry { Path = "docs", Kind = EntryKind.Directory },
            new ArchiveEntry { Path = "docs/a.txt", Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(frame, 0, data.Length)], Hash = XxHash128.HashToUInt128(data) },
            new ArchiveEntry { Path = "empty.bin", Kind = EntryKind.File, Size = 0, Hash = XxHash128.HashToUInt128([]) },
        ]);
        w.Commit(overwrite: false);
        return path;
    }

    /// <summary>The values that depend on the build (the creator's version and so the archive's size) and the
    /// folder, filled in; everything else is the output as it was.</summary>
    private static (string Archive, string Creator, string Size) Facts(string path)
    {
        using var r = OsaReader.Open(path, null);
        long length = new FileInfo(path).Length;
        return (path, r.Header.Creator ?? "", $"{length} B ({(double)length / 38:P2})");
    }

    [Theory]
    [InlineData("...osa", "...osa files")]
    [InlineData("..osa", "..osa files")]
    [InlineData("CON.osa", "_CON.osa files")]
    [InlineData("photos.osa", "photos")]
    public async Task TheDefaultFolderIsAlwaysInsideTheArchivesFolder(string name, string folder)
    {
        string box = Path.Combine(_dir, "box");
        Directory.CreateDirectory(box);
        File.Copy(Deterministic(_dir), Path.Combine(box, name));
        var r = await CliRunner.RunAsync(["x", Path.Combine(box, name), "-q"]);
        Assert.True(r.ExitCode == 0, r.Error);
        Assert.True(File.Exists(Path.Combine(box, folder, "docs", "a.txt")));
        // Nothing next to the folder holding the archive, and nothing else beside the archive.
        Assert.Equal(["box", "fixed.osa"], Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { folder, name }.Order(StringComparer.Ordinal), Directory.GetFileSystemEntries(box).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ListingIsUnchanged()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            string path = Deterministic(_dir);
            var (_, _, size) = Facts(path);
            var r = await CliRunner.RunAsync(["l", path]);
            Assert.Equal(0, r.ExitCode);
            Assert.Equal(
                "            38    docs/a.txt\n" +
                "             0    empty.bin\n" +
                $"            38  2 file(s) in {size}\n", r.Output.Replace("\r\n", "\n"));
            var v = await CliRunner.RunAsync(["l", path, "-v"]);
            Assert.Equal(
                "         <dir>    docs/\n" +
                "            38    docs/a.txt  store\n" +
                "             0    empty.bin\n" +
                $"            38  2 file(s) in {size}\n", v.Output.Replace("\r\n", "\n"));
            Assert.Empty(r.Error + v.Error);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public async Task InfoShowsTheArchivesOwnVersion()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            string path = Deterministic(_dir);
            var (archive, creator, size) = Facts(path);
            var r = await CliRunner.RunAsync(["info", path]);
            Assert.Equal(0, r.ExitCode);
            Assert.Equal(
                $"Archive:      {archive}\n" +
                $"Format:       .osa 1.1   created by {creator}\n" +
                "Spare copy:   none\n" +
                "Encrypted:    no\n" +
                "Experimental: no\n" +
                "Profile:      ?\n" +
                "Content:      2 files, 1 dirs, 38 B\n" +
                $"Archive size: {size}\n" +
                "Dedup:        0 B saved by deduplication\n" +
                "Extract cost: ~<1 s single-core, ≤ 1.0 KiB RAM per block\n" +
                "Blocks (1):\n" +
                "  #0          38 B →       73 B  store\n", r.Output.Replace("\r\n", "\n"));
            Assert.Empty(r.Error);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
}
