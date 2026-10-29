using System.Globalization;
using Trevunix.Core.Common;

namespace Trevunix.Core.Tests;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(10 * 1024 * 1024, "10.0 MB")]
    [InlineData(250L * 1024 * 1024 * 1024, "250 GB")]
    public void Format_Invariant_UsesBinaryUnits(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Format_Vietnamese_UsesCommaDecimal()
    {
        Assert.Equal("1,50 KB", ByteSize.Format(1536, new CultureInfo("vi-VN")));
    }
}
