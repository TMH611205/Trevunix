using Trevunix.Core.Cleanup;

namespace Trevunix.Core.Tests;

public class CleanupSafetyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CleanupSafety SafetyFor(TempTree tree) =>
        new(
            neverTouch: [Path.Combine(tree.Root, "Profile"), Path.Combine(tree.Root, "Windows")],
            personalFolders:
            [
                Path.Combine(tree.Root, "Profile", "Desktop"),
                Path.Combine(tree.Root, "Profile", "Documents"),
                Path.Combine(tree.Root, "D", "Downloads"),
            ]
        );

    [Fact]
    public void IsSafeRoot_CacheInsideProfile_IsAllowed()
    {
        using var tree = new TempTree();
        var safety = SafetyFor(tree);

        Assert.True(safety.IsSafeRoot(Path.Combine(tree.Root, "Profile", "AppData", "Temp")));
        Assert.True(safety.IsSafeRoot(Path.Combine(tree.Root, "Windows", "Temp")));
    }

    [Fact]
    public void IsSafeRoot_FolderThatContainsTheProfile_IsRefused()
    {
        using var tree = new TempTree();
        var safety = SafetyFor(tree);

        // e.g. %TEMP% pointed at C:\Users or the profile itself.
        Assert.False(safety.IsSafeRoot(tree.Root));
        Assert.False(safety.IsSafeRoot(Path.Combine(tree.Root, "Profile")));
        Assert.False(safety.IsSafeRoot(Path.Combine(tree.Root, "Windows")));
    }

    [Fact]
    public void IsSafeRoot_InsidePersonalFolder_IsRefused()
    {
        using var tree = new TempTree();
        var safety = SafetyFor(tree);

        // e.g. %TEMP% set to Desktop\temp or a folder in Downloads.
        Assert.False(safety.IsSafeRoot(Path.Combine(tree.Root, "Profile", "Desktop", "temp")));
        Assert.False(safety.IsSafeRoot(Path.Combine(tree.Root, "D", "Downloads", "cache")));
        Assert.False(safety.IsSafeRoot(Path.Combine(tree.Root, "Profile", "Documents")));
    }

    [Fact]
    public void IsSafeRoot_DriveRoot_IsRefused()
    {
        var safety = new CleanupSafety([], []);

        Assert.False(safety.IsSafeRoot(@"C:\"));
        Assert.False(safety.IsSafeRoot(@"D:\"));
    }

    [Fact]
    public void IsSafeRoot_SingleFileInSystemFolder_IsAllowedButNotInPersonal()
    {
        using var tree = new TempTree();
        var safety = SafetyFor(tree);

        Assert.True(safety.IsSafeRoot(Path.Combine(tree.Root, "Windows"), singleFile: true));
        Assert.False(
            safety.IsSafeRoot(Path.Combine(tree.Root, "Profile", "Desktop"), singleFile: true)
        );
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".PDF")]
    [InlineData(".jpg")]
    [InlineData(".mp4")]
    [InlineData(".zip")]
    [InlineData(".kdbx")]
    [InlineData(".pst")]
    public void IsProtectedExtension_PersonalTypes_AreKept(string extension)
    {
        Assert.True(CleanupSafety.IsProtectedExtension(extension));
    }

    [Theory]
    [InlineData(".tmp")]
    [InlineData(".log")]
    [InlineData(".dmp")]
    [InlineData("")]
    public void IsProtectedExtension_CacheTypes_AreNotKept(string extension)
    {
        Assert.False(CleanupSafety.IsProtectedExtension(extension));
    }

    [Fact]
    public void Scan_TempWithAUserDocument_KeepsTheDocument()
    {
        using var tree = new TempTree();
        var report = tree.File(@"temp\Bao cao quy 3.docx", 500);
        var photo = tree.File(@"temp\Zalo\anh.jpg", 300);
        tree.File(@"temp\junk.tmp", 100);
        var engine = new CleanupEngine(safety: new CleanupSafety([], []));
        var target = new CleanupTarget(
            "Test",
            CleanupGroup.System,
            true,
            [new CleanupSource(Path.Combine(tree.Root, "temp"))]
        );

        var scan = engine.Scan(target, Ct);
        engine.Clean([scan], null, Ct);

        Assert.Equal(1, scan.Count);
        Assert.Equal(2, scan.KeptFiles);
        Assert.True(File.Exists(report));
        Assert.True(File.Exists(photo));
    }

    [Fact]
    public void Scan_SystemAttributeFile_IsKept()
    {
        using var tree = new TempTree();
        var sys = tree.File(@"cache\important.bin", 10);
        File.SetAttributes(sys, FileAttributes.System);
        try
        {
            var engine = new CleanupEngine(safety: new CleanupSafety([], []));
            var target = new CleanupTarget(
                "Test",
                CleanupGroup.System,
                true,
                [new CleanupSource(Path.Combine(tree.Root, "cache"))]
            );

            var scan = engine.Scan(target, Ct);

            Assert.Equal(0, scan.Count);
            Assert.Equal(1, scan.KeptFiles);
        }
        finally
        {
            File.SetAttributes(sys, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Scan_UnsafeRoot_FindsNothing()
    {
        using var tree = new TempTree();
        tree.File(@"Profile\Desktop\temp\work.tmp", 10);
        var engine = new CleanupEngine(safety: SafetyFor(tree));
        var target = new CleanupTarget(
            "Test",
            CleanupGroup.System,
            true,
            [new CleanupSource(Path.Combine(tree.Root, "Profile", "Desktop", "temp"))]
        );

        Assert.Equal(0, engine.Scan(target, Ct).Count);
    }

    [Fact]
    public void Catalog_EveryRealRoot_PassesTheDefaultSafety()
    {
        var safety = new CleanupSafety();
        foreach (var source in CleanupCatalog.All.SelectMany(t => t.Sources))
        {
            var singleFile = !source.Recursive && !source.Pattern.Contains('*');
            foreach (var root in CleanupEngine.ResolveFolders(source.Path))
                Assert.True(safety.IsSafeRoot(root, singleFile), root);
        }
    }
}
