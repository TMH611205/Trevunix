using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using Trevunix.Core.Cleanup;
using Trevunix.Core.Files;
using Trevunix.Core.Settings;
using Trevunix.Core.Tools;

namespace Trevunix.Core.Apps;

public enum LeftoverKind
{
    Folder,
    Shortcut,
    Service,
    ScheduledTask,
    StartupEntry,
    RegistryKey,
}

/// <summary>Something an uninstalled program left behind.</summary>
/// <param name="Path">
///     A folder or shortcut path, a registry key such as <c>HKCU\Software\Vendor\App</c>, a
///     service name, or a task path such as <c>\Vendor\Updater</c>.
/// </param>
/// <param name="Sure">
///     True when the program pointed at it itself (its install folder or Uninstall entry, or
///     something that runs a file from its folders), false when it only matched by name and
///     deserves a look before removing.
/// </param>
/// <param name="Detail">
///     What else identifies it: a shortcut's target, a service's display name, a task's XML
///     file, or the value name of a startup entry under the key in <see cref="Path"/>.
/// </param>
public sealed record Leftover(
    LeftoverKind Kind,
    string Path,
    long SizeBytes,
    bool Sure,
    string? Detail = null
);

public sealed record LeftoverRemoval(int Removed, int Failed, string? RegistryBackup);

/// <summary>
///     Finds what a program leaves behind after its uninstaller ran: its install folder, data
///     folders in Program Files, ProgramData and AppData, and its keys under Software. Matching is
///     deliberately strict (whole folder or key names only, never shared vendor folders), and
///     removal is undoable: folders go to the Recycle Bin and registry keys are exported first.
/// </summary>
public sealed partial class Leftovers
{
    /// <summary>Names that are shared by many programs or by Windows itself; never a leftover.</summary>
    private static readonly HashSet<string> Shared = new(StringComparer.Ordinal)
    {
        "microsoft",
        "windows",
        "windowsapps",
        "commonfiles",
        "packages",
        "temp",
        "tmp",
        "programs",
        "cache",
        "packagecache",
        "microsoftshared",
        "system32",
        "syswow64",
        "caches",
        "classes",
        "policies",
        "clients",
        "wow6432node",
        "registeredapplications",
        "intel",
        "nvidia",
        "nvidiacorporation",
        "amd",
        "google",
        "mozilla",
        "apple",
        "adobe",
        "oracle",
        "java",
        "python",
        "dotnet",
        "microsoftnet",
        "windowspowershell",
        "internetexplorer",
        "installer",
        "updater",
        "update",
        "launcher",
        "setup",
        "uninstall",
        "app",
        "apps",
        "data",
        "common",
        "shared",
        "tools",
        "bin",
        "trevunix",
    };

    private readonly IReadOnlyList<string> _folderBases;
    private readonly CleanupSafety _safety;
    private List<string> _otherFolders = [];

    public Leftovers()
        : this(DefaultFolderBases(), new CleanupSafety()) { }

    /// <param name="folderBases">Where programs keep folders: Program Files, ProgramData, AppData, ...</param>
    public Leftovers(IReadOnlyList<string> folderBases, CleanupSafety safety)
    {
        _folderBases = folderBases;
        _safety = safety;
    }

    public static string BackupDirectory { get; } =
        Path.Combine(AppPaths.DataDirectory, "Uninstall backups");

