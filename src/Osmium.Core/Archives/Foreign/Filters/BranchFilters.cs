using System.Buffers.Binary;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Archives.Foreign.Filters;

/// <summary>
/// The branch-call filters of 7z and xz (BCJ for x86, ARM, ARM Thumb, ARM64, PowerPC, SPARC), decoding direction,
/// written from the published algorithms. Each converts in place and returns how many bytes it finished; the rest
/// waits for more input (and, at the end of the data, passes through unchanged).
/// </summary>
internal abstract class BranchConverter
{
    /// <summary>Converts the start of <paramref name="data"/>, whose first byte is at stream position
    /// <paramref name="ip"/>; returns the number of bytes done.</summary>
    public abstract int Convert(Span<byte> data, uint ip, bool encoding = false);

    public static BranchConverter X86() => new X86Converter();
    public static BranchConverter Arm() => new ArmConverter();
    public static BranchConverter ArmThumb() => new ArmThumbConverter();
    public static BranchConverter Arm64() => new Arm64Converter();
    public static BranchConverter PowerPc() => new PowerPcConverter();
    public static BranchConverter Sparc() => new SparcConverter();

    private sealed class X86Converter : BranchConverter
    {
        private static ReadOnlySpan<byte> MaskToAllowed => [1, 1, 1, 0, 1, 0, 0, 0];
        private static ReadOnlySpan<byte> MaskToBitNumber => [0, 1, 2, 2, 3, 3, 3, 3];
        private uint _state;

        private static bool Test86MsByte(byte b) => b == 0 || b == 0xFF;

