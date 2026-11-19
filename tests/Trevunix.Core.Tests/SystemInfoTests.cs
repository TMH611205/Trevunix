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
