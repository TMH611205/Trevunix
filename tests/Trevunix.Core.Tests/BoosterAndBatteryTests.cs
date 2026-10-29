using System.Xml.Linq;
using Trevunix.Core.Maintenance;
using Trevunix.Core.Performance;
using Trevunix.Core.SystemInfo;
using Trevunix.Core.Tweaks;

namespace Trevunix.Core.Tests;

public class BoosterAndBatteryTests
{
    [Fact]
    public void Logon_task_starts_the_exe_in_the_tray_at_sign_in()
    {
        var doc = XDocument.Parse(LaunchAtLogon.BuildTaskXml(@"C:\Apps\Trevunix.exe", @"PC\Owner"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal(
            @"PC\Owner",
            doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value
        );
        Assert.Equal(LaunchAtLogon.Argument, doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("HighestAvailable", doc.Descendants(ns + "RunLevel").Single().Value);
        // It stays running in the tray, so no time limit.
        Assert.Equal("PT0S", doc.Descendants(ns + "ExecutionTimeLimit").Single().Value);
    }

    [Theory]
    [InlineData(
        "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)",
        "381b4222-f694-41f0-9685-ff5bb260df2e"
    )]
    [InlineData(
        "GUID của sơ đồ nguồn: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Hiệu suất cao)",
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"
    )]
    public void Power_plan_is_read_whatever_the_language(string output, string expected) =>
        Assert.Equal(Guid.Parse(expected), GameBooster.ParsePowerPlan(output));

    [Fact]
    public void Power_plan_is_null_without_a_guid() => Assert.Null(GameBooster.ParsePowerPlan(""));

    [Theory]
    [InlineData("explorer")]
    [InlineData("svchost")]
    [InlineData("StartMenuExperienceHost")]
    [InlineData("Trevunix")]
    public void Booster_never_closes_windows_itself(string name) =>
        Assert.False(ProcessControl.CanClose(name));

    [Fact]
    public void Booster_may_close_ordinary_apps() => Assert.True(ProcessControl.CanClose("chrome"));

    [Theory]
    [InlineData(50000L, 40000L, 80)]
    [InlineData(50000L, 51000L, 100)]
    [InlineData(0L, 40000L, null)]
    [InlineData(50000L, null, null)]
    public void Battery_health_is_full_charge_over_design(
        long? design,
        long? full,
        int? expected
    ) => Assert.Equal(expected, BatteryInfo.Health(design, full));

    [Fact]
    public void Appearance_page_takes_colors_taskbar_and_explorer()
    {
        Assert.True(TweakCatalog.IsAppearance(TweakCategory.Appearance));
        Assert.True(TweakCatalog.IsAppearance(TweakCategory.Taskbar));
        Assert.True(TweakCatalog.IsAppearance(TweakCategory.FileExplorer));
        Assert.False(TweakCatalog.IsAppearance(TweakCategory.Performance));
    }

    [Fact]
    public void Appearance_tweaks_are_never_applied_automatically() =>
        Assert.All(
            TweakCatalog.All.Where(t => t.Category == TweakCategory.Appearance),
            t => Assert.Equal(TweakRisk.Optional, t.Risk)
        );
}

public class WmiTests
{
    [Fact]
    public void Old_framework_errors_count_as_wmi_unavailable()
    {
        Assert.True(Wmi.IsUnavailable(new PlatformNotSupportedException()));
        Assert.True(Wmi.IsUnavailable(new TypeInitializationException("X", null)));
        Assert.False(Wmi.IsUnavailable(new ArgumentNullException()));
    }
}
