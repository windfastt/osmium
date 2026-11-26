using Osmium.Core.Archives;
using Osmium.Core.Archives.Foreign;

namespace Osmium.Core.Tests.Foreign;

/// <summary>One entry of a <see cref="MemorySource"/>.</summary>
public sealed record MemoryEntry(string Path, byte[]? Data = null)
{
    internal EntryType Type { get; init; } = EntryType.File;
    public string? LinkTarget { get; init; }
    public DateTime? Modified { get; init; }
    public bool ReadOnly { get; init; }
    public bool Verified { get; init; } = true;
    /// <summary>Reading the data throws at its end, as a file whose check fails does.</summary>
    public bool Damaged { get; init; }
    /// <summary>Called with the number of bytes read so far, while the data is read.</summary>
    public Action<long>? OnRead { get; init; }

    public static MemoryEntry File(string path, string text) => new(path, System.Text.Encoding.UTF8.GetBytes(text));
    public static MemoryEntry Folder(string path) => new(path) { Type = EntryType.Directory };
    public static MemoryEntry SymLink(string path, string target) => new(path) { Type = EntryType.SymbolicLink, LinkTarget = target };
    public static MemoryEntry HardLink(string path, string target) => new(path) { Type = EntryType.HardLink, LinkTarget = target };
    public static MemoryEntry Device(string path) => new(path) { Type = EntryType.Special };
}

/// <summary>A foreign source over entries held in memory, to test the guarded writer without a format. Its archive
/// file is a small real file, whose length the limits use.</summary>
internal sealed class MemorySource : ForeignSource
{
    private readonly List<MemoryEntry> _data;

    public MemorySource(string archivePath, IEnumerable<MemoryEntry> entries, ArchiveOpenOptions? options = null)
        : base(archivePath, options ?? new ArchiveOpenOptions())
    {
        _data = entries.ToList();
        SetEntries([.. _data.Select((d, i) => new ForeignEntry
        {
            Index = i,
            Path = d.Path,
            Type = d.Type,
            Size = d.Data?.LongLength ?? (d.Type == EntryType.File ? 0 : null),
            ModifiedUtc = d.Modified,
            LinkTarget = d.LinkTarget,
            ReadOnly = d.ReadOnly,
        })], complete: true);
    }

    public override ArchiveKind Kind => ArchiveKind.Zip;
    public override string FormatName => "memory";
    protected override IReadOnlyList<ArchiveDetail> Describe() => [];

    protected override void Run(Operation op)
    {
        foreach (var e in Entries)
        {
            if (op.Selected(e) is not { } rel) continue;
            var d = _data[e.Index];
            try
            {
                op.Sink.Accept(e, rel, () => new EntryContent(new CountingStream(new Data(d), op.Guard), d.Verified));
            }
            catch (EntryDataException) { }
        }
    }

    private sealed class Data(MemoryEntry entry) : ReadOnlyStream
    {
        private readonly MemoryStream _ms = new(entry.Data ?? []);

        public override int Read(Span<byte> buffer)
        {
            int n = _ms.Read(buffer[..Math.Min(buffer.Length, 1 << 16)]);
            entry.OnRead?.Invoke(_ms.Position);
            if (n == 0 && entry.Damaged) throw new Osmium.Core.Pipeline.CorruptDataException("damaged data (CRC mismatch)");
            return n;
        }
    }
}
