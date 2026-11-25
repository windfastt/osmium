using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Osmium.Core.Codecs.Cm2;

/// <summary>Integer mixer kernels. Element-wise integer arithmetic only, so SIMD and scalar results are identical.</summary>
internal static class Cm2Mix
{
    public const int WeightLimit = (1 << 19) - 1;

    /// <summary>Σ (x[i]·w[wo+i]) &gt;&gt; 8 over <paramref name="n"/> elements (a multiple of the vector width).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Dot(int[] x, int[] w, int wo, int n)
    {
        var xs = x.AsSpan(0, n);
        var ws = w.AsSpan(wo, n);
        var acc = Vector<int>.Zero;
        for (int i = 0; i < n; i += Vector<int>.Count)
            acc += Vector.ShiftRightArithmetic(new Vector<int>(xs[i..]) * new Vector<int>(ws[i..]), 8);
        return Vector.Sum(acc);
    }

    /// <summary>w[wo+i] += (x[i]·err + 2^15) &gt;&gt; 16, clamped to ±<see cref="WeightLimit"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Train(int[] x, int[] w, int wo, int n, int err)
    {
        var xs = x.AsSpan(0, n);
        var ws = w.AsSpan(wo, n);
        var e = new Vector<int>(err);
        var r = new Vector<int>(1 << 15);
        var hi = new Vector<int>(WeightLimit);
        var lo = -hi;
        for (int i = 0; i < n; i += Vector<int>.Count)
        {
            var nw = new Vector<int>(ws[i..]) + Vector.ShiftRightArithmetic(new Vector<int>(xs[i..]) * e + r, 16);
            Vector.Min(Vector.Max(nw, lo), hi).CopyTo(ws[i..]);
        }
    }
}

/// <summary>
/// The cm2 bit predictor.
/// <list type="bullet">
/// <item>Hashed contexts live in 64-byte lines of four 16-byte slots; a slot is a check byte plus the 15 bit-history
/// states (<see cref="Cm2States"/>) of one nibble tree. A context probes the four slots of its line and, on a miss,
/// replaces the one whose first node has seen the fewest bits.</item>
/// <item>Each model maps its states to probabilities with an adaptive state map indexed by bit position and state.
/// Per hashed context the mixer gets stretch(p), the linear p - 1/2 and, for a history that has seen only one bit
/// value, stretch(p) again.</item>
/// <item>Order 0 and 1 keep direct probability counters (limit = <c>CounterLimit</c>) and order 1 a direct bit history.
/// The match model predicts the bit after the longest recent repeat.</item>
/// <item>First-layer neurons whose weight sets are selected by the partial byte, the number of contexts found, the
/// match state, byte -1, byte -2 and, with the x86 model, the decoder's field and byte index; a per-bit-position
/// final neuron combines them. Three APMs (order 0, order 1, match) and a learned per-bit-position blend refine the
/// result.</item>
/// </list>
/// All arithmetic is integer; prefetching only affects speed.
/// </summary>
internal sealed unsafe class Cm2Predictor
{
    private enum Ctx { O2, O3, O4, O5, O6, O8, Word, WordPair, SparseA, SparseB, SparseC, Record1, Record2, XA, XB, XC, XD }

    private const int MaxNeurons = 6;
    private const int InitWeight = 1 << 14;
    private const int WarmupBytes = 16384;
    private const int StateMapLimit = 1023;
    private const int StateMapStride = 256 * 8; // per model: bit position × state

    private readonly byte[] _hist;
    private readonly bool _writeHistory;
    private int _pos;
    private readonly int _stride;

    // byte/bit state
    private int _c0 = 1, _bitPos, _nib = 1;
    private uint _c4, _c8;

    // direct counters (order 0 and 1) and the order-1 bit history
    private readonly uint[] _recip = new uint[1024];
    private readonly int _limit;
    private readonly uint[] _o0 = new uint[256];
    private readonly uint[] _o1 = new uint[65536];
    private readonly byte[] _h1 = new byte[65536];
    private int _o1Base;

