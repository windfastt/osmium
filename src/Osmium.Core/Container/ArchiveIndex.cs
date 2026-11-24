using System.Buffers.Binary;
using System.Text;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Container;

public enum EntryKind : byte { File = 0, Directory = 1 }

public readonly record struct Segment(int Frame, long Offset, long Length);

public sealed class ArchiveEntry
{
    public required string Path { get; init; }
    public EntryKind Kind { get; init; }
    public DateTime? ModifiedUtc { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public FileAttributes? Attributes { get; init; }
    public long Size { get; init; }
    public IReadOnlyList<Segment> Segments { get; init; } = [];
    public UInt128 Hash { get; init; }

    public string Name => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;
    public bool IsDirectory => Kind == EntryKind.Directory;
}

public sealed record BlockInfo(int Frame, long Offset, long FrameLength, long RawSize, long DecodeMemory, BlockDescriptor Descriptor)
{
    public long PackedSize => FrameLength - Osa.FrameHeaderSize;
}

/// <summary>Archive index (FORMAT §3).</summary>
public sealed class ArchiveIndex
{
    public const int Version = 1;
    public const FileAttributes StoredAttributes = (FileAttributes)0x2027;

    public List<BlockInfo> Blocks { get; } = [];
    public List<ArchiveEntry> Entries { get; } = [];
    public Dictionary<string, byte[]> Metadata { get; } = new(StringComparer.Ordinal);

    public string? MetaString(string key) => Metadata.TryGetValue(key, out var v) ? Encoding.UTF8.GetString(v) : null;

    public static int ComparePaths(string a, string b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));

    public byte[] Serialize()
    {
        var w = new BlobWriter().U(Version).U((ulong)Blocks.Count);
        foreach (var b in Blocks)
        {
            // Every record is length-prefixed: newer writers may append fields that older readers skip.
            var rec = new BlobWriter().U(0).U((ulong)b.FrameLength).U((ulong)b.RawSize)
                .U((ulong)((b.DecodeMemory + 1023) / 1024)).Bytes(b.Descriptor.Serialize());
            w.Bytes(rec.ToArray());
        }

        var sorted = Entries.OrderBy(e => e.Path, Comparer<string>.Create(ComparePaths)).ToList();
        w.U((ulong)sorted.Count);
        byte[] prev = [];
        Span<byte> h = stackalloc byte[16];
        foreach (var e in sorted)
        {
            var w0 = w;
            w = new BlobWriter();
            var p = Encoding.UTF8.GetBytes(e.Path);
            int shared = 0;
            while (shared < p.Length && shared < prev.Length && p[shared] == prev[shared]) shared++;
            w.U((ulong)shared).U((ulong)(p.Length - shared)).Raw(p.AsSpan(shared));
            prev = p;
            w.B((byte)e.Kind);
            ulong present = (e.ModifiedUtc.HasValue ? 1u : 0u) | (e.CreatedUtc.HasValue ? 2u : 0u) | (e.Attributes.HasValue ? 4u : 0u);
            w.U(present);
            if (e.ModifiedUtc is { } m) w.U64((ulong)m.ToFileTimeUtc());
            if (e.CreatedUtc is { } c) w.U64((ulong)c.ToFileTimeUtc());
            if (e.Attributes is { } a) w.U((ulong)(a & StoredAttributes));
            if (e.Kind == EntryKind.File)
            {
                w.U((ulong)e.Size).U((ulong)e.Segments.Count);
                foreach (var s in e.Segments) w.U((ulong)s.Frame).U((ulong)s.Offset).U((ulong)s.Length);
                BinaryPrimitives.WriteUInt128LittleEndian(h, e.Hash);
                w.Raw(h);
            }
            w0.Bytes(w.ToArray());
            w = w0;
        }
        w.U((ulong)Metadata.Count);
        foreach (var kv in Metadata.OrderBy(k => k.Key, StringComparer.Ordinal)) w.Str(kv.Key).Bytes(kv.Value);
        return w.ToArray();
    }

