using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Osmium.Core.Analysis;

public enum FileKind
{
    Unknown, Empty, Text, Utf16Text, Executable, Wav, Bmp, Compressed, Pdf, Database, Binary,
}

public enum CpuArch { None, X86, X64, Arm64, Il }

/// <summary>Compatibility class: files in the same class can share a solid block and a pipeline.</summary>
public enum CompatClass
{
    Incompressible, CodeX86, CodeArm64, Text, Utf16Text, Audio, Image, Structured, Repetitive, Binary,
}

public sealed record PcmInfo(long DataOffset, long DataLength, int Channels, int Bits, int Format);
public sealed record ImageInfo(long PixelOffset, int Width, int Height, int BytesPerPixel, int RowBytes);

public sealed record SampleStats(
    long SampledBytes, double H0, double H1, double Printable, double Zero, double ZeroRunCoverage,
    double Utf8Valid, double Zstd1Ratio, int Stride, double StrideGain);

public sealed record FileAnalysis(
    FileKind Kind, CompatClass Class, string Format, CpuArch Arch, IReadOnlyList<string> Evidence,
    SampleStats Stats, PcmInfo? Pcm, ImageInfo? Image)
{
    public string Summary => $"{Format} → {Class} (H0 {Stats.H0:F2}, zstd-1 {Stats.Zstd1Ratio:P0}{(Stats.Stride > 0 ? $", stride {Stats.Stride}" : "")})";
}

/// <summary>Signature detection + structure parsing + sampled statistics (BLUEPRINT §7).</summary>
public static class Sniffer
{
    private const int Window = 64 * 1024;
    private const long FullScanLimit = 1 << 20;

