using System.Text.Json;
using Osmium.Desktop.Theming;

namespace Osmium.Desktop.Tests.Theming;

public class TypeScaleTests
{
    [Fact]
    public void EveryRoleHasOneSpecWithAFaceAndAFallback()
    {
        Assert.Equal(Enum.GetValues<TypeRole>().Order(), TypeScale.All.Select(s => s.Role).Order());
        Assert.All(TypeScale.All, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Face));
            Assert.NotEmpty(s.Fallbacks);
            Assert.DoesNotContain(s.Face, s.Fallbacks);
            Assert.All(s.Fallbacks, f => Assert.DoesNotContain(f, TypeScale.BundledFiles.Select(b => b.Family)));
        });
    }

    [Fact]
    public void TextIsAtLeastEightPoints()
    {
        Assert.All(TypeScale.All, s => Assert.True(s.Size >= 8f, $"{s.Role}: {s.Size} pt"));
        Assert.True(TypeScale.Get(TypeRole.Title).Size > TypeScale.Get(TypeRole.CardHeading).Size);
        Assert.True(TypeScale.Get(TypeRole.CardHeading).Size > TypeScale.Get(TypeRole.Body).Size);
        Assert.True(TypeScale.Get(TypeRole.Lede).Size >= TypeScale.Get(TypeRole.Body).Size);
    }

    [Fact]
    public void BundledFacesAreOnlyForOsmiumsOwnWords()
    {
        var bundled = TypeScale.All.Where(s => s.Bundled).Select(s => s.Role).ToHashSet();
        Assert.Subset(TypeScale.BrandRoles.ToHashSet(), bundled);
        foreach (var role in new[] { TypeRole.Body, TypeRole.BodyStrong, TypeRole.Lede, TypeRole.Caption, TypeRole.Mono, TypeRole.NumberLive })
            Assert.DoesNotContain(role, bundled);
        Assert.All(TypeScale.All, s => Assert.Equal(s.Bundled, TypeScale.BundledFiles.Any(b => b.Family == s.Face)));
    }

    [Fact]
    public void SpecLinesAndTagsAreCapitals()
    {
        Assert.Equal("ARCHIVE · .OSA 1.1", TypeScale.Cased(TypeRole.Spec, "Archive · .osa 1.1"));
        Assert.Equal("Compress", TypeScale.Cased(TypeRole.Title, "Compress"));
        Assert.True(TypeScale.Get(TypeRole.Tag).Uppercase);
    }

    [Fact]
    public void BundledFacesMatchTheFontBuild()
    {
        string json = File.ReadAllText(Path.Combine(ColorTokenTests.RepositoryRoot(), "tools", "fonts", "fonts.json"));
        var instances = JsonDocument.Parse(json).RootElement.GetProperty("instances").EnumerateArray()
            .ToDictionary(i => i.GetProperty("family").GetString()!);
        Assert.Equal(TypeScale.BundledFiles.Select(b => b.Family).Order(), instances.Keys.Order());
        foreach (var (file, family) in TypeScale.BundledFiles)
            Assert.Equal(file, instances[family].GetProperty("file").GetString());
        Assert.All(TypeScale.All.Where(s => s.Bundled), s =>
        {
            var axes = instances[s.Face].GetProperty("axes");
            Assert.Equal(s.Weight, axes.GetProperty("wght").GetDouble());
            Assert.Equal(s.Width, axes.GetProperty("wdth").GetDouble());
        });
    }
}
