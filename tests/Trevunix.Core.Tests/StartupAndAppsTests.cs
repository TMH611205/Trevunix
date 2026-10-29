using Microsoft.Win32;
using Trevunix.Core.Apps;
using Trevunix.Core.Startup;

namespace Trevunix.Core.Tests;

public sealed class StartupManagerTests : IDisposable
{
    private readonly string _sub = @"Software\TrevunixTests_" + Guid.NewGuid().ToString("N");
    private readonly TempTree _folders = new();
    private readonly StartupLocations _at;

    public StartupManagerTests()
    {
        var root = $@"HKCU\{_sub}";
        _at = new StartupLocations(
            $@"{root}\UserRun",
            $@"{root}\MachineRun",
            $@"{root}\MachineRun32",
            $@"{root}\UserApproved",
            $@"{root}\MachineApproved",
            _folders.Folder("user"),
            _folders.Folder("machine")
        );
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_sub, throwOnMissingSubKey: false);
        _folders.Dispose();
    }

    private void AddRun(string key, string name, string command)
    {
        using var k = Registry.CurrentUser.CreateSubKey($@"{_sub}\{key}");
        k.SetValue(name, command);
    }

    private byte[]? Approved(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{_sub}\{key}");
        return k?.GetValue(name) as byte[];
    }

    [Fact]
    public void List_ReadsRunKeysAndFolders()
    {
        AddRun("UserRun", "Chat", @"""C:\Apps\chat.exe"" --minimized");
        AddRun("MachineRun32", "Updater", @"C:\Old\upd.exe /silent");
        _folders.File(@"user\Notes.lnk", 10);
        _folders.File(@"user\desktop.ini", 10);

        var entries = new StartupManager(_at).List();

        Assert.Equal(["Chat", "Notes", "Updater"], entries.Select(e => e.Name));
        Assert.All(entries, e => Assert.True(e.Enabled));
        Assert.Equal(StartupSource.MachineRegistry32, entries[2].Source);
        Assert.Equal("Notes.lnk", entries[1].ApprovedName);
    }

    [Fact]
    public void SetEnabled_WritesStartupApprovedAndListReflectsIt()
    {
        AddRun("UserRun", "Chat", @"C:\Apps\chat.exe");
        var manager = new StartupManager(_at);
        var entry = manager.List().Single();

        manager.SetEnabled(entry, false);
        var data = Approved(@"UserApproved\Run", "Chat");
        Assert.NotNull(data);
        Assert.Equal(12, data.Length);
        Assert.Equal(0x03, data[0]);
        Assert.False(manager.List().Single().Enabled);

        manager.SetEnabled(entry, true);
        Assert.Equal(0x02, Approved(@"UserApproved\Run", "Chat")![0]);
        Assert.True(manager.List().Single().Enabled);
    }

    [Fact]
    public void SetEnabled_FolderEntry_UsesFileNameAndRun32UsesItsOwnKey()
    {
        _folders.File(@"machine\Tool.lnk", 1);
        AddRun("MachineRun32", "Legacy", @"C:\x.exe");
        var manager = new StartupManager(_at);
        var entries = manager.List();

        manager.SetEnabled(entries.Single(e => e.Name == "Tool"), false);
        manager.SetEnabled(entries.Single(e => e.Name == "Legacy"), false);

        Assert.Equal(0x03, Approved(@"MachineApproved\StartupFolder", "Tool.lnk")![0]);
        Assert.Equal(0x03, Approved(@"MachineApproved\Run32", "Legacy")![0]);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" -x", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe -x", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\tool.EXE", @"C:\tool.EXE")]
    [InlineData(@"rundll32 shell32.dll,Control_RunDLL", "rundll32")]
    [InlineData("", null)]
    public void ExtractPath_HandlesCommonCommandShapes(string command, string? expected)
    {
        Assert.Equal(expected, StartupManager.ExtractPath(command));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[] { 0x02, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, false)]
    public void IsEnabled_FollowsTaskManagerRule(byte[]? data, bool expected)
    {
        Assert.Equal(expected, StartupManager.IsEnabled(data));
    }
}

public class StoreAppsTests
{
    [Fact]
    public void Parse_ArrayOfPackages_MapsNamesAndFiltersProtected()
    {
        const string json = """
            [
              {"Name":"Microsoft.BingNews","PackageFullName":"Microsoft.BingNews_1.0_x64__8wekyb3d8bbwe","Publisher":"CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond","Version":"1.0"},
              {"Name":"Microsoft.WindowsStore","PackageFullName":"Microsoft.WindowsStore_1_x64__8wekyb3d8bbwe","Publisher":"CN=Microsoft Corporation","Version":"1"},
              {"Name":"Contoso.SuperEditor","PackageFullName":"Contoso.SuperEditor_2_x64__abc","Publisher":"CN=Contoso","Version":"2"},
              {"Name":"king.com.CandyCrushSaga","PackageFullName":"king.com.CandyCrushSaga_1_x86__kgqvnymyfvs32","Publisher":"CN=king.com","Version":"1"}
            ]
            """;

        var apps = StoreApps.Parse(json);

        Assert.DoesNotContain(apps, a => a.Name == "Microsoft.WindowsStore");
        var news = apps.Single(a => a.Name == "Microsoft.BingNews");
        Assert.Equal("Microsoft News", news.DisplayName);
        Assert.Equal("Microsoft Corporation", news.Publisher);
        Assert.True(news.Suggested);
        Assert.True(apps.Single(a => a.Name.StartsWith("king.com")).Suggested);
        var editor = apps.Single(a => a.Name == "Contoso.SuperEditor");
        Assert.False(editor.Suggested);
        Assert.Equal("Super Editor", editor.DisplayName);
        Assert.False(apps[^1].Suggested);
    }

    [Fact]
    public void Parse_SingleObject_IsAccepted()
    {
        var apps = StoreApps.Parse(
            """{"Name":"Microsoft.People","PackageFullName":"Microsoft.People_1_x64__8w","Publisher":"CN=Microsoft Corporation","Version":"1"}"""
        );

        Assert.Single(apps);
    }

    [Fact]
    public void Parse_Empty_ReturnsNothing()
    {
        Assert.Empty(StoreApps.Parse("  "));
    }

    [Theory]
    [InlineData("Microsoft.WindowsCalculator", false)]
    [InlineData("Microsoft.Windows.Photos", false)]
    [InlineData("Microsoft.Getstarted", true)]
    [InlineData("SpotifyAB.SpotifyMusic", true)]
    public void IsSuggested_KeepsEverydayTools(string name, bool expected)
    {
        Assert.Equal(expected, StoreApps.IsSuggested(name));
    }

    [Theory]
    [InlineData("Microsoft.WinAppRuntime.DDLM.4000.1049.117.0-x6", true)]
    [InlineData("MicrosoftCorporationII.WinAppRuntime.Main.1.4", true)]
    [InlineData("Microsoft.VCLibs.140.00", true)]
    [InlineData("Microsoft.WindowsStore", true)]
    [InlineData("Microsoft.Windows.Photos", false)]
    [InlineData("Microsoft.BingNews", false)]
    public void IsProtected_HidesRuntimesButKeepsRealApps(string name, bool expected)
    {
        Assert.Equal(expected, StoreApps.IsProtected(name));
    }
}
