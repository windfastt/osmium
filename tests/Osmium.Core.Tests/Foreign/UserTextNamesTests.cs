using System.Text.RegularExpressions;
using Osmium.Core.Archives;

namespace Osmium.Core.Tests.Foreign;

/// <summary>
/// No text a user sees calls a method or format by a trademark ("Deflate64", "ZIP64") or names a product: not the
/// methods and details of any archive Osmium opens, and not the string literals of the app, the desktop code, the
/// command-line tool or the engine. The command-line test project checks every run's output the same way.
/// </summary>
public partial class UserTextNamesTests
{
    private static readonly string[] Forbidden =
        ["Deflate64", "Deflate 64", "ZIP64", "Zip64", "WinZip", "WinRAR", "7-Zip", "PKZIP", "PKWARE", "Bandizip", "PeaZip", "NanaZip"];

    /// <summary>The accepted exceptions: (file, text, reason). Both are engine strings that the app shows and the
    /// command-line tool may print, nominative uses covered by the trademark sentence of the notices; they are
    /// reworded together with the codec registry.</summary>
    private static readonly (string File, string Text, string Reason)[] Allowed =
    [
        ("src/Osmium.Core/Codecs/StandardCodecs.cs", "The 7-Zip algorithm.", "the LZMA codec's description names where the algorithm comes from"),
        ("src/Osmium.Core/Analysis/Sniffer.cs", "7-Zip", "the detected-type label for 7z files; rules may match on it, so it is not renamed in this release"),
    ];

    private static string? FirstForbidden(string text) =>
        Forbidden.FirstOrDefault(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    public static TheoryData<string> Fixtures() => [.. ForeignData.AllFixtures()];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void MethodsAndDetailsOfEveryFixtureUseGenericNames(string fixture)
    {
        using var s = ArchiveSources.Open(ForeignData.Fixture(fixture.Split('/')), new ArchiveOpenOptions { Password = () => fixture.Contains("headers") ? "Pässwörd ü" : null },
            TestContext.Current.CancellationToken);
        foreach (var item in s.Items)
            Assert.Null(FirstForbidden(item.Method ?? ""));
        foreach (var d in s.Details)
            Assert.Null(FirstForbidden(d.Label + " " + d.Value));
        Assert.Null(FirstForbidden(s.FormatName));
    }

    [Fact]
    public void MethodsAndDetailsOfGeneratedArchivesUseGenericNames()
    {
        using var t = new TempDir();
        (string, byte[])[] tree = [("a/b.txt", ForeignData.Text(1000)), ("a/", [])];
        foreach (var (name, data) in new[]
        {
            ("g.zip", ForeignData.Zip(tree)), ("g.tar", ForeignData.Tar(tree)), ("g.tgz", ForeignData.GZip(ForeignData.Tar(tree))),
            ("g.gz", ForeignData.GZip(tree[0].Item2)), ("g.bz2", ForeignData.BZip2(tree[0].Item2)), ("g.zst", ForeignData.Zstd(tree[0].Item2)),
        })
        {
            using var s = ArchiveSources.Open(ForeignData.Write(t, name, data), null, TestContext.Current.CancellationToken);
            foreach (var item in s.Items) Assert.Null(FirstForbidden(item.Method ?? ""));
            foreach (var d in s.Details) Assert.Null(FirstForbidden(d.Label + " " + d.Value));
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Osmium.sln"))) return dir.FullName;
        throw new InvalidOperationException("Osmium.sln was not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"\$?@?""(?:[^""\\\n]|\\.|"""")*""")]
    private static partial Regex Literals();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Holes();

    [Fact]
    public void StringLiteralsOfTheAppTheToolAndTheEngineUseGenericNames()
    {
        string root = RepoRoot();
        var problems = new List<string>();
        int literals = 0;
        bool sawAllowed = false;
        foreach (var project in new[] { "Osmium.App", "Osmium.Desktop", "Osmium.Cli", "Osmium.Core" })
        {
            string dir = Path.Combine(root, "src", project);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal)) continue;
                string code = Comments().Replace(File.ReadAllText(file), m => m.Value.StartsWith("//", StringComparison.Ordinal) && IsInsideString(m) ? m.Value : "");
                foreach (Match lit in Literals().Matches(code))
                {
                    literals++;
                    string text = lit.Value.StartsWith('$') ? Holes().Replace(lit.Value, "") : lit.Value;
                    foreach (var (f, allowed, _) in Allowed)
                        if (f == rel && text.Contains(allowed, StringComparison.Ordinal))
                        {
                            sawAllowed = true;
                            text = text.Replace(allowed, "", StringComparison.Ordinal);
                        }
                    if (FirstForbidden(text) is { } word) problems.Add($"{rel}: {word} in {lit.Value}");
                }
            }
        }
        Assert.Empty(problems);
        Assert.True(literals > 1000, $"only {literals} string literals were found");
        Assert.True(sawAllowed, "the allowed exceptions were not found; update the list");
    }

    /// <summary>"https://…" inside a string looks like a comment to the simple comment pattern: keep those.</summary>
    private static bool IsInsideString(Match m)
    {
        int quotes = 0;
        string before = m.Result("$`");
        int lineStart = before.LastIndexOf('\n') + 1;
        for (int i = lineStart; i < before.Length; i++) if (before[i] == '"') quotes++;
        return quotes % 2 == 1;
    }
}
