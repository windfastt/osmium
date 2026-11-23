using System.Buffers.Binary;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Filters;

/// <summary>Base for filters that never change the data length.</summary>
public abstract class SameLengthFilter : MethodBase
{
    public override MethodKind Kind => MethodKind.Filter;
    public override double EncodeSpeed(ParamValues p) => 400;
    public override double DecodeSpeed(ParamValues p) => 400;

    public sealed override byte[] Encode(ReadOnlyMemory<byte> input, ParamValues p, CodecContext ctx)
    {
        var output = Forward(input.Span, p);
        ctx.Checkpoint(input.Length);
        return output;
    }

    public sealed override byte[] Decode(ReadOnlyMemory<byte> input, int outputLength, ParamValues p, CodecContext ctx)
    {
        if (input.Length != outputLength) throw new InvalidDataException($"{Name}: length mismatch.");
        var output = Inverse(input.Span, p);
        ctx.Checkpoint(outputLength);
        return output;
    }

    protected abstract byte[] Forward(ReadOnlySpan<byte> input, ParamValues p);
    protected abstract byte[] Inverse(ReadOnlySpan<byte> input, ParamValues p);
}

/// <summary>
/// x86/x64 CALL/JMP (E8/E9) relative→absolute transform.
/// Rule: at an E8/E9 whose operand's top byte is 00 or FF, rewrite the 25-bit signed operand as
/// (operand ± position) mod 2^25, keep the 00/FF marker as bit 24, and skip 5 bytes. At an E8/E9 that does
/// not qualify, skip 4 bytes (not 1): otherwise a later transform could rewrite the marker byte this position
/// was judged on, and the decoder would decide differently. Decisions read only bytes the scan has not yet
/// modified, so encoder and decoder make identical decisions.
/// </summary>
public sealed class X86Filter : SameLengthFilter
{
    public override int Id => MethodIds.X86;
    public override string Name => "x86";
    public override string DisplayName => "x86/x64 branch converter";
    public override string Description => "Converts relative CALL/JMP targets to absolute addresses so repeated calls look identical. For .exe/.dll (not .NET IL).";

    protected override byte[] Forward(ReadOnlySpan<byte> input, ParamValues p) => Transform(input, true);
    protected override byte[] Inverse(ReadOnlySpan<byte> input, ParamValues p) => Transform(input, false);

    internal static byte[] Transform(ReadOnlySpan<byte> input, bool encode)
    {
        var d = input.ToArray();
        int n = d.Length;
        int i = 0;
        while (i <= n - 5)
        {
            byte b = d[i];
            if (b == 0xE8 || b == 0xE9)
            {
                byte t = d[i + 4];
                if (t == 0x00 || t == 0xFF)
                {
                    int v = d[i + 1] | (d[i + 2] << 8) | (d[i + 3] << 16) | (t == 0xFF ? 1 << 24 : 0);
                    v = (v << 7) >> 7; // sign-extend 25 bits
                    int pos = i + 5;
                    int a = (encode ? v + pos : v - pos) & ((1 << 25) - 1);
                    d[i + 1] = (byte)a;
                    d[i + 2] = (byte)(a >> 8);
                    d[i + 3] = (byte)(a >> 16);
                    d[i + 4] = (a & (1 << 24)) != 0 ? (byte)0xFF : (byte)0x00;
                    i += 5;
                }
                else i += 4;
            }
            else i++;
        }
        return d;
    }
}

/// <summary>ARM64 BL (branch-with-link) relative→absolute transform on 4-byte aligned words.</summary>
public sealed class Arm64Filter : SameLengthFilter
{
    public override int Id => MethodIds.Arm64;
    public override string Name => "arm64";
    public override string DisplayName => "ARM64 branch converter";
    public override string Description => "Converts relative BL targets to absolute so repeated calls match. For ARM64 executables.";

    protected override byte[] Forward(ReadOnlySpan<byte> input, ParamValues p) => Transform(input, true);
    protected override byte[] Inverse(ReadOnlySpan<byte> input, ParamValues p) => Transform(input, false);

