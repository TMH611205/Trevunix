using System.Security;
using System.Text;
using Trevunix.Core.Tools;

namespace Trevunix.Core.Maintenance;

/// <summary>
///     Trevunix's own Task Scheduler entries, made through schtasks.exe from task XML. They run
///     the exe with full rights while the owner is signed in, so there is no UAC prompt.
/// </summary>
internal static class ScheduledTask
{
    /// <summary>Creates or replaces the task. False when Task Scheduler refused it.</summary>
    public static bool Register(string name, string xml)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"trevunix-task-{Guid.NewGuid():N}.xml");
        try
        {
            // Task Scheduler reads task XML as UTF-16.
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);
            return Run("/Create", "/TN", name, "/XML", xmlPath, "/F");
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch (IOException) { }
        }
    }

    /// <summary>Deletes the task; true when it is gone, including when it never existed.</summary>
    public static bool Remove(string name) =>
        Run("/Delete", "/TN", name, "/F") || !Run("/Query", "/TN", name);

    public static bool Exists(string name) => Run("/Query", "/TN", name);

    public static string CurrentUser() =>
        System.Security.Principal.WindowsIdentity.GetCurrent().Name;

    /// <param name="trigger">One trigger element, such as a CalendarTrigger.</param>
    /// <param name="timeLimit">ISO 8601 duration; <c>PT0S</c> lets the task run as long as it likes.</param>
    public static string BuildXml(
        string description,
        string trigger,
        string user,
        string exePath,
        string arguments,
        string timeLimit,
        bool startWhenAvailable,
        bool elevated = true
    ) =>
        $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{SecurityElement.Escape(description)}</Description>
              </RegistrationInfo>
              <Triggers>
                {trigger}
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>{(elevated ? "HighestAvailable" : "LeastPrivilege")}</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>{(startWhenAvailable ? "true" : "false")}</StartWhenAvailable>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>{timeLimit}</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>
                  <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

    private static bool Run(params string[] args) =>
        CommandRunner.Run("schtasks.exe", args).Code == 0;
}
