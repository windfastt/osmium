using Osmium.Core.Archives;
using Osmium.Core.Container;

namespace Osmium.Core.Tests;

public class ImportLimitsTests
{
    [Fact]
    public void DefaultsSuitRealArchives()
    {
        var l = ImportLimits.Default;
        Assert.Equal(1_000_000, l.MaxEntries);
        Assert.Equal(64, l.MaxPathDepth);
        Assert.Equal(1L << 30, l.ExpansionFloorBytes);
        Assert.Equal(1000, l.MaxExpansionRatio);
        Assert.Equal(1L << 30, l.FreeSpaceReserveBytes);
        Assert.Equal(0.02, l.FreeSpaceReserveFraction);
        Assert.Equal(MemoryBudgets.Default(), l.MemoryBudgetBytes);
        Assert.False(l.AllowLargeExpansion);
        Assert.Equal(new ImportLimits(), l);
        Assert.Same(ImportLimits.Default, new ArchiveOpenOptions().Limits);
        Assert.Null(new ArchiveExtractRequest().Limits);
    }

    [Fact]
    public void WithChangesOneValueAndKeepsTheOthers()
    {
        var l = ImportLimits.Default with { AllowLargeExpansion = true };
        Assert.True(l.AllowLargeExpansion);
        Assert.Equal(ImportLimits.Default with { AllowLargeExpansion = true }, l);
        Assert.Equal(ImportLimits.Default, l with { AllowLargeExpansion = false });
        Assert.False(ImportLimits.Default.AllowLargeExpansion); // the default itself is unchanged

        var m = ImportLimits.Default with { MaxEntries = 10, MemoryBudgetBytes = 1 << 20 };
        Assert.Equal(10, m.MaxEntries);
        Assert.Equal(1 << 20, m.MemoryBudgetBytes);
        Assert.Equal(64, m.MaxPathDepth);
        Assert.Equal(1000, m.MaxExpansionRatio);
        Assert.Equal(0.02, m.FreeSpaceReserveFraction);
    }

    [Fact]
    public void LimitExceptionsAreIOExceptions()
    {
        var e = new ExpansionLimitException(1000, 5L << 30);
        Assert.IsAssignableFrom<IOException>(e);
        Assert.Equal(1000, e.Factor);
        Assert.Equal(5L << 30, e.OutputBytes);
        Assert.Contains("times its size", e.Message);
        Assert.IsAssignableFrom<IOException>(new FreeSpaceException("full"));
        Assert.IsAssignableFrom<IOException>(new EntryLimitException("deep"));
    }
}
