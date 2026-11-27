using Osmium.Core.Pipeline;

namespace Osmium.Core.Crypto;

/// <summary>
/// The key derivation needs more memory than the limit allows (<see cref="LimitBytes"/>; the caller may ask the user
/// and try again with a limit of <see cref="NeededBytes"/>), or than the system could give
/// (<see cref="NotEnoughFreeMemory"/>; a higher limit does not help). Raised before the password is used.
/// </summary>
public sealed class KdfMemoryLimitException : IOException
{
    /// <summary>Reading an archive: its key derivation needs more memory than the limit.</summary>
    public KdfMemoryLimitException(long neededBytes, long limitBytes)
        : this($"Checking the password of this archive needs {SizeText.Format(neededBytes)} of memory, more than the {SizeText.Format(limitBytes)} Osmium may use.",
            neededBytes, limitBytes, notEnoughFree: false) { }

    private KdfMemoryLimitException(string message, long neededBytes, long limitBytes, bool notEnoughFree) : base(message)
    {
        NeededBytes = neededBytes;
        LimitBytes = limitBytes;
        NotEnoughFreeMemory = notEnoughFree;
    }

    public long NeededBytes { get; }
    public long LimitBytes { get; }
    /// <summary>The memory was within the limit, but the system had too little free memory to give it.</summary>
    public bool NotEnoughFreeMemory { get; }

    /// <summary>Making an archive: the protection needs more memory than this PC has.</summary>
    internal static KdfMemoryLimitException MoreThanThisPcHas(long neededBytes, long totalBytes) =>
        new($"This password protection needs {SizeText.Format(neededBytes)} of memory to make the archive, more than this PC has.",
            neededBytes, totalBytes, notEnoughFree: false);

    /// <summary>Making an archive: the protection needs more memory than the configured limit.</summary>
    internal static KdfMemoryLimitException MoreThanTheLimit(long neededBytes, long limitBytes) =>
        new($"This password protection needs {SizeText.Format(neededBytes)} of memory to make the archive, more than the {SizeText.Format(limitBytes)} Osmium may use (Memory limit).",
            neededBytes, limitBytes, notEnoughFree: false);

    /// <summary>The work area could not be allocated.</summary>
    internal static KdfMemoryLimitException OutOfMemory(long neededBytes, long limitBytes, bool creating) =>
        new(creating
                ? $"There is not enough free memory to make the archive with this password protection (it needs {SizeText.Format(neededBytes)}). Close other programs and try again."
                : $"There is not enough free memory to check the password (it needs {SizeText.Format(neededBytes)}). Close other programs and try again.",
            neededBytes, limitBytes, notEnoughFree: true);
}

/// <summary>
/// The archive's key derivation asks for more work than any Osmium preset (more than
/// <see cref="KdfLimits.QuestionWorkKiB"/>). The caller may ask the user and open again with the slow derivation
/// allowed. Raised before the password is used.
/// </summary>
public sealed class KdfCostException(KdfParameters parameters)
    : IOException($"Checking the password of this archive takes more work than any Osmium preset asks for ({parameters.CostText()}). "
        + "It can take a long time on this PC, and the archive may have been made by modified software.")
{
    public KdfParameters Parameters { get; } = parameters;
}

/// <summary>
/// The archive is protected more weakly than any Osmium preset (below <see cref="KdfLimits"/>' minimum). Refused unless
/// the caller allows it (<see cref="ProtectionPolicy.AllowWeak"/>, after the user agreed). Raised before the password
/// is used.
/// </summary>
public sealed class WeakProtectionException : IOException
{
    public WeakProtectionException(KdfParameters parameters) : base(ReadingMessage(parameters)) => Parameters = parameters;

    private WeakProtectionException(string message, KdfParameters parameters) : base(message) => Parameters = parameters;

    public KdfParameters Parameters { get; }

    private static string ReadingMessage(KdfParameters p) => p.Algorithm == KdfAlgorithm.Pbkdf2Sha512
        ? $"This archive is protected more weakly than Osmium protects archives today ({p.CostText()}). Osmium 0.1's command line "
          + "could make such archives with --kdf-iterations; otherwise it may have been made by modified software."
        : $"This archive is protected more weakly than any Osmium preset ({p.CostText()}). It may have been made by modified software.";

    /// <summary>Making an archive with protection below the minimum.</summary>
    internal static WeakProtectionException Creating(KdfParameters p) =>
        new($"This password protection ({p.CostText()}) is weaker than any Osmium preset; Osmium does not make archives with it.", p);
}

/// <summary>
/// The archive says it is encrypted, but its contents are not protected as Osmium protects them: frames that are not
/// encrypted, frame headers that do not match the authenticated file list, a header that claims encryption without its
/// settings. This is never damage, never answered with a spare copy, and never lifted by an override.
/// </summary>
public class ModifiedArchiveException : CorruptDataException
{
    public const string DefaultMessage = "This archive says it is encrypted, but its contents are not protected as Osmium protects them. "
        + "It may have been made by modified software. Nothing was extracted.";

    public ModifiedArchiveException() : base(DefaultMessage) { }

    protected ModifiedArchiveException(string message) : base(message) { }
}

/// <summary>
/// An encrypted archive holds data outside its protected parts (a plaintext header record, bytes between frames, a
/// spare copy that differs). Refused unless the caller allows it (<see cref="ProtectionPolicy.AllowWeak"/>, after the
/// user agreed); then the archive opens and says so in its warnings.
/// </summary>
public sealed class UnprotectedDataException : ModifiedArchiveException
{
    public const string ModifiedMessage = DefaultMessage;
    public const string MayBeDamageMessage = "This encrypted archive holds data outside its protected parts. It may be damaged, or it may "
        + "have been changed by other software. Nothing was extracted.";
    public const string CopiesDifferMessage = "The header of this encrypted archive and its spare copy differ. It may be damaged, or it may "
        + "have been changed by other software. Nothing was extracted.";

    internal UnprotectedDataException(bool mayBeDamage, string? message = null)
        : base(message ?? (mayBeDamage ? MayBeDamageMessage : ModifiedMessage)) => MayBeDamage = mayBeDamage;

    /// <summary>The data could also come from damage (a spare copy that differs), not only from other software.</summary>
    public bool MayBeDamage { get; }
}
