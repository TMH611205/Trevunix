using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Trevunix.Core.SystemInfo;
using Trevunix.Core.Tools;

namespace Trevunix.Core.Recovery;

public sealed record RestorePointInfo(int Sequence, string Description, DateTime Created);

/// <summary>Lists, makes and removes System Restore points, and sets how much space they may use.</summary>
public static class RestorePoints
{
    private const string FrequencyKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyValue = "SystemRestorePointCreationFrequency";

    public static IReadOnlyList<RestorePointInfo> List() =>
        [
            .. Wmi.Query(
                    "SELECT SequenceNumber, Description, CreationTime FROM SystemRestore",
                    @"\\.\root\default"
                )
                .Select(p => new RestorePointInfo(
                    Convert.ToInt32(p["SequenceNumber"]),
                    p["Description"] as string ?? "",
                    p["CreationTime"] is string dmtf
                        ? ManagementDateTimeConverter.ToDateTime(dmtf)
                        : DateTime.MinValue
                ))
                .OrderByDescending(p => p.Created),
        ];

    /// <summary>
    ///     Makes a point now, even when Windows made one in the last 24 hours: the once-a-day
    ///     limit is lifted for this call and put back afterwards.
    /// </summary>
    public static bool CreateNow(string description)
    {
        using var key = Registry.LocalMachine.CreateSubKey(FrequencyKey, writable: true);
        var before = key.GetValue(FrequencyValue);
        key.SetValue(FrequencyValue, 0, RegistryValueKind.DWord);
        try
        {
            return RestorePoint.Create(description);
        }
        finally
        {
            if (before is null)
                key.DeleteValue(FrequencyValue, throwOnMissingValue: false);
            else
                key.SetValue(FrequencyValue, before, RegistryValueKind.DWord);
        }
    }

    public static bool Delete(int sequence) => SRRemoveRestorePoint(sequence) == 0;

    /// <summary>Caps restore point storage on the system drive at a share of its size.</summary>
    public static bool SetMaxPercent(int percent)
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        return CommandRunner
                .Run(
                    "vssadmin.exe",
                    "resize",
                    "shadowstorage",
                    $"/For={drive}",
                    $"/On={drive}",
                    $"/MaxSize={Math.Clamp(percent, 1, 100)}%"
                )
                .Code == 0;
    }

    [DllImport("srclient.dll")]
    private static extern int SRRemoveRestorePoint(int dwRPNum);
}
