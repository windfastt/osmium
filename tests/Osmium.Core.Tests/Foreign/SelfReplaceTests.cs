using System.IO.Hashing;
using Osmium.Core.Archives;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Foreign;

/// <summary>Extraction never replaces the archive it reads, also when the destination reaches the archive's folder by
/// another path (a symbolic link to it, a junction): the archive is known by its file identity, not by its path.</summary>
public class SelfReplaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>"w/backup.zip" holding an entry named backup.zip, and "w/self.osa" holding one named self.osa.</summary>
    private static (string Zip, string Osa) Archives(TempDir t)
    {
        Directory.CreateDirectory(t.Sub("w"));
        string zip = Path.Combine(t.Sub("w"), "backup.zip");
        File.WriteAllBytes(zip, ForeignData.Zip([("backup.zip", "an entry named like the archive"u8.ToArray()), ("later.txt", "later"u8.ToArray())]));
        string osa = Path.Combine(t.Sub("w"), "self.osa");
        var data = "an entry named like the archive"u8.ToArray();
        using (var w = new OsaWriter(osa))
        {
            var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", MethodRegistry.Default), data, CodecContext.None);
            int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, data.Length);
            w.Finish([new ArchiveEntry { Path = "self.osa", Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(frame, 0, data.Length)], Hash = XxHash128.HashToUInt128(data) }]);
            w.Commit(overwrite: false);
        }
        return (zip, osa);
    }

    /// <summary>Extracts both archives with Overwrite into <paramref name="other"/>, a second path to their folder, and
    /// checks that neither was replaced.</summary>
    private static void ExtractThrough(string other, string zip, string osa)
    {
        var zipBefore = File.ReadAllBytes(zip);
        var osaBefore = File.ReadAllBytes(osa);
        using (var s = ArchiveSources.Open(zip, null, Ct))
        {
            var r = s.Extract(other, new ArchiveExtractRequest { Overwrite = OverwriteMode.Overwrite }, null, Ct);
            Assert.True((r.FilesOk, r.FilesFailed) == (2, 0), string.Join("; ", r.Errors));
        }
        Assert.Equal(zipBefore, File.ReadAllBytes(zip));
        Assert.Equal("an entry named like the archive", File.ReadAllText(Path.Combine(other, "backup (2).zip")));
        using (var reader = OsaReader.Open(osa))
        {
            var r = Extractor.Extract(reader, other, null, new ExtractOptions { Overwrite = OverwriteMode.Overwrite }, null, Ct);
            Assert.True((r.FilesOk, r.FilesFailed) == (1, 0), string.Join("; ", r.Errors));
        }
        Assert.Equal(osaBefore, File.ReadAllBytes(osa));
        Assert.Equal("an entry named like the archive", File.ReadAllText(Path.Combine(other, "self (2).osa")));
    }

    [Fact]
    public void ThroughALinkToItsFolderTheArchiveIsNotReplaced()
    {
        using var t = new TempDir();
        var (zip, osa) = Archives(t);
        try { Directory.CreateSymbolicLink(t.Sub("wlink"), t.Sub("w")); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("creating a symbolic link needs Developer Mode or elevation on Windows");
        }
        ExtractThrough(t.Sub("wlink"), zip, osa);
    }

    [Fact]
    public void ThroughAJunctionToItsFolderTheArchiveIsNotReplaced()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Junctions exist only on Windows.");
        using var t = new TempDir();
        var (zip, osa) = Archives(t);
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{t.Sub("junction")}\" \"{t.Sub("w")}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(t.Sub("junction")));
        ExtractThrough(t.Sub("junction"), zip, osa);
    }

    [Fact]
    public void AFileIsKnownByItsIdentityUnderEveryPath()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) Assert.Skip("File identity is read on Windows and Linux only.");
        using var t = new TempDir();
        var (zip, osa) = Archives(t);
        string? id = TargetNames.FileIdentity(zip);
        Assert.NotNull(id);
        Assert.NotEqual(id, TargetNames.FileIdentity(osa));
        string link = t.Sub("link.zip");
        try { File.CreateSymbolicLink(link, zip); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("creating a symbolic link needs Developer Mode or elevation on Windows");
        }
        Assert.Equal(id, TargetNames.FileIdentity(link));
        using var open = File.OpenHandle(zip);
        Assert.Equal(id, TargetNames.FileIdentity(open));
    }
}
