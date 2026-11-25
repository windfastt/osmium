using Osmium.Core.Pipeline;

namespace Osmium.Core.Filters;

/// <summary>
/// x86/x64 converter, version 2. It follows the instruction stream with an approximate 64-bit decoder and rewrites
/// relative 32-bit operands as positions in the block, so repeated references to one target look identical:
/// <list type="bullet">
/// <item>RIP-relative memory operands (ModRM mod=00 rm=101: <c>call [rip+x]</c>, <c>lea</c>, <c>mov</c> of globals and
/// constants), relative to the end of their instruction, as the CPU computes them;</item>
/// <item>with <c>calls=1</c> (the default), CALL/JMP rel32 (E8/E9) at instruction starts.</item>
/// </list>
/// Conditional jumps (0F 80-8F) are left alone: their targets are near and rarely shared, so the relative form
/// compresses better. Only operands whose top byte is 00 or FF (targets within 16 MiB) are rewritten, modulo 2^25 with
/// the top byte kept as a 00/FF marker, as in the x86 filter; an all-zero operand stays zero (in data the decoder
/// misreads as code, zero runs would otherwise turn into position-dependent values).
/// <para>
/// Invertibility: only displacement and immediate bytes are ever rewritten, and every decoding decision reads only
/// prefix, opcode, ModRM and SIB bytes, so the inverse walks exactly the same instruction boundaries. The rewrite of
/// one operand is a bijection on its 25-bit value for a given position (see <see cref="Rewrite"/>).
/// </para>
/// </summary>
public sealed class X86V2Filter : SameLengthFilter
{
    private static readonly ParamDef[] Defs =
    [
        new("calls", "Convert CALL/JMP", ParamType.Bool, 0, 1, 1,
            "Also convert CALL/JMP rel32 (E8/E9) targets. Helps native code; .NET ReadyToRun code calls mostly through RIP-relative cells."),
    ];

    public override int Id => MethodIds.X86V2;
    public override string Name => "x86v2";
    public override string DisplayName => "x86/x64 branch and RIP-relative converter";
    public override string Description =>
        "Converts x64 RIP-relative addresses and CALL/JMP targets to absolute ones, following the instructions. For .exe/.dll (not .NET IL); needs Osmium 0.2.0 or newer to decompress.";
    public override IReadOnlyList<ParamDef> Params => Defs;

    protected override byte[] Forward(ReadOnlySpan<byte> input, ParamValues p) => Transform(input, true, Get(p, "calls") != 0);
    protected override byte[] Inverse(ReadOnlySpan<byte> input, ParamValues p) => Transform(input, false, Get(p, "calls") != 0);

    // Operand flags per opcode: bit 0 = ModRM; bits 1-3 = immediate kind; bit 4 = CALL/JMP rel32.
    private const int M = 1;
    private const int Ib = 1 << 1, Iw = 2 << 1, Iz = 3 << 1, Iv = 4 << 1, Moffs = 5 << 1, Enter = 6 << 1, Grp3 = 7 << 1;
    private const int Call = 1 << 4;

    private static readonly byte[] One = BuildOne();
    private static readonly byte[] Two = BuildTwo();

    internal static byte[] Transform(ReadOnlySpan<byte> input, bool encode, bool calls)
    {
        var d = input.ToArray();
        int n = d.Length;
        int i = 0;
        while (i < n)
        {
            // prefixes (40-4F are REX in 64-bit mode)
            bool p66 = false, p67 = false, rexW = false;
            while (i < n)
            {
                byte b = d[i];
                if (b is >= 0x40 and <= 0x4F) { rexW = (b & 8) != 0; i++; }
                else if (b == 0x66) { p66 = true; i++; }
                else if (b == 0x67) { p67 = true; i++; }
                else if (b is 0xF2 or 0xF3 or 0xF0 or 0x26 or 0x2E or 0x36 or 0x3E or 0x64 or 0x65) i++;
                else break;
            }
            if (i >= n) break;
            int flags;
            byte op = d[i++];
            if (op == 0x0F)
            {
                if (i >= n) break;
                byte op2 = d[i++];
                if (op2 == 0x38) { if (i >= n) break; i++; flags = M; }
                else if (op2 == 0x3A) { if (i >= n) break; i++; flags = M | Ib; }
                else flags = Two[op2];
            }
            else if (op is 0xC4 or 0xC5 or 0x62)
            {
                // VEX (C4/C5) or EVEX (62): payload bytes, then the opcode in the map they select
                int payload = op == 0xC5 ? 1 : op == 0xC4 ? 2 : 3;
                if (i + payload >= n) break;
                int map = op == 0xC5 ? 1 : d[i] & 3;
                i += payload;
                byte vop = d[i++];
                flags = map == 1 ? (vop == 0x77 ? 0 : Two[vop] | M) : map == 3 ? M | Ib : M;
            }
            else flags = One[op];

            int kind = (flags >> 1) & 7;
            int imm = kind switch
            {
                1 => 1,
                2 => 2,
                3 => p66 ? 2 : 4,
                4 => rexW ? 8 : p66 ? 2 : 4,
                6 => 3,
                _ => 0,
            };
            int disp = kind == 5 ? (p67 ? 4 : 8) : 0;
            bool ripRelative = false;
            if ((flags & M) != 0)
            {
                if (i >= n) break;
                byte modrm = d[i++];
                int mod = modrm >> 6, rm = modrm & 7;
                if (kind == 7) imm = ((modrm >> 3) & 7) < 2 ? (op == 0xF6 ? 1 : p66 ? 2 : 4) : 0; // F6/F7 TEST has an immediate
                if (mod != 3 && rm == 4)
                {
                    if (i >= n) break;
                    byte sib = d[i++];
                    disp = mod == 1 ? 1 : mod == 2 ? 4 : (mod == 0 && (sib & 7) == 5) ? 4 : 0;
                }
                else
                {
                    disp = mod == 1 ? 1 : mod == 2 ? 4 : (mod == 0 && rm == 5) ? 4 : 0;
                    ripRelative = mod == 0 && rm == 5;
                }
            }
            int end = i + disp + imm;
            if (end > n) break;
            if (ripRelative) Rewrite(d, i, end, encode);
            else if (calls && (flags & Call) != 0 && imm == 4) Rewrite(d, i, end, encode);
            i = end;
        }
        return d;
    }

