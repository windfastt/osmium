using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Osmium.Core.Archives;
using Osmium.Core.Archives.Foreign.Compression;

namespace Osmium.Core.Tests.Foreign;

/// <summary>The committed fixtures: each one reads cleanly, and none carries a user name, a host name, an owner, a
/// comment or a path other than the generated content's.</summary>
public partial class FixtureTests
{
    internal static string? PasswordFor(string fixture) =>
        fixture.Contains("aes", StringComparison.Ordinal) && fixture.EndsWith(".7z", StringComparison.Ordinal) ? "Pässwörd ü"
        : fixture.EndsWith("zipcrypto-nonascii.zip", StringComparison.Ordinal) ? "Pässwörd ü"
        : fixture.Contains("aes", StringComparison.Ordinal) || fixture.Contains("zipcrypto", StringComparison.Ordinal) ? "secret"
        : null;

    public static TheoryData<string> Fixtures() => [.. ForeignData.AllFixtures()];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryFixtureReadsCleanly(string fixture)
    {
        using var s = ArchiveSources.Open(ForeignData.Fixture(fixture.Split('/')), new ArchiveOpenOptions { Password = () => PasswordFor(fixture) },
            TestContext.Current.CancellationToken);
        var r = s.Test(null, null, TestContext.Current.CancellationToken);
        Assert.True(r.FilesOk > 0, fixture);
        Assert.Equal(0, r.FilesFailed);
        Assert.Equal(0, r.LeftOut);
        Assert.Empty(r.Errors);
    }

    [GeneratedRegex(@"^(in(/(empty\.txt|hello\.txt|sub(/(empty|numbers\.txt))?))?|code\.bin|numbers\.txt|hello\.txt)$")]
    private static partial Regex AllowedName();

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void FixturesHoldNoPersonalData(string fixture)
    {
        string path = ForeignData.Fixture(fixture.Split('/'));
        using (var s = ArchiveSources.Open(path, new ArchiveOpenOptions { Password = () => PasswordFor(fixture) }, TestContext.Current.CancellationToken))
        {
            foreach (var item in s.Items)
                if (!fixture.StartsWith("xz/", StringComparison.Ordinal) || fixture.EndsWith(".tar.xz", StringComparison.Ordinal))
                    Assert.Matches(AllowedName(), item.Path);
            Assert.DoesNotContain(s.Details, d => d.Label == "Comment");
        }
        if (fixture.EndsWith(".zip", StringComparison.Ordinal)) CheckZipHeaders(File.ReadAllBytes(path), fixture);
        if (fixture.EndsWith(".tar.xz", StringComparison.Ordinal)) CheckTarHeaders(path, fixture);
    }

    /// <summary>Every central-directory record: no comment, and only extra fields that hold times, sizes, AES
    /// parameters or a Unicode name; owner ids, where present, are 0.</summary>
    private static void CheckZipHeaders(byte[] zip, string fixture)
    {
        int eocd = zip.Length - 22;
        while (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd)) != 0x06054b50) eocd--;
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(eocd + 20)));
        int p = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd + 16));
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(eocd + 10));
        for (int i = 0; i < entries; i++)
        {
            Assert.Equal(0x02014b50u, BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(p)));
            int n = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(p + 28)), x = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(p + 30)), c = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(p + 32));
            Assert.Equal(0, c);
            var extra = zip.AsSpan(p + 46 + n, x);
            for (int q = 0; q + 4 <= extra.Length;)
            {
                int id = BinaryPrimitives.ReadUInt16LittleEndian(extra[q..]), size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(q + 2)..]);
                var data = extra.Slice(q + 4, size);
                switch (id)
                {
                    case 0x000A or 0x5455 or 0x9901 or 0x0001 or 0x7075: break; // times, AES, sizes, Unicode name
                    case 0x5855: // old Unix extra: access and modification times, then (optionally) owner ids
                        if (data.Length >= 12) Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data[8..]));
                        break;
                    case 0x7875: // Unix owner ids: only root's (0), from the generating machine
                        Assert.All(data[1..].ToArray(), b => Assert.True(b is 0 or 4, $"{fixture}: owner id {b}"));
                        break;
                    default:
                        Assert.Fail($"{fixture}: unexpected extra field 0x{id:X4}");
                        break;
                }
                q += 4 + size;
            }
            p += 46 + n + x + c;
        }
    }

    /// <summary>Every TAR header: owner and group names empty, ids 0, names from the generated content.</summary>
    private static void CheckTarHeaders(string path, string fixture)
    {
        using var fs = File.OpenRead(path);
        using var xz = new XzDecoder(fs, ImportLimits.Default, fixture);
        var tar = new MemoryStream();
        xz.CopyTo(tar);
        var b = tar.ToArray();
        for (int at = 0; at + 512 <= b.Length; at += 512)
        {
            var h = b.AsSpan(at, 512).ToArray();
            if (h.AsSpan().IndexOfAnyExcept((byte)0) < 0) break;
            string Field(int o, int l) => Encoding.ASCII.GetString(h, o, l).TrimEnd('\0', ' ');
            Assert.Equal("", Field(265, 32));
            Assert.Equal("", Field(297, 32));
            Assert.Equal(0, Convert.ToInt64(Field(108, 8).Trim() is { Length: > 0 } u ? u : "0", 8));
            Assert.Equal(0, Convert.ToInt64(Field(116, 8).Trim() is { Length: > 0 } g ? g : "0", 8));
            Assert.Matches(AllowedName(), Field(0, 100).TrimEnd('/'));
            long size = Convert.ToInt64(Field(124, 12).Trim() is { Length: > 0 } s ? s : "0", 8);
            at += (int)((size + 511) / 512 * 512);
        }
    }
}