    private static byte[] Transform(ReadOnlySpan<byte> input, bool encode)
    {
        var d = input.ToArray();
        for (int pos = 0; pos + 4 <= d.Length; pos += 4)
        {
            uint w = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos));
            if ((w >> 26) != 0x25) continue;
            uint off = (uint)(pos >> 2);
            uint imm = w & 0x3FFFFFF;
            imm = (encode ? imm + off : imm - off) & 0x3FFFFFF;
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(pos), (w & 0xFC000000) | imm);
        }
        return d;
    }
}

public sealed class DeltaFilter : SameLengthFilter
{
    private static readonly ParamDef[] Defs =
    [
        new("stride", "Stride (bytes)", ParamType.Int, 1, 256, 1, "Distance to the byte this one is predicted from.", [1, 2, 3, 4, 6, 8, 12, 16]),
    ];
    public override int Id => MethodIds.Delta;
    public override string Name => "delta";
    public override string DisplayName => "Delta";
    public override string Description => "Stores differences between bytes that are 'stride' apart. Great for tables, raw audio, sensor logs.";
    public override IReadOnlyList<ParamDef> Params => Defs;

    protected override byte[] Forward(ReadOnlySpan<byte> x, ParamValues p)
    {
        int s = (int)Get(p, "stride");
        var y = new byte[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = (byte)(i >= s ? x[i] - x[i - s] : x[i]);
        return y;
    }

    protected override byte[] Inverse(ReadOnlySpan<byte> y, ParamValues p)
    {
        int s = (int)Get(p, "stride");
        var x = new byte[y.Length];
        for (int i = 0; i < y.Length; i++) x[i] = (byte)(i >= s ? y[i] + x[i - s] : y[i]);
        return x;
    }
}

public sealed class TransposeFilter : SameLengthFilter
{
    private static readonly ParamDef[] Defs =
    [
        new("stride", "Record size (bytes)", ParamType.Int, 2, 64, 4, "Splits records into byte planes (all first bytes, then all second bytes…).", [2, 3, 4, 8, 12, 16]),
    ];
    public override int Id => MethodIds.Transpose;
    public override string Name => "transpose";
    public override string DisplayName => "Byte-plane transpose";
    public override string Description => "Groups the k-th byte of every record together. Helps numeric arrays, floats, UTF-16.";
    public override IReadOnlyList<ParamDef> Params => Defs;

    protected override byte[] Forward(ReadOnlySpan<byte> input, ParamValues p)
    {
        int s = (int)Get(p, "stride");
        int m = input.Length / s;
        var o = new byte[input.Length];
        for (int k = 0; k < s; k++)
            for (int r = 0; r < m; r++)
                o[k * m + r] = input[r * s + k];
        input[(m * s)..].CopyTo(o.AsSpan(m * s));
        return o;
    }

    protected override byte[] Inverse(ReadOnlySpan<byte> input, ParamValues p)
    {
        int s = (int)Get(p, "stride");
        int m = input.Length / s;
        var o = new byte[input.Length];
        for (int k = 0; k < s; k++)
            for (int r = 0; r < m; r++)
                o[r * s + k] = input[k * m + r];
        input[(m * s)..].CopyTo(o.AsSpan(m * s));
        return o;
    }
}

/// <summary>
/// 16-bit PCM audio predictor for a region of the block (the WAV "data" chunk).
/// Per channel: fixed order-2 prediction, refined by an optional sign-sign LMS filter over past stage-1
/// residuals. Residuals are zig-zag encoded and optionally split into low/high byte planes. All integer,
/// computed only from already-reconstructed samples, so the inverse is exact.
/// </summary>
public sealed class Pcm16Filter : SameLengthFilter
{
    private static readonly ParamDef[] Defs =
    [
        new("offset", "Region offset", ParamType.Int, 0, int.MaxValue, 0, "Start of the PCM data in the block."),
        new("length", "Region length", ParamType.Int, 0, int.MaxValue, int.MaxValue, "Length of the PCM data (clipped to the block)."),
        new("channels", "Channels", ParamType.Int, 1, 8, 2, "Interleaved channel count."),
        new("lms", "LMS order", ParamType.Int, 0, 32, 16, "Adaptive filter taps (0 = fixed predictor only).", [0, 8, 16, 32]),
        new("split", "Split byte planes", ParamType.Bool, 0, 1, 1, "Store low bytes then high bytes of the residuals."),
    ];
    public override int Id => MethodIds.Pcm16;
    public override string Name => "pcm16";
    public override string DisplayName => "PCM audio predictor (16-bit)";
    public override string Description => "Predicts each audio sample from previous ones and stores only the error. For uncompressed .wav.";
    public override IReadOnlyList<ParamDef> Params => Defs;
    public override double EncodeSpeed(ParamValues p) => 60;
    public override double DecodeSpeed(ParamValues p) => 60;

