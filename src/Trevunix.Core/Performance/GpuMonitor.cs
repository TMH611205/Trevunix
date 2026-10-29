using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Trevunix.Core.Performance;

public sealed record GpuProcessSample(
    int Id,
    string Name,
    double UsagePercent,
    long DedicatedBytes,
    string? Path
);

public sealed record GpuSample(
    double UsagePercent,
    long DedicatedBytes,
    IReadOnlyList<GpuProcessSample> Processes
);

/// <summary>
///     GPU use per process from the same Windows counters Task Manager reads ("GPU Engine" and
///     "GPU Process Memory"). Usage is measured between two calls, so the first one reports 0%.
///     Like Task Manager, a process's usage is its busiest engine type (3D, copy, video...).
/// </summary>
public sealed partial class GpuMonitor
{
    private Dictionary<string, CounterSample> _last = [];
    private readonly Dictionary<int, (string? Name, string? Path)> _names = [];

    public static bool IsAvailable => PerformanceCounterCategory.Exists("GPU Engine");

    public GpuSample Sample()
    {
        var now = ReadUtilization();
        // engine type -> pid -> summed percent
        var byType = new Dictionary<string, Dictionary<int, double>>();
        foreach (var (instance, sample) in now)
        {
            if (
                !_last.TryGetValue(instance, out var before)
                || ParseEngine(instance) is not { } engine
            )
                continue;

            var percent = CounterSample.Calculate(before, sample);
            if (percent <= 0 || double.IsNaN(percent))
                continue;

            if (!byType.TryGetValue(engine.Type, out var perPid))
                byType[engine.Type] = perPid = [];
            perPid[engine.ProcessId] = perPid.GetValueOrDefault(engine.ProcessId) + percent;
        }
        _last = now;

        var memory = ReadDedicatedMemory();
        var usage = Combine(byType);
        var total = byType.Count == 0 ? 0 : byType.Values.Max(p => p.Values.Sum());

        var ids = usage.Keys.Union(memory.Keys).Where(id => id > 0).ToHashSet();
        // Forget processes that ended so a reused id is looked up again.
        foreach (var gone in _names.Keys.Where(id => !ids.Contains(id)).ToList())
            _names.Remove(gone);
        var processes = new List<GpuProcessSample>();
        foreach (var id in ids)
        {
            if (!_names.TryGetValue(id, out var known))
                _names[id] = known = Describe(id);
            var (name, path) = known;
            if (name is null)
                continue;
            processes.Add(
                new GpuProcessSample(
                    id,
                    name,
                    Math.Min(100, usage.GetValueOrDefault(id)),
                    memory.GetValueOrDefault(id),
                    path
                )
            );
        }

        return new GpuSample(Math.Min(100, total), memory.Values.Sum(), processes);
    }

    /// <summary>Per process: the highest total over its engine types.</summary>
    internal static Dictionary<int, double> Combine(
        Dictionary<string, Dictionary<int, double>> byType
    )
    {
        var result = new Dictionary<int, double>();
        foreach (var perPid in byType.Values)
        foreach (var (pid, percent) in perPid)
            result[pid] = Math.Max(result.GetValueOrDefault(pid), percent);
        return result;
    }

    internal static (int ProcessId, string Type)? ParseEngine(string instance)
    {
        var m = EnginePattern().Match(instance);
        return m.Success ? (int.Parse(m.Groups[1].Value), m.Groups[2].Value) : null;
    }

    internal static int? ParseProcessId(string instance)
    {
        var m = PidPattern().Match(instance);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private static Dictionary<string, CounterSample> ReadUtilization()
    {
        var result = new Dictionary<string, CounterSample>();
        try
        {
            var data = new PerformanceCounterCategory("GPU Engine").ReadCategory();
            if (data["Utilization Percentage"] is { } counter)
                foreach (InstanceData instance in counter.Values)
                    result[instance.InstanceName] = instance.Sample;
        }
        catch (Exception ex)
            when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return result;
    }

    private static Dictionary<int, long> ReadDedicatedMemory()
    {
        var result = new Dictionary<int, long>();
        try
        {
            var data = new PerformanceCounterCategory("GPU Process Memory").ReadCategory();
            if (data["Dedicated Usage"] is { } counter)
                foreach (InstanceData instance in counter.Values)
                    if (ParseProcessId(instance.InstanceName) is { } pid)
                        result[pid] = result.GetValueOrDefault(pid) + instance.RawValue;
        }
        catch (Exception ex)
            when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return result;
    }

    private static (string? Name, string? Path) Describe(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex)
                when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return (process.ProcessName, path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    [GeneratedRegex(@"^pid_(\d+)_.*_engtype_(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EnginePattern();

    [GeneratedRegex(@"^pid_(\d+)_", RegexOptions.IgnoreCase)]
    private static partial Regex PidPattern();
}
