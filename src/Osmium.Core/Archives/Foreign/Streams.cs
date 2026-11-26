using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Archives.Foreign;

/// <summary>A forward-only readable stream; the base of the decoding streams here.</summary>
internal abstract class ReadOnlyStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public abstract override int Read(Span<byte> buffer);
    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }
}

/// <summary>
/// A seekable read-only view of <c>[offset, offset + length)</c> of a file, read with positional reads (so several
/// views of one handle never disturb each other), buffered. <see cref="BytesRead"/> counts the file bytes read.
/// </summary>
internal sealed class FileSlice : Stream
{
    private readonly SafeFileHandle _handle;
    private readonly long _offset;
    private readonly byte[] _buffer;
    private long _bufferStart = -1;
    private int _bufferLength;
    private long _position;

    public FileSlice(SafeFileHandle handle, long offset, long length, int bufferSize = 1 << 16)
    {
        if (offset < 0 || length < 0) throw new CorruptDataException("damaged data (an entry lies outside the file)");
        _handle = handle;
        _offset = offset;
        Length = Math.Max(0, length);
        _buffer = new byte[(int)Math.Min(bufferSize, Math.Max(16, Length))];
    }

    public SafeFileHandle Handle => _handle;

    /// <summary>Called with the number of file bytes each underlying read returned (progress by archive bytes).</summary>
    public Action<long>? OnRead { get; set; }
    public long BytesRead { get; private set; }
    public override long Length { get; }
    public long Remaining => Length - _position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        if (destination.IsEmpty || _position >= Length) return 0;
        int want = (int)Math.Min(destination.Length, Length - _position);
        if (_bufferStart < 0 || _position < _bufferStart || _position >= _bufferStart + _bufferLength)
        {
            if (want >= _buffer.Length)
            {
                int direct = RandomAccess.Read(_handle, destination[..want], _offset + _position);
                Account(direct);
                if (direct <= 0) throw Truncated();
                _position += direct;
                return direct;
            }
            Fill();
        }
        int start = (int)(_position - _bufferStart);
        int n = Math.Min(want, _bufferLength - start);
        _buffer.AsSpan(start, n).CopyTo(destination);
        _position += n;
        return n;
    }

    private void Fill()
    {
        int len = (int)Math.Min(_buffer.Length, Length - _position);
        int got = RandomAccess.Read(_handle, _buffer.AsSpan(0, len), _offset + _position);
        Account(got);
        if (got <= 0) throw Truncated();
        _bufferStart = _position;
        _bufferLength = got;
    }

    private void Account(int n)
    {
        if (n <= 0) return;
        BytesRead += n;
        OnRead?.Invoke(n);
    }

    private static CorruptDataException Truncated() => new("the file is shorter than its headers say (truncated?)");

    /// <summary>Reads exactly <paramref name="destination"/>.Length bytes at <paramref name="position"/>.</summary>
    public void ReadAt(long position, Span<byte> destination)
    {
        Position = position;
        if (position < 0 || position + destination.Length > Length) throw Truncated();
        ReadExactly(destination);
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Counts every byte read from <paramref name="inner"/> towards the limits (expansion, cancellation).</summary>
internal sealed class CountingStream(Stream inner, LimitGuard guard) : ReadOnlyStream
{
    public long Count { get; private set; }

    public override int Read(Span<byte> buffer)
    {
        guard.Token.ThrowIfCancellationRequested();
        int n = inner.Read(buffer);
        Count += n;
        guard.Count(n);
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A sink that counts and drops everything written to it (password checks and Test never keep data).</summary>
internal sealed class DiscardSink : Stream
{
    public long Written { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Written;
    public override long Position { get => Written; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Written += count;
    public override void Write(ReadOnlySpan<byte> buffer) => Written += buffer.Length;
}

/// <summary>
/// Passes through exactly <paramref name="expectedLength"/> bytes and, at the end, compares their CRC-32 with
/// <paramref name="expectedCrc"/> (when given). More or fewer bytes, or another CRC, throw
/// <see cref="CorruptDataException"/>: the reader never sees a clean end of a file that does not verify.
/// </summary>
internal sealed class VerifyingStream(Stream inner, long expectedLength, uint? expectedCrc, string what) : ReadOnlyStream
{
    private readonly Crc32 _crc = new();
    private long _total;
    private bool _done;

    public override int Read(Span<byte> buffer)
    {
        if (_done || buffer.IsEmpty) return 0;
        // Ask for at most one byte more than expected, so an overlong entry is noticed without reading it all.
        long room = expectedLength - _total + 1;
        if (buffer.Length > room) buffer = buffer[..(int)room];
        int n = inner.Read(buffer);
        if (n > 0)
        {
            _total += n;
            if (_total > expectedLength) throw new CorruptDataException($"{what}: more data than the archive says");
            if (expectedCrc is not null) _crc.Append(buffer[..n]);
            return n;
        }
        _done = true;
        if (_total != expectedLength) throw new CorruptDataException($"{what}: {_total:N0} of {expectedLength:N0} bytes (truncated or damaged)");
        if (expectedCrc is { } want && _crc.GetCurrentHashAsUInt32() != want) throw new CorruptDataException($"{what}: CRC mismatch");
        return 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Exactly <paramref name="length"/> bytes of <paramref name="inner"/>: reads stop there, and an early end
/// throws (decoders without an end marker, such as 7z PPMd, rely on the size).</summary>
internal sealed class ExactStream(Stream inner, long length, string what) : ReadOnlyStream
{
    private long _left = length;

    public override int Read(Span<byte> buffer)
    {
        if (_left <= 0 || buffer.IsEmpty) return 0;
        if (buffer.Length > _left) buffer = buffer[..(int)_left];
        int n = inner.Read(buffer);
        if (n <= 0) throw new CorruptDataException($"{what}: the data ends early");
        _left -= n;
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Stops a decoder that keeps reading after its input ended. Some SharpCompress decoders treat the end of their input
/// as bytes of 0xFF and can then loop without producing output (LZMA2 reads a chunk header of -1 bytes, for one).
/// LZMA and LZMA2 never read past the end of valid data, so for them the first read at the end means damage; other
/// decoders may look once or twice, so a few reads are allowed.
/// </summary>
internal sealed class EndGuardStream(Stream inner, int allowedEndReads = 8) : ReadOnlyStream
{
    private int _endReads;

    public override int Read(Span<byte> buffer)
    {
        int n = inner.Read(buffer);
        if (n == 0 && !buffer.IsEmpty) End();
        return n;
    }

    public override int ReadByte()
    {
        int b = inner.ReadByte();
        if (b < 0) End();
        return b;
    }

    private void End()
    {
        if (++_endReads > allowedEndReads) throw new CorruptDataException("damaged data (the compressed data ends early)");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
