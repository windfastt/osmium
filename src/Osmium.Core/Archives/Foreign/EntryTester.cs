using Osmium.Core.Text;

namespace Osmium.Core.Archives.Foreign;

/// <summary>Test: reads every selected file through the format's checks into a discarding sink. Nothing is written,
/// so the naming rules do not apply; entries that could never be extracted (overlapping, sparse, devices, methods
/// Osmium cannot read) are counted as they would be by Extract.</summary>
internal sealed class EntryTester(ProgressReporter progress) : IEntrySink
{
    private readonly List<string> _errors = [], _warnings = [];
    private int _ok, _failed, _unsafe, _unsupported, _unverified;
    private readonly byte[] _buf = new byte[1 << 18];

    private static string Esc(string s) => DisplayText.Escape(s);

    public void Accept(ForeignEntry e, string relativePath, Func<EntryContent>? open)
    {
        if (e.Unsafe is { } why && e.HasData)
        {
            _unsafe++;
            _errors.Add($"{Esc(e.Path)}: not checked: {Esc(why)}");
            return;
        }
        if (e.Type == EntryType.Special || (e.HasData && e.Unsupported is not null))
        {
            _unsupported++;
            _errors.Add($"{Esc(e.Path)}: not checked: {Esc(e.Unsupported ?? "not a regular file")}");
            return;
        }
        if (!e.HasData || open is null) return;
        progress.Current(e.Path);
        EntryContent content;
        try { content = open(); }
        catch (NotSupportedException ex)
        {
            _unsupported++;
            _errors.Add(ex.Message);
            return;
        }
        catch (Exception ex) when (ForeignErrors.IsDecoderFailure(ex))
        {
            _failed++;
            _errors.Add($"{Esc(e.Path)}: {(e.Encrypted ? "wrong password or damaged file" : "damaged data")} ({Esc(ForeignErrors.Reason(ex))})");
            throw new EntryDataException(ex);
        }
        try
        {
            while (true)
            {
                int n = content.Stream.Read(_buf, 0, _buf.Length);
                if (n == 0) break;
                progress.Output(n);
            }
        }
        catch (Exception ex) when (ForeignErrors.IsDecoderFailure(ex))
        {
            _failed++;
            _errors.Add($"{Esc(e.Path)}: {(e.Encrypted ? "wrong password or damaged file" : "damaged data")} ({Esc(ForeignErrors.Reason(ex))})");
            throw new EntryDataException(ex);
        }
        _ok++;
        if (!content.Verified) _unverified++;
        progress.FileDone();
    }

    public void Fail(ForeignEntry entry, string message)
    {
        if (!entry.HasData) return;
        _failed++;
        _errors.Add($"{Esc(entry.Path)}: {message}");
    }

    public void Warn(string warning) => _warnings.Add(warning);

    public void Damaged(string message)
    {
        _failed++;
        _errors.Add(message);
    }

    public ArchiveExtractResult Result() => new()
    {
        FilesOk = _ok,
        FilesFailed = _failed,
        SkippedUnsafe = _unsafe,
        SkippedUnsupported = _unsupported,
        Unverified = _unverified,
        Errors = [.. _errors],
        Warnings = [.. _warnings],
    };
}
