using Osmium.Core.Container;

namespace Osmium.Core.Tests;

/// <summary><c>osmium salvage</c> on archives of container 1.1 and on archives written as Osmium 0.1.x wrote them.</summary>
public class SalvageWithSpareTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertRecovered(Dictionary<string, byte[]> files, string destination, SalvageResult res)
    {
        Assert.True(res.IndexRecovered, string.Join(" | ", res.Summary()));
        Assert.Equal(files.Count, res.FilesRecovered);
        Assert.Empty(res.Lost);
        foreach (var (name, data) in files)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine([destination, .. name.Split('/')])));
    }

    private static OsaWriterOptions WithSpares(string? password = null) => new() { Password = password, SpareCopies = SpareCopyPolicy.Always };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnIntactArchiveWithSpareCopiesSalvagesAsComplete(bool encrypted)
    {
        using var t = new TempDir();
        string? pw = encrypted ? SpareArchives.Password : null;
        var files = SpareArchives.Files(4, 3000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, WithSpares(pw));
        var tail = SpareArchives.TailOf(path, pw);

        var res = Salvage.Run(path, t.Sub("out"), () => pw, null, Ct);
        Assert.Equal(0, res.ExitCode);
        Assert.True(res.Complete);
        Assert.Equal(0, res.UnrecognizedBytes); // the header copy is recognised
        Assert.Equal(0, res.FramesSetAside);
        Assert.Equal(tail.IndexOffset, res.IndexFrame!.Offset);
        var spare = Assert.Single(res.Frames, f => f.IsSpareCopy);
        Assert.Equal((tail.SpareOffset, SalvageFrameState.Duplicate), (spare.Offset, spare.State));
        Assert.Equal("Everything was recovered.", res.Summary().Last());
        Assert.DoesNotContain(res.Summary(), s => s.Contains("duplicate", StringComparison.Ordinal));
        AssertRecovered(files, t.Sub("out"), res);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithTheFileListDestroyedSalvageRestoresEveryFileFromItsSpareCopy(bool encrypted)
    {
        using var t = new TempDir();
        string? pw = encrypted ? SpareArchives.Password : null;
        var files = SpareArchives.Files(4, 3000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, WithSpares(pw));
        var tail = SpareArchives.TailOf(path, pw);
        var rnd = new Random(3);
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b => rnd.NextBytes(b.AsSpan((int)tail.IndexOffset, (int)tail.IndexLength)));

        var res = Salvage.Run(bad, t.Sub("out"), () => pw, null, Ct);
        Assert.Equal(0, res.ExitCode);
        Assert.Equal(tail.SpareOffset, res.IndexFrame!.Offset);
        Assert.Equal(tail.IndexLength, res.UnrecognizedBytes); // the destroyed frame, and nothing else
        Assert.StartsWith("Everything was recovered: the archive is damaged", res.Summary().Last());
        AssertRecovered(files, t.Sub("out"), res);
    }

    [Theory]
    [InlineData("zeroed")]
    [InlineData("crc")]
    [InlineData("marker")]
    public void WithTheHeaderDamagedAnEncrypted11ArchiveSalvagesAtLeastAsWellAsA10One(string how)
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(4, 3000);
        SalvageResult Salvaged(string name, OsaWriterOptions options)
        {
            string path = SpareArchives.Write(t.Sub(name + ".osa"), files, options with { Password = SpareArchives.Password });
            long header = SpareArchives.TailOf(path, SpareArchives.Password).HeaderLength;
            string bad = SpareArchives.Damage(path, t.Sub(name + "-bad.osa"), b =>
            {
                if (how == "zeroed") Array.Clear(b, 0, (int)header);
                else b[how == "crc" ? header - 1 : 1] ^= 0x10;
            });
            return Salvage.Run(bad, t.Sub(name + "-out"), () => SpareArchives.Password, null, Ct);
        }
        var older = Salvaged("v10", new OsaWriterOptions { HeaderMinor = 0, SpareThreshold = long.MaxValue });
        var newer = Salvaged("v11", WithSpares());
        Assert.True(newer.FilesRecovered >= older.FilesRecovered, $"1.1 recovered {newer.FilesRecovered}, 1.0 {older.FilesRecovered}");
        Assert.True(newer.BlocksVerified >= older.BlocksVerified);
        Assert.True(newer.ExitCode <= older.ExitCode);
        // With the header zeroed, a 1.0 archive keeps no other copy of its salt and archive id; a 1.1 archive does.
        AssertRecovered(files, t.Sub("v11-out"), newer);
        if (how == "zeroed") Assert.Equal((0, 0), (older.FilesRecovered, newer.ExitCode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEncryptedArchiveWithItsHeaderDestroyedIsSalvagedWithTheKeyOfItsHeaderCopy(bool footerDamaged)
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(4, 3000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, WithSpares(SpareArchives.Password));
        var tail = SpareArchives.TailOf(path, SpareArchives.Password);
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b =>
        {
            Array.Clear(b, 0, (int)tail.HeaderLength);
            if (footerDamaged) b[^5] ^= 0x01; // the footer's CRC
        });
        int asked = 0;

        var res = Salvage.Run(bad, t.Sub("out"), () => { asked++; return SpareArchives.Password; }, null, Ct);
        Assert.Equal(1, asked);
        Assert.True(res.Encrypted);
        Assert.False(res.HeaderIntact);
        Assert.Null(res.KeyProblem);
        Assert.Contains(res.Notes, n => n.Contains("its spare copy beside the file list is intact and was used", StringComparison.Ordinal));
        AssertRecovered(files, t.Sub("out"), res);

        // Vouched for by the footer, the copy says that a password is wrong; without the footer, it may be another archive's.
        if (!footerDamaged)
        {
            Assert.Throws<WrongPasswordException>(() => Salvage.Run(bad, t.Sub("wrong"), () => "not the password", null, Ct));
            return;
        }
        var wrong = Salvage.Run(bad, t.Sub("wrong"), () => "not the password", null, Ct);
        Assert.Contains("the spare copy of it that was found does not accept the password", wrong.KeyProblem);
        Assert.Equal(0, wrong.FilesRecovered);
    }

    [Theory]
    [InlineData("the spare file list")]
    [InlineData("the padding of the header copy")]
    [InlineData("the archive id in the header copy")]
    [InlineData("the header copy, beyond the id's first bytes")]
    public void ADamagedSpareCopyIsDamageThoughNoDataIsLost(string what)
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(4, 3000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, WithSpares());
        var tail = SpareArchives.TailOf(path);
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b =>
        {
            long h = tail.HeaderCopyOffset;
            switch (what)
            {
                case "the spare file list": b[tail.SpareOffset + tail.IndexLength / 2] ^= 0x01; break;
                case "the padding of the header copy": b[h + tail.HeaderLength + 100] ^= 0x01; break;
                default: RewriteHeaderCopy(b, h, tail.HeaderLength, what == "the archive id in the header copy" ? 17 : 27); break;
            }
        });

        var res = Salvage.Run(bad, t.Sub("out"), null, null, Ct);
        AssertRecovered(files, t.Sub("out"), res);
        Assert.Equal("Everything was recovered: the archive is damaged, but no data was lost.", res.Summary().Last());
    }

    /// <summary>Changes one byte of the header copy at <paramref name="h"/> and recomputes its CRC, so that it still
    /// checks out as a header.</summary>
    private static void RewriteHeaderCopy(byte[] b, long h, long headerLength, int at)
    {
        b[h + at] ^= 0x01;
        int crcAt = (int)(h + headerLength - 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(crcAt), Osa.Crc(b.AsSpan((int)h, (int)headerLength - 4)));
    }

    [Fact]
    public void WithTheHeaderDamagedAHeaderCopyOfAnotherArchiveIsNotRecognised()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(4, 3000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files, WithSpares());
        var tail = SpareArchives.TailOf(path);
        long copy = tail.Trailer!.Value.CopyLength;
        SalvageResult Salvaged(string name, bool otherId) => Salvage.Run(SpareArchives.Damage(path, t.Sub(name + ".osa"), b =>
        {
            b[2] ^= 0x01; // the header's marker
            if (otherId) RewriteHeaderCopy(b, tail.HeaderCopyOffset, tail.HeaderLength, 17);
        }), t.Sub(name), null, null, Ct);

        // The footer names the archive: a header copy with that id is recognised, one with another id is not.
        Assert.Equal(tail.HeaderLength, Salvaged("own", otherId: false).UnrecognizedBytes);
        var other = Salvaged("other", otherId: true);
        Assert.Equal(tail.HeaderLength + copy, other.UnrecognizedBytes);
        AssertRecovered(files, t.Sub("other"), other);
    }

    [Fact]
    public void TheSpareFileListOfAnotherArchiveIsNeverTakenForThisArchives()
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(4, 3000); // one stored block per file, in name order
        string path = SpareArchives.Write(t.Sub("a.osa"), files, new OsaWriterOptions { SpareCopies = SpareCopyPolicy.Never });
        var other = new Dictionary<string, byte[]> { ["away/elsewhere.bin"] = SpareArchives.Files(1, 5000, seed: 9).Values.Single() };
        var otherBytes = File.ReadAllBytes(SpareArchives.Write(t.Sub("other.osa"), other, WithSpares()));
        long cut;
        using (var r = OsaReader.Open(path)) cut = r.Blocks[2].Offset + r.Blocks[2].FrameLength / 2;
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), _ => { });
        File.WriteAllBytes(bad, [.. File.ReadAllBytes(path).AsSpan(0, (int)cut), .. otherBytes]); // cut short, another archive after it

        var res = Salvage.Run(bad, t.Sub("out"), null, null, Ct);
        Assert.False(res.IndexRecovered, string.Join(" | ", res.Summary()));
        Assert.False(Directory.Exists(t.Sub(Path.Combine("out", "away"))));
        var ours = files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Value).ToList();
        for (int k = 0; k < 2; k++) Assert.Equal(ours[k], File.ReadAllBytes(t.Sub(Path.Combine("out", $"block-{k}.bin"))));
        Assert.All(res.Frames.Where(f => f.IsIndex && f.Intact), f => Assert.Equal(SalvageFrameState.Ignored, f.State));
    }

    [Fact]
    public void AnArchiveStoredInABlockKeepsItsBlocksWhenItHasSpareCopies()
    {
        using var t = new TempDir();
        var inner = File.ReadAllBytes(SpareArchives.Write(t.Sub("inner.osa"), new Dictionary<string, byte[]> { ["inner/x.bin"] = new byte[3000] }, WithSpares()));
        var rnd = new Random(4);
        var filler = new byte[70_000];
        rnd.NextBytes(filler);
        var notes = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 300).Select(i => $"note {i}\n")));
        // Block 0 holds the filler and then the stored archive, block 1 the notes.
        string path = ExtractSafetyTests.Write(t.Sub("a.osa"), [("a-filler.bin", filler), ("backups/inner.osa", inner), ("notes.txt", notes)],
            blockSize: filler.Length + inner.Length);
        long indexOffset;
        using (var r = OsaReader.Open(path))
        {
            Assert.Equal(2, r.Blocks.Count);
            indexOffset = r.Blocks[^1].Offset + r.Blocks[^1].FrameLength;
        }
        var bytes = File.ReadAllBytes(path);
        Array.Clear(bytes, 0, 512); // the header and block 0's frame header
        File.WriteAllBytes(t.Sub("bad.osa"), bytes[..(int)indexOffset]); // and the file list and footer gone

        var res = Salvage.Run(t.Sub("bad.osa"), t.Sub("out"), null, null, Ct);
        Assert.False(res.IndexRecovered);
        Assert.Equal(notes, File.ReadAllBytes(t.Sub(Path.Combine("out", "block-1.bin"))));
        Assert.False(File.Exists(t.Sub(Path.Combine("out", "block-0.bin"))), "the stored archive's block was taken for this archive's block 0");
        var innerBlock = Assert.Single(res.Frames, f => !f.IsIndex && f.ForeignIndexOffset is not null);
        Assert.Equal($"block-0-at-{innerBlock.Offset}.bin", innerBlock.Output);
    }

    [Fact]
    public async Task ASalvagedReadOnlyProgramKeepsTheMarkOnARealStream()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "needs NTFS alternate data streams");
        using var t = new TempDir();
        try
        {
            string archive = await ExtractSafetyTests.ZonedArchiveWithAReadOnlyProgram(t, Ct);
            var res = Salvage.Run(archive, t.Sub("salvaged"), null, null, Ct);
            Assert.Equal(0, res.ExitCode);
            Assert.Empty(res.Warnings);
            string exe = Path.Combine(t.Sub("salvaged"), "in", "setup.exe");
            Assert.True(File.GetAttributes(exe).HasFlag(FileAttributes.ReadOnly));
            Assert.Contains("ZoneId=3", File.ReadAllText(exe + ":Zone.Identifier"));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(t.Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AnEncryptedArchiveWithADamagedMarkerIsRebuiltWhateverItsMinorVersion(ushort minor)
    {
        using var t = new TempDir();
        var files = SpareArchives.Files(3, 4000);
        string path = SpareArchives.Write(t.Sub("a.osa"), files,
            new OsaWriterOptions { Password = SpareArchives.Password, HeaderMinor = minor, SpareThreshold = long.MaxValue });
        string bad = SpareArchives.Damage(path, t.Sub("bad.osa"), b => b[1] ^= 0x20);

        var res = Salvage.Run(bad, t.Sub("out"), () => SpareArchives.Password, null, Ct);
        Assert.Equal(0, res.ExitCode);
        Assert.False(res.HeaderIntact);
        AssertRecovered(files, t.Sub("out"), res);
    }
}
