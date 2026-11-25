using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Osmium.Core.Codecs.Cm2;

/// <summary>
/// Model-selection flags for cm2. Order-0 and order-1 models are always active; everything else can be toggled by
/// the planner and its parameter search. The bit values match cm's; X86 is new.
/// </summary>
[Flags]
public enum Cm2Models
{
    None = 0,
    Order2 = 1 << 0,
    Order3 = 1 << 1,
    Order4 = 1 << 2,
    Order6 = 1 << 3,
    Word = 1 << 4,
    WordPair = 1 << 5,
    SparseA = 1 << 6,   // bytes -2,-3
    SparseB = 1 << 7,   // bytes -4,-8 (32-bit records)
    SparseC = 1 << 8,   // bytes -2,-4 (16-bit samples)
    Match = 1 << 9,
    Order5 = 1 << 10,
    Order8 = 1 << 11,
    Record = 1 << 12,   // bytes at -stride and -2*stride (needs RecordStride > 0)
    X86 = 1 << 13,      // x86/x64 instruction-structure contexts (Cm2X86 decoder)

    Text = Order2 | Order3 | Order4 | Order5 | Order6 | Word | WordPair | Match,
    Binary = Order2 | Order3 | Order4 | Order6 | SparseA | SparseB | SparseC | Match,
    Default = Order2 | Order3 | Order4 | Order6 | Word | WordPair | SparseA | SparseB | Match,
    Exe = Binary | X86,
    All = Order2 | Order3 | Order4 | Order5 | Order6 | Order8 | Word | WordPair | SparseA | SparseB | SparseC | Match | X86,
}

/// <summary>Tunable parameters. Every field is serialized into the pipeline descriptor (via Cm2Method).</summary>
public sealed record Cm2Options
{
    /// <summary>log2 of the number of 64-byte hash-table lines (four 16-byte slots each). Memory = 64 &lt;&lt; TableBits bytes.</summary>
    public int TableBits { get; init; } = 22;
    public Cm2Models Models { get; init; } = Cm2Models.Default;
    /// <summary>Adaptation limit of the direct order-0/1 counters (higher = more stationary).</summary>
    public int CounterLimit { get; init; } = 255;
    public int MixerRate { get; init; } = 16;
    public int FinalMixerRate { get; init; } = 10;
    public int MatchMinLen { get; init; } = 6;
    public int MatchBits { get; init; } = 22;
    public int ApmRate { get; init; } = 7;
    /// <summary>Record length for the record model (0 = off). Set by the analyzer when it detects a stride.</summary>
    public int RecordStride { get; init; }

    /// <summary>Model memory: hash table + match table + direct tables, APMs and mixers (history excluded).</summary>
    public long EstimatedMemory => (64L << TableBits) + (4L << MatchBits) + (6L << 20);

    /// <summary>Returns a copy whose table sizes are clamped to what a block of <paramref name="length"/> bytes can
    /// use. Encoder and decoder both call this with the same length, so the result is identical on both sides.</summary>
    public Cm2Options FitTo(long length)
    {
        int lg = 0;
        while ((1L << lg) < Math.Max(1, length)) lg++;
        return this with
        {
            TableBits = Math.Min(TableBits, Math.Max(16, lg + 4)),
            MatchBits = Math.Min(MatchBits, Math.Max(16, lg + 1)),
        };
    }

    public Cm2Options Validate()
    {
        static void Range(string n, int v, int lo, int hi)
        {
            if (v < lo || v > hi) throw new ArgumentOutOfRangeException(n, v, $"{n} must be in [{lo},{hi}].");
        }
        Range(nameof(TableBits), TableBits, 12, Cm2Method.MaxMem); // the table's byte offsets must fit an int
        Range(nameof(CounterLimit), CounterLimit, 2, 1023);
        Range(nameof(MixerRate), MixerRate, 1, 64);
        Range(nameof(FinalMixerRate), FinalMixerRate, 1, 64);
        Range(nameof(MatchMinLen), MatchMinLen, 3, 32);
        Range(nameof(MatchBits), MatchBits, 10, 26);
        Range(nameof(ApmRate), ApmRate, 4, 9);
        Range(nameof(RecordStride), RecordStride, 0, 1 << 16);
        return this;
    }
}

