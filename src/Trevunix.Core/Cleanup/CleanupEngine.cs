using Trevunix.Core.Files;

namespace Trevunix.Core.Cleanup;

public sealed record FoundFile(string Path, long Size);

/// <summary>What a scan found for one target. Cleaning deletes exactly these files.</summary>
public sealed class TargetScan
{
    internal TargetScan(
        CleanupTarget target,
        List<FoundFile> files,
        List<string> roots,
        long keptFiles = 0,
        int registryValues = 0
    )
    {
        Target = target;
        Files = files;
        Roots = roots;
        KeptFiles = keptFiles;
        RegistryValues = registryValues;
        Size = files.Sum(f => f.Size);
        Count = files.Count + registryValues;
    }

    /// <summary>History entries found under the target's registry keys.</summary>
    public int RegistryValues { get; }

    internal TargetScan(CleanupTarget target, long size, long count)
    {
        Target = target;
        Files = [];
        Roots = [];
        Size = size;
        Count = count;
    }

    public CleanupTarget Target { get; }
    public IReadOnlyList<FoundFile> Files { get; }
    public IReadOnlyList<string> Roots { get; }
    public long Size { get; }
    public long Count { get; }

    /// <summary>Files found in the folders but kept because they look personal or system-owned.</summary>
    public long KeptFiles { get; }
}

public sealed record CleanupProgress(string TargetId, long Done, long Total);

public sealed record CleanupResult(long FreedBytes, long DeletedFiles, long SkippedFiles);

/// <summary>
///     Finds and deletes cleanup files. Files in use or protected are skipped, not an error.
///     Reparse points are never followed, so a link inside a cache cannot lead outside it.
/// </summary>
public sealed class CleanupEngine(Func<DateTime>? utcNow = null, CleanupSafety? safety = null)
{
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private readonly CleanupSafety _safety = safety ?? new CleanupSafety();

    /// <summary>Deleting is mostly waiting on the file system, so a few threads overlap well.</summary>
    private static readonly int DeleteThreads = Math.Clamp(Environment.ProcessorCount, 2, 8);

