using System.Text.RegularExpressions;

namespace Osmium.Desktop.Tests;

/// <summary>
/// App files that say <c>// Layout: logical units only</c> in their first 20 lines give sizes in logical pixels, so
/// they come out right at every display scale: no raw pixel numbers in <c>new Size(…)</c>, <c>new Padding(…)</c> or
/// assignments to Width, Height, MinimumSize and MaximumSize, unless the number is inside
/// <c>LogicalToDeviceUnits(…)</c>, <c>Space.Px(…)</c> or <c>Space.Pad(…)</c>, or the line ends with
/// <c>// layout: device pixels on purpose (reason)</c>. Zero is the same in every unit and always allowed.
/// </summary>
public partial class LayoutRulesTests(ITestOutputHelper output)
{
    public const string Marker = "// Layout: logical units only";

    /// <summary>App files that never carry the marker, relative to src/Osmium.App with '/' separators; a name ending
    /// in '*' matches every file that starts with it.</summary>
    public static readonly IReadOnlyDictionary<string, string> ExemptFromMarker = new Dictionary<string, string>
    {
        ["SelfTest.cs"] = "hosts windows at device sizes on purpose: the 1040 x 760 screenshots",
        ["SelfTest.*"] = "hosts windows at device sizes on purpose: the 1040 x 760 screenshots",
        ["Program.cs"] = "no layout code",
        ["ShellRecycler.cs"] = "no layout code",
    };

    /// <summary>False: <see cref="EveryAppFileIsMarkedOrExempt"/> only reports the unmarked files. True: it fails on
    /// them.</summary>
    public const bool RequireMarkerEverywhere = false;

    [GeneratedRegex(@"//\s*layout: device pixels on purpose \(.+\)\s*$")]
    private static partial Regex OnPurpose();

    [GeneratedRegex(@"(?<![\w.])\d+(?![\w.])")]
    private static partial Regex IntegerLiteral();

    [GeneratedRegex(@"\bnew\s+(Size|Padding)\s*\(")]
    private static partial Regex NewSizeOrPadding();

    [GeneratedRegex(@"\b(Width|Height|MinimumSize|MaximumSize)\s*=(?!=)")]
    private static partial Regex SizeAssignment();

    [GeneratedRegex(@"\b(LogicalToDeviceUnits|Space\.Px|Space\.Pad)\s*\(")]
    private static partial Regex Scaled();

    [GeneratedRegex(@"@?\$?""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])'")]
    private static partial Regex StringLiteral();

