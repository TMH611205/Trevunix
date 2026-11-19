using Trevunix.Core.Network;
using Trevunix.Core.SystemInfo;

namespace Trevunix.Core.Tests;

public class SystemInfoTests
{
    [Theory]
    [InlineData(26, "DDR4")]
    [InlineData(34, "DDR5")]
    [InlineData(35, "LPDDR5")]
    [InlineData(0, "")]
    [InlineData(999, "")]
    public void MemoryTypeName_MapsSmbiosCodes(int code, string expected) =>
        Assert.Equal(expected, HardwareInfo.MemoryTypeName(code));

    [Theory]
    [InlineData("Samsung", "Samsung")]
    [InlineData("0x802C", "Micron")]
    [InlineData("0x00CE", "Samsung")]
    [InlineData("0x9801", "Kingston")]
    [InlineData("0xFFFF", "")]
    [InlineData(null, "")]
    [InlineData("To be filled by O.E.M.", "")]
    public void MemoryVendor_DecodesJedecCodes(string? raw, string expected) =>
        Assert.Equal(expected, HardwareInfo.MemoryVendor(raw));

    [Theory]
    [InlineData("  ASUS  ", "ASUS")]
    [InlineData("Default string", "")]
    [InlineData("System Product Name", "")]
    [InlineData(null, "")]
    public void Clean_DropsOemPlaceholders(string? raw, string expected) =>
        Assert.Equal(expected, HardwareInfo.Clean(raw));

    [Fact]
    public void SpeedMath_EdgeCases()
    {
        Assert.Equal(0, SpeedMath.Median([]));
        Assert.Equal(0, SpeedMath.Jitter([42]));
        Assert.Equal(0, SpeedMath.ToMbps(1000, 0));
        Assert.Equal(8, SpeedMath.ToMbps(1_000_000, 1));
        Assert.Equal(0, SpeedMath.SteadyMbps([(1, 100)], warmupSeconds: 0));
        Assert.False(SpeedMath.IsSteady([]));
    }
}

public class SystemReportTests
{
    [Fact]
    public void Build_ListsHardwareAndDisks()
    {
        var hw = new HardwareInfo(
            "PC1",
            TimeSpan.FromHours(26.5),
            new OsInfo("Windows 11 Pro", "10.0.26200", "64-bit", new DateTime(2025, 1, 2)),
            new BoardInfo("ASUS", "PRIME B660", "", "AMI", "1.0", "2024-01-01"),
            new CpuInfo("Intel Core i5", 6, 12, 4400, "LGA1700"),
            new MemoryInfo(
                16L << 30,
                4,
                [new MemoryModule("DIMM1", 16L << 30, 3200, "DDR4", "Kingston", "KF432")]
            ),
            [new GpuInfo("RTX 3060", 12L << 30, "32.0")],
            []
        );
        var disks = new[]
        {
            new Trevunix.Core.Storage.DiskHealth(
                "Samsung 980",
                "SSD",
                "NVMe",
                1L << 40,
                Trevunix.Core.Storage.HealthState.Healthy,
                40,
                3,
                1200,
                0,
                0
            ),
        };

        var text = SystemReport.Build(
            hw,
            disks,
            [],
            "3.3.0.5",
            new DateTime(2026, 11, 19, 9, 0, 0)
        );

        Assert.Contains("Trevunix 3.3.0.5 system report, 2026-11-19 09:00", text);
        Assert.Contains("Windows 11 Pro 10.0.26200 (64-bit)", text);
        Assert.Contains("Uptime        1d 2h 30m", text);
        Assert.Contains("6 cores, 12 threads, 4400 MHz", text);
        Assert.Contains("1 of 4 slots", text);
        Assert.Contains("Samsung 980 (NVMe", text);
        Assert.Contains("Healthy, 40 C, wear 3%, 1200 h on", text);
    }
}
