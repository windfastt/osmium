using System.Globalization;
using Osmium.Core.Container;

namespace Osmium.Core.Archives.Foreign;

/// <summary>One Extract or Test of a foreign archive: the limits, the selection, where entries go, progress.</summary>
internal sealed class Operation(LimitGuard guard, Func<ForeignEntry, string?> selected, ProgressReporter progress, CancellationToken ct)
{
    public LimitGuard Guard => guard;
    public ProgressReporter Progress => progress;
    public CancellationToken Token => ct;
    public IEntrySink Sink { get; set; } = null!;
    /// <summary>Archive bytes read so far (progress when the total is unknown).</summary>
    public long ArchiveBytesRead { get; private set; }
    public void Read(long bytes) => ArchiveBytesRead += bytes;
    /// <summary>The entry's path below the destination when it is selected (inside the prefix); null otherwise.</summary>
    public string? Selected(ForeignEntry e) => selected(e);
}

/// <summary>
/// What every foreign source shares: the items, the password (asked at most once, lazily, and spent when wrong or
/// refused), the selection and prefix, the limits checked before anything is read, and the guarded writer or tester
/// that receives each selected entry. A format only lists and decodes, in <see cref="Run"/>.
/// </summary>
internal abstract class ForeignSource : IArchiveSource
{
    private readonly Func<string?>? _passwordCallback;
    private bool _passwordAsked;
    private string? _password;
    private Exception? _passwordFailure;
    private List<ArchiveItem> _items = [];
    private IReadOnlyList<ArchiveDetail>? _details;

    protected ForeignSource(string path, ArchiveOpenOptions options)
    {
        Path = System.IO.Path.GetFullPath(path);
        Options = options;
        FileLength = new FileInfo(Path).Length;
        _passwordCallback = options.Password;
    }

    protected ArchiveOpenOptions Options { get; }
    protected List<ForeignEntry> Entries { get; private set; } = [];
    protected List<string> OpenWarnings { get; } = [];
    /// <summary>The password was shown right (encrypted headers, or a check that passed): later checks are skipped.</summary>
    protected bool PasswordProven { get; set; }
    /// <summary>The password, once asked (null before).</summary>
    protected string? CurrentPassword => _password;

    public string Path { get; }
    public abstract ArchiveKind Kind { get; }
    public abstract string FormatName { get; }
    public virtual bool IsEncrypted => Entries.Any(e => e.Encrypted);
    public long FileLength { get; }
    public bool ItemsComplete { get; protected set; } = true;
    public IReadOnlyList<ArchiveItem> Items => _items;
    public IReadOnlyList<ArchiveDetail> Details => _details ??= Describe();
    public IReadOnlyList<string> Warnings => OpenWarnings;

    public long? TotalSize
    {
        get
        {
            if (!ItemsComplete) return null;
            long sum = 0;
            foreach (var e in Entries)
            {
                if (!e.HasData) continue;
                if (e.Size is not { } s) return null;
                sum = LimitGuard.Add(sum, s);
            }
            return sum;
        }
    }

    /// <summary>Sets the listing (checked against <see cref="ImportLimits.MaxEntries"/>).</summary>
    protected void SetEntries(List<ForeignEntry> entries, bool complete)
    {
        LimitGuard.CheckEntryCount(entries.Count, Options.Limits);
        Entries = entries;
        _items = entries.ConvertAll(e => e.ToItem());
        ItemsComplete = complete;
        _details = null;
    }

    protected abstract IReadOnlyList<ArchiveDetail> Describe();

    /// <summary>Goes through the archive in its order and hands every entry <see cref="Operation.Selected"/> accepts
    /// to <see cref="Operation.Sink"/>; entries not selected are skipped, and their decoded bytes still counted.</summary>
    protected abstract void Run(Operation op);

    /// <summary>Checks the password on the selected encrypted files (normally the first one), streaming into a
    /// discarding sink under the operation's limits; throws <see cref="WrongPasswordException"/> when it is wrong.
    /// Returns false when nothing in the selection can show whether it is right (7z data without any stored CRC).</summary>
    protected virtual bool VerifyPassword(IReadOnlyList<ForeignEntry> encrypted, string password, Operation op) => true;

