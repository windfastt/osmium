namespace Osmium.Core.Container;

public static class ExtractResultExtensions
{
    /// <summary>
    /// True when every error of the result is a "needs a newer version of Osmium" error, i.e. nothing else went wrong
    /// (a folder that could not be created, a damaged block…), so that "the other files verified" is a fair thing to say.
    /// </summary>
    public static bool OnlyNewerVersionErrors(this ExtractResult result) =>
        result.Errors.All(e => e.StartsWith("This archive needs a newer version of Osmium", StringComparison.Ordinal));
}
