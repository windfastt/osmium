using Microsoft.Win32.SafeHandles;
using Osmium.Core.Archives.Foreign.Compression;

namespace Osmium.Core.Archives.Foreign;

/// <summary>
/// A single compressed file (.gz, .bz2, .xz, .zst) that is not a TAR: one item, named after the archive without its
/// compression extension. Every member, stream or frame is decoded, and bytes after the last one (other than xz
/// stream padding) make it damaged. Its size is known only when the format states it exactly (the xz index, the
/// content size of every zstd frame); the gzip size field is the size modulo 4 GiB and is not used.
/// </summary>
internal sealed class SingleStreamSource : ForeignSource
{
    private readonly SafeFileHandle _handle;
    private readonly StreamCompression _compression;
    private StreamLayout _layout = new(null, false, "");

    private SingleStreamSource(string path, StreamCompression compression, ArchiveOpenOptions options) : base(path, options)
    {
        _handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        _compression = compression;
    }

    public static SingleStreamSource Open(string path, StreamCompression compression, ArchiveOpenOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var s = new SingleStreamSource(path, compression, options);
        try
        {
            using (var slice = new FileSlice(s._handle, 0, s.FileLength, 256))
                s._layout = ForeignErrors.Guard(s.Name, () => StreamCodecs.Inspect(compression, slice, options.Limits, s.Name));
            s.SetEntries([new ForeignEntry
            {
                Index = 0,
                Path = ItemName(path),
                Type = EntryType.File,
                Size = s._layout.ExactSize,
                PackedSize = s.FileLength,
                ModifiedUtc = File.GetLastWriteTimeUtc(s.Path),
                Method = StreamCodecs.Name(compression),
            }], complete: true);
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    private string Name => System.IO.Path.GetFileName(Path);

    /// <summary>"notes.txt.gz" → "notes.txt", "x.tgz" → "x.tar"; a name without such an extension keeps its stem, and
    /// a name without any extension gets " (decompressed)": never the archive's own name, which the file would
    /// otherwise take next to it.</summary>
    internal static string ItemName(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        foreach (var (ext, replacement) in new[] { (".tgz", ".tar"), (".tbz2", ".tar"), (".tbz", ".tar"), (".txz", ".tar"), (".tzst", ".tar"), (".gz", ""), (".bz2", ""), (".xz", ""), (".zst", "") })
            if (name.Length > ext.Length && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length] + replacement;
        string stem = ArchiveSources.StemOf(path);
        return string.Equals(stem, name, StringComparison.OrdinalIgnoreCase) ? stem + " (decompressed)" : stem;
    }

    public override ArchiveKind Kind => _compression switch
    {
        StreamCompression.GZip => ArchiveKind.GZip,
        StreamCompression.BZip2 => ArchiveKind.BZip2,
        StreamCompression.Xz => ArchiveKind.Xz,
        _ => ArchiveKind.Zstd,
    };

    public override string FormatName => StreamCodecs.Name(_compression);
    public override bool IsEncrypted => false;

    protected override void Run(Operation op)
    {
        var e = Entries[0];
        if (op.Selected(e) is not { } rel) return;
        var slice = new FileSlice(_handle, 0, FileLength);
        slice.OnRead = n => op.Read(n);
        try
        {
            op.Sink.Accept(e, rel, () => new EntryContent(
                new CountingStream(ForeignErrors.Guard(Name, () => StreamCodecs.Open(_compression, slice, Options.Limits, Name)), op.Guard),
                Verified: !_layout.Unchecked));
        }
        catch (EntryDataException) { }
    }

    protected override IReadOnlyList<ArchiveDetail> Describe()
    {
        var d = new List<ArchiveDetail> { new("Format", FormatName) };
        d.Add(new("Original size", _layout.ExactSize is { } size ? Bytes(size) : "not stored"));
        d.Add(new("Archive size", Bytes(FileLength)));
        if (_layout.Detail.Length > 0) d.Add(new("Compressed data", _layout.Detail));
        d.Add(new("Checksum", _compression switch
        {
            StreamCompression.GZip => "CRC-32 of each member",
            StreamCompression.BZip2 => "CRC-32 of each block and stream",
            StreamCompression.Xz => _layout.Unchecked ? "none for some blocks" : "every block's check",
            _ => _layout.Unchecked ? "none for some frames" : "every frame's checksum",
        }));
        return d;
    }

    public override void Dispose() => _handle.Dispose();
}
