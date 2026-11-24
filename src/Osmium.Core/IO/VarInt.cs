using System.Buffers.Binary;
using System.Text;

namespace Osmium.Core.IO;

/// <summary>
/// Unsigned LEB128 varints plus small helpers used by the container format.
/// All multi-byte fixed-width integers in the format are little-endian.
/// </summary>
public static class VarInt
{
    public static void Write(Stream s, ulong value)
    {
        Span<byte> buf = stackalloc byte[10];
        int n = Encode(buf, value);
        s.Write(buf[..n]);
    }

    public static int Encode(Span<byte> dst, ulong value)
    {
        int i = 0;
        while (value >= 0x80)
        {
            dst[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        dst[i++] = (byte)value;
        return i;
    }

    public static ulong Read(Stream s)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0) throw new InvalidDataException("Unexpected end of stream in varint.");
            if (shift == 63 && b > 1) throw new InvalidDataException("Varint overflow.");
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("Varint too long.");
        }
    }

    public static ulong Read(ReadOnlySpan<byte> src, ref int pos)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (pos >= src.Length) throw new InvalidDataException("Unexpected end of buffer in varint.");
            byte b = src[pos++];
            if (shift == 63 && b > 1) throw new InvalidDataException("Varint overflow.");
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("Varint too long.");
        }
    }

    /// <summary>Zig-zag encodes a signed value so small magnitudes stay short.</summary>
    public static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));
    public static long UnZigZag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

    public static int ReadInt(Stream s, int max = int.MaxValue)
    {
        ulong v = Read(s);
        if (v > (ulong)max) throw new InvalidDataException($"Value {v} exceeds limit {max}.");
        return (int)v;
    }
}

/// <summary>Convenience writer for building small metadata blobs (pipeline descriptors, index).</summary>
public sealed class BlobWriter
{
    private readonly MemoryStream _ms = new();

    public BlobWriter U(ulong v) { VarInt.Write(_ms, v); return this; }
    public BlobWriter I(long v) { VarInt.Write(_ms, VarInt.ZigZag(v)); return this; }
    public BlobWriter B(byte v) { _ms.WriteByte(v); return this; }
    public BlobWriter Bool(bool v) { _ms.WriteByte(v ? (byte)1 : (byte)0); return this; }

    public BlobWriter U64(ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        _ms.Write(b);
        return this;
    }

    public BlobWriter Bytes(ReadOnlySpan<byte> data)
    {
        VarInt.Write(_ms, (ulong)data.Length);
        _ms.Write(data);
        return this;
    }

    public BlobWriter Raw(ReadOnlySpan<byte> data) { _ms.Write(data); return this; }

    public BlobWriter Str(string s) => Bytes(Encoding.UTF8.GetBytes(s));

    public int Length => (int)_ms.Length;
    public byte[] ToArray() => _ms.ToArray();
}

/// <summary>Cursor-based reader matching <see cref="BlobWriter"/>.</summary>
public ref struct BlobReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public BlobReader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; }

    public readonly int Position => _pos;
    public readonly bool End => _pos >= _data.Length;
    public readonly int Remaining => _data.Length - _pos;

    public ulong U() => VarInt.Read(_data, ref _pos);

    /// <summary>A varint in [0, <paramref name="max"/>]. A negative <paramref name="max"/> (e.g. "count - 1" of an empty
    /// list) allows no value at all.</summary>
    public int Int(int max = int.MaxValue)
    {
        ulong v = U();
        if (max < 0 || v > (ulong)max) throw new InvalidDataException($"Value {v} exceeds limit {max}.");
        return (int)v;
    }

    public long I() => VarInt.UnZigZag(U());

    public byte B()
    {
        if (_pos >= _data.Length) throw new InvalidDataException("Unexpected end of blob.");
        return _data[_pos++];
    }

    public bool Bool() => B() != 0;

    public ulong U64()
    {
        if (_pos + 8 > _data.Length) throw new InvalidDataException("Unexpected end of blob.");
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_pos, 8));
        _pos += 8;
        return v;
    }

    public ReadOnlySpan<byte> Bytes(int max = 1 << 30)
    {
        int n = Int(max);
        if (_pos + n > _data.Length) throw new InvalidDataException("Blob field overruns buffer.");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public ReadOnlySpan<byte> Raw(int n)
    {
        if (n < 0 || _pos + n > _data.Length) throw new InvalidDataException("Blob field overruns buffer.");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public string Str(int maxBytes = 1 << 16) => Encoding.UTF8.GetString(Bytes(maxBytes));
}
