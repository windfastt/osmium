using System.Security.Cryptography;
using System.Text;
using Osmium.Core.Container;

namespace Osmium.Core.Crypto;

/// <summary>
/// Turns a password into an archive's master key (FORMAT §5): the password in normalisation form C, as UTF-8, through
/// PBKDF2-HMAC-SHA512 or Argon2id. Every check that needs no password comes first (<see cref="Admit"/>), so a refusal
/// or a question never costs a password prompt; one derivation runs at a time in the process.
/// </summary>
public static class PasswordKdf
{
    public const int MasterKeyBytes = 32;

    internal const string EmptyPassword = "A password cannot be empty.";
    internal const string UnpairedSurrogate = "The password contains a character that cannot be stored (an unpaired surrogate).";

    /// <summary>Two derivations never hold two work areas at once.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Set by Osmium's own test assembly only (never by Osmium): <see cref="ProtectionPolicy.Default"/> then allows weak
    /// protection, so that tests can make small archives cheaply. Tests of the refusal pass
    /// <see cref="ProtectionPolicy.Enforce"/>.
    /// </summary>
    internal static bool WeakAllowedByDefault;

    private static readonly AsyncLocal<bool?> s_weakAllowedHere = new();

    /// <summary>Tests: overrides <see cref="WeakAllowedByDefault"/> for the current flow of execution (null: no override),
    /// so that a test of the refusal does not change what tests running in parallel see.</summary>
    internal static bool? WeakAllowedHere { get => s_weakAllowedHere.Value; set => s_weakAllowedHere.Value = value; }

    private static bool WeakAllowed => WeakAllowedHere ?? WeakAllowedByDefault;

    internal static ProtectionPolicy Resolve(ProtectionPolicy p) =>
        p != ProtectionPolicy.Default ? p : WeakAllowed ? ProtectionPolicy.AllowWeak : ProtectionPolicy.Enforce;

    /// <summary>
    /// The checks before a password is asked for, in this order: the hard limits (outside them:
    /// <see cref="Pipeline.CorruptDataException"/>), the minimum (<see cref="WeakProtectionException"/> unless the policy
    /// allows weak protection), the memory limit (<see cref="KdfMemoryLimitException"/>), and the work of the strongest
    /// preset (<see cref="KdfCostException"/> unless <see cref="KdfRun.AllowSlow"/>).
    /// </summary>
    public static void Admit(KdfParameters parameters, KdfRun run)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(run);
        var verdict = parameters.Check();
        if (verdict.Kind == KdfVerdictKind.Invalid) throw new Pipeline.CorruptDataException(verdict.Reason!);
        if (verdict.Kind == KdfVerdictKind.Weak && Resolve(run.Policy) != ProtectionPolicy.AllowWeak) throw new WeakProtectionException(parameters);
        if (parameters.Algorithm != KdfAlgorithm.Argon2id) return;
        if (parameters.MemoryBytes > run.MemoryLimitBytes) throw new KdfMemoryLimitException(parameters.MemoryBytes, run.MemoryLimitBytes);
        if (parameters.WorkKiB > KdfLimits.QuestionWorkKiB && !run.AllowSlow) throw new KdfCostException(parameters);
    }

    /// <summary>
    /// The checks a writer makes before it derives: parameters within the limits (else <see cref="ArgumentException"/>)
    /// and at or above the minimum (else <see cref="WeakProtectionException"/>), and memory within what this PC has and
    /// within <paramref name="memoryLimitBytes"/> (else <see cref="KdfMemoryLimitException"/>).
    /// </summary>
    internal static void AdmitForWriting(KdfParameters parameters, long memoryLimitBytes)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var verdict = parameters.Check();
        if (verdict.Kind == KdfVerdictKind.Invalid) throw new ArgumentException(verdict.Reason);
        if (verdict.Kind == KdfVerdictKind.Weak && !WeakAllowed) throw WeakProtectionException.Creating(parameters);
        if (parameters.Algorithm != KdfAlgorithm.Argon2id) return;
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total > 0 && parameters.MemoryBytes > total) throw KdfMemoryLimitException.MoreThanThisPcHas(parameters.MemoryBytes, total);
        if (parameters.MemoryBytes > memoryLimitBytes) throw KdfMemoryLimitException.MoreThanTheLimit(parameters.MemoryBytes, memoryLimitBytes);
    }

    /// <summary>
    /// An empty password, or one that is not valid text (an unpaired surrogate), cannot have made an archive: reading,
    /// it is a wrong password (<see cref="WrongPasswordException"/>); creating, it is refused
    /// (<see cref="ArgumentException"/>). Never derives, and no message holds the password.
    /// </summary>
    public static void CheckPasswordText(string password, bool creating)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0)
        {
            if (creating) throw new ArgumentException(EmptyPassword);
            throw new WrongPasswordException();
        }
        if (!IsValidText(password))
        {
            if (creating) throw new ArgumentException(UnpairedSurrogate);
            throw new WrongPasswordException();
        }
    }

    private static bool IsValidText(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            else if (char.IsSurrogate(s[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// The 32-byte master key of <paramref name="password"/>, in a pinned array the caller zeroes. Admits the
    /// parameters again first (reading: <see cref="Admit"/>; creating: the writer's checks against
    /// <see cref="KdfRun.MemoryLimitBytes"/>), so that no caller can skip them; then checks the text, waits for the
    /// gate, and derives. The password bytes and, on failure, the key are wiped.
    /// </summary>
    internal static byte[] DeriveMaster(string password, ReadOnlySpan<byte> salt, KdfParameters parameters, KdfRun run, bool creating)
    {
        if (creating) AdmitForWriting(parameters, run.MemoryLimitBytes);
        else Admit(parameters, run);
        CheckPasswordText(password, creating);
        var master = GC.AllocateArray<byte>(MasterKeyBytes, pinned: true);
        byte[]? bytes = null;
        Gate.Wait(run.Cancellation);
        try
        {
            bytes = PasswordBytes(password, creating);
            if (parameters.Algorithm == KdfAlgorithm.Pbkdf2Sha512)
                Rfc2898DeriveBytes.Pbkdf2(bytes, salt, master, parameters.Iterations, HashAlgorithmName.SHA512);
            else
            {
                Action<double>? progress = run.Progress is { } p ? p.Report : null;
                int threads = run.Threads > 0 ? run.Threads : Environment.ProcessorCount;
                try
                {
                    Argon2id.Hash(bytes, salt, [], [], parameters.TimeCost, parameters.MemoryKiB, parameters.Lanes, master, threads, run.Cancellation, progress);
                }
                catch (WorkAreaUnavailableException e)
                {
                    throw KdfMemoryLimitException.OutOfMemory(e.Bytes, run.MemoryLimitBytes, creating);
                }
            }
            return master;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(master);
            throw;
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            Gate.Release();
        }
    }

    /// <summary>UTF-8 of the NFC form, in a pinned array the caller zeroes. The normalised string is one more copy that
    /// cannot be wiped; it lives only for this call.</summary>
    private static byte[] PasswordBytes(string password, bool creating)
    {
        string normalized;
        try
        {
            normalized = password.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Not valid text after all: as CheckPasswordText says, never with the password in the message.
            if (creating) throw new ArgumentException(UnpairedSurrogate);
            throw new WrongPasswordException();
        }
        var bytes = GC.AllocateArray<byte>(Encoding.UTF8.GetByteCount(normalized), pinned: true);
        Encoding.UTF8.GetBytes(normalized, bytes);
        return bytes;
    }
}
