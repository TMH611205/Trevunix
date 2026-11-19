using Trevunix.Core.Settings;

namespace Trevunix.Core.Common;

/// <summary>
///     Appends unexpected errors to <c>errors.log</c>. When the file passes
///     <see cref="MaxBytes"/> it becomes <c>errors.old.log</c>, so at most two files are kept.
/// </summary>
public static class ErrorLog
{
    public const long MaxBytes = 1024 * 1024;

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "errors.log");

    /// <summary>Never throws: a failure to log must not turn into a second error.</summary>
    public static void Write(Exception exception, string? path = null, long maxBytes = MaxBytes)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new FileInfo(path);
            if (file.Exists && file.Length > maxBytes)
                File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);

            File.AppendAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}"
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
