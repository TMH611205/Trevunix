using System.Management;
using Microsoft.Win32;

namespace Trevunix.Core.SystemInfo;

public sealed record BoardInfo(
    string Manufacturer,
    string Product,
    string Version,
    string BiosVendor,
    string BiosVersion,
    string BiosDate
);

public sealed record CpuInfo(string Name, int Cores, int Threads, int MaxClockMhz, string Socket);

public sealed record MemoryModule(
    string Slot,
    long CapacityBytes,
    int SpeedMhz,
    string Type,
    string Manufacturer,
    string PartNumber
);

public sealed record MemoryInfo(
    long TotalBytes,
    int SlotCount,
    IReadOnlyList<MemoryModule> Modules
);

public sealed record GpuInfo(string Name, long MemoryBytes, string DriverVersion);

public sealed record DiskInfo(string Model, long SizeBytes, string Interface);

public sealed record OsInfo(
    string Name,
    string Version,
    string Architecture,
    DateTime? InstalledOn
);

public sealed record HardwareInfo(
    string ComputerName,
    TimeSpan Uptime,
    OsInfo Os,
    BoardInfo Board,
    CpuInfo Cpu,
    MemoryInfo Memory,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<DiskInfo> Disks
)
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string DisplayClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Reads everything; slow parts (WMI) take about a second, so call it off the UI thread.</summary>
    public static HardwareInfo Read()
    {
        var machine = MachineInfo.Read();
        return new HardwareInfo(
            machine.ComputerName,
            machine.Uptime,
            ReadOs(machine),
            ReadBoard(),
            ReadCpu(machine),
            ReadMemory(machine.MemoryBytes),
            ReadGpus(),
            ReadDisks()
        );
    }

    /// <summary>SMBIOS memory type codes to names.</summary>
    internal static string MemoryTypeName(int smbiosType) =>
        smbiosType switch
        {
            20 => "DDR",
            21 => "DDR2",
            24 => "DDR3",
            26 => "DDR4",
            27 => "LPDDR",
            28 => "LPDDR2",
            29 => "LPDDR3",
            30 => "LPDDR4",
            34 => "DDR5",
            35 => "LPDDR5",
            _ => "",
        };

    private static readonly Dictionary<string, string> JedecVendors = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["2C"] = "Micron",
        ["802C"] = "Micron",
        ["CE"] = "Samsung",
        ["80CE"] = "Samsung",
        ["AD"] = "SK hynix",
        ["80AD"] = "SK hynix",
        ["9801"] = "Kingston",
        ["0198"] = "Kingston",
        ["04CD"] = "G.Skill",
        ["CD04"] = "G.Skill",
        ["029E"] = "Corsair",
        ["9E02"] = "Corsair",
        ["859B"] = "Crucial",
        ["9B85"] = "Crucial",
        ["0D9B"] = "Crucial",
        ["04EF"] = "Team Group",
        ["EF04"] = "Team Group",
        ["0B83"] = "ADATA",
        ["04CB"] = "ADATA",
        ["8551"] = "Qimonda",
    };

    /// <summary>Some boards report the module vendor as a JEDEC code such as "0x802C".</summary>
    internal static string MemoryVendor(string? raw)
    {
        var text = Clean(raw);
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return text;

        var code = text[2..].TrimStart('0');
        return
            JedecVendors.TryGetValue(text[2..], out var name)
            || JedecVendors.TryGetValue(code, out name)
            ? name
            : "";
    }

    /// <summary>OEM placeholders such as "To be filled by O.E.M." are treated as empty.</summary>
    internal static string Clean(string? value)
    {
        var text = value?.Trim() ?? "";
        return
            text.Contains("O.E.M", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            || text.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)
            ? ""
            : text;
    }

    private static OsInfo ReadOs(MachineInfo machine)
    {
        DateTime? installed = null;
        foreach (var os in Query("SELECT InstallDate FROM Win32_OperatingSystem"))
        {
            if (os["InstallDate"] is string dmtf && dmtf.Length >= 14)
                installed = ManagementDateTimeConverter.ToDateTime(dmtf);
        }

        return new OsInfo(
            machine.WindowsName,
            machine.WindowsVersion,
            Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit",
            installed
        );
    }

    private static BoardInfo ReadBoard()
    {
        using var key = Registry.LocalMachine.OpenSubKey(BiosKey);
        string Get(string name) => Clean(key?.GetValue(name) as string);
        return new BoardInfo(
            Get("BaseBoardManufacturer"),
            Get("BaseBoardProduct"),
            Get("BaseBoardVersion"),
            Get("BIOSVendor"),
            Get("BIOSVersion"),
            Get("BIOSReleaseDate")
        );
    }

    private static CpuInfo ReadCpu(MachineInfo machine)
    {
        foreach (
            var cpu in Query(
                "SELECT NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, SocketDesignation FROM Win32_Processor"
            )
        )
        {
            return new CpuInfo(
                machine.Processor,
                ToInt(cpu["NumberOfCores"]),
                ToInt(cpu["NumberOfLogicalProcessors"]),
                ToInt(cpu["MaxClockSpeed"]),
                Clean(cpu["SocketDesignation"] as string)
            );
        }

        return new CpuInfo(machine.Processor, 0, machine.LogicalCores, 0, "");
    }

    private static MemoryInfo ReadMemory(long visibleBytes)
    {
        var modules = Query(
                "SELECT DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Manufacturer, PartNumber FROM Win32_PhysicalMemory"
            )
            .Select(m => new MemoryModule(
                Clean(m["DeviceLocator"] as string),
                ToLong(m["Capacity"]),
                ToInt(m["ConfiguredClockSpeed"]) is > 0 and var configured
                    ? configured
                    : ToInt(m["Speed"]),
                MemoryTypeName(ToInt(m["SMBIOSMemoryType"])),
                MemoryVendor(m["Manufacturer"] as string),
                Clean(m["PartNumber"] as string)
            ))
            .ToList();

        var slots = Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray")
            .Sum(a => ToInt(a["MemoryDevices"]));

        var total = modules.Sum(m => m.CapacityBytes);
        return new MemoryInfo(total > 0 ? total : visibleBytes, slots, modules);
    }

    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        var gpus = new List<GpuInfo>();
        using var root = Registry.LocalMachine.OpenSubKey(DisplayClass);
        if (root is null)
            return gpus;

        foreach (var name in root.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            try
            {
                using var key = root.OpenSubKey(name);
                var desc = key?.GetValue("DriverDesc") as string;
                if (
                    string.IsNullOrWhiteSpace(desc)
                    || desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                )
                    continue;

                var memory = key!.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long q => q,
                    byte[] { Length: >= 8 } b => BitConverter.ToInt64(b, 0),
                    int i => (uint)i,
                    byte[] { Length: 4 } b => BitConverter.ToUInt32(b, 0),
                    _ => 0L,
                };
                if (memory == 0 && key.GetValue("HardwareInformation.MemorySize") is int legacy)
                    memory = (uint)legacy;

                gpus.Add(
                    new GpuInfo(desc.Trim(), memory, key.GetValue("DriverVersion") as string ?? "")
                );
            }
            catch (System.Security.SecurityException) { }
        }

        return gpus.DistinctBy(g => g.Name).ToList();
    }

    private static IReadOnlyList<DiskInfo> ReadDisks() =>
        Query("SELECT Model, Size, InterfaceType FROM Win32_DiskDrive")
            .Select(d => new DiskInfo(
                (d["Model"] as string ?? "").Trim(),
                ToLong(d["Size"]),
                (d["InterfaceType"] as string ?? "").Trim()
            ))
            .Where(d => d.Model.Length > 0)
            .ToList();

    private static List<ManagementBaseObject> Query(string wql) => Wmi.Query(wql);

    private static int ToInt(object? value) =>
        value switch
        {
            null => 0,
            uint u => (int)u,
            ushort s => s,
            int i => i,
            _ => int.TryParse(value.ToString(), out var n) ? n : 0,
        };

    private static long ToLong(object? value) =>
        value switch
        {
            null => 0,
            ulong u => (long)u,
            long l => l,
            uint u => u,
            _ => long.TryParse(value.ToString(), out var n) ? n : 0,
        };
}
