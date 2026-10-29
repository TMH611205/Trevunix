using Microsoft.Win32;

namespace Trevunix.Core.SystemInfo;

public sealed record MachineInfo(
    string ComputerName,
    string WindowsName,
    string WindowsVersion,
    string Processor,
    int LogicalCores,
    long MemoryBytes,
    TimeSpan Uptime
)
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public static MachineInfo Read()
    {
        using var nt = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
        var build = Environment.OSVersion.Version.Build;
        var product = nt?.GetValue("ProductName") as string ?? "Windows";
        // ProductName still says "Windows 10" on Windows 11 builds.
        if (build >= 22000)
            product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);

        var display = nt?.GetValue("DisplayVersion") as string ?? "";
        var ubr = nt?.GetValue("UBR") is int u ? u : 0;
        var version = string.IsNullOrEmpty(display)
            ? $"{build}.{ubr}"
            : $"{display} ({build}.{ubr})";

        using var cpu = Registry.LocalMachine.OpenSubKey(CpuKey);
        var cpuName = (cpu?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";

        return new MachineInfo(
            Environment.MachineName,
            product,
            version,
            cpuName,
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            TimeSpan.FromMilliseconds(Environment.TickCount64)
        );
    }
}