/// <summary>
/// "Osmium CM2" (method id 8): the second-generation bitwise context-mixing compressor, with bit-history (indirect)
/// context models, x86/x64 instruction contexts and a wider mixer (see <see cref="Cm2Predictor"/>). It shares cm's
/// stream framing: no header, the arithmetic coder's bytes only. All arithmetic is integer-only so that encoder and
/// decoder are bit-identical on every CPU architecture. Symmetric: decoding costs the same time and memory as
/// encoding.
/// </summary>
public static class Cm2Codec
{
    private const int ProgressInterval = 1 << 16;

    public static byte[] Encode(ReadOnlyMemory<byte> input, Cm2Options options,
        CancellationToken ct = default, Action<long>? progress = null)
    {
        options.Validate();
        byte[] hist = AsArray(input);
        int n = input.Length;
        var enc = new Cm2Encoder(Math.Max(64, n / 3));
        var model = new Cm2Predictor(options, hist, writeHistory: false);
        for (int i = 0; i < n; i++)
        {
            int c = hist[i];
            for (int b = 7; b >= 0; b--)
            {
                int bit = (c >> b) & 1;
                enc.Encode(bit, model.P());
                model.Update(bit);
            }
            if ((i & (ProgressInterval - 1)) == ProgressInterval - 1)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(i + 1);
            }
        }
        enc.Flush();
        progress?.Invoke(n);
        return enc.ToArray();
    }

    public static void Decode(ReadOnlyMemory<byte> input, Memory<byte> output, Cm2Options options,
        CancellationToken ct = default, Action<long>? progress = null)
    {
        options.Validate();
        int n = output.Length;
        // The predictor needs random access to already-decoded bytes; decode into an array we own
        // unless the caller handed us an array-backed buffer starting at offset 0.
        byte[] hist;
        bool direct = MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)output, out var seg) && seg.Offset == 0 && seg.Array!.Length >= n;
        hist = direct ? seg.Array! : new byte[n];
        var dec = new Cm2Decoder(input);
        var model = new Cm2Predictor(options, hist, writeHistory: true);
        for (int i = 0; i < n; i++)
        {
            for (int b = 0; b < 8; b++)
            {
                int bit = dec.Decode(model.P());
                model.Update(bit);
            }
            if ((i & (ProgressInterval - 1)) == ProgressInterval - 1)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(i + 1);
            }
        }
        if (!dec.ConsumedExactly) throw new InvalidDataException("CM2 stream is truncated or has trailing garbage.");
        if (!direct) hist.AsSpan(0, n).CopyTo(output.Span);
        progress?.Invoke(n);
    }

    private static byte[] AsArray(ReadOnlyMemory<byte> m)
    {
        if (MemoryMarshal.TryGetArray(m, out var seg) && seg.Offset == 0 && seg.Array is not null) return seg.Array;
        return m.ToArray();
    }
}

/// <summary>Integer logistic helpers (12-bit probabilities, stretch domain ±2047).</summary>
internal static class Cm2Logistic
{
    // floor(4096 / (1 + e^-x)) for x = -8 .. 8 step 0.5, clamped to [1, 4095].
    private static readonly int[] T =
    [
        1, 2, 3, 6, 10, 16, 27, 45, 73, 120, 194, 310, 488, 747, 1101, 1546, 2048,
        2549, 2994, 3348, 3607, 3785, 3901, 3975, 4022, 4050, 4068, 4079, 4085, 4089, 4092, 4093, 4094,
    ];

    private static readonly short[] StretchTable = BuildStretch();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Squash(int d)
    {
        if (d > 2047) return 4095;
        if (d < -2047) return 1;
        int w = d & 127;
        int i = (d >> 7) + 16;
        return (T[i] * (128 - w) + T[i + 1] * w + 64) >> 7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Stretch(int p) => StretchTable[p];

    private static short[] BuildStretch()
    {
        var t = new short[4096];
        int pi = 0;
        for (int x = -2047; x <= 2047; x++)
        {
            int v = Squash(x);
            for (int j = pi; j <= v; j++) t[j] = (short)x;
            pi = v + 1;
        }
        for (int j = pi; j < 4096; j++) t[j] = 2047;
        return t;
    }
}

/// <summary>Carry-less 32-bit binary arithmetic coder.</summary>
internal sealed class Cm2Encoder
{
    private uint _x1, _x2 = 0xFFFFFFFF;
    private byte[] _buf;
    private int _len;