    /// <summary>Asks for the password (at most once per source); a refusal stays spent.</summary>
    protected string ObtainPassword()
    {
        if (_passwordFailure is not null)
            throw _passwordFailure is WrongPasswordException ? new WrongPasswordException() : new PasswordRequiredException();
        if (_password is not null) return _password;
        if (!_passwordAsked)
        {
            _passwordAsked = true;
            _password = _passwordCallback?.Invoke();
        }
        if (_password is null)
        {
            _passwordFailure = new PasswordRequiredException();
            throw new PasswordRequiredException();
        }
        return _password;
    }

    /// <summary>Marks the password as wrong for the life of the source.</summary>
    protected WrongPasswordException SpendPassword()
    {
        _password = null;
        _passwordFailure = new WrongPasswordException();
        return new WrongPasswordException();
    }

    public ArchiveExtractResult Extract(string destination, ArchiveExtractRequest request, IProgress<ExtractProgress>? progress, CancellationToken ct)
    {
        var limits = request.Limits ?? Options.Limits;
        return Execute(limits, request.Indices, request.Select, request.StripPrefix, progress, ct, destination,
            (op, reserved) => new GuardedExtractor(destination, request, limits, op.Guard, op.Progress, Path, reserved, ct));
    }

    public ArchiveExtractResult Test(Func<ArchiveItem, bool>? select, IProgress<ExtractProgress>? progress, CancellationToken ct) =>
        Execute(Options.Limits, null, select, null, progress, ct, null, (op, _) => new EntryTester(op.Progress));

    private ArchiveExtractResult Execute(ImportLimits limits, IReadOnlySet<int>? indices, Func<ArchiveItem, bool>? select,
        string? stripPrefix, IProgress<ExtractProgress>? progress, CancellationToken ct, string? destination,
        Func<Operation, IEnumerable<string>?, IEntrySink> makeSink)
    {
        ct.ThrowIfCancellationRequested();
        var guard = new LimitGuard(limits, FileLength, ct);
        string? prefix = stripPrefix?.TrimEnd('/');
        if (string.IsNullOrEmpty(prefix)) prefix = null;
        string? Selected(ForeignEntry e)
        {
            bool chosen = indices is not null ? indices.Contains(e.Index)
                : select is null || select(ItemsComplete && e.Index < _items.Count ? _items[e.Index] : e.ToItem());
            if (!chosen) return null;
            if (prefix is null) return e.Path;
            return e.Path.Length > prefix.Length + 1 && e.Path[prefix.Length] == '/' && e.Path.StartsWith(prefix, StringComparison.Ordinal)
                ? e.Path[(prefix.Length + 1)..] : null;
        }

        // What the listing tells before anything is decoded: the declared output, and which file needs the password.
        List<ForeignEntry>? chosen = ItemsComplete ? Entries.Where(e => Selected(e) is not null).ToList() : null;
        long declared = 0;
        int files = 0;
        bool sizesKnown = chosen is not null;
        foreach (var e in chosen ?? [])
        {
            if (!e.HasData || e.Unsafe is not null || e.Unsupported is not null) continue;
            files++;
            if (e.Size is { } s) declared = LimitGuard.Add(declared, s);
            else sizesKnown = false;
        }
        guard.CheckDeclared(declared);
        Operation? op = null;
        var reporter = new ProgressReporter(progress, sizesKnown ? declared : 0, sizesKnown ? files : 0, () => op?.ArchiveBytesRead ?? 0);
        op = new Operation(guard, Selected, reporter, ct);

        // Rule 1: the password is asked lazily and checked before anything is created.
        var encrypted = chosen?.Where(e => e.Encrypted && e.HasData && e.Unsafe is null && e.Unsupported is null).ToList() ?? [];
        if (encrypted.Count > 0)
        {
            string password = ObtainPassword();
            if (!PasswordProven)
            {
                reporter.Current("Checking the password");
                try { PasswordProven = VerifyPassword(encrypted, password, op); }
                catch (WrongPasswordException) { throw SpendPassword(); }
                reporter.Current(null);
            }
        }
        if (destination is not null) FreeSpace.CheckBefore(destination, declared, limits);

        var reserved = chosen?.Where(e => e.Type is EntryType.File or EntryType.Directory or EntryType.HardLink).Select(e => Selected(e)!);
        var sink = makeSink(op, reserved);
        op.Sink = sink;
        try
        {
            Run(op);
        }
        catch
        {
            (sink as GuardedExtractor)?.Abort();
            throw;
        }
        var result = sink.Result();
        reporter.Final();
        return result;
    }

    public virtual void Dispose() { }

    protected static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
    protected static string Bytes(long b) => FreeSpace.Bytes(b);
}