    // hashed contexts
    private readonly byte[] _t;
    private readonly int _tBase;          // offset of the first 64-byte aligned line in _t
    private readonly byte* _tp;           // address of _t[_tBase] (the array is pinned); used only for prefetching
    private readonly int _lineBits;
    private readonly Ctx[] _ctx;
    private readonly ulong[] _ctxHash;
    private readonly ulong[] _lineHash;
    private readonly int[] _line;
    private readonly int[] _slot;
    private int _hits;

    // state maps: model × bit position × state → 22-bit probability + 10-bit count (model 0 = order-1 history)
    private readonly uint[] _sm;
    private readonly int[] _smIdx;

    // word model
    private ulong _word, _prevWord;

    // match model
    private readonly bool _useMatch;
    private readonly int[] _mt = [];
    private readonly int _matchBits, _minLen;
    private int _matchPtr, _matchLen, _expected;
    private readonly uint[] _mm = new uint[64];
    private int _mIdx = -1;

    // x86 instruction decoder (X86 model)
    private readonly Cm2X86? _x86;

    // mixer
    private readonly int _nIn, _nPad;
    private readonly int[] _x;
    private readonly int[] _w;
    private readonly int _neurons;
    private readonly int _byte2Neuron;
    private readonly int[] _wBase = new int[MaxNeurons];
    private readonly int[] _sel = new int[MaxNeurons];
    private readonly int[] _s1 = new int[MaxNeurons];
    private readonly int[] _p1 = new int[MaxNeurons];
    private readonly int[] _w2;
    private int _sel2, _pMix;
    private readonly int _lr1, _lr2;

    // learned final stage (per bit position) over the mixer output and the three APMs
    private readonly int[] _wf = new int[8 * 5];
    private readonly int[] _xf = new int[5];
    private int _pf;
    private readonly Cm2Apm _a1, _a2, _a3;

