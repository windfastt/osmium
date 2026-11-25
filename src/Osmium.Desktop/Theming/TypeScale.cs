namespace Osmium.Desktop.Theming;

/// <summary>What a piece of text is, which sets its typeface, size and colour.</summary>
public enum TypeRole
{
    /// <summary>A page's title (site <c>h2</c>).</summary>
    Title,
    /// <summary>Card and dialog headings (site <c>h3</c>).</summary>
    CardHeading,
    /// <summary>The "Osmium" wordmark (site <c>.brand</c>).</summary>
    Brand,
    /// <summary>The line of facts above a title (site <c>.spec</c>).</summary>
    Spec,
    /// <summary>State labels: EXPERIMENTAL, CAUTION, ENCRYPTED (site <c>.tag</c>).</summary>
    Tag,
    /// <summary>Big values that do not change while shown (site <c>.st .v</c>).</summary>
    Number,
    /// <summary>Big values that change while shown: digits of equal width.</summary>
    NumberLive,
    /// <summary>The sentence below a title (site <c>.lede</c>).</summary>
    Lede,
    Body,
    BodyStrong,
    /// <summary>The status line, hints, small notes.</summary>
    Caption,
    /// <summary>Pipelines, logs, JSON.</summary>
    Mono,
    /// <summary>Icons of the navigation rail.</summary>
    Glyph,
    /// <summary>Icons of empty states.</summary>
    GlyphLarge,
}

/// <summary>Which of the colour tokens a role's text uses by default.</summary>
public enum TypeColor { Text, TextSecondary, Muted }

/// <summary>
/// A role's typeface and size. <paramref name="Face"/> is the family Windows knows it by: a bundled font
/// (<paramref name="Bundled"/>, built by tools/fonts from the product page's typefaces at <paramref name="Weight"/> and
/// <paramref name="Width"/>) or a system font. When it is missing, <paramref name="Fallbacks"/> are tried in order.
/// <paramref name="Site"/> is the product page's setting it follows.
/// </summary>
public sealed record TypeSpec(TypeRole Role, string Face, float Size, int Weight, float Width, bool Bundled, bool Uppercase,
    TypeColor Color, IReadOnlyList<string> Fallbacks, string Site);

/// <summary>The app's type scale: one place for every role's typeface, size and casing.</summary>
public static class TypeScale
{
    public const string SegoeText = "Segoe UI Variable Text", SegoeTextSemibold = "Segoe UI Variable Text Semibold",
        SegoeDisplaySemibold = "Segoe UI Variable Display Semibold", SegoeSemibold = "Segoe UI Semibold", Segoe = "Segoe UI",
        Cascadia = "Cascadia Mono", Consolas = "Consolas", FluentIcons = "Segoe Fluent Icons", Mdl2 = "Segoe MDL2 Assets";

    /// <summary>The bundled faces (tools/fonts/fonts.json), by family name.</summary>
    public const string AnybodyTitle = "Anybody Title", AnybodyHeading = "Anybody Heading", MartianMonoSpec = "Martian Mono Spec",
        InstrumentSansNumber = "Instrument Sans Number";

    /// <summary>The bundled font files (in src/Osmium.App/Fonts) and the family each holds.</summary>
    public static IReadOnlyList<(string File, string Family)> BundledFiles { get; } =
    [
        ("AnybodyTitle.ttf", AnybodyTitle), ("AnybodyHeading.ttf", AnybodyHeading),
        ("MartianMonoSpec.ttf", MartianMonoSpec), ("InstrumentSansNumber.ttf", InstrumentSansNumber),
    ];

    private static readonly string[] DisplayFallbacks = [SegoeDisplaySemibold, SegoeSemibold, Segoe];
    private static readonly string[] TextFallbacks = [Segoe];
    private static readonly string[] MonoFallbacks = [Cascadia, Consolas];

    public static IReadOnlyList<TypeSpec> All { get; } =
    [
        // Site h2: Anybody 760, width 70 %. The app's titles are smaller, so a little wider and lighter reads better.
        new(TypeRole.Title, AnybodyTitle, 20f, 740, 75f, true, false, TypeColor.Text, DisplayFallbacks, "h2: Anybody 760, 70 %"),
        new(TypeRole.CardHeading, AnybodyHeading, 12f, 680, 86f, true, false, TypeColor.Text, DisplayFallbacks, "h3: Anybody 680, 86 %"),
        // Site .brand: Anybody 720, 80 %; the title's instance (740, 75 %) is the nearest of the four files.
        new(TypeRole.Brand, AnybodyTitle, 14f, 740, 75f, true, false, TypeColor.Text, DisplayFallbacks, ".brand: Anybody 720, 80 %"),
        // Site .spec: Martian Mono 500, 87.5 %, 11.5 px, caps, letter-spacing .04em (GDI draws no extra spacing).
        new(TypeRole.Spec, MartianMonoSpec, 8f, 500, 87.5f, true, true, TypeColor.Muted, MonoFallbacks, ".spec: Martian Mono 500, 87.5 %, caps"),
        new(TypeRole.Tag, MartianMonoSpec, 8f, 500, 87.5f, true, true, TypeColor.TextSecondary, MonoFallbacks, ".tag: Martian Mono 500, 87.5 %, caps"),
        new(TypeRole.Number, InstrumentSansNumber, 18f, 650, 100f, true, false, TypeColor.Text, DisplayFallbacks, ".st .v: Instrument Sans 650"),
        new(TypeRole.NumberLive, SegoeDisplaySemibold, 18f, 600, 100f, false, false, TypeColor.Text, [SegoeSemibold, Segoe], ".st .v, tabular digits"),
        new(TypeRole.Lede, SegoeText, 10f, 400, 100f, false, false, TypeColor.TextSecondary, TextFallbacks, ".lede: --ink-2"),
        new(TypeRole.Body, SegoeText, 9f, 400, 100f, false, false, TypeColor.Text, TextFallbacks, "--font-win"),
        new(TypeRole.BodyStrong, SegoeTextSemibold, 9f, 600, 100f, false, false, TypeColor.Text, [SegoeSemibold, Segoe], "--font-win, 600"),
        new(TypeRole.Caption, SegoeText, 8f, 400, 100f, false, false, TypeColor.Muted, TextFallbacks, ".meta: --muted"),
        new(TypeRole.Mono, Cascadia, 9f, 400, 100f, false, false, TypeColor.Text, [Consolas], "--font-mono on Windows"),
        new(TypeRole.Glyph, FluentIcons, 12f, 400, 100f, false, false, TypeColor.Text, [Mdl2], "system icons"),
        new(TypeRole.GlyphLarge, FluentIcons, 24f, 400, 100f, false, false, TypeColor.Muted, [Mdl2], "system icons"),
    ];

    private static readonly Dictionary<TypeRole, TypeSpec> ByRole = All.ToDictionary(s => s.Role);

    public static TypeSpec Get(TypeRole role) => ByRole[role];

    /// <summary>The text a role shows for <paramref name="text"/> (spec lines and tags in capitals).</summary>
    public static string Cased(TypeRole role, string text) =>
        Get(role).Uppercase ? text.ToUpperInvariant() : text;

    /// <summary>
    /// Fonts that carry text which is not Osmium's own (file names, paths, messages) must cover any script, so only these
    /// roles may use a bundled face, which covers Latin only.
    /// </summary>
    public static IReadOnlySet<TypeRole> BrandRoles { get; } =
        new HashSet<TypeRole> { TypeRole.Title, TypeRole.CardHeading, TypeRole.Brand, TypeRole.Spec, TypeRole.Tag, TypeRole.Number };
}