    private static readonly EnumerationOptions TopLevel = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        RecurseSubdirectories = false,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    private static readonly EnumerationOptions Deep = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        RecurseSubdirectories = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public Task<IReadOnlyList<TargetScan>> ScanAsync(
        IEnumerable<CleanupTarget> targets,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run<IReadOnlyList<TargetScan>>(
            () =>
                targets
                    .AsParallel()
                    .AsOrdered()
                    .WithCancellation(cancellationToken)
                    .Select(t => Scan(t, cancellationToken))
                    .ToList(),
            cancellationToken
        );

    public TargetScan Scan(CleanupTarget target, CancellationToken cancellationToken = default)
    {
        if (target.IsRecycleBin)
        {
            var (size, count) = RecycleBin.Query();
            return new TargetScan(target, size, count);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FoundFile>();
        var roots = new List<string>();
        var now = _utcNow();
        long kept = 0;

        foreach (var source in target.Sources)
        {
            var singleFile =
                !source.Recursive && !source.Pattern.Contains('*') && !source.Pattern.Contains('?');
            foreach (var root in ResolveFolders(source.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_safety.IsSafeRoot(root, singleFile))
                    continue;
                if (source.RemoveEmptyFolders)
                    roots.Add(root);

                IEnumerable<FileInfo> found;
                try
                {
                    found = new DirectoryInfo(root).EnumerateFiles(
                        source.Pattern,
                        source.Recursive ? Deep : TopLevel
                    );
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var inside = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                foreach (var file in SafeEnumerate(found))
                {
                    // Belt and braces: never leave the folder we were asked to clean.
                    if (!file.FullName.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (_safety.IsProtectedFile(file))
                    {
                        kept++;
                        continue;
                    }
                    if (source.MinAge is { } age && now - file.LastWriteTimeUtc < age)
                        continue;
                    if (!seen.Add(file.FullName))
                        continue;

                    try
                    {
                        files.Add(new FoundFile(file.FullName, file.Length));
                    }
                    catch (IOException) { }
                }
            }
        }

        var values = target.RegistryKeys?.Sum(RegistryTraces.Count) ?? 0;
        return new TargetScan(target, files, roots, kept, values);
    }

    public Task<CleanupResult> CleanAsync(
        IEnumerable<TargetScan> scans,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default
    ) => Task.Run(() => Clean(scans.ToList(), progress, cancellationToken), CancellationToken.None);

    public CleanupResult Clean(
        IReadOnlyList<TargetScan> scans,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        long freed = 0;
        long deleted = 0;
        long skipped = 0;

        foreach (var scan in scans)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (scan.Target.IsRecycleBin)
            {
                if (scan.Count > 0 && RecycleBin.Empty())
                {
                    freed += scan.Size;
                    deleted += scan.Count;
                }
                progress?.Report(new CleanupProgress(scan.Target.Id, 1, 1));
                continue;
            }

            var done = 0L;
            try
            {
                Parallel.ForEach(
                    scan.Files,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = DeleteThreads,
                    },
                    file =>
                    {
                        if (TryDelete(file.Path))
                        {
                            Interlocked.Add(ref freed, file.Size);
                            Interlocked.Increment(ref deleted);
                        }
                        else
                        {
                            Interlocked.Increment(ref skipped);
                        }

                        var count = Interlocked.Increment(ref done);
                        if (count % 200 == 0)
                            progress?.Report(
                                new CleanupProgress(scan.Target.Id, count, scan.Files.Count)
                            );
                    }
                );
            }
            catch (OperationCanceledException)
            {
                break;
            }

            foreach (var root in scan.Roots)
                RemoveEmptyFolders(root);
            if (scan.RegistryValues > 0)
                deleted += scan.Target.RegistryKeys!.Sum(RegistryTraces.Clear);

            progress?.Report(new CleanupProgress(scan.Target.Id, done, scan.Files.Count));
        }

        return new CleanupResult(freed, deleted, skipped);
    }

    /// <summary>Expands variables and <c>*</c> folder segments into existing folders.</summary>
    internal static IEnumerable<string> ResolveFolders(string pattern)
    {
        var expanded = Environment.ExpandEnvironmentVariables(pattern);
        if (expanded.Contains('%'))
            return [];

        var parts = expanded.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return [];

        IEnumerable<string> current = [parts[0] + "\\"];
        foreach (var part in parts.Skip(1))
        {
            current = part.Contains('*')
                ? current.SelectMany(dir => MatchFolders(dir, part))
                : current.Select(dir => Path.Combine(dir, part));
        }

        return current.Where(Directory.Exists).Where(dir => !IsReparsePoint(dir)).ToList();
    }

    private static IEnumerable<string> MatchFolders(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir)
                ? new DirectoryInfo(dir)
                    .EnumerateDirectories(pattern, TopLevel)
                    .Select(d => d.FullName)
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsReparsePoint(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static IEnumerable<FileInfo> SafeEnumerate(IEnumerable<FileInfo> source)
    {
        using var e = source.GetEnumerator();
        while (true)
        {
            try
            {
                if (!e.MoveNext())
                    yield break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                yield break;
            }

            yield return e.Current;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            // File.Delete does not complain about a file that is already gone, so check first.
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Usually a read-only file; clear the flag and try once more.
            try
            {
                var info = new FileInfo(path);
                if (!info.IsReadOnly)
                    return false;
                info.IsReadOnly = false;
                info.Delete();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Deletes empty subfolders below <paramref name="root"/>; the root itself stays.</summary>
    private static void RemoveEmptyFolders(string root)
    {
        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(root, "*", Deep).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // Deepest first, so a parent is empty once its children are gone.
        foreach (var dir in dirs.OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
