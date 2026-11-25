using System.Text.RegularExpressions;

namespace Osmium.Desktop.Tests;

/// <summary>
/// App files that say <c>// Layout: logical units only</c> in their first 20 lines give sizes in logical pixels, so
/// they come out right at every display scale: no raw pixel numbers in <c>new Size(…)</c>, <c>new Padding(…)</c> or
/// assignments to Width, Height, MinimumSize and MaximumSize, unless the number is inside
/// <c>LogicalToDeviceUnits(…)</c>, <c>Space.Px(…)</c> or <c>Space.Pad(…)</c>, or the line ends with
/// <c>// layout: device pixels on purpose (reason)</c>. Zero is the same in every unit and always allowed.
/// </summary>
public partial class LayoutRulesTests
{
    public const string Marker = "// Layout: logical units only";

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

    [Fact]
    public void AppFilesThatOptInUseLogicalUnitsOnly()
    {
        string app = Path.Combine(RepositoryRoot(), "src", "Osmium.App");
        Assert.True(Directory.Exists(app), app);
        var problems = new List<string>();
        foreach (var file in Directory.GetFiles(app, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            var text = File.ReadAllText(file);
            if (!text.Split('\n').Take(20).Any(l => l.Contains(Marker, StringComparison.Ordinal))) continue;
            problems.AddRange(RawPixelSizes(text).Select(p => $"{Path.GetRelativePath(app, file)}({p.Line}): {p.Text}"));
        }
        Assert.True(problems.Count == 0, "Sizes in device pixels (use LogicalToDeviceUnits, Space.Px or Space.Pad):\n" + string.Join("\n", problems));
    }

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
