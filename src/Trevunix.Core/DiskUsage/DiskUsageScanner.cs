using System.Collections.Concurrent;
using System.Diagnostics;

namespace Trevunix.Core.DiskUsage;

public sealed class DiskUsageScannerOptions
{
    /// <summary>Files kept as their own rows per folder; the rest fold into one group row.</summary>
    public int FilesPerFolder { get; init; } = 100;

    /// <summary>Size of the global "largest files" list.</summary>
    public int LargestFileCount { get; init; } = 200;

    /// <summary>Folders down to this depth are scanned in parallel.</summary>
    public int ParallelDepth { get; init; } = 3;

    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(150);
}

/// <summary>
///     Walks a folder tree and totals sizes. Reparse points (junctions, symlinks, OneDrive
///     placeholders that point elsewhere) are not followed, so nothing is counted twice.
/// </summary>
public sealed class DiskUsageScanner(DiskUsageScannerOptions? options = null)
{
    private readonly DiskUsageScannerOptions _options = options ?? new();

    private static readonly EnumerationOptions Enumeration = new()
    {
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public Task<ScanResult> ScanAsync(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default
    ) => Task.Run(() => Scan(rootPath, progress, cancellationToken), CancellationToken.None);

    public ScanResult Scan(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        var full = Path.GetFullPath(rootPath);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException(full);

        var state = new ScanState(_options, progress, cancellationToken);
        var root = new UsageNode(DisplayName(full), full, UsageNodeKind.Directory, null);

        try
        {
            ScanDirectory(root, 0, state);
        }
        catch (OperationCanceledException) { }
        catch (AggregateException ex)
            when (ex.Flatten().InnerExceptions.All(e => e is OperationCanceledException)) { }

        state.Report(full, force: true);

        return new ScanResult
        {
            Root = root,
            LargestFiles = state.TakeLargest(),
            FileTypes = state
                .FileTypes.Select(kv => new FileTypeUsage(kv.Key, kv.Value.Size, kv.Value.Count))
                .OrderByDescending(t => t.Size)
                .ToList(),
            Elapsed = state.Elapsed,
            Cancelled = cancellationToken.IsCancellationRequested,
        };
    }

    private void ScanDirectory(UsageNode node, int depth, ScanState state)
    {
        state.Token.ThrowIfCancellationRequested();

        var info = new DirectoryInfo(node.FullPath);
        var subdirs = new List<UsageNode>();
        var files = new List<(string Path, string Name, long Size, DateTime Write)>();
        var types = new Dictionary<string, (long Size, long Count)>(
            StringComparer.OrdinalIgnoreCase
        );

        try
        {
            node.LastWriteUtc = info.LastWriteTimeUtc;
            foreach (var entry in info.EnumerateFileSystemInfos("*", Enumeration))
            {
                if (entry is DirectoryInfo dir)
                {
                    subdirs.Add(
                        new UsageNode(dir.Name, dir.FullName, UsageNodeKind.Directory, node)
                    );
                }
                else if (entry is FileInfo file)
                {
                    long fileSize;
                    try
                    {
                        fileSize = file.Length;
                    }
                    catch (IOException)
                    {
                        node.Incomplete = true;
                        continue;
                    }

                    files.Add((file.FullName, file.Name, fileSize, file.LastWriteTimeUtc));
                    var ext = file.Extension.Length > 0 ? file.Extension.ToLowerInvariant() : "";
                    types[ext] = types.TryGetValue(ext, out var t)
                        ? (t.Size + fileSize, t.Count + 1)
                        : (fileSize, 1);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            node.Incomplete = true;
        }

        if (depth < _options.ParallelDepth && subdirs.Count > 1)
        {
            Parallel.ForEach(
                subdirs,
                new ParallelOptions { CancellationToken = state.Token },
                child => ScanDirectory(child, depth + 1, state)
            );
        }
        else
        {
            foreach (var child in subdirs)
                ScanDirectory(child, depth + 1, state);
        }

        long size = 0;
        long fileCount = files.Count;
        long folderCount = subdirs.Count;

        foreach (var child in subdirs)
        {
            node.AddChild(child);
            size += child.Size;
            fileCount += child.FileCount;
            folderCount += child.FolderCount;
            node.Incomplete |= child.Incomplete;
        }

        files.Sort((a, b) => b.Size.CompareTo(a.Size));
        long restSize = 0;
        long restCount = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            size += f.Size;
            state.OfferLargeFile(f.Path, f.Size, f.Write);

            if (i < _options.FilesPerFolder)
            {
                node.AddChild(
                    new UsageNode(f.Name, f.Path, UsageNodeKind.File, node)
                    {
                        Size = f.Size,
                        FileCount = 1,
                        LastWriteUtc = f.Write,
                    }
                );
            }
            else
            {
                restSize += f.Size;
                restCount++;
            }
        }

        if (restCount > 0)
        {
            node.AddChild(
                new UsageNode(
                    restCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    node.FullPath,
                    UsageNodeKind.Group,
                    node
                )
                {
                    Size = restSize,
                    FileCount = restCount,
                }
            );
        }

        node.Size = size;
        node.FileCount = fileCount;
        node.FolderCount = folderCount;
        node.SortChildrenBySize();

        state.MergeTypes(types);
        state.Count(files.Count, files.Sum(f => f.Size), node.FullPath);
    }

    private static string DisplayName(string fullPath)
    {
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? fullPath : name;
    }

    private sealed class ScanState(
        DiskUsageScannerOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken token
    )
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Lock _largestLock = new();
        private readonly PriorityQueue<LargeFile, long> _largest = new();
        private long _files;
        private long _folders;
        private long _bytes;
        private long _lastReportTicks;

        public CancellationToken Token { get; } = token;

        public ConcurrentDictionary<string, (long Size, long Count)> FileTypes { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public TimeSpan Elapsed => _clock.Elapsed;

        public void OfferLargeFile(string path, long size, DateTime write)
        {
            lock (_largestLock)
            {
                if (_largest.Count < options.LargestFileCount)
                {
                    _largest.Enqueue(new LargeFile(path, size, write), size);
                }
                else if (_largest.TryPeek(out _, out var smallest) && size > smallest)
                {
                    _largest.EnqueueDequeue(new LargeFile(path, size, write), size);
                }
            }
        }

        public IReadOnlyList<LargeFile> TakeLargest()
        {
            lock (_largestLock)
            {
                return _largest
                    .UnorderedItems.Select(i => i.Element)
                    .OrderByDescending(f => f.Size)
                    .ToList();
            }
        }

        public void MergeTypes(Dictionary<string, (long Size, long Count)> local)
        {
            foreach (var (ext, value) in local)
            {
                FileTypes.AddOrUpdate(
                    ext,
                    value,
                    (_, old) => (old.Size + value.Size, old.Count + value.Count)
                );
            }
        }

        public void Count(long files, long bytes, string path)
        {
            Interlocked.Add(ref _files, files);
            Interlocked.Add(ref _bytes, bytes);
            Interlocked.Increment(ref _folders);
            Report(path, force: false);
        }

        public void Report(string path, bool force)
        {
            if (progress is null)
                return;

            var now = _clock.Elapsed.Ticks;
            var last = Interlocked.Read(ref _lastReportTicks);
            if (!force && now - last < options.ProgressInterval.Ticks)
                return;
            if (!force && Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last)
                return;

            progress.Report(
                new ScanProgress(
                    Interlocked.Read(ref _files),
                    Interlocked.Read(ref _folders),
                    Interlocked.Read(ref _bytes),
                    path
                )
            );
        }
    }
}
