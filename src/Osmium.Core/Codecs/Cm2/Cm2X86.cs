namespace Osmium.Core.Codecs.Cm2;

/// <summary>
/// Approximate x86/x64 instruction decoder for cm2's x86 contexts. It follows the byte stream one byte at a time and
/// tracks which part of an instruction the next byte belongs to (prefix/opcode, ModRM, SIB, displacement, immediate),
/// the opcode, the ModRM byte and the previous instructions. It assumes 64-bit mode (40-4F are REX prefixes) and
/// only needs to be a good heuristic: on data or misdecoded code it resynchronizes within a few instructions.
/// Decoding depends only on bytes already seen, so encoder and decoder agree.
/// </summary>
internal sealed class Cm2X86
{
    // What the next byte is.
    public const int FStart = 0, FOp2 = 1, FOp38 = 2, FOp3A = 3, FVex = 4, FVexOp = 5, FModRM = 6, FSib = 7, FDisp = 8, FImm = 9;

    // Prefix bits.
    public const int P66 = 1, P67 = 2, PRexW = 4, PRex = 8, PF2 = 16, PF3 = 32, PLock = 64, PSeg = 128, PVex = 256;

    // Operand flags per opcode: bit 0 = ModRM; bits 1-3 = immediate kind.
    private const int M = 1;
    private const int Ib = 1 << 1, Iw = 2 << 1, Iz = 3 << 1, Iv = 4 << 1, Moffs = 5 << 1, Enter = 6 << 1, Grp3 = 7 << 1;

    private static readonly byte[] One = BuildOne();
    private static readonly byte[] Two = BuildTwo();

    public int Field;
    /// <summary>Opcode of the current instruction: 0xNN, 0x1NN (0F NN), 0x2NN (0F 38 NN), 0x3NN (0F 3A NN); -1 before it is known.</summary>
    public int Op = -1;
    /// <summary>ModRM of the current instruction, or -1.</summary>
    public int ModRM = -1;
    /// <summary>Index of the next byte within its displacement or immediate field.</summary>
    public int Pos;
    public int Pref;
    /// <summary>Bytes of the current instruction seen so far.</summary>
    public int Len;
    /// <summary>Opcodes and ModRM of the previous instructions.</summary>
    public int PrevOp = -1, PrevOp2 = -1, PrevModRM = -1;
    /// <summary>Rolling hash of the bytes of the current instruction so far, and of the previous whole instruction.</summary>
    public uint InsHash, PrevInsHash;

    private int _dispLeft, _immLeft, _vexLeft, _vexMap, _immKind;

    public void Update(int c)
    {
        Len++;
        InsHash = (InsHash + (uint)c + 1) * 0x9E3779B1u;
        switch (Field)
        {
            case FStart:
                if (c is >= 0x40 and <= 0x4F) { Pref |= PRex | ((c & 8) != 0 ? PRexW : 0); return; }
                switch (c)
                {
                    case 0x66: Pref |= P66; return;
                    case 0x67: Pref |= P67; return;
                    case 0xF2: Pref |= PF2; return;
                    case 0xF3: Pref |= PF3; return;
                    case 0xF0: Pref |= PLock; return;
                    case 0x26 or 0x2E or 0x36 or 0x3E or 0x64 or 0x65: Pref |= PSeg; return;
                    case 0x0F: Field = FOp2; return;
                    case 0xC5: Pref |= PVex; _vexLeft = 1; _vexMap = 1; Field = FVex; return;
                    case 0xC4: Pref |= PVex; _vexLeft = 2; _vexMap = -1; Field = FVex; return;
                    case 0x62: Pref |= PVex; _vexLeft = 3; _vexMap = -1; Field = FVex; return;
                }
                Op = c;
                Operands(One[c]);
                return;
            case FOp2:
                if (c == 0x38) { Field = FOp38; return; }
                if (c == 0x3A) { Field = FOp3A; return; }
                Op = 0x100 | c;
                Operands(Two[c]);
                return;
            case FOp38:
                Op = 0x200 | c;
                Operands(M);
                return;
            case FOp3A:
                Op = 0x300 | c;
                Operands(M | Ib);
                return;
            case FVex:
                if (_vexMap < 0) _vexMap = c & 3; // map select (mmmmm or EVEX mm): 1 = 0F, 2 = 0F 38, 3 = 0F 3A
                if (--_vexLeft == 0) Field = FVexOp;
                return;
            case FVexOp:
                Op = (_vexMap << 8) | c;
                Operands(_vexMap == 1 ? (c == 0x77 ? 0 : Two[c] | M) : _vexMap == 3 ? M | Ib : M);
                return;
            case FModRM:
                ModRM = c;
                if (_immKind == Grp3 >> 1) _immLeft = ((c >> 3) & 7) < 2 ? (Op == 0xF6 ? 1 : (Pref & P66) != 0 ? 2 : 4) : 0;
                if ((c >> 6) != 3 && (c & 7) == 4) { Field = FSib; return; }
                _dispLeft = (c >> 6) switch { 1 => 1, 2 => 4, 0 when (c & 7) == 5 => 4, _ => 0 };
                Next();
                return;
            case FSib:
                _dispLeft = (ModRM >> 6) switch { 1 => 1, 2 => 4, 0 when (c & 7) == 5 => 4, _ => 0 };
                Next();
                return;
            case FDisp:
                Pos++;
                if (--_dispLeft == 0) Next();
                return;
            default: // FImm
                Pos++;
                if (--_immLeft == 0) End();
                return;
        }
    }

    private void Operands(int flags)
    {
        int kind = (flags >> 1) & 7;
        _immKind = kind;
        _immLeft = kind switch
        {
            1 => 1,
            2 => 2,
            3 => (Pref & P66) != 0 ? 2 : 4,
            4 => (Pref & PRexW) != 0 ? 8 : (Pref & P66) != 0 ? 2 : 4,
            6 => 3,
            _ => 0,
        };
        _dispLeft = kind == 5 ? ((Pref & P67) != 0 ? 4 : 8) : 0;
        if ((flags & M) != 0) { Field = FModRM; return; }
        Next();
    }

    private void Next()
    {
        Pos = 0;
        if (_dispLeft > 0) Field = FDisp;
        else if (_immLeft > 0) Field = FImm;
        else End();
    }

    private void End()
    {
        PrevOp2 = PrevOp;
        PrevOp = Op;
        PrevModRM = ModRM;
        PrevInsHash = InsHash;
        InsHash = 0;
        Op = -1;
        ModRM = -1;
        Pref = 0;
        Len = 0;
        Pos = 0;
        Field = FStart;
    }

    private static byte[] BuildOne()
    {
        var t = new byte[256];
        for (int i = 0; i < 0x40; i += 8)
        {
            // ALU group: op r/m,r (4 forms with ModRM), AL,ib, eAX,iz
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
        t[0xE8] = Iz; t[0xE9] = Iz; t[0xEB] = Ib;
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
        for (int i = 0x80; i <= 0x8F; i++) t[i] = Iz;
        for (int i = 0xC8; i <= 0xCF; i++) t[i] = 0;
        foreach (int i in new[] { 0x0F, 0x70, 0x71, 0x72, 0x73, 0xA4, 0xAC, 0xBA, 0xC2, 0xC4, 0xC5, 0xC6 })
            t[i] = M | Ib;
        return t;
    }
}
