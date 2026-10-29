using Trevunix.Core.Cleanup;

namespace Trevunix.Core.Tests;

public class CleanupEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CleanupTarget Target(params CleanupSource[] sources) =>
        new("Test", CleanupGroup.System, true, sources);

    [Fact]
    public void Scan_FolderWithFiles_FindsAllFilesAndTotals()
    {
        using var tree = new TempTree();
        tree.File("cache/a.tmp", 100);
        tree.File("cache/sub/b.tmp", 250);

        var scan = new CleanupEngine().Scan(
            Target(new CleanupSource(Path.Combine(tree.Root, "cache"))),
            Ct
        );

        Assert.Equal(2, scan.Count);
        Assert.Equal(350, scan.Size);
    }

    [Fact]
    public void Scan_PatternAndNonRecursive_OnlyMatchesTopLevel()
    {
        using var tree = new TempTree();
        tree.File("logs/keep.txt", 10);
        tree.File("logs/old.log", 20);
        tree.File("logs/nested/deep.log", 30);

        var source = new CleanupSource(Path.Combine(tree.Root, "logs"), "*.log", Recursive: false);
        var scan = new CleanupEngine().Scan(Target(source), Ct);

        var file = Assert.Single(scan.Files);
        Assert.EndsWith("old.log", file.Path);
    }

    [Fact]
    public void Scan_MinAge_SkipsRecentFiles()
    {
        using var tree = new TempTree();
        var fresh = tree.File("temp/fresh.tmp", 10);
        var old = tree.File("temp/old.tmp", 20);
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fresh, now.AddHours(-2));
        File.SetLastWriteTimeUtc(old, now.AddDays(-3));

        var source = new CleanupSource(
            Path.Combine(tree.Root, "temp"),
            MinAge: TimeSpan.FromDays(1)
        );
        var scan = new CleanupEngine(() => now).Scan(Target(source), Ct);

        Assert.Equal([old], scan.Files.Select(f => f.Path));
    }

    [Fact]
    public void Scan_WildcardFolderSegment_MatchesEveryProfile()
    {
        using var tree = new TempTree();
        tree.File(@"Profiles\abc.default\cache2\x", 5);
        tree.File(@"Profiles\xyz.work\cache2\y", 7);
        tree.File(@"Profiles\xyz.work\other\z", 100);

        var source = new CleanupSource(Path.Combine(tree.Root, @"Profiles\*\cache2"));
        var scan = new CleanupEngine().Scan(Target(source), Ct);

        Assert.Equal(12, scan.Size);
    }

    [Fact]
    public void Scan_OverlappingSources_CountsEachFileOnce()
    {
        using var tree = new TempTree();
        tree.File("dir/a.log", 10);

        var dir = Path.Combine(tree.Root, "dir");
        var scan = new CleanupEngine().Scan(
            Target(new CleanupSource(dir), new CleanupSource(dir, "*.log")),
            Ct
        );

        Assert.Equal(1, scan.Count);
    }

    [Fact]
    public void Scan_MissingFolder_FindsNothing()
    {
        var scan = new CleanupEngine().Scan(
            Target(new CleanupSource(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))),
            Ct
        );

        Assert.Equal(0, scan.Count);
    }

    [Fact]
    public void Clean_DeletesFilesAndEmptySubfoldersButKeepsRoot()
    {
        using var tree = new TempTree();
        tree.File("cache/a.tmp", 100);
        tree.File("cache/sub/deeper/b.tmp", 50);
        var root = Path.Combine(tree.Root, "cache");
        var engine = new CleanupEngine();

        var result = engine.Clean([engine.Scan(Target(new CleanupSource(root)), Ct)], null, Ct);

        Assert.Equal(150, result.FreedBytes);
        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(0, result.SkippedFiles);
        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void Clean_ReadOnlyFile_IsDeleted()
    {
        using var tree = new TempTree();
        var path = tree.File("cache/ro.tmp", 10);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var engine = new CleanupEngine();

        var result = engine.Clean(
            [engine.Scan(Target(new CleanupSource(Path.Combine(tree.Root, "cache"))), Ct)],
            null,
            Ct
        );

        Assert.Equal(1, result.DeletedFiles);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Clean_FileInUse_IsSkippedNotThrown()
    {
        using var tree = new TempTree();
        var locked = tree.File("cache/locked.tmp", 10);
        tree.File("cache/free.tmp", 20);
        var engine = new CleanupEngine();
        var scan = engine.Scan(Target(new CleanupSource(Path.Combine(tree.Root, "cache"))), Ct);

        CleanupResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            result = engine.Clean([scan], null, Ct);

        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.Equal(20, result.FreedBytes);
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void Scan_LinkInsideCache_IsNotFollowed()
    {
        using var tree = new TempTree();
        var precious = tree.File("documents/precious.docx", 999);
        tree.File("cache/a.tmp", 1);
        try
        {
            Directory.CreateSymbolicLink(
                Path.Combine(tree.Root, "cache", "link"),
                Path.Combine(tree.Root, "documents")
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link needs Developer Mode or an elevated shell.");
        }

        var engine = new CleanupEngine();
        var scan = engine.Scan(Target(new CleanupSource(Path.Combine(tree.Root, "cache"))), Ct);
        engine.Clean([scan], null, Ct);

        Assert.Equal(1, scan.Count);
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void ResolveFolders_UnknownVariable_ResolvesToNothing()
    {
        Assert.Empty(CleanupEngine.ResolveFolders(@"%TREVUNIX_NOT_SET_42%\Cache"));
    }

    [Fact]
    public void Catalog_IdsAreUniqueAndPathsUseVariables()
    {
        var all = CleanupCatalog.All;

        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct().Count());
        Assert.All(
            all.SelectMany(t => t.Sources),
            s => Assert.StartsWith("%", s.Path, StringComparison.Ordinal)
        );
    }
}
