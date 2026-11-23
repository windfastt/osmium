using System.Text;
using Osmium.Core.IO;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Filters;

/// <summary>
/// Text preprocessor: capitalization flags + a per-block dictionary of frequent words.
///
/// Words are maximal runs of ASCII letters. Each word is classified lower / Capitalized / UPPER (≥2 letters) /
/// mixed. Lower, Capitalized and UPPER words are emitted as an optional case flag followed by the lowercase
/// form, which is replaced by a 1- or 2-byte code when it is in the dictionary. Mixed words are emitted raw.
///
/// Reserved bytes (escape E, flags Fc/Fu, 1-byte codes, 2-byte code prefixes) are chosen among NON-letters,
/// preferring bytes that never occur in the block. A literal occurrence of a reserved byte is emitted as
/// "E b". Because reserved bytes are never letters and a word is always followed by a non-letter, the decoder
/// can parse unambiguously and its "uppercase until the next non-letter" run ends exactly where the encoder's
/// word ended. Works on arbitrary binary input (it just will not help there).
/// </summary>
public sealed class TextFilter : MethodBase
{
    private const byte Magic = (byte)'T';
    private const byte Version = 1;
    private const int MinWord = 2, MaxWord = 32;

    public override int Id => MethodIds.Text;
    public override string Name => "text";
    public override string DisplayName => "Text dictionary + capitalization";
    public override string Description => "Replaces frequent words with short codes and folds capital letters into flags. For English-like text.";
    public override MethodKind Kind => MethodKind.Filter;
    public override double EncodeSpeed(ParamValues p) => 40;
    public override double DecodeSpeed(ParamValues p) => 80;

    private static bool IsUpper(byte c) => c >= 'A' && c <= 'Z';
    private static bool IsLower(byte c) => c >= 'a' && c <= 'z';
    private static bool IsLetter(byte c) => IsUpper(c) || IsLower(c);

    private enum Case { Lower, Cap, Upper, Mixed }

    private static Case Classify(ReadOnlySpan<byte> w)
    {
        bool firstUpper = IsUpper(w[0]);
        bool restLower = true, restUpper = true;
        for (int i = 1; i < w.Length; i++)
        {
            if (IsUpper(w[i])) restLower = false; else restUpper = false;
        }
        if (!firstUpper) return restLower ? Case.Lower : Case.Mixed;
        if (restLower) return Case.Cap;                // includes 1-letter words
        return restUpper ? Case.Upper : Case.Mixed;
    }

