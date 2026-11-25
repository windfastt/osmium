namespace Osmium.Desktop.Theming;

/// <summary>The app's colours by role. Each one comes from the product page's palette (tools/site/template.html) or is
/// derived from it.</summary>
public enum ColorToken
{
    /// <summary>The window and pages behind the cards (site <c>--page</c>).</summary>
    Page,
    /// <summary>Cards, lists, inputs and menus (site <c>--surface</c>).</summary>
    Card,
    /// <summary>Meter tracks, a pressed ghost button, the selected segment (site <c>--sunk</c>).</summary>
    Sunk,
    /// <summary>Behind monospaced text: pipelines, job logs, the rules editor (site <c>--code</c>).</summary>
    Code,
    Text,
    /// <summary>Ledes, labels, column headers (site <c>--ink-2</c>).</summary>
    TextSecondary,
    /// <summary>Spec lines, captions, hints, disabled text.</summary>
    Muted,
    /// <summary>Card borders, dividers, the ghost button's border.</summary>
    Line,
    /// <summary>Row separators, the rail divider, the status line's rule.</summary>
    LineSoft,
    /// <summary>The ghost button's border under the pointer (site <c>--axis</c>).</summary>
    LineStrong,
    /// <summary>The primary button's face (site <c>--btn</c>).</summary>
    Accent,
    /// <summary>The primary button's label (site <c>--btn-ink</c>).</summary>
    AccentText,
    AccentHover,
    AccentPressed,
    /// <summary>Links, accent text and Accent tags (site <c>--accent</c>).</summary>
    AccentInk,
    /// <summary>Keyboard focus rings.</summary>
    Focus,
    /// <summary>Data: meter fills, ratio bars, the rail's selection pill (site <c>--mark</c>).</summary>
    Mark,
    /// <summary>Secondary bars, on cards only (site <c>--mark-rest</c>).</summary>
    MarkRest,
    /// <summary>The "running" dot; never text on light surfaces.</summary>
    Live,
    /// <summary>Paused meters, warning dots and tags on the page or a card (site <c>--exp</c>).</summary>
    Warning,
    /// <summary>Warning text on any surface.</summary>
    WarningText,
    /// <summary>Done meters, check marks (site <c>--ok</c>).</summary>
    Success,
    /// <summary>Success text on any surface.</summary>
    SuccessText,
    /// <summary>Errors, failed meters.</summary>
    Danger,
    /// <summary>Hover fill in the rail, menus and owner-drawn lists.</summary>
    Subtle,
}

/// <summary>
/// One colour role in both themes (24-bit RGB, 0xRRGGBB). <paramref name="SiteVariable"/> names the product page's CSS
/// variable it is taken from, with that variable's values; null for a colour only the app has. When the app's value
/// differs from the site's, <paramref name="Adjusted"/> says why.
/// </summary>
public sealed record TokenSpec(ColorToken Token, int Light, int Dark, string? SiteVariable, int SiteLight, int SiteDark, string? Adjusted = null)
{
    public int Value(bool dark) => dark ? Dark : Light;
}

/// <summary>A text or mark colour drawn on a background, and the contrast it needs (WCAG 2.2: 4.5 for text, 3 for
/// marks that carry meaning).</summary>
public sealed record ContrastPair(ColorToken Foreground, ColorToken Background, double Minimum, string Use);

/// <summary>The palette of the light and dark themes, and the colour pairs the app draws.</summary>
public static class ColorTokens
{
    public const double Text = 4.5, NonText = 3.0;

    /// <summary>Every token. Values are the product page's, except where <see cref="TokenSpec.Adjusted"/> says why.</summary>
    public static IReadOnlyList<TokenSpec> All { get; } =
    [
        new(ColorToken.Page, 0xEEF0F4, 0x0E0F13, "--page", 0xEEF0F4, 0x0E0F13),
        new(ColorToken.Card, 0xFFFFFF, 0x17181E, "--surface", 0xFFFFFF, 0x17181E),
        new(ColorToken.Sunk, 0xE4E7ED, 0x121318, "--sunk", 0xE4E7ED, 0x121318),
        new(ColorToken.Code, 0xF6F7F9, 0x121318, "--code", 0xF6F7F9, 0x121318),
        new(ColorToken.Text, 0x14151B, 0xECEEF4, "--ink", 0x14151B, 0xECEEF4),
        new(ColorToken.TextSecondary, 0x3D404D, 0xC3C6D1, "--ink-2", 0x3D404D, 0xC3C6D1),
        new(ColorToken.Muted, 0x5F6372, 0x9296A4, "--muted", 0x5F6372, 0x9296A4),
        new(ColorToken.Line, 0xD5D9E2, 0x2A2C35, "--line", 0xD5D9E2, 0x2A2C35),
        new(ColorToken.LineSoft, 0xE2E5EB, 0x202229, "--line-soft", 0xE2E5EB, 0x202229),
        new(ColorToken.LineStrong, 0xB9BFCB, 0x3D404B, "--axis", 0xB9BFCB, 0x3D404B),
        new(ColorToken.Accent, 0x3A2A96, 0xB3A9FF, "--btn", 0x3A2A96, 0xB3A9FF),
        new(ColorToken.AccentText, 0xFFFFFF, 0x121218, "--btn-ink", 0xFFFFFF, 0x121218),
        new(ColorToken.AccentHover, 0x2C1F7A, 0xCBC4FF, "--btn-hover", 0x2C1F7A, 0xCBC4FF),
        // The site has no pressed state: one more step from the face in the hover's direction.
        new(ColorToken.AccentPressed, 0x221766, 0xDAD5FF, null, 0, 0),
        new(ColorToken.AccentInk, 0x3A2A96, 0xB3A9FF, "--accent", 0x3A2A96, 0xB3A9FF),
        // The site's focus outline is the accent colour.
        new(ColorToken.Focus, 0x3A2A96, 0xB3A9FF, "--accent", 0x3A2A96, 0xB3A9FF),
        new(ColorToken.Mark, 0x5B4BD5, 0x9085E9, "--mark", 0x5B4BD5, 0x9085E9),
        new(ColorToken.MarkRest, 0x8E93A3, 0x666B7A, "--mark-rest", 0x8E93A3, 0x666B7A),
        new(ColorToken.Live, 0x1592C8, 0x3BB8EE, "--live", 0x1592C8, 0x3BB8EE),
        new(ColorToken.Warning, 0xC2410C, 0xF0702A, "--exp", 0xC2410C, 0xF0702A),
        new(ColorToken.WarningText, 0xB43C00, 0xF0702A, "--exp", 0xC2410C, 0xF0702A,
            "light: the site's #C2410C reaches only 4.18:1 on the sunk colour; #B43C00 reaches 4.73:1"),
        new(ColorToken.Success, 0x0F7A3D, 0x3CCF7A, "--ok", 0x0F7A3D, 0x3CCF7A),
        new(ColorToken.SuccessText, 0x0D6E37, 0x3CCF7A, "--ok", 0x0F7A3D, 0x3CCF7A,
            "light: the site's #0F7A3D reaches only 4.38:1 on the sunk colour; #0D6E37 reaches 5.13:1"),
        // Errors are the app's own; the site shows none.
        new(ColorToken.Danger, 0xC42B1C, 0xFF6363, null, 0, 0),
        // Hover fills: light is the sunk colour, dark is one step above the card.
        new(ColorToken.Subtle, 0xE4E7ED, 0x1C1D24, null, 0, 0),
    ];

