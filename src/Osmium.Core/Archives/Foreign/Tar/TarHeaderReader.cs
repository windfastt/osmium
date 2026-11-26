using System.Globalization;
using System.Text;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Archives.Foreign.Tar;

internal enum TarKind { File, Directory, SymbolicLink, HardLink, Special }

/// <summary>One TAR entry as its headers describe it (PAX and GNU long-name records already applied).</summary>
/// <param name="Unsupported">Why the entry cannot be extracted (sparse, multi-volume part, device…), or null.</param>
internal sealed record TarHeader(string Name, TarKind Kind, long Size, DateTime? ModifiedUtc, int Mode, string? LinkName, string? Unsupported);

/// <summary>
/// Reads TAR headers (V7, POSIX ustar and PAX, GNU) from a forward-only stream. Written here rather than taken from
/// <c>System.Formats.Tar</c>, which allocates PAX and long-name records at their declared size, aborts the whole
/// archive at a GNU sparse entry and returns a PAX sparse entry as an ordinary file holding the sparse map. Here a
/// metadata record over 1 MiB is refused, and sparse, multi-volume and volume-label entries come back as
/// <see cref="TarKind.Special"/> with a reason, so the rest of the archive is still read.
/// </summary>
internal sealed class TarHeaderReader(Stream input)
{
    public const int MaxMetadataRecord = 1 << 20;
    private readonly byte[] _block = new byte[512];
    private long _dataLeft, _padLeft;
    private bool _ended;

    /// <summary>Bytes of the TAR stream consumed so far.</summary>
    public long Position { get; private set; }

    /// <summary>The next entry, or null at the end of the archive. The data of the previous entry that was not read
    /// is skipped first.</summary>
    public TarHeader? Next()
    {
        if (_ended) return null;
        SkipRest();
        string? paxPath = null, paxLink = null, gnuName = null, gnuLink = null, sparseName = null;
        long? paxSize = null;
        DateTime? paxTime = null;
        bool sparse = false;
        while (true)
        {
            if (!ReadBlock(allowEnd: true)) { _ended = true; return null; }
            if (_block.AsSpan().IndexOfAnyExcept((byte)0) < 0) { _ended = true; return null; }
            if (!ChecksumMatches(_block)) throw new CorruptDataException("damaged data (a TAR header's checksum does not match)");
            char type = (char)_block[156];
            long size = Number(_block.AsSpan(124, 12), "size");
            if (type is 'x' or 'g' or 'L' or 'K')
            {
                if (size > MaxMetadataRecord) throw new CorruptDataException("damaged data (TAR header too large)");
                var data = new byte[size];
                ReadRecord(data);
                SkipPadding(size);
                if (type == 'L') gnuName = CString(data);
                else if (type == 'K') gnuLink = CString(data);
                else
                {
                    foreach (var (key, value) in Pax(data))
                    {
                        if (key.StartsWith("GNU.sparse.", StringComparison.Ordinal))
                        {
                            sparse = true;
                            if (key == "GNU.sparse.name") sparseName = value;
                            continue;
                        }
                        if (type == 'g') continue; // global records: only the sparse mark matters here
                        switch (key)
                        {
                            case "path": paxPath = value; break;
                            case "linkpath": paxLink = value; break;
                            case "size":
                                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long s) || s < 0)
                                    throw new CorruptDataException("damaged data (a TAR size record is not a number)");
                                paxSize = s;
                                break;
                            case "mtime": paxTime = PaxTime(value); break;
                        }
                    }
                }
                continue;
            }

