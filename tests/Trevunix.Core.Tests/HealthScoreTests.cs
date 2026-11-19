using Trevunix.Core.Cleanup;
using Trevunix.Core.Monitoring;

namespace Trevunix.Core.Tests;

public class HealthScoreTests
{
    [Fact]
    public void Compute_PerfectWhenNothingIsWrongOrKnown()
    {
        Assert.Equal(100, HealthScore.Compute(new HealthInputs()).Score);
        Assert.Equal(100, HealthScore.Compute(new HealthInputs(50, 0, 0, 0, 0, 0)).Score);
    }

    [Fact]
    public void Compute_CapsEachKind()
    {
        var (score, items) = HealthScore.Compute(
            new HealthInputs(
                SystemDriveFreePercent: 5,
                FailingDisks: 0,
                PendingRecommendedTweaks: 3,
                DriverProblems: 0,
                AppUpdates: 40,
                HighImpactStartup: 1
            )
        );

        // 25 (drive) + 6 (tweaks) + 10 (updates, capped) + 3 (startup)
        Assert.Equal(56, score);
        Assert.Equal(
            [
                ScoreIssue.SystemDriveFull,
                ScoreIssue.PendingTweaks,
                ScoreIssue.AppUpdates,
                ScoreIssue.HeavyStartup,
            ],
            items.Select(i => i.Issue)
        );
    }

    [Fact]
    public void Compute_NeverBelowZero() =>
        Assert.Equal(0, HealthScore.Compute(new HealthInputs(1, 5, 50, 9, 99, 20)).Score);

    [Fact]
    public void ParseWingetUpgrades_ReadsRowsInAnyLanguage()
    {
        const string output = """
            Tên                Id                  Phiên bản  Có sẵn     Nguồn
            ------------------------------------------------------------------
            7-Zip 24.07 (x64)  7zip.7zip           24.07      24.09      winget
            Spotify            9NCBCSZSJRSB        1.2        1.3        msstore
            Old Tool           Vendor.OldTool      < 2.0      2.5        winget
            Có 3 bản nâng cấp.
            """;

        var updates = HealthScore.ParseWingetUpgrades(output);

        Assert.Equal(
            [
                new AppUpdate("7-Zip 24.07 (x64)", "7zip.7zip", "24.07", "24.09"),
                new AppUpdate("Spotify", "9NCBCSZSJRSB", "1.2", "1.3"),
                new AppUpdate("Old Tool", "Vendor.OldTool", "< 2.0", "2.5"),
            ],
            updates
        );
        Assert.Empty(HealthScore.ParseWingetUpgrades("No installed package found."));
    }

    [Fact]
    public void BrowserCloseWatch_ReportsBrowsersThatWentAway()
    {
        var watch = new BrowserCloseWatch();
        Assert.Empty(watch.Update(["chrome", "explorer", "firefox"]));
        Assert.Equal(["Chrome"], watch.Update(["firefox", "explorer"]));
        Assert.Empty(watch.Update(["firefox"]));
        Assert.Equal(["Firefox"], watch.Update([]));
    }
}
