using System.Security.Cryptography;
using Osmium.Core.Container;

namespace Osmium.Core.Tests;

/// <summary>
/// Pins the end of a small archive with spare copies, written with a fixed archive id, salt and creator: where B, H and
/// the index frame are, H's bytes (by SHA-256) and its trailer. Container 1.1 is permanent: a change here is a format
/// change. The compressed index bytes are not pinned (they follow the compressor), only that B repeats them.
/// </summary>
public class TailLayoutGoldenTests
{
    private static readonly byte[] Id = [.. Enumerable.Range(0x10, 16).Select(i => (byte)i)];
    private static readonly byte[] Salt = [.. Enumerable.Range(0x40, 32).Select(i => (byte)i)];

    private static OsaWriterOptions Fixed(string? password) => new()
    {
        Password = password, Creator = "Osmium golden", SpareThreshold = 0, TestFixedArchiveId = Id,
        TestFixedSalt = password is null ? null : Salt, TestFixedSaltAcknowledged = password is not null,
    };

    [Theory]
    [InlineData(false, 55, 6186, 6186, 4096, "4F484452000000003700000000000000A90F00000000000000000000481A4374",
        "3829CE558520D01DCC5413CF51E4D69C934B93291E3E0712A509EF8F0A55729C")]
    [InlineData(true, 109, 6272, 6272, 4096, "4F484452000000006D00000000000000730F00000000000000000000C86CE340",
        "EE711FDE1BBD33F74B1DDD8B2EB455B9B0C539ED0230DA076308E195C5601B09")]
    public void TheTailOfASmallArchiveIsPinned(bool encrypted, long headerLength, long blocksEnd, long spareOffset, long headerCopyLength,
        string trailerHex, string headerCopySha256)
    {
        using var t = new TempDir();
        string? pw = encrypted ? SpareArchives.Password : null;
        string path = SpareArchives.Write(t.Sub("golden.osa"), SpareArchives.Files(2, 3000, seed: 1), Fixed(pw));
        var tail = SpareArchives.TailOf(path, pw);
        var b = File.ReadAllBytes(path);

        Assert.Equal(headerLength, tail.HeaderLength);
        Assert.Equal(blocksEnd, tail.BlocksEnd);
        Assert.Equal(spareOffset, tail.SpareOffset);
        Assert.Equal(headerCopyLength, tail.Trailer!.Value.CopyLength);
        Assert.Equal(tail.SpareOffset + tail.IndexLength, tail.HeaderCopyOffset);
        Assert.Equal(tail.HeaderCopyOffset + headerCopyLength, tail.IndexOffset);
        Assert.Equal(b.Length - 32, tail.IndexOffset + tail.IndexLength);
        Assert.Equal(trailerHex, Convert.ToHexString(b.AsSpan((int)tail.TrailerOffset, 32)));
        Assert.Equal(headerCopySha256, Convert.ToHexString(SHA256.HashData(b.AsSpan((int)tail.HeaderCopyOffset, (int)headerCopyLength))));
        Assert.True(b.AsSpan((int)tail.SpareOffset, (int)tail.IndexLength).SequenceEqual(b.AsSpan((int)tail.IndexOffset, (int)tail.IndexLength)));
    }

    [Fact]
    public void AFixedSaltIsRefusedWithAPasswordUnlessATestSaysItKnows()
    {
        using var t = new TempDir();
        var e = Assert.Throws<InvalidOperationException>(() => new OsaWriter(t.Sub("a.osa"), new OsaWriterOptions { Password = "pw", TestFixedSalt = Salt }));
        Assert.Contains("reuse their nonces", e.Message);
        Assert.Empty(Directory.GetFileSystemEntries(t.Path)); // refused before anything was written
        using (new OsaWriter(t.Sub("b.osa"), new OsaWriterOptions { Password = "pw", TestFixedSalt = Salt, TestFixedSaltAcknowledged = true })) { }
        using (new OsaWriter(t.Sub("c.osa"), new OsaWriterOptions { TestFixedSalt = Salt })) { } // no password: the salt is not used
    }
}
