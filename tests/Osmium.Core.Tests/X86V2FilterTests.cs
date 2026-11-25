using System.Buffers.Binary;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests;

public class X86V2FilterTests
{
    private static readonly MethodRegistry Reg = MethodRegistry.Default;
    private static readonly IMethod Filter = Reg.Get("x86v2");
    private static readonly ParamValues Calls = ParamValues.Empty, NoCalls = ParamValues.Of(("calls", 0));

    private static byte[] Enc(byte[] data, ParamValues p) => Filter.Encode(data, p, CodecContext.None);
    private static byte[] Dec(byte[] data, ParamValues p) => Filter.Decode(data, data.Length, p, CodecContext.None);

    [Fact]
    public void X86V2FilterSurvivesExhaustiveShortPatterns()
    {
        // Every 7-byte string over an alphabet that reaches RIP-relative operands (00/FF opcodes with ModRM 05),
        // E8 calls, 0F escapes, REX.W and 66 prefixes and a VEX prefix, with both settings.
        byte[] alphabet = [0x00, 0x05, 0xE8, 0xFF, 0x0F, 0x48, 0x66, 0xC5];
        var buf = new byte[7];
        int total = 1 << (3 * buf.Length);
        foreach (var p in new[] { Calls, NoCalls })
            for (int code = 0; code < total; code++)
            {
                int c = code;
                for (int i = 0; i < buf.Length; i++) { buf[i] = alphabet[c & 7]; c >>= 3; }
                var enc = Enc(buf, p);
                var dec = Dec(enc, p);
                Assert.True(buf.AsSpan().SequenceEqual(dec), $"x86v2 {p} failed on {Convert.ToHexString(buf)}");
            }
    }

    private static void Check(ReadOnlySpan<byte> buf, bool calls)
    {
        var enc = Osmium.Core.Filters.X86V2Filter.Transform(buf, true, calls);
        var dec = Osmium.Core.Filters.X86V2Filter.Transform(enc, false, calls);
        if (!buf.SequenceEqual(dec)) Assert.Fail($"x86v2 calls={calls} failed on {Convert.ToHexString(buf)}");
    }

    [Fact]
    public void X86V2FilterSurvivesEveryThreeByteInput()
    {
        Span<byte> buf = stackalloc byte[3];
        for (int v = 0; v < 1 << 24; v++)
        {
            buf[0] = (byte)v; buf[1] = (byte)(v >> 8); buf[2] = (byte)(v >> 16);
            Check(buf, true);
        }
    }

    [Fact]
    public void X86V2FilterSurvivesSixteenSymbolAlphabets()
    {
        // Operand values that hit the rewrite's swapped slots near small block positions (F9/FA/FB FF FF FF), ModRM
        // forms with an immediate after the displacement (81/83/C7, F6/F7 TEST), escapes, VEX/EVEX, the address-size
        // prefix and moffs (A1).
        byte[] a = [0x00, 0x05, 0xE8, 0xE9, 0xFF, 0xF9, 0xFA, 0xFB, 0x0F, 0x38, 0x3A, 0x81, 0x83, 0xC7, 0xF6, 0xF7];
        byte[] b = [0x00, 0x05, 0xE8, 0xFF, 0xFA, 0xFB, 0xC4, 0xC5, 0x62, 0x67, 0x66, 0x48, 0xA1, 0x15, 0x0D, 0x3D];
        Span<byte> buf = stackalloc byte[6];
        for (int code = 0; code < 1 << 24; code++)
        {
            for (int i = 0; i < 6; i++) buf[i] = a[(code >> (4 * i)) & 15];
            Check(buf, true);
        }
        var r = new Rng(31);
        Span<byte> seven = stackalloc byte[7];
        foreach (var alphabet in new[] { a, b })
            for (int round = 0; round < 1_000_000; round++)
            {
                for (int i = 0; i < 7; i++) seven[i] = alphabet[r.Below(16)];
                Check(seven, true);
                Check(seven, false);
            }
    }

