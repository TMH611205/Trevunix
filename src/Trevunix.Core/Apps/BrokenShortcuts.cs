namespace Trevunix.Core.Apps;

public enum ShortcutProblem
{
    MissingTarget,
    EmptyFolder,
}

/// <param name="Target">The missing file a broken shortcut points to; empty for folders.</param>
public sealed record ShortcutIssue(string Path, ShortcutProblem Problem, string Target);

/// <summary>
///     Start menu and desktop shortcuts whose program is gone, and Start menu folders left empty
///     by uninstallers. Targets on drives that are not connected right now are not reported.
/// </summary>
public static class BrokenShortcuts
{
    public static IReadOnlyList<string> DefaultFolders { get; } =
    [
        .. new[]
        {
            Environment.SpecialFolder.Programs,
            Environment.SpecialFolder.CommonPrograms,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.CommonDesktopDirectory,
        }
            .Select(Environment.GetFolderPath)
            .Where(p => p.Length > 0),
    ];

    public static IReadOnlyList<ShortcutIssue> Find(IEnumerable<string>? folders = null)
    {
        var issues = new List<ShortcutIssue>();
        foreach (var folder in folders ?? DefaultFolders)
        {
            if (!Directory.Exists(folder))
                continue;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var link in Directory.EnumerateFiles(folder, "*.lnk", options))
            {
                if (ShortcutFile.Target(link) is { } target && IsMissing(target))
                    issues.Add(new ShortcutIssue(link, ShortcutProblem.MissingTarget, target));
            }

            // Desktops are left alone: an empty folder there is usually on purpose.
            if (folder.Contains("Start Menu", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var dir in Directory.EnumerateDirectories(folder, "*", options))
                {
                    var insideReported = issues.Any(i =>
                        i.Problem == ShortcutProblem.EmptyFolder
                        && dir.StartsWith(i.Path + "\\", StringComparison.OrdinalIgnoreCase)
                    );
                    if (!insideReported && IsEmpty(dir))
                        issues.Add(new ShortcutIssue(dir, ShortcutProblem.EmptyFolder, ""));
                }
            }
        }
        return issues;
    }

    /// <summary>A local path whose drive is present but whose file is not.</summary>
    internal static bool IsMissing(string target)
    {
        if (target.Length < 3 || target[1] != ':' || target.Contains("::"))
            return false;
        var root = Path.GetPathRoot(target);
        return root is not null
            && Directory.Exists(root)
            && !File.Exists(target)
            && !Directory.Exists(target);
    }

    private static bool IsEmpty(string dir)
    {
        try
        {
            return !Directory
                .EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Any(f =>
                    !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
