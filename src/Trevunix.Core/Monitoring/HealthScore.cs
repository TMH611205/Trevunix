using Trevunix.Core.SystemInfo;
using Trevunix.Core.Tools;

namespace Trevunix.Core.Monitoring;

public enum ScoreIssue
{
    SystemDriveFull,
    SystemDriveLow,
    DiskFailing,
    PendingTweaks,
    DriverProblems,
    AppUpdates,
    HeavyStartup,
}

/// <param name="Count">How many of it (tweaks, devices, apps); the free percent for drive issues.</param>
public sealed record ScoreItem(ScoreIssue Issue, int Count, int Points);

/// <summary>What the home page score is made of; each part is optional (null when not known yet).</summary>
public sealed record HealthInputs(
    double? SystemDriveFreePercent = null,
    int? FailingDisks = null,
    int? PendingRecommendedTweaks = null,
    int? DriverProblems = null,
    int? AppUpdates = null,
    int? HighImpactStartup = null
);

/// <summary>
///     A 0 to 100 summary of the PC's state. Each problem takes away a fixed number of points,
///     capped per kind so one long list (say, 30 app updates) cannot sink the score alone.
/// </summary>
public static class HealthScore
{
    public static (int Score, IReadOnlyList<ScoreItem> Items) Compute(HealthInputs input)
    {
        var items = new List<ScoreItem>();

        if (input.SystemDriveFreePercent is { } free)
        {
            if (free < 10)
                items.Add(new ScoreItem(ScoreIssue.SystemDriveFull, (int)free, 25));
            else if (free < 20)
                items.Add(new ScoreItem(ScoreIssue.SystemDriveLow, (int)free, 10));
        }
        Add(items, ScoreIssue.DiskFailing, input.FailingDisks, each: 30, max: 40);
        Add(items, ScoreIssue.PendingTweaks, input.PendingRecommendedTweaks, each: 2, max: 15);
        Add(items, ScoreIssue.DriverProblems, input.DriverProblems, each: 10, max: 20);
        Add(items, ScoreIssue.AppUpdates, input.AppUpdates, each: 1, max: 10);
        Add(items, ScoreIssue.HeavyStartup, input.HighImpactStartup, each: 3, max: 15);

        return (Math.Clamp(100 - items.Sum(i => i.Points), 0, 100), items);
    }

    private static void Add(List<ScoreItem> items, ScoreIssue issue, int? count, int each, int max)
    {
        if (count is > 0)
            items.Add(new ScoreItem(issue, count.Value, Math.Min(count.Value * each, max)));
    }

    /// <summary>Devices Windows reports with a problem code (missing or failing driver).</summary>
    public static int CountDriverProblems() =>
        Wmi.Query(
            "SELECT ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"
        ).Count;

    /// <summary>Apps winget can update, or null when winget is missing or failed.</summary>
    public static IReadOnlyList<AppUpdate>? ListAppUpdates()
    {
        var (code, output) = CommandRunner.Run(
            "winget.exe",
            "upgrade",
            "--include-unknown",
            "--accept-source-agreements",
            "--disable-interactivity"
        );
        return code == -1 ? null : ParseWingetUpgrades(output);
    }

    /// <summary>
    ///     Rows of the winget upgrade table. Headers and footers are translated, but every row
    ///     ends with its source name, which is not. Read from the right, the columns before it
    ///     are the available version, the installed version and the id; the rest is the name.
    /// </summary>
    internal static IReadOnlyList<AppUpdate> ParseWingetUpgrades(string output)
    {
        var updates = new List<AppUpdate>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (
                !line.EndsWith(" winget", StringComparison.Ordinal)
                && !line.EndsWith(" msstore", StringComparison.Ordinal)
            )
                continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count < 5)
                continue;
            var available = parts[^2];
            var version = parts[^3];
            var idAt = parts.Count - 4;
            // Unknown versions show as "< 1.2" or "> 1.2", which is two words.
            if (idAt > 0 && parts[idAt] is "<" or ">")
            {
                version = parts[idAt] + " " + version;
                idAt--;
            }
            if (idAt < 1)
                continue;
            updates.Add(
                new AppUpdate(string.Join(' ', parts[..idAt]), parts[idAt], version, available)
            );
        }
        return updates;
    }
}

/// <summary>One app winget can update.</summary>
public sealed record AppUpdate(string Name, string Id, string Version, string Available);