    [Fact]
    public void InstructionsEndingAtTheModulusAndTwiceItRoundTrip()
    {
        // 0x90 (nop) everywhere, so every instruction starts exactly where it is put.
        var d = new byte[(1 << 26) + 64];
        Array.Fill(d, (byte)0x90);
        foreach (int end in new[] { 1 << 25, 1 << 26 })
        {
            int call = end - 5;            // E8 rel32 ending at `end`
            d[call] = 0xE8;
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(call + 1), -100);
            int lea = end - 40 - 7;        // lea rax, [rip+x] ending 40 bytes earlier
            d[lea] = 0x48; d[lea + 1] = 0x8D; d[lea + 2] = 0x05;
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(lea + 3), 40);  // target `end` ≡ 0 (mod 2^25): the swapped slot
            int cmp = end + 8;             // cmp dword [rip+x], imm8 starting at `end`+8
            d[cmp] = 0x83; d[cmp + 1] = 0x3D;
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(cmp + 2), 1234);
            d[cmp + 6] = 7;
        }
        var enc = Enc(d, Calls);
        foreach (int end in new[] { 1 << 25, 1 << 26 })
        {
            // end ≡ 0 (mod 2^25): the call operand is its own absolute form
            Assert.Equal(-100, BinaryPrimitives.ReadInt32LittleEndian(enc.AsSpan(end - 4)));
            // a target ≡ 0 would rewrite to 0, so it takes the free slot g(0) = (end - 40) mod 2^25 = -40 with its marker
            Assert.Equal(-40, BinaryPrimitives.ReadInt32LittleEndian(enc.AsSpan(end - 40 - 4)));
            // relative to the end of the instruction, after its immediate: 1234 + 15
            Assert.Equal(1249, BinaryPrimitives.ReadInt32LittleEndian(enc.AsSpan(end + 8 + 2)));
        }
        Assert.Equal(d, Dec(enc, Calls));
        Assert.Equal(d, Dec(Enc(d, NoCalls), NoCalls));
    }

    [Fact]
    public void ZeroOperandsStayZeroAndTheTargetZeroOperandRoundTrips()
    {
        // E8 at offset k with operand 0 (left alone) and with operand -(k+5) (target 0: the rewrite would give 0,
        // so it takes the free slot g(0) instead), and the same for RIP-relative lea.
        for (int k = 0; k < 40; k++)
        {
            foreach (int operand in new[] { 0, -(k + 5), -(k + 5) + 1, 1 })
            {
                var call = new byte[k + 5 + 3];
                Array.Fill(call, (byte)0x90);
                call[k] = 0xE8;
                BinaryPrimitives.WriteInt32LittleEndian(call.AsSpan(k + 1), operand);
                RoundTrip(call, Calls);
                RoundTrip(call, NoCalls);
                if (operand == 0) Assert.Equal(call, Enc(call, Calls));
            }
            foreach (int operand in new[] { 0, -(k + 7), -(k + 7) - 1 })
            {
                var lea = new byte[k + 7 + 2];
                Array.Fill(lea, (byte)0x90);
                lea[k] = 0x48; lea[k + 1] = 0x8D; lea[k + 2] = 0x05;
                BinaryPrimitives.WriteInt32LittleEndian(lea.AsSpan(k + 3), operand);
                RoundTrip(lea, NoCalls);
                if (operand == 0) Assert.Equal(lea, Enc(lea, NoCalls));
                else Assert.NotEqual(lea, Enc(lea, NoCalls));
            }
        }
    }

    [Fact]
    public void RepeatedReferencesToOneTargetBecomeIdentical()
    {
        // Three times: call [rip+cell] (FF 15 disp32, 6 bytes), then call function (E8 rel32, 5 bytes).
        var d = new byte[64];
        Array.Fill(d, (byte)0x90);
        int cell = 60, function = 2;
        int[] starts = [10, 21, 32];
        foreach (int at in starts)
        {
            d[at] = 0xFF; d[at + 1] = 0x15;
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at + 2), cell - (at + 6));
            d[at + 6] = 0xE8;
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at + 7), function - (at + 11));
        }
        var enc = Enc(d, Calls);
        var noCalls = Enc(d, NoCalls);
        foreach (int at in starts)
        {
            Assert.Equal(cell, BinaryPrimitives.ReadInt32LittleEndian(enc.AsSpan(at + 2)));
            Assert.Equal(function, BinaryPrimitives.ReadInt32LittleEndian(enc.AsSpan(at + 7)));
            // calls=0 converts the RIP-relative operand only
            Assert.Equal(cell, BinaryPrimitives.ReadInt32LittleEndian(noCalls.AsSpan(at + 2)));
            Assert.Equal(d.AsSpan(at + 7, 4).ToArray(), noCalls.AsSpan(at + 7, 4).ToArray());
        }
        Assert.Equal(d, Dec(enc, Calls));
        Assert.Equal(d, Dec(noCalls, NoCalls));
    }

    [Fact]
    public void X86V2FilterSurvivesRandomInstructionLikeBuffers()
    {
        var r = new Rng(77);
        byte[] common = [0x00, 0x05, 0x0D, 0x15, 0x25, 0x0F, 0x48, 0x4C, 0x66, 0x67, 0xF2, 0xF3, 0xC4, 0xC5, 0x62, 0xE8, 0xE9,
            0xFF, 0xF6, 0xF7, 0x8B, 0x8D, 0x83, 0x81, 0x38, 0x3A, 0x80, 0x84, 0xA1, 0xB8, 0xC8, 0x44, 0x24, 0x77, 0xFE, 0x01];
        for (int round = 0; round < 3000; round++)
        {
            var b = new byte[r.Below(600)];
            for (int i = 0; i < b.Length; i++) b[i] = r.Below(3) == 0 ? (byte)r.Below(256) : common[r.Below(common.Length)];
            RoundTrip(b, Calls);
            RoundTrip(b, NoCalls);
        }
        foreach (var size in new[] { 1 << 12, 1 << 16 })
        {
            RoundTrip(X64Synthetic.Code(size, (ulong)size), Calls);
            RoundTrip(X64Synthetic.Code(size, (ulong)size), NoCalls);
            RoundTrip(Synthetic.BranchNoise(size), Calls);
        }
    }

    [Fact]
    public void PositionsBeyondTheRewriteModulusRoundTrip()
    {
        // The rewrite works modulo 2^25: put calls and RIP-relative operands around 32 MiB.
        var d = new byte[(1 << 25) + 4096];
        foreach (int at in new[] { (1 << 25) - 20, (1 << 25) - 7, (1 << 25) - 3, (1 << 25) + 30 })
        {
            d[at] = 0xE8;
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at + 1), -100);
            d[at + 5] = 0x8B; d[at + 6] = 0x05;
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at + 7), -(at + 11));
        }
        RoundTrip(d, Calls);
    }

    [Fact]
    public void ThePlannerDoesNotStackTheX86FilterOnX86V2()
    {
        // A CodeX86 pipeline that already starts with x86v2 must not get the x86 filter added by the search.
        var spec = PipelineSpec.Parse("x86v2+lzma(dict=1M)", Reg);
        Assert.Equal("x86v2", spec.Filters.Single().Method.Name);
        Assert.True(Osmium.Core.Planning.Candidates.HasNaturalFilter(spec, "x86"));
        Assert.True(Osmium.Core.Planning.Candidates.HasNaturalFilter(PipelineSpec.Parse("x86+lzma(dict=1M)", Reg), "x86"));
        Assert.False(Osmium.Core.Planning.Candidates.HasNaturalFilter(PipelineSpec.Parse("lzma(dict=1M)", Reg), "x86"));
    }

    [Fact]
    public void TheGoldenVectorInputsExerciseTheConversions()
    {
        // The two x86v2 vectors in GoldenVectorTests only pin something if the filter changes their inputs.
        var x86 = Synthetic.X86(24 << 10);
        var x64 = X64Synthetic.Code(24 << 10);
        Assert.NotEqual(x86, Enc(x86, Calls));
        Assert.NotEqual(x64, Enc(x64, NoCalls));
        Assert.NotEqual(Enc(x64, NoCalls), Enc(x64, Calls));
    }

    private static void RoundTrip(byte[] data, ParamValues p)
    {
        var enc = Enc(data, p);
        Assert.Equal(data.Length, enc.Length);
        Assert.True(data.AsSpan().SequenceEqual(Dec(enc, p)), $"x86v2 {p} failed on a {data.Length}-byte buffer");
    }
}

