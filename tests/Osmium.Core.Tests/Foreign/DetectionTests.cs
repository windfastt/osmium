using System.IO.Hashing;
using Osmium.Core.Archives;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Foreign;

/// <summary>Archives are recognised by their content; the name only breaks ties.</summary>
public class DetectionTests
{
    private static readonly (string, byte[])[] Tree = [("a/one.txt", ForeignData.Text(3000)), ("a/two.txt", ForeignData.Text(500, 2)), ("a/", [])];

    public static TheoryData<string, ArchiveKind> Generated() => new()
    {
        { "zip", ArchiveKind.Zip },
        { "empty-zip", ArchiveKind.Zip },
        { "tar", ArchiveKind.Tar },
        { "tar.gz", ArchiveKind.Tar },
        { "tar.bz2", ArchiveKind.Tar },
        { "tar.zst", ArchiveKind.Tar },
        { "gz", ArchiveKind.GZip },
        { "bz2", ArchiveKind.BZip2 },
        { "zst", ArchiveKind.Zstd },
    };

    private static byte[] Make(string kind) => kind switch
    {
        "zip" => ForeignData.Zip(Tree),
        "empty-zip" => ForeignData.Zip([]),
        "tar" => ForeignData.Tar(Tree),
        "tar.gz" => ForeignData.GZip(ForeignData.Tar(Tree)),
        "tar.bz2" => ForeignData.BZip2(ForeignData.Tar(Tree)),
        "tar.zst" => ForeignData.Zstd(ForeignData.Tar(Tree)),
        "gz" => ForeignData.GZip(ForeignData.Text(5000)),
        "bz2" => ForeignData.BZip2(ForeignData.Text(5000)),
        "zst" => ForeignData.Zstd(ForeignData.Text(5000)),
        _ => throw new ArgumentException(kind),
    };

    [Theory]
    [MemberData(nameof(Generated))]
    public void RecognisesGeneratedArchivesByContentUnderAnyName(string kind, ArchiveKind expected)
    {
        using var t = new TempDir();
        var data = Make(kind);
        foreach (var name in new[] { "archive." + kind.Replace("empty-", ""), "renamed.zip", "renamed.dat", "no-extension" })
        {
            string path = ForeignData.Write(t, name, data);
            Assert.Equal(expected, ArchiveSources.Detect(path));
        }
    }

    [Theory]
    [InlineData("xz/crc64.xz", ArchiveKind.Xz)]
    [InlineData("xz/concat.xz", ArchiveKind.Xz)]
    [InlineData("xz/tree.tar.xz", ArchiveKind.Tar)]
    [InlineData("7z/lzma2-solid.7z", ArchiveKind.SevenZip)]
    [InlineData("7z/aes-headers.7z", ArchiveKind.SevenZip)]
    [InlineData("zip/aes256.zip", ArchiveKind.Zip)]
    [InlineData("zip/deflate64.zip", ArchiveKind.Zip)]
    public void RecognisesFixturesByContentUnderAWrongName(string fixture, ArchiveKind expected)
    {
        using var t = new TempDir();
        Assert.Equal(expected, ArchiveSources.Detect(ForeignData.Fixture(fixture.Split('/'))));
        string renamed = t.Sub("renamed.zip");
        File.Copy(ForeignData.Fixture(fixture.Split('/')), renamed);
        Assert.Equal(expected, ArchiveSources.Detect(renamed));
    }

    [Fact]
    public void RandomDataAndEmptyFilesAreNoArchive()
    {
        using var t = new TempDir();
        var rnd = new Random(5);
        foreach (int size in new[] { 0, 1, 31, 32, 600, 5000, 70000 })
        {
            var data = new byte[size];
            rnd.NextBytes(data);
            if (size > 0 && data[0] is 0x1F or 0x50 or 0x37 or 0x42 or 0xFD or 0x28 or 0x52 or 0x4F) data[0] = 0x11;
            Assert.Null(ArchiveSources.Detect(ForeignData.Write(t, $"random{size}.bin", data)));
        }
    }

