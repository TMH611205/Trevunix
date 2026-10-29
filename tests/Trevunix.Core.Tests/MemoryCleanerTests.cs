using Trevunix.Core.Memory;

namespace Trevunix.Core.Tests;

public class MemoryCleanerTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData(59, 60, false)]
    [InlineData(60, 60, true)]
    [InlineData(91, 90, true)]
    [InlineData(95, 0, false)]
    public void Auto_clean_starts_at_the_threshold(int load, int threshold, bool expected) =>
        Assert.Equal(expected, MemoryCleaner.ShouldAutoClean(load, threshold, Now, null, Cooldown));

    [Fact]
    public void Auto_clean_waits_for_the_cooldown()
    {
        Assert.False(MemoryCleaner.ShouldAutoClean(90, 70, Now, Now.AddMinutes(-1), Cooldown));
        Assert.True(MemoryCleaner.ShouldAutoClean(90, 70, Now, Now.AddMinutes(-2), Cooldown));
    }

    [Fact]
    public void Thresholds_run_from_60_to_90_in_steps_of_5() =>
        Assert.Equal([60, 65, 70, 75, 80, 85, 90], MemoryCleaner.AutoThresholds);

    [Fact]
    public void Read_reports_this_machine()
    {
        var status = MemoryCleaner.Read();

        Assert.True(status.Total > 0);
        Assert.InRange(status.Available, 1, status.Total);
        Assert.InRange(status.LoadPercent, 0, 100);
    }

    [Fact]
    public void Freed_is_never_negative() =>
        Assert.Equal(0, new MemoryCleanResult(500, 400, 3, false).Freed);
}
