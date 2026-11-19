using System.Globalization;
using System.Text;

namespace Trevunix.Core.Apps;

/// <summary>The installed programs as CSV, e.g. to keep a record before reinstalling Windows.</summary>
public static class ProgramListExport
{
    public static string ToCsv(IEnumerable<InstalledProgram> programs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name,Publisher,Version,Size (MB),Installed,Location");
        foreach (
            var p in programs.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        )
        {
            sb.AppendJoin(
                    ',',
                    Field(p.DisplayName),
                    Field(p.Publisher),
                    Field(p.Version),
                    p.SizeBytes > 0
                        ? (p.SizeBytes / 1024d / 1024d).ToString(
                            "0.#",
                            CultureInfo.InvariantCulture
                        )
                        : "",
                    p.InstalledOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                    Field(p.InstallLocation)
                )
                .AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Quotes a field when it holds a comma, quote or line break (RFC 4180).</summary>
    internal static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value.IndexOfAny([',', '"', '\n', '\r']) < 0
            ? value
            : "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
