using Trevunix.Core.Files;
using Trevunix.Core.Maintenance;

namespace Trevunix.Core.Tests;

public class DuplicatesAndHistoryTests
{
    private static void Write(TempTree tree, string relative, string content)
    {
        var path = Path.Combine(tree.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task FindAsync_GroupsIdenticalFilesOnly()
    {
        using var tree = new TempTree();
        var big = new string('x', 100_000);
        Write(tree, "a/one.txt", big);
        Write(tree, "b/two.txt", big);
        Write(tree, "c/three.txt", big);
        // Same size, same first 64 KB, different end.
        Write(tree, "d/near.txt", big[..^1] + "y");
        Write(tree, "e/small1.txt", "hello world!");
        Write(tree, "e/small2.txt", "hello world!");
        Write(tree, "e/other.txt", "hello there!");

        var groups = await DuplicateFinder.FindAsync(
            tree.Root,
            minBytes: 1,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(2, groups.Count);
        Assert.Equal(3, groups[0].Paths.Count);
        Assert.Equal(200_000, groups[0].WastedBytes);
        Assert.All(groups[0].Paths, p => Assert.DoesNotContain("near", p));
        Assert.Equal(["small1.txt", "small2.txt"], groups[1].Paths.Select(Path.GetFileName));
    }

    [Fact]
    public async Task FindAsync_SkipsFilesUnderMinimum()
    {
        using var tree = new TempTree();
        Write(tree, "x.txt", "same");
        Write(tree, "y.txt", "same");

        Assert.Empty(
            await DuplicateFinder.FindAsync(
                tree.Root,
                minBytes: 1024,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public void IsSystemPath_CoversWindowsButNotUserFolders()
    {
        Assert.True(
            DuplicateFinder.IsSystemPath(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32"
                )
            )
        );
        Assert.False(DuplicateFinder.IsSystemPath(Path.GetTempPath()));
    }

    [Fact]
    public void ActivityLog_ReadsNewestFirstAndSkipsBadLines()
    {
        using var tree = new TempTree();
        var log = new ActivityLog(Path.Combine(tree.Root, "h.jsonl"));

        log.Add(HistoryKind.Cleanup, 100);
        File.AppendAllText(log.FilePath, "not json" + Environment.NewLine);
        log.Add(HistoryKind.TweakApplied, detail: "Telemetry");

        var entries = log.Read();
        Assert.Equal(2, entries.Count);
        Assert.Equal(HistoryKind.TweakApplied, entries[0].Kind);
        Assert.Equal("Telemetry", entries[0].Detail);
        Assert.Equal(100, entries[1].FreedBytes);
    }

    [Fact]
    public void ActivityLog_TrimsToKeep()
    {
        using var tree = new TempTree();
        var log = new ActivityLog(Path.Combine(tree.Root, "h.jsonl"));
        for (var i = 0; i <= ActivityLog.Keep * 2; i++)
            log.Add(HistoryKind.Cleanup, i);

        Assert.Equal(ActivityLog.Keep, File.ReadAllLines(log.FilePath).Length);
        Assert.Equal(ActivityLog.Keep * 2, log.Read()[0].FreedBytes);
    }
}
