using System.ComponentModel;
using System.Diagnostics;
using Trevunix.Core.Monitoring;

namespace Trevunix.Core.Performance;

/// <summary>
///     Lowers or restores process priority and picks which processes are safe to touch.
///     Windows processes the system cannot run without are never offered.
/// </summary>
public static class ProcessControl
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle",
        "System",
        "Registry",
        "Secure System",
        "Memory Compression",
        "smss",
        "csrss",
        "wininit",
        "winlogon",
        "services",
        "lsass",
        "lsaiso",
        "svchost",
        "fontdrvhost",
        "dwm",
        "explorer",
        "audiodg",
        "MsMpEng",
        "NisSrv",
        "SecurityHealthService",
        "sihost",
        "ctfmon",
        "conhost",
        "spoolsv",
        "WUDFHost",
        "Trevunix",
    };

    public static bool IsProtected(string processName) => Protected.Contains(processName);

    /// <summary>
    ///     Background processes busy enough to be worth slowing down: at least
    ///     <paramref name="minCpuPercent" />, not protected, not this app, busiest first.
    /// </summary>
    public static IReadOnlyList<ProcessSample> PickCpuHogs(
        IEnumerable<ProcessSample> samples,
        double minCpuPercent,
        int selfId
    ) =>
        samples
            .Where(s => s.Id != selfId && s.CpuPercent >= minCpuPercent && !IsProtected(s.Name))
            .OrderByDescending(s => s.CpuPercent)
            .ToList();

    /// <summary>Sets the priority and returns the one it had, or null when Windows refused.</summary>
    public static ProcessPriorityClass? SetPriority(int processId, ProcessPriorityClass priority)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var previous = process.PriorityClass;
            process.PriorityClass = priority;
            return previous;
        }
        catch (Exception ex)
            when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    public static ProcessPriorityClass? GetPriority(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.PriorityClass;
        }
        catch (Exception ex)
            when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}
