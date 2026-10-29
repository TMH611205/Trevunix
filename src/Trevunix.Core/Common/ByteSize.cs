using System.Globalization;

namespace Trevunix.Core.Common;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 1024)
            return string.Create(culture, $"{bytes} B");

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format =
            value >= 100 ? "0"
            : value >= 10 ? "0.0"
            : "0.00";
        return value.ToString(format, culture) + " " + Units[unit];
    }
}
