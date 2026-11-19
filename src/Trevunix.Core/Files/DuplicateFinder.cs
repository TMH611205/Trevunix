using System.Security.Cryptography;

namespace Trevunix.Core.Files;

/// <summary>Files with identical content. <see cref="Paths"/> is sorted, so the first is the oldest-looking name.</summary>
public sealed record DuplicateGroup(long Size, IReadOnlyList<string> Paths)
{
    /// <summary>Space freed by keeping one copy.</summary>
    public long WastedBytes => Size * (Paths.Count - 1);
}

/// <summary>
///     Finds duplicate files: same size first, then the same hash of the first 64 KB, then the
///     same SHA-256 of the whole file. Only files that survive each step are read further.
/// </summary>
public static class DuplicateFinder
{
    private const int HeadBytes = 64 * 1024;

    private static readonly string[] SystemRoots =
    [
        .. new[]
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
        }
            .Select(Environment.GetFolderPath)
            .Where(p => p.Length > 0),
    ];

    /// <summary>Windows and installed programs: identical files there are on purpose, so they are never listed.</summary>
    public static bool IsSystemPath(string path) =>
        SystemRoots.Any(r => Cleanup.CleanupSafety.IsSameOrInside(path, r));

    public static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(
        string root,
        long minBytes = 1024,
        IProgress<long>? filesSeen = null,
        CancellationToken cancellationToken = default
    ) =>
        await Task.Run(() => Find(root, minBytes, filesSeen, cancellationToken), cancellationToken);

    private static List<DuplicateGroup> Find(
        string root,
        long minBytes,
        IProgress<long>? filesSeen,
        CancellationToken ct
    )
    {
        var bySize = new Dictionary<long, List<string>>();
        long seen = 0;
        foreach (var file in Walk(root))
        {
            ct.ThrowIfCancellationRequested();
            if (++seen % 500 == 0)
                filesSeen?.Report(seen);

            long length;
            try
            {
                length = file.Length;
            }
            catch (IOException)
            {
                continue;
            }
            if (length < minBytes)
                continue;
            if (!bySize.TryGetValue(length, out var list))
                bySize[length] = list = [];
            list.Add(file.FullName);
        }
        filesSeen?.Report(seen);

        var groups = new List<DuplicateGroup>();
        foreach (var (size, sameSize) in bySize.Where(p => p.Value.Count > 1))
        {
            foreach (var sameHead in SplitBy(sameSize, p => Hash(p, HeadBytes, ct)))
            {
                var sameContent =
                    size <= HeadBytes ? [sameHead] : SplitBy(sameHead, p => Hash(p, null, ct));
                groups.AddRange(sameContent.Select(g => new DuplicateGroup(size, [.. g.Order()])));
            }
        }

        return [.. groups.OrderByDescending(g => g.WastedBytes)];
    }

    /// <summary>Every file under <paramref name="root"/>, without entering system folders or links.</summary>
    private static IEnumerable<FileInfo> Walk(string root)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            if (IsSystemPath(dir.FullName))
                continue;

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo sub)
                    pending.Push(sub);
                else if (entry is FileInfo file)
                    yield return file;
            }
        }
    }

    /// <summary>Groups of two or more paths with the same key; unreadable files (null key) drop out.</summary>
    private static List<List<string>> SplitBy(List<string> paths, Func<string, string?> key) =>
        paths
            .Select(p => (Path: p, Key: key(p)))
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Key)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(x => x.Path).ToList())
            .ToList();

    private static string? Hash(string path, int? limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.SequentialScan
            );
            if (limit is null)
                return Convert.ToHexString(SHA256.HashData(stream));

            var buffer = new byte[limit.Value];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
