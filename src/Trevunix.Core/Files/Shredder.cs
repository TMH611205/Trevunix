using System.Security.Cryptography;

namespace Trevunix.Core.Files;

public sealed record ShredResult(int Shredded, int Failed);

/// <summary>
///     Deletes files so ordinary recovery tools cannot bring them back: the content is overwritten
///     with random bytes, the name is replaced, then the file is deleted. On SSDs the drive may
///     keep old copies of blocks internally, so this is best effort there.
/// </summary>
public static class Shredder
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>Paths that must never be shredded: drive roots, Windows and program folders.</summary>
    public static bool IsRefused(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        return Path.GetPathRoot(full)?.TrimEnd('\\') == full || DuplicateFinder.IsSystemPath(full);
    }

    /// <summary>Shreds files and whole folders. Refused paths count as failed.</summary>
    public static ShredResult Shred(IEnumerable<string> paths, CancellationToken ct = default)
    {
        int ok = 0,
            failed = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (IsRefused(path))
            {
                failed++;
                continue;
            }

            if (Directory.Exists(path))
            {
                foreach (
                    var file in Directory.EnumerateFiles(
                        path,
                        "*",
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            IgnoreInaccessible = true,
                            AttributesToSkip = FileAttributes.ReparsePoint,
                        }
                    )
                )
                {
                    ct.ThrowIfCancellationRequested();
                    if (ShredFile(file))
                        ok++;
                    else
                        failed++;
                }
                try
                {
                    Directory.Delete(path, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            else if (File.Exists(path))
            {
                if (ShredFile(path))
                    ok++;
                else
                    failed++;
            }
        }
        return new ShredResult(ok, failed);
    }

    internal static bool ShredFile(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            using (
                var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.WriteThrough
                )
            )
            {
                var buffer = new byte[BufferSize];
                for (long left = stream.Length; left > 0; left -= buffer.Length)
                {
                    RandomNumberGenerator.Fill(buffer);
                    stream.Write(buffer, 0, (int)Math.Min(buffer.Length, left));
                }
                stream.Flush(flushToDisk: true);
            }

            var renamed = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N"));
            File.Move(path, renamed);
            File.Delete(renamed);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
