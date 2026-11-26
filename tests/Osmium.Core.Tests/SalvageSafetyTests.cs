using Osmium.Core.Container;
using static Osmium.Core.Tests.ExtractSafetyTests;

namespace Osmium.Core.Tests;

/// <summary>
/// <c>osmium salvage</c> writes the files it recovers by the rules of extraction: never through a link that is already
/// in the destination, never replacing another entry of the same archive, and with folder times and attributes only on
/// folders it created.
/// </summary>
public class SalvageSafetyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void NothingIsSalvagedThroughASymbolicLinkAlreadyInTheDestination()
    {
        using var t = new TempDir();
        string archive = Write(t.Sub("a.osa"), [("docs", null), ("docs/a.txt", Data("a")), ("docs/sub/b.txt", Data("b")), ("ok.txt", Data("ok"))]);
        string dest = t.Sub("out"), outside = t.Sub("outside");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(outside);
        if (!TryLink(Path.Combine(dest, "docs"), outside)) Assert.Skip("Symbolic links cannot be created here (Windows needs Developer Mode or administrator rights).");

        var res = Salvage.Run(archive, dest, null, null, Ct);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.Equal(["ok.txt"], res.Written);
        Assert.Equal(3, res.Lost.Count); // the folder and both files below it
        Assert.All(res.Lost, l => Assert.Equal(TargetNames.LinkRefused, l.Reason));
        Assert.Equal(2, res.ExitCode);
    }

    [Fact]
    public void AFolderThatWasAlreadyThereKeepsItsTimeAndAttributes()
    {
        using var t = new TempDir();
        string archive = Write(t.Sub("a.osa"), [("old", null), ("old/a.txt", Data("a")), ("new", null), ("new/b.txt", Data("b"))],
            folderAttributes: FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
        string dest = t.Sub("out");
        Directory.CreateDirectory(Path.Combine(dest, "old"));
        var attributesBefore = File.GetAttributes(Path.Combine(dest, "old"));

        Assert.Equal(0, Salvage.Run(archive, dest, null, null, Ct).ExitCode);
        Assert.NotEqual(When, Directory.GetLastWriteTimeUtc(Path.Combine(dest, "old"))); // the archive's time is never set on it
        Assert.Equal(attributesBefore, File.GetAttributes(Path.Combine(dest, "old")));
        Assert.Equal(When, Directory.GetLastWriteTimeUtc(Path.Combine(dest, "new")));
        if (OperatingSystem.IsWindows())
        {
            var made = File.GetAttributes(Path.Combine(dest, "new"));
            Assert.True(made.HasFlag(FileAttributes.Hidden));
            Assert.False(made.HasFlag(FileAttributes.System) || made.HasFlag(FileAttributes.ReadOnly), "System and ReadOnly are never set on a folder");
        }
    }

    [Fact]
    public void NamesThatDifferOnlyInCaseNeverReplaceEachOther()
    {
        using var t = new TempDir();
        string archive = Write(t.Sub("a.osa"), [("Notes.txt", Data("first")), ("notes.txt", Data("second"))]);
        var res = Salvage.Run(archive, t.Sub("out"), null,
            new SalvageOptions { Overwrite = OverwriteMode.Overwrite, NameComparer = StringComparer.OrdinalIgnoreCase }, Ct);
        Assert.Equal(2, res.FilesRecovered);
        Assert.Equal("first", File.ReadAllText(Path.Combine(t.Sub("out"), "Notes.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(t.Sub("out"), "notes (2).txt")));
    }
}
