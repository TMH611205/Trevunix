using System.Security;

namespace Trevunix.Core.Maintenance;

/// <summary>
///     Starts Trevunix in the notification area when the owner signs in. A Task Scheduler
///     entry instead of the Run key, because the Run key cannot start an app that needs admin
///     rights without a UAC prompt at every sign-in.
/// </summary>
public static class LaunchAtLogon
{
    /// <summary>Start hidden in the notification area instead of opening the window.</summary>
    public const string Argument = "--tray";
    public const string TaskName = @"Trevunix\Launch";

    public static bool Set(string exePath, bool enabled) =>
        enabled
            ? ScheduledTask.Register(TaskName, BuildTaskXml(exePath, ScheduledTask.CurrentUser()))
            : ScheduledTask.Remove(TaskName);

    internal static string BuildTaskXml(string exePath, string user) =>
        ScheduledTask.BuildXml(
            "Starts Trevunix in the notification area at sign-in",
            $"""
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(user)}</UserId>
              <Delay>PT15S</Delay>
            </LogonTrigger>
            """,
            user,
            exePath,
            Argument,
            "PT0S",
            startWhenAvailable: false
        );
}
