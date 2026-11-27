using System.Reflection;
using System.Security.Cryptography;
using Osmium.Core.Container;
using Osmium.Core.Crypto;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Crypto;

public class PasswordKdfTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Salt = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private const string Secret = "Tr0ub4dor&3-sekret";
    private static readonly KdfParameters Small = KdfParameters.Argon2id(1, 64, 1);

    private static KdfRun Run(long limit = 16L << 30, ProtectionPolicy policy = ProtectionPolicy.Default, bool allowSlow = false) =>
        new() { MemoryLimitBytes = limit, Policy = policy, AllowSlow = allowSlow, Cancellation = Ct };

    private static string Master(string password, KdfParameters p, byte[]? salt = null)
    {
        var m = PasswordKdf.DeriveMaster(password, salt ?? Salt, p, Run(), creating: false);
        try { return Convert.ToHexStringLower(m); }
        finally { CryptographicOperations.ZeroMemory(m); }
    }

    [Fact]
    public void Pbkdf2KeepsTheKeysOfEarlierArchives()
    {
        // An independent PBKDF2-HMAC-SHA512 (Python's hashlib) of the NFC UTF-8 password, the salt 00..1f, 1,000 iterations.
        Assert.Equal("d2d585039baab1022a06644ed360e51838e09d50defec3042d643504d584420e", Master("pässwörd", KdfParameters.Pbkdf2(1000)));
        // The same master as the key derivation of Osmium 0.1: its password check value comes out the same.
        var master = PasswordKdf.DeriveMaster("pässwörd", Salt, KdfParameters.Pbkdf2(1000), Run(), creating: false);
        var verifier = HKDF.Expand(HashAlgorithmName.SHA256, master, 16, "osmium/v1/verify"u8.ToArray());
        Assert.Equal(verifier, ArchiveKey.Derive("pässwörd", Salt, 1000).Verifier);
    }

    [Fact]
    public void Argon2idGivesTheReferenceTag()
    {
        var c = Argon2idVectors.Cross.First(v => v.Salt.Length == 64 && v.Secret == "" && v.Data == "" && v.TagLength == 32 && v.Password == "70617373776f7264");
        Assert.Equal(c.Tag, Master("password", KdfParameters.Argon2id(c.TimeCost, c.MemoryKiB, c.Lanes), Convert.FromHexString(c.Salt)));
    }

    [Fact]
    public void ComposedAndDecomposedTextGiveTheSameKey()
    {
        Assert.Equal(Master("café", Small), Master("café", Small));
        Assert.Equal(Master("café", KdfParameters.Pbkdf2(1000)), Master("café", KdfParameters.Pbkdf2(1000)));
        Assert.NotEqual(Master("cafe", Small), Master("café", Small));
    }

    // Built in code: attribute arguments are stored as UTF-8, which turns a lone surrogate into U+FFFD.
    public static TheoryData<string> NotText => [
        "\uD800" + "abc",       // a high surrogate without its pair
        "abc" + "\uDC00",       // a low surrogate alone
        "\uDC00" + "\uD800",    // the pair the wrong way round
    ];

    [Theory]
    [MemberData(nameof(NotText))]
    public void TextThatCannotBeStoredIsRefusedWhenCreatingAndWrongWhenReading(string password)
    {
        Assert.Contains(password, c => char.IsSurrogate(c));
        using var seams = new KdfSeams();
        var creating = Assert.Throws<ArgumentException>(() => PasswordKdf.DeriveMaster(password, Salt, Small, Run(), creating: true));
        Assert.Equal("The password contains a character that cannot be stored (an unpaired surrogate).", creating.Message);
        Assert.DoesNotContain("abc", creating.Message);
        Assert.Throws<WrongPasswordException>(() => PasswordKdf.DeriveMaster(password, Salt, Small, Run(), creating: false));
        Assert.Equal(0, seams.Allocations); // nothing was derived
        Assert.Equal(64, Master("x" + char.ConvertFromUtf32(0x1F600), Small).Length); // a pair that belongs together is text
    }

    [Fact]
    public void AnEmptyPasswordIsNeverDerivedFrom()
    {
        using var seams = new KdfSeams();
        Assert.Equal("A password cannot be empty.", Assert.Throws<ArgumentException>(() => PasswordKdf.DeriveMaster("", Salt, Small, Run(), creating: true)).Message);
        Assert.Throws<WrongPasswordException>(() => PasswordKdf.DeriveMaster("", Salt, Small, Run(), creating: false));
        Assert.Throws<WrongPasswordException>(() => PasswordKdf.CheckPasswordText("", creating: false));
        Assert.Equal(0, seams.Allocations);
    }

    [Fact]
    public void MemoryOverTheLimitIsRefusedBeforeAnythingIsAllocated()
    {
        using var seams = new KdfSeams();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<KdfMemoryLimitException>(() =>
            PasswordKdf.DeriveMaster(Secret, Salt, KdfPresets.Standard, Run(limit: 512L << 20), creating: false));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
        Assert.Equal(0, seams.Allocations);
        Assert.Equal((1L << 30, 512L << 20, false), (e.NeededBytes, e.LimitBytes, e.NotEnoughFreeMemory));
        Assert.Equal("Checking the password of this archive needs 1 GiB of memory, more than the 512 MiB Osmium may use.", e.Message);
    }

    [Fact]
    public void WorkAboveTheStrongestPresetAsksFirst()
    {
        using var seams = new KdfSeams();
        var heavy = KdfParameters.Argon2id(16, 1 << 20, 4); // 16 GiB-passes
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<KdfCostException>(() => PasswordKdf.DeriveMaster(Secret, Salt, heavy, Run(), creating: false));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
        Assert.Equal(0, seams.Allocations);
        Assert.Same(heavy, e.Parameters);
        Assert.Equal("Checking the password of this archive takes more work than any Osmium preset asks for (Argon2id, 1 GiB, 16 passes). "
            + "It can take a long time on this PC, and the archive may have been made by modified software.", e.Message);
        PasswordKdf.Admit(heavy, Run(allowSlow: true)); // allowed once the user agreed (no derivation of that size runs here)
        PasswordKdf.Admit(KdfPresets.Maximum, Run()); // the strongest preset itself never asks
    }

    [Fact]
    public void TheChecksComeInOrderWeakThenMemoryThenCost()
    {
        // weak and over the memory limit: weak
        Assert.Throws<WeakProtectionException>(() => PasswordKdf.Admit(KdfParameters.Argon2id(1, 128 << 10, 4), Run(limit: 64L << 20, policy: ProtectionPolicy.Enforce)));
        // over the memory limit and the work limit: memory
        var both = KdfParameters.Argon2id(2, 8 << 20, 4);
        Assert.Throws<KdfMemoryLimitException>(() => PasswordKdf.Admit(both, Run(limit: 1L << 30)));
        Assert.Throws<KdfCostException>(() => PasswordKdf.Admit(both, Run(limit: 16L << 30)));
        // invalid before everything
        Assert.Throws<CorruptDataException>(() => PasswordKdf.Admit(KdfParameters.Argon2id(17, 128 << 10, 4), Run(limit: 64L << 20, policy: ProtectionPolicy.Enforce)));
    }

    [Fact]
    public void WeakProtectionIsRefusedUnlessAllowed()
    {
        var pbkdf2 = Assert.Throws<WeakProtectionException>(() => PasswordKdf.Admit(KdfParameters.Pbkdf2(1000), Run(policy: ProtectionPolicy.Enforce)));
        Assert.Equal("This archive is protected more weakly than Osmium protects archives today (PBKDF2, 1,000 iterations). Osmium 0.1's command "
            + "line could make such archives with --kdf-iterations; otherwise it may have been made by modified software.", pbkdf2.Message);
        var argon = Assert.Throws<WeakProtectionException>(() => PasswordKdf.Admit(KdfParameters.Argon2id(1, 64 << 10, 4), Run(policy: ProtectionPolicy.Enforce)));
        Assert.Equal("This archive is protected more weakly than any Osmium preset (Argon2id, 64 MiB, 1 pass). It may have been made by modified software.", argon.Message);
        PasswordKdf.Admit(KdfParameters.Pbkdf2(1000), Run(policy: ProtectionPolicy.AllowWeak));
        PasswordKdf.Admit(KdfParameters.Pbkdf2(100_000), Run(policy: ProtectionPolicy.Enforce));
        PasswordKdf.Admit(KdfPresets.Floor, Run(policy: ProtectionPolicy.Enforce));
    }

    [Fact]
    public void AWriterChecksTheMemoryLimitAndThePc()
    {
        var limit = Assert.Throws<KdfMemoryLimitException>(() => PasswordKdf.AdmitForWriting(KdfPresets.Standard, 512L << 20));
        Assert.Equal("This password protection needs 1 GiB of memory to make the archive, more than the 512 MiB Osmium may use (Memory limit).", limit.Message);
        var huge = KdfParameters.Argon2id(1, KdfLimits.MaxMemoryKiB, 4);
        if (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes < huge.MemoryBytes)
            Assert.EndsWith("more than this PC has.", Assert.Throws<KdfMemoryLimitException>(() => PasswordKdf.AdmitForWriting(huge, long.MaxValue)).Message);
        Assert.Throws<ArgumentException>(() => PasswordKdf.AdmitForWriting(KdfParameters.Pbkdf2(0), long.MaxValue));
        PasswordKdf.AdmitForWriting(KdfPresets.Standard, 1L << 30);
        using (TestDefaults.AsOsmiumDoes())
        {
            var weak = Assert.Throws<WeakProtectionException>(() => PasswordKdf.AdmitForWriting(KdfParameters.Pbkdf2(99_999), long.MaxValue));
            Assert.Equal("This password protection (PBKDF2, 99,999 iterations) is weaker than any Osmium preset; Osmium does not make archives with it.", weak.Message);
            Assert.Throws<WeakProtectionException>(() => PasswordKdf.AdmitForWriting(KdfParameters.Argon2id(3, 262_143, 4), long.MaxValue));
            PasswordKdf.AdmitForWriting(KdfPresets.Compatible(100_000), long.MaxValue);
            PasswordKdf.AdmitForWriting(KdfPresets.Floor, long.MaxValue);
        }
    }

    [Fact]
    public async Task TwoDerivationsNeverHoldAWorkAreaAtOnce()
    {
        int live = 0, most = 0, allocations = 0;
        Argon2id.Allocating = _ =>
        {
            Interlocked.Increment(ref allocations);
            int now = Interlocked.Increment(ref live);
            int seen;
            while (now > (seen = Volatile.Read(ref most)) && Interlocked.CompareExchange(ref most, now, seen) != seen) { }
        };
        Argon2id.BeforeFree = (_, _) => Interlocked.Decrement(ref live);
        try
        {
            var p = KdfParameters.Argon2id(1, 8 << 10, 2);
            var masters = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => Master(Secret + i, p), Ct)));
            Assert.Equal(6, masters.Distinct().Count());
        }
        finally
        {
            Argon2id.Allocating = null;
            Argon2id.BeforeFree = null;
        }
        Assert.Equal((6, 1), (allocations, most));
    }

    [Fact]
    public void ACancelledWaitDerivesNothing()
    {
        using var seams = new KdfSeams();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PasswordKdf.DeriveMaster(Secret, Salt, Small, new KdfRun { Cancellation = cts.Token }, creating: false));
        Assert.Equal(0, seams.Allocations);
        Assert.Equal(64, Master(Secret, Small).Length); // the gate was not left taken
    }

    public static TheoryData<string> Failures => ["surrogate-creating", "surrogate-reading", "memory", "cost", "weak", "invalid", "weak-creating",
        "memory-creating", "cancelled"];

    [Theory]
    [MemberData(nameof(Failures))]
    public void NoMessageHoldsThePassword(string failure)
    {
        using var cts = new CancellationTokenSource();
        string password = failure.StartsWith("surrogate", StringComparison.Ordinal) ? Secret + "\uD800" : Secret;
        Exception e = Assert.ThrowsAny<Exception>(() =>
        {
            switch (failure)
            {
                case "surrogate-creating": PasswordKdf.DeriveMaster(password, Salt, Small, Run(), creating: true); break;
                case "surrogate-reading": PasswordKdf.DeriveMaster(password, Salt, Small, Run(), creating: false); break;
                case "memory": PasswordKdf.DeriveMaster(password, Salt, KdfPresets.Standard, Run(limit: 1 << 20), creating: false); break;
                case "cost": PasswordKdf.DeriveMaster(password, Salt, KdfParameters.Argon2id(16, 1 << 20, 4), Run(), creating: false); break;
                case "weak": PasswordKdf.DeriveMaster(password, Salt, Small, Run(policy: ProtectionPolicy.Enforce), creating: false); break;
                case "invalid": PasswordKdf.DeriveMaster(password, Salt, KdfParameters.Pbkdf2(0), Run(), creating: false); break;
                case "weak-creating":
                    using (TestDefaults.AsOsmiumDoes()) PasswordKdf.DeriveMaster(password, Salt, Small, Run(), creating: true);
                    break;
                case "memory-creating": PasswordKdf.DeriveMaster(password, Salt, KdfPresets.Standard, Run(limit: 1 << 20), creating: true); break;
                default:
                    cts.Cancel();
                    PasswordKdf.DeriveMaster(password, Salt, Small, new KdfRun { Cancellation = cts.Token }, creating: false);
                    break;
            }
        });
        for (var x = e; x is not null; x = x.InnerException)
        {
            Assert.DoesNotContain(Secret, x.Message);
            Assert.DoesNotContain("Tr0ub4dor", x.ToString());
        }
    }

    [Fact]
    public void OnlyTheTestAssemblySetsTheSwitchThatAllowsWeakProtection()
    {
        // No method of Osmium stores to the switch (stsfld) or calls the setter of the per-test override (call).
        var field = typeof(PasswordKdf).GetField(nameof(PasswordKdf.WeakAllowedByDefault), BindingFlags.Static | BindingFlags.NonPublic)!;
        var setter = typeof(PasswordKdf).GetProperty(nameof(PasswordKdf.WeakAllowedHere), BindingFlags.Static | BindingFlags.NonPublic)!.SetMethod!;
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var setters = new List<string>();
        foreach (var type in typeof(PasswordKdf).Assembly.GetTypes())
            foreach (var method in type.GetMethods(All).Cast<System.Reflection.MethodBase>().Concat(type.GetConstructors(All)))
            {
                if (method == setter) continue;
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null) continue;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    int token = BitConverter.ToInt32(il, i + 1);
                    if ((il[i] == 0x80 && token == field.MetadataToken) || (il[i] is 0x28 or 0x6F && token == setter.MetadataToken))
                        setters.Add($"{type.FullName}.{method.Name}");
                }
            }
        Assert.Empty(setters);
        Assert.Null(PasswordKdf.WeakAllowedHere);
        Assert.True(PasswordKdf.WeakAllowedByDefault); // this assembly's module initializer
    }
}
