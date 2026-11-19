using System.Diagnostics;
using Microsoft.Win32;
using Trevunix.Core.SystemInfo;

namespace Trevunix.Core.Startup;

/// <param name="Path">Task path, such as <c>\GoogleSystem\GoogleUpdater</c>.</param>
public sealed record AutorunTask(string Path, string Command, bool Enabled);

/// <param name="Automatic">Starts with Windows; otherwise it starts only when something needs it.</param>
public sealed record AutorunService(
    string Name,
    string DisplayName,
    string Command,
    string? Company,
    bool Automatic
);

/// <summary>
///     Scheduled tasks and services that third-party programs add, the other places besides the
///     Run keys where software starts by itself. Microsoft's own entries are left out; turning
///     one off is reversible (a task is disabled, a service is set to start on demand).
/// </summary>
public static class Autoruns
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private const int AutomaticStart = 2;
    private const int ManualStart = 3;

    public static IReadOnlyList<AutorunTask> ListTasks()
    {
        var tasks = new List<AutorunTask>();
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        Walk(service.GetFolder("\\"), tasks);
        return [.. tasks.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)];
    }

    public static void SetTaskEnabled(string path, bool enabled)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        var slash = path.LastIndexOf('\\');
        dynamic folder = service.GetFolder(slash <= 0 ? "\\" : path[..slash]);
        dynamic task = folder.GetTask(path[(slash + 1)..]);
        task.Enabled = enabled;
    }

    public static IReadOnlyList<AutorunService> ListServices() =>
        [
            .. Wmi.Query("SELECT Name, DisplayName, PathName, StartMode FROM Win32_Service")
                .Select(s =>
                {
                    var command = s["PathName"] as string ?? "";
                    return new AutorunService(
                        s["Name"] as string ?? "",
                        s["DisplayName"] as string ?? "",
                        command,
                        Company(StartupManager.ExtractPath(command)),
                        string.Equals(
                            s["StartMode"] as string,
                            "Auto",
                            StringComparison.OrdinalIgnoreCase
                        )
                    );
                })
                .Where(s =>
                    s.Name.Length > 0 && !IsMicrosoft(s.Company) && !IsWindowsPath(s.Command)
                )
                .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        ];

    /// <summary>On demand instead of at boot; the service still starts when an app asks for it.</summary>
    public static void SetServiceAutomatic(string name, bool automatic)
    {
        using var key =
            Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{name}", writable: true)
            ?? throw new UnauthorizedAccessException(name);
        key.SetValue("Start", automatic ? AutomaticStart : ManualStart, RegistryValueKind.DWord);
    }

    internal static bool IsMicrosoftTask(string path) =>
        path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);

    internal static bool IsMicrosoft(string? company) =>
        company?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Drivers and services that live in the Windows folder belong to Windows.</summary>
    internal static bool IsWindowsPath(string command)
    {
        var path = StartupManager.ExtractPath(command);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return path is null
            || path.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase);
    }

    private static void Walk(dynamic folder, List<AutorunTask> into)
    {
        string folderPath = folder.Path;
        if (IsMicrosoftTask(folderPath + "\\"))
            return;

        // 1 = TASK_ENUM_HIDDEN, so tasks marked hidden are listed too.
        foreach (dynamic task in folder.GetTasks(1))
        {
            string command = "";
            foreach (dynamic action in task.Definition.Actions)
            {
                // 0 = TASK_ACTION_EXEC; COM handlers and e-mail actions have no command line.
                if ((int)action.Type == 0)
                {
                    command = $"{action.Path} {action.Arguments}".Trim();
                    break;
                }
            }
            into.Add(new AutorunTask((string)task.Path, command, (bool)task.Enabled));
        }
        foreach (dynamic sub in folder.GetFolders(0))
            Walk(sub, into);
    }

    private static string? Company(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