    private static readonly Dictionary<ColorToken, TokenSpec> ByToken = All.ToDictionary(t => t.Token);

    /// <summary>A token's 0xRRGGBB value in the light or dark theme.</summary>
    public static int Get(ColorToken token, bool dark) => ByToken[token].Value(dark);

    public static TokenSpec Spec(ColorToken token) => ByToken[token];

    /// <summary>Every colour pair the app draws, with the contrast it needs.</summary>
    public static IReadOnlyList<ContrastPair> Pairs { get; } = BuildPairs();

    private static List<ContrastPair> BuildPairs()
    {
        var pairs = new List<ContrastPair>();
        void Add(ColorToken fg, ColorToken[] backgrounds, double minimum, string use)
        {
            foreach (var bg in backgrounds) pairs.Add(new ContrastPair(fg, bg, minimum, use));
        }
        ColorToken[] surfaces = [ColorToken.Page, ColorToken.Card, ColorToken.Sunk, ColorToken.Code];
        Add(ColorToken.Text, [.. surfaces, ColorToken.Subtle], Text, "text");
        Add(ColorToken.TextSecondary, [.. surfaces, ColorToken.Subtle], Text, "ledes, labels, column headers, rail items");
        Add(ColorToken.Muted, [.. surfaces, ColorToken.Subtle], Text, "spec lines, captions, hints, disabled labels");
        Add(ColorToken.AccentText, [ColorToken.Accent, ColorToken.AccentHover, ColorToken.AccentPressed], Text, "primary button label");
        Add(ColorToken.AccentInk, surfaces, Text, "links, accent text, Accent tags");
        Add(ColorToken.WarningText, surfaces, Text, "warning text and tags");
        Add(ColorToken.SuccessText, surfaces, Text, "success text and tags");
        Add(ColorToken.Danger, surfaces, Text, "error text and tags");
        Add(ColorToken.Mark, [ColorToken.Sunk, ColorToken.Card, ColorToken.Page, ColorToken.Subtle], NonText, "meter fill, ratio bars, rail selection pill");
        Add(ColorToken.MarkRest, [ColorToken.Card], NonText, "secondary bars");
        Add(ColorToken.Focus, [ColorToken.Page, ColorToken.Card], NonText, "focus ring against the gap and the background around it");
        Add(ColorToken.Accent, [ColorToken.Page, ColorToken.Card], NonText, "primary button face against the focus ring's gap");
        Add(ColorToken.Warning, [ColorToken.Card, ColorToken.Page, ColorToken.Sunk], NonText, "paused meter, warning dots, tag and banner borders");
        Add(ColorToken.Success, [ColorToken.Card, ColorToken.Page, ColorToken.Sunk], NonText, "done meter, check marks");
        Add(ColorToken.Danger, [ColorToken.Card, ColorToken.Page, ColorToken.Sunk], NonText, "failed meter, error dots");
        Add(ColorToken.Live, [ColorToken.Card, ColorToken.Page], NonText, "running dot");
        return pairs;
    }

    /// <summary>A 0xRRGGBB colour as a Windows COLORREF (0x00BBGGRR).</summary>
    public static int ColorRef(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    /// <summary>"#RRGGBB" for a 0xRRGGBB colour.</summary>
    public static string Hex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Contrast between two colours as WCAG 2.2 defines it.</summary>
public static class Contrast
{
    /// <summary>The contrast ratio of two 0xRRGGBB colours, from 1 (equal) to 21 (black on white).</summary>
    public static double Ratio(int foreground, int background)
    {
        double a = Luminance(foreground), b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Relative luminance of a 0xRRGGBB colour (0 for black, 1 for white).</summary>
    public static double Luminance(int rgb)
    {
        static double Channel(int c)
        {
            double s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel((rgb >> 16) & 0xFF) + 0.7152 * Channel((rgb >> 8) & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
    }
}
