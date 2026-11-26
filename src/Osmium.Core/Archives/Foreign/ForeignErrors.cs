using Osmium.Core.Pipeline;
using Osmium.Core.Text;

namespace Osmium.Core.Archives.Foreign;

/// <summary>
/// Turns what the parsers and decoders throw on damaged input into <see cref="CorruptDataException"/> with a plain
/// message. The limit exceptions, cancellation, password and "not supported" exceptions pass through unchanged.
/// </summary>
internal static class ForeignErrors
{
    private static readonly AsyncLocal<Action<Exception>?> FaultSeam = new();

    /// <summary>For tests (this async flow only): sees every exception of a kind that also means a programming error
    /// (a null reference, an index out of range…) as it is taken for damage, so that a fuzz test can tell a fault in
    /// Osmium's own parsers from a decoder library's way of reporting bad data.</summary>
    internal static Action<Exception>? FaultObserver { get => FaultSeam.Value; set => FaultSeam.Value = value; }

    /// <summary>An exception a parser or decoder throws on bad input (not a limit, a cancellation or a password).</summary>
    public static bool IsDecoderFailure(Exception e)
    {
        bool failure = IsFailure(e);
        if (failure && FaultObserver is { } observe && e is IndexOutOfRangeException or ArgumentException or OverflowException
                or InvalidOperationException or NullReferenceException or DivideByZeroException or InvalidCastException or ArrayTypeMismatchException)
            observe(e);
        return failure;
    }

    private static bool IsFailure(Exception e) => e switch
    {
        ExpansionLimitException or FreeSpaceException or EntryLimitException or OperationCanceledException => false,
        IOException => true, // CorruptDataException, EndOfStreamException, "Not a BZIP2 marked stream"…
        SharpCompress.Common.SharpCompressException or ZstdSharp.ZstdException or InvalidDataException => true,
        IndexOutOfRangeException or ArgumentException or OverflowException or FormatException or InvalidOperationException => true,
        System.Security.Cryptography.CryptographicException => true,
        NullReferenceException or DivideByZeroException or InvalidCastException or ArrayTypeMismatchException => true, // decoder faults on crafted input
        _ => false,
    };

    /// <summary>The short reason of a decoder failure, for "name: damaged data (reason)".</summary>
    public static string Reason(Exception e)
    {
        const string prefix = "damaged data (";
        string m = e.Message;
        if (e is CorruptDataException)
        {
            int i = m.IndexOf(prefix, StringComparison.Ordinal);
            if (i >= 0 && m.EndsWith(')')) return m[(i + prefix.Length)..^1];
            return m.TrimEnd('.');
        }
        string reason = e switch
        {
            EndOfStreamException => "the data ends early",
            ZstdSharp.ZstdException => m.TrimEnd('.'),
            InvalidOperationException when m.Contains("BZip2", StringComparison.Ordinal) => m.Contains("ends", StringComparison.Ordinal) ? "the data ends early" : "invalid compressed data",
            IOException when m.Contains("BZIP2", StringComparison.Ordinal) => "unexpected data after the end of the compressed stream",
            System.Security.Cryptography.CryptographicException => "decryption failed",
            _ => "invalid compressed data",
        };
        return reason.Length > 200 ? reason[..200] : reason;
    }

    /// <summary>"<paramref name="name"/>: damaged data (reason)", the name escaped for display.</summary>
    public static CorruptDataException Damaged(string name, Exception e) =>
        new($"{DisplayText.Escape(name)}: damaged data ({DisplayText.Escape(Reason(e))})", e);

    /// <summary>Runs <paramref name="action"/> and maps decoder failures (see <see cref="Damaged"/>).</summary>
    public static T Guard<T>(string name, Func<T> action)
    {
        try { return action(); }
        catch (Exception e) when (IsDecoderFailure(e)) { throw Damaged(name, e); }
    }
}
