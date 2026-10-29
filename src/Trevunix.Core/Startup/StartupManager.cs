using System.Diagnostics;
using Microsoft.Win32;
using Trevunix.Core.Tweaks;

namespace Trevunix.Core.Startup;

public enum StartupSource
{
    UserRegistry,
    MachineRegistry,
    MachineRegistry32,
    UserFolder,
    MachineFolder,
}

public sealed record StartupEntry(
    string Name,
    string Command,
    StartupSource Source,
    bool Enabled,
    string? FilePath,
    string? Publisher
)
{
    /// <summary>The StartupApproved value name: the file name (with extension) for folder entries.</summary>
    public string ApprovedName { get; init; } = Name;

    public bool IsMachineWide =>
        Source is not (StartupSource.UserRegistry or StartupSource.UserFolder);
}

/// <summary>Where startup entries live. Tests point these at throwaway keys and folders.</summary>
public sealed record StartupLocations(
    string UserRun,
    string MachineRun,
    string MachineRun32,
    string UserApproved,
    string MachineApproved,
    string UserFolder,
    string MachineFolder
)
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static StartupLocations Windows { get; } =
        new(
            $@"HKCU\{Run}",
            $@"HKLM\{Run}",
            @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            $@"HKCU\{Approved}",
            $@"HKLM\{Approved}",
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        );
}

/// <summary>
///     Lists startup programs and turns them on or off the same way Task Manager does:
///     through the StartupApproved values, so nothing is deleted and every change is reversible.
/// </summary>
public sealed class StartupManager(StartupLocations? locations = null)
{
    private const byte EnabledMark = 0x02;
    private const byte DisabledMark = 0x03;

    private readonly StartupLocations _at = locations ?? StartupLocations.Windows;

    public IReadOnlyList<StartupEntry> List()
    {
        var entries = new List<StartupEntry>();
        ReadRun(entries, _at.UserRun, StartupSource.UserRegistry);
        ReadRun(entries, _at.MachineRun, StartupSource.MachineRegistry);
        ReadRun(entries, _at.MachineRun32, StartupSource.MachineRegistry32);
        ReadFolder(entries, _at.UserFolder, StartupSource.UserFolder);
        ReadFolder(entries, _at.MachineFolder, StartupSource.MachineFolder);
        return entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void SetEnabled(StartupEntry entry, bool enabled)
    {
        var (root, sub) = RegistryValueAction.Split(ApprovedKey(entry.Source));
        using var key =
            root.CreateSubKey(sub, writable: true)
            ?? throw new UnauthorizedAccessException(ApprovedKey(entry.Source));

        var data = new byte[12];
        data[0] = enabled ? EnabledMark : DisabledMark;
        if (!enabled)
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        key.SetValue(entry.ApprovedName, data, RegistryValueKind.Binary);
    }

    /// <summary>The exe a startup command runs: a quoted path, or everything up to ".exe".</summary>
    public static string? ExtractPath(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.Length == 0)
            return null;

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
            return text[..(exe + 4)];

        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>Task Manager treats a missing value or an even first byte as enabled.</summary>
    internal static bool IsEnabled(byte[]? approved) =>
        approved is not { Length: > 0 } || (approved[0] & 1) == 0;

    private string ApprovedKey(StartupSource source) =>
        source switch
        {
            StartupSource.UserRegistry => $@"{_at.UserApproved}\Run",
            StartupSource.MachineRegistry => $@"{_at.MachineApproved}\Run",
            StartupSource.MachineRegistry32 => $@"{_at.MachineApproved}\Run32",
            StartupSource.UserFolder => $@"{_at.UserApproved}\StartupFolder",
            _ => $@"{_at.MachineApproved}\StartupFolder",
        };

    private byte[]? ReadApproved(StartupSource source, string name)
    {
        var (root, sub) = RegistryValueAction.Split(ApprovedKey(source));
        using var key = root.OpenSubKey(sub);
        return key?.GetValue(name) as byte[];
    }

    private void ReadRun(List<StartupEntry> into, string keyPath, StartupSource source)
    {
        var (root, sub) = RegistryValueAction.Split(keyPath);
        using var key = root.OpenSubKey(sub);
        if (key is null)
            return;

        foreach (var name in key.GetValueNames())
        {
            if (name.Length == 0 || key.GetValue(name) is not string command)
                continue;

            var path = ExtractPath(command);
            into.Add(
                new StartupEntry(
                    name,
                    command,
                    source,
                    IsEnabled(ReadApproved(source, name)),
                    path,
                    Publisher(path)
                )
            );
        }
    }

    private void ReadFolder(List<StartupEntry> into, string folder, StartupSource source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                continue;

            into.Add(
                new StartupEntry(
                    Path.GetFileNameWithoutExtension(file),
                    file,
                    source,
                    IsEnabled(ReadApproved(source, name)),
                    file,
                    Publisher(file)
                )
                {
                    ApprovedName = name,
                }
            );
        }
    }

    private static string? Publisher(string? path)
    {
        try
        {
            if (
                path is null
                || !File.Exists(path)
                || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            )
                return null;
            var company = FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim();
            return string.IsNullOrEmpty(company) ? null : company;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
