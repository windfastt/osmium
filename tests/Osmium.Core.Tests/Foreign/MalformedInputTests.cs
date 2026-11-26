using System.IO.Compression;
using Osmium.Core.Archives;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Tests.Foreign;

/// <summary>
/// Every format, truncated at 64 points and with 200 seeded byte flips: opening, testing and extracting may only end
/// in a result or in one of the expected exceptions (damage, password, not supported, a limit), never in a crash, and
/// each case must finish within 10 seconds. A null reference, an index out of range or another exception that means a
/// programming error fails the case when Osmium's own code threw it, even though it is reported as damage: only the
/// decoder libraries may report bad data that way.
/// </summary>
public class MalformedInputTests
{
    /// <summary>The password for kinds without one of their own; an encrypted fixture gets its real password, so that
    /// damaged bytes reach decryption and the decoders behind it ("…!wrong": a wrong one, which stops at the check).</summary>
    private const string Password = "Pässwörd ü";

    private static string PasswordOf(string kind) =>
        kind.EndsWith("!wrong", StringComparison.Ordinal) ? "wrong" : FixtureTests.PasswordFor(kind) ?? Password;
    private static readonly TimeSpan CaseBudget = TimeSpan.FromSeconds(10);

    private static byte[] Sample(string kind)
    {
        (string, byte[])[] tree = [("a/", []), ("a/one.txt", ForeignData.Text(20_000)), ("a/two.bin", ForeignData.Text(3000, 2)), ("a/zero", [])];
        return kind switch
        {
            "zip" => ForeignData.Zip(tree),
            "zip-stored" => ForeignData.Zip(tree, CompressionLevel.NoCompression),
            "tar" => ForeignData.Tar(tree),
            "tar.gz" => ForeignData.GZip(ForeignData.Tar(tree)),
            "tar.bz2" => ForeignData.BZip2(ForeignData.Tar(tree)),
            "tar.zst" => ForeignData.Zstd(ForeignData.Tar(tree)),
            "gz" => ForeignData.GZip(ForeignData.Text(20_000)),
            "bz2" => ForeignData.BZip2(ForeignData.Text(20_000)),
            "zst" => ForeignData.Zstd(ForeignData.Text(20_000)),
            _ => File.ReadAllBytes(ForeignData.Fixture(kind.Split('/'))),
        };
    }

    public static TheoryData<string> Kinds() =>
    [
        "zip", "zip-stored", "tar", "tar.gz", "tar.bz2", "tar.zst", "gz", "bz2", "zst",
        "zip/aes256.zip", "zip/zipcrypto.zip", "zip/aes256.zip!wrong", "zip/zipcrypto.zip!wrong",
        "zip/deflate64.zip", "zip/lzma.zip", "zip/ppmd.zip", "zip/bzip2.zip",
        "7z/lzma2-solid.7z", "7z/lzma.7z", "7z/ppmd.7z", "7z/bcj2.7z", "7z/bcj.7z", "7z/delta.7z", "7z/aes.7z", "7z/aes-headers.7z",
        "7z/aes.7z!wrong",
        "7z/bzip2.7z", "7z/deflate.7z",
        "xz/crc64.xz", "xz/multiblock.xz", "xz/concat.xz", "xz/x86.xz", "xz/tree.tar.xz",
    ];

    private static bool Expected(Exception e) => e is CorruptDataException or WrongPasswordException or PasswordRequiredException
        or NotSupportedException or ExpansionLimitException or FreeSpaceException or EntryLimitException;

    /// <summary>Where in Osmium's own code a programming-error exception was thrown ("Type.Method"), or null when a
    /// decoder library threw it (the first frame outside the runtime decides).</summary>
    internal static string? OwnFault(Exception e)
    {
        foreach (var frame in new System.Diagnostics.StackTrace(e).GetFrames())
        {
            var type = frame.GetMethod()?.DeclaringType;
            string? assembly = type?.Assembly.GetName().Name;
            if (assembly is null || assembly == "mscorlib" || assembly.StartsWith("System", StringComparison.Ordinal)
                || assembly.StartsWith("Microsoft", StringComparison.Ordinal)) continue;
            return assembly == typeof(ArchiveSources).Assembly.GetName().Name ? $"{type!.FullName}.{frame.GetMethod()!.Name}" : null;
        }
        return null;
    }

    /// <summary>Open, list, test and (sometimes) extract; returns the unexpected exception, if any.</summary>
    private static Exception? Exercise(string path, string dest, bool extract, string password)
    {
        Exception? fault = null;
        Archives.Foreign.ForeignErrors.FaultObserver = e => { if (fault is null && OwnFault(e) is not null) fault = e; };
        try
        {
            return Run();
        }
        finally { Archives.Foreign.ForeignErrors.FaultObserver = null; }

        Exception? Run()
        {
            var unexpected = Attempt(path, dest, extract, password);
            return unexpected ?? (fault is null ? null : new InvalidOperationException($"a fault in {OwnFault(fault)} was taken for damage", fault));
        }
    }

    private static Exception? Attempt(string path, string dest, bool extract, string password)
    {
        try
        {
            var limits = ImportLimits.Default with { ExpansionFloorBytes = 64 << 20, MaxExpansionRatio = 1000, MemoryBudgetBytes = 256 << 20 };
            using var s = ArchiveSources.Open(path, new ArchiveOpenOptions { Password = () => password, Limits = limits });
            _ = s.Items.Count;
            _ = s.Details.Count;
            s.Test(null, null, CancellationToken.None);
            if (extract) s.Extract(dest, new ArchiveExtractRequest { Overwrite = OverwriteMode.Overwrite }, null, CancellationToken.None);
            return null;
        }
        catch (Exception e) when (Expected(e)) { return null; }
        catch (Exception e) { return e; }
    }

    private static void Run(string kind, Func<byte[], int, byte[]> mutate, int cases)
    {
        string password = PasswordOf(kind);
        kind = kind.Split('!')[0];
        var original = Sample(kind);
        using var t = new TempDir();
        string archive = t.Sub("case." + (kind.Contains('/') ? Path.GetExtension(kind).TrimStart('.') : kind));
        var failures = new List<string>();
        for (int i = 0; i < cases; i++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var data = mutate(original, i);
            File.WriteAllBytes(archive, data);
            string dest = t.Sub("out" + i);
            var task = Task.Run(() => Exercise(archive, dest, extract: i % 4 == 0, password));
            if (!task.Wait(CaseBudget)) { failures.Add($"case {i}: did not finish within {CaseBudget.TotalSeconds:F0} s"); break; }
            if (task.Result is { } e) failures.Add($"case {i}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}{(e.InnerException is { } inner ? $"\n{inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}" : "")}");
            if (Directory.Exists(dest))
                foreach (var f in Directory.GetFiles(dest, "*", SearchOption.AllDirectories))
                    Assert.DoesNotContain(".osmium-part-", f);
        }
        Assert.True(failures.Count == 0, string.Join("\n\n", failures.Take(3)));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void TruncatedAt64Points(string kind) =>
        Run(kind, (data, i) => data[..(int)((long)data.Length * i / 64)], 64);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void With200SeededByteFlips(string kind) =>
        Run(kind, (data, i) =>
        {
            var rnd = new Random(1000 + i);
            var copy = (byte[])data.Clone();
            int flips = 1 + rnd.Next(4);
            for (int k = 0; k < flips; k++)
            {
                int at = rnd.Next(copy.Length);
                copy[at] = rnd.Next(3) == 0 ? (byte)rnd.Next(256) : (byte)(copy[at] ^ (1 << rnd.Next(8)));
            }
            return copy;
        }, 200);
}
