using Microsoft.Win32.SafeHandles;
using Osmium.Core.Archives.Foreign.Compression;
using Osmium.Core.Text;

namespace Osmium.Core.Archives.Foreign.Tar;

/// <summary>
/// A TAR archive, plain or inside gzip, bzip2, xz or Zstandard. Every operation reads the stream once from the start
/// (listing too), with its decoded bytes counted against the limits. TAR stores no checksum of the content, so
/// every file counts as unverified; the compression's own checks (gzip and bzip2 CRCs, xz block checks, zstd frame
/// checksums) are still verified when the stream ends. With <see cref="ArchiveOpenOptions.ListEagerly"/> false,
/// Open does not list, and the first operation that reads the whole archive fills <see cref="IArchiveSource.Items"/>.
/// </summary>
internal sealed class TarSource : ForeignSource
{
    private readonly SafeFileHandle _handle;
    private readonly StreamCompression _compression;
    private StreamLayout? _layout;
    private int _links, _specials;

    private TarSource(string path, StreamCompression compression, ArchiveOpenOptions options) : base(path, options)
    {
        _handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        _compression = compression;
    }

    public static TarSource Open(string path, StreamCompression compression, ArchiveOpenOptions options, CancellationToken ct)
    {
        var s = new TarSource(path, compression, options);
        try
        {
            s.Initialize(ct);
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    public override ArchiveKind Kind => ArchiveKind.Tar;
    public override string FormatName => _compression == StreamCompression.None ? "TAR" : "TAR + " + StreamCodecs.Name(_compression);
    public override bool IsEncrypted => false;

    private string Name => System.IO.Path.GetFileName(Path);

    private void Initialize(CancellationToken ct)
    {
        if (_compression != StreamCompression.None)
        {
            using var slice = new FileSlice(_handle, 0, FileLength, 256);
            _layout = ForeignErrors.Guard(Name, () => StreamCodecs.Inspect(_compression, slice, Options.Limits, Name));
        }
        if (!Options.ListEagerly)
        {
            SetEntries([], complete: false);
            return;
        }
        var guard = new LimitGuard(Options.Limits, FileLength, ct);
        long lastReport = 0;
        var entries = new List<ForeignEntry>();
        long nameChars = 0;
        long read = 0;
        Pass(guard, n => read = n, (reader, header) =>
        {
            entries.Add(Make(header, entries.Count, ref nameChars));
            long now = Environment.TickCount64;
            if (Options.ListingProgress is { } p && now - lastReport >= 50)
            {
                lastReport = now;
                p.Report(read);
            }
            return true;
        }, onEnd: null);
        Options.ListingProgress?.Report(FileLength);
        SetEntries(entries, complete: true);
    }

    /// <summary>Reads the archive once: <paramref name="visit"/> for each header (false stops), then the end checks.</summary>
    private void Pass(LimitGuard guard, Action<long> onRead, Func<TarHeaderReader, TarHeader, bool> visit, Action<Exception>? onEnd)
    {
        var slice = new FileSlice(_handle, 0, FileLength);
        slice.OnRead = n => onRead(slice.BytesRead);
        using var decoded = new CountingStream(StreamCodecs.Open(_compression, slice, Options.Limits, Name), guard);
        var reader = new TarHeaderReader(decoded);
        int count = 0;
        while (true)
        {
            TarHeader? header = ForeignErrors.Guard(Name, reader.Next);
            if (header is null) break;
            LimitGuard.CheckEntryCount(++count, Options.Limits);
            if (!visit(reader, header)) return;
        }
        // The compression's own checks run when its end is read (gzip CRC and length, xz index, bzip2 stream CRC).
        try { reader.DrainToEnd(new byte[1 << 16]); }
        catch (Exception e) when (onEnd is not null && ForeignErrors.IsDecoderFailure(e)) { onEnd(e); }
        catch (Exception e) when (ForeignErrors.IsDecoderFailure(e)) { throw ForeignErrors.Damaged(Name, e); }
    }

    private ForeignEntry Make(TarHeader h, int index, ref long nameChars)
    {
        string name = h.Name;
        string? tooLong = null;
        if (name.Length > EntryNames.MaxTotal)
        {
            name = name[..EntryNames.MaxTotal];
            tooLong = "a path longer than 32,000 characters";
        }
        nameChars += name.Length + (h.LinkName?.Length ?? 0);
        if (nameChars * 2 > Options.Limits.MemoryBudgetBytes / 4)
            throw new EntryLimitException("The file list of this archive is larger than Osmium accepts.");
        var type = h.Kind switch
        {
            TarKind.Directory => EntryType.Directory,
            TarKind.SymbolicLink => EntryType.SymbolicLink,
            TarKind.HardLink => EntryType.HardLink,
            TarKind.Special => EntryType.Special,
            _ => EntryType.File,
        };
        if (type is EntryType.SymbolicLink or EntryType.HardLink) _links++;
        if (type == EntryType.Special) _specials++;
        return new ForeignEntry
        {
            Index = index,
            Path = type == EntryType.Directory ? name.TrimEnd('/') : name,
            Type = type,
            Size = type == EntryType.File ? h.Size : null,
            PackedSize = null,
            ModifiedUtc = h.ModifiedUtc,
            LinkTarget = h.LinkName,
            ReadOnly = type == EntryType.File && h.Mode != 0 && (h.Mode & 0x80) == 0,
            Unsupported = h.Unsupported,
            Unsafe = tooLong,
        };
    }

    protected override void Run(Operation op)
    {
        bool complete = ItemsComplete;
        var listed = complete ? null : new List<ForeignEntry>();
        long nameChars = 0;
        int index = 0, unverified = 0;
        bool damaged = false;
        try
        {
            Pass(op.Guard, read => op.Read(read - op.ArchiveBytesRead), (reader, header) =>
            {
                if (complete && index >= Entries.Count) throw new Osmium.Core.Pipeline.CorruptDataException("damaged data (the archive changed since it was opened)");
                var e = complete ? Entries[index] : Make(header, index, ref nameChars);
                index++;
                listed?.Add(e);
                if (op.Selected(e) is not { } rel) return true;
                if (e.HasData) unverified++;
                op.Sink.Accept(e, rel, e.HasData ? () => new EntryContent(reader.OpenData(), Verified: false) : null);
                return true;
            }, onEnd: ex =>
            {
                damaged = true;
                op.Sink.Damaged($"{DisplayText.Escape(Name)}: damaged data ({DisplayText.Escape(ForeignErrors.Reason(ex))}); the files read before this point were not checked");
            });
        }
        catch (Exception ex) when (ex is EntryDataException || ForeignErrors.IsDecoderFailure(ex))
        {
            damaged = true;
            var reason = ex is EntryDataException ? ex.InnerException! : ex;
            if (complete)
            {
                // Every selected file after the damage could not be read.
                foreach (var e in Entries.Skip(index))
                    if (op.Selected(e) is not null) op.Sink.Fail(e, "not read: the archive is damaged before it");
            }
            if (ex is not EntryDataException)
                op.Sink.Damaged($"{DisplayText.Escape(Name)}: damaged data ({DisplayText.Escape(ForeignErrors.Reason(reason))}); the rest of the archive could not be read");
        }
        if (listed is not null && !damaged) SetEntries(listed, complete: true);
        if (op.Sink is EntryTester && unverified > 0 && !damaged)
            op.Sink.Warn($"{Count(unverified)} file(s) have no stored checksum; the archive's structure was read completely"
                + (_compression == StreamCompression.None ? "." : $" and its {StreamCodecs.Name(_compression)} checks passed."));
    }

    protected override IReadOnlyList<ArchiveDetail> Describe()
    {
        var d = new List<ArchiveDetail> { new("Format", FormatName) };
        if (ItemsComplete)
        {
            d.Add(new("Files", Count(Entries.Count(e => e.HasData))));
            d.Add(new("Folders", Count(Entries.Count(e => e.Type == EntryType.Directory))));
            if (_links > 0) d.Add(new("Links", $"{Count(_links)} (not extracted)"));
            if (_specials > 0) d.Add(new("Other entries", $"{Count(_specials)} (not extracted)"));
            if (TotalSize is { } total) d.Add(new("Original size", Bytes(total)));
        }
        d.Add(new("Archive size", Bytes(FileLength)));
        if (_layout is { Detail.Length: > 0 }) d.Add(new("Compressed data", _layout.Detail));
        d.Add(new("Checksums", _compression switch
        {
            StreamCompression.None => "none (TAR stores none)",
            _ when _layout is { Unchecked: true } => $"none for the files; part of the {StreamCodecs.Name(_compression)} data has no check",
            _ => $"none for the files; the {StreamCodecs.Name(_compression)} data is checked",
        }));
        return d;
    }

    public override void Dispose() => _handle.Dispose();
}
