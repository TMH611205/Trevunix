using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Trevunix.Core.Monitoring;
using Trevunix.Core.Tools;

namespace Trevunix.Core.Performance;

/// <summary>A running app the booster may close, grouped by process name.</summary>
public sealed record BoosterApp(string Name, string? Path, long MemoryBytes, int Instances);

/// <summary>What starting the booster changed, so stopping it can put things back.</summary>
public sealed record BoostSession(Guid? PreviousPowerPlan, IReadOnlyList<string> ClosedAppPaths);

/// <param name="HighPerformanceOn">False when this PC has no High performance plan to switch to.</param>
public sealed record BoostResult(BoostSession Session, bool HighPerformanceOn);

/// <summary>
///     Frees the PC up for a game: switches to the High performance power plan and closes the
///     apps the owner picked. Stopping restores the previous plan and reopens those apps.
///     Freeing RAM is left to <see cref="Memory.MemoryCleaner"/>.
/// </summary>
public static partial class GameBooster
{
    /// <summary>The built-in High performance plan.</summary>
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(4);

    /// <summary>Apps with a window or a large footprint, biggest first, never system processes.</summary>
    public static IReadOnlyList<BoosterApp> ListApps()
    {
        var self = Environment.ProcessId;
        var apps = new List<BoosterApp>();
        foreach (var group in Process.GetProcesses().GroupBy(p => p.ProcessName))
        {
            try
            {
                if (!ProcessControl.CanClose(group.Key) || group.Any(p => p.Id == self))
                    continue;

                var memory = group.Sum(p => Try(() => p.WorkingSet64, 0L));
                var hasWindow = group.Any(p => Try(() => p.MainWindowHandle, IntPtr.Zero) != 0);
                if (!hasWindow && memory < 150L * 1024 * 1024)
                    continue;

                apps.Add(
                    new BoosterApp(
                        group.Key,
                        group.Select(ProcessSampler.TryPath).FirstOrDefault(p => p is not null),
                        memory,
                        group.Count()
                    )
                );
            }
            finally
            {
                foreach (var p in group)
                    p.Dispose();
            }
        }
        return apps.OrderByDescending(a => a.MemoryBytes).ToList();
    }

    public static BoostResult Start(IEnumerable<string> appNames)
    {
        var previous = ActivePowerPlan();
        var changed = previous != HighPerformance && SetPowerPlan(HighPerformance);
        var closed = CloseApps(appNames.Distinct(StringComparer.OrdinalIgnoreCase));
        return new BoostResult(
            new BoostSession(changed ? previous : null, closed),
            changed || previous == HighPerformance
        );
    }

    /// <summary>Puts the power plan back. Reopening apps is separate so it can be skipped on exit.</summary>
    public static void RestorePowerPlan(BoostSession session)
    {
        if (session.PreviousPowerPlan is { } plan)
            SetPowerPlan(plan);
    }

    public static int ReopenApps(BoostSession session)
    {
        var opened = 0;
        foreach (var path in session.ClosedAppPaths.Where(File.Exists))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
                opened++;
            }
            catch (Win32Exception) { }
        }
        return opened;
    }

    public static Guid? ActivePowerPlan() =>
        ParsePowerPlan(CommandRunner.Run("powercfg.exe", "/getactivescheme").Output);

    /// <summary>Reads the GUID from <c>powercfg /getactivescheme</c>, whose text is localized.</summary>
    internal static Guid? ParsePowerPlan(string output) =>
        GuidPattern().Match(output) is { Success: true } m ? Guid.Parse(m.Value) : null;

    private static bool SetPowerPlan(Guid plan) =>
        CommandRunner.Run("powercfg.exe", "/setactive", plan.ToString()).Code == 0;

    /// <summary>
    ///     Asks every window of every chosen app to close at once, waits one shared grace period,
    ///     then ends what is left. Returns the exe of each app that was running.
    /// </summary>
    private static List<string> CloseApps(IEnumerable<string> names)
    {
        var running = names
            .Where(ProcessControl.CanClose)
            .Select(Process.GetProcessesByName)
            .Where(group => group.Length > 0)
            .ToList();
        var paths = running
            .Select(group =>
                group.Select(ProcessSampler.TryPath).FirstOrDefault(p => p is not null)
            )
            .OfType<string>()
            .ToList();
        var processes = running.SelectMany(group => group).ToList();
        try
        {
            foreach (var p in processes)
                Try(p.CloseMainWindow, false);

            var deadline = DateTime.UtcNow + CloseWait;
            foreach (var p in processes)
            {
                try
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left > TimeSpan.Zero && p.WaitForExit(left))
                        continue;
                    if (!p.HasExited)
                        p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
        return paths;
    }

    /// <summary>Reads a property of a process that may exit or deny access while we look.</summary>
    private static T Try<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return fallback;
        }
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}
