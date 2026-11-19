using System.Globalization;
using System.Text;
using Trevunix.Core.Common;
using Trevunix.Core.Storage;

namespace Trevunix.Core.SystemInfo;

/// <summary>
///     A plain-text summary of the PC to paste into a help request. Always English, so whoever
///     helps can read it, and free of personal data beyond the computer name.
/// </summary>
public static class SystemReport
{
    public static string Build(
        HardwareInfo hw,
        IReadOnlyList<DiskHealth> disks,
        IEnumerable<DriveInfo> drives,
        string appVersion,
        DateTime now
    )
    {
        var c = CultureInfo.InvariantCulture;
        string Size(long bytes) => ByteSize.Format(bytes, c);

        var sb = new StringBuilder();
        void Line(string label, string value) => sb.AppendLine($"{label, -14}{value}");
        void Head(string title) => sb.AppendLine().AppendLine($"== {title} ==");

        sb.AppendLine(
            $"Trevunix {appVersion} system report, {now.ToString("yyyy-MM-dd HH:mm", c)}"
        );

        Head("System");
        Line("Computer", hw.ComputerName);
        Line("Windows", $"{hw.Os.Name} {hw.Os.Version} ({hw.Os.Architecture})");
        if (hw.Os.InstalledOn is { } installed)
            Line("Installed", installed.ToString("yyyy-MM-dd", c));
        Line("Uptime", $"{(int)hw.Uptime.TotalDays}d {hw.Uptime.Hours}h {hw.Uptime.Minutes}m");

        Head("Board");
        Line("Model", $"{hw.Board.Manufacturer} {hw.Board.Product} {hw.Board.Version}".Trim());
        Line("BIOS", $"{hw.Board.BiosVendor} {hw.Board.BiosVersion} {hw.Board.BiosDate}".Trim());

        Head("CPU");
        Line("Name", hw.Cpu.Name);
        Line("Cores", $"{hw.Cpu.Cores} cores, {hw.Cpu.Threads} threads, {hw.Cpu.MaxClockMhz} MHz");

        Head("Memory");
        Line(
            "Total",
            $"{Size(hw.Memory.TotalBytes)} in {hw.Memory.Modules.Count} of {hw.Memory.SlotCount} slots"
        );
        // Slot names can be long ("Controller0-ChannelA-DIMM1"), so they get their own indent.
        foreach (var m in hw.Memory.Modules)
            sb.AppendLine(
                $"  {m.Slot}: {Size(m.CapacityBytes)} {m.Type} {m.SpeedMhz} MHz {m.Manufacturer} {m.PartNumber}".TrimEnd()
            );

        Head("Graphics");
        foreach (var g in hw.Gpus)
            Line("GPU", $"{g.Name}, {Size(g.MemoryBytes)}, driver {g.DriverVersion}");

        Head("Disks");
        foreach (var d in disks)
        {
            var extra = new List<string> { d.Health.ToString() };
            if (d.TemperatureC is { } t)
                extra.Add($"{t} C");
            if (d.WearPercent is { } w)
                extra.Add($"wear {w}%");
            if (d.PowerOnHours is { } h)
                extra.Add($"{h} h on");
            Line(
                d.MediaType,
                $"{d.Name} ({d.BusType}, {Size(d.SizeBytes)}): {string.Join(", ", extra)}"
            );
        }

        Head("Drives");
        foreach (var drive in drives)
            Line(
                drive.Name,
                $"{drive.DriveFormat}, {Size(drive.AvailableFreeSpace)} free of {Size(drive.TotalSize)}"
            );

        return sb.ToString();
    }

    /// <summary>Fixed drives that are ready; others throw when asked for their size.</summary>
    public static IEnumerable<DriveInfo> ReadyFixedDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady);
}
