namespace Osmium.Cli.Tests;

/// <summary>
/// Every test class of this project is in this collection: the console (its output, error and input streams and
/// Ctrl+C handling) belongs to the whole process, so no two tests may use it at once.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}

/// <summary>What a run of the command-line tool printed and returned.</summary>
public sealed record CliResult(int ExitCode, string Output, string Error);

/// <summary>Runs the command-line tool in this process with its console redirected.</summary>
public static class CliRunner
{
    /// <summary>Runs <c>osmium &lt;args&gt;</c> with <paramref name="input"/> as standard input; the console's own
    /// streams are restored afterwards, also when the run throws. Every run also checks that the command's code read
    /// no option its row of the option table leaves out (it would be refused before it could be read).</summary>
    public static async Task<CliResult> RunAsync(string[] args, string input = "")
    {
        TextWriter output = Console.Out, error = Console.Error;
        TextReader stdin = Console.In;
        using var o = new StringWriter();
        using var e = new StringWriter();
        using var i = new StringReader(input);
        var undeclared = new List<string>();
        Args.UndeclaredRead = undeclared.Add;
        try
        {
            Console.SetOut(o);
            Console.SetError(e);
            Console.SetIn(i);
            int code = await Cli.RunAsync(args);
            var result = new CliResult(code, o.ToString(), e.ToString());
            UserTextNames.AssertNone(result.Output + result.Error, "osmium " + string.Join(' ', args));
            Assert.True(undeclared.Count == 0, $"osmium {args.FirstOrDefault()} reads options it does not declare: {string.Join(", ", undeclared.Distinct())}");
            return result;
        }
        finally
        {
            Args.UndeclaredRead = null;
            Console.SetOut(output);
            Console.SetError(error);
            Console.SetIn(stdin);
        }
    }
}

/// <summary>Text a user sees never uses trademarks for formats or methods: no "Deflate64" or "ZIP64", and no product
/// names. Every CLI run of these tests is checked.</summary>
public static class UserTextNames
{
    public static readonly string[] Forbidden =
        ["Deflate64", "Deflate 64", "ZIP64", "Zip64", "WinZip", "WinRAR", "7-Zip", "PKZIP", "PKWARE", "Bandizip", "PeaZip", "NanaZip"];

    /// <summary>Accepted exceptions, each with its reason (none in the command-line tool's own text).</summary>
    public static readonly (string Text, string Reason)[] Allowed = [];

    public static void AssertNone(string text, string where)
    {
        foreach (var (allowed, _) in Allowed) text = text.Replace(allowed, "", StringComparison.Ordinal);
        foreach (var word in Forbidden)
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                throw new Xunit.Sdk.XunitException($"'{word}' appears in the output of {where}.");
    }
}
