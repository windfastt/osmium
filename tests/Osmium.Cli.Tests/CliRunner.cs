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
    /// streams are restored afterwards, also when the run throws.</summary>
    public static async Task<CliResult> RunAsync(string[] args, string input = "")
    {
        TextWriter output = Console.Out, error = Console.Error;
        TextReader stdin = Console.In;
        using var o = new StringWriter();
        using var e = new StringWriter();
        using var i = new StringReader(input);
        try
        {
            Console.SetOut(o);
            Console.SetError(e);
            Console.SetIn(i);
            int code = await Cli.RunAsync(args);
            return new CliResult(code, o.ToString(), e.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
            Console.SetIn(stdin);
        }
    }
}
