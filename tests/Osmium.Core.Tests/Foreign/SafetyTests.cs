using System.Formats.Tar;
using System.Text;
using Osmium.Core.Archives;
using Osmium.Core.Container;

namespace Osmium.Core.Tests.Foreign;

/// <summary>The safety rules through real formats: unsafe names from a ZIP or TAR never leave the destination, the
/// password never appears in any message, and (on Windows) the Mark-of-the-Web survives a read-only entry.</summary>
public class SafetyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Names() =>
        ["../evil.txt", "a/../../evil.txt", "/abs.txt", "C:\\evil.txt", "//host/share/evil.txt", "CON", "aux.txt", "x.exe:Zone.Identifier", "name.", "\u202Eevil.txt"];

    private static byte[] TarOf(params (string Name, string Text)[] entries)
    {
        var ms = new MemoryStream();
        using (var w = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
            foreach (var (name, text) in entries)
                w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        return ms.ToArray();
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void UnsafeNamesFromRealFormatsStayInside(string name)
    {
        foreach (var kind in new[] { "zip", "tar", "tgz" })
        {
            using var t = new TempDir();
            Directory.CreateDirectory(t.Sub("outside"));
            File.WriteAllText(Path.Combine(t.Sub("outside"), "sentinel.txt"), "untouched");
            (string, string)[] entries = [("good.txt", "good"), (name, "payload")];
            byte[] data = kind switch
            {
                "zip" => ForeignData.Zip(entries.Select(e => (e.Item1, Encoding.UTF8.GetBytes(e.Item2)))),
                "tar" => TarOf(entries),
                _ => ForeignData.GZip(TarOf(entries)),
            };
            string archive = ForeignData.Write(t, "a." + kind, data);
            using var s = ArchiveSources.Open(archive, null, Ct);
            var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
            Assert.True(r.FilesOk >= 1);
            Assert.Equal(2, r.FilesOk + r.SkippedUnsafe);
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(t.Sub("outside"), "sentinel.txt")));
            Assert.Equal(["a." + kind, "out", "outside"], Directory.GetFileSystemEntries(t.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Single(Directory.GetFiles(t.Sub("outside")));
            foreach (var f in Directory.GetFiles(t.Sub("out"), "*", SearchOption.AllDirectories))
            {
                Assert.StartsWith(Path.GetFullPath(t.Sub("out")) + Path.DirectorySeparatorChar, Path.GetFullPath(f));
                Assert.DoesNotContain(':', Path.GetFileName(f));
            }
        }
    }

    [Fact]
    public void ThePasswordNeverAppearsInMessages()
    {
        const string wrong = "Hunter2-not-the-password", right7z = "Pässwörd ü";
        var texts = new List<string>();
        void Collect(Action a)
        {
            try { a(); }
            catch (Exception e) { texts.Add(e.ToString()); }
        }
        void Result(ArchiveExtractResult r) => texts.AddRange([.. r.Errors, .. r.Warnings]);
        using var t = new TempDir();
        foreach (var (fixture, right) in new[] { ("zip/aes256.zip", "secret"), ("zip/zipcrypto.zip", "secret"), ("7z/aes.7z", right7z), ("7z/aes-headers.7z", right7z) })
        {
            foreach (var pw in new[] { wrong, right })
            {
                Collect(() =>
                {
                    using var s = ArchiveSources.Open(ForeignData.Fixture(fixture.Split('/')), new ArchiveOpenOptions { Password = () => pw }, Ct);
                    texts.AddRange(s.Warnings);
                    texts.AddRange(s.Details.Select(d => d.Value));
                    Collect(() => Result(s.Test(null, null, Ct)));
                    Collect(() => Result(s.Extract(t.Sub(Guid.NewGuid().ToString("N")), new ArchiveExtractRequest(), null, Ct)));
                });
            }
            // A damaged copy, with the right password.
            var bytes = File.ReadAllBytes(ForeignData.Fixture(fixture.Split('/')));
            bytes[bytes.Length / 2] ^= 0x40;
            string damaged = ForeignData.Write(t, "damaged-" + Path.GetFileName(fixture), bytes);
            Collect(() =>
            {
                using var s = ArchiveSources.Open(damaged, new ArchiveOpenOptions { Password = () => right }, Ct);
                Collect(() => Result(s.Extract(t.Sub(Guid.NewGuid().ToString("N")), new ArchiveExtractRequest(), null, Ct)));
            });
        }
        Assert.NotEmpty(texts);
        foreach (var text in texts)
            foreach (var secret in new[] { wrong, "secret", right7z })
                Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public void AReadOnlyEntryFromADownloadedArchiveKeepsTheMarkOfTheWeb()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Mark-of-the-Web is an NTFS alternate data stream.");
        using var t = new TempDir();
        var z = new ZipCraft();
        var exe = z.Add("tool.exe", Encoding.ASCII.GetBytes("MZ not really a program"));
        exe.External = 0x01; // read-only, from a DOS/Windows tool
        string archive = ForeignData.Write(t, "downloaded.zip", z.Build());
        File.WriteAllText(archive + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        using var s = ArchiveSources.Open(archive, null, Ct);
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal(1, r.FilesOk);
        Assert.Empty(r.Warnings);
        string target = Path.Combine(t.Sub("out"), "tool.exe");
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        Assert.Contains("ZoneId=3", File.ReadAllText(target + ":Zone.Identifier"));
        File.SetAttributes(target, FileAttributes.Normal);
    }

    [Fact]
    public void OnWindowsTrailingDotsAndDeviceNamesNeverReachTheFileSystem()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows strips trailing dots and opens devices; elsewhere the names are ordinary.");
        using var t = new TempDir();
        var z = new ZipCraft();
        z.Add("trailing.", "dot"u8.ToArray());
        z.Add("space ", "space"u8.ToArray());
        z.Add("CON", "device"u8.ToArray());
        z.Add("nul.txt", "device"u8.ToArray());
        using var s = ArchiveSources.Open(ForeignData.Write(t, "w.zip", z.Build()), null, Ct);
        var r = s.Extract(t.Sub("out"), new ArchiveExtractRequest(), null, Ct);
        Assert.Equal((2, 2), (r.FilesOk, r.SkippedUnsafe));
        Assert.Equal(["space_", "trailing_"], Directory.GetFiles(t.Sub("out")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }
}
