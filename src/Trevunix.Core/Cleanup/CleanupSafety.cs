using Microsoft.Win32;

namespace Trevunix.Core.Cleanup;

/// <summary>
///     The last line of defence before anything is deleted. A folder is refused when it is,
///     contains, or sits inside a place where people keep their own files; a file is refused
///     when its type is one people create (documents, photos, videos, archives, keys, ...)
///     or Windows marks it as a system file.
/// </summary>
public sealed class CleanupSafety
{
    /// <summary>File types that are someone's work, never cache. Kept even inside Temp.</summary>
    private static readonly HashSet<string> PersonalExtensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // Documents
        ".doc",
        ".docx",
        ".docm",
        ".dot",
        ".dotx",
        ".odt",
        ".rtf",
        ".pages",
        ".xls",
        ".xlsx",
        ".xlsm",
        ".xlsb",
        ".ods",
        ".csv",
        ".numbers",
        ".ppt",
        ".pptx",
        ".pptm",
        ".odp",
        ".key",
        ".pdf",
        ".epub",
        ".md",
        ".tex",
        ".one",
        ".vsdx",
        ".pub",
        ".xps",
        // Images and design
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".bmp",
        ".tif",
        ".tiff",
        ".webp",
        ".heic",
        ".heif",
        ".raw",
        ".cr2",
        ".cr3",
        ".nef",
        ".arw",
        ".dng",
        ".svg",
        ".psd",
        ".ai",
        ".xcf",
        ".indd",
        ".sketch",
        ".fig",
        ".blend",
        ".dwg",
        ".dxf",
        ".skp",
        // Video and audio
        ".mp4",
        ".mkv",
        ".mov",
        ".avi",
        ".wmv",
        ".flv",
        ".webm",
        ".m4v",
        ".mts",
        ".mp3",
        ".wav",
        ".flac",
        ".aac",
        ".m4a",
        ".ogg",
        ".wma",
        ".aiff",
        // Archives and disk images
        ".zip",
        ".rar",
        ".7z",
        ".tar",
        ".gz",
        ".bz2",
        ".xz",
        ".iso",
        ".vhd",
        ".vhdx",
        // Mail, databases, keys and wallets
        ".pst",
        ".ost",
        ".eml",
        ".msg",
        ".mbox",
        ".db",
        ".sqlite",
        ".mdb",
        ".accdb",
        ".kdbx",
        ".kdb",
        ".pfx",
        ".p12",
        ".pem",
        ".ppk",
        ".gpg",
        ".asc",
        ".wallet",
        // Source and projects
        ".sln",
        ".slnx",
        ".csproj",
        ".cs",
        ".py",
        ".js",
        ".ts",
        ".java",
        ".cpp",
        ".c",
        ".h",
        ".go",
        ".rs",
        ".php",
        ".html",
        ".css",
        ".ipynb",
        // Game saves
        ".sav",
        ".save",
    };

    private readonly List<string> _neverTouch;
    private readonly List<string> _personalFolders;

    public CleanupSafety()
        : this(SystemFolders(), PersonalFolders()) { }

    /// <param name="neverTouch">Folders that must never be a cleanup root nor sit inside one.</param>
    /// <param name="personalFolders">Folders whose contents are the user's own files.</param>
    public CleanupSafety(IEnumerable<string> neverTouch, IEnumerable<string> personalFolders)
    {
        _personalFolders = Normalize(personalFolders);
        _neverTouch = Normalize(neverTouch).Concat(_personalFolders).Distinct().ToList();
    }

    /// <summary>
    ///     False when cleaning <paramref name="root"/> could reach personal or system files, for
    ///     example when %TEMP% has been pointed at a drive root, the user profile or the Desktop.
    /// </summary>
    /// <param name="singleFile">
    ///     True when only one named file in the folder is targeted (e.g. %WINDIR%\MEMORY.DMP);
    ///     then only personal folders are refused, since nothing else in the folder is touched.
    /// </param>
    public bool IsSafeRoot(string root, bool singleFile = false)
    {
        var dir = Normal(root);
        if (dir.Length == 0)
            return false;

        if (singleFile)
            return !_personalFolders.Any(p => IsSameOrInside(dir, p) || IsSameOrInside(p, dir));

        if (Path.GetPathRoot(dir)?.TrimEnd('\\') == dir)
            return false;

        foreach (var protectedDir in _neverTouch)
        {
            // The root is that folder, or contains it.
            if (IsSameOrInside(protectedDir, dir))
                return false;
        }

        foreach (var personal in _personalFolders)
        {
            // The root lives inside someone's Documents, Desktop, OneDrive, ...
            if (IsSameOrInside(dir, personal))
                return false;
        }

        return true;
    }

    /// <summary>True when the file must be kept whatever the cleanup target says.</summary>
    public bool IsProtectedFile(FileInfo file)
    {
        if (PersonalExtensions.Contains(file.Extension))
            return true;

        try
        {
            return file.Attributes.HasFlag(FileAttributes.System);
        }
        catch (IOException)
        {
            return true;
        }
    }

    internal static bool IsProtectedExtension(string extension) =>
        PersonalExtensions.Contains(extension);

    /// <summary>True when <paramref name="path"/> equals or is below <paramref name="folder"/>.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        var p = Normal(path);
        var f = Normal(folder);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\');
        }
        catch (Exception ex)
            when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    private static List<string> Normalize(IEnumerable<string> folders) =>
        folders
            .Select(Normal)
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IEnumerable<string> SystemFolders()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        return
        [
            F(Environment.SpecialFolder.UserProfile),
            F(Environment.SpecialFolder.ApplicationData),
            F(Environment.SpecialFolder.LocalApplicationData),
            F(Environment.SpecialFolder.CommonApplicationData),
            F(Environment.SpecialFolder.Windows),
            F(Environment.SpecialFolder.System),
            F(Environment.SpecialFolder.SystemX86),
            F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(F(Environment.SpecialFolder.Windows), "WinSxS"),
            Path.GetDirectoryName(F(Environment.SpecialFolder.UserProfile)) ?? "",
        ];
    }

    private static IEnumerable<string> PersonalFolders()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var folders = new List<string>
        {
            F(Environment.SpecialFolder.Desktop),
            F(Environment.SpecialFolder.MyDocuments),
            F(Environment.SpecialFolder.MyPictures),
            F(Environment.SpecialFolder.MyMusic),
            F(Environment.SpecialFolder.MyVideos),
            F(Environment.SpecialFolder.Favorites),
            F(Environment.SpecialFolder.CommonDesktopDirectory),
            F(Environment.SpecialFolder.CommonDocuments),
            Path.Combine(F(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
            Environment.GetEnvironmentVariable("OneDriveConsumer") ?? "",
            Environment.GetEnvironmentVariable("OneDriveCommercial") ?? "",
        };

        // The real Downloads folder when the user moved it (e.g. to D:\Downloads).
        try
        {
            using var shell = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"
            );
            if (shell?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string downloads)
                folders.Add(Environment.ExpandEnvironmentVariables(downloads));
        }
        catch (System.Security.SecurityException) { }

        return folders;
    }
}