        public override int Convert(Span<byte> data, uint ip, bool encoding = false)
        {
            int size = data.Length;
            if (size < 5) return 0;
            int bufferPos = 0;
            uint prevMask = _state & 7;
            ip += 5;
            int prevPosT = -1;
            while (true)
            {
                int p = bufferPos, limit = size - 4;
                while (p < limit && (data[p] & 0xFE) != 0xE8) p++;
                bufferPos = p;
                if (p >= limit) break;
                int distance = bufferPos - prevPosT;
                if (distance > 3) prevMask = 0;
                else
                {
                    prevMask = (prevMask << (distance - 1)) & 7;
                    if (prevMask != 0)
                    {
                        byte b = data[p + 4 - MaskToBitNumber[(int)prevMask]];
                        if (MaskToAllowed[(int)prevMask] == 0 || Test86MsByte(b))
                        {
                            prevPosT = bufferPos;
                            prevMask = ((prevMask << 1) & 7) | 1;
                            bufferPos++;
                            continue;
                        }
                    }
                }
                prevPosT = bufferPos;
                if (Test86MsByte(data[p + 4]))
                {
                    uint src = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 1)..]);
                    uint dest;
                    while (true)
                    {
                        dest = encoding ? ip + (uint)bufferPos + src : src - (ip + (uint)bufferPos);
                        if (prevMask == 0) break;
                        int index = MaskToBitNumber[(int)prevMask] * 8;
                        byte b = (byte)(dest >> (24 - index));
                        if (!Test86MsByte(b)) break;
                        src = dest ^ (uint)((1L << (32 - index)) - 1);
                    }
                    data[p + 4] = (byte)~(((dest >> 24) & 1) - 1);
                    data[p + 3] = (byte)(dest >> 16);
                    data[p + 2] = (byte)(dest >> 8);
                    data[p + 1] = (byte)dest;
                    bufferPos += 5;
                }
                else
                {
                    prevMask = ((prevMask << 1) & 7) | 1;
                    bufferPos++;
                }
            }
            int tail = bufferPos - prevPosT;
            _state = tail > 3 ? 0 : (prevMask << (tail - 1)) & 7;
            return bufferPos;
        }
    }

    private sealed class ArmConverter : BranchConverter
    {
        public override int Convert(Span<byte> data, uint ip, bool encoding = false)
        {
            int size = data.Length & ~3, i;
            ip += 8;
            for (i = 0; i < size; i += 4)
            {
                if (data[i + 3] != 0xEB) continue;
                uint src = ((uint)data[i + 2] << 16 | (uint)data[i + 1] << 8 | data[i]) << 2;
                uint dest = (encoding ? ip + (uint)i + src : src - (ip + (uint)i)) >> 2;
                data[i + 2] = (byte)(dest >> 16);
                data[i + 1] = (byte)(dest >> 8);
                data[i] = (byte)dest;
            }
            return i;
        }
    }

    private sealed class ArmThumbConverter : BranchConverter
    {
        public override int Convert(Span<byte> data, uint ip, bool encoding = false)
        {
            int size = data.Length & ~1;
            if (size < 4) return 0;
            size -= 4;
            ip += 4;
            int i;
            for (i = 0; i <= size; i += 2)
            {
                if ((data[i + 1] & 0xF8) != 0xF0 || (data[i + 3] & 0xF8) != 0xF8) continue;
                uint src = (((uint)data[i + 1] & 7) << 19 | (uint)data[i] << 11 | ((uint)data[i + 3] & 7) << 8 | data[i + 2]) << 1;
                uint dest = (encoding ? ip + (uint)i + src : src - (ip + (uint)i)) >> 1;
                data[i + 1] = (byte)(0xF0 | ((dest >> 19) & 7));
                data[i] = (byte)(dest >> 11);
                data[i + 3] = (byte)(0xF8 | ((dest >> 8) & 7));
                data[i + 2] = (byte)dest;
                i += 2;
            }
            return i;
        }
    }

    private sealed class Arm64Converter : BranchConverter
    {
        public override int Convert(Span<byte> data, uint ip, bool encoding = false)
        {
            int i;
            for (i = 0; i + 4 <= data.Length; i += 4)
            {
                uint pc = ip + (uint)i;
                uint instr = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
                if ((instr >> 26) == 0x25)
                {
                    uint src = instr;
                    pc >>= 2;
                    if (!encoding) pc = 0u - pc;
                    instr = 0x94000000 | ((src + pc) & 0x03FFFFFF);
                    BinaryPrimitives.WriteUInt32LittleEndian(data[i..], instr);
                }
                else if ((instr & 0x9F000000) == 0x90000000)
                {
                    uint src = ((instr >> 29) & 3) | ((instr >> 3) & 0x001FFFFC);
                    if (((src + 0x00020000) & 0x001C0000) != 0) continue;
                    instr &= 0x9000001F;
                    pc >>= 12;
                    if (!encoding) pc = 0u - pc;
                    uint dest = src + pc;
                    instr |= (dest & 3) << 29;
                    instr |= (dest & 0x0003FFFC) << 3;
                    instr |= (0u - (dest & 0x00020000)) & 0x00E00000;
                    BinaryPrimitives.WriteUInt32LittleEndian(data[i..], instr);
                }
            }
            return i;
        }
    }

    private sealed class PowerPcConverter : BranchConverter
    {
        public override int Convert(Span<byte> data, uint ip, bool encoding = false)
        {
            int size = data.Length & ~3, i;
            for (i = 0; i < size; i += 4)
            {
                if ((data[i] >> 2) != 0x12 || (data[i + 3] & 3) != 1) continue;
                uint src = ((uint)data[i] & 3) << 24 | (uint)data[i + 1] << 16 | (uint)data[i + 2] << 8 | ((uint)data[i + 3] & ~3u);
                uint dest = encoding ? ip + (uint)i + src : src - (ip + (uint)i);
                data[i] = (byte)(0x48 | ((dest >> 24) & 3));
                data[i + 1] = (byte)(dest >> 16);
                data[i + 2] = (byte)(dest >> 8);
                data[i + 3] = (byte)(((uint)data[i + 3] & 3u) | (dest & ~3u));
            }
            return i;
        }
    }

    private sealed class SparcConverter : BranchConverter
    {
        public override int Convert(Span<byte> data, uint ip, bool encoding = false)
        {
            int size = data.Length & ~3, i;
            for (i = 0; i < size; i += 4)
            {
                if (!((data[i] == 0x40 && (data[i + 1] & 0xC0) == 0) || (data[i] == 0x7F && (data[i + 1] & 0xC0) == 0xC0))) continue;
                uint src = BinaryPrimitives.ReadUInt32BigEndian(data[i..]) << 2;
                uint dest = (encoding ? ip + (uint)i + src : src - (ip + (uint)i)) >> 2;
                dest = (((0u - ((dest >> 22) & 1)) << 22) & 0x3FFFFFFF) | (dest & 0x3FFFFF) | 0x40000000;
                BinaryPrimitives.WriteUInt32BigEndian(data[i..], dest);
            }
            return i;
        }
    }
}

/// <summary>Runs a <see cref="BranchConverter"/> over a stream.</summary>
internal sealed class BranchFilterStream(Stream inner, BranchConverter converter, uint startOffset = 0) : ReadOnlyStream
{
    private readonly byte[] _buf = new byte[1 << 16];
    private int _start, _converted, _end;
    private uint _ip = startOffset;
    private bool _eof;

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        while (_start == _converted)
        {
            if (_eof) return 0;
            // Keep the bytes not yet converted, then fill up.
            int keep = _end - _converted;
            Buffer.BlockCopy(_buf, _converted, _buf, 0, keep);
            _start = _converted = 0;
            _end = keep;
            while (_end < _buf.Length)
            {
                int n = inner.Read(_buf, _end, _buf.Length - _end);
                if (n <= 0) { _eof = true; break; }
                _end += n;
            }
            int done = converter.Convert(_buf.AsSpan(0, _end), _ip);
            _ip += (uint)done;
            _converted = _eof ? _end : done;
            if (_converted == 0 && !_eof && _end == _buf.Length) throw new CorruptDataException("branch filter made no progress");
        }
        int count = Math.Min(buffer.Length, _converted - _start);
        _buf.AsSpan(_start, count).CopyTo(buffer);
        _start += count;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The Delta filter: each byte is stored as the difference to the byte <paramref name="distance"/> before it.</summary>
internal sealed class DeltaFilterStream(Stream inner, int distance) : ReadOnlyStream
{
    private readonly byte[] _history = new byte[256];
    private int _pos;

