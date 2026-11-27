using System.Globalization;
using Osmium.Core.Container;

namespace Osmium.Core.Crypto;

/// <summary>The key-derivation functions of the header's EncryptionInfo record (FORMAT §1, §5): the kdf id.</summary>
public enum KdfAlgorithm : byte
{
    /// <summary>PBKDF2-HMAC-SHA512: Osmium 0.1, and Compatible protection.</summary>
    Pbkdf2Sha512 = 1,
    /// <summary>Argon2id, version 0x13 (RFC 9106): the default since Osmium 0.2.0.</summary>
    Argon2id = 2,
}

public enum KdfVerdictKind
{
    /// <summary>At or above the minimum, within the limits.</summary>
    Ok,
    /// <summary>Within the limits, but weaker than any Osmium preset (<see cref="KdfLimits"/>).</summary>
    Weak,
    /// <summary>Outside what the algorithm allows or what a reader accepts: a damaged or crafted header.</summary>
    Invalid,
}

/// <summary>What <see cref="KdfParameters.Check"/> found; <see cref="Reason"/> says why parameters are invalid.</summary>
public readonly record struct KdfVerdict(KdfVerdictKind Kind, string? Reason = null)
{
    public static KdfVerdict Ok => new(KdfVerdictKind.Ok);
    public static KdfVerdict Weak => new(KdfVerdictKind.Weak);
}

/// <summary>
/// How an archive's key is derived from its password: PBKDF2 with a number of iterations, or Argon2id with a time cost
/// (passes), a memory size in KiB and a number of lanes. The factories do not validate; <see cref="Check"/> does.
/// </summary>
public sealed record KdfParameters
{
    public KdfAlgorithm Algorithm { get; init; }
    /// <summary>PBKDF2 iterations; 0 for Argon2id.</summary>
    public int Iterations { get; init; }
    /// <summary>Argon2id passes over the memory (t); 0 for PBKDF2.</summary>
    public int TimeCost { get; init; }
    /// <summary>Argon2id memory in KiB (m); 0 for PBKDF2.</summary>
    public int MemoryKiB { get; init; }
    /// <summary>Argon2id lanes (p); 0 for PBKDF2. Part of the hash, so it never follows the processor count.</summary>
    public int Lanes { get; init; }

    public static KdfParameters Pbkdf2(int iterations) => new() { Algorithm = KdfAlgorithm.Pbkdf2Sha512, Iterations = iterations };

    public static KdfParameters Argon2id(int timeCost, int memoryKiB, int lanes) =>
        new() { Algorithm = KdfAlgorithm.Argon2id, TimeCost = timeCost, MemoryKiB = memoryKiB, Lanes = lanes };

    /// <summary>The memory a derivation needs (m KiB for Argon2id, nothing to speak of for PBKDF2).</summary>
    public long MemoryBytes => Algorithm == KdfAlgorithm.Argon2id ? MemoryKiB * 1024L : 0;

    /// <summary>Argon2id: t·m in KiB-passes, the work of one derivation; 0 for PBKDF2.</summary>
    public long WorkKiB => Algorithm == KdfAlgorithm.Argon2id ? (long)TimeCost * MemoryKiB : 0;

    /// <summary>"Argon2id, 1 GiB, 2 passes, 4 lanes" or "PBKDF2-SHA512, 600,000 iterations".</summary>
    public string Describe() => Algorithm switch
    {
        KdfAlgorithm.Pbkdf2Sha512 => $"PBKDF2-SHA512, {Count(Iterations, "iteration")}",
        KdfAlgorithm.Argon2id => $"Argon2id, {SizeText.ExactKiB(MemoryKiB)}, {Count(TimeCost, "pass", "passes")}, {Count(Lanes, "lane")}",
        _ => $"key derivation function {(byte)Algorithm}",
    };

    /// <summary>The cost as messages quote it: "Argon2id, 64 MiB, 1 pass" or "PBKDF2, 1,000 iterations".</summary>
    internal string CostText() => Algorithm switch
    {
        KdfAlgorithm.Pbkdf2Sha512 => $"PBKDF2, {Count(Iterations, "iteration")}",
        KdfAlgorithm.Argon2id => $"Argon2id, {SizeText.ExactKiB(MemoryKiB)}, {Count(TimeCost, "pass", "passes")}",
        _ => Describe(),
    };

    public override string ToString() => Describe();

