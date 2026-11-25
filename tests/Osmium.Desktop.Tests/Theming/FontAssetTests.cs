using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Osmium.Desktop.Tests.Theming;

/// <summary>The brand fonts the app ships: as built by tools/fonts, each with its licence and copyright notice.</summary>
public class FontAssetTests
{
    private static readonly string Root = ColorTokenTests.RepositoryRoot();
    private static readonly string FontDir = Path.Combine(Root, "src", "Osmium.App", "Fonts");

    private static JsonElement Spec() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tools", "fonts", "fonts.json"))).RootElement;

    private static IEnumerable<JsonElement> Instances() => Spec().GetProperty("instances").EnumerateArray();

    [Fact]
    public void EachFontIsTheOneTheToolBuilt()
    {
        foreach (var inst in Instances())
        {
            string path = Path.Combine(FontDir, inst.GetProperty("file").GetString()!);
            Assert.True(File.Exists(path), path);
            string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(inst.GetProperty("sha256").GetString(), hash);
        }
    }

    [Fact]
    public void TheFolderHoldsOnlyTheRecordedFontsAndLicences()
    {
        var expected = Instances().Select(i => i.GetProperty("file").GetString()!)
            .Concat(Spec().GetProperty("families").EnumerateArray().Select(f => f.GetProperty("licenseFile").GetString()!))
            .Order(StringComparer.Ordinal);
        var actual = Directory.GetFiles(FontDir).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EachFontHasItsLicenceAndCopyrightNotice()
    {
        var families = Spec().GetProperty("families").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!);
        foreach (var inst in Instances())
        {
            var family = families[inst.GetProperty("source").GetString()!];
            string license = Path.Combine(FontDir, family.GetProperty("licenseFile").GetString()!);
            Assert.True(File.Exists(license), license);
            string text = File.ReadAllText(license);
            Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", text, StringComparison.Ordinal);
            // No Reserved Font Names: the notices above the licence would name them.
            string notices = text[..text.IndexOf("This Font Software is licensed", StringComparison.Ordinal)];
            Assert.DoesNotContain("Reserved Font Name", notices, StringComparison.Ordinal);
            var names = Names(File.ReadAllBytes(Path.Combine(FontDir, inst.GetProperty("file").GetString()!)));
            string firstLine = text.Split('\n')[0].Trim();
            Assert.StartsWith("Copyright ", firstLine, StringComparison.Ordinal);
            Assert.Equal(firstLine, names[0]);
            Assert.Contains("SIL Open Font License, Version 1.1", names[13], StringComparison.Ordinal);
            Assert.Contains("scripts.sil.org/OFL", names[14], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FamilyNamesAreUniqueAndAsRecorded()
    {
        var families = Instances().Select(i =>
        {
            var names = Names(File.ReadAllBytes(Path.Combine(FontDir, i.GetProperty("file").GetString()!)));
            Assert.Equal(i.GetProperty("family").GetString(), names[1]);
            Assert.Equal("Regular", names[2]);
            return names[1];
        }).ToList();
        Assert.Equal(families.Count, families.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.InRange(families.Count, 1, 4);
    }

    [Fact]
    public void TheFontsTogetherStayUnder600KB()
    {
        long total = Instances().Sum(i => new FileInfo(Path.Combine(FontDir, i.GetProperty("file").GetString()!)).Length);
        Assert.InRange(total, 1, 600 * 1024);
    }

    /// <summary>The Windows (platform 3) records of a TrueType font's name table, by name ID.</summary>
    private static Dictionary<int, string> Names(byte[] font)
    {
        int tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int t = 0; t < tables; t++)
        {
            var record = font.AsSpan(12 + 16 * t, 16);
            if (Encoding.ASCII.GetString(record[..4]) != "name") continue;
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var name = font.AsSpan(offset);
            int count = BinaryPrimitives.ReadUInt16BigEndian(name[2..]), strings = BinaryPrimitives.ReadUInt16BigEndian(name[4..]);
            var result = new Dictionary<int, string>();
            for (int i = 0; i < count; i++)
            {
                var r = name.Slice(6 + 12 * i, 12);
                int platform = BinaryPrimitives.ReadUInt16BigEndian(r), id = BinaryPrimitives.ReadUInt16BigEndian(r[6..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(r[8..]), at = BinaryPrimitives.ReadUInt16BigEndian(r[10..]);
                if (platform == 3) result[id] = Encoding.BigEndianUnicode.GetString(name.Slice(strings + at, length));
            }
            return result;
        }
        throw new InvalidDataException("no name table");
    }
}
