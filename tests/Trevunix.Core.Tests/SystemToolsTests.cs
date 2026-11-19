using Microsoft.Win32;
using Trevunix.Core.Network;
using Trevunix.Core.Tweaks;
using Trevunix.Core.Updates;

namespace Trevunix.Core.Tests;

public class SystemToolsTests
{
    [Fact]
    public void WindowsUpdatePause_PausesClampsAndResumes()
    {
        var sub = @"Software\TrevunixTests\" + Guid.NewGuid().ToString("N");
        var pause = new WindowsUpdatePause($@"HKCU\{sub}");
        var now = new DateTime(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc);
        try
        {
            Assert.Null(pause.PausedUntil(now));

            pause.Pause(99, now);
            Assert.Equal(now.AddDays(WindowsUpdatePause.MaxDays), pause.PausedUntil(now));
            using (var key = Registry.CurrentUser.OpenSubKey(sub))
                Assert.Equal("2026-12-24T08:00:00Z", key!.GetValue("PauseQualityUpdatesEndTime"));

            Assert.Null(pause.PausedUntil(now.AddDays(40)));

            pause.Resume();
            Assert.Null(pause.PausedUntil(now));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [InlineData("Open with &Code", "Open with Code")]
    [InlineData(" Scan with Defender ", "Scan with Defender")]
    public void ContextMenu_CleansNames(string raw, string expected) =>
        Assert.Equal(expected, ContextMenu.CleanName(raw));

    [Theory]
    [InlineData("{09a47860-11b0-4da5-afa5-26d86198a780}", "{09A47860-11B0-4DA5-AFA5-26D86198A780}")]
    [InlineData("EPP", null)]
    [InlineData(null, null)]
    public void ContextMenu_ParsesClsids(string? raw, string? expected) =>
        Assert.Equal(expected, ContextMenu.ParseClsid(raw));

    [Fact]
    public void ContextMenu_ListsWithoutThrowing() => Assert.NotNull(ContextMenu.List());

    [Fact]
    public void Firewall_RuleNamesDifferByDirection() =>
        Assert.NotEqual(
            FirewallBlock.RuleName(@"C:\a.exe", 1),
            FirewallBlock.RuleName(@"C:\a.exe", 2)
        );

    [Fact]
    public void Firewall_BlocksAndUnblocks_WhenElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (
            !new System.Security.Principal.WindowsPrincipal(identity).IsInRole(
                System.Security.Principal.WindowsBuiltInRole.Administrator
            )
        )
            Assert.Skip("Needs administrator rights.");

        var exe = $@"C:\TrevunixTest\{Guid.NewGuid():N}.exe";
        try
        {
            FirewallBlock.BlockApp(exe);
            Assert.Contains(FirewallBlock.List(), a => a.Path == exe);
        }
        finally
        {
            FirewallBlock.UnblockApp(exe);
        }
        Assert.DoesNotContain(FirewallBlock.List(), a => a.Path == exe);
    }
}
