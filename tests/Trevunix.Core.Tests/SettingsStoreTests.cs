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
}
