using System.Text;
using Osmium.Core.Archives;
using Osmium.Core.Archives.Foreign;
using Osmium.Core.Container;

namespace Osmium.Core.Tests.Foreign;

/// <summary>The rules of the guarded writer, through a source held in memory: names, links, collisions, existing
/// files, metadata, cleanup. Every case also checks that nothing appeared outside the destination.</summary>
public class GuardedExtractorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string SentinelText = "outside the destination";

    /// <summary>A test folder with the archive file, the destination "out" and a sentinel in "outside".</summary>
    internal sealed class Setup : IDisposable
    {
        public TempDir T { get; } = new();
        public string Out => T.Sub("out");
        public string Archive { get; }
        public string Sentinel => Path.Combine(T.Path, "outside", "sentinel.txt");

        public Setup()
        {
            Archive = T.Sub("archive.bin");
            File.WriteAllBytes(Archive, new byte[1024]);
            Directory.CreateDirectory(T.Sub("outside"));
            File.WriteAllText(Sentinel, SentinelText);
        }

        public string At(params string[] parts) => Path.Combine([Out, .. parts]);

        public ArchiveExtractResult Extract(IEnumerable<MemoryEntry> entries, ArchiveExtractRequest? request = null, ArchiveOpenOptions? options = null)
        {
            using var s = new MemorySource(Archive, entries, options);
            return s.Extract(Out, request ?? new ArchiveExtractRequest(), null, Ct);
        }

        /// <summary>The test folder holds only the archive, the destination and the untouched sentinel.</summary>
        public void AssertNothingOutside()
        {
            var expected = new List<string> { "archive.bin", "outside" };
            if (Directory.Exists(Out)) expected.Add("out");
            Assert.Equal(expected.Order(StringComparer.Ordinal), Directory.GetFileSystemEntries(T.Path).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal));
            Assert.Equal([Sentinel], Directory.GetFileSystemEntries(T.Sub("outside"), "*", SearchOption.AllDirectories));
            Assert.Equal(SentinelText, File.ReadAllText(Sentinel));
        }

        public List<string> FilesUnder() => !Directory.Exists(Out) ? []
            : Directory.GetFiles(Out, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Out, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

        public void Dispose() => T.Dispose();
    }

    public static TheoryData<string> UnsafeNames() =>
    [
        "../evil", "..\\evil", "a/../../evil", "a/b/../../../evil", "/abs", "\\abs", "C:\\x", "C:x", "c:/x",
        "\\\\host\\share\\x", "//host/x", "//?/C:/x", "\\\\.\\pipe\\x", "CON", "con", "aux.txt", "dir/NUL.tar.gz", "COM1",
        "lpt9.log", "CONIN$", "COM\u00B9", "dir/CON /x.txt", new string('n', 300), "a/" + new string('b', 256) + "/c",
        string.Join('/', Enumerable.Repeat("d", 1000)) + "/deep.txt", ".", "./", "", "/",
    ];

    [Theory]
    [MemberData(nameof(UnsafeNames))]
    public void UnsafeNamesAreRefusedAndReported(string name)
    {
        using var s = new Setup();
        var r = s.Extract([MemoryEntry.File(name, "evil"), MemoryEntry.File("good.txt", "good")]);
        Assert.Equal(1, r.SkippedUnsafe);
        Assert.Equal(1, r.FilesOk);
        Assert.Equal(1, r.LeftOut);
        Assert.Contains(r.Errors, e => e.Contains("not extracted", StringComparison.Ordinal));
        Assert.Equal(["good.txt"], s.FilesUnder());
        s.AssertNothingOutside();
    }

    [Theory]
    [InlineData("file.txt:ads", "file.txt_ads")]
    [InlineData("x.exe:Zone.Identifier", "x.exe_Zone.Identifier")]
    [InlineData("name.", "name_")]
    [InlineData("name ", "name_")]
    [InlineData("dir./file", "dir_/file")]
    [InlineData("a\u0000b", "a_b")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("esc\u001b[31m", "esc_[31m")]
    [InlineData("q?*\"<>|.txt", "q______.txt")]
    public void NamesWindowsDoesNotAllowAreMappedAndReported(string name, string saved)
    {
        using var s = new Setup();
        var r = s.Extract([MemoryEntry.File(name, "content")]);
        Assert.Equal(1, r.FilesOk);
        Assert.Equal(1, r.Renamed);
        Assert.Equal(0, r.LeftOut);
        Assert.Equal([saved], s.FilesUnder());
        Assert.Contains(r.Warnings, w => w.EndsWith($"was saved as '{saved}'", StringComparison.Ordinal));
        Assert.All(r.Warnings, w => Assert.DoesNotContain('\u001b', w));
        // No alternate data stream is ever written from an entry name.
        Assert.DoesNotContain(':', Path.GetFileName(r.Written.Single().Path));
        s.AssertNothingOutside();
    }

    [Theory]
    [InlineData("./a.txt", "a.txt")]
    [InlineData("././a.txt", "a.txt")]
    [InlineData("a//b.txt", "a/b.txt")]
    [InlineData("a/./b.txt", "a/b.txt")]
    [InlineData("dir\\file.txt", "dir/file.txt")]
    [InlineData("\u202Eevil.txt", "\u202Eevil.txt")]
    [InlineData("ünïcödé — файл.txt", "ünïcödé — файл.txt")]
    public void NamesAreNormalised(string name, string saved)
    {
        using var s = new Setup();
        var r = s.Extract([MemoryEntry.File(name, "content")]);
        Assert.Equal(1, r.FilesOk);
        Assert.Equal(0, r.Renamed);
        Assert.Equal([saved], s.FilesUnder());
        s.AssertNothingOutside();
    }

    [Fact]
    public void ADirectionOverrideIsShownEscapedInMessages()
    {
        using var s = new Setup();
        var r = s.Extract([MemoryEntry.File("\u202Etxt.exe/../../x", "x")]);
        Assert.Equal(1, r.SkippedUnsafe);
        Assert.Contains("<U+202E>", r.Errors.Single());
        Assert.DoesNotContain('\u202E', r.Errors.Single());
    }

    [Fact]
    public void AGoodNameNextToATraversalOneIsExtractedAlone()
    {
        using var s = new Setup();
        var r = s.Extract([MemoryEntry.File("good.txt", "good"), MemoryEntry.File("../../outside/evil.txt", "evil")]);
        Assert.Equal(1, r.FilesOk);
        Assert.Equal(1, r.SkippedUnsafe);
        Assert.Equal("good", File.ReadAllText(s.At("good.txt")));
        s.AssertNothingOutside();
    }

    [Fact]
    public void SymbolicLinksAreNeverCreatedAndNothingIsWrittenThroughThem()
    {
        using var s = new Setup();
        var r = s.Extract([MemoryEntry.SymLink("link", "/etc"), MemoryEntry.File("link/pwn.txt", "pwn"), MemoryEntry.SymLink("up", "../outside")]);
        Assert.Equal(2, r.SkippedLinks);
        // "link" became an ordinary folder inside the destination.
        Assert.Equal(["link/pwn.txt"], s.FilesUnder());
        Assert.Null(new DirectoryInfo(s.At("link")).LinkTarget);
        Assert.Contains(r.Errors, e => e.Contains("a symbolic link (to '/etc')", StringComparison.Ordinal));
        s.AssertNothingOutside();
    }

    [Fact]
    public void AHardLinkToAFileWrittenInThisRunIsACopy()
    {
        using var s = new Setup();
        var r = s.Extract([MemoryEntry.File("dir/a.txt", "shared"), MemoryEntry.HardLink("b.txt", "dir/a.txt"), MemoryEntry.HardLink("c.txt", "../x"), MemoryEntry.HardLink("d.txt", "missing.txt")]);
        Assert.Equal(2, r.FilesOk);
        Assert.Equal(2, r.SkippedLinks);
        Assert.Equal("shared", File.ReadAllText(s.At("b.txt")));
        Assert.Null(new FileInfo(s.At("b.txt")).LinkTarget);
        File.WriteAllText(s.At("b.txt"), "changed");
        Assert.Equal("shared", File.ReadAllText(s.At("dir", "a.txt"))); // a copy, not a link
        s.AssertNothingOutside();
    }

    [Fact]
    public void DeletionMarkersAreSkippedWithAWarningAndNotLeftOut()
    {
        using var s = new Setup();
        var r = s.Extract([new MemoryEntry("deleted.txt") { Type = EntryType.Anti }, MemoryEntry.File("kept.txt", "kept")]);
        Assert.Equal((1, 0), (r.FilesOk, r.LeftOut));
        Assert.Contains(r.Warnings, w => w.StartsWith("deleted.txt: a deletion marker", StringComparison.Ordinal));
        Assert.Equal(["kept.txt"], s.FilesUnder());
    }

    [Fact]
    public void DevicesAndOtherSpecialEntriesAreRefused()
    {
        using var s = new Setup();
        var r = s.Extract([MemoryEntry.Device("dev/null"), MemoryEntry.File("ok.txt", "ok")]);
        Assert.Equal(1, r.SkippedUnsupported);
        Assert.Equal(1, r.LeftOut);
        Assert.Equal(["ok.txt"], s.FilesUnder());
    }

    [Fact]
    public void AFolderLinkAlreadyInTheDestinationIsNeverWrittenThrough()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Creating symbolic links needs a privilege on Windows; the junction case covers Windows.");
        using var s = new Setup();
        Directory.CreateDirectory(s.Out);
        Directory.CreateSymbolicLink(s.At("linked"), s.T.Sub("outside"));
        var r = s.Extract([MemoryEntry.File("linked/evil.txt", "evil"), MemoryEntry.File("linked/sub/evil.txt", "evil"), MemoryEntry.File("fine.txt", "fine")]);
        Assert.Equal(2, r.SkippedUnsafe);
        Assert.Equal(1, r.FilesOk);
        Assert.Contains(r.Errors, e => e.Contains("through a link", StringComparison.Ordinal));
        s.AssertNothingOutside();
    }

    [Fact]
    public void AJunctionInTheDestinationIsNeverWrittenThrough()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Junctions exist only on Windows.");
        using var s = new Setup();
        Directory.CreateDirectory(s.Out);
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{s.At("junction")}\" \"{s.T.Sub("outside")}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(s.At("junction")));
        var r = s.Extract([MemoryEntry.File("junction/evil.txt", "evil")]);
        Assert.Equal(1, r.SkippedUnsafe);
        s.AssertNothingOutside();
    }

    public static TheoryData<OverwriteMode> Modes() => [OverwriteMode.Skip, OverwriteMode.Rename, OverwriteMode.Overwrite, OverwriteMode.Fail];

    [Theory]
    [MemberData(nameof(Modes))]
    public void NamesThatDifferOnlyInCaseAreRenamedInEveryMode(OverwriteMode mode)
    {
        using var s = new Setup();
        var r = s.Extract([MemoryEntry.File("a.txt", "first"), MemoryEntry.File("A.TXT", "second"), MemoryEntry.File("a.txt", "third"), MemoryEntry.File("Makefile", "m1"), MemoryEntry.File("makefile", "m2")],
            new ArchiveExtractRequest { Overwrite = mode });
        Assert.Equal(5, r.FilesOk);
        Assert.Equal(3, r.Renamed);
        Assert.Equal(0, r.SkippedExisting);
        Assert.Equal(["A (2).TXT", "Makefile", "a (3).txt", "a.txt", "makefile (2)"], s.FilesUnder());
        Assert.Equal("first", File.ReadAllText(s.At("a.txt")));
        Assert.Equal(3, r.Warnings.Count(w => w.Contains("was saved as", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void AFileAndAFolderOfTheSameNameAreBothKept(OverwriteMode mode)
    {
        using var s = new Setup();
        var r = s.Extract([MemoryEntry.File("dir", "a file"), MemoryEntry.File("dir/file", "below"), MemoryEntry.File("dir/more/x", "x"),
            MemoryEntry.Folder("other"), MemoryEntry.File("other", "a file named like the folder")], new ArchiveExtractRequest { Overwrite = mode });
        Assert.Equal(4, r.FilesOk);
        Assert.Equal(["dir", "dir (2)/file", "dir (2)/more/x", "other (2)"], s.FilesUnder());
        Assert.True(Directory.Exists(s.At("other")));
        Assert.Equal(3, r.Renamed);
    }

    [Fact]
    public void RenamedFilesNeverTakeTheNameOfAnotherEntry()
    {
        using var s = new Setup();
        var r = s.Extract([MemoryEntry.File("report.txt", "1"), MemoryEntry.File("Report.txt", "2"), MemoryEntry.File("report (2).txt", "3")]);
        Assert.Equal(["Report (3).txt", "report (2).txt", "report.txt"], s.FilesUnder());
        Assert.Equal("3", File.ReadAllText(s.At("report (2).txt")));
        Assert.Equal(1, r.Renamed);
    }

    [Theory]
    [InlineData(OverwriteMode.Skip)]
    [InlineData(OverwriteMode.Rename)]
    [InlineData(OverwriteMode.Overwrite)]
    public void APathAnEarlierPassWroteGetsAFreeNameInEveryMode(OverwriteMode mode)
    {
        using var s = new Setup();
        Directory.CreateDirectory(s.Out);
        File.WriteAllText(s.At("Report.txt"), "from the first pass");
        var replaced = new List<string>();
        var r = s.Extract([MemoryEntry.File("report.txt", "second pass")], new ArchiveExtractRequest
        {
            Overwrite = mode,
            WrittenEarlier = [s.At("Report.txt")],
            BeforeReplace = replaced.Add,
        });
        Assert.Equal(1, r.FilesOk);
        Assert.Equal(0, r.SkippedExisting);
        Assert.Equal(1, r.Renamed);
        Assert.Empty(replaced);
        Assert.Equal("from the first pass", File.ReadAllText(s.At("Report.txt")));
        Assert.Equal("second pass", File.ReadAllText(s.At("report (2).txt")));
    }

    [Fact]
    public void ExistingFilesFollowTheOverwriteMode()
    {
        foreach (var mode in new[] { OverwriteMode.Skip, OverwriteMode.Rename, OverwriteMode.Overwrite, OverwriteMode.Fail })
        {
            using var s = new Setup();
            Directory.CreateDirectory(s.Out);
            File.WriteAllText(s.At("a.txt"), "old");
            var replaced = new List<string>();
            var r = s.Extract([MemoryEntry.File("a.txt", "new")], new ArchiveExtractRequest { Overwrite = mode, BeforeReplace = p => { replaced.Add(p); Assert.Equal("old", File.ReadAllText(p)); } });
            switch (mode)
            {
                case OverwriteMode.Skip:
                    Assert.Equal((0, 1), (r.FilesOk, r.SkippedExisting));
                    Assert.Equal("old", File.ReadAllText(s.At("a.txt")));
                    break;
                case OverwriteMode.Rename:
                    Assert.Equal(1, r.Renamed);
                    Assert.Equal("new", File.ReadAllText(s.At("a (2).txt")));
                    break;
                case OverwriteMode.Overwrite:
                    Assert.Equal([Path.GetFullPath(s.At("a.txt"))], replaced);
                    Assert.Equal("new", File.ReadAllText(s.At("a.txt")));
                    Assert.Equal(0, r.Renamed);
                    break;
                case OverwriteMode.Fail:
                    Assert.Equal(1, r.FilesFailed);
                    Assert.Equal("old", File.ReadAllText(s.At("a.txt")));
                    break;
            }
            Assert.Empty(Directory.GetFiles(s.Out, "*.osmium-part-*"));
        }
    }

    [Fact]
    public void AFailingBeforeReplaceKeepsTheExistingFile()
    {
        using var s = new Setup();
        Directory.CreateDirectory(s.Out);
        File.WriteAllText(s.At("a.txt"), "old");
        var r = s.Extract([MemoryEntry.File("a.txt", "new"), MemoryEntry.File("b.txt", "b")],
            new ArchiveExtractRequest { Overwrite = OverwriteMode.Overwrite, BeforeReplace = _ => throw new IOException("the Recycle Bin is full") });
        Assert.Equal(1, r.FilesFailed);
        Assert.Equal(1, r.FilesOk);
        Assert.Equal("old", File.ReadAllText(s.At("a.txt")));
        Assert.Contains(r.Errors, e => e.Contains("the Recycle Bin is full", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(s.Out, "*.osmium-part-*"));
    }

    [Fact]
    public void ADamagedFileFailsAloneAndLeavesNoPartFile()
    {
        foreach (bool keep in new[] { false, true })
        {
            using var s = new Setup();
            var r = s.Extract([MemoryEntry.File("a.txt", "a"), new MemoryEntry("bad.txt", Encoding.UTF8.GetBytes("bad")) { Damaged = true }, MemoryEntry.File("c.txt", "c")],
                new ArchiveExtractRequest { KeepBroken = keep });
            Assert.Equal(2, r.FilesOk);
            Assert.Equal(1, r.FilesFailed);
            Assert.Contains(r.Errors, e => e.StartsWith("bad.txt: damaged data (CRC mismatch)", StringComparison.Ordinal));
            Assert.False(File.Exists(s.At("bad.txt")));
            Assert.Equal(keep ? 1 : 0, Directory.GetFiles(s.Out, "bad.txt.osmium-part-*").Length);
        }
    }

    [Fact]
    public void TimesAndAttributesGoOnlyOnWhatThisRunCreated()
    {
        using var s = new Setup();
        var old = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var entryTime = new DateTime(2015, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        Directory.CreateDirectory(s.At("existing"));
        Directory.SetLastWriteTimeUtc(s.At("existing"), old);
        var r = s.Extract([MemoryEntry.Folder("existing") with { Modified = entryTime }, MemoryEntry.File("existing/a.txt", "a") with { Modified = entryTime },
            MemoryEntry.Folder("new") with { Modified = entryTime }, MemoryEntry.File("new/b.txt", "b") with { Modified = entryTime, ReadOnly = true }]);
        Assert.Equal(2, r.FilesOk);
        Assert.Equal(entryTime, File.GetLastWriteTimeUtc(s.At("existing", "a.txt")));
        Assert.Equal(entryTime, Directory.GetLastWriteTimeUtc(s.At("new")));
        Assert.NotEqual(entryTime, Directory.GetLastWriteTimeUtc(s.At("existing")));
        if (OperatingSystem.IsWindows()) Assert.True(File.GetAttributes(s.At("new", "b.txt")).HasFlag(FileAttributes.ReadOnly));
        if (OperatingSystem.IsWindows()) File.SetAttributes(s.At("new", "b.txt"), FileAttributes.Normal);
    }

    [Fact]
    public void TheMarkOfTheWebGoesOnThePartFileBeforeItTakesItsPlace()
    {
        using var s = new Setup();
        var calls = new List<(string Path, bool TargetExists)>();
        GuardedExtractor.ZoneOverride = "[ZoneTransfer]\r\nZoneId=3\r\n";
        GuardedExtractor.ZoneWriterOverride = (path, zone) => { calls.Add((path, File.Exists(s.At("tool.exe")))); return null; };
        try
        {
            var r = s.Extract([MemoryEntry.File("tool.exe", "MZ") with { ReadOnly = true }]);
            Assert.Equal(1, r.FilesOk);
            var call = Assert.Single(calls);
            Assert.Contains(".osmium-part-", call.Path);
            Assert.False(call.TargetExists);
        }
        finally
        {
            GuardedExtractor.ZoneOverride = null;
            GuardedExtractor.ZoneWriterOverride = null;
        }
        if (OperatingSystem.IsWindows()) File.SetAttributes(s.At("tool.exe"), FileAttributes.Normal);
    }

    [Fact]
    public void AMarkThatCannotBeWrittenIsAWarning()
    {
        using var s = new Setup();
        GuardedExtractor.ZoneOverride = "[ZoneTransfer]\r\nZoneId=3\r\n";
        GuardedExtractor.ZoneWriterOverride = (_, _) => "the volume does not support it.";
        try
        {
            var r = s.Extract([MemoryEntry.File("a\u001b.txt", "x")]);
            Assert.Equal(1, r.FilesOk);
            Assert.Contains(r.Warnings, w => w.Contains("Mark-of-the-Web", StringComparison.Ordinal) && w.Contains("\\u001b", StringComparison.Ordinal));
        }
        finally
        {
            GuardedExtractor.ZoneOverride = null;
            GuardedExtractor.ZoneWriterOverride = null;
        }
    }

    [Fact]
    public void CancelRemovesOnlyThisRunsPartFilesAndEmptyNewFolders()
    {
        using var s = new Setup();
        Directory.CreateDirectory(s.At("kept"));
        File.WriteAllText(s.At("kept", "mine.txt"), "mine");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var big = new byte[8 << 20];
        var entries = new[]
        {
            MemoryEntry.File("done/first.txt", "finished"),
            new MemoryEntry("new/deeper/big.bin", big) { OnRead = n => { if (n > (2 << 20)) cts.Cancel(); } },
        };
        using var src = new MemorySource(s.Archive, entries);
        Assert.ThrowsAny<OperationCanceledException>(() => src.Extract(s.Out, new ArchiveExtractRequest(), null, cts.Token));
        Assert.Equal(["done/first.txt", "kept/mine.txt"], s.FilesUnder());
        Assert.False(Directory.Exists(s.At("new")));
        Assert.Empty(Directory.GetFiles(s.Out, "*.osmium-part-*", SearchOption.AllDirectories));
        s.AssertNothingOutside();
    }

    [Fact]
    public void WrittenListsEveryFileByIndexInArchiveOrder()
    {
        using var s = new Setup();
        var r = s.Extract([MemoryEntry.Folder("d"), MemoryEntry.File("d/a.txt", "a"), MemoryEntry.File("bc?.txt", "b"), MemoryEntry.File("d/A.txt", "c")]);
        Assert.Equal([1, 2, 3], r.Written.Select(w => w.Index));
        Assert.Equal(Path.GetFullPath(s.At("d", "a.txt")), r.Written[0].Path);
        Assert.Equal(Path.GetFullPath(s.At("bc_.txt")), r.Written[1].Path);
        Assert.Equal(Path.GetFullPath(s.At("d", "A (2).txt")), r.Written[2].Path);
    }

    [Fact]
    public void StripPrefixPutsAFoldersContentAtTheRoot()
    {
        using var s = new Setup();
        var r = s.Extract([MemoryEntry.Folder("docs"), MemoryEntry.File("docs/img/a.png", "a"), MemoryEntry.File("docs/readme.txt", "r"), MemoryEntry.File("other.txt", "o"), MemoryEntry.File("docs2/x", "x")],
            new ArchiveExtractRequest { StripPrefix = "docs/" });
        Assert.Equal(2, r.FilesOk);
        Assert.Equal(0, r.LeftOut);
        Assert.Equal(["img/a.png", "readme.txt"], s.FilesUnder());
    }

    [Fact]
    public void IndicesSelectExactlyOneEntryEvenWhenAnotherMapsToTheSameName()
    {
        using var s = new Setup();
        // "a.pdf " is saved as "a.pdf_", and so would be a folder "a.pdf /"; only the selected entry is extracted.
        var r = s.Extract([MemoryEntry.Folder("a.pdf "), MemoryEntry.File("a.pdf /inner.txt", "inner"), MemoryEntry.File("a.pdf ", "the file")],
            new ArchiveExtractRequest { Indices = new HashSet<int> { 2 } });
        Assert.Equal(1, r.FilesOk);
        Assert.Equal(2, r.Written.Single().Index);
        Assert.Equal("the file", File.ReadAllText(r.Written.Single().Path));
        Assert.Equal(["a.pdf_"], s.FilesUnder());
    }

    [Fact]
    public void TheExpansionLimitStopsTheRunAndRemovesThePartFile()
    {
        using var s = new Setup();
        var limits = ImportLimits.Default with { ExpansionFloorBytes = 1 << 20, MaxExpansionRatio = 10 };
        // Declared sizes pass the rule: refused before anything is written.
        using (var src = new MemorySource(s.Archive, [new MemoryEntry("zeros.bin", new byte[3 << 20])], new ArchiveOpenOptions { Limits = limits }))
        {
            var e = Assert.Throws<ExpansionLimitException>(() => src.Extract(s.Out, new ArchiveExtractRequest(), null, Ct));
            Assert.Equal(10, e.Factor);
            Assert.False(Directory.Exists(s.Out));
            // With the user's go-ahead it extracts.
            var r = src.Extract(s.Out, new ArchiveExtractRequest { Limits = limits with { AllowLargeExpansion = true } }, null, Ct);
            Assert.Equal(1, r.FilesOk);
        }
        s.AssertNothingOutside();
    }

    [Fact]
    public void FreeSpaceIsCheckedBeforeAndWhileWriting()
    {
        using var s = new Setup();
        using (FreeSpace.Simulate(_ => (10L << 20, 100L << 30)))
        {
            var e = Assert.Throws<FreeSpaceException>(() => s.Extract([new MemoryEntry("a.bin", new byte[1000])]));
            Assert.Contains("must stay free", e.Message);
            Assert.False(Directory.Exists(s.Out));
        }
        long free = 5L << 30;
        GuardedExtractor.FreeSpaceIntervalOverride = 1 << 20;
        try
        {
            using (FreeSpace.Simulate(_ => (free, 10L << 30)))
            {
                var entries = new[] { MemoryEntry.File("first.txt", "ok"), new MemoryEntry("big.bin", new byte[4 << 20]) { OnRead = n => { if (n > (1 << 20)) free = 100 << 20; } } };
                var e = Assert.Throws<FreeSpaceException>(() => s.Extract(entries, new ArchiveExtractRequest { Limits = ImportLimits.Default with { FreeSpaceReserveBytes = 1L << 30 } }));
                Assert.Contains("Extraction stopped", e.Message);
            }
        }
        finally { GuardedExtractor.FreeSpaceIntervalOverride = null; }
        Assert.Equal(["first.txt"], s.FilesUnder());
        Assert.Empty(Directory.GetFiles(s.Out, "*.osmium-part-*"));
    }
}