    /// <summary>The lines of <paramref name="source"/> (numbered from 1) that give a size in device pixels.</summary>
    public static IReadOnlyList<(int Line, string Text)> RawPixelSizes(string source)
    {
        var found = new List<(int, string)>();
        var lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (OnPurpose().IsMatch(line)) continue;
            string code = StringLiteral().Replace(line, "\"\"");
            int comment = code.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) code = code[..comment];
            code = BlankScaledArguments(code);
            if (HasRawNumber(code)) found.Add((i + 1, line.Trim()));
        }
        return found;
    }

    /// <summary>Blanks the arguments of LogicalToDeviceUnits(…), Space.Px(…) and Space.Pad(…): numbers there are logical.</summary>
    private static string BlankScaledArguments(string code)
    {
        var chars = code.ToCharArray();
        foreach (Match m in Scaled().Matches(code))
        {
            int depth = 1;
            for (int j = m.Index + m.Length; j < chars.Length && depth > 0; j++)
            {
                if (chars[j] == '(') depth++;
                else if (chars[j] == ')' && --depth == 0) break;
                chars[j] = ' ';
            }
        }
        return new string(chars);
    }

    private static bool HasRawNumber(string code)
    {
        foreach (Match m in NewSizeOrPadding().Matches(code))
            if (NonZeroNumber(Arguments(code, m.Index + m.Length))) return true;
        foreach (Match m in SizeAssignment().Matches(code))
            if (NonZeroNumber(Expression(code, m.Index + m.Length))) return true;
        return false;
    }

    /// <summary>The text up to the parenthesis that closes the one before <paramref name="start"/>.</summary>
    private static string Arguments(string code, int start)
    {
        int depth = 1, j = start;
        for (; j < code.Length; j++)
        {
            if (code[j] == '(') depth++;
            else if (code[j] == ')' && --depth == 0) break;
        }
        return code[start..j];
    }

    /// <summary>The assigned expression: up to a ';', ',' or '}' outside parentheses, or the end of the line.</summary>
    private static string Expression(string code, int start)
    {
        int depth = 0, j = start;
        for (; j < code.Length; j++)
        {
            char c = code[j];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') { if (--depth < 0) break; }
            else if (depth == 0 && c is ';' or ',' or '}') break;
        }
        return code[start..j];
    }

    private static bool NonZeroNumber(string text) => IntegerLiteral().Matches(text).Any(m => m.Value.Trim('0').Length > 0);

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Osmium.sln"))) return dir.FullName;
        throw new InvalidOperationException("Osmium.sln was not found above " + AppContext.BaseDirectory);
    }

    private static string AppFolder() => Path.Combine(RepositoryRoot(), "src", "Osmium.App");

    /// <summary>Every .cs file of the app outside bin and obj, with its path relative to the app folder ('/'-separated).</summary>
    private static IEnumerable<(string File, string Relative)> AppSources(string app)
    {
        string bin = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
        string obj = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
        foreach (var file in Directory.GetFiles(app, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(app, file);
            string inner = Path.DirectorySeparatorChar + relative;
            if (inner.Contains(bin, StringComparison.Ordinal) || inner.Contains(obj, StringComparison.Ordinal)) continue;
            yield return (file, relative.Replace(Path.DirectorySeparatorChar, '/'));
        }
    }

    private static bool HasMarker(string text) => text.Split('\n').Take(20).Any(l => l.Contains(Marker, StringComparison.Ordinal));

    /// <summary>True when <paramref name="relative"/> ('/'-separated, relative to the app folder) is in
    /// <see cref="ExemptFromMarker"/>.</summary>
    public static bool IsExempt(string relative) => ExemptFromMarker.Keys.Any(k =>
        k.EndsWith('*') ? relative.StartsWith(k[..^1], StringComparison.Ordinal) : relative == k);

    [Fact]
    public void AppFilesThatOptInUseLogicalUnitsOnly()
    {
        string app = AppFolder();
        Assert.True(Directory.Exists(app), app);
        var problems = new List<string>();
        foreach (var (file, relative) in AppSources(app))
        {
            var text = File.ReadAllText(file);
            if (!HasMarker(text)) continue;
            problems.AddRange(RawPixelSizes(text).Select(p => $"{relative}({p.Line}): {p.Text}"));
        }
        Assert.True(problems.Count == 0, "Sizes in device pixels (use LogicalToDeviceUnits, Space.Px or Space.Pad):\n" + string.Join("\n", problems));
    }

    [Fact]
    public void EveryAppFileIsMarkedOrExempt()
    {
        string app = AppFolder();
        Assert.True(Directory.Exists(app), app);
        var unmarked = AppSources(app)
            .Where(s => !IsExempt(s.Relative) && !HasMarker(File.ReadAllText(s.File)))
            .Select(s => s.Relative)
            .Order(StringComparer.Ordinal)
            .ToList();
        string list = string.Join("\n", unmarked);
        output.WriteLine(unmarked.Count == 0 ? "Every app file carries the layout marker or is exempt."
            : $"{unmarked.Count} app files carry neither the layout marker nor an exemption:\n{list}");
        if (RequireMarkerEverywhere)
            Assert.True(unmarked.Count == 0, $"Add \"{Marker}\" to these files, or an exemption with its reason:\n{list}");
    }

    [Theory]
    [InlineData("SelfTest.cs", true)]
    [InlineData("SelfTest.Layout.cs", true)]
    [InlineData("Program.cs", true)]
    [InlineData("ShellRecycler.cs", true)]
    [InlineData("Pages/CompressPage.cs", false)]
    [InlineData("Pages/SelfTest.cs", false)]
    [InlineData("Ui.cs", false)]
    public void TheExemptionsMatchOnlyTheirFiles(string relative, bool exempt) => Assert.Equal(exempt, IsExempt(relative));

    [Fact]
    public void TheRuleFindsRawPixelSizesAndAcceptsLogicalOnes()
    {
        const string sample = """
            // Layout: logical units only
            var a = new Size(300, 200);
            var b = new Padding(12, 6, 0, 0);
            var c = new Padding(8);
            box.Width = 520;
            panel.Height = 170;
            form.MinimumSize = new Size(900, 600);
            label.MaximumSize = new Size(Math.Max(300, w), 0);
            var d = new Label { Text = "x", Height = 24, AutoSize = true };
            row.Width = Math.Max(300, ClientSize.Width - 30);
            var ok1 = new Size(LogicalToDeviceUnits(300), LogicalToDeviceUnits(200));
            var ok2 = Space.Pad(this, 12, 6, 0, 0);
            box.Width = LogicalToDeviceUnits(520);
            box.Width = Space.Px(this, Space.Lg) + Space.Px(this, 4);
            var ok3 = new Padding(0);
            var ok4 = new Size(0, 0);
            panel.Height = 0;
            box.Width = width;
            if (box.Width == 520) Redraw();
            var ok5 = new Size(width, height);
            label.Text = "new Size(300, 200) and Width = 5";
            var ok6 = 9f; ColumnWidth = 40;
            strip.Height = 32; // layout: device pixels on purpose (an icon strip of 16-pixel icons)
            // box.Width = 520; in a comment
            var ok7 = new Font(family, 9f);
            """;
        var found = RawPixelSizes(sample).Select(p => p.Line).ToList();
        Assert.Equal([2, 3, 4, 5, 6, 7, 8, 9, 10], found);
    }
}