    public Cm2Encoder(int capacity) => _buf = new byte[capacity];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encode(int bit, int p12)
    {
        uint xmid = _x1 + (uint)(((ulong)(_x2 - _x1) * (uint)p12) >> 12);
        if (bit != 0) _x2 = xmid; else _x1 = xmid + 1;
        while (((_x1 ^ _x2) & 0xFF000000) == 0)
        {
            Put((byte)(_x2 >> 24));
            _x1 <<= 8;
            _x2 = (_x2 << 8) | 255;
        }
    }

    public void Flush()
    {
        // Emit all of x1 so the decoder's window lands exactly inside [x1, x2].
        Put((byte)(_x1 >> 24)); Put((byte)(_x1 >> 16)); Put((byte)(_x1 >> 8)); Put((byte)_x1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Put(byte b)
    {
        if (_len == _buf.Length) Array.Resize(ref _buf, Math.Max(64, _buf.Length * 2));
        _buf[_len++] = b;
    }

    public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();
}

internal sealed class Cm2Decoder
{
    private uint _x1, _x2 = 0xFFFFFFFF, _x;
    private readonly byte[] _buf;
    private readonly int _len;
    private int _pos;

    public Cm2Decoder(ReadOnlyMemory<byte> input)
    {
        if (MemoryMarshal.TryGetArray(input, out var seg) && seg.Offset == 0) { _buf = seg.Array!; _len = seg.Count; }
        else { _buf = input.ToArray(); _len = _buf.Length; }
        for (int i = 0; i < 4; i++) _x = (_x << 8) | Next();
    }

    /// <summary>True when the stream was consumed exactly (encoder emits shifts + 4 bytes; decoder reads 4 + shifts).</summary>
    public bool ConsumedExactly => _pos == _len;

    /// <summary>The next stream byte. A valid stream is never read past its end, so a read past the end means the stream
    /// is truncated: fail at once, so the work done follows the payload, not the declared size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Next()
    {
        if (_pos < _len) return _buf[_pos++];
        return Truncated();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint Truncated() => throw new InvalidDataException("CM2 stream is truncated.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Decode(int p12)
    {
        uint xmid = _x1 + (uint)(((ulong)(_x2 - _x1) * (uint)p12) >> 12);
        int bit;
        if (_x <= xmid) { bit = 1; _x2 = xmid; } else { bit = 0; _x1 = xmid + 1; }
        while (((_x1 ^ _x2) & 0xFF000000) == 0)
        {
            _x1 <<= 8;
            _x2 = (_x2 << 8) | 255;
            _x = (_x << 8) | Next();
        }
        return bit;
    }
}

/// <summary>Adaptive probability map with interpolation (secondary symbol estimation).</summary>
internal sealed class Cm2Apm
{
    private readonly ushort[] _t;
    private readonly int _rate;
    private int _idx;

    public Cm2Apm(int contexts, int rate)
    {
        _rate = rate;
        _t = new ushort[contexts * 33];
        for (int i = 0; i < contexts; i++)
            for (int j = 0; j < 33; j++)
                _t[i * 33 + j] = (ushort)(Cm2Logistic.Squash((j - 16) * 128) * 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int P(int pr, int cx)
    {
        int s = Cm2Logistic.Stretch(pr) + 2048; // 1..4095
        int lo = s >> 7, w = s & 127;
        int b = cx * 33 + lo;
        _idx = w < 64 ? b : b + 1;
        return (_t[b] * (128 - w) + _t[b + 1] * w) >> 11;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(int bit)
    {
        int g = (bit << 16) + (bit << _rate) - bit - bit;
        int t = _t[_idx];
        _t[_idx] = (ushort)(t + ((g - t) >> _rate));
    }
}
