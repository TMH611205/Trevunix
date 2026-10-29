using System.Xml.Linq;
using Trevunix.Core.DiskUsage;
using Trevunix.Core.Maintenance;
using Trevunix.Core.Monitoring;
using Trevunix.Core.Storage;

namespace Trevunix.Core.Tests;

public class MaintenanceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Quick_cleanup_uses_the_default_targets_but_never_the_recycle_bin()
    {
        Assert.NotEmpty(QuickMaintenance.CleanupTargets);
        Assert.All(QuickMaintenance.CleanupTargets, t => Assert.True(t.SelectedByDefault));
        Assert.DoesNotContain(QuickMaintenance.CleanupTargets, t => t.IsRecycleBin);
    }

    [Theory]
    [InlineData(CleanFrequency.Daily, "ScheduleByDay")]
    [InlineData(CleanFrequency.Weekly, "ScheduleByWeek")]
    [InlineData(CleanFrequency.Monthly, "ScheduleByWeek")]
    public void Task_xml_runs_the_exe_with_the_auto_clean_argument(
        CleanFrequency frequency,
        string schedule
    )
    {
        var xml = AutoCleanSchedule.BuildTaskXml(
            @"C:\Tools & Apps\Trevunix.exe",
            frequency,
            new DateTime(2026, 10, 28, 12, 0, 0),
            @"PC\Owner"
        );

        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(
            @"C:\Tools & Apps\Trevunix.exe",
            doc.Descendants(ns + "Command").Single().Value
        );
        Assert.Equal(AutoCleanSchedule.Argument, doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("HighestAvailable", doc.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("true", doc.Descendants(ns + "StartWhenAvailable").Single().Value);
        Assert.Equal("2026-10-28T12:00:00", doc.Descendants(ns + "StartBoundary").Single().Value);
        Assert.Single(doc.Descendants(ns + schedule));
    }

    [Fact]
    public void Monthly_runs_every_four_weeks()
    {
        var xml = AutoCleanSchedule.BuildTaskXml(
            "a.exe",
            CleanFrequency.Monthly,
            DateTime.Today,
            "u"
        );
        Assert.Contains("<WeeksInterval>4</WeeksInterval>", xml);
    }

    [Fact]
    public void Off_has_no_task_xml() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AutoCleanSchedule.BuildTaskXml("a.exe", CleanFrequency.Off, DateTime.Today, "u")
        );

    [Theory]
    [InlineData(100, 50, false)]
    [InlineData(100, 9, true)] // 91% used
    [InlineData(1000, 4, true)] // under 5 GB left
    [InlineData(10, 0, false)] // small partition, full by design
    public void Drive_alert_when_almost_full(long totalGb, long freeGb, bool expected)
    {
        var drive = new DriveSummary(@"C:\", "", "NTFS", totalGb * Gb, freeGb * Gb);

        var alerts = HealthCheck.Evaluate([drive], [], null);

        Assert.Equal(expected, alerts.Any(a => a.Kind == HealthAlertKind.DriveAlmostFull));
    }

    [Fact]
    public void Disk_alerts_for_bad_health_and_heat()
    {
        var alerts = HealthCheck.Evaluate(
            [],
            [
                Disk("A", HealthState.Warning, 40),
                Disk("B", HealthState.Healthy, 72),
                Disk("C", HealthState.Healthy, 45),
            ],
            null
        );

        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a is { Kind: HealthAlertKind.DiskFailing, Subject: "A" });
        Assert.Contains(
            alerts,
            a => a is { Kind: HealthAlertKind.DiskHot, Subject: "B", Value: 72 }
        );
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(85, false)]
    [InlineData(90, true)]
    public void Cpu_alert_from_90_degrees(int? celsius, bool expected) =>
        Assert.Equal(
            expected,
            HealthCheck.Evaluate([], [], celsius).Any(a => a.Kind == HealthAlertKind.CpuHot)
        );

    [Fact]
    public void Alert_keys_tell_problems_apart()
    {
        var a = new HealthAlert(HealthAlertKind.DiskHot, "A", 70);
        var b = new HealthAlert(HealthAlertKind.DiskHot, "A", 75);
        var c = new HealthAlert(HealthAlertKind.DiskFailing, "A", 0);

        Assert.Equal(a.Key, b.Key);
        Assert.NotEqual(a.Key, c.Key);
    }

    private static DiskHealth Disk(string name, HealthState health, int temperature) =>
        new(name, "SSD", "NVMe", 500 * Gb, health, temperature, null, null, null, null);
}