    public override byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx)
    {
        var data = input.Span;
        // 1. byte frequencies → reserved bytes
        var freq = new long[256];
        foreach (var b in data) freq[b]++;
        var candidates = Enumerable.Range(0, 256).Where(b => !IsLetter((byte)b))
            .OrderBy(b => freq[b]).ThenBy(b => b).Select(b => (byte)b).ToList();
        byte esc = candidates[0], fc = candidates[1], fu = candidates[2];
        var free = candidates.Skip(3).Where(b => freq[b] == 0).ToList();
        int n2 = Math.Min(16, free.Count / 4);
        int n1 = Math.Min(64, free.Count - n2);
        var code1 = free.Take(n1).ToArray();
        var prefix = free.Skip(n1).Take(n2).ToArray();

        // 2. word statistics on lowercase forms of non-mixed words
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        ForEachWord(data, (w, c) =>
        {
            if (c == Case.Mixed || w.Length < MinWord || w.Length > MaxWord) return;
            string key = Encoding.ASCII.GetString(w).ToLowerInvariant();
            counts[key] = counts.GetValueOrDefault(key) + 1;
        });
        ctx.Checkpoint(data.Length / 3);
        var ranked = counts.Where(kv => kv.Value >= 3)
            .OrderByDescending(kv => (long)kv.Value * kv.Key.Length).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();
        var words = new List<string>();
        foreach (var kv in ranked)
        {
            int codeLen = words.Count < n1 ? 1 : 2;
            if (codeLen == 2 && words.Count >= n1 + n2 * 256) break;
            long gain = (long)kv.Value * (kv.Key.Length - codeLen) - (kv.Key.Length + 1);
            if (gain <= 0) continue;
            words.Add(kv.Key);
        }
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < words.Count; i++) index[words[i]] = i;

        var reserved = new bool[256];
        reserved[esc] = reserved[fc] = reserved[fu] = true;
        foreach (var b in code1) reserved[b] = true;
        foreach (var b in prefix) reserved[b] = true;

        // 3. header
        var ms = new MemoryStream(data.Length + 1024);
        ms.WriteByte(Magic); ms.WriteByte(Version);
        ms.WriteByte(esc); ms.WriteByte(fc); ms.WriteByte(fu);
        ms.WriteByte((byte)n1); ms.Write(code1);
        ms.WriteByte((byte)n2); ms.Write(prefix);
        VarInt.Write(ms, (ulong)words.Count);
        foreach (var w in words) { ms.WriteByte((byte)w.Length); ms.Write(Encoding.ASCII.GetBytes(w)); }

        // 4. body
        Span<byte> lower = stackalloc byte[MaxWord];
        int i0 = 0;
        while (i0 < data.Length)
        {
            byte c = data[i0];
            if (!IsLetter(c))
            {
                if (reserved[c]) ms.WriteByte(esc);
                ms.WriteByte(c);
                i0++;
                continue;
            }
            int j = i0;
            while (j < data.Length && IsLetter(data[j])) j++;
            var w = data[i0..j];
            var kind = Classify(w);
            if (kind == Case.Mixed)
            {
                ms.Write(w);
            }
            else
            {
                if (kind == Case.Cap) ms.WriteByte(fc);
                else if (kind == Case.Upper) ms.WriteByte(fu);
                int code = -1;
                if (w.Length >= MinWord && w.Length <= MaxWord && index.Count > 0)
                {
                    for (int k = 0; k < w.Length; k++) lower[k] = (byte)(w[k] | 0x20);
                    code = index.GetValueOrDefault(Encoding.ASCII.GetString(lower[..w.Length]), -1);
                }
                if (code < 0)
                {
                    foreach (var b in w) ms.WriteByte((byte)(b | 0x20));
                }
                else if (code < n1) ms.WriteByte(code1[code]);
                else
                {
                    int k = code - n1;
                    ms.WriteByte(prefix[k >> 8]);
                    ms.WriteByte((byte)(k & 0xFF));
                }
            }
            i0 = j;
            if ((i0 & 0xFFFFF) < (j - i0 + 1)) ctx.Checkpoint(data.Length / 3 + i0 * 2 / 3);
        }
        ctx.Checkpoint(data.Length);
        return ms.ToArray();
    }

    private delegate void WordAction(ReadOnlySpan<byte> word, Case kind);

    private static void ForEachWord(ReadOnlySpan<byte> data, WordAction action)
    {
        int i = 0;
        while (i < data.Length)
        {
            if (!IsLetter(data[i])) { i++; continue; }
            int j = i;
            while (j < data.Length && IsLetter(data[j])) j++;
            var w = data[i..j];
            action(w, Classify(w));
            i = j;
        }
    }

    public override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx)
    {
        byte[] src = input.ToArray();
        int pos = 0;
        byte Next()
        {
            if (pos >= src.Length) throw new InvalidDataException("Text filter stream truncated.");
            return src[pos++];
        }
        if (Next() != Magic || Next() != Version) throw new InvalidDataException("Bad text filter header.");
        byte esc = Next(), fc = Next(), fu = Next();
        var role = new int[256]; // 0 literal, 1 esc, 2 fc, 3 fu, 4 code1, 5 prefix
        var roleIndex = new int[256];
        role[esc] = 1; role[fc] = 2; role[fu] = 3;
        int n1 = Next();
        for (int i = 0; i < n1; i++) { byte b = Next(); role[b] = 4; roleIndex[b] = i; }
        int n2 = Next();
        for (int i = 0; i < n2; i++) { byte b = Next(); role[b] = 5; roleIndex[b] = i; }
        int wordCount = (int)Math.Min(VarInt.Read(src, ref pos), int.MaxValue);
        if (wordCount > n1 + n2 * 256) throw new InvalidDataException("Text filter dictionary too large.");
        var words = new byte[wordCount][];
        for (int i = 0; i < wordCount; i++)
        {
            int len = Next();
            if (len < MinWord || len > MaxWord || pos + len > src.Length) throw new InvalidDataException("Bad dictionary word.");
            words[i] = src.AsSpan(pos, len).ToArray();
            pos += len;
        }

        var o = new byte[outputLength];
        int op = 0;
        bool pendingCap = false, upperRun = false;
        void Emit(byte c)
        {
            if (op >= o.Length) throw new InvalidDataException("Text filter output overflow.");
            if (IsLower(c))
            {
                if (pendingCap) { c -= 32; pendingCap = false; }
                else if (upperRun) c -= 32;
            }
            else if (!IsLetter(c)) { upperRun = false; pendingCap = false; }
            o[op++] = c;
        }
        void EmitWord(int idx)
        {
            if ((uint)idx >= (uint)words.Length) throw new InvalidDataException("Bad dictionary code.");
            foreach (var c in words[idx]) Emit(c);
        }

        while (op < outputLength)
        {
            byte x = Next();
            switch (role[x])
            {
                case 1: Emit(Next()); break;
                case 2: pendingCap = true; break;
                case 3: upperRun = true; break;
                case 4: EmitWord(roleIndex[x]); break;
                case 5: EmitWord(n1 + (roleIndex[x] << 8) + Next()); break;
                default: Emit(x); break;
            }
            if ((op & 0xFFFFF) == 0) ctx.Checkpoint(op);
        }
        if (pos != src.Length) throw new InvalidDataException("Trailing bytes after text filter stream.");
        ctx.Checkpoint(outputLength);
        return o;
    }
}
