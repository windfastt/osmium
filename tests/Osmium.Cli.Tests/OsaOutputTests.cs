using System.Globalization;
using System.IO.Hashing;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;

namespace Osmium.Cli.Tests;

/// <summary>The output of l and info for an .osa archive stays what it was before other formats were added.</summary>
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
    public async Task InfoIsUnchanged()
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
                $"Format:       .osa 1.0   created by {creator}\n" +
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