    /// <summary>
    ///     A name reduced to what identifies it: lower case letters and digits only, without
    ///     version numbers, bitness or anything in brackets. "7-Zip 23.01 (x64)" and the folder
    ///     "7-Zip" both become "7zip".
    /// </summary>
    public static string Normalize(string name)
    {
        var text = name.ToLowerInvariant();
        // Drop "(x64 en-US)", "[beta]" and similar.
        var sb = new StringBuilder(text.Length);
        var depth = 0;
        foreach (var c in text)
        {
            if (c is '(' or '[')
                depth++;
            else if (c is ')' or ']')
                depth = Math.Max(0, depth - 1);
            else if (depth == 0)
                sb.Append(c);
        }

        // Version numbers such as 23.01 or v2.4.1; a lone number (the 7 in 7-Zip) is kept.
        var words = Version()
            .Replace(sb.ToString(), " ")
            .Split([' ', '-', '_', '.', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !IsNoise(w));
        return new string(string.Concat(words).Where(char.IsLetterOrDigit).ToArray());
    }

    /// <param name="others">
    ///     The other installed programs. A folder that holds any of them (say a shared
    ///     D:\Tools folder) is never offered, whatever its name.
    /// </param>
    public IReadOnlyList<Leftover> Find(
        InstalledProgram program,
        IEnumerable<InstalledProgram>? others = null
    )
    {
        var names = Names(program);
        var found = new Dictionary<string, Leftover>(StringComparer.OrdinalIgnoreCase);
        _otherFolders = (others ?? []).Where(o => o != program).SelectMany(OwnFolders).ToList();

        // Folders the program itself named.
        foreach (var folder in OwnFolders(program))
            AddFolder(found, folder, sure: true);

        // Folders that carry its name, directly or inside its publisher's folder.
        var publisher = program.Publisher is null ? "" : Normalize(program.Publisher);
        foreach (var root in _folderBases)
        {
            foreach (var child in SafeDirectories(root))
            {
                var key = Normalize(Path.GetFileName(child));
                if (names.Contains(key))
                {
                    AddFolder(found, child, sure: false);
                }
                else if (IsPublisherFolder(key, publisher))
                {
                    foreach (var grandChild in SafeDirectories(child))
                    {
                        if (names.Contains(Normalize(Path.GetFileName(grandChild))))
                            AddFolder(found, grandChild, sure: false);
                    }
                }
            }
        }

        // Shortcuts, services, tasks and startup entries that run something from its folders.
        var traceFolders = found
            .Values.Where(l => l.Kind == LeftoverKind.Folder)
            .Select(l => l.Path)
            .Concat(OwnFolders(program).Where(IsPlausibleFolder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var trace in SystemTraces.Find(traceFolders, names))
            found.TryAdd(trace.Kind + ":" + trace.Path + ":" + trace.Detail, trace);

        // Its Uninstall entry, when the uninstaller left it, and its settings keys.
        if (InstalledPrograms.IsStillInstalled(program))
            found[program.RegistryPath] = new Leftover(
                LeftoverKind.RegistryKey,
                program.RegistryPath,
                0,
                Sure: true
            );
        foreach (var key in SoftwareKeys(names, publisher))
            found.TryAdd(key, new Leftover(LeftoverKind.RegistryKey, key, 0, Sure: false));

        // A folder or shortcut inside another found folder goes with it; if either was certain,
        // both are.
        var folders = found.Values.Where(l => l.Kind == LeftoverKind.Folder).ToList();
        foreach (
            var (id, inner) in found
                .Where(kv => kv.Value.Kind is LeftoverKind.Folder or LeftoverKind.Shortcut)
                .ToList()
        )
        {
            var outer = folders.FirstOrDefault(o =>
                o != inner && CleanupSafety.IsSameOrInside(inner.Path, o.Path)
            );
            if (outer is null)
                continue;
            found.Remove(id);
            if (inner.Sure && found.TryGetValue(outer.Path, out var parent))
                found[outer.Path] = parent with { Sure = true };
        }

        return found
            .Values.OrderBy(l => l.Kind)
            .ThenByDescending(l => l.Sure)
            .ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    ///     Removes what was chosen, undoably where Windows allows: folders and shortcuts go to the
    ///     Recycle Bin, registry keys, startup entries and services are exported to one .reg file
    ///     first, and task definitions are copied next to it. Programs still running from a
    ///     folder are closed first, or the folder could not be moved.
    /// </summary>
    public static LeftoverRemoval Remove(IEnumerable<Leftover> leftovers)
    {
        int removed = 0,
            failed = 0;
        var list = leftovers.ToList();
        void Count(bool ok)
        {
            if (ok)
                removed++;
            else
                failed++;
        }

        // Everything in the registry is backed up in one file before any of it changes.
        var registry = list.Where(l =>
                l.Kind
                    is LeftoverKind.RegistryKey
                        or LeftoverKind.StartupEntry
                        or LeftoverKind.Service
            )
            .ToList();
        string? backup = null;
        var backedUp = registry.Count == 0;
        if (registry.Count > 0)
        {
            backup = BackUp(
                registry.Select(RegistryKeyOf).Distinct(StringComparer.OrdinalIgnoreCase)
            );
            backedUp = backup is not null;
        }

        foreach (var item in list)
        {
            switch (item.Kind)
            {
                // Without a backup nothing in the registry is touched.
                case LeftoverKind.RegistryKey:
                    Count(backedUp && DeleteKey(item.Path));
                    break;
                case LeftoverKind.StartupEntry:
                    Count(backedUp && DeleteValue(item.Path, item.Detail ?? ""));
                    break;
                case LeftoverKind.Service:
                    Count(backedUp && DeleteService(item.Path));
                    break;
                case LeftoverKind.ScheduledTask:
                    Count(DeleteTask(item));
                    break;
            }
        }

        foreach (
            var item in list.Where(l => l.Kind is LeftoverKind.Folder or LeftoverKind.Shortcut)
        )
        {
            if (item.Kind == LeftoverKind.Folder)
                StopProcessesIn(item.Path);
            var exists =
                item.Kind == LeftoverKind.Folder
                    ? Directory.Exists(item.Path)
                    : File.Exists(item.Path);
            Count(!exists || RecycleBin.Send(item.Path));
        }

        return new LeftoverRemoval(removed, failed, backup);
    }

    /// <summary>The registry key that holds a leftover, for the backup.</summary>
    private static string RegistryKeyOf(Leftover item) =>
        item.Kind == LeftoverKind.Service ? $@"HKLM\{ServicesKey}\{item.Path}" : item.Path;

    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    private static bool DeleteValue(string keyPath, string name)
    {
        var (hive, subKey) = SplitHive(keyPath);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
            when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Stops the service and deletes it; Windows finishes removing it once nothing holds it.</summary>
    private static bool DeleteService(string name)
    {
        CommandRunner.Run("sc.exe", "stop", name);
        var code = CommandRunner.Run("sc.exe", "delete", name).Code;
        // 1060: the service is already gone.
        return code is 0 or 1060;
    }

    private static bool DeleteTask(Leftover task)
    {
        try
        {
            if (task.Detail is { } file && File.Exists(file))
            {
                var copies = Path.Combine(BackupDirectory, "Tasks");
                Directory.CreateDirectory(copies);
                File.Copy(
                    file,
                    Path.Combine(
                        copies,
                        $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(file)}.xml"
                    ),
                    overwrite: true
                );
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        var code = CommandRunner.Run("schtasks.exe", "/Delete", "/TN", task.Path, "/F").Code;
        return code == 0 || task.Detail is null || !File.Exists(task.Detail);
    }

    /// <summary>Ends programs whose exe sits in a folder about to be removed.</summary>
    private static void StopProcessesIn(string folder)
    {
        var self = Environment.ProcessId;
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == self)
                    continue;
                if (
                    !CleanupSafety.IsSameOrInside(
                        Monitoring.ProcessSampler.TryPath(process) ?? "",
                        folder
                    )
                )
                    continue;
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch (Exception ex)
                    when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                { }
            }
        }
    }

    private static (RegistryHive Hive, string SubKey) SplitHive(string path)
    {
        var slash = path.IndexOf('\\');
        var hive = path[..slash] == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        return (hive, path[(slash + 1)..]);
    }

    /// <summary>A folder path worth matching against, without looking at the disk.</summary>
    private bool IsPlausibleFolder(string folder) =>
        Path.IsPathRooted(folder)
        && _safety.IsSafeRoot(folder)
        && !Shared.Contains(Normalize(Path.GetFileName(folder.TrimEnd('\\'))))
        && !_folderBases.Any(b => CleanupSafety.IsSameOrInside(b, folder))
        && !_otherFolders.Any(o => CleanupSafety.IsSameOrInside(o, folder));

    /// <summary>The program's own names: display name, install folder name and exe folder name.</summary>
    internal static HashSet<string> Names(InstalledProgram program)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { Normalize(program.DisplayName) };
        foreach (var folder in new[] { program.InstallLocation, ExeFolder(program) })
        {
            if (!string.IsNullOrWhiteSpace(folder))
                names.Add(Normalize(Path.GetFileName(folder.TrimEnd('\\'))));
        }
        // Short or shared names would match other programs' folders.
        names.RemoveWhere(n => n.Length < 4 || Shared.Contains(n));
        return names;
    }

    private static IEnumerable<string> OwnFolders(InstalledProgram program)
    {
        if (!string.IsNullOrWhiteSpace(program.InstallLocation))
            yield return program.InstallLocation;
        if (ExeFolder(program) is { } exeFolder)
            yield return exeFolder;
    }

    /// <summary>The folder of the program's icon or uninstaller, when that sits in its own folder.</summary>
    private static string? ExeFolder(InstalledProgram program)
    {
        var exe =
            program.IconPath ?? InstalledPrograms.ParseCommand(program.UninstallString)?.FileName;
        if (exe is null || !Path.IsPathRooted(exe))
            return null;
        var folder = Path.GetDirectoryName(exe);
        // An uninstaller that lives in a shared place (Common Files, System32, a package
        // cache, another vendor's app data) does not tell where the program itself is.
        return
            folder is null
            || folder
                .Split(Path.DirectorySeparatorChar)
                .Any(part => Shared.Contains(Normalize(part)))
            ? null
            : folder;
    }

    private void AddFolder(Dictionary<string, Leftover> found, string folder, bool sure)
    {
        string full;
        try
        {
            full = Path.GetFullPath(folder).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return;
        }

        if (!Directory.Exists(full) || !_safety.IsSafeRoot(full))
            return;
        // A shared folder (Program Files\Common Files, ...) is never a program's leftover.
        if (Shared.Contains(Normalize(Path.GetFileName(full))))
            return;
        // One of the base folders itself, such as Program Files.
        if (_folderBases.Any(b => CleanupSafety.IsSameOrInside(b, full)))
            return;
        // A folder another installed program lives in.
        if (_otherFolders.Any(o => CleanupSafety.IsSameOrInside(o, full)))
            return;

        if (found.TryGetValue(full, out var existing))
        {
            if (sure && !existing.Sure)
                found[full] = existing with { Sure = true };
            return;
        }
        found[full] = new Leftover(LeftoverKind.Folder, full, FolderSize(full), sure);
    }

    private static bool IsPublisherFolder(string folder, string publisher) =>
        publisher.Length >= 3
        && !Shared.Contains(folder)
        && folder.Length >= 3
        && (publisher.StartsWith(folder, StringComparison.Ordinal) || folder == publisher);

    private static IEnumerable<string> SoftwareKeys(HashSet<string> names, string publisher)
    {
        foreach (
            var (prefix, hive, view, software) in new[]
            {
                ("HKCU", RegistryHive.CurrentUser, RegistryView.Registry64, "Software"),
                ("HKLM", RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE"),
                (
                    "HKLM",
                    RegistryHive.LocalMachine,
                    RegistryView.Registry64,
                    @"SOFTWARE\WOW6432Node"
                ),
            }
        )
        {
            List<string> found = [];
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(software);
                if (key is null)
                    continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    var normal = Normalize(name);
                    if (names.Contains(normal))
                    {
                        found.Add($@"{prefix}\{software}\{name}");
                    }
                    else if (IsPublisherFolder(normal, publisher))
                    {
                        using var vendor = key.OpenSubKey(name);
                        foreach (var child in vendor?.GetSubKeyNames() ?? [])
                        {
                            if (names.Contains(Normalize(child)))
                                found.Add($@"{prefix}\{software}\{name}\{child}");
                        }
                    }
                }
            }
            catch (Exception ex)
                when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }

            foreach (var path in found)
                yield return path;
        }
    }

    /// <summary>Exports every key into one .reg file next to Trevunix's data; null when that failed.</summary>
    private static string? BackUp(IEnumerable<string> keys)
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var file = Path.Combine(BackupDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}.reg");
            var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
            foreach (var key in keys)
            {
                var part = Path.Combine(Path.GetTempPath(), $"trevunix-{Guid.NewGuid():N}.reg");
                try
                {
                    if (CommandRunner.Run("reg.exe", "export", key, part, "/y").Code != 0)
                        continue;
                    // Each export starts with its own header; keep only the key data.
                    var lines = File.ReadAllLines(part).Skip(1);
                    text.Append("\r\n").AppendJoin("\r\n", lines);
                }
                finally
                {
                    File.Delete(part);
                }
            }
            File.WriteAllText(file, text.ToString(), Encoding.Unicode);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not back up registry keys: {0}", ex.Message);
            return null;
        }
    }

    private static bool DeleteKey(string path)
    {
        var (hive, subKey) = SplitHive(path);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            root.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex)
            when (ex
                    is System.Security.SecurityException
                        or UnauthorizedAccessException
                        or ArgumentException
            )
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory
                    .EnumerateDirectories(
                        folder,
                        "*",
                        new EnumerationOptions
                        {
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

    private static long FolderSize(string folder)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateFiles(
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    }
                )
                .Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool IsNoise(string word) =>
        word is "x64" or "x86" or "64bit" or "32bit" or "win64" or "win32" or "amd64" or "arm64";

    [System.Text.RegularExpressions.GeneratedRegex(@"\bv?\d+(\.\d+)+\b")]
    private static partial System.Text.RegularExpressions.Regex Version();

    private static IReadOnlyList<string> DefaultFolderBases()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData),
            F(Environment.SpecialFolder.ApplicationData),
            local,
            Path.Combine(local, "Programs"),
            // Start menu folders named after the program.
            F(Environment.SpecialFolder.Programs),
            F(Environment.SpecialFolder.CommonPrograms),
            Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
        }.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
