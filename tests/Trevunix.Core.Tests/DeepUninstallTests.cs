using System.Text.Json.Nodes;
using Microsoft.Win32;
using Trevunix.Core.Apps;
using Trevunix.Core.Browsers;
using Trevunix.Core.Cleanup;

namespace Trevunix.Core.Tests;

public class DeepUninstallTests
{
    private const string Id = "abcdefghijklmnopabcdefghijklmnop";

    [Fact]
    public void Chromium_settings_lose_the_extension_and_its_hashes_but_nothing_else()
    {
        var root = JsonNode
            .Parse(
                $$"""
                {
                  "extensions": {
                    "settings": { "{{Id}}": { "location": 1 }, "other": { "location": 1 } },
                    "pinned_extensions": [ "{{Id}}", "other" ]
                  },
                  "protection": {
                    "macs": {
                      "extensions": {
                        "settings": { "{{Id}}": "A", "other": "B" },
                        "settings_encrypted_hash": { "{{Id}}": "C", "other": "D" }
                      }
                    }
                  }
                }
                """
            )!
            .AsObject();

        Assert.True(BrowserExtensions.RemoveExtension(root, Id));

        var text = root.ToJsonString();
        Assert.DoesNotContain(Id, text);
        // The other extension keeps its settings, both hashes and its pin.
        Assert.Equal(4, text.Split("\"other\"").Length - 1);
        Assert.False(BrowserExtensions.RemoveExtension(root, Id));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --tray", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Program Files\\App\\app.exe /background", "C:\\Program Files\\App\\app.exe")]
    [InlineData("\\??\\C:\\Program Files\\App\\driver.sys", "C:\\Program Files\\App\\driver.sys")]
    [InlineData("", null)]
    public void Command_lines_give_the_file_they_run(string command, string? expected) =>
        Assert.Equal(expected, SystemTraces.ExeOf(command));

    [Fact]
    public void Traces_match_only_inside_the_program_folders()
    {
        string[] folders = [@"C:\Program Files\Fancy Writer"];

        Assert.True(SystemTraces.IsInside(@"C:\Program Files\Fancy Writer\bin\fw.exe", folders));
        Assert.False(SystemTraces.IsInside(@"C:\Program Files\Fancy Writer 2\fw.exe", folders));
        Assert.False(SystemTraces.IsInside(@"C:\Windows\System32\svchost.exe", folders));
        Assert.False(SystemTraces.IsInside("fw.exe", folders));
    }

    [Fact]
    public void A_forced_uninstall_of_a_folder_finds_the_folder_and_has_no_entry()
    {
        using var tree = new TempTree();
        var programFiles = tree.Folder("Program Files");
        var folder = tree.Folder(@"Program Files\Stubborn Tool");
        tree.File(@"Program Files\Stubborn Tool\tool.exe", 10);
        var program = new InstalledProgram(
            "Stubborn Tool",
            null,
            null,
            folder,
            null,
            null,
            null,
            0,
            null,
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            ""
        );

        Assert.False(InstalledPrograms.IsStillInstalled(program));
        var found = new Leftovers([programFiles], new CleanupSafety([], [])).Find(program);
        Assert.Contains(found, l => l.Kind == LeftoverKind.Folder && l.Path == folder && l.Sure);
        Assert.DoesNotContain(found, l => l.Kind == LeftoverKind.RegistryKey && l.Sure);
    }

    [Fact]
    public void A_forced_uninstall_never_takes_a_base_folder()
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var program = new InstalledProgram(
            "Temp",
            null,
            null,
            temp,
            null,
            null,
            null,
            0,
            null,
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            ""
        );

        var found = new Leftovers([temp], new CleanupSafety([], [])).Find(program);

        Assert.DoesNotContain(found, l => l.Kind == LeftoverKind.Folder);
    }

    [Fact]
    public void A_file_that_is_not_a_shortcut_has_no_target() =>
        Assert.Null(ShortcutFile.Parse([1, 2, 3, 4]));
}
