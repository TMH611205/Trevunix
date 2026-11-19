using System.Xml.Linq;
using Trevunix.Core.Apps;
using Trevunix.Core.Startup;

namespace Trevunix.Core.Tests;

public class StartupExtrasTests
{
    [Theory]
    [InlineData(100_000, 100_000, StartupImpactLevel.Low)]
    [InlineData(500_000, 0, StartupImpactLevel.Medium)]
    [InlineData(0, 1_000_000, StartupImpactLevel.Medium)]
    [InlineData(2_000_000, 0, StartupImpactLevel.High)]
    [InlineData(0, 5_000_000, StartupImpactLevel.High)]
    public void Classify_UsesTaskManagerLimits(long cpu, long disk, StartupImpactLevel expected) =>
        Assert.Equal(expected, StartupImpact.Classify(cpu, disk));

    [Fact]
    public void Parse_SumsProcessesWithTheSameImage()
    {
        var doc = XDocument.Parse(
            """
            <StartupData>
              <Process Name="C:\A\app.exe"><DiskUsage>200000</DiskUsage><CpuUsage>200000</CpuUsage></Process>
              <Process Name="c:\a\APP.exe"><DiskUsage>200000</DiskUsage><CpuUsage>200000</CpuUsage></Process>
              <Process Name="C:\B\tiny.exe"><DiskUsage>10</DiskUsage><CpuUsage>10</CpuUsage></Process>
            </StartupData>
            """
        );

        var impacts = StartupImpact.Parse(doc);

        Assert.Equal(StartupImpactLevel.Medium, impacts[@"C:\A\app.exe"]);
        Assert.Equal(StartupImpactLevel.Low, impacts[@"C:\B\tiny.exe"]);
    }

    [Theory]
    [InlineData(
        "\"C:\\Program Files\\App\\app.exe\" --tray",
        "C:\\Program Files\\App\\app.exe",
        "--tray"
    )]
    [InlineData("C:\\Tools\\run.exe /min", "C:\\Tools\\run.exe", "/min")]
    [InlineData("C:\\Tools\\run.exe", "C:\\Tools\\run.exe", "")]
    public void SplitCommand_SeparatesProgramAndArguments(
        string command,
        string exe,
        string args
    ) => Assert.Equal((exe, args), StartupDelay.SplitCommand(command));

    [Fact]
    public void TaskName_HasNoPathSeparators()
    {
        var entry = new StartupEntry(
            "My\\App: Helper",
            "x.exe",
            StartupSource.UserRegistry,
            true,
            null,
            null
        );
        Assert.Equal("\\Trevunix\\Delayed\\My_App_ Helper", StartupDelay.TaskName(entry));
    }

    [Fact]
    public void Delay_CreatesTaskAndUndoRemovesIt_WhenElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (
            !new System.Security.Principal.WindowsPrincipal(identity).IsInRole(
                System.Security.Principal.WindowsBuiltInRole.Administrator
            )
        )
            Assert.Skip("Needs administrator rights.");

        using var folders = new TempTree();
        var root = @"HKCU\Software\TrevunixTests\" + Guid.NewGuid().ToString("N");
        var manager = new StartupManager(
            new StartupLocations(
                $@"{root}\UserRun",
                $@"{root}\MachineRun",
                $@"{root}\MachineRun32",
                $@"{root}\UserApproved",
                $@"{root}\MachineApproved",
                folders.Folder("user"),
                folders.Folder("machine")
            )
        );
        var entry = new StartupEntry(
            "TrevunixTest " + Guid.NewGuid().ToString("N"),
            "\"C:\\Windows\\notepad.exe\" /x",
            StartupSource.UserRegistry,
            true,
            null,
            null
        );
        try
        {
            Assert.True(StartupDelay.Delay(entry, manager));
            Assert.True(StartupDelay.IsDelayed(entry));
        }
        finally
        {
            Assert.True(StartupDelay.Undo(entry, manager));
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                root["HKCU\\".Length..],
                throwOnMissingSubKey: false
            );
        }
        Assert.False(StartupDelay.IsDelayed(entry));
    }

    [Fact]
    public void BrokenShortcuts_ReportsTopEmptyStartMenuFolders()
    {
        using var tree = new TempTree();
        var menu = tree.Folder("Start Menu");
        tree.Folder(@"Start Menu\Old App\Sub");
        tree.File(@"Start Menu\Kept\readme.txt", 1);

        var issues = BrokenShortcuts.Find([menu]);

        var issue = Assert.Single(issues);
        Assert.Equal(ShortcutProblem.EmptyFolder, issue.Problem);
        Assert.EndsWith("Old App", issue.Path);
    }

    [Fact]
    public void IsMissing_OnlyForLocalPathsOnPresentDrives()
    {
        Assert.True(
            BrokenShortcuts.IsMissing(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"))
        );
        Assert.False(BrokenShortcuts.IsMissing(Environment.ProcessPath!));
        Assert.False(BrokenShortcuts.IsMissing(@"\\server\share\app.exe"));
    }
}
