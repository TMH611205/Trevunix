using System.Xml.Linq;
using Microsoft.Win32;
using Trevunix.Core.Cleanup;

namespace Trevunix.Core.Apps;

/// <summary>
///     The places outside its folders where a program hooks into Windows: shortcuts, services,
///     scheduled tasks and startup entries. All of them are matched by the file they run, which
///     must sit inside one of the program's own folders, never by name alone.
/// </summary>
internal static class SystemTraces
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    private static readonly (string Prefix, RegistryHive Hive, string Key)[] RunKeys =
    [
        ("HKCU", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        ("HKCU", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        ("HKLM", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        ("HKLM", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        (
            "HKLM",
            RegistryHive.LocalMachine,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"
        ),
    ];

    public static IEnumerable<Leftover> Find(IReadOnlyList<string> folders, HashSet<string> names)
    {
        if (folders.Count == 0 && names.Count == 0)
            return [];
        return Shortcuts(folders, names)
            .Concat(Services(folders))
            .Concat(Tasks(folders))
            .Concat(StartupEntries(folders))
            .ToList();
    }

    /// <summary>The exe a command line starts: quoted, or up to the first ".exe".</summary>
    internal static string? ExeOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        // Driver and service paths are often written as \??\C:\... or relative to the system.
        if (text.StartsWith(@"\??\", StringComparison.Ordinal))
            text = text[4..];
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        // Unquoted paths may contain spaces; the file ends at its extension.
        var ends = Runnable
            .Select(ext =>
                text.IndexOf(ext, StringComparison.OrdinalIgnoreCase) is var i and >= 0
                    ? i + ext.Length
                    : -1
            )
            .Where(i => i > 0 && (i == text.Length || text[i] is ' ' or '"' or ','))
            .ToList();
        return ends.Count > 0 ? text[..ends.Min()] : text.Split(' ')[0];
    }

    private static readonly string[] Runnable =
    [
        ".exe",
        ".sys",
        ".dll",
        ".cmd",
        ".bat",
        ".com",
        ".ps1",
        ".vbs",
        ".js",
    ];

    internal static bool IsInside(string? path, IReadOnlyList<string> folders) =>
        path is not null
        && Path.IsPathRooted(path)
        && folders.Any(f => CleanupSafety.IsSameOrInside(path, f));

    private static IEnumerable<Leftover> Shortcuts(
        IReadOnlyList<string> folders,
        HashSet<string> names
    )
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var places = new (string Folder, bool Deep)[]
        {
            (F(Environment.SpecialFolder.Programs), true),
            (F(Environment.SpecialFolder.CommonPrograms), true),
            (F(Environment.SpecialFolder.DesktopDirectory), false),
            (F(Environment.SpecialFolder.CommonDesktopDirectory), false),
        };

        foreach (var (folder, deep) in places)
        {
            foreach (var link in Links(folder, deep))
            {
                var target = ShortcutFile.Target(link);
                // It starts something in the program's folders, or it is a broken shortcut
                // carrying the program's name.
                var ours =
                    IsInside(target, folders)
                    || (
                        target is not null
                        && !File.Exists(target)
                        && !Directory.Exists(target)
                        && names.Contains(
                            Leftovers.Normalize(Path.GetFileNameWithoutExtension(link))
                        )
                    );
                if (ours)
                    yield return new Leftover(LeftoverKind.Shortcut, link, 0, Sure: true, target);
            }
        }
    }

    private static IEnumerable<Leftover> Services(IReadOnlyList<string> folders)
    {
        var found = new List<Leftover>();
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(ServicesKey);
            foreach (var name in services?.GetSubKeyNames() ?? [])
            {
                using var service = services!.OpenSubKey(name);
                if (IsInside(ExeOf(service?.GetValue("ImagePath") as string), folders))
                    found.Add(
                        new Leftover(
                            LeftoverKind.Service,
                            name,
                            0,
                            Sure: true,
                            service?.GetValue("DisplayName") as string
                        )
                    );
            }
        }
        catch (Exception ex)
            when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        return found;
    }

    /// <summary>Task definitions are XML files under System32\Tasks; read them directly.</summary>
    private static IEnumerable<Leftover> Tasks(IReadOnlyList<string> folders)
    {
        var root = Path.Combine(Environment.SystemDirectory, "Tasks");
        var found = new List<Leftover>();
        IEnumerable<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(
                    root,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                    }
                )
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return found;
        }

        foreach (var file in files)
        {
            try
            {
                var doc = XDocument.Load(file);
                var commands = doc.Descendants()
                    .Where(e => e.Name.LocalName == "Command")
                    .Select(e => ExeOf(e.Value));
                if (commands.Any(c => IsInside(c, folders)))
                    found.Add(
                        new Leftover(
                            LeftoverKind.ScheduledTask,
                            file[root.Length..],
                            0,
                            Sure: true,
                            file
                        )
                    );
            }
            catch (Exception ex)
                when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            { }
        }
        return found;
    }

    private static IEnumerable<Leftover> StartupEntries(IReadOnlyList<string> folders)
    {
        var found = new List<Leftover>();
        foreach (var (prefix, hive, path) in RunKeys)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(path);
                foreach (var name in key?.GetValueNames() ?? [])
                {
                    if (IsInside(ExeOf(key!.GetValue(name) as string), folders))
                        found.Add(
                            new Leftover(
                                LeftoverKind.StartupEntry,
                                $@"{prefix}\{path}",
                                0,
                                Sure: true,
                                name
                            )
                        );
                }
            }
            catch (Exception ex)
                when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        }
        return found;
    }

    private static IEnumerable<string> Links(string folder, bool deep)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory
                    .EnumerateFiles(
                        folder,
                        "*.lnk",
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = deep,
                            IgnoreInaccessible = true,
                            AttributesToSkip = FileAttributes.ReparsePoint,
                        }
                    )
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
