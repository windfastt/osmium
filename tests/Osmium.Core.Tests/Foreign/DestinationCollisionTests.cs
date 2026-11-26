using System.IO.Hashing;
using Osmium.Core.Archives;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Foreign;

/// <summary>
/// A name that is already in the destination follows the overwrite mode (FORMAT §7), for .osa archives and for other
/// formats alike, and nothing already there is ever replaced by something of the other kind: a file where the archive
/// has a folder gives the folder a free name (Rename, Overwrite), skips it (Skip) or fails it (Fail); a folder where the
/// archive has a file gives the file a free name in Rename and Overwrite.
/// </summary>
public class DestinationCollisionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly (string Path, string Text)[] Entries = [("sub/f1.txt", "below sub"), ("report", "a file named report"), ("a.txt", "a")];

    /// <summary>What one extraction did: files, failures, skips, renames.</summary>
    private sealed record Outcome(int Ok, int Failed, int Skipped, int Renamed, IReadOnlyList<string> Errors);

    private static Outcome Extract(string kind, TempDir t, string dest, OverwriteMode mode)
    {
        if (kind == "zip")
        {
            string zip = ForeignData.Write(t, "a.zip", ForeignData.Zip(Entries.Select(e => (e.Path, System.Text.Encoding.UTF8.GetBytes(e.Text)))));
            using var s = ArchiveSources.Open(zip, null, Ct);
            var r = s.Extract(dest, new ArchiveExtractRequest { Overwrite = mode }, null, Ct);
            return new(r.FilesOk, r.FilesFailed, r.SkippedExisting, r.Renamed, r.Errors);
        }
        string osa = t.Sub("a.osa");
        using (var w = new OsaWriter(osa))
        {
            var entries = new List<ArchiveEntry> { new() { Path = "sub", Kind = EntryKind.Directory } };
            foreach (var (path, text) in Entries)
            {
                var data = System.Text.Encoding.UTF8.GetBytes(text);
                var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", MethodRegistry.Default), data, CodecContext.None);
                int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, data.Length);
                entries.Add(new ArchiveEntry { Path = path, Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(frame, 0, data.Length)], Hash = XxHash128.HashToUInt128(data) });
            }
            w.Finish(entries);
            w.Commit(overwrite: false);
        }
        using var reader = OsaReader.Open(osa);
        var x = Extractor.Extract(reader, dest, null, new ExtractOptions { Overwrite = mode }, null, Ct);
        return new(x.FilesOk, x.FilesFailed, x.FilesSkipped, x.Renamed, x.Errors);
    }

    public static TheoryData<string, OverwriteMode> Cases()
    {
        var data = new TheoryData<string, OverwriteMode>();
        foreach (var kind in new[] { "zip", "osa" })
            foreach (var mode in new[] { OverwriteMode.Rename, OverwriteMode.Overwrite, OverwriteMode.Skip, OverwriteMode.Fail })
                data.Add(kind, mode);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WhatIsAlreadyThereFollowsTheModeAndIsNeverReplacedByTheOtherKind(string kind, OverwriteMode mode)
    {
        using var t = new TempDir();
        string dest = t.Sub("out");
        Directory.CreateDirectory(Path.Combine(dest, "report"));
        File.WriteAllText(Path.Combine(dest, "report", "inside.txt"), "kept");
        File.WriteAllText(Path.Combine(dest, "sub"), "a file named sub");

        var r = Extract(kind, t, dest, mode);

        // Nothing that was there changes kind or content.
        Assert.Equal("a file named sub", File.ReadAllText(Path.Combine(dest, "sub")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(dest, "report", "inside.txt")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(dest, "a.txt")));
        switch (mode)
        {
            case OverwriteMode.Rename or OverwriteMode.Overwrite:
                Assert.True((r.Ok, r.Failed) == (3, 0), string.Join("; ", r.Errors));
                Assert.Equal("below sub", File.ReadAllText(Path.Combine(dest, "sub (2)", "f1.txt")));
                Assert.Equal("a file named report", File.ReadAllText(Path.Combine(dest, "report (2)")));
                Assert.True(r.Renamed >= 2);
                break;
            case OverwriteMode.Skip:
                Assert.Equal((1, 0), (r.Ok, r.Failed));
                Assert.True(r.Skipped >= 2);
                Assert.False(Directory.Exists(Path.Combine(dest, "sub (2)")));
                break;
            case OverwriteMode.Fail:
                Assert.Equal(1, r.Ok);
                Assert.True(r.Failed >= 2); // the file below sub and report (and the folder entry sub, where it is one)
                Assert.Contains(r.Errors, e => e.Contains("A file of this name is in the destination where the archive has a folder.", StringComparison.Ordinal));
                break;
        }
    }
}
