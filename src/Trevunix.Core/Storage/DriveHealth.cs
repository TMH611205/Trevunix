using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Trevunix.Core.Storage;

public enum HealthState
{
    Healthy,
    Warning,
    Unhealthy,
    Unknown,
}

public sealed record DiskHealth(
    string Name,
    string MediaType,
    string BusType,
    long SizeBytes,
    HealthState Health,
    int? TemperatureC,
    int? WearPercent,
    long? PowerOnHours,
    long? ReadErrors,
    long? WriteErrors
);

/// <summary>Physical disk health from the Windows storage stack (Get-PhysicalDisk and its reliability counters).</summary>
public static class DriveHealth
{
    private const string Script =
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8;"
        + "Get-PhysicalDisk | ForEach-Object { $r = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue;"
        + "[pscustomobject]@{ Name=$_.FriendlyName; Media=[string]$_.MediaType; Bus=[string]$_.BusType;"
        + "Size=[int64]$_.Size; Health=[string]$_.HealthStatus; Temp=$r.Temperature; Wear=$r.Wear;"
        + "Hours=$r.PowerOnHours; ReadErr=$r.ReadErrorsTotal; WriteErr=$r.WriteErrorsTotal } } | "
        + "ConvertTo-Json -Compress";

    public static async Task<IReadOnlyList<DiskHealth>> ReadAsync(CancellationToken ct = default)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", Script })
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return Parse(output);
    }

    internal static IReadOnlyList<DiskHealth> Parse(string json)
    {
        json = json.Trim();
        if (json.Length == 0)
            return [];

        using var doc = JsonDocument.Parse(json);
        var items =
            doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];

        return items
            .Select(i => new DiskHealth(
                Text(i, "Name"),
                Text(i, "Media"),
                Text(i, "Bus"),
                Number(i, "Size") ?? 0,
                ParseHealth(Text(i, "Health")),
                (int?)Number(i, "Temp") is { } t and > 0 ? t : null,
                (int?)Number(i, "Wear"),
                Number(i, "Hours"),
                Number(i, "ReadErr"),
                Number(i, "WriteErr")
            ))
            .ToList();
    }

    private static HealthState ParseHealth(string text) =>
        text switch
        {
            "Healthy" or "0" => HealthState.Healthy,
            "Warning" or "1" => HealthState.Warning,
            "Unhealthy" or "2" => HealthState.Unhealthy,
            _ => HealthState.Unknown,
        };

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => "",
            }
            : "";

    private static long? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n)
            ? n
            : null;
}
