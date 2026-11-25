using Osmium.Core.Analysis;
using Osmium.Core.Codecs.Cm2;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Planning;

/// <summary>
/// Seed pipelines per compatibility class, most promising first (BLUEPRINT §7.4). Profiles without trials use
/// the first non-CM entry; trial profiles race the first N that pass rules and memory limits. Context mixing uses
/// cm2, which measured smaller than cm on every class; cm stays available for reading and for explicit pipelines.
/// </summary>
public static class Candidates
{
    public static List<PipelineSpec> For(CompatClass cls, FileAnalysis? rep, Profile p, MethodRegistry reg)
    {
        var list = new List<string>();
        string cm(string models, int stride = 0) =>
            $"cm2(mem={Math.Min(p.CmMem, Cm2Method.MaxMem)},models={(int)(p.CmAllModels ? Cm2Models.All : Enum.Parse<Cm2Models>(models))}{(stride > 0 ? $",stride={stride}" : "")})";
        string bx = BranchFilter(rep);
        string lz(int lc, int lp, int pb, long? dict = null) =>
            $"lzma(dict={dict ?? p.LzmaDict},lc={lc},lp={lp},pb={pb},fb=273,mf=1)";
        string pm(int order) => $"ppmd(order={order},mem={(p.Level >= ProfileLevel.Ultra ? 256 : 64)}M)";
        string zs(int level, bool ldm = false) => $"zstd(level={level}{(ldm ? ",ldm=1" : "")})";
        string br = "brotli(quality=11,window=24)";

        if (p.Level == ProfileLevel.Store) return [Parse("store")];
        if (p.Level == ProfileLevel.Fast)
            return cls == CompatClass.Incompressible ? [Parse("store")] : [Parse(zs(p.ZstdLevel))];

        switch (cls)
        {
            case CompatClass.Incompressible:
                list.AddRange(["store", zs(19)]);
                break;

            case CompatClass.Text:
                list.AddRange([cm("Text"), cm("All"), $"text+{cm("Text")}", pm(8), lz(3, 0, 0), $"text+{pm(8)}", pm(12),
                    $"text+{lz(3, 0, 0)}", pm(6), br, zs(p.ZstdLevel), cm("Default"), "bzip2", pm(16)]);
                break;

            case CompatClass.Utf16Text:
                list.AddRange([$"transpose(stride=2)+{cm("Text")}", cm("Text"), $"transpose(stride=2)+{pm(8)}", pm(8),
                    $"transpose(stride=2)+{lz(3, 0, 0)}", lz(0, 1, 1), zs(p.ZstdLevel)]);
                break;

            case CompatClass.CodeX86:
                list.AddRange([$"{bx}+{cm("All")}", $"{bx}+{cm("Exe")}", $"{bx}+{lz(0, 2, 2)}", $"{bx}+{lz(3, 0, 2)}", $"x86+{lz(0, 2, 2)}",
                    lz(3, 0, 2), cm("Default"), $"{bx}+{lz(1, 2, 2)}", $"{bx}+{pm(8)}", $"{bx}+{zs(p.ZstdLevel)}", $"x86+{cm("All")}"]);
                break;

            case CompatClass.CodeArm64:
                list.AddRange([$"arm64+{cm("Binary")}", $"arm64+{cm("All")}", $"arm64+{lz(0, 2, 2)}", $"arm64+{cm("Default")}", lz(0, 2, 2),
                    lz(3, 0, 2), cm("Default"), $"arm64+{zs(p.ZstdLevel)}"]);
                break;

            case CompatClass.Audio when rep?.Pcm is { } pcm:
            {
                string f(int lms, int split) => $"pcm16(offset={pcm.DataOffset},length={pcm.DataLength},channels={pcm.Channels},lms={lms},split={split})";
                int fs = 2 * pcm.Channels;
                list.AddRange([$"{f(16, 1)}+{cm("Binary")}", $"{f(16, 0)}+{cm("Binary", fs)}", $"{f(32, 1)}+{cm("Binary")}",
                    $"{f(16, 1)}+{lz(0, 0, 0)}", $"{f(16, 0)}+{lz(0, 1, 1)}", $"{f(0, 1)}+{lz(0, 0, 0)}",
                    $"delta(stride={fs})+{lz(0, 1, 1)}", cm("Binary", fs), lz(0, 1, 1)]);
                break;
            }

            case CompatClass.Image when rep?.Image is { } im:
            {
                string f(int green) => $"image(offset={im.PixelOffset},width={im.Width},rows={im.Height},bpp={im.BytesPerPixel},rowbytes={im.RowBytes},green={green})";
                list.AddRange([$"{f(1)}+{cm("Binary", im.RowBytes)}", $"{f(1)}+{cm("Binary")}", $"{f(0)}+{cm("Binary")}",
                    $"{f(1)}+{lz(0, 0, 0)}", $"{f(0)}+{lz(0, 0, 0)}", $"delta(stride={im.BytesPerPixel})+{lz(0, 0, 0)}",
                    cm("Binary", im.RowBytes), lz(0, 0, 0)]);
                break;
            }

            case CompatClass.Structured when rep is { Stats.Stride: > 1 }:
            {
                int s = rep.Stats.Stride;
                int lp = s is 2 ? 1 : s is 4 ? 2 : s is 8 or 16 or 32 ? 3 : 0;
                list.AddRange([cm("Binary", s), $"transpose(stride={Math.Min(s, 64)})+{cm("Binary")}", $"delta(stride={s})+{cm("Binary", s)}",
                    $"delta(stride={s})+{lz(0, lp, lp)}", $"transpose(stride={Math.Min(s, 64)})+{lz(0, 0, 0)}",
                    $"delta(stride={s})+transpose(stride={Math.Min(s, 64)})+{lz(0, 0, 0)}", lz(0, lp, lp), cm("Default"),
                    $"delta(stride={s})+{zs(p.ZstdLevel)}", lz(3, 0, 2)]);
                break;
            }

            case CompatClass.Repetitive:
                list.AddRange([$"zstd(level={Math.Max(19, p.ZstdLevel)},ldm=1)", lz(0, 2, 2, Math.Max(p.LzmaDict, 64L << 20)), cm("Default"),
                    lz(3, 0, 2), cm("Binary"), zs(19)]);
                break;

            default: // Binary and unknown structure
                list.AddRange([cm("All"), cm("Default"), lz(3, 0, 2), lz(0, 2, 2), cm("Binary"), pm(6), zs(p.ZstdLevel),
                    $"x86+{lz(3, 0, 2)}", lz(1, 0, 0), br, "bzip2"]);
                break;
        }
        if (p.Level == ProfileLevel.Exhaustive)
            list.AddRange(["deflate(level=9)", "bzip2", zs(22), br, lz(4, 0, 0), lz(0, 0, 0), pm(4), pm(16)]);

        return list.Distinct().Select(Parse).ToList();

        PipelineSpec Parse(string s) => PipelineSpec.Parse(s, reg);
    }

    /// <summary>The pipeline used when the profile does not run trials (first non-CM candidate).</summary>
    public static PipelineSpec Default(CompatClass cls, FileAnalysis? rep, Profile p, MethodRegistry reg) =>
        For(cls, rep, p, reg).FirstOrDefault(s => !MethodIds.IsContextMixing(s.Codec.Method.Id)) ?? PipelineSpec.Parse("lzma", reg);

    /// <summary>The branch converter for x86 code: x86v2 for x64 (it also converts RIP-relative addresses), x86 for
    /// 32-bit code, where the same ModRM encoding is an absolute address.</summary>
    public static string BranchFilter(FileAnalysis? rep) => rep?.Arch == CpuArch.X86 ? "x86" : "x86v2";

    /// <summary>True when <paramref name="spec"/> already has the class's natural filter or one of its family (x86 and
    /// x86v2), so the parameter search does not stack a second one on top.</summary>
    public static bool HasNaturalFilter(PipelineSpec spec, string natural) =>
        spec.Filters.Any(f => natural.StartsWith(f.Method.Name, StringComparison.Ordinal) || f.Method.Name.StartsWith(natural, StringComparison.Ordinal));
}