    /// <summary>
    /// <see cref="KdfVerdictKind.Invalid"/> outside the algorithm's own ranges or the limits a reader accepts
    /// (<see cref="KdfLimits"/>); <see cref="KdfVerdictKind.Weak"/> below the minimum Osmium writes; else Ok.
    /// </summary>
    public KdfVerdict Check()
    {
        switch (Algorithm)
        {
            case KdfAlgorithm.Pbkdf2Sha512:
                if (Iterations < 1) return Invalid($"Key derivation cost {Iterations} is invalid.");
                if (Iterations > KdfLimits.MaxPbkdf2Iterations) return Invalid($"Key derivation cost {Iterations} exceeds the safety limit.");
                return Iterations < KdfLimits.MinPbkdf2Iterations ? KdfVerdict.Weak : KdfVerdict.Ok;
            case KdfAlgorithm.Argon2id:
                if (TimeCost < 1 || Lanes < 1 || MemoryKiB < 8L * Lanes) return Invalid($"Key derivation cost ({Describe()}) is invalid.");
                if (TimeCost > KdfLimits.MaxTimeCost || Lanes > KdfLimits.MaxLanes || MemoryKiB > KdfLimits.MaxMemoryKiB || WorkKiB > KdfLimits.MaxWorkKiB)
                    return Invalid($"Key derivation cost ({Describe()}) exceeds the safety limit.");
                return MemoryKiB < KdfLimits.MinArgon2MemoryKiB || WorkKiB < KdfLimits.MinArgon2WorkKiB ? KdfVerdict.Weak : KdfVerdict.Ok;
            default:
                return Invalid($"Key derivation function {(byte)Algorithm} is unknown.");
        }

        static KdfVerdict Invalid(string reason) => new(KdfVerdictKind.Invalid, reason);
    }

    private static string Count(int n, string one, string? many = null) =>
        n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many ?? one + "s");
}

/// <summary>The protection presets (Settings → Security → Password protection; the command line's --protection).</summary>
public static class KdfPresets
{
    public const int CompatibleIterations = 600_000;

    /// <summary>The weakest protection a reader accepts without asking (<see cref="KdfLimits"/>): Argon2id, 256 MiB, 3
    /// passes. Not offered for new archives.</summary>
    public static KdfParameters Floor { get; } = KdfParameters.Argon2id(3, 256 << 10, 4);
    /// <summary>Argon2id, 1 GiB, 2 passes, 4 lanes: the default.</summary>
    public static KdfParameters Standard { get; } = KdfParameters.Argon2id(2, 1 << 20, 4);
    /// <summary>Argon2id, 2 GiB, 3 passes, 4 lanes.</summary>
    public static KdfParameters Strong { get; } = KdfParameters.Argon2id(3, 2 << 20, 4);
    /// <summary>Argon2id, 4 GiB, 3 passes, 4 lanes.</summary>
    public static KdfParameters Maximum { get; } = KdfParameters.Argon2id(3, 4 << 20, 4);
    /// <summary>PBKDF2-HMAC-SHA512, for archives that Osmium 0.1 must open.</summary>
    public static KdfParameters Compatible(int iterations = CompatibleIterations) => KdfParameters.Pbkdf2(iterations);
    public static KdfParameters Default => Standard;
}

/// <summary>
/// What a reader accepts before it derives a key (FORMAT §5). Outside the hard limits a header is damaged or crafted;
/// below the minimum, protection is weaker than any Osmium preset; above <see cref="QuestionWorkKiB"/> a reader asks.
/// The minimum never rises: archives that earlier releases wrote must keep opening.
/// </summary>
public static class KdfLimits
{
    public const int MaxTimeCost = 16;
    public const int MaxLanes = 64;
    /// <summary>8 GiB: the work area stays within 2^30 64-bit words.</summary>
    public const int MaxMemoryKiB = 8 << 20;
    /// <summary>t·m: 32 GiB-passes.</summary>
    public const long MaxWorkKiB = 32L << 20;
    /// <summary>t·m above the strongest preset (Maximum: 3 passes over 4 GiB) asks first.</summary>
    public const long QuestionWorkKiB = 12L << 20;
    public const int MaxPbkdf2Iterations = 20_000_000;
    /// <summary>The settings minimum of Osmium 0.1 and of Compatible protection.</summary>
    public const int MinPbkdf2Iterations = 100_000;
    /// <summary>The Floor preset's memory: 256 MiB.</summary>
    public const int MinArgon2MemoryKiB = 256 << 10;
    /// <summary>The Floor preset's work: 768 MiB-passes.</summary>
    public const long MinArgon2WorkKiB = 768L << 10;
}

/// <summary>Whether a reader opens an archive whose protection is weaker than any Osmium preset (and, from item 3 of
/// the reader policy, one that holds unprotected data beside its encrypted parts).</summary>
public enum ProtectionPolicy
{
    /// <summary><see cref="Enforce"/>.</summary>
    Default,
    /// <summary>Refuse weak protection and unprotected data.</summary>
    Enforce,
    /// <summary>Open them anyway (after the user agreed). Never lifts the refusal of an archive that only claims to be
    /// encrypted.</summary>
    AllowWeak,
}

/// <summary>How a key derivation may run: the memory it may use, the policy, whether it may take more work than the
/// strongest preset, threads, cancellation and progress (0 to 1).</summary>
public sealed record KdfRun
{
    public long MemoryLimitBytes { get; init; } = MemoryBudgets.Default();
    public ProtectionPolicy Policy { get; init; } = ProtectionPolicy.Default;
    /// <summary>Allow more work than <see cref="KdfLimits.QuestionWorkKiB"/> (the user agreed).</summary>
    public bool AllowSlow { get; init; }
    /// <summary>0: as many as there are processors (never more than the lanes).</summary>
    public int Threads { get; init; }
    public CancellationToken Cancellation { get; init; }
    public IProgress<double>? Progress { get; init; }
}
