using Microsoft.Win32;
using Trevunix.Core.Apps;
using Trevunix.Core.Startup;

namespace Trevunix.Core.Tests;

public class RevoGapTests
{
    private static InstalledProgram Program(string name, string? publisher = null, long size = 0) =>
        new(
            name,
            publisher,
            "1.0",
            @"C:\Apps\" + name,
            null,
            null,
            null,
            size,
            new DateTime(2026, 1, 2),
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            name
        );

    [Fact]
    public void ToCsv_QuotesAndSorts()
    {
        var csv = ProgramListExport.ToCsv([
            Program("Zeta"),
            Program("Alpha, Pro", "Acme \"Inc\"", 5 * 1024 * 1024),
        ]);
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Name,Publisher,Version,Size (MB),Installed,Location", lines[0]);
        Assert.Equal(
            "\"Alpha, Pro\",\"Acme \"\"Inc\"\"\",1.0,5,2026-01-02,\"C:\\Apps\\Alpha, Pro\"",
            lines[1]
        );
        Assert.StartsWith("Zeta,,1.0,,2026-01-02,", lines[2]);
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag", true)]
    [InlineData(@"\GoogleSystem\GoogleUpdater", false)]
    [InlineData(@"\MicrosoftEdgeUpdateTaskMachineCore", false)]
    public void IsMicrosoftTask_OnlyTheMicrosoftFolder(string path, bool expected) =>
        Assert.Equal(expected, Autoruns.IsMicrosoftTask(path));

    [Theory]
    [InlineData(@"C:\Windows\system32\svchost.exe -k netsvcs", true)]
    [InlineData(@"\SystemRoot\System32\drivers\x.sys", true)]
    [InlineData("\"C:\\Program Files\\Vendor\\svc.exe\" /run", false)]
    public void IsWindowsPath_SpotsWindowsBinaries(string command, bool expected) =>
        Assert.Equal(expected, Autoruns.IsWindowsPath(command));

    [Fact]
    public void Lists_RunOnThisPc()
    {
        Assert.NotNull(Autoruns.ListTasks());
        Assert.All(Autoruns.ListServices(), s => Assert.False(Autoruns.IsMicrosoft(s.Company)));
    }
}
