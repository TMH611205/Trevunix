using Trevunix.Core.Settings;

namespace Trevunix.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var tree = new TempTree();
        var store = new SettingsStore(Path.Combine(tree.Root, "s.json"));

        store.Save(new AppSettings { Language = "en", Theme = AppTheme.Dark });
        var loaded = store.Load();

        Assert.Equal("en", loaded.Language);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
    }

    [Fact]
    public void Load_MissingOrBrokenFile_ReturnsDefaults()
    {
        using var tree = new TempTree();
        var path = Path.Combine(tree.Root, "s.json");
        Assert.Equal("vi", new SettingsStore(path).Load().Language);

        File.WriteAllText(path, "{ not json");
        Assert.Equal("vi", new SettingsStore(path).Load().Language);
    }

    [Fact]
    public void Load_BrokenFile_KeepsBackupAndReportsIt()
    {
        using var tree = new TempTree();
        var store = new SettingsStore(Path.Combine(tree.Root, "s.json"));
        File.WriteAllText(store.FilePath, "{ not json");

        store.Load();

        Assert.True(store.LoadedDefaultsAfterError);
        Assert.Equal("{ not json", File.ReadAllText(store.BackupPath));

        store.Save(new AppSettings());
        store.Load();
        Assert.False(store.LoadedDefaultsAfterError);
    }

    [Fact]
    public void ErrorLog_RotatesPastLimit()
    {
        using var tree = new TempTree();
        var path = Path.Combine(tree.Root, "errors.log");

        Trevunix.Core.Common.ErrorLog.Write(new InvalidOperationException("first"), path, 10);
        Trevunix.Core.Common.ErrorLog.Write(new InvalidOperationException("second"), path, 10);

        Assert.Contains("second", File.ReadAllText(path));
        Assert.DoesNotContain("first", File.ReadAllText(path));
        Assert.Contains("first", File.ReadAllText(Path.Combine(tree.Root, "errors.old.log")));
    }
}
