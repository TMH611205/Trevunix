using System.Globalization;
using System.Xml.Linq;

namespace Trevunix.Core.Startup;

public enum StartupImpactLevel
{
    NotMeasured,
    Low,
    Medium,
    High,
}

/// <summary>
///     How much each startup program slows sign-in, from the measurements Windows keeps for Task
///     Manager in <c>System32\wdi\LogFiles\StartupInfo</c>, rated with Task Manager's documented
///     limits: high above 1 s of CPU or 3 MB of disk, medium above 300 ms or 300 KB.
/// </summary>
public static class StartupImpact
{
    public static StartupImpactLevel Classify(long cpuMicroseconds, long diskBytes) =>
        cpuMicroseconds > 1_000_000 || diskBytes > 3 * 1024 * 1024 ? StartupImpactLevel.High
        : cpuMicroseconds > 300_000 || diskBytes > 300 * 1024 ? StartupImpactLevel.Medium
        : StartupImpactLevel.Low;

    /// <summary>Impact per exe path (case-insensitive), from the newest log of the current user.</summary>
    public static IReadOnlyDictionary<string, StartupImpactLevel> Read(string? folder = null)
    {
        folder ??= Path.Combine(Environment.SystemDirectory, @"wdi\LogFiles\StartupInfo");
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        try
        {
            var newest = new DirectoryInfo(folder)
                .EnumerateFiles($"{sid}_StartupInfo*.xml")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return newest is null ? Empty() : Parse(XDocument.Load(newest.FullName));
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return Empty();
        }
    }

    internal static Dictionary<string, StartupImpactLevel> Parse(XDocument doc)
    {
        var totals = new Dictionary<string, (long Cpu, long Disk)>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var process in doc.Descendants("Process"))
        {
            if ((string?)process.Attribute("Name") is not { Length: > 0 } name)
                continue;
            totals.TryGetValue(name, out var sum);
            totals[name] = (
                sum.Cpu + Number(process, "CpuUsage"),
                sum.Disk + Number(process, "DiskUsage")
            );
        }
        return totals.ToDictionary(
            t => t.Key,
            t => Classify(t.Value.Cpu, t.Value.Disk),
            StringComparer.OrdinalIgnoreCase
        );
    }

    private static long Number(XElement process, string name) =>
        long.TryParse(
            (string?)process.Element(name),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value
        )
            ? value
            : 0;

    private static Dictionary<string, StartupImpactLevel> Empty() =>
        new(StringComparer.OrdinalIgnoreCase);
}