            long dataSize = paxSize ?? size;
            bool ustar = _block.AsSpan(257, 6).SequenceEqual("ustar\0"u8);
            string name = paxPath ?? gnuName ?? HeaderName(ustar);
            string link = paxLink ?? gnuLink ?? CString(_block.AsSpan(157, 100));
            DateTime? mtime = paxTime ?? Time(Number(_block.AsSpan(136, 12), "time"));
            int mode = (int)Math.Clamp(Number(_block.AsSpan(100, 8), "mode"), 0, int.MaxValue);
            string? unsupported = null;
            TarKind kind;
            switch (type)
            {
                case '0' or '\0' or '7':
                    kind = name.EndsWith('/') ? TarKind.Directory : TarKind.File;
                    break;
                case '5' or 'D': kind = TarKind.Directory; break;
                case '1': kind = TarKind.HardLink; break;
                case '2': kind = TarKind.SymbolicLink; break;
                case '3' or '4': kind = TarKind.Special; unsupported = "a device"; break;
                case '6': kind = TarKind.Special; unsupported = "a FIFO"; break;
                case 'S':
                    kind = TarKind.Special;
                    sparse = true;
                    SkipSparseExtensions();
                    break;
                case 'M': kind = TarKind.Special; unsupported = "a part of a multi-volume TAR archive"; break;
                case 'V': kind = TarKind.Special; unsupported = "a TAR volume header"; break;
                default: kind = TarKind.Special; unsupported = $"an unknown TAR entry type '{(type < ' ' || type > '~' ? '?' : type)}'"; break;
            }
            if (sparse)
            {
                kind = TarKind.Special;
                unsupported = "sparse TAR entries are not supported";
                if (sparseName is not null) name = sparseName;
            }
            _dataLeft = dataSize;
            _padLeft = (512 - dataSize % 512) % 512;
            return new TarHeader(name, kind, dataSize, mtime, mode, kind is TarKind.SymbolicLink or TarKind.HardLink ? link : null, unsupported);
        }
    }

    /// <summary>The current entry's data (its declared size; an early end of the stream throws).</summary>
    public int ReadData(Span<byte> buffer)
    {
        if (_dataLeft <= 0 || buffer.IsEmpty) return 0;
        if (buffer.Length > _dataLeft) buffer = buffer[..(int)_dataLeft];
        int n = input.Read(buffer);
        if (n <= 0) throw new CorruptDataException("damaged data (the TAR archive ends in the middle of an entry)");
        _dataLeft -= n;
        Position += n;
        return n;
    }

    /// <summary>A stream over the current entry's data.</summary>
    public Stream OpenData() => new DataStream(this);

    /// <summary>Reads what is left of the TAR stream after its end (padding), so the compression's own end checks run.</summary>
    public void DrainToEnd(Span<byte> scratch)
    {
        while (true)
        {
            int n = input.Read(scratch);
            if (n <= 0) return;
            Position += n;
        }
    }

    private void SkipRest()
    {
        Skip(_dataLeft);
        _dataLeft = 0;
        Skip(_padLeft);
        _padLeft = 0;
    }

    private void SkipPadding(long size) => Skip((512 - size % 512) % 512);

    private void Skip(long count)
    {
        if (count <= 0) return;
        if (input.CanSeek)
        {
            if (input.Position + count > input.Length) throw new CorruptDataException("damaged data (the TAR archive ends in the middle of an entry)");
            input.Position += count;
            Position += count;
            return;
        }
        var scratch = new byte[(int)Math.Min(count, 1 << 16)];
        while (count > 0)
        {
            int n = input.Read(scratch, 0, (int)Math.Min(count, scratch.Length));
            if (n <= 0) throw new CorruptDataException("damaged data (the TAR archive ends in the middle of an entry)");
            count -= n;
            Position += n;
        }
    }

    private void ReadRecord(byte[] data)
    {
        int got = 0;
        while (got < data.Length)
        {
            int n = input.Read(data, got, data.Length - got);
            if (n <= 0) throw new CorruptDataException("damaged data (the TAR archive ends in the middle of a header)");
            got += n;
        }
        Position += got;
    }

    private bool ReadBlock(bool allowEnd)
    {
        int got = 0;
        while (got < 512)
        {
            int n = input.Read(_block, got, 512 - got);
            if (n <= 0)
            {
                if (got == 0 && allowEnd) return false;
                throw new CorruptDataException("damaged data (the TAR archive ends in the middle of a header)");
            }
            got += n;
        }
        Position += 512;
        return true;
    }

    /// <summary>GNU sparse headers may be followed by extension blocks listing more of the map.</summary>
    private void SkipSparseExtensions()
    {
        bool more = _block[482] != 0;
        int guard = 0;
        while (more)
        {
            if (++guard > 1_000_000) throw new CorruptDataException("damaged data (TAR header too large)");
            ReadBlock(allowEnd: false);
            more = _block[504] != 0;
        }
    }

    private string HeaderName(bool ustar)
    {
        string name = CString(_block.AsSpan(0, 100));
        if (ustar)
        {
            string prefix = CString(_block.AsSpan(345, 155));
            if (prefix.Length > 0) name = prefix + "/" + name;
        }
        return name;
    }

    /// <summary>The header checksum: the sum of its bytes with the checksum field counted as spaces (unsigned, or
    /// signed as some old writers did).</summary>
    internal static bool ChecksumMatches(ReadOnlySpan<byte> block)
    {
        long stored;
        try { stored = Number(block.Slice(148, 8), "checksum"); }
        catch (CorruptDataException) { return false; }
        long unsigned = 0, signed = 0;
        for (int i = 0; i < 512; i++)
        {
            byte b = i is >= 148 and < 156 ? (byte)' ' : block[i];
            unsigned += b;
            signed += (sbyte)b;
        }
        return stored == unsigned || stored == signed;
    }

    /// <summary>An octal field (leading spaces, ending at a space or NUL) or GNU's base-256 form.</summary>
    internal static long Number(ReadOnlySpan<byte> field, string what)
    {
        if ((field[0] & 0x80) != 0)
        {
            if (field[0] != 0x80) throw new CorruptDataException($"damaged data (a negative TAR {what})");
            long v = 0;
            for (int i = 1; i < field.Length; i++)
            {
                if (v > (long.MaxValue >> 8)) throw new CorruptDataException($"damaged data (a TAR {what} is too large)");
                v = (v << 8) | field[i];
            }
            return v;
        }
        int p = 0;
        while (p < field.Length && field[p] == ' ') p++;
        long value = 0;
        bool any = false;
        for (; p < field.Length; p++)
        {
            byte b = field[p];
            if (b is 0 or (byte)' ') break;
            if (b is < (byte)'0' or > (byte)'7') throw new CorruptDataException($"damaged data (a TAR {what} field is not a number)");
            if (value > (long.MaxValue >> 3)) throw new CorruptDataException($"damaged data (a TAR {what} is too large)");
            value = (value << 3) | (uint)(b - '0');
            any = true;
        }
        return any ? value : 0;
    }

    private static DateTime? Time(long seconds) =>
        seconds is >= 0 and < 253402300799 ? DateTime.UnixEpoch.AddSeconds(seconds) : null;

    private static DateTime? PaxTime(string value)
    {
        int dot = value.IndexOf('.');
        string whole = dot < 0 ? value : value[..dot];
        return long.TryParse(whole, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long s) ? Time(s) : null;
    }

    private static string CString(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Decode(end < 0 ? bytes : bytes[..end]);
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>UTF-8 when the bytes are valid UTF-8, else Latin-1 (old archives in a local code page).</summary>
    internal static string Decode(ReadOnlySpan<byte> bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>PAX records: "<length> <key>=<value>\n", the length counting the whole record.</summary>
    private static IEnumerable<(string Key, string Value)> Pax(byte[] data)
    {
        var list = new List<(string, string)>();
        int p = 0;
        while (p < data.Length)
        {
            if (data[p] == 0) break; // some writers pad with NULs
            int space = Array.IndexOf(data, (byte)' ', p);
            if (space < 0 || space - p > 10) throw new CorruptDataException("damaged data (a TAR extended header record is malformed)");
            if (!int.TryParse(Encoding.ASCII.GetString(data, p, space - p), NumberStyles.None, CultureInfo.InvariantCulture, out int len)
                || len <= space - p + 1 || p + len > data.Length || data[p + len - 1] != (byte)'\n')
                throw new CorruptDataException("damaged data (a TAR extended header record is malformed)");
            var record = data.AsSpan(space + 1, p + len - 1 - (space + 1));
            int eq = record.IndexOf((byte)'=');
            if (eq <= 0) throw new CorruptDataException("damaged data (a TAR extended header record is malformed)");
            list.Add((Encoding.UTF8.GetString(record[..eq]), Decode(record[(eq + 1)..])));
            p += len;
        }
        return list;
    }

    private sealed class DataStream(TarHeaderReader reader) : ReadOnlyStream
    {
        public override int Read(Span<byte> buffer) => reader.ReadData(buffer);
    }
}
