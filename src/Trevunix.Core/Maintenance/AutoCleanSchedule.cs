using System.Globalization;
using System.Text.Json;
using Trevunix.Core.Settings;

namespace Trevunix.Core.Maintenance;

public enum CleanFrequency
{
    Off,
    Daily,
    Weekly,
    Monthly,
}

/// <summary>What the last scheduled cleanup did; kept in its own file because it runs in a separate process.</summary>
public sealed record AutoCleanRun(DateTime FinishedAt, long FreedBytes, long DeletedFiles);

/// <summary>
///     Runs the cleanup on a schedule through Windows Task Scheduler with
///     <c>Trevunix.exe --auto-clean</c>, only while the owner is signed in (the caches live in
///     their profile), catching up after a run missed because the PC was off.
/// </summary>
public static class AutoCleanSchedule
{
    public const string Argument = "--auto-clean";
    public const string TaskName = @"Trevunix\AutoClean";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string LogPath { get; } = Path.Combine(AppPaths.DataDirectory, "autoclean.json");

    /// <summary>Creates or replaces the task. False when Task Scheduler refused it.</summary>
    public static bool Register(string exePath, CleanFrequency frequency) =>
        frequency == CleanFrequency.Off
            ? Remove()
            : ScheduledTask.Register(
                TaskName,
                BuildTaskXml(
                    exePath,
                    frequency,
                    DateTime.Today.AddHours(12),
                    ScheduledTask.CurrentUser()
                )
            );

    /// <summary>Deletes the task; true when it is gone, including when it never existed.</summary>
    public static bool Remove() => ScheduledTask.Remove(TaskName);

    public static AutoCleanRun? LastRun()
    {
        try
        {
            return File.Exists(LogPath)
                ? JsonSerializer.Deserialize<AutoCleanRun>(File.ReadAllText(LogPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public static void SaveRun(AutoCleanRun run)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllText(LogPath, JsonSerializer.Serialize(run, Json));
    }

    internal static string BuildTaskXml(
        string exePath,
        CleanFrequency frequency,
        DateTime start,
        string user
    )
    {
        var schedule = frequency switch
        {
            CleanFrequency.Daily => "<ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>",
            CleanFrequency.Weekly => Weekly(1),
            CleanFrequency.Monthly => Weekly(4),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency)),
        };

        var trigger = $"""
            <CalendarTrigger>
              <StartBoundary>{start.ToString(
                "yyyy-MM-ddTHH:mm:ss",
                CultureInfo.InvariantCulture
            )}</StartBoundary>
              <Enabled>true</Enabled>
              {schedule}
            </CalendarTrigger>
            """;
        return ScheduledTask.BuildXml(
            "Trevunix scheduled cleanup",
            trigger,
            user,
            exePath,
            Argument,
            "PT1H",
            startWhenAvailable: true
        );

        static string Weekly(int weeks) =>
            $"<ScheduleByWeek><WeeksInterval>{weeks}</WeeksInterval><DaysOfWeek><Sunday /></DaysOfWeek></ScheduleByWeek>";
    }
}
