namespace Trevunix.Core.Cleanup;

public enum CleanupGroup
{
    System,
    Browsers,
    AppsAndGames,
    Privacy,
    RecycleBin,
}

/// <summary>
///     A folder whose files may be removed. <see cref="Path"/> may contain environment
///     variables and <c>*</c> in folder names (for browser profiles); <see cref="Pattern"/>
///     filters file names.
/// </summary>
public sealed record CleanupSource(
    string Path,
    string Pattern = "*",
    bool Recursive = true,
    TimeSpan? MinAge = null,
    bool RemoveEmptyFolders = true
);

/// <summary>
///     One line in the cleanup list, made of one or more folders. <see cref="RegistryKeys"/> are
///     history lists (such as the Run box) whose values are all removed; the keys stay.
/// </summary>
public sealed record CleanupTarget(
    string Id,
    CleanupGroup Group,
    bool SelectedByDefault,
    IReadOnlyList<CleanupSource> Sources,
    IReadOnlyList<string>? RegistryKeys = null
)
{
    /// <summary>The Recycle Bin is emptied through the shell, not by deleting files.</summary>
    public bool IsRecycleBin => Group == CleanupGroup.RecycleBin;
}

/// <summary>The built-in list. Only caches, temp files, logs and reports; never user documents.</summary>
public static class CleanupCatalog
{
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);
    private static readonly TimeSpan OneWeek = TimeSpan.FromDays(7);

    private const string ChromiumProfiles = "*";
    private const string ExplorerKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer";

    public static IReadOnlyList<CleanupTarget> All { get; } =
    [
        new("UserTemp", CleanupGroup.System, true, [new(@"%TEMP%", MinAge: OneDay)]),
        new("WindowsTemp", CleanupGroup.System, true, [new(@"%WINDIR%\Temp", MinAge: OneDay)]),
        new(
            "ErrorReports",
            CleanupGroup.System,
            true,
            [
                new(@"%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive"),
                new(@"%PROGRAMDATA%\Microsoft\Windows\WER\ReportQueue"),
                new(@"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive"),
                new(@"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportQueue"),
            ]
        ),
        new(
            "CrashDumps",
            CleanupGroup.System,
            true,
            [
                new(@"%WINDIR%\Minidump"),
                new(@"%WINDIR%", "MEMORY.DMP", Recursive: false, RemoveEmptyFolders: false),
                new(@"%LOCALAPPDATA%\CrashDumps"),
            ]
        ),
        new(
            "DeliveryOptimization",
            CleanupGroup.System,
            true,
            [
                new(
                    @"%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"
                ),
            ]
        ),
        new(
            "WindowsUpdate",
            CleanupGroup.System,
            false,
            [new(@"%WINDIR%\SoftwareDistribution\Download", MinAge: OneDay)]
        ),
        new(
            "ThumbnailCache",
            CleanupGroup.System,
            false,
            [
                new(
                    @"%LOCALAPPDATA%\Microsoft\Windows\Explorer",
                    "thumbcache_*.db",
                    Recursive: false,
                    RemoveEmptyFolders: false
                ),
            ]
        ),
        new(
            "OldLogs",
            CleanupGroup.System,
            false,
            [
                new(@"%WINDIR%\Logs", "*.log", MinAge: OneWeek, RemoveEmptyFolders: false),
                new(@"%WINDIR%\Logs", "*.etl", MinAge: OneWeek, RemoveEmptyFolders: false),
                new(@"%WINDIR%\Panther", "*.log", MinAge: OneWeek, RemoveEmptyFolders: false),
            ]
        ),
        new(
            "Chrome",
            CleanupGroup.Browsers,
            true,
            ChromiumCaches(@"%LOCALAPPDATA%\Google\Chrome\User Data")
        ),
        new(
            "Edge",
            CleanupGroup.Browsers,
            true,
            ChromiumCaches(@"%LOCALAPPDATA%\Microsoft\Edge\User Data")
        ),
        new(
            "Brave",
            CleanupGroup.Browsers,
            true,
            ChromiumCaches(@"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data")
        ),
        new(
            "Opera",
            CleanupGroup.Browsers,
            true,
            [
                new(@"%LOCALAPPDATA%\Opera Software\Opera Stable\Cache"),
                new(@"%LOCALAPPDATA%\Opera Software\Opera GX Stable\Cache"),
            ]
        ),
        new(
            "Firefox",
            CleanupGroup.Browsers,
            true,
            [
                new(@"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2"),
                new(@"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\startupCache"),
            ]
        ),
        new(
            "ShaderCache",
            CleanupGroup.AppsAndGames,
            false,
            [
                new(@"%LOCALAPPDATA%\D3DSCache"),
                new(@"%LOCALAPPDATA%\NVIDIA\DXCache"),
                new(@"%LOCALAPPDATA%\NVIDIA\GLCache"),
                new(@"%LOCALAPPDATA%\AMD\DxCache"),
                new(@"%LOCALAPPDATA%\AMD\GLCache"),
            ]
        ),
        new(
            "Discord",
            CleanupGroup.AppsAndGames,
            true,
            [
                new(@"%APPDATA%\discord\Cache"),
                new(@"%APPDATA%\discord\Code Cache"),
                new(@"%APPDATA%\discord\GPUCache"),
            ]
        ),
        new(
            "Teams",
            CleanupGroup.AppsAndGames,
            true,
            [
                new(
                    @"%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\Default\Cache"
                ),
            ]
        ),
        new(
            "VsCode",
            CleanupGroup.AppsAndGames,
            true,
            [
                new(@"%APPDATA%\Code\Cache"),
                new(@"%APPDATA%\Code\CachedData"),
                new(@"%APPDATA%\Code\Code Cache"),
                new(@"%APPDATA%\Code\GPUCache"),
                new(@"%APPDATA%\Code\CachedExtensionVSIXs"),
            ]
        ),
        new(
            "Steam",
            CleanupGroup.AppsAndGames,
            true,
            [new(@"%LOCALAPPDATA%\Steam\htmlcache\Cache")]
        ),
        new("Spotify", CleanupGroup.AppsAndGames, false, [new(@"%LOCALAPPDATA%\Spotify\Data")]),
        new(
            "DevCaches",
            CleanupGroup.AppsAndGames,
            false,
            [
                new(@"%LOCALAPPDATA%\npm-cache"),
                new(@"%LOCALAPPDATA%\NuGet\v3-cache"),
                new(@"%LOCALAPPDATA%\pip\Cache"),
                new(@"%LOCALAPPDATA%\Yarn\Cache"),
            ]
        ),
        new(
            "RecentFiles",
            CleanupGroup.Privacy,
            false,
            [
                new(
                    @"%APPDATA%\Microsoft\Windows\Recent",
                    "*.lnk",
                    Recursive: false,
                    RemoveEmptyFolders: false
                ),
            ]
        ),
        new(
            "JumpLists",
            CleanupGroup.Privacy,
            false,
            [
                new(
                    @"%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations",
                    RemoveEmptyFolders: false
                ),
                new(
                    @"%APPDATA%\Microsoft\Windows\Recent\CustomDestinations",
                    RemoveEmptyFolders: false
                ),
            ]
        ),
        new("RunHistory", CleanupGroup.Privacy, false, [], [$@"{ExplorerKey}\RunMRU"]),
        new(
            "ExplorerHistory",
            CleanupGroup.Privacy,
            false,
            [],
            [$@"{ExplorerKey}\TypedPaths", $@"{ExplorerKey}\WordWheelQuery"]
        ),
        new("RecycleBin", CleanupGroup.RecycleBin, false, []),
    ];

    private static IReadOnlyList<CleanupSource> ChromiumCaches(string userData) =>
        [
            new($@"{userData}\{ChromiumProfiles}\Cache"),
            new($@"{userData}\{ChromiumProfiles}\Code Cache"),
            new($@"{userData}\{ChromiumProfiles}\GPUCache"),
            new($@"{userData}\{ChromiumProfiles}\Service Worker\CacheStorage"),
            new($@"{userData}\ShaderCache"),
            new($@"{userData}\GrShaderCache"),
        ];
}
