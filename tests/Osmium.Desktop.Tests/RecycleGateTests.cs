using Osmium.Desktop.Jobs;

namespace Osmium.Desktop.Tests;

/// <summary>
/// The decisions the system's recycler asks for while one Recycle Bin operation runs: before each delete, whether it may
/// go ahead, and after it, what happened. The system is played by the test, which names what it is about to delete by
/// its path, as the shell's progress sinks do.
/// </summary>
public sealed class RecycleGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "osm-recycle-gate-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _folder, _child, _file;
    private readonly List<string> _rechecked = [];

    public RecycleGateTests()
    {
        Directory.CreateDirectory(_root);
        _folder = Path.Combine(_root, "photos");
        _child = Path.Combine(_folder, "sub", "a.txt");
        _file = Path.Combine(_root, "notes.txt");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>A gate whose last check records each item it checks and answers with <paramref name="reason"/>.</summary>
    private RecycleGate Gate(string? reason = null, CancellationToken? ct = null) => new(path =>
    {
        _rechecked.Add(path);
        return reason;
    }, ct ?? TestContext.Current.CancellationToken);

    [Fact]
    public void AnItemHandedOverMovesOnceTheSystemRecyclesIt()
    {
        var gate = Gate();
        var item = Assert.IsType<RecycleGate.Item>(gate.Expect(_folder));
        Assert.Null(gate.PreDelete(recycles: true, item, _folder));
        Assert.Equal([_folder], _rechecked);
        string bin = Path.Combine(_root, "bin", "$R0001");
        Assert.True(gate.PostDelete(item, _folder, 0, recycled: true, bin));
        Assert.Equal(new RecycleOutcome(_folder, true, null, bin), gate.Outcome(item, 0, aborted: false));
        Assert.Empty(gate.Others());
        Assert.Equal(1, gate.PreDeletes);
    }

    [Fact]
    public void ADeleteTheSystemWouldNotRecycleIsRefused()
    {
        var gate = Gate();
        var item = gate.Expect(_folder)!;
        var other = gate.Expect(_file)!;
        Assert.Equal(RecycleGate.CannotTake, gate.PreDelete(recycles: false, item, _folder));
        Assert.Empty(_rechecked);
        Assert.Equal(RecycleGate.CannotTake, gate.Outcome(item, 0, aborted: true).Reason);
        // The operation stops: nothing after it goes either.
        Assert.Equal($"an earlier item failed ({_folder}: {RecycleGate.CannotTake})", gate.Outcome(other, 0, aborted: true).Reason);
        Assert.NotNull(gate.PreDelete(recycles: true, other, _file));
    }

    [Fact]
    public void OnlyWhatWasHandedOverMayGo()
    {
        string elsewhere = Path.Combine(_root, "other.txt");
        var gate = Gate();
        gate.Expect(_folder);
        Assert.Equal($"the shell was about to move something else with it: {elsewhere}", gate.PreDelete(recycles: true, null, elsewhere));

        gate = Gate();
        var item = gate.Expect(_folder)!;
        var other = gate.Expect(_file)!;
        Assert.Equal($"the shell was about to move something else with it: {_file}", gate.PreDelete(recycles: true, item, _file)); // another item's
        Assert.Empty(_rechecked);
        Assert.False(gate.Outcome(other, 0, aborted: true).Recycled);

        gate = Gate();
        item = gate.Expect(_folder)!;
        Assert.Equal("the shell named no path for an item it was about to move", gate.PreDelete(recycles: true, item, null));
    }

    [Fact]
    public void ItemsGivenTwiceOrInsideOneAnotherAreNotHandedOver()
    {
        var gate = Gate();
        Assert.NotNull(gate.Expect(_folder));
        Assert.Null(gate.Expect(_folder + Path.DirectorySeparatorChar));
        Assert.Null(gate.Expect(_child));
        Assert.Null(gate.Expect(_root));
        Assert.NotNull(gate.Expect(_file));
    }

    [Fact]
    public void TheLastCheckCanKeepAnItem()
    {
        var gate = Gate("it changed after it was read");
        var item = gate.Expect(_folder)!;
        Assert.Equal("checked again just before it moved: it changed after it was read", gate.PreDelete(recycles: true, item, _folder));
        Assert.Equal("checked again just before it moved: it changed after it was read", gate.Outcome(item, 0, aborted: true).Reason);

        var throws = new RecycleGate(_ => throw new IOException("the disk did not answer"), TestContext.Current.CancellationToken);
        item = throws.Expect(_folder)!;
        Assert.Equal("it could not be checked again (the disk did not answer)", throws.PreDelete(recycles: true, item, _folder));
    }

    [Fact]
    public void NothingIsDeletedOnceTheJobIsCanceled()
    {
        using var cts = new CancellationTokenSource();
        var gate = Gate(null, cts.Token);
        var item = gate.Expect(_folder)!;
        cts.Cancel();
        Assert.Equal("the job was canceled", gate.PreDelete(recycles: true, item, _folder));
        Assert.Empty(_rechecked);
    }

    [Fact]
    public void ACancelDuringTheLastCheckKeepsTheItem()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        // The check finds nothing wrong, but the job is canceled while it reads the files.
        var gate = new RecycleGate(_ =>
        {
            cts.Cancel();
            return null;
        }, cts.Token);
        var item = gate.Expect(_folder)!;
        Assert.Equal("the job was canceled", gate.PreDelete(recycles: true, item, _folder));
        Assert.Equal("the job was canceled", gate.Outcome(item, 0, aborted: true).Reason);
    }

    [Fact]
    public void TheFirstDeleteThatTouchesAnItemChecksItFirst()
    {
        // The shell may name something inside a folder before the folder itself.
        var gate = Gate();
        var item = gate.Expect(_folder)!;
        Assert.Null(gate.PreDelete(recycles: true, item, _child));
        Assert.Equal([_folder], _rechecked); // before any of it went
        Assert.Null(gate.PreDelete(recycles: true, null, _child)); // the sink of the whole operation asks too
        Assert.Null(gate.PreDelete(recycles: true, item, _folder));
        Assert.Equal([_folder], _rechecked); // once
    }

    [Fact]
    public void AnItemThatChangedKeepsEvenThePartTheSystemNamesFirst()
    {
        const string why = "checked again just before it moved: it holds a file that is not in the archive: late.txt";
        var gate = Gate("it holds a file that is not in the archive: late.txt");
        var item = gate.Expect(_folder)!;
        Assert.Equal(why, gate.PreDelete(recycles: true, item, _child));
        Assert.Equal([_folder], _rechecked);
        Assert.NotNull(gate.PreDelete(recycles: true, item, _folder));
        Assert.Equal(why, gate.Outcome(item, 0, aborted: true).Reason);
    }

    [Fact]
    public void APartTheSystemWouldNotRecycleIsRefusedBeforeAnyCheck()
    {
        var gate = Gate();
        var item = gate.Expect(_folder)!;
        Assert.Equal(RecycleGate.CannotTake, gate.PreDelete(recycles: false, item, _child));
        Assert.Empty(_rechecked);
        Assert.Equal(RecycleGate.CannotTake, gate.Outcome(item, 0, aborted: true).Reason);
    }

    [Fact]
    public void AnItemDeletedRatherThanRecycledStopsTheOperation()
    {
        var gate = Gate();
        var item = gate.Expect(_folder)!;
        var other = gate.Expect(_file)!;
        Assert.Null(gate.PreDelete(recycles: true, item, _folder));
        Assert.False(gate.PostDelete(item, _folder, 0, recycled: false, null));
        Assert.Equal("the shell deleted it, or part of it, rather than moved it to the Recycle Bin", gate.Outcome(item, 0, aborted: true).Reason);
        Assert.NotNull(gate.PreDelete(recycles: true, other, _file));
    }

    [Fact]
    public void SomethingMovedThatWasNotHandedOverStopsTheOperation()
    {
        var gate = Gate();
        var item = gate.Expect(_folder)!;
        string elsewhere = Path.Combine(_root, "other.txt"), bin = Path.Combine(_root, "bin", "$R0002");
        Assert.False(gate.PostDelete(null, elsewhere, 0, recycled: true, bin));
        Assert.Equal(new RecycleOutcome(elsewhere, true, "the operation moved it to the Recycle Bin, although it was not handed over", bin), Assert.Single(gate.Others()));
        // Nothing after it is approved.
        string why = $"{elsewhere}: the operation moved it although it was not handed over";
        Assert.Equal(why, gate.PreDelete(recycles: true, item, _folder));
        Assert.Empty(_rechecked);
        Assert.Equal($"an earlier item failed ({why})", gate.Outcome(item, 0, aborted: true).Reason);
    }

    [Fact]
    public void AnItemAfterOneThatFailedSaysSo()
    {
        const int SharingViolation = unchecked((int)0x8007_0020);
        string third = Path.Combine(_root, "third.txt");
        var gate = Gate();
        var first = gate.Expect(_folder)!;
        var second = gate.Expect(_file)!;
        var unreached = gate.Expect(third)!;
        Assert.Null(gate.PreDelete(recycles: true, first, _folder));
        Assert.True(gate.PostDelete(first, _folder, SharingViolation, recycled: false, null)); // the shell could not move it
        // The shell goes on to the next item, which is refused: the operation has stopped.
        Assert.NotNull(gate.PreDelete(recycles: true, second, _file));
        string why = $"an earlier item failed ({_folder}: the shell could not move it)";
        Assert.Equal(why, gate.Outcome(second, 0, aborted: true).Reason);
        Assert.Equal(why, gate.Outcome(unreached, 0, aborted: true).Reason);
        // The item that failed keeps its own reason, also when the shell asks about part of it again.
        Assert.NotNull(gate.PreDelete(recycles: true, first, _child));
        Assert.StartsWith("the shell could not move it: ", gate.Outcome(first, 0, aborted: true).Reason);
    }

    [Fact]
    public void AnItemAfterACancelSaysTheJobWasCanceled()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var gate = new RecycleGate(_ =>
        {
            cts.Cancel();
            return null;
        }, cts.Token);
        var first = gate.Expect(_folder)!;
        var second = gate.Expect(_file)!;
        var unreached = gate.Expect(Path.Combine(_root, "third.txt"))!;
        Assert.Equal("the job was canceled", gate.PreDelete(recycles: true, first, _folder));
        Assert.NotNull(gate.PreDelete(recycles: true, second, _file));
        Assert.Equal("the job was canceled", gate.Outcome(second, 0, aborted: true).Reason);
        Assert.Equal("the job was canceled", gate.Outcome(unreached, 0, aborted: true).Reason);
    }

    [Fact]
    public void OnlyFinalPathsWithADriveLetterOrAShareAreAskedForTheirDrive()
    {
        Assert.Equal(@"C:\Users\a.txt", FinalPaths.ToDos(@"\\?\C:\Users\a.txt"));
        Assert.Equal(@"C:\", FinalPaths.ToDos(@"\\?\C:\"));
        Assert.Equal(@"\\server\share\a.txt", FinalPaths.ToDos(@"\\?\UNC\server\share\a.txt"));
        Assert.Equal(@"C:\Users\a.txt", FinalPaths.ToDos(@"C:\Users\a.txt"));
        // A volume without a drive letter: without the prefix this would name a place below the current folder. Its
        // drive type is unknown, so the item stays.
        Assert.Null(FinalPaths.ToDos(@"\\?\Volume{0b1c2d3e-0000-0000-0000-000000000000}\a.txt"));
        Assert.Null(FinalPaths.ToDos(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\a.txt"));
        Assert.Null(FinalPaths.ToDos(@"Volume{0b1c2d3e-0000-0000-0000-000000000000}\a.txt"));
    }

    [Fact]
    public void AnItemStillInPlaceDidNotMove()
    {
        Directory.CreateDirectory(_folder);
        var gate = Gate();
        var item = gate.Expect(_folder)!;
        Assert.Null(gate.PreDelete(recycles: true, item, _folder));
        Assert.True(gate.PostDelete(item, _folder, 0, recycled: true, null));
        Assert.Equal(new RecycleOutcome(_folder, false, "the shell did not move it"), gate.Outcome(item, 0, aborted: false));
    }
}
