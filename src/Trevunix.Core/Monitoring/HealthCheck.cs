using System.Management;
using Trevunix.Core.DiskUsage;
using Trevunix.Core.Storage;
using Trevunix.Core.SystemInfo;

namespace Trevunix.Core.Monitoring;

public enum HealthAlertKind
{
    DriveAlmostFull,
    DiskFailing,
    DiskHot,
    CpuHot,
}

/// <summary>One problem worth telling the owner about. <see cref="Key"/> identifies it for repeat suppression.</summary>
public sealed record HealthAlert(HealthAlertKind Kind, string Subject, long Value)
{
    public string Key => $"{Kind}:{Subject}";
}

/// <summary>Turns readings into alerts. Pure, so the thresholds are easy to test.</summary>
public static class HealthCheck
{
    /// <summary>A drive is almost full when 90% is used or less than 5 GB is left.</summary>
    public const double FullShare = 0.9;
    public const long MinFreeBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>Most SSDs start throttling around 70 °C.</summary>
    public const int DiskHotC = 70;

    /// <summary>Close to where desktop and laptop CPUs throttle.</summary>
    public const int CpuHotC = 90;

    public static IReadOnlyList<HealthAlert> Evaluate(
        IEnumerable<DriveSummary> drives,
        IEnumerable<DiskHealth> disks,
        int? cpuTemperatureC
    )
    {
        var alerts = new List<HealthAlert>();

        foreach (var drive in drives.Where(d => d.TotalBytes > 0))
        {
            // Small partitions (recovery, EFI) are nearly full by design; only warn past 20 GB.
            if (drive.TotalBytes < 4 * MinFreeBytes)
                continue;
            if (drive.UsedShare >= FullShare || drive.FreeBytes < MinFreeBytes)
                alerts.Add(
                    new HealthAlert(
                        HealthAlertKind.DriveAlmostFull,
                        drive.RootPath,
                        drive.FreeBytes
                    )
                );
        }

        foreach (var disk in disks)
        {
            if (disk.Health is HealthState.Warning or HealthState.Unhealthy)
                alerts.Add(new HealthAlert(HealthAlertKind.DiskFailing, disk.Name, 0));
            else if (disk.TemperatureC is { } t && t >= DiskHotC)
                alerts.Add(new HealthAlert(HealthAlertKind.DiskHot, disk.Name, t));
        }

        if (cpuTemperatureC is { } cpu && cpu >= CpuHotC)
            alerts.Add(new HealthAlert(HealthAlertKind.CpuHot, "CPU", cpu));

        return alerts;
    }

    /// <summary>
    ///     Highest ACPI thermal zone in °C, or null when the firmware does not report one. Many
    ///     PCs do not; the reading is the board's view of the CPU area, not a per-core sensor.
    /// </summary>
    public static int? ReadCpuTemperature()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"
            );
            int? highest = null;
            foreach (var zone in searcher.Get())
            {
                using (zone)
                {
                    // Tenths of a kelvin.
                    if (zone["CurrentTemperature"] is not uint raw)
                        continue;
                    var celsius = (int)Math.Round(raw / 10d - 273.15);
                    // Some boards report placeholder values; ignore anything implausible.
                    if (celsius is > 0 and < 125)
                        highest = Math.Max(highest ?? celsius, celsius);
                }
            }
            return highest;
        }
        catch (Exception ex) when (Wmi.IsUnavailable(ex))
        {
            return null;
        }
    }
}