    /// <summary>
    /// Rewrites the 4-byte operand at <paramref name="at"/> relative to <paramref name="end"/> (the next instruction).
    /// With u the operand's 25-bit value (3 low bytes, top byte 00/FF as bit 24) and g(u) = (u + end) mod 2^25, the
    /// forward map is g with two outputs swapped: an all-zero operand stays 0, and the operand that g would send to 0
    /// takes g(0) instead. The inverse undoes exactly that, so every value round-trips.
    /// </summary>
    private static void Rewrite(byte[] d, int at, int end, bool encode)
    {
        const int Mask = (1 << 25) - 1;
        byte t = d[at + 3];
        if (t != 0x00 && t != 0xFF) return;
        int u = d[at] | (d[at + 1] << 8) | (d[at + 2] << 16) | (t == 0xFF ? 1 << 24 : 0);
        if (u == 0) return; // zero stays zero both ways
        int e = end & Mask;
        int a;
        if (encode)
        {
            a = (u + e) & Mask;
            if (a == 0) a = e; // g(0) is free: zero is never rewritten
        }
        else
        {
            a = u == e ? (-e) & Mask : (u - e) & Mask;
        }
        d[at] = (byte)a;
        d[at + 1] = (byte)(a >> 8);
        d[at + 2] = (byte)(a >> 16);
        d[at + 3] = (a & (1 << 24)) != 0 ? (byte)0xFF : (byte)0x00;
    }

    private static byte[] BuildOne()
    {
        var t = new byte[256];
        for (int i = 0; i < 0x40; i += 8)
        {
            // ALU group: op r/m,r and op r,r/m (ModRM), op AL,ib, op eAX,iz
            t[i] = t[i + 1] = t[i + 2] = t[i + 3] = M;
            t[i + 4] = Ib;
            t[i + 5] = Iz;
        }
        t[0x63] = M;
        t[0x68] = Iz; t[0x69] = M | Iz; t[0x6A] = Ib; t[0x6B] = M | Ib;
        for (int i = 0x70; i <= 0x7F; i++) t[i] = Ib;
        t[0x80] = M | Ib; t[0x81] = M | Iz; t[0x82] = M | Ib; t[0x83] = M | Ib;
        for (int i = 0x84; i <= 0x8F; i++) t[i] = M;
        t[0xA0] = t[0xA1] = t[0xA2] = t[0xA3] = Moffs;
        t[0xA8] = Ib; t[0xA9] = Iz;
        for (int i = 0xB0; i <= 0xB7; i++) t[i] = Ib;
        for (int i = 0xB8; i <= 0xBF; i++) t[i] = Iv;
        t[0xC0] = M | Ib; t[0xC1] = M | Ib; t[0xC2] = Iw; t[0xC6] = M | Ib; t[0xC7] = M | Iz;
        t[0xC8] = Enter; t[0xCA] = Iw; t[0xCD] = Ib;
        t[0xD0] = t[0xD1] = t[0xD2] = t[0xD3] = M;
        for (int i = 0xD8; i <= 0xDF; i++) t[i] = M;
        for (int i = 0xE0; i <= 0xE7; i++) t[i] = Ib;
        t[0xE8] = Iz | Call; t[0xE9] = Iz | Call; t[0xEB] = Ib;
        t[0xF6] = M | Grp3; t[0xF7] = M | Grp3;
        t[0xFE] = M; t[0xFF] = M;
        return t;
    }

    private static byte[] BuildTwo()
    {
        var t = new byte[256];
        Array.Fill(t, (byte)M);
        foreach (int i in new[] { 0x05, 0x06, 0x07, 0x08, 0x09, 0x0B, 0x0E, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37,
                     0x77, 0xA0, 0xA1, 0xA2, 0xA8, 0xA9, 0xAA })
            t[i] = 0;
        for (int i = 0x80; i <= 0x8F; i++) t[i] = Iz; // Jcc rel32: skipped over, not converted
        for (int i = 0xC8; i <= 0xCF; i++) t[i] = 0;
        foreach (int i in new[] { 0x0F, 0x70, 0x71, 0x72, 0x73, 0xA4, 0xAC, 0xBA, 0xC2, 0xC4, 0xC5, 0xC6 })
            t[i] = M | Ib;
        return t;
    }
}
