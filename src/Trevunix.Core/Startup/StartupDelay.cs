using System.Security;
using Trevunix.Core.Maintenance;

namespace Trevunix.Core.Startup;

/// <summary>
///     Starts a program a while after sign-in instead of with it: its normal startup entry is
///     turned off (as Task Manager would) and a Task Scheduler task runs the same command later,
///     without admin rights. Undoing removes the task and turns the entry back on.
/// </summary>
public static class StartupDelay
{
    public const int DefaultSeconds = 60;
    private const string Folder = @"\Trevunix\Delayed\";

    public static string TaskName(StartupEntry entry) =>
        Folder
        + string.Concat(
            entry.Name.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) || c == '\\' ? '_' : c
            )
        );

    /// <summary>Task Scheduler keeps each task as a file; checking it is much faster than schtasks.</summary>
    public static bool IsDelayed(StartupEntry entry) =>
        File.Exists(
            Path.Combine(Environment.SystemDirectory, "Tasks", TaskName(entry).TrimStart('\\'))
        );

    public static bool Delay(
        StartupEntry entry,
        StartupManager manager,
        int seconds = DefaultSeconds
    )
    {
        var (exe, args) = entry.Source is StartupSource.UserFolder or StartupSource.MachineFolder
            // Shortcuts and other files in the Startup folder open like a double-click.
            ? ("explorer.exe", $"\"{entry.Command}\"")
            : SplitCommand(entry.Command);
        var user = ScheduledTask.CurrentUser();
        var xml = ScheduledTask.BuildXml(
            $"Starts {entry.Name} {seconds} seconds after sign-in (delayed by Trevunix)",
            $"""
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(user)}</UserId>
              <Delay>PT{seconds}S</Delay>
            </LogonTrigger>
            """,
            user,
            exe,
            args,
            "PT0S",
            startWhenAvailable: false,
            elevated: false
        );
        if (!ScheduledTask.Register(TaskName(entry), xml))
            return false;
        manager.SetEnabled(entry, false);
        return true;
    }

    public static bool Undo(StartupEntry entry, StartupManager manager)
    {
        if (!ScheduledTask.Remove(TaskName(entry)))
            return false;
        manager.SetEnabled(entry, true);
        return true;
    }

    /// <summary>Splits a Run command into the program and its arguments.</summary>
    public static (string Exe, string Args) SplitCommand(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 0)
                return (text[1..end], text[(end + 1)..].Trim());
        }

        var exe = StartupManager.ExtractPath(text) ?? text;
        return (exe, text.Length > exe.Length ? text[exe.Length..].Trim() : "");
    }
}
