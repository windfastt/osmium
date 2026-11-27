using System.IO.Hashing;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests;

/// <summary>Salvage never replaces the archive it reads, also when the destination reaches the archive's folder by
/// another path (a symbolic link to it): the archive is known by its file identity, as in extraction.</summary>
public class SalvageSelfReplaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Data = "an entry named like the archive"u8.ToArray();

    /// <summary>"w/<paramref name="name"/>": an archive holding one file of that name.</summary>
    private static string Archive(TempDir t, string name)
    {
        Directory.CreateDirectory(t.Sub("w"));
        string path = Path.Combine(t.Sub("w"), name);
        using var w = new OsaWriter(path, new OsaWriterOptions { SpareCopies = SpareCopyPolicy.Never });
        var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", MethodRegistry.Default), Data, CodecContext.None);
        int frame = w.WriteBlock(desc, BlockBody.Hash128(Data), payload, Data.Length);
        w.Finish([new ArchiveEntry { Path = name, Kind = EntryKind.File, Size = Data.Length, Segments = [new Segment(frame, 0, Data.Length)], Hash = XxHash128.HashToUInt128(Data) }]);
        w.Commit(overwrite: false);
        return path;
    }

    /// <summary>A second path to "w", or a skip where links cannot be made.</summary>
    private static string LinkToFolder(TempDir t)
    {
        try { Directory.CreateSymbolicLink(t.Sub("wlink"), t.Sub("w")); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("creating a symbolic link needs Developer Mode or elevation on Windows");
        }
        return t.Sub("wlink");
    }

    [Fact]
    public void ARecoveredFileOfTheArchivesNameGetsAFreeName()
    {
        using var t = new TempDir();
        string archive = Archive(t, "self.osa");
        string other = LinkToFolder(t);
        var before = File.ReadAllBytes(archive);
        var r = Container.Salvage.Run(archive, other, null, new SalvageOptions { Overwrite = OverwriteMode.Overwrite, MemoryBudget = 1L << 30 }, Ct);
        Assert.True(r.FilesRecovered == 1, string.Join("; ", r.Summary()));
        Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.Equal(Data, File.ReadAllBytes(Path.Combine(other, "self (2).osa")));
    }

    [Fact]
    public void ABlockFileOfTheArchivesNameGetsAFreeName()
    {
        // The file list cut off, so salvage writes block files; the archive is named as the first of them.
        using var t = new TempDir();
        string archive = Archive(t, "block-0.bin");
        long indexOffset;
        using (var fs = File.OpenRead(archive)) indexOffset = ArchiveFooter.Read(fs).IndexOffset;
        var cut = File.ReadAllBytes(archive)[..(int)indexOffset];
        File.WriteAllBytes(archive, cut);
        string other = LinkToFolder(t);
        var r = Container.Salvage.Run(archive, other, null, new SalvageOptions { Overwrite = OverwriteMode.Overwrite, MemoryBudget = 1L << 30 }, Ct);
        Assert.False(r.IndexRecovered);
        Assert.Equal(cut, File.ReadAllBytes(archive));
        Assert.Equal(Data, File.ReadAllBytes(Path.Combine(other, "block-0 (2).bin")));
    }
}
