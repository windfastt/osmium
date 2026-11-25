using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Osmium.Core.Codecs.Cm;

/// <summary>
/// Model-selection flags for the Osmium context-mixing codec. Order-0 and order-1
/// models are always active; everything else can be toggled by the planner/tuner.
/// </summary>
[Flags]
public enum CmModels
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

    Text = Order2 | Order3 | Order4 | Order5 | Order6 | Word | WordPair | Match,
    Binary = Order2 | Order3 | Order4 | Order6 | SparseA | SparseB | SparseC | Match,
    Default = Order2 | Order3 | Order4 | Order6 | Word | WordPair | SparseA | SparseB | Match,
    All = Order2 | Order3 | Order4 | Order5 | Order6 | Order8 | Word | WordPair | SparseA | SparseB | SparseC | Match,
}

/// <summary>Tunable parameters. Every field is serialized into the pipeline descriptor (via CmMethod).</summary>
public sealed record CmOptions
{
    /// <summary>log2 of the number of 64-byte hash buckets. Memory = 64 &lt;&lt; TableBits bytes.</summary>
    public int TableBits { get; init; } = 22;
    public CmModels Models { get; init; } = CmModels.Default;
    /// <summary>Adaptation limit of the probability counters (higher = more stationary).</summary>
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
    public CmOptions FitTo(long length)
    {
        int lg = 0;
        while ((1L << lg) < Math.Max(1, length)) lg++;
        return this with
        {
            TableBits = Math.Min(TableBits, Math.Max(16, lg + 4)),
            MatchBits = Math.Min(MatchBits, Math.Max(16, lg + 1)),
        };
    }