    private sealed class Channel(int order)
    {
        public int S1, S2;
        public readonly int[] W = new int[order];
        public readonly int[] H = new int[order];

        public int Predict(out int p1)
        {
            p1 = Math.Clamp(2 * S1 - S2, -32768, 32767);
            if (W.Length == 0) return p1;
            long acc = 0;
            for (int j = 0; j < W.Length; j++) acc += (long)W[j] * H[j];
            return (int)Math.Clamp(p1 + (acc >> 12), -32768, 32767);
        }

        public void Update(int s, int p1, int e)
        {
            if (W.Length > 0)
            {
                int se = Math.Sign(e);
                for (int j = 0; j < W.Length; j++)
                    W[j] = Math.Clamp(W[j] + 16 * se * Math.Sign(H[j]), -(1 << 16), 1 << 16);
                Array.Copy(H, 0, H, 1, H.Length - 1);
                H[0] = Math.Clamp(s - p1, -32768, 32767);
            }
            S2 = S1;
            S1 = s;
        }
    }

    private static (int start, int frames, int ch) Region(int n, ParamValues p, Func<ParamValues, string, long> get)
    {
        int ch = (int)get(p, "channels");
        long off = Math.Min(get(p, "offset"), n);
        long len = Math.Min(get(p, "length"), n - off);
        return ((int)off, (int)(len / (2 * ch)), ch);
    }

    protected override byte[] Forward(ReadOnlySpan<byte> input, ParamValues p)
    {
        var o = input.ToArray();
        var (start, frames, ch) = Region(input.Length, p, Get);
        int order = (int)Get(p, "lms");
        bool split = Get(p, "split") != 0;
        int count = frames * ch;
        var chans = Enumerable.Range(0, ch).Select(_ => new Channel(order)).ToArray();
        for (int i = 0; i < count; i++)
        {
            var c = chans[i % ch];
            int s = BinaryPrimitives.ReadInt16LittleEndian(input.Slice(start + 2 * i));
            int pred = c.Predict(out int p1);
            int e = (short)(s - pred);
            ushort z = (ushort)((e << 1) ^ (e >> 31));
            if (split) { o[start + i] = (byte)z; o[start + count + i] = (byte)(z >> 8); }
            else BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(start + 2 * i), z);
            c.Update(s, p1, e);
        }
        return o;
    }

    protected override byte[] Inverse(ReadOnlySpan<byte> input, ParamValues p)
    {
        var o = input.ToArray();
        var (start, frames, ch) = Region(input.Length, p, Get);
        int order = (int)Get(p, "lms");
        bool split = Get(p, "split") != 0;
        int count = frames * ch;
        var chans = Enumerable.Range(0, ch).Select(_ => new Channel(order)).ToArray();
        for (int i = 0; i < count; i++)
        {
            var c = chans[i % ch];
            ushort z = split
                ? (ushort)(input[start + i] | (input[start + count + i] << 8))
                : BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(start + 2 * i));
            int e = (z >> 1) ^ -(z & 1);
            int pred = c.Predict(out int p1);
            int s = (short)(pred + e);
            BinaryPrimitives.WriteInt16LittleEndian(o.AsSpan(start + 2 * i), (short)s);
            c.Update(s, p1, e);
        }
        return o;
    }
}