    public override int Read(Span<byte> buffer)
    {
        int n = inner.Read(buffer);
        for (int i = 0; i < n; i++)
        {
            byte b = (byte)(buffer[i] + _history[(_pos - distance) & 0xFF]);
            _history[_pos & 0xFF] = b;
            _pos++;
            buffer[i] = b;
        }
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// 7z's BCJ2 decoder: x86 CALL and JMP targets were moved into two separate streams, and a range coder says which
/// E8/E9/Jcc opcodes were converted. Inputs: main, call, jump and range-coder streams; output size known.
/// </summary>
internal sealed class Bcj2DecoderStream : ReadOnlyStream
{
    private const int NumBitModelTotalBits = 11, BitModelTotal = 1 << NumBitModelTotalBits, NumMoveBits = 5;
    private const uint TopValue = 1u << 24;
    private readonly Stream _main, _call, _jump, _rc;
    private readonly ushort[] _probs = new ushort[256 + 2];
    private readonly long _outSize;
    private long _outPos;
    private uint _range = 0xFFFFFFFF, _code;
    private byte _prevByte;
    private readonly byte[] _pending = new byte[4];
    private int _pendingStart, _pendingEnd;
    private bool _initialized;

    public Bcj2DecoderStream(Stream main, Stream call, Stream jump, Stream rangeCoder, long outSize)
    {
        _main = main; _call = call; _jump = jump; _rc = rangeCoder;
        _outSize = outSize;
        Array.Fill(_probs, (ushort)(BitModelTotal >> 1));
    }

    private static byte Next(Stream s, string what)
    {
        int b = s.ReadByte();
        if (b < 0) throw new CorruptDataException($"BCJ2: the {what} stream ends early");
        return (byte)b;
    }

    private static bool IsJ(byte b0, byte b1) => (b1 & 0xFE) == 0xE8 || (b0 == 0x0F && (b1 & 0xF0) == 0x80);

    public override int Read(Span<byte> buffer)
    {
        if (!_initialized)
        {
            _initialized = true;
            for (int i = 0; i < 5; i++) _code = (_code << 8) | Next(_rc, "range coder");
        }
        int written = 0;
        while (written < buffer.Length && (_pendingStart < _pendingEnd || _outPos < _outSize))
        {
            if (_pendingStart < _pendingEnd)
            {
                buffer[written++] = _pending[_pendingStart++];
                continue;
            }
            int m = _main.ReadByte();
            if (m < 0) throw new CorruptDataException("BCJ2: the main stream ends early");
            byte b = (byte)m;
            buffer[written++] = b;
            _outPos++;
            if (!IsJ(_prevByte, b)) { _prevByte = b; continue; }
            if (_outPos == _outSize) break;
            int probIndex = b == 0xE8 ? _prevByte : b == 0xE9 ? 256 : 257;
            uint ttt = _probs[probIndex];
            uint bound = (_range >> NumBitModelTotalBits) * ttt;
            if (_code < bound)
            {
                _range = bound;
                _probs[probIndex] = (ushort)(ttt + ((BitModelTotal - ttt) >> NumMoveBits));
                Normalize();
                _prevByte = b;
                continue;
            }
            _range -= bound;
            _code -= bound;
            _probs[probIndex] = (ushort)(ttt - (ttt >> NumMoveBits));
            Normalize();
            var src = b == 0xE8 ? _call : _jump;
            uint v = (uint)Next(src, b == 0xE8 ? "call" : "jump") << 24 | (uint)Next(src, "address") << 16 | (uint)Next(src, "address") << 8 | Next(src, "address");
            uint dest = v - (uint)(_outPos + 4);
            BinaryPrimitives.WriteUInt32LittleEndian(_pending, dest);
            long left = _outSize - _outPos;
            _pendingStart = 0;
            _pendingEnd = (int)Math.Min(4, left);
            _outPos += _pendingEnd;
            _prevByte = (byte)(dest >> 24);
        }
        return written;
    }

    private void Normalize()
    {
        if (_range >= TopValue) return;
        _range <<= 8;
        _code = (_code << 8) | Next(_rc, "range coder");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _main.Dispose(); _call.Dispose(); _jump.Dispose(); _rc.Dispose(); }
        base.Dispose(disposing);
    }
}