    private static readonly (byte[] Sig, int Offset, string Name)[] CompressedSigs =
    [
        ("PK\x03\x04"u8.ToArray(), 0, "ZIP/Office/JAR"), ("PK\x05\x06"u8.ToArray(), 0, "ZIP (empty)"),
        ([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], 0, "7-Zip"), ("Rar!\x1A\x07"u8.ToArray(), 0, "RAR"),
        ([0x1F, 0x8B, 0x08], 0, "gzip"), ([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], 0, "xz"),
        ([0x28, 0xB5, 0x2F, 0xFD], 0, "zstd"), ("BZh"u8.ToArray(), 0, "bzip2"), ([0x04, 0x22, 0x4D, 0x18], 0, "LZ4"),
        ("MSCF"u8.ToArray(), 0, "CAB"), ([0xFF, 0xD8, 0xFF], 0, "JPEG"), ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0, "PNG"),
        ("GIF8"u8.ToArray(), 0, "GIF"), ("ftyp"u8.ToArray(), 4, "MP4/MOV/HEIC"), ([0x1A, 0x45, 0xDF, 0xA3], 0, "MKV/WebM"),
        ("fLaC"u8.ToArray(), 0, "FLAC"), ("OggS"u8.ToArray(), 0, "Ogg"), ("ID3"u8.ToArray(), 0, "MP3"),
        ("OSMA"u8.ToArray(), 0, "Osmium archive"), ("wOF2"u8.ToArray(), 0, "WOFF2"), ("wOFF"u8.ToArray(), 0, "WOFF"),
    ];

    public static FileAnalysis Analyze(string path, int windows = 16)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long size = RandomAccess.GetLength(h);
        return Analyze(h, size, windows);
    }

    public static FileAnalysis Analyze(SafeFileHandle h, long size, int windows)
    {
        if (size == 0)
            return new FileAnalysis(FileKind.Empty, CompatClass.Binary, "empty", CpuArch.None, [], new SampleStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0), null, null);
        var head = Read(h, 0, (int)Math.Min(size, 4096));
        var ev = new List<string>();
        FileKind kind = FileKind.Unknown;
        string format = "data";
        CpuArch arch = CpuArch.None;
        PcmInfo? pcm = null;
        ImageInfo? img = null;

        foreach (var (sig, off, name) in CompressedSigs)
        {
            if (head.Length >= off + sig.Length && head.AsSpan(off, sig.Length).SequenceEqual(sig))
            {
                // RIFF WEBP and similar are handled below; MP3 by ID3 is compressed.
                kind = FileKind.Compressed; format = name; ev.Add($"signature {name}");
                break;
            }
        }
        if (kind == FileKind.Unknown && head.AsSpan().StartsWith("RIFF"u8) && head.Length >= 12)
        {
            if (head.AsSpan(8, 4).SequenceEqual("WEBP"u8)) { kind = FileKind.Compressed; format = "WebP"; }
            else if (head.AsSpan(8, 4).SequenceEqual("WAVE"u8) && TryParseWav(h, size, out pcm))
            {
                kind = FileKind.Wav;
                format = pcm!.Format is 1 or 0xFFFE ? $"WAV PCM {pcm.Bits}-bit ×{pcm.Channels}" : $"WAV format {pcm.Format}";
                ev.Add($"RIFF/WAVE data @{pcm.DataOffset} len {pcm.DataLength}");
            }
        }
        if (kind == FileKind.Unknown && head.Length >= 2 && head[0] == 'M' && head[1] == 'Z' && TryParsePe(head, out arch, out string peDesc))
        {
            kind = FileKind.Executable; format = peDesc; ev.Add(peDesc);
        }
        if (kind == FileKind.Unknown && head.Length >= 0x14 && head.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x7F, 0x45, 0x4C, 0x46]) && head[5] == 1)
        {
            ushort m = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0x12));
            arch = m switch { 0x03 => CpuArch.X86, 0x3E => CpuArch.X64, 0xB7 => CpuArch.Arm64, _ => CpuArch.None };
            kind = FileKind.Executable; format = $"ELF {arch}"; ev.Add(format);
        }
        if (kind == FileKind.Unknown && head.Length >= 54 && head[0] == 'B' && head[1] == 'M' && TryParseBmp(head, size, out img))
        {
            kind = FileKind.Bmp; format = $"BMP {img!.Width}×{img.Height} {img.BytesPerPixel * 8}-bit"; ev.Add(format);
        }
        if (kind == FileKind.Unknown && head.AsSpan().StartsWith("%PDF-"u8)) { kind = FileKind.Pdf; format = "PDF"; }
        if (kind == FileKind.Unknown && head.AsSpan().StartsWith("SQLite format 3\0"u8)) { kind = FileKind.Database; format = "SQLite"; }
        if (kind == FileKind.Unknown && head.Length >= 8 && head.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1])) { kind = FileKind.Database; format = "OLE compound (MSI/DOC/XLS)"; }

        var sample = SampleBody(h, size, windows);
        var stats = ComputeStats(sample);

        if (kind == FileKind.Unknown)
        {
            if (LooksUtf16(head)) { kind = FileKind.Utf16Text; format = "UTF-16 text"; }
            else if (stats.Printable > 0.95 && stats.Zero < 0.001) { kind = FileKind.Text; format = stats.Utf8Valid > 0.999 ? "text (UTF-8/ASCII)" : "text (8-bit)"; }
            else { kind = FileKind.Binary; format = "binary"; }
        }
        if (kind == FileKind.Pdf && stats.Zstd1Ratio > 0.9) ev.Add("PDF streams mostly deflated");

        var cls = Classify(kind, arch, stats, pcm, img);
        ev.Add($"H0 {stats.H0:F2} bit/B, H1 {stats.H1:F2}, printable {stats.Printable:P0}, zstd-1 ratio {stats.Zstd1Ratio:P1}");
        if (stats.Stride > 0) ev.Add($"stride {stats.Stride} (gain {stats.StrideGain:F2} bit/B)");
        return new FileAnalysis(kind, cls, format, arch, ev, stats, pcm, img);
    }

    public static CompatClass Classify(FileKind kind, CpuArch arch, SampleStats s, PcmInfo? pcm, ImageInfo? img)
    {
        bool incompressible = s.H0 >= 7.9 && s.Zstd1Ratio >= 0.985;
        if (kind == FileKind.Compressed || incompressible) return CompatClass.Incompressible;
        if (kind == FileKind.Pdf && s.Zstd1Ratio > 0.95) return CompatClass.Incompressible;
        if (kind == FileKind.Executable && arch is CpuArch.X86 or CpuArch.X64) return CompatClass.CodeX86;
        if (kind == FileKind.Executable && arch == CpuArch.Arm64) return CompatClass.CodeArm64;
        if (kind == FileKind.Wav && pcm is { Bits: 16, Format: 1 or 0xFFFE }) return CompatClass.Audio;
        if (kind == FileKind.Bmp && img is not null) return CompatClass.Image;
        if (kind == FileKind.Text) return CompatClass.Text;
        if (kind == FileKind.Utf16Text) return CompatClass.Utf16Text;
        if (s.ZeroRunCoverage > 0.3) return CompatClass.Repetitive;
        if (s.Stride > 1 && s.StrideGain >= 0.5) return CompatClass.Structured;
        return CompatClass.Binary;
    }

    private static byte[] Read(SafeFileHandle h, long offset, int count)
    {
        var b = new byte[count];
        int total = 0;
        while (total < count)
        {
            int n = RandomAccess.Read(h, b.AsSpan(total), offset + total);
            if (n <= 0) break;
            total += n;
        }
        return total == count ? b : b[..total];
    }

    /// <summary>Deterministic stratified sample (seeded by file size so identical files sample identically).</summary>
    public static byte[] SampleBody(SafeFileHandle h, long size, int windows)
    {
        if (size <= FullScanLimit || windows <= 0) return Read(h, 0, (int)Math.Min(size, FullScanLimit));
        long lo = Math.Min(4096, size / 8), hi = size - Math.Min(4096, size / 8);
        long span = hi - lo;
        windows = (int)Math.Min(windows, Math.Max(1, span / Window));
        var ms = new MemoryStream(windows * Window);
        ulong rng = (ulong)size * 0x9E3779B97F4A7C15UL + 1;
        long stratum = span / windows;
        for (int i = 0; i < windows; i++)
        {
            rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17;
            long slack = Math.Max(0, stratum - Window);
            long off = lo + i * stratum + (slack > 0 ? (long)(rng % (ulong)slack) : 0);
            ms.Write(Read(h, off, (int)Math.Min(Window, size - off)));
        }
        return ms.ToArray();
    }

    public static SampleStats ComputeStats(ReadOnlySpan<byte> d)
    {
        int n = d.Length;
        if (n == 0) return new SampleStats(0, 0, 0, 0, 0, 0, 1, 1, 0, 0);
        var f0 = new int[256];
        var f1 = new int[65536];
        int printable = 0, zero = 0, zeroRunBytes = 0, run = 0;
        for (int i = 0; i < n; i++)
        {
            byte c = d[i];
            f0[c]++;
            if (i > 0) f1[(d[i - 1] << 8) | c]++;
            if (c is 9 or 10 or 13 || (c >= 32 && c < 127) || c >= 128) printable++;
            if (c == 0) { zero++; run++; } else { if (run >= 16) zeroRunBytes += run; run = 0; }
        }
        if (run >= 16) zeroRunBytes += run;
        double h0 = Entropy(f0, n);
        double h1 = 0;
        for (int a = 0; a < 256; a++)
        {
            int rowTotal = 0;
            for (int b = 0; b < 256; b++) rowTotal += f1[(a << 8) | b];
            if (rowTotal == 0) continue;
            double e = 0;
            for (int b = 0; b < 256; b++)
            {
                int k = f1[(a << 8) | b];
                if (k > 0) { double p = (double)k / rowTotal; e -= p * Math.Log2(p); }
            }
            h1 += e * rowTotal / (n - 1);
        }
        double utf8 = Utf8Validity(d);
        double printableRatio = (double)printable / n;
        if (utf8 < 0.98) printableRatio = Math.Min(printableRatio, (double)(printable - CountHigh(d)) / n);

        double zstd1;
        try
        {
            using var c = new ZstdSharp.Compressor(1);
            zstd1 = (double)c.Wrap(d).Length / n;
        }
        catch (Exception) { zstd1 = 1; }

        var (stride, gain) = DetectStride(d, h0);
        return new SampleStats(n, h0, h1, printableRatio, (double)zero / n, (double)zeroRunBytes / n, utf8, zstd1, stride, gain);
    }

    private static int CountHigh(ReadOnlySpan<byte> d)
    {
        int k = 0;
        foreach (var c in d) if (c >= 128) k++;
        return k;
    }

    private static double Entropy(int[] f, int n)
    {
        double e = 0;
        foreach (int k in f) if (k > 0) { double p = (double)k / n; e -= p * Math.Log2(p); }
        return e;
    }

    /// <summary>Fraction of bytes that belong to well-formed UTF-8 sequences.</summary>
    private static double Utf8Validity(ReadOnlySpan<byte> d)
    {
        int good = 0, i = 0;
        while (i < d.Length)
        {
            byte c = d[i];
            int len = c < 0x80 ? 1 : (c & 0xE0) == 0xC0 && c >= 0xC2 ? 2 : (c & 0xF0) == 0xE0 ? 3 : (c & 0xF8) == 0xF0 && c <= 0xF4 ? 4 : 0;
            if (len == 0 || i + len > d.Length) { i++; continue; }
            bool ok = true;
            for (int k = 1; k < len; k++) if ((d[i + k] & 0xC0) != 0x80) { ok = false; break; }
            if (ok) { good += len; i += len; } else i++;
        }
        return (double)good / Math.Max(1, d.Length);
    }

    private static readonly int[] StrideCandidates = [2, 3, 4, 6, 8, 12, 16, 24, 32];

    /// <summary>Picks the record stride whose delta transform lowers order-0 entropy the most (harmonics suppressed).</summary>
    public static (int Stride, double Gain) DetectStride(ReadOnlySpan<byte> d, double h0)
    {
        if (d.Length < 4096) return (0, 0);
        var gains = new double[StrideCandidates.Length];
        var f = new int[256];
        for (int k = 0; k < StrideCandidates.Length; k++)
        {
            int s = StrideCandidates[k];
            Array.Clear(f);
            for (int i = s; i < d.Length; i++) f[(byte)(d[i] - d[i - s])]++;
            gains[k] = h0 - Entropy(f, d.Length - s);
        }
        double best = gains.Max();
        if (best < 0.5) return (0, best);
        for (int k = 0; k < StrideCandidates.Length; k++)
            if (gains[k] >= 0.9 * best) return (StrideCandidates[k], gains[k]);
        return (0, 0);
    }

    private static bool LooksUtf16(byte[] head)
    {
        if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF))) return true;
        if (head.Length < 64) return false;
        int oddZero = 0, evenPrint = 0, pairs = head.Length / 2;
        for (int i = 0; i + 1 < head.Length; i += 2)
        {
            if (head[i + 1] == 0) oddZero++;
            byte c = head[i];
            if (c is 9 or 10 or 13 || (c >= 32 && c < 127)) evenPrint++;
        }
        // Without a BOM demand near-certainty: little-endian integer tables with zero high bytes otherwise look
        // like UTF-16 (half their odd bytes are zero and many even bytes happen to be printable).
        return oddZero > pairs * 0.9 && evenPrint > pairs * 0.9;
    }

    public static bool TryParsePe(byte[] head, out CpuArch arch, out string desc)
    {
        arch = CpuArch.None; desc = "";
        if (head.Length < 0x40) return false;
        int pe = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0x3C));
        // Compared by subtraction: the offset comes from the file and pe + 24 can overflow.
        if (pe <= 0 || pe > head.Length - 24 || !head.AsSpan(pe, 4).SequenceEqual("PE\0\0"u8)) return false;
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(pe + 4));
        arch = machine switch { 0x014C => CpuArch.X86, 0x8664 => CpuArch.X64, 0xAA64 => CpuArch.Arm64, _ => CpuArch.None };
        int opt = pe + 24;
        bool dotnet = false;
        if (opt + 2 <= head.Length)
        {
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(opt));
            int dirBase = magic == 0x20B ? opt + 112 : opt + 96;
            int clr = dirBase + 14 * 8;
            if (clr + 8 <= head.Length && BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(clr + 4)) > 0) dotnet = true;
        }
        if (dotnet && arch == CpuArch.X86) arch = CpuArch.Il; // AnyCPU IL assemblies are marked x86
        desc = $"PE {(arch == CpuArch.Il ? ".NET IL" : arch.ToString())}{(dotnet && arch != CpuArch.Il ? " (.NET)" : "")}";
        return true;
    }

    public static bool TryParseWav(SafeFileHandle h, long size, out PcmInfo? pcm)
    {
        pcm = null;
        long pos = 12;
        int format = 0, channels = 0, bits = 0;
        var hdr = new byte[8];
        for (int guard = 0; guard < 64 && pos + 8 <= size; guard++)
        {
            if (RandomAccess.Read(h, hdr, pos) != 8) return false;
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
            var id = Encoding.ASCII.GetString(hdr, 0, 4);
            if (id == "fmt " && len >= 16)
            {
                var fmt = new byte[Math.Min(len, 40)];
                RandomAccess.Read(h, fmt, pos + 8);
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                if (format == 0xFFFE && fmt.Length >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24)) == 1 ? 0xFFFE : 3;
            }
            else if (id == "data" && channels > 0)
            {
                long dataLen = Math.Min(len, size - pos - 8);
                pcm = new PcmInfo(pos + 8, dataLen, channels, bits, format);
                return channels is >= 1 and <= 8;
            }
            pos += 8 + len + (len & 1);
        }
        return false;
    }

    public static bool TryParseBmp(byte[] head, long size, out ImageInfo? img)
    {
        img = null;
        uint pixOff = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(10));
        uint dib = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(14));
        if (dib < 40) return false;
        int w = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(18));
        int hgt = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(22)));
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(28));
        uint comp = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(30));
        if (w <= 0 || hgt <= 0 || bpp is not (8 or 24 or 32) || comp is not (0 or 3)) return false;
        // Width and height come from the file: compute the row size in 64 bits, and check that the rows fit by dividing,
        // since rowBytes * height can overflow even a long.
        long rowBytes = ((long)w * bpp + 31) / 32 * 4;
        if (pixOff >= size || rowBytes > int.MaxValue || rowBytes > (size - pixOff) / hgt) return false;
        img = new ImageInfo(pixOff, w, hgt, bpp / 8, (int)rowBytes);
        return true;
    }
}
