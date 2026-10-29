using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Trevunix.Core.Apps;

/// <summary>A desktop program from the Windows "Installed apps" list, or one Windows hides from it.</summary>
/// <param name="IsHidden">
///     Windows does not list it: a system component, a part of another program, or an entry
///     whose uninstaller is gone. Such a program can still be removed, leftovers first.
/// </param>
public sealed record InstalledProgram(
    string DisplayName,
    string? Publisher,
    string? Version,
    string? InstallLocation,
    string? UninstallString,
    string? QuietUninstallString,
    string? IconPath,
    long SizeBytes,
    DateTime? InstalledOn,
    RegistryHive Hive,
    RegistryView View,
    string KeyName,
    bool IsHidden = false
)
{
    /// <summary>The program's entry under the Uninstall key, e.g. for removing it when it is left behind.</summary>
    public string RegistryPath =>
        $@"{(Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\{InstalledPrograms.UninstallKey}\{KeyName}";

    public bool IsMachineWide => Hive == RegistryHive.LocalMachine;
}

/// <summary>A program's uninstaller split into the exe to start and its arguments.</summary>
public sealed record UninstallCommand(string FileName, string Arguments);

/// <summary>
///     Lists desktop programs from the Uninstall keys (64- and 32-bit, machine and user) and
///     starts their own uninstallers. Besides what Settings shows it also returns the entries
///     Windows hides, flagged <see cref="InstalledProgram.IsHidden"/>; only updates and hotfixes
///     are left out, since they are removed with their program.
/// </summary>
public static partial class InstalledPrograms
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledProgram> List()
    {
        var found = new List<InstalledProgram>();
        foreach (
            var (hive, view) in new[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
                (RegistryHive.CurrentUser, RegistryView.Registry64),
            }
        )
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null)
                    continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is not null && Read(key, hive, view, name) is { } program)
                        found.Add(program);
                }
            }
            catch (Exception ex)
                when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        }

        // The same program can be registered in both views; show it once.
        return found
            .GroupBy(p => (p.DisplayName.ToUpperInvariant(), p.Version))
            .Select(g => g.OrderBy(p => p.IsHidden).First())
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    ///     Splits an UninstallString into exe and arguments. Windows Installer entries are
    ///     turned into a removal (<c>/X</c>), since many register <c>/I</c>, which opens a
    ///     repair dialog instead.
    /// </summary>
    public static UninstallCommand? ParseCommand(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var text = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        string file;
        string args;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0)
                return null;
            file = text[1..end];
            args = text[(end + 1)..].Trim();
        }
        else
        {
            // Unquoted paths may contain spaces; the exe ends at the first ".exe".
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            var end = exe >= 0 ? exe + 4 : text.IndexOf(' ');
            if (end < 0)
                end = text.Length;
            file = text[..end];
            args = text[end..].Trim();
        }

        if (Path.GetFileName(file).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            args = MsiInstall().Replace(args, "/X");

        return file.Length == 0 ? null : new UninstallCommand(file, args);
    }

    /// <summary>
    ///     Starts the program's own uninstaller and waits for it. Many uninstallers copy
    ///     themselves to Temp and exit at once, so the caller should still check whether the
    ///     program is gone before looking for leftovers. Null when there was nothing to start.
    /// </summary>
    public static async Task<int?> RunUninstallerAsync(
        InstalledProgram program,
        CancellationToken cancellationToken = default
    )
    {
        if (ParseCommand(program.UninstallString) is not { } command)
            return null;
        // A full path that is gone means a broken entry; a bare name (msiexec) is found on PATH.
        if (Path.IsPathRooted(command.FileName) && !File.Exists(command.FileName))
            return null;

        try
        {
            using var process = Process.Start(
                new ProcessStartInfo(command.FileName, command.Arguments) { UseShellExecute = true }
            );
            if (process is null)
                return null;
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Whether the program's Uninstall entry is still there.</summary>
    public static bool IsStillInstalled(InstalledProgram program)
    {
        // A program picked by folder for a forced uninstall has no entry of its own.
        if (program.KeyName.Length == 0)
            return false;
        try
        {
            using var root = RegistryKey.OpenBaseKey(program.Hive, program.View);
            using var key = root.OpenSubKey($@"{UninstallKey}\{program.KeyName}");
            return key is not null;
        }
        catch (Exception ex)
            when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static InstalledProgram? Read(
        RegistryKey key,
        RegistryHive hive,
        RegistryView view,
        string keyName
    )
    {
        var name = Text(key, "DisplayName");
        var uninstall = Text(key, "UninstallString");
        if (name is null)
            return null;
        if (Text(key, "ReleaseType") is "Update" or "Hotfix" or "Security Update")
            return null;
        var hidden =
            key.GetValue("SystemComponent") is int and 1
            || Text(key, "ParentKeyName") is not null
            || uninstall is null;

        return new InstalledProgram(
            name,
            Text(key, "Publisher"),
            Text(key, "DisplayVersion"),
            Text(key, "InstallLocation")?.Trim('"'),
            uninstall,
            Text(key, "QuietUninstallString"),
            IconFile(Text(key, "DisplayIcon")),
            key.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0,
            InstallDate(Text(key, "InstallDate")),
            hive,
            view,
            keyName,
            hidden
        );
    }

    /// <summary>"C:\App\app.exe,0" or a quoted path, to just the file.</summary>
    internal static string? IconFile(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
            return null;
        var path = displayIcon.Trim();
        var comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path[(comma + 1)..], out _))
            path = path[..comma];
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        return path.Length > 0 ? path : null;
    }

    private static DateTime? InstallDate(string? yyyymmdd) =>
        DateTime.TryParseExact(
            yyyymmdd,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date
        )
            ? date
            : null;

    private static string? Text(RegistryKey key, string name) =>
        key.GetValue(name) is string s && s.Trim().Length > 0 ? s.Trim() : null;

    [GeneratedRegex(@"/I(?=\s*\{)", RegexOptions.IgnoreCase)]
    private static partial Regex MsiInstall();
}
