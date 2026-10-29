using Microsoft.Win32;
using Trevunix.Core.Apps;
using Trevunix.Core.Cleanup;

namespace Trevunix.Core.Tests;

public class UninstallTests
{
    [Theory]
    [InlineData("7-Zip 23.01 (x64)", "7zip")]
    [InlineData("7-Zip", "7zip")]
    [InlineData("Mozilla Firefox (x64 en-US)", "mozillafirefox")]
    [InlineData("Notepad++ (64-bit x64)", "notepad")]
    [InlineData("VLC media player v3.0.20", "vlcmediaplayer")]
    [InlineData("Microsoft Office 2016", "microsoftoffice2016")]
    public void Names_are_compared_without_versions_and_bitness(string name, string expected) =>
        Assert.Equal(expected, Leftovers.Normalize(name));

    [Theory]
    [InlineData(
        "\"C:\\Program Files\\App\\unins000.exe\" /SILENT",
        "C:\\Program Files\\App\\unins000.exe",
        "/SILENT"
    )]
    [InlineData(
        "C:\\Program Files\\App\\uninstall.exe --remove",
        "C:\\Program Files\\App\\uninstall.exe",
        "--remove"
    )]
    [InlineData(
        "MsiExec.exe /I{12345678-1234-1234-1234-123456789012}",
        "MsiExec.exe",
        "/X{12345678-1234-1234-1234-123456789012}"
    )]
    [InlineData(
        "MsiExec.exe /X{12345678-1234-1234-1234-123456789012}",
        "MsiExec.exe",
        "/X{12345678-1234-1234-1234-123456789012}"
    )]
    public void Uninstall_commands_are_split_and_msi_removes(string line, string file, string args)
    {
        var command = InstalledPrograms.ParseCommand(line);

        Assert.NotNull(command);
        Assert.Equal(file, command.FileName);
        Assert.Equal(args, command.Arguments);
    }

    [Fact]
    public void Empty_uninstall_command_is_null() =>
        Assert.Null(InstalledPrograms.ParseCommand("  "));

    [Theory]
    [InlineData("C:\\App\\app.exe,0", "C:\\App\\app.exe")]
    [InlineData("\"C:\\App\\app.exe\"", "C:\\App\\app.exe")]
    [InlineData("C:\\App\\app.ico", "C:\\App\\app.ico")]
    public void Icon_path_drops_the_index(string value, string expected) =>
        Assert.Equal(expected, InstalledPrograms.IconFile(value));

    [Fact]
    public void Finds_install_folder_and_name_matches_but_not_shared_or_other_programs()
    {
        using var tree = new TempTree();
        var programFiles = tree.Folder("Program Files");
        var appData = tree.Folder("AppData");
        var install = tree.Folder(@"Program Files\Fancy Writer");
        tree.File(@"Program Files\Fancy Writer\writer.exe", 10);
        tree.File(@"AppData\Fancy Writer\settings.ini", 5);
        tree.File(@"AppData\Quill Soft\Fancy Writer\cache.bin", 5);
        tree.File(@"AppData\Microsoft\Fancy Writer.txt", 5);
        tree.File(@"AppData\Other App\data.bin", 5);
        // Another installed program lives here; never offered even though the name matches.
        tree.File(@"Program Files\Tools\FancyWriter\other.exe", 5);

        var finder = new Leftovers([programFiles, appData], new CleanupSafety([], []));
        var program = Program("Fancy Writer 2.1 (x64)", "Quill Soft Ltd", install);
        var other = Program(
            "Shared Tools",
            "Someone",
            System.IO.Path.Combine(tree.Root, @"Program Files\Tools")
        );

        var found = finder.Find(program, [program, other]);
        var folders = found.Where(l => l.Kind == LeftoverKind.Folder).ToList();

        Assert.Contains(folders, l => l.Path == install && l.Sure);
        Assert.Contains(
            folders,
            l => l.Path == System.IO.Path.Combine(tree.Root, @"AppData\Fancy Writer") && !l.Sure
        );
        Assert.Contains(
            folders,
            l => l.Path == System.IO.Path.Combine(tree.Root, @"AppData\Quill Soft\Fancy Writer")
        );
        Assert.DoesNotContain(folders, l => l.Path.Contains("Microsoft"));
        Assert.DoesNotContain(folders, l => l.Path.Contains("Other App"));
        Assert.DoesNotContain(folders, l => l.Path.Contains("Tools"));
        Assert.DoesNotContain(folders, l => l.Path == programFiles || l.Path == appData);
    }

    [Fact]
    public void Install_location_at_a_base_folder_is_never_offered()
    {
        using var tree = new TempTree();
        var programFiles = tree.Folder("Program Files");
        tree.File(@"Program Files\Someone Else\app.exe", 1);
        var finder = new Leftovers([programFiles], new CleanupSafety([], []));

        var found = finder.Find(Program("Broken Entry", null, programFiles));

        Assert.DoesNotContain(found, l => l.Kind == LeftoverKind.Folder);
    }

    private static InstalledProgram Program(string name, string? publisher, string? location) =>
        new(
            name,
            publisher,
            "1.0",
            location,
            null,
            null,
            null,
            0,
            null,
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            "Trevunix-test-" + Guid.NewGuid().ToString("N")
        );
}
