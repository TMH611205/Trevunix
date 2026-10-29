using Trevunix.Core.DiskUsage;

namespace Trevunix.Core.Tests;

public class DiskUsageScannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Scan_NestedFolders_TotalsSizesAndCounts()
    {
        using var tree = new TempTree();
        tree.File("a.bin", 1000);
        tree.File("sub/b.txt", 300);
        tree.File("sub/deeper/c.txt", 200);
        tree.Folder("empty");

        var result = new DiskUsageScanner().Scan(tree.Root, null, Ct);

        Assert.Equal(1500, result.Root.Size);
        Assert.Equal(3, result.Root.FileCount);
        Assert.Equal(3, result.Root.FolderCount);
        Assert.False(result.Cancelled);

        var sub = result.Root.Children.Single(c => c.Name == "sub");
        Assert.Equal(500, sub.Size);
        Assert.Equal(1, sub.FolderCount);
    }

    [Fact]
    public void Scan_Children_AreSortedLargestFirst()
    {
        using var tree = new TempTree();
        tree.File("small.bin", 10);
        tree.File("big/x.bin", 5000);
        tree.File("medium.bin", 700);

        var result = new DiskUsageScanner().Scan(tree.Root, null, Ct);

        Assert.Equal(["big", "medium.bin", "small.bin"], result.Root.Children.Select(c => c.Name));
        Assert.Equal(5000d / 5710, result.Root.Children[0].ShareOfParent, 6);
    }

    [Fact]
    public void Scan_ManySmallFiles_FoldsTheRestIntoAGroup()
    {
        using var tree = new TempTree();
        for (var i = 0; i < 5; i++)
            tree.File($"f{i}.dat", 100 + i);

        var options = new DiskUsageScannerOptions { FilesPerFolder = 2 };
        var result = new DiskUsageScanner(options).Scan(tree.Root, null, Ct);

        var files = result.Root.Children.Where(c => c.Kind == UsageNodeKind.File).ToList();
        var group = Assert.Single(result.Root.Children, c => c.Kind == UsageNodeKind.Group);
        Assert.Equal(2, files.Count);
        Assert.Equal(3, group.FileCount);
        Assert.Equal(100 + 101 + 102, group.Size);
        Assert.Equal(510, result.Root.Size);
    }

    [Fact]
    public void Scan_LargestFiles_KeepsTopNAcrossFolders()
    {
        using var tree = new TempTree();
        tree.File("one/a.bin", 50);
        tree.File("two/b.bin", 900);
        tree.File("three/c.bin", 400);
        tree.File("d.bin", 10);

        var options = new DiskUsageScannerOptions { LargestFileCount = 2 };
        var result = new DiskUsageScanner(options).Scan(tree.Root, null, Ct);

        Assert.Equal(["b.bin", "c.bin"], result.LargestFiles.Select(f => f.Name));
    }

    [Fact]
    public void Scan_FileTypes_GroupsByExtensionCaseInsensitive()
    {
        using var tree = new TempTree();
        tree.File("a.MP4", 600);
        tree.File("x/b.mp4", 400);
        tree.File("c.txt", 5);
        tree.File("noext", 7);

        var result = new DiskUsageScanner().Scan(tree.Root, null, Ct);

        var mp4 = result.FileTypes[0];
        Assert.Equal(".mp4", mp4.Extension);
        Assert.Equal(1000, mp4.Size);
        Assert.Equal(2, mp4.Count);
        Assert.Contains(result.FileTypes, t => t.Extension == "" && t.Size == 7);
    }

    [Fact]
    public void Scan_Cancelled_ReturnsFlaggedResult()
    {
        using var tree = new TempTree();
        tree.File("a.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = new DiskUsageScanner().Scan(tree.Root, null, cts.Token);

        Assert.True(result.Cancelled);
    }

    [Fact]
    public void Scan_MissingFolder_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Assert.Throws<DirectoryNotFoundException>(() =>
            new DiskUsageScanner().Scan(missing, null, Ct)
        );
    }

    [Fact]
    public void Scan_SymbolicLink_IsNotFollowed()
    {
        using var tree = new TempTree();
        tree.File("real/data.bin", 1000);
        var link = Path.Combine(tree.Root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(tree.Root, "real"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link needs Developer Mode or an elevated shell.");
        }

        var result = new DiskUsageScanner().Scan(tree.Root, null, Ct);

        Assert.Equal(1000, result.Root.Size);
    }

    [Fact]
    public void RemoveFromTree_SubtractsFromAncestors()
    {
        using var tree = new TempTree();
        tree.File("keep.bin", 100);
        tree.File("sub/inner/gone.bin", 400);

        var result = new DiskUsageScanner().Scan(tree.Root, null, Ct);
        var sub = result.Root.Children.Single(c => c.Name == "sub");

        sub.RemoveFromTree();

        Assert.Equal(100, result.Root.Size);
        Assert.Equal(1, result.Root.FileCount);
        Assert.Equal(0, result.Root.FolderCount);
        Assert.DoesNotContain(sub, result.Root.Children);
    }

    [Fact]
    public void Scan_WithProgress_FinalReportHasTotals()
    {
        using var tree = new TempTree();
        tree.File("a/b.bin", 10);
        var reports = new List<ScanProgress>();

        new DiskUsageScanner().Scan(tree.Root, new SyncProgress<ScanProgress>(reports.Add), Ct);

        var last = reports[^1];
        Assert.Equal(1, last.Files);
        Assert.Equal(10, last.Bytes);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