    [Fact]
    public void AnOldTarWithoutTheUstarMarkNeedsTheTarName()
    {
        using var t = new TempDir();
        var tar = ForeignData.Tar(Tree);
        // Make the first header an old-style one: no magic, checksum fixed up.
        var header = tar.AsSpan(0, 512);
        header.Slice(257, 8).Clear();
        header.Slice(148, 8).Fill((byte)' ');
        long sum = 0;
        foreach (byte b in header) sum += b;
        System.Text.Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header[148..]);
        Assert.Equal(ArchiveKind.Tar, ArchiveSources.Detect(ForeignData.Write(t, "old.tar", tar)));
        Assert.Null(ArchiveSources.Detect(ForeignData.Write(t, "old.bin", tar)));
    }

    [Fact]
    public void RarAndSpannedZipAreRefusedWithAReason()
    {
        using var t = new TempDir();
        var rar4 = new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0xCF, 0x90, 0x73, 0, 0 };
        var rar5 = new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00, 0x33, 0x92, 0xB5, 0xE5 };
        var spanned = new byte[] { 0x50, 0x4B, 0x07, 0x08, 0x50, 0x4B, 0x03, 0x04, 0, 0 };
        foreach (var (name, data) in new[] { ("a.rar", rar4), ("b.rar", rar5), ("renamed.zip", rar5) })
        {
            var e = Assert.Throws<NotSupportedException>(() => ArchiveSources.Detect(ForeignData.Write(t, name, data)));
            Assert.Equal("RAR archives are not supported by this version of Osmium.", e.Message);
            Assert.Throws<NotSupportedException>(() => ArchiveSources.Open(t.Sub(name), ct: TestContext.Current.CancellationToken));
        }
        var z = Assert.Throws<NotSupportedException>(() => ArchiveSources.Detect(ForeignData.Write(t, "x.z01", spanned)));
        Assert.Equal("Multi-volume ZIP archives are not supported.", z.Message);
    }

    [Fact]
    public void AnOsaWithADamagedHeaderButAValidFooterIsStillAnOsa()
    {
        using var t = new TempDir();
        string path = t.Sub("x.bin");
        var data = "content"u8.ToArray();
        using (var w = new OsaWriter(path))
        {
            var (desc, payload) = PipelineExecutor.Encode(PipelineSpec.Parse("store", MethodRegistry.Default), data, CodecContext.None);
            int frame = w.WriteBlock(desc, BlockBody.Hash128(data), payload, data.Length);
            w.Finish([new ArchiveEntry { Path = "a.txt", Kind = EntryKind.File, Size = data.Length, Segments = [new Segment(frame, 0, data.Length)], Hash = XxHash128.HashToUInt128(data) }]);
            w.Commit(overwrite: false);
        }
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0xFF; // magic gone
        File.WriteAllBytes(path, bytes);
        Assert.Equal(ArchiveKind.Osa, ArchiveSources.Detect(path));
        // With the footer gone too, nothing says it is one.
        File.WriteAllBytes(path, bytes[..^32]);
        Assert.Null(ArchiveSources.Detect(path));
    }

    [Fact]
    public void DetectionDoesNotAllocateADeclaredDictionary()
    {
        using var t = new TempDir();
        // An xz file whose block header declares a 3 GiB LZMA2 dictionary (property 38).
        var xz = File.ReadAllBytes(ForeignData.Fixture("xz", "crc64.xz"));
        int headerSize = (xz[12] + 1) * 4;
        int propAt = Array.IndexOf(xz, (byte)0x21, 13) + 2; // filter id 0x21, props size 1, props
        xz[propAt] = 38;
        uint crc = Crc32.HashToUInt32(xz.AsSpan(12, headerSize - 4));
        BitConverter.GetBytes(crc).CopyTo(xz, 12 + headerSize - 4);
        string path = ForeignData.Write(t, "big.xz", xz);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var kind = ArchiveSources.Detect(path);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(ArchiveKind.Xz, kind);
        Assert.True(allocated < 64 << 20, $"detection allocated {allocated:N0} bytes");
    }

    [Theory]
    [InlineData("photos.tar.gz", "photos")]
    [InlineData("photos.TAR.GZ", "photos")]
    [InlineData("x.tgz", "x")]
    [InlineData("x.tar.bz2", "x")]
    [InlineData("x.tbz2", "x")]
    [InlineData("x.tar.xz", "x")]
    [InlineData("x.txz", "x")]
    [InlineData("x.tar.zst", "x")]
    [InlineData("x.tzst", "x")]
    [InlineData("notes.txt.gz", "notes.txt")]
    [InlineData("a.zip", "a")]
    [InlineData("a.7z", "a")]
    [InlineData("a.tar", "a")]
    [InlineData(".tar.gz", ".tar")]
    [InlineData("CON.zip", "_CON.zip files")]
    [InlineData("aux.tar.gz", "_aux.tar.gz files")]
    [InlineData("dots..zip", "dots..zip files")]
    public void StemOfStripsTheArchiveExtensions(string name, string stem)
    {
        Assert.Equal(stem, ArchiveSources.StemOf(Path.Combine("dir", name)));
        Assert.Null(PathSafety.Validate(ArchiveSources.StemOf(name)));
    }

    [Fact]
    public void ExtensionsCoverEveryFormat()
    {
        Assert.Equal(
            [".osa", ".zip", ".7z", ".tar", ".tgz", ".tar.gz", ".gz", ".tbz", ".tbz2", ".tar.bz2", ".bz2", ".txz", ".tar.xz", ".xz", ".tzst", ".tar.zst", ".zst"],
            ArchiveSources.Extensions);
        Assert.All(ArchiveSources.Extensions, e => Assert.Equal(e.ToLowerInvariant(), e));
    }
}