    public Cm2Predictor(Cm2Options o, byte[] hist, bool writeHistory)
    {
        _hist = hist;
        _writeHistory = writeHistory;
        _limit = o.CounterLimit;
        for (int n = 0; n < 1024; n++) _recip[n] = (uint)(131072 / (2 * n + 3));

        var list = new List<Ctx>();
        void Add(Cm2Models f, Ctx c) { if ((o.Models & f) != 0) list.Add(c); }
        Add(Cm2Models.Order2, Ctx.O2); Add(Cm2Models.Order3, Ctx.O3); Add(Cm2Models.Order4, Ctx.O4);
        Add(Cm2Models.Order5, Ctx.O5); Add(Cm2Models.Order6, Ctx.O6); Add(Cm2Models.Order8, Ctx.O8);
        Add(Cm2Models.Word, Ctx.Word); Add(Cm2Models.WordPair, Ctx.WordPair);
        Add(Cm2Models.SparseA, Ctx.SparseA); Add(Cm2Models.SparseB, Ctx.SparseB); Add(Cm2Models.SparseC, Ctx.SparseC);
        _stride = o.RecordStride;
        if (_stride > 0 && (o.Models & Cm2Models.Record) != 0) { list.Add(Ctx.Record1); list.Add(Ctx.Record2); }
        if ((o.Models & Cm2Models.X86) != 0)
        {
            _x86 = new Cm2X86();
            list.AddRange([Ctx.XA, Ctx.XB, Ctx.XC, Ctx.XD]);
        }
        _ctx = list.ToArray();
        _ctxHash = new ulong[_ctx.Length];
        _lineHash = new ulong[_ctx.Length];
        _line = new int[_ctx.Length];
        _slot = new int[_ctx.Length];
        _lineBits = o.TableBits;
        if (_ctx.Length > 0)
        {
            // Pinned, so lines can be aligned to cache lines (a probe then touches one line) and prefetched.
            _t = GC.AllocateArray<byte>((64 << _lineBits) + 64, pinned: true);
            byte* p0 = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_t));
            _tBase = (int)((64 - ((nint)p0 & 63)) & 63);
            _tp = p0 + _tBase;
        }
        else _t = [];

        int models = _ctx.Length + 1;
        _sm = new uint[models * StateMapStride];
        for (int i = 0; i < _sm.Length; i++)
        {
            int s = i & 255, n0 = Cm2States.N0[s], n1 = Cm2States.N1[s];
            int p = (int)(((2L * n1 + 1) << 12) / (2 * (n0 + n1) + 2));
            _sm[i] = (uint)Math.Clamp(p, 1, 4095) << 20;
        }
        _smIdx = new int[models];

        _useMatch = (o.Models & Cm2Models.Match) != 0;
        _matchBits = o.MatchBits;
        _minLen = o.MatchMinLen;
        if (_useMatch) _mt = new int[1 << _matchBits];

        // inputs: order 0/1 counters, order-1 history, 3 per hashed context, match ×2, bias
        _nIn = 2 + 1 + _ctx.Length * 3 + 2 + 1;
        int vw = Math.Max(16, Vector<int>.Count);
        _nPad = (_nIn + vw - 1) / vw * vw;
        _x = new int[_nPad];
        // weight sets per neuron: partial byte, contexts found × bit, match state × bit, byte -1, [x86 field × index × bit], byte -2
        var sets = new List<int> { 256, (_ctx.Length + 1) * 8, 33 * 8, 256 };
        if (_x86 is not null) sets.Add(10 * 4 * 8);
        _byte2Neuron = sets.Count;
        sets.Add(256);
        _neurons = sets.Count;
        int total = 0;
        for (int k = 0; k < _neurons; k++) { _wBase[k] = total; total += sets[k] * _nPad; }
        _w = new int[total];
        for (int k = 0; k < _neurons; k++)
            for (int s = 0; s < sets[k]; s++)
                Array.Fill(_w, InitWeight, _wBase[k] + s * _nPad, _nIn);
        _w2 = new int[8 * _neurons];
        Array.Fill(_w2, 65536 / _neurons);
        Array.Fill(_wf, 65536 / 4);
        _lr1 = o.MixerRate;
        _lr2 = o.FinalMixerRate;

        _a1 = new Cm2Apm(256, o.ApmRate);
        _a2 = new Cm2Apm(65536, o.ApmRate);
        _a3 = new Cm2Apm(33 * 256, o.ApmRate);

        ComputeContextHashes();
        LookupSlots();
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdSm(int idx, int bit)
    {
        uint e = _sm[idx];
        int n = (int)(e & 1023), p22 = (int)(e >> 10);
        int target = bit != 0 ? (1 << 22) - 1 : 0;
        p22 += (int)(((long)(target - p22) * _recip[n]) >> 16);
        if (n < StateMapLimit) n++;
        _sm[idx] = ((uint)p22 << 10) | (uint)n;
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
        ulong xa = 0, xb = 0, xc = 0, xd = 0;
        if (_x86 is { } d)
        {
            int prefixes = d.Pref & (Cm2X86.P66 | Cm2X86.PRexW | Cm2X86.PVex);
            int pos = Math.Min(d.Pos, 7);
            // XA: field, opcode, ModRM, byte index, operand-size prefixes
            xa = (ulong)(uint)d.Field | ((ulong)(uint)(d.Op + 1) << 4) | ((ulong)(uint)(d.ModRM + 1) << 14) | ((ulong)(uint)pos << 23)
                | ((ulong)(uint)prefixes << 26);
            // XB: field and the bytes of the instruction so far
            xb = (ulong)(uint)d.Field | ((ulong)d.InsHash << 8);
            // XC: field, opcode, previous opcode, byte index, ModRM (the previous one at an instruction start)
            int modrm = d.Field == Cm2X86.FStart ? d.PrevModRM + 1 : d.ModRM + 1;
            xc = (ulong)(uint)d.Field | ((ulong)(uint)(d.Op + 1) << 4) | ((ulong)(uint)(d.PrevOp + 1) << 14) | ((ulong)(uint)pos << 24)
                | ((ulong)(uint)modrm << 27);
            // XD: the previous instruction and the current one so far
            xd = (ulong)d.PrevInsHash | ((ulong)d.InsHash << 32);
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
                Ctx.XA => xa,
                Ctx.XB => xb,
                Ctx.XC => xc,
                Ctx.XD => xd,
                _ => 0,
            };
            _ctxHash[k] = h + ((ulong)(k + 1) << 56) + (ulong)(k + 1) * 0x2545F4914F6CDD1DUL;
        }
    }

    /// <summary>Prefetches the lines both possible values of the next bit would use at the coming nibble boundary.</summary>
    private void PrefetchNibble()
    {
        if (!Sse.IsSupported || _tp == null) return;
        int shift = 64 - _lineBits;
        ulong n0 = (ulong)(_c0 << 1) * 0xD6E8FEB86659FD93UL, n1 = (ulong)((_c0 << 1) | 1) * 0xD6E8FEB86659FD93UL;
        for (int k = 0; k < _ctxHash.Length; k++)
        {
            Sse.Prefetch0(_tp + ((long)(Mix(_ctxHash[k] + n0) >> shift) << 6));
            Sse.Prefetch0(_tp + ((long)(Mix(_ctxHash[k] + n1) >> shift) << 6));
        }
    }

    private void LookupSlots()
    {
        int hits = 0;
        ulong nibCtx = _bitPos == 0 ? 0UL : (ulong)_c0 * 0xD6E8FEB86659FD93UL;
        byte[] t = _t;
        int shift = 64 - _lineBits;
        int n = _ctx.Length;
        // First compute every line and start loading them all, so the cache misses overlap.
        for (int k = 0; k < n; k++)
        {
            ulong m = Mix(_ctxHash[k] + nibCtx);
            _lineHash[k] = m;
            int line = _tBase + ((int)(m >> shift) << 6);
            _line[k] = line;
            if (Sse.IsSupported) Sse.Prefetch0(_tp + (line - _tBase));
        }
        for (int k = 0; k < n; k++)
        {
            ulong m = _lineHash[k];
            int line = _line[k];
            byte chk = (byte)(m >> 8);
            int i0 = (int)(m & 3);
            int found = -1;
            for (int j = 0; j < 4; j++)
            {
                int s = line + ((i0 ^ j) << 4);
                if (t[s] == chk) { found = s; break; }
            }
            if (found >= 0) { _slot[k] = found; hits++; continue; }
            // replace the slot whose first node has seen the fewest bits
            int victim = line + (i0 << 4);
            int best = Cm2States.Priority[t[victim + 1]];
            for (int j = 1; j < 4; j++)
            {
                int s = line + ((i0 ^ j) << 4);
                int pr = Cm2States.Priority[t[s + 1]];
                if (pr < best) { best = pr; victim = s; }
            }
            Array.Clear(t, victim + 1, 15);
            t[victim] = chk;
            _slot[k] = victim;
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
        _x86?.Update(c);
        ComputeContextHashes();
        LookupSlots();
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
        int i = 0;
        x[i++] = Cm2Logistic.Stretch(SlotP(_o0[c0]));
        x[i++] = Cm2Logistic.Stretch(SlotP(_o1[_o1Base | c0]));
        int smBit = _bitPos << 8;
        {
            int idx = smBit + _h1[_o1Base | c0];
            _smIdx[0] = idx;
            x[i++] = Cm2Logistic.Stretch((int)(_sm[idx] >> 20));
        }
        byte[] t = _t;
        int nib = _nib;
        for (int k = 0; k < _slot.Length; k++)
        {
            int s = t[_slot[k] + nib];
            int idx = (k + 1) * StateMapStride + smBit + s;
            _smIdx[k + 1] = idx;
            int p = (int)(_sm[idx] >> 20);
            int st = Cm2Logistic.Stretch(p);
            x[i++] = st;
            x[i++] = (p - 2048) >> 1;
            x[i++] = s != 0 && (Cm2States.N0[s] == 0 || Cm2States.N1[s] == 0) ? st : 0;
        }

        int mstate = 0;
        _mIdx = -1;
        if (_matchLen > 0 && ((_expected | 256) >> (8 - _bitPos)) == c0)
        {
            int eb = (_expected >> (7 - _bitPos)) & 1;
            int lb = LenBucket(_matchLen);
            _mIdx = lb * 2 + eb;
            x[i++] = Cm2Logistic.Stretch(SlotP(_mm[_mIdx]));
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

        int nPad = _nPad;
        _sel[0] = _wBase[0] + c0 * nPad;
        _sel[1] = _wBase[1] + (_hits * 8 + _bitPos) * nPad;
        _sel[2] = _wBase[2] + (mstate * 8 + _bitPos) * nPad;
        _sel[3] = _wBase[3] + (int)(_c4 & 0xFF) * nPad;
        if (_x86 is not null) _sel[4] = _wBase[4] + ((_x86.Field * 4 + Math.Min(_x86.Pos, 3)) * 8 + _bitPos) * nPad;
        _sel[_byte2Neuron] = _wBase[_byte2Neuron] + (int)((_c4 >> 8) & 0xFF) * nPad;

        long sum2 = 0;
        int neurons = _neurons;
        _sel2 = _bitPos * neurons;
        for (int k = 0; k < neurons; k++)
        {
            int s = Cm2Mix.Dot(x, _w, _sel[k], nPad) >> 8;
            if (s > 2047) s = 2047; else if (s < -2047) s = -2047;
            _s1[k] = s;
            _p1[k] = Cm2Logistic.Squash(s);
            sum2 += (long)s * _w2[_sel2 + k];
        }
        int s2 = (int)(sum2 >> 16);
        if (s2 > 2047) s2 = 2047; else if (s2 < -2047) s2 = -2047;
        _pMix = Cm2Logistic.Squash(s2);

        int pa = _a1.P(_pMix, c0);
        int pb = _a2.P(_pMix, c0 | ((int)(_c4 & 0xFF) << 8));
        int pc = _a3.P(_pMix, (_mIdx + 1) * 256 + c0);
        _xf[0] = Cm2Logistic.Stretch(_pMix); _xf[1] = Cm2Logistic.Stretch(pa); _xf[2] = Cm2Logistic.Stretch(pb); _xf[3] = Cm2Logistic.Stretch(pc); _xf[4] = 256;
        long d = 0;
        int bb = _bitPos * 5;
        for (int j = 0; j < 5; j++) d += (long)_xf[j] * _wf[bb + j];
        int sd = (int)(d >> 16);
        if (sd > 2047) sd = 2047; else if (sd < -2047) sd = -2047;
        _pf = Cm2Logistic.Squash(sd);
        int pr = (_pf * 3 + pb + 2) >> 2;
        if (pr < 1) pr = 1; else if (pr > 4095) pr = 4095;
        return pr;
    }

    public void Update(int bit)
    {
        int c0 = _c0;
        _o0[c0] = UpdSlot(_o0[c0], bit, _limit);
        int o1 = _o1Base | c0;
        _o1[o1] = UpdSlot(_o1[o1], bit, _limit);
        _h1[o1] = Cm2States.Next[_h1[o1] * 2 + bit];
        UpdSm(_smIdx[0], bit);
        byte[] t = _t;
        int nib = _nib;
        for (int k = 0; k < _slot.Length; k++)
        {
            int idx = _slot[k] + nib;
            t[idx] = Cm2States.Next[t[idx] * 2 + bit];
            UpdSm(_smIdx[k + 1], bit);
        }
        if (_mIdx >= 0) _mm[_mIdx] = UpdSlot(_mm[_mIdx], bit, 1023);

        // mixers: learning-rate warm-up, up to 4x during the first ~16 KiB, decaying to MixerRate
        int target = bit << 12;
        int lr = _lr1 + (int)((3L * _lr1 * WarmupBytes) / (WarmupBytes + _pos));
        if (lr > 128) lr = 128;
        int neurons = _neurons;
        for (int k = 0; k < neurons; k++)
            Cm2Mix.Train(_x, _w, _sel[k], _nPad, (target - _p1[k]) * lr);
        {
            int err = (target - _pMix) * _lr2;
            for (int k = 0; k < neurons; k++)
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

        _c0 = (c0 << 1) | bit;
        _nib = (_nib << 1) | bit;
        _bitPos++;
        if (_bitPos == 8) OnByte();
        else if (_bitPos == 4) LookupSlots();
        else if (_bitPos == 3) PrefetchNibble();
    }
}
