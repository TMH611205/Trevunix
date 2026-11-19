using System.Globalization;
using Microsoft.Win32;
using Trevunix.Core.Tweaks;

namespace Trevunix.Core.Updates;

/// <summary>
///     Pauses Windows Update the way Settings > Windows Update does, through the same values, so
///     Settings shows the pause and its end date. Windows allows at most 35 days.
/// </summary>
public sealed class WindowsUpdatePause(string? keyPath = null)
{
    public const int MaxDays = 35;
    private const string Format = "yyyy-MM-ddTHH:mm:ssZ";

    private static readonly string[] StartNames =
    [
        "PauseFeatureUpdatesStartTime",
        "PauseQualityUpdatesStartTime",
        "PauseUpdatesStartTime",
    ];

    private static readonly string[] EndNames =
    [
        "PauseFeatureUpdatesEndTime",
        "PauseQualityUpdatesEndTime",
        "PauseUpdatesExpiryTime",
    ];

    private readonly string _keyPath =
        keyPath ?? @"HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    /// <summary>When the pause ends (UTC), or null when updates are not paused.</summary>
    public DateTime? PausedUntil(DateTime utcNow)
    {
        var (root, sub) = RegistryValueAction.Split(_keyPath);
        using var key = root.OpenSubKey(sub);
        if (
            key?.GetValue("PauseUpdatesExpiryTime") is string text
            && DateTime.TryParseExact(
                text,
                Format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var until
            )
            && until > utcNow
        )
            return until;
        return null;
    }

    public void Pause(int days, DateTime utcNow)
    {
        days = Math.Clamp(days, 1, MaxDays);
        var (root, sub) = RegistryValueAction.Split(_keyPath);
        using var key = root.CreateSubKey(sub, writable: true);
        var start = utcNow.ToString(Format, CultureInfo.InvariantCulture);
        var end = utcNow.AddDays(days).ToString(Format, CultureInfo.InvariantCulture);
        foreach (var name in StartNames)
            key.SetValue(name, start, RegistryValueKind.String);
        foreach (var name in EndNames)
            key.SetValue(name, end, RegistryValueKind.String);
    }

    public void Resume()
    {
        var (root, sub) = RegistryValueAction.Split(_keyPath);
        using var key = root.OpenSubKey(sub, writable: true);
        if (key is null)
            return;
        foreach (var name in StartNames.Concat(EndNames))
            key.DeleteValue(name, throwOnMissingValue: false);
    }
}