    public CmOptions Validate()
    {
        static void Range(string n, int v, int lo, int hi)
        {
            if (v < lo || v > hi) throw new ArgumentOutOfRangeException(n, v, $"{n} must be in [{lo},{hi}].");
        }
        Range(nameof(TableBits), TableBits, 12, 26);
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
/// "Osmium CM": an original bitwise context-mixing compressor in the PAQ/lpaq family.
/// All arithmetic is integer-only so that encoder and decoder are bit-identical on
/// every CPU architecture (archives made on x64 decode on ARM64, distributed workers agree).
/// Symmetric: decoding costs the same time and memory as encoding.
/// </summary>
public static class CmCodec
{
    private const int ProgressInterval = 1 << 16;

    public static byte[] Encode(ReadOnlyMemory<byte> input, CmOptions options,
        CancellationToken ct = default, Action<long>? progress = null)
    {
        options.Validate();
        byte[] hist = AsArray(input);
        int n = input.Length;
        var enc = new ArithmeticEncoder(Math.Max(64, n / 3));
        var model = new CmPredictor(options, hist, writeHistory: false);
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

    public static void Decode(ReadOnlyMemory<byte> input, Memory<byte> output, CmOptions options,
        CancellationToken ct = default, Action<long>? progress = null)
    {
        options.Validate();
        int n = output.Length;
        // The predictor needs random access to already-decoded bytes; decode into an array we own
        // unless the caller handed us an array-backed buffer starting at offset 0.
        byte[] hist;
        bool direct = MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)output, out var seg) && seg.Offset == 0 && seg.Array!.Length >= n;
        hist = direct ? seg.Array! : new byte[n];
        var dec = new ArithmeticDecoder(input);
        var model = new CmPredictor(options, hist, writeHistory: true);
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
        if (!dec.ConsumedExactly) throw new InvalidDataException("CM stream is truncated or has trailing garbage.");
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
internal static class Logistic
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
internal sealed class ArithmeticEncoder
{
    private uint _x1, _x2 = 0xFFFFFFFF;
    private byte[] _buf;
    private int _len;

    public ArithmeticEncoder(int capacity) => _buf = new byte[capacity];

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

internal sealed class ArithmeticDecoder
{
    private uint _x1, _x2 = 0xFFFFFFFF, _x;
    private readonly byte[] _buf;
    private readonly int _len;
    private int _pos;

    public ArithmeticDecoder(ReadOnlyMemory<byte> input)
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
    private static uint Truncated() => throw new InvalidDataException("CM stream is truncated.");

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
internal sealed class Apm
{
    private readonly ushort[] _t;
    private readonly int _rate;
    private int _idx;

    public Apm(int contexts, int rate)
    {
        _rate = rate;
        _t = new ushort[contexts * 33];
        for (int i = 0; i < contexts; i++)
            for (int j = 0; j < 33; j++)
                _t[i * 33 + j] = (ushort)(Logistic.Squash((j - 16) * 128) * 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int P(int pr, int cx)
    {
        int s = Logistic.Stretch(pr) + 2048; // 1..4095
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

/// <summary>
/// The bit predictor. Hashed contexts live in 64-byte buckets (1 checksum/priority word +
/// 15 nibble-tree nodes), 2-way associative. Each node is a 22-bit probability + 10-bit count.
/// </summary>
internal sealed class CmPredictor
{
    private enum Ctx { O2, O3, O4, O5, O6, O8, Word, WordPair, SparseA, SparseB, SparseC, Record1, Record2 }

    private const int MixerNeurons = 4;
    private const int InitWeight = 1 << 14;

    private readonly byte[] _hist;
    private readonly bool _writeHistory;
    private int _pos;
    private readonly int _stride;

    // byte/bit state
    private int _c0 = 1, _bitPos, _nib = 1;
    private uint _c4, _c8;

    // counters
    private readonly uint[] _recip = new uint[1024];
    private readonly int _limit;
    private readonly uint[] _o0 = new uint[256];
    private readonly uint[] _o1 = new uint[65536];
    private int _o1Base;

    // hashed contexts
    private readonly uint[] _t;
    private readonly int _tableBits;
    private readonly Ctx[] _ctx;
    private readonly ulong[] _ctxHash;
    private readonly int[] _bucket;
    private int _hits;

    // word model
    private ulong _word, _prevWord;

    // match model
    private readonly bool _useMatch;
    private readonly int[] _mt = [];
    private readonly int _matchBits, _minLen;
    private int _matchPtr, _matchLen, _expected;
    private readonly uint[] _mm = new uint[64];
    private int _mIdx = -1;

    // mixer
    private readonly int _nIn;
    private readonly int[] _x;
    private readonly int[][] _w;
    private readonly int[] _setCount;
    private readonly int[] _sel = new int[MixerNeurons];
    private readonly int[] _s1 = new int[MixerNeurons];
    private readonly int[] _p1 = new int[MixerNeurons];
    private readonly int[] _w2;
    private int _sel2, _pMix;
    private readonly int _lr1, _lr2;

    // learned final stage (per bit position) over the mixer output and the three APMs
    private const int WarmupBytes = 16384;
    private readonly int[] _wf = new int[8 * 5];
    private readonly int[] _xf = new int[5];
    private int _pf;
    // SSE
    private readonly Apm _a1, _a2, _a3;
    private int _pFinal;

    public CmPredictor(CmOptions o, byte[] hist, bool writeHistory)
    {
        _hist = hist;
        _writeHistory = writeHistory;
        _limit = o.CounterLimit;
        for (int n = 0; n < 1024; n++) _recip[n] = (uint)(131072 / (2 * n + 3));

        var list = new List<Ctx>();
        void Add(CmModels f, Ctx c) { if ((o.Models & f) != 0) list.Add(c); }
        Add(CmModels.Order2, Ctx.O2); Add(CmModels.Order3, Ctx.O3); Add(CmModels.Order4, Ctx.O4);
        Add(CmModels.Order5, Ctx.O5); Add(CmModels.Order6, Ctx.O6); Add(CmModels.Order8, Ctx.O8);
        Add(CmModels.Word, Ctx.Word); Add(CmModels.WordPair, Ctx.WordPair);
        Add(CmModels.SparseA, Ctx.SparseA); Add(CmModels.SparseB, Ctx.SparseB); Add(CmModels.SparseC, Ctx.SparseC);
        _stride = o.RecordStride;
        if (_stride > 0 && (o.Models & CmModels.Record) != 0) { list.Add(Ctx.Record1); list.Add(Ctx.Record2); }
        _ctx = list.ToArray();
        _ctxHash = new ulong[_ctx.Length];
        _bucket = new int[_ctx.Length];
        _tableBits = o.TableBits;
        _t = _ctx.Length > 0 ? new uint[(1 << _tableBits) * 16] : [];

        _useMatch = (o.Models & CmModels.Match) != 0;
        _matchBits = o.MatchBits;
        _minLen = o.MatchMinLen;
        if (_useMatch) _mt = new int[1 << _matchBits];

        // inputs: o0, o1, (stretch + confidence) per hashed context, match ×2, bias
        _nIn = 2 + _ctx.Length * 2 + 2 + 1;
        Array.Fill(_wf, 65536 / 4);
        _x = new int[_nIn];
        _setCount = [256, (_ctx.Length + 1) * 8, 33 * 8, 256];
        _w = new int[MixerNeurons][];
        for (int k = 0; k < MixerNeurons; k++)
        {
            _w[k] = new int[_setCount[k] * _nIn];
            Array.Fill(_w[k], InitWeight);
        }
        _w2 = new int[8 * MixerNeurons];
        Array.Fill(_w2, 65536 / MixerNeurons);
        _lr1 = o.MixerRate;
        _lr2 = o.FinalMixerRate;

        _a1 = new Apm(256, o.ApmRate);
        _a2 = new Apm(65536, o.ApmRate);
        _a3 = new Apm(33 * 256, o.ApmRate);

        ComputeContextHashes();
        LookupBuckets();
    }

    // ---------------------------------------------------------------- counters

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SlotP(uint s) => s == 0 ? 2048 : (int)(s >> 20);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint UpdSlot(uint s, int bit, int limit)
    {
        int p22, n;
        if (s == 0) { p22 = 1 << 21; n = 0; }
        else { p22 = (int)(s >> 10); n = (int)(s & 1023); }
        int target = bit != 0 ? (1 << 22) - 1 : 0;
        p22 += (int)(((long)(target - p22) * _recip[n]) >> 16);
        if (n < limit) n++;
        return ((uint)p22 << 10) | (uint)n;
    }

    // ---------------------------------------------------------------- hashing

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong x)
    {
        x ^= x >> 33; x *= 0xFF51AFD7ED558CCDUL;
        x ^= x >> 33; x *= 0xC4CEB9FE1A85EC53UL;
        x ^= x >> 33;
        return x;
    }

    private void ComputeContextHashes()
    {
        uint c4 = _c4, c8 = _c8;
        ulong r1 = 0, r2 = 0;
        if (_stride > 0)
        {
            int p1 = _pos - _stride, p2 = _pos - 2 * _stride;
            r1 = p1 >= 0 ? _hist[p1] + 1u : 0u;
            r2 = p2 >= 0 ? _hist[p2] + 1u : 0u;
        }
        for (int k = 0; k < _ctx.Length; k++)
        {
            ulong h = _ctx[k] switch
            {
                Ctx.O2 => c4 & 0xFFFF,
                Ctx.O3 => c4 & 0xFFFFFF,
                Ctx.O4 => c4,
                Ctx.O5 => c4 | ((ulong)(c8 & 0xFF) << 32),
                Ctx.O6 => c4 | ((ulong)(c8 & 0xFFFF) << 32),
                Ctx.O8 => c4 | ((ulong)c8 << 32),
                Ctx.Word => _word * 0x9E3779B97F4A7C15UL + (c4 & 0xFF),
                Ctx.WordPair => _word * 0x9E3779B97F4A7C15UL + _prevWord * 0xC2B2AE3D27D4EB4FUL,
                Ctx.SparseA => (c4 >> 8) & 0xFFFF,
                Ctx.SparseB => (c4 >> 24) | ((c8 >> 16) & 0xFF00),
                Ctx.SparseC => ((c4 >> 8) & 0xFF) | ((c4 >> 16) & 0xFF00),
                Ctx.Record1 => r1 | (r2 << 9),
                Ctx.Record2 => r1 | ((ulong)(c4 & 0xFF) << 9),
                _ => 0,
            };
            _ctxHash[k] = h + ((ulong)(k + 1) << 56) + (ulong)(k + 1) * 0x2545F4914F6CDD1DUL;
        }
    }

    private void LookupBuckets()
    {
        int hits = 0;
        ulong nibCtx = _bitPos == 0 ? 0UL : (ulong)_c0 * 0xD6E8FEB86659FD93UL;
        uint[] t = _t;
        int shift = 64 - _tableBits;
        for (int k = 0; k < _ctx.Length; k++)
        {
            ulong m = Mix(_ctxHash[k] + nibCtx);
            int b0 = (int)(m >> shift) << 4;
            uint chk = (uint)(m & 0xFFFF);
            int b1 = b0 ^ 16;
            uint s0 = t[b0];
            if ((s0 & 0xFFFF) == chk)
            {
                if (s0 < 0xFFFF0000) t[b0] = s0 + 0x10000;
                _bucket[k] = b0; hits++; continue;
            }
            uint s1 = t[b1];
            if ((s1 & 0xFFFF) == chk)
            {
                if (s1 < 0xFFFF0000) t[b1] = s1 + 0x10000;
                _bucket[k] = b1; hits++; continue;
            }
            int v, other;
            if ((s0 >> 16) <= (s1 >> 16)) { v = b0; other = b1; } else { v = b1; other = b0; }
            uint so = t[other];
            t[other] = (so & 0xFFFF) | (((so >> 16) >> 1) << 16); // age the survivor
            Array.Clear(t, v + 1, 15);
            t[v] = chk;
            _bucket[k] = v;
        }
        _hits = hits;
        _nib = 1;
    }

    // ---------------------------------------------------------------- byte boundary

    private void OnByte()
    {
        int c = _c0 & 0xFF;
        if (_writeHistory) _hist[_pos] = (byte)c;
        _pos++;
        _c8 = (_c8 << 8) | (_c4 >> 24);
        _c4 = (_c4 << 8) | (uint)c;
        _c0 = 1;
        _bitPos = 0;
        _o1Base = c << 8;

        // word model: letters (case-folded) and UTF-8 continuation/lead bytes form words
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c >= 128)
        {
            int lc = c >= 'A' && c <= 'Z' ? c + 32 : c;
            _word = (_word + (ulong)lc + 1) * 0x2F0B3A4927D4EB4FUL;
        }
        else if (_word != 0)
        {
            _prevWord = _word;
            _word = 0;
        }

        if (_useMatch) UpdateMatch(c);
        ComputeContextHashes();
        LookupBuckets();
    }

    private void UpdateMatch(int c)
    {
        int pos = _pos; // bytes known: [0, pos)
        if (_matchLen > 0)
        {
            if (_hist[_matchPtr] == c) { _matchLen++; _matchPtr++; if (_matchLen > 65535) _matchLen = 65535; }
            else _matchLen = 0;
        }
        if (pos >= _minLen)
        {
            uint h = 0;
            for (int i = pos - _minLen; i < pos; i++) h = (h + _hist[i] + 1) * 0x2F0B3A49u;
            int idx = (int)(h >> (32 - _matchBits));
            if (_matchLen == 0)
            {
                int cand = _mt[idx];
                if (cand > 0)
                {
                    int len = 0;
                    while (len < 64 && len < cand && _hist[cand - 1 - len] == _hist[pos - 1 - len]) len++;
                    if (len >= _minLen) { _matchLen = len; _matchPtr = cand; }
                }
            }
            _mt[idx] = pos;
        }
        _expected = _matchLen > 0 ? _hist[_matchPtr] : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LenBucket(int len) =>
        len < 12 ? len : len < 16 ? 12 : len < 32 ? 13 : len < 64 ? 14 : 15;

    // ---------------------------------------------------------------- predict / update

    public int P()
    {
        int[] x = _x;
        int c0 = _c0;
        x[0] = Logistic.Stretch(SlotP(_o0[c0]));
        x[1] = Logistic.Stretch(SlotP(_o1[_o1Base | c0]));
        int i = 2;
        uint[] t = _t;
        int nib = _nib;
        for (int k = 0; k < _bucket.Length; k++)
        {
            uint sv = t[_bucket[k] + nib];
            int pp = SlotP(sv);
            x[i++] = Logistic.Stretch(pp);
            // confidence input: linear-domain deviation scaled by how often the context was seen
            x[i++] = sv == 0 ? 0 : ((pp - 2048) * (int)Math.Min(sv & 1023, 31)) >> 5;
        }

        int mstate = 0;
        _mIdx = -1;
        if (_matchLen > 0 && ((_expected | 256) >> (8 - _bitPos)) == c0)
        {
            int eb = (_expected >> (7 - _bitPos)) & 1;
            int lb = LenBucket(_matchLen);
            _mIdx = lb * 2 + eb;
            x[i++] = Logistic.Stretch(SlotP(_mm[_mIdx]));
            int strength = Math.Min(_matchLen, 32) * 32;
            x[i++] = eb != 0 ? strength : -strength;
            mstate = 1 + _mIdx;
        }
        else
        {
            x[i++] = 0;
            x[i++] = 0;
        }
        x[i] = 256; // bias

        _sel[0] = c0;
        _sel[1] = _hits * 8 + _bitPos;
        _sel[2] = mstate * 8 + _bitPos;
        _sel[3] = (int)(_c4 & 0xFF);

        long sum2 = 0;
        _sel2 = _bitPos * MixerNeurons;
        for (int k = 0; k < MixerNeurons; k++)
        {
            int[] w = _w[k];
            int b = _sel[k] * _nIn;
            long dot = 0;
            for (int j = 0; j < _nIn; j++) dot += (long)x[j] * w[b + j];
            int s = (int)(dot >> 16);
            if (s > 2047) s = 2047; else if (s < -2047) s = -2047;
            _s1[k] = s;
            _p1[k] = Logistic.Squash(s);
            sum2 += (long)s * _w2[_sel2 + k];
        }
        int s2 = (int)(sum2 >> 16);
        if (s2 > 2047) s2 = 2047; else if (s2 < -2047) s2 = -2047;
        _pMix = Logistic.Squash(s2);

        int pa = _a1.P(_pMix, c0);
        int pb = _a2.P(_pMix, c0 | ((int)(_c4 & 0xFF) << 8));
        int pc = _a3.P(_pMix, (_mIdx + 1) * 256 + c0);
        _xf[0] = Logistic.Stretch(_pMix); _xf[1] = Logistic.Stretch(pa); _xf[2] = Logistic.Stretch(pb); _xf[3] = Logistic.Stretch(pc); _xf[4] = 256;
        long d = 0;
        int bb = _bitPos * 5;
        for (int j = 0; j < 5; j++) d += (long)_xf[j] * _wf[bb + j];
        int sd = (int)(d >> 16);
        if (sd > 2047) sd = 2047; else if (sd < -2047) sd = -2047;
        _pf = Logistic.Squash(sd);
        int p = (_pf * 3 + pb + 2) >> 2;
        if (p < 1) p = 1; else if (p > 4095) p = 4095;
        _pFinal = p;
        return p;
    }

    public void Update(int bit)
    {
        // counters
        int c0 = _c0;
        _o0[c0] = UpdSlot(_o0[c0], bit, _limit);
        int o1 = _o1Base | c0;
        _o1[o1] = UpdSlot(_o1[o1], bit, _limit);
        uint[] t = _t;
        int nib = _nib;
        for (int k = 0; k < _bucket.Length; k++)
        {
            int idx = _bucket[k] + nib;
            t[idx] = UpdSlot(t[idx], bit, _limit);
        }
        if (_mIdx >= 0) _mm[_mIdx] = UpdSlot(_mm[_mIdx], bit, 1023);

        // mixers
        int[] x = _x;
        int target = bit << 12;
        for (int k = 0; k < MixerNeurons; k++)
        {
            // learning-rate warm-up: up to 4x during the first ~16 KiB, decaying to MixerRate
            int lr = _lr1 + (int)((3L * _lr1 * WarmupBytes) / (WarmupBytes + _pos));
            int err = (target - _p1[k]) * lr;
            int[] w = _w[k];
            int b = _sel[k] * _nIn;
            for (int j = 0; j < _nIn; j++)
            {
                int nw = w[b + j] + ((x[j] * err + 0x8000) >> 16);
                w[b + j] = nw > (1 << 23) ? (1 << 23) : nw < -(1 << 23) ? -(1 << 23) : nw;
            }
        }
        {
            int err = (target - _pMix) * _lr2;
            for (int k = 0; k < MixerNeurons; k++)
            {
                int nw = _w2[_sel2 + k] + ((_s1[k] * err + 0x8000) >> 16);
                _w2[_sel2 + k] = nw > (1 << 23) ? (1 << 23) : nw < -(1 << 23) ? -(1 << 23) : nw;
            }
        }
        _a1.Update(bit);
        _a2.Update(bit);
        _a3.Update(bit);
        {
            int errf = (target - _pf) * 6;
            int bb = _bitPos * 5;
            for (int j = 0; j < 5; j++) _wf[bb + j] += (_xf[j] * errf + 0x8000) >> 16;
        }

        // advance bit state
        _c0 = (c0 << 1) | bit;
        _nib = (_nib << 1) | bit;
        _bitPos++;
        if (_bitPos == 8) OnByte();
        else if (_bitPos == 4) LookupBuckets();
    }
}
