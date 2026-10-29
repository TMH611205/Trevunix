using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Trevunix.Core.Performance;

/// <summary>A running app the booster may close, grouped by process name.</summary>
public sealed record BoosterApp(string Name, string? Path, long MemoryBytes, int Instances);

/// <summary>What starting the booster changed, so stopping it can put things back.</summary>
public sealed record BoostSession(Guid? PreviousPowerPlan, IReadOnlyList<string> ClosedAppPaths);

public sealed record BoostResult(BoostSession Session, bool PowerPlanChanged, int AppsClosed);

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

    /// <summary>Parts of the Windows shell that have windows but are not apps to close.</summary>
    private static readonly HashSet<string> Shell = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "TextInputHost",
        "LockApp",
        "SystemSettings",
        "Widgets",
        "WidgetService",
        "PhoneExperienceHost",
        "RuntimeBroker",
        "dllhost",
        "taskhostw",
        "SecurityHealthSystray",
    };

    public static bool CanClose(string name) =>
        !ProcessControl.IsProtected(name) && !Shell.Contains(name);

    /// <summary>Apps with a window or a large footprint, biggest first, never system processes.</summary>
    public static IReadOnlyList<BoosterApp> ListApps()
    {
        var self = Environment.ProcessId;
        var apps = new List<BoosterApp>();
        foreach (var group in Process.GetProcesses().GroupBy(p => p.ProcessName))
        {
            try
            {
                if (!CanClose(group.Key) || group.Any(p => p.Id == self))
                    continue;

                var memory = group.Sum(SafeMemory);
                var hasWindow = group.Any(p => SafeHandle(p) != IntPtr.Zero);
                if (!hasWindow && memory < 150L * 1024 * 1024)
                    continue;

                apps.Add(
                    new BoosterApp(
                        group.Key,
                        group.Select(SafePath).FirstOrDefault(p => p is not null),
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

        var closed = new List<string>();
        foreach (var name in appNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!CanClose(name))
                continue;
            var path = CloseApp(name);
            if (path is not null)
                closed.Add(path);
        }

        return new BoostResult(
            new BoostSession(changed ? previous : null, closed),
            changed,
            closed.Count
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

    public static Guid? ActivePowerPlan() => ParsePowerPlan(RunPowercfg("/getactivescheme").Output);

    /// <summary>Reads the GUID from <c>powercfg /getactivescheme</c>, whose text is localized.</summary>
    internal static Guid? ParsePowerPlan(string output) =>
        GuidPattern().Match(output) is { Success: true } m ? Guid.Parse(m.Value) : null;

    private static bool SetPowerPlan(Guid plan) => RunPowercfg($"/setactive {plan}").Code == 0;

    /// <summary>Asks every window of the app to close, then ends what is left. Returns its exe.</summary>
    private static string? CloseApp(string name)
    {
        var processes = Process.GetProcessesByName(name);
        if (processes.Length == 0)
            return null;

        var path = processes.Select(SafePath).FirstOrDefault(p => p is not null);
        try
        {
            foreach (var p in processes)
            {
                try
                {
                    p.CloseMainWindow();
                }
                catch (InvalidOperationException) { }
            }

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
        return path;
    }

    private static long SafeMemory(Process p)
    {
        try
        {
            return p.WorkingSet64;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static IntPtr SafeHandle(Process p)
    {
        try
        {
            return p.MainWindowHandle;
        }
        catch (InvalidOperationException)
        {
            return IntPtr.Zero;
        }
    }

    private static string? SafePath(Process p)
    {
        try
        {
            return p.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static (int Code, string Output) RunPowercfg(string arguments)
    {
        var info = new ProcessStartInfo("powercfg.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (Win32Exception)
        {
            return (-1, "");
        }
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}
