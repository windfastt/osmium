using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using Osmium.Core.Archives;
using Osmium.Core.Archives.Zip;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;
using SharpCompress.Common;
using SharpZip = SharpCompress.Archives.Zip.ZipArchive;

namespace Osmium.Core.Tests;

/// <summary>
/// ZIP files written by <see cref="ZipCreator"/>: read back by System.IO.Compression and SharpCompress (and by unzip and
/// 7z where installed) with exact names and content; never a torn copy of a changed file; never a partial file left;
/// an existing file replaced only once the new one has verified.
/// </summary>
public class ZipCreatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string P(TempDir t, params string[] parts) => Path.Combine([t.Path, .. parts]);

    /// <summary>Text, binary, already-compressed, empty and deeply nested files, Unicode names and an empty folder.</summary>
    private static void MakeTree(string root)
    {
        var rnd = new Random(11);
        Directory.CreateDirectory(Path.Combine(root, "docs", "notes"));
        Directory.CreateDirectory(Path.Combine(root, "empty-dir"));
        Directory.CreateDirectory(Path.Combine(root, "photos"));
        File.WriteAllText(Path.Combine(root, "docs", "readme.txt"), string.Concat(Enumerable.Range(0, 4000).Select(i => $"line {i} of the readme\n")));
        File.WriteAllText(Path.Combine(root, "docs", "notes", "short.txt"), "a short note\n");
        File.WriteAllBytes(Path.Combine(root, "docs", "zero.bin"), []);
        var random = new byte[1_500_000];
        rnd.NextBytes(random);
        File.WriteAllBytes(Path.Combine(root, "random.bin"), random);
        var jpeg = new byte[40_000];
        rnd.NextBytes(jpeg);
        File.WriteAllBytes(Path.Combine(root, "photos", "holiday.JPG"), jpeg);
        File.WriteAllText(Path.Combine(root, "ünïcödé — файл.txt"), "unicode name");
        File.WriteAllText(Path.Combine(root, "日本語 😀.txt"), "more unicode");
        string deep = root;
        for (int i = 0; i < 20; i++) deep = Path.Combine(deep, $"d{i}");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "leaf.txt"), "deep down");
    }

    private static Task<ZipResult> Zip(string input, string output, ZipOptions? options = null, ZipTestHooks? hooks = null, CancellationToken? ct = null) =>
        ZipCreator.CreateAsync(Scanner.Scan([input], ct: Ct), (options ?? new ZipOptions { OutputPath = output }) with { OutputPath = output }, hooks, ct ?? Ct);

    /// <summary>Every file and folder under <paramref name="root"/> as ZIP names ("dir/" for folders), relative to its parent.</summary>
    private static Dictionary<string, byte[]?> Expected(string root)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(root))!;
        string Name(string p) => Path.GetRelativePath(parent, p).Replace(Path.DirectorySeparatorChar, '/');
        var map = new Dictionary<string, byte[]?>(StringComparer.Ordinal) { [Name(root) + "/"] = null };
        foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)) map[Name(d) + "/"] = null;
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) map[Name(f)] = File.ReadAllBytes(f);
        return map;
    }

    private static Dictionary<string, byte[]?> ReadWithBcl(string zip)
    {
        using var a = ZipFile.OpenRead(zip);
        var map = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var e in a.Entries)
        {
            if (e.FullName.EndsWith('/')) { map.Add(e.FullName, null); continue; }
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            map.Add(e.FullName, ms.ToArray());
        }
        return map;
    }

    private static Dictionary<string, byte[]?> ReadWithSharpCompress(string zip)
    {
        using var a = SharpZip.Open(zip, new SharpCompress.Readers.ReaderOptions());
        var map = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var e in a.Entries)
        {
            if (e.IsDirectory) { map.Add(e.Key!, null); continue; }
            using var s = e.OpenEntryStream();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            map.Add(e.Key!, ms.ToArray());
        }
        return map;
    }

    private static void AssertSameContent(Dictionary<string, byte[]?> expected, Dictionary<string, byte[]?> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, data) in expected) Assert.Equal(data, actual[name]);
    }

    private static string[] Partials(string folder) => Directory.GetFiles(folder, "*.partial-*");

    [Fact]
    public async Task AZipRoundTripsThroughBothReaders()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        string output = P(t, "out.zip");
        var progress = new ConcurrentQueue<ZipProgress>();
        var r = await Zip(input, output, new ZipOptions { OutputPath = output, Progress = progress.Enqueue });

        var expected = Expected(input);
        AssertSameContent(expected, ReadWithBcl(output));
        AssertSameContent(expected, ReadWithSharpCompress(output));
        int files = expected.Values.Count(v => v is not null);
        Assert.Equal(Path.GetFullPath(output), r.OutputPath);
        Assert.Equal(files, r.FilesWritten);
        Assert.Equal(1, r.FilesStored); // the photo
        Assert.Equal(files - 1, r.FilesDeflated);
        Assert.Equal(0, r.LeftOut);
        Assert.Empty(r.Warnings);
        Assert.Equal(expected.Values.Sum(v => v?.Length ?? 0), r.BytesIn);
        Assert.Equal(new FileInfo(output).Length, r.BytesOut);
        Assert.Empty(Partials(t.Path));

        // Folders come before what they hold, and the entry order is the written order.
        using (var a = ZipFile.OpenRead(output))
        {
            var names = a.Entries.Select(e => e.FullName).ToList();
            Assert.Equal(names.Order(StringComparer.Ordinal), names);
            Assert.Equal("in/", names[0]);
        }
        var last = progress.Last();
        Assert.Equal((last.BytesTotal, last.FilesTotal, (string?)null), (last.BytesDone, last.FilesDone, last.Current));
        Assert.Equal(files, last.FilesTotal);
        Assert.Contains(progress, p => p.Current is not null);
    }

    [Fact]
    public async Task CompressedTypesAreStoredAndTheRestDeflated()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        Directory.CreateDirectory(input);
        var text = string.Concat(Enumerable.Repeat("compressible text ", 2000));
        foreach (var name in new[] { "a.jpg", "b.PNG", "c.mp4", "d.zip", "e.docx", "f.woff2", "g.txt", "h.bin", "noext" })
            File.WriteAllText(Path.Combine(input, name), text);

        string stored = P(t, "stored.zip"), deflated = P(t, "deflated.zip");
        var r = await Zip(input, stored);
        Assert.Equal(6, r.FilesStored);
        Assert.Equal(3, r.FilesDeflated);
        using (var a = SharpZip.Open(stored, new SharpCompress.Readers.ReaderOptions()))
            foreach (var e in a.Entries.Where(e => !e.IsDirectory))
            {
                bool shouldStore = ZipWriter.StoredExtensions.Contains(Path.GetExtension(e.Key!).TrimStart('.').ToLowerInvariant());
                Assert.Equal(shouldStore ? CompressionType.None : CompressionType.Deflate, e.CompressionType);
            }

        var all = await Zip(input, deflated, new ZipOptions { OutputPath = deflated, StoreCompressedTypes = false });
        Assert.Equal(0, all.FilesStored);
        Assert.Equal(9, all.FilesDeflated);
        using (var a = SharpZip.Open(deflated, new SharpCompress.Readers.ReaderOptions()))
            Assert.All(a.Entries.Where(e => !e.IsDirectory), e => Assert.Equal(CompressionType.Deflate, e.CompressionType));
    }

    [Theory]
    [InlineData(1975, 1980, 1, 1, 0, 0, 0)]
    [InlineData(2150, 2107, 12, 31, 23, 59, 58)]
    public async Task TimesOutsideTheZipRangeAreClamped(int year, int y, int mo, int d, int h, int mi, int s)
    {
        using var t = new TempDir();
        string input = P(t, "in");
        Directory.CreateDirectory(input);
        string file = Path.Combine(input, "old-or-new.txt");
        File.WriteAllText(file, "time travel");
        var when = new DateTime(year, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, when);
        Directory.SetLastWriteTimeUtc(input, when);
        string output = P(t, "times.zip");
        await Zip(input, output);

        using var a = ZipFile.OpenRead(output);
        var limit = new DateTime(y, mo, d, h, mi, s);
        Assert.All(a.Entries, e => Assert.Equal(limit, e.LastWriteTime.DateTime));
    }

    [Fact]
    public async Task TimesInsideTheRangeAreKeptInLocalTime()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        Directory.CreateDirectory(input);
        string file = Path.Combine(input, "now.txt");
        File.WriteAllText(file, "now");
        var when = new DateTime(2024, 3, 14, 15, 9, 26, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, when);
        string output = P(t, "now.zip");
        await Zip(input, output);

        using var a = ZipFile.OpenRead(output);
        var local = when.ToLocalTime();
        Assert.Equal(new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second / 2 * 2), a.GetEntry("in/now.txt")!.LastWriteTime.DateTime);
    }

    [Fact]
    public async Task AnExistingOutputIsRefusedWithoutOverwriteAndLeftUntouched()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        string output = P(t, "out.zip");
        File.WriteAllText(output, "an older file");
        var e = await Assert.ThrowsAsync<IOException>(() => Zip(input, output));
        Assert.Contains("already exists", e.Message);
        Assert.Equal("an older file", File.ReadAllText(output));
        Assert.Empty(Partials(t.Path));
    }

    [Fact]
    public async Task OverwriteReplacesTheOutputOnlyOnceTheNewZipVerified()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        string output = P(t, "out.zip");
        File.WriteAllText(output, "an older file");

        var options = new ZipOptions { OutputPath = output, Overwrite = true };
        var bad = new ZipTestHooks { VerifyStreamFilter = (name, s) => name.EndsWith("readme.txt", StringComparison.Ordinal) ? new FlipStream(s, 1000) : s };
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => Zip(input, output, options, bad));
        Assert.Equal("The ZIP file did not verify: in/docs/readme.txt.", e.Message);
        Assert.Equal("an older file", File.ReadAllText(output));
        Assert.Empty(Partials(t.Path));

        await Zip(input, output, options);
        AssertSameContent(Expected(input), ReadWithBcl(output));
    }

    [Fact]
    public async Task AByteChangedWhileVerifyingLeavesNoOutputAndNoPartial()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        string output = P(t, "made", "here", "out.zip");
        var hooks = new ZipTestHooks { VerifyStreamFilter = (name, s) => name == "in/random.bin" ? new FlipStream(s, 1_234_567) : s };
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => Zip(input, output, hooks: hooks));
        Assert.Equal("The ZIP file did not verify: in/random.bin.", e.Message);
        Assert.False(Directory.Exists(P(t, "made")), "the folders made for the output are removed again");
        Assert.Empty(Partials(t.Path));
    }

    [Fact]
    public async Task CancelingLeavesNoPartialFile()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        string output = P(t, "new", "out.zip");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var hooks = new ZipTestHooks { AfterRead = _ => cts.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Zip(input, output, hooks: hooks, ct: cts.Token));
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(P(t, "new")));
        Assert.Empty(Directory.GetFiles(t.Path, "*.partial-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task APausedZipWaitsUntilItIsResumed()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        string output = P(t, "out.zip");
        var gate = new PauseGate();
        gate.Pause();
        var task = Zip(input, output, new ZipOptions { OutputPath = output, Gate = gate });
        await Task.Delay(300, Ct);
        Assert.False(task.IsCompleted);
        Assert.False(File.Exists(output));
        gate.Resume();
        var r = await task;
        Assert.Equal(0, r.LeftOut);
        AssertSameContent(Expected(input), ReadWithBcl(output));
    }

    [Fact]
    public async Task AnOutputInsideAnInputFolderIsRefused()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        string output = Path.Combine(input, "docs", "self.zip");
        var e = await Assert.ThrowsAsync<IOException>(() => Zip(input, output));
        Assert.Equal("The ZIP file cannot be saved inside a folder that is being compressed.", e.Message);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(t.Path, "*.partial-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TheOutputItselfIsNeverAddedToTheZip()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        string output = P(t, "out.zip");
        await Zip(input, output);
        var scan = Scanner.Scan([input, output], ct: Ct); // the old ZIP is one of the inputs
        var r = await ZipCreator.CreateAsync(scan, new ZipOptions { OutputPath = output, Overwrite = true }, Ct);
        AssertSameContent(Expected(input), ReadWithBcl(output));
        Assert.Equal(0, r.LeftOut);
    }

    [Fact]
    public async Task AFileThatCannotBeReadIsLeftOutAndReported()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        var scan = Scanner.Scan([input], ct: Ct);
        string gone = Path.Combine(input, "docs", "readme.txt");
        File.Delete(gone); // after the scan: the file is gone when the ZIP is written
        string output = P(t, "out.zip");
        var r = await ZipCreator.CreateAsync(scan, new ZipOptions { OutputPath = output }, Ct);

        Assert.Equal(1, r.LeftOutUnreadable);
        Assert.Equal(1, r.LeftOut);
        var warning = Assert.Single(r.Warnings);
        Assert.StartsWith(gone + ": ", warning);
        AssertSameContent(Expected(input), ReadWithBcl(output));
    }

    [Fact]
    public async Task AFileChangedWhileItIsReadIsLeftOutWithoutATornCopy()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        string victim = Path.Combine(input, "random.bin");
        int changes = 0;
        var hooks = new ZipTestHooks
        {
            AfterRead = p =>
            {
                if (p != victim || Interlocked.Increment(ref changes) > 1) return;
                using var f = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                f.Write("appended while the ZIP was written"u8);
            },
        };
        string output = P(t, "out.zip");
        var r = await Zip(input, output, hooks: hooks);

        Assert.Equal(1, changes);
        Assert.Equal(1, r.LeftOutChanged);
        Assert.Equal(0, r.LeftOutUnreadable);
        Assert.Equal($"{victim}: it changed while it was being read, so it was left out", Assert.Single(r.Warnings));
        var expected = Expected(input);
        expected.Remove("in/random.bin");
        AssertSameContent(expected, ReadWithBcl(output));
        Assert.Empty(Partials(t.Path));
    }

    [Fact]
    public async Task FilesThatKeepChangingFailTheZipAfterTwoRestarts()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        // One more file changes in each pass: three passes, three changes.
        var victims = new[] { Path.Combine(input, "docs", "readme.txt"), Path.Combine(input, "random.bin"), Path.Combine(input, "photos", "holiday.JPG") };
        string first = Directory.GetFiles(input, "leaf.txt", SearchOption.AllDirectories).Single(); // the first file of every pass
        var changed = new List<string>();
        bool changedThisPass = false;
        var hooks = new ZipTestHooks
        {
            AfterRead = p =>
            {
                if (p == first) changedThisPass = false;
                if (changedThisPass || !victims.Contains(p) || changed.Contains(p)) return;
                File.SetLastWriteTimeUtc(p, File.GetLastWriteTimeUtc(p).AddSeconds(-10));
                changed.Add(p);
                changedThisPass = true;
            },
        };
        string output = P(t, "out.zip");
        var e = await Assert.ThrowsAsync<SourceChangedException>(() => Zip(input, output, hooks: hooks));
        Assert.Equal(3, changed.Count);
        Assert.Contains("three times in a row", e.Message);
        Assert.Contains(changed[2], e.Message);
        Assert.False(File.Exists(output));
        Assert.Empty(Partials(t.Path));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFileOver4GiBIsWrittenWithZip64()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OSMIUM_SLOW_TESTS") == "1", "Writes a 4 GiB sparse file; set OSMIUM_SLOW_TESTS=1 to run it.");
        using var t = new TempDir();
        string input = P(t, "in");
        Directory.CreateDirectory(input);
        long size = (4L << 30) + 12345;
        using (var f = new FileStream(Path.Combine(input, "big.bin"), FileMode.CreateNew)) f.SetLength(size);
        string output = P(t, "big.zip");
        var r = await Zip(input, output);
        Assert.Equal(size, r.BytesIn);
        using var a = ZipFile.OpenRead(output);
        Assert.Equal(size, a.GetEntry("in/big.bin")!.Length);
    }

    // ------------------------------------------------------------------ other tools (compat workflow, when installed)

    private static (int Code, string Output)? Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync(Ct);
            var stderr = p.StandardError.ReadToEndAsync(Ct);
            p.WaitForExit();
            return (p.ExitCode, stdout.Result + stderr.Result);
        }
        catch (Win32Exception)
        {
            return null; // not installed
        }
    }

    private static void RequireTool(string tool, params string[] probe)
    {
        if (Run(tool, probe) is not null) return;
        if (Environment.GetEnvironmentVariable("OSMIUM_REQUIRE_INTEROP") == "1") Assert.Fail($"'{tool}' is not installed, but OSMIUM_REQUIRE_INTEROP=1 requires it.");
        Assert.Skip($"'{tool}' is not installed; the compatibility workflow installs it.");
    }

    private static bool IsAscii(string s) => s.All(char.IsAscii);

    /// <summary>The files (not folders) of a tree as ZIP names, in name order, optionally only some of them.</summary>
    private static List<KeyValuePair<string, byte[]?>> FilesOf(string root, Func<string, bool>? which = null) =>
        [.. Expected(root).Where(kv => kv.Value is not null && (which?.Invoke(kv.Key) ?? true)).OrderBy(kv => kv.Key, StringComparer.Ordinal)];

    /// <summary>Each entry's name as stored and its general-purpose flags in the local header and in the central
    /// directory, read from the ZIP's bytes (a small ZIP without ZIP64 records).</summary>
    private static List<(string Name, ushort LocalFlags, ushort CentralFlags)> Headers(string zip)
    {
        var b = File.ReadAllBytes(zip);
        int eocd = b.AsSpan().LastIndexOf([(byte)0x50, (byte)0x4B, (byte)0x05, (byte)0x06]);
        Assert.True(eocd >= 0, "no end of central directory record");
        int count = BitConverter.ToUInt16(b, eocd + 10), at = (int)BitConverter.ToUInt32(b, eocd + 16);
        var list = new List<(string, ushort, ushort)>();
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(0x02014B50u, BitConverter.ToUInt32(b, at));
            ushort central = BitConverter.ToUInt16(b, at + 8);
            int nameLength = BitConverter.ToUInt16(b, at + 28), extra = BitConverter.ToUInt16(b, at + 30), comment = BitConverter.ToUInt16(b, at + 32);
            int local = (int)BitConverter.ToUInt32(b, at + 42);
            var name = b.AsSpan(at + 46, nameLength);
            Assert.Equal(0x04034B50u, BitConverter.ToUInt32(b, local));
            Assert.True(name.SequenceEqual(b.AsSpan(local + 30, BitConverter.ToUInt16(b, local + 26))), "local and central names differ");
            list.Add((System.Text.Encoding.UTF8.GetString(name), BitConverter.ToUInt16(b, local + 6), central));
            at += 46 + nameLength + extra + comment;
        }
        return list;
    }

    [Fact]
    public async Task NamesThatAreNotAsciiAreStoredAsUtf8AndMarkedSo()
    {
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        string output = P(t, "out.zip");
        await Zip(input, output);

        var headers = Headers(output);
        Assert.Equal(Expected(input).Keys.Order(StringComparer.Ordinal), headers.Select(h => h.Name).Order(StringComparer.Ordinal));
        const ushort Utf8 = 1 << 11; // general-purpose flag bit 11: the name is UTF-8
        var unicode = headers.Where(h => !IsAscii(h.Name)).ToList();
        Assert.Equal(2, unicode.Count);
        Assert.All(unicode, h => Assert.True((h.LocalFlags & Utf8) != 0 && (h.CentralFlags & Utf8) != 0, $"{h.Name} is not marked as UTF-8"));
    }

    [Fact]
    [Trait("Category", "ZipInterop")]
    public async Task UnzipTestsAndExtractsTheZipExactly()
    {
        RequireTool("unzip", "-v");
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        string output = P(t, "out.zip");
        await Zip(input, output);

        var test = Run("unzip", "-t", output)!.Value;
        Assert.True(test.Code == 0, test.Output);
        var x = Run("unzip", "-q", output, "-d", P(t, "x"))!.Value;
        Assert.True(x.Code == 0, x.Output);
        Assert.True(Directory.Exists(P(t, "x", "in", "empty-dir")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(FilesOf(input), FilesOf(P(t, "x", "in")));
            return;
        }

        // The unzip on Windows runners is the MSYS build of Info-ZIP: it ignores the UTF-8 flag (bit 11, checked in
        // NamesThatAreNotAsciiAreStoredAsUtf8AndMarkedSo) and writes the raw bytes through the ANSI API. Its ASCII
        // names are compared exactly; the others go through Windows' own tar, which reads the flag.
        TestContext.Current.TestOutputHelper?.WriteLine("unzip here ignores the UTF-8 name flag: names that are not ASCII are compared through Windows' tar.exe.");
        var unzipped = FilesOf(P(t, "x", "in")).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        Assert.Equal(FilesOf(input).Count, unzipped.Count); // every file came out, under some name
        foreach (var (name, data) in FilesOf(input, IsAscii)) Assert.Equal(data, unzipped[name]);
        string tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar))
        {
            TestContext.Current.TestOutputHelper?.WriteLine("Windows' tar.exe (Windows 10 1803 and later) is missing: the names that are not ASCII were not compared here.");
            return;
        }
        Directory.CreateDirectory(P(t, "tar"));
        var tx = Run(tar, "-xf", output, "-C", P(t, "tar"))!.Value;
        Assert.True(tx.Code == 0, tx.Output);
        var unicode = FilesOf(input, n => !IsAscii(n));
        Assert.Equal(2, unicode.Count);
        Assert.Equal(unicode, FilesOf(P(t, "tar", "in"), n => !IsAscii(n)));
    }

    [Fact]
    [Trait("Category", "ZipInterop")]
    public async Task SevenZipTestsAndExtractsTheZipExactly()
    {
        RequireTool("7z", "i");
        using var t = new TempDir();
        string input = P(t, "in");
        MakeTree(input);
        string output = P(t, "out.zip");
        await Zip(input, output);

        var test = Run("7z", "t", output)!.Value;
        Assert.True(test.Code == 0, test.Output);
        var x = Run("7z", "x", "-y", "-o" + P(t, "x"), output)!.Value;
        Assert.True(x.Code == 0, x.Output);
        Assert.Equal(Expected(input).Where(kv => kv.Value is not null).OrderBy(kv => kv.Key, StringComparer.Ordinal),
            Expected(P(t, "x", "in")).Where(kv => kv.Value is not null).OrderBy(kv => kv.Key, StringComparer.Ordinal));
        Assert.True(Directory.Exists(P(t, "x", "in", "empty-dir")));
    }

    /// <summary>Passes a stream through, with the byte at one position flipped.</summary>
    private sealed class FlipStream(Stream inner, long at) : Stream
    {
        private long _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            if (at >= _pos && at < _pos + n) buffer[offset + (at - _pos)] ^= 0x01;
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