/// <summary>
/// Raster image predictor (BMP pixel arrays): optional subtract-green colour transform, then MED (LOCO-I)
/// prediction per channel. Row padding and bytes outside the region pass through unchanged.
/// </summary>
public sealed class Image2DFilter : SameLengthFilter
{
    private static readonly ParamDef[] Defs =
    [
        new("offset", "Pixel data offset", ParamType.Int, 0, int.MaxValue, 0, "Start of the pixel array in the block."),
        new("width", "Width (pixels)", ParamType.Int, 1, 1 << 20, 1, "Image width."),
        new("rows", "Rows", ParamType.Int, 1, 1 << 20, 1, "Image height."),
        new("bpp", "Bytes per pixel", ParamType.Int, 1, 4, 3, "3 = 24-bit BGR, 4 = 32-bit BGRA, 1 = grayscale/indexed."),
        new("rowbytes", "Row stride (bytes)", ParamType.Int, 1, 1 << 24, 4, "Bytes per row including padding."),
        new("green", "Subtract green", ParamType.Bool, 0, 1, 1, "Decorrelate colour channels before prediction (bpp ≥ 3)."),
    ];
    public override int Id => MethodIds.Image2D;
    public override string Name => "image";
    public override string DisplayName => "Image predictor (2-D)";
    public override string Description => "Predicts each pixel from its left, upper and upper-left neighbours (MED). For uncompressed .bmp.";
    public override IReadOnlyList<ParamDef> Params => Defs;
    public override double EncodeSpeed(ParamValues p) => 80;
    public override double DecodeSpeed(ParamValues p) => 80;

    private readonly record struct Geo(int Offset, int Width, int Rows, int Bpp, int Stride, bool Green);

    private Geo Geometry(int n, ParamValues p)
    {
        int off = (int)Math.Min(Get(p, "offset"), n);
        int bpp = (int)Get(p, "bpp");
        int width = (int)Get(p, "width");
        int stride = (int)Math.Max(Get(p, "rowbytes"), (long)width * bpp);
        int rows = (int)Math.Min(Get(p, "rows"), (n - off) / Math.Max(1, stride));
        return new Geo(off, width, Math.Max(0, rows), bpp, stride, Get(p, "green") != 0 && bpp >= 3);
    }

    private static int Med(int a, int b, int c)
    {
        int mx = Math.Max(a, b), mn = Math.Min(a, b);
        if (c >= mx) return mn;
        if (c <= mn) return mx;
        return a + b - c;
    }

    private static int Predict(byte[] v, Geo g, int row, int x, int ch)
    {
        int idx = g.Offset + row * g.Stride + x * g.Bpp + ch;
        bool hasLeft = x > 0, hasUp = row > 0;
        if (!hasLeft && !hasUp) return 0;
        if (!hasUp) return v[idx - g.Bpp];
        if (!hasLeft) return v[idx - g.Stride];
        return Med(v[idx - g.Bpp], v[idx - g.Stride], v[idx - g.Stride - g.Bpp]);
    }

    private static void ColorTransform(byte[] d, Geo g, bool forward)
    {
        if (!g.Green) return;
        for (int r = 0; r < g.Rows; r++)
            for (int x = 0; x < g.Width; x++)
            {
                int i = g.Offset + r * g.Stride + x * g.Bpp; // BGR(A)
                byte green = d[i + 1];
                d[i] = (byte)(forward ? d[i] - green : d[i] + green);
                d[i + 2] = (byte)(forward ? d[i + 2] - green : d[i + 2] + green);
            }
    }

    protected override byte[] Forward(ReadOnlySpan<byte> input, ParamValues p)
    {
        var g = Geometry(input.Length, p);
        var src = input.ToArray();
        ColorTransform(src, g, forward: true);
        var o = (byte[])src.Clone();
        for (int r = 0; r < g.Rows; r++)
            for (int x = 0; x < g.Width; x++)
                for (int ch = 0; ch < g.Bpp; ch++)
                {
                    int idx = g.Offset + r * g.Stride + x * g.Bpp + ch;
                    o[idx] = (byte)(src[idx] - Predict(src, g, r, x, ch));
                }
        return o;
    }

    protected override byte[] Inverse(ReadOnlySpan<byte> input, ParamValues p)
    {
        var g = Geometry(input.Length, p);
        var o = input.ToArray();
        for (int r = 0; r < g.Rows; r++)
            for (int x = 0; x < g.Width; x++)
                for (int ch = 0; ch < g.Bpp; ch++)
                {
                    int idx = g.Offset + r * g.Stride + x * g.Bpp + ch;
                    o[idx] = (byte)(input[idx] + Predict(o, g, r, x, ch));
                }
        ColorTransform(o, g, forward: false);
        return o;
    }
}
