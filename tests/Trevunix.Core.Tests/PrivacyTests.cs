using Microsoft.Win32;
using Trevunix.Core.Cleanup;
using Trevunix.Core.Files;

namespace Trevunix.Core.Tests;

public class PrivacyTests
{
    [Fact]
    public void RegistryHistory_IsCountedAndCleared()
    {
        var sub = @"Software\TrevunixTests\" + Guid.NewGuid().ToString("N");
        using (var key = Registry.CurrentUser.CreateSubKey(sub))
        {
            key.SetValue("a", "cmd");
            key.SetValue("b", "notepad");
            key.SetValue("MRUList", "ab");
        }
        try
        {
            var target = new CleanupTarget("T", CleanupGroup.Privacy, false, [], [$@"HKCU\{sub}"]);
            var engine = new CleanupEngine();

            var scan = engine.Scan(target, TestContext.Current.CancellationToken);
            Assert.Equal(3, scan.Count);

            var result = engine.Clean(
                [scan],
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.Equal(3, result.DeletedFiles);
            using var after = Registry.CurrentUser.OpenSubKey(sub);
            Assert.NotNull(after);
            Assert.Empty(after.GetValueNames());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void RecentFolder_IsASafeCleanupRoot()
    {
        var recent = Environment.ExpandEnvironmentVariables(@"%APPDATA%\Microsoft\Windows\Recent");
        Assert.True(new CleanupSafety().IsSafeRoot(recent));
    }

    [Fact]
    public void Shredder_DestroysFilesAndFolders()
    {
        using var tree = new TempTree();
        var file = tree.File("one.bin", 3000);
        tree.File(@"dir\two.bin", 10);
        tree.File(@"dir\sub\three.bin", 0);

        var result = Shredder.Shred(
            [file, Path.Combine(tree.Root, "dir")],
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new ShredResult(3, 0), result);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tree.Root));
    }

    [Fact]
    public void Shredder_RefusesSystemPathsAndDriveRoots()
    {
        Assert.True(Shredder.IsRefused(@"C:\"));
        Assert.True(
            Shredder.IsRefused(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
        );
        Assert.False(Shredder.IsRefused(Path.Combine(Path.GetTempPath(), "x.txt")));
    }
}