    public static ArchiveIndex Deserialize(ReadOnlySpan<byte> data, long headerLength, MethodRegistry registry)
    {
        var r = new BlobReader(data);
        int ver = r.Int(1000);
        if (ver > Version) throw new NewerVersionException($"index version {ver}");
        if (ver != Version) throw new CorruptDataException($"Unsupported index version {ver}.");
        var idx = new ArchiveIndex();
        int nb = r.Int(1 << 24);
        long offset = headerLength;
        for (int i = 0; i < nb; i++)
        {
            var rr = new BlobReader(r.Bytes(1 << 20));
            long gap = (long)rr.U();
            long frameLen = (long)rr.U();
            long raw = (long)rr.U();
            long memKiB = (long)rr.U();
            if (raw > BlockLimits.MaxRawSize) throw new CorruptDataException("Block larger than the 1 GiB limit.");
            // A block that needs a newer Osmium gets a stand-in, so the archive still opens and its other blocks extract.
            var desc = BlockDescriptor.DeserializeOrStandIn(rr.Bytes(1 << 16), registry, $"block {i}");
            offset += gap;
            idx.Blocks.Add(new BlockInfo(i, offset, frameLen, raw, memKiB * 1024, desc));
            offset += frameLen;
        }
        int ne = r.Int(1 << 26);
        byte[] prev = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < ne; i++)
        {
            var r2 = new BlobReader(r.Bytes(1 << 26));
            int shared = r2.Int(32768);
            int suffix = r2.Int(32768);
            if (shared > prev.Length || shared + suffix > 32768) throw new CorruptDataException("Corrupt path encoding in index.");
            var p = new byte[shared + suffix];
            prev.AsSpan(0, shared).CopyTo(p);
            r2.Raw(suffix).CopyTo(p.AsSpan(shared));
            prev = p;
            var kind = (EntryKind)r2.B();
            if (kind is not (EntryKind.File or EntryKind.Directory)) throw new CorruptDataException("Unknown entry kind.");
            ulong present = r2.U();
            DateTime? m = (present & 1) != 0 ? SafeFileTime((long)r2.U64()) : null;
            DateTime? c = (present & 2) != 0 ? SafeFileTime((long)r2.U64()) : null;
            FileAttributes? a = (present & 4) != 0 ? (FileAttributes)(int)r2.U() & StoredAttributes : null;
            long size = 0;
            var segs = new List<Segment>();
            UInt128 hash = 0;
            if (kind == EntryKind.File)
            {
                size = (long)r2.U();
                int ns = r2.Int(1 << 24);
                long sum = 0;
                for (int s = 0; s < ns; s++)
                {
                    if (nb == 0) throw new CorruptDataException("A file refers to a block, but the archive has none.");
                    int frame = r2.Int(nb - 1);
                    ulong uoff = r2.U(), ulen = r2.U();
                    long raw = idx.Blocks[frame].RawSize;
                    // Unsigned comparisons: a crafted offset or length must not wrap around to pass the check.
                    if (ulen == 0 || uoff > (ulong)raw || ulen > (ulong)raw - uoff) throw new CorruptDataException("Segment points outside its block.");
                    long off = (long)uoff, len = (long)ulen;
                    segs.Add(new Segment(frame, off, len));
                    sum += len;
                }
                if (sum != size) throw new CorruptDataException("Segment lengths do not add up to the file size.");
                hash = BinaryPrimitives.ReadUInt128LittleEndian(r2.Raw(16));
            }
            string path = Encoding.UTF8.GetString(p);
            if (!seen.Add(path)) throw new CorruptDataException($"Duplicate path '{path}' in index.");
            idx.Entries.Add(new ArchiveEntry
            {
                Path = path, Kind = kind, ModifiedUtc = m, CreatedUtc = c, Attributes = a,
                Size = size, Segments = segs, Hash = hash,
            });
        }
        int nm = r.Int(1 << 16);
        for (int i = 0; i < nm; i++) idx.Metadata[r.Str(1024)] = r.Bytes(1 << 28).ToArray();
        return idx;
    }

    private static DateTime? SafeFileTime(long ft)
    {
        try { return DateTime.FromFileTimeUtc(ft); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
