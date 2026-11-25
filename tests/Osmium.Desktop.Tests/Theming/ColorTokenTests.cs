using System.Globalization;
using System.Text.RegularExpressions;
using Osmium.Desktop.Theming;

namespace Osmium.Desktop.Tests.Theming;

public partial class ColorTokenTests
{
    public static TheoryData<bool> Themes => new() { false, true };

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryPairTheAppDrawsHasEnoughContrast(bool dark)
    {
        var failures = ColorTokens.Pairs
            .Select(p => (p, ratio: Contrast.Ratio(ColorTokens.Get(p.Foreground, dark), ColorTokens.Get(p.Background, dark))))
            .Where(x => x.ratio < x.p.Minimum)
            .Select(x => $"{x.p.Foreground} on {x.p.Background}: {x.ratio:F2} < {x.p.Minimum} ({x.p.Use})")
            .ToList();
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void PairsCoverTheRolesTheAppDraws()
    {
        bool Has(ColorToken fg, ColorToken bg) => ColorTokens.Pairs.Any(p => p.Foreground == fg && p.Background == bg);
        foreach (var fg in new[] { ColorToken.Text, ColorToken.TextSecondary, ColorToken.Muted })
            foreach (var bg in new[] { ColorToken.Page, ColorToken.Card, ColorToken.Sunk, ColorToken.Code })
                Assert.True(Has(fg, bg), $"{fg} on {bg}");
        foreach (var bg in new[] { ColorToken.Accent, ColorToken.AccentHover, ColorToken.AccentPressed })
            Assert.True(Has(ColorToken.AccentText, bg), $"AccentText on {bg}");
        foreach (var fg in new[] { ColorToken.AccentInk, ColorToken.WarningText, ColorToken.SuccessText, ColorToken.Danger })
            foreach (var bg in new[] { ColorToken.Page, ColorToken.Card, ColorToken.Sunk })
                Assert.True(Has(fg, bg), $"{fg} on {bg}");
        Assert.True(Has(ColorToken.Mark, ColorToken.Sunk) && Has(ColorToken.Mark, ColorToken.Card));
        Assert.True(Has(ColorToken.Focus, ColorToken.Page) && Has(ColorToken.Focus, ColorToken.Card));
        foreach (var dot in new[] { ColorToken.Warning, ColorToken.Success, ColorToken.Danger, ColorToken.Live })
            Assert.True(Has(dot, ColorToken.Card), $"{dot} on Card");
        Assert.All(ColorTokens.Pairs, p => Assert.True(p.Minimum is ColorTokens.Text or ColorTokens.NonText));
    }

    [Theory]
    [InlineData(0xFFFFFF, 0x000000, 21.0)]
    [InlineData(0x000000, 0xFFFFFF, 21.0)]
    [InlineData(0x767676, 0xFFFFFF, 4.54)]
    [InlineData(0x777777, 0x777777, 1.0)]
    [InlineData(0x14151B, 0xEEF0F4, 15.97)]
    [InlineData(0xFFFFFF, 0x3A2A96, 10.76)]
    [InlineData(0x121218, 0xB3A9FF, 8.92)]
    public void RatioFollowsWcag(int foreground, int background, double expected) =>
        Assert.Equal(expected, Contrast.Ratio(foreground, background), 2);

    [Theory]
    [InlineData(0x112233, 0x332211)]
    [InlineData(0xEEF0F4, 0xF4F0EE)]
    [InlineData(0x0000FF, 0xFF0000)]
    [InlineData(0x000000, 0x000000)]
    public void ColorRefIsBlueGreenRed(int rgb, int colorRef) => Assert.Equal(colorRef, ColorTokens.ColorRef(rgb));

    [Fact]
    public void EveryTokenHasBothThemesAndIsListedOnce()
    {
        Assert.Equal(Enum.GetValues<ColorToken>().Order(), ColorTokens.All.Select(t => t.Token).Order());
        Assert.All(ColorTokens.All, t =>
        {
            Assert.InRange(t.Light, 0, 0xFFFFFF);
            Assert.InRange(t.Dark, 0, 0xFFFFFF);
        });
    }

    [Fact]
    public void TokensTakenFromTheSiteKeepItsValuesUnlessAdjusted()
    {
        Assert.All(ColorTokens.All.Where(t => t.SiteVariable is not null), t =>
        {
            if (t.Adjusted is null)
            {
                Assert.True(t.Light == t.SiteLight, $"{t.Token} light {ColorTokens.Hex(t.Light)} is not the site's {ColorTokens.Hex(t.SiteLight)}");
                Assert.True(t.Dark == t.SiteDark, $"{t.Token} dark {ColorTokens.Hex(t.Dark)} is not the site's {ColorTokens.Hex(t.SiteDark)}");
            }
            else Assert.True(t.Light != t.SiteLight || t.Dark != t.SiteDark, $"{t.Token} is marked as adjusted but equals the site's values");
        });
    }

    [Fact]
    public void SiteValuesMatchTheProductPage()
    {
        string template = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "site", "template.html"));
        var light = Variables(Block(template, ":root {"));
        var dark = Variables(Block(template, ":root[data-theme=\"dark\"] {"));
        Assert.All(ColorTokens.All.Where(t => t.SiteVariable is not null), t =>
        {
            Assert.True(light.TryGetValue(t.SiteVariable!, out int l), $"{t.SiteVariable} is not on the product page");
            Assert.True(dark.TryGetValue(t.SiteVariable!, out int d), $"{t.SiteVariable} has no dark value on the product page");
            Assert.Equal(ColorTokens.Hex(l), ColorTokens.Hex(t.SiteLight));
            Assert.Equal(ColorTokens.Hex(d), ColorTokens.Hex(t.SiteDark));
        });
    }

    [GeneratedRegex(@"(--[a-z0-9-]+)\s*:\s*#([0-9a-fA-F]{6})\s*;")]
    private static partial Regex Variable();

    /// <summary>The first CSS block that starts with <paramref name="opening"/> and holds colour variables.</summary>
    private static string Block(string css, string opening)
    {
        for (int at = css.IndexOf(opening, StringComparison.Ordinal); at >= 0; at = css.IndexOf(opening, at + 1, StringComparison.Ordinal))
        {
            int end = css.IndexOf('}', at);
            string block = css[at..end];
            if (block.Contains("--page", StringComparison.Ordinal)) return block;
        }
        throw new InvalidOperationException($"no block {opening} with colours in the template");
    }

    private static Dictionary<string, int> Variables(string block) =>
        Variable().Matches(block).ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    internal static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Osmium.sln"))) return dir.FullName;
        throw new InvalidOperationException("Osmium.sln was not found above " + AppContext.BaseDirectory);
    }
}
