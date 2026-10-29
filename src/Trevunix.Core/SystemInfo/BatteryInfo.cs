using System.Management;

namespace Trevunix.Core.SystemInfo;

/// <summary>One laptop battery. Capacities are in mWh; null when the firmware does not say.</summary>
public sealed record BatteryInfo(
    string Name,
    string Manufacturer,
    long? DesignCapacity,
    long? FullChargeCapacity,
    int? CycleCount,
    int? ChargePercent,
    bool PluggedIn,
    bool Charging
)
{
    /// <summary>How much of its original capacity the battery still holds, in percent.</summary>
    public int? HealthPercent => Health(DesignCapacity, FullChargeCapacity);

    /// <summary>Capped at 100: a new battery often reports a little over its design capacity.</summary>
    public static int? Health(long? design, long? full) =>
        design is > 0 && full is > 0
            ? (int)Math.Min(100, Math.Round(100d * full.Value / design.Value))
            : null;

    /// <summary>
    ///     Batteries from the ACPI battery driver's WMI classes, the same source as
    ///     <c>powercfg /batteryreport</c>. Empty on a desktop.
    /// </summary>
    public static IReadOnlyList<BatteryInfo> Read()
    {
        var statics = Query("BatteryStaticData");
        if (statics.Count == 0)
            return [];

        var full = Query("BatteryFullChargedCapacity");
        var cycles = Query("BatteryCycleCount");
        var status = Query("BatteryStatus");

        var result = new List<BatteryInfo>();
        foreach (var (tag, s) in statics)
        {
            full.TryGetValue(tag, out var f);
            cycles.TryGetValue(tag, out var c);
            status.TryGetValue(tag, out var st);

            var design = Long(s, "DesignedCapacity");
            var fullCapacity = Long(f, "FullChargedCapacity");
            var remaining = Long(st, "RemainingCapacity");
            int? charge =
                fullCapacity is > 0 && remaining is { } r
                    ? (int)Math.Clamp(Math.Round(100d * r / fullCapacity.Value), 0, 100)
                    : null;
            var cycleCount = Long(c, "CycleCount");

            result.Add(
                new BatteryInfo(
                    Text(s, "DeviceName"),
                    Text(s, "ManufactureName"),
                    design,
                    fullCapacity,
                    // Many batteries report 0 when they do not count cycles.
                    cycleCount is > 0
                        ? (int)cycleCount
                        : null,
                    charge,
                    Bool(st, "PowerOnline"),
                    Bool(st, "Charging")
                )
            );
        }
        return result;
    }

    /// <summary>Instances of a root\WMI battery class, keyed by InstanceName so the classes line up.</summary>
    private static Dictionary<string, Dictionary<string, object?>> Query(string className)
    {
        var result = new Dictionary<string, Dictionary<string, object?>>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                $"SELECT * FROM {className}"
            );
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var values = item
                        .Properties.Cast<PropertyData>()
                        .ToDictionary(p => p.Name, p => (object?)p.Value);
                    if (values.TryGetValue("InstanceName", out var tag) && tag is string key)
                        result[key] = values;
                }
            }
        }
        catch (Exception ex) when (Wmi.IsUnavailable(ex)) { }
        return result;
    }

    private static long? Long(Dictionary<string, object?>? values, string name) =>
        values is not null && values.TryGetValue(name, out var v) && v is not null
            ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static string Text(Dictionary<string, object?>? values, string name) =>
        values is not null && values.TryGetValue(name, out var v) ? (v as string ?? "").Trim() : "";

    private static bool Bool(Dictionary<string, object?>? values, string name) =>
        values is not null && values.TryGetValue(name, out var v) && v is true;
}