/// <summary>Deterministic x86-64-like code with RIP-relative operands (integer arithmetic only).</summary>
internal static class X64Synthetic
{
    /// <summary>CALL rel32 to a few functions, call [rip+cell] to a few import cells, lea/mov/cmp of a few globals
    /// (some with an immediate after the displacement), VEX loads, Jcc rel32, operand-size prefixes, short data runs
    /// of zeros and small integers, ret/int3 padding.</summary>
    public static byte[] Code(int bytes, ulong seed = 11)
    {
        var r = new Rng(seed);
        var d = new byte[bytes];
        int[] functions = [.. Enumerable.Range(0, 16).Select(k => 32 + k * (bytes / 16))];
        int[] cells = [.. Enumerable.Range(0, 8).Select(k => bytes - 64 + k * 8)];
        int[] globals = [.. Enumerable.Range(0, 12).Select(k => bytes / 2 + k * 24)];
        int i = 0;
        while (i < bytes - 40)
        {
            switch (r.Below(11))
            {
                case 0: // call rel32
                    d[i] = 0xE8;
                    BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(i + 1), functions[r.Below(functions.Length)] - (i + 5));
                    i += 5;
                    break;
                case 1: // call [rip+cell]
                    d[i] = 0xFF; d[i + 1] = 0x15;
                    BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(i + 2), cells[r.Below(cells.Length)] - (i + 6));
                    i += 6;
                    break;
                case 2: // lea rcx, [rip+global]
                    d[i] = 0x48; d[i + 1] = 0x8D; d[i + 2] = 0x0D;
                    BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(i + 3), globals[r.Below(globals.Length)] - (i + 7));
                    i += 7;
                    break;
                case 3: // cmp dword [rip+global], imm8: the operand is relative to the end, after the immediate
                    d[i] = 0x83; d[i + 1] = 0x3D;
                    BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(i + 2), globals[r.Below(globals.Length)] - (i + 7));
                    d[i + 6] = (byte)r.Below(16);
                    i += 7;
                    break;
                case 4: // jcc rel32 (left relative)
                    d[i] = 0x0F; d[i + 1] = (byte)(0x80 + r.Below(16));
                    BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(i + 2), r.Below(512) - 256);
                    i += 6;
                    break;
                case 5: // vmovups xmm0, [rip+global]
                    d[i] = 0xC5; d[i + 1] = 0xF8; d[i + 2] = 0x10; d[i + 3] = 0x05;
                    BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(i + 4), globals[r.Below(globals.Length)] - (i + 8));
                    i += 8;
                    break;
                case 6: // mov ax, imm16; mov eax, [rsp+disp8]
                    d[i] = 0x66; d[i + 1] = 0xB8; d[i + 2] = (byte)r.Below(256); d[i + 3] = 0;
                    d[i + 4] = 0x8B; d[i + 5] = 0x44; d[i + 6] = 0x24; d[i + 7] = (byte)(r.Below(16) * 8);
                    i += 8;
                    break;
                case 7: // data: zeros and small integers
                    for (int k = 8 + r.Below(24); k > 0; k--) d[i++] = r.Below(4) == 0 ? (byte)r.Below(8) : (byte)0;
                    break;
                case 8: // ret, int3 padding to 16 bytes
                    d[i++] = 0xC3;
                    while (i % 16 != 0) d[i++] = 0xCC;
                    break;
                default: // mov rax, [rbp+disp8]; add rsp, imm8
                    d[i] = 0x48; d[i + 1] = 0x8B; d[i + 2] = 0x45; d[i + 3] = (byte)(r.Below(16) * 8);
                    d[i + 4] = 0x48; d[i + 5] = 0x83; d[i + 6] = 0xC4; d[i + 7] = (byte)(8 * r.Below(8));
                    i += 8;
                    break;
            }
        }
        return d;
    }
}
