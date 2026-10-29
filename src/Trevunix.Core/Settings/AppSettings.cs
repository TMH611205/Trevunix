using System.Text.Json;

namespace Trevunix.Core.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class AppSettings
{
    public string Language { get; set; } = "vi";
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Clean RAM automatically once this percent is in use; 0 turns it off.</summary>
    public int AutoFreeRamThreshold { get; set; }

    /// <summary>Also drop the standby cache when cleaning RAM.</summary>
    public bool PurgeStandbyCache { get; set; } = true;

    /// <summary>Make a System Restore point before the first tweak of each session.</summary>
    public bool CreateRestorePoint { get; set; } = true;

    public Maintenance.CleanFrequency AutoClean { get; set; } = Maintenance.CleanFrequency.Off;

    /// <summary>Closing the window keeps Trevunix running in the notification area.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Notify when a drive is almost full, a disk reports trouble or the PC runs hot.</summary>
    public bool HealthAlerts { get; set; } = true;

    /// <summary>Start in the notification area when the owner signs in to Windows.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>The exe the scheduled tasks were last pointed at, so they are redone only after a move.</summary>
    public string? TasksExePath { get; set; }

    /// <summary>Apps the game booster closes, by process name, as last chosen.</summary>
    public List<string> BoosterApps { get; set; } = [];

    /// <summary>The "still running in the tray" tip is shown once.</summary>
    public bool TrayTipShown { get; set; }
}

public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Trevunix"
        );
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON; a broken file falls back to defaults.</summary>
public sealed class SettingsStore(string? filePath = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string FilePath { get; } =
        filePath ?? Path.Combine(AppPaths.DataDirectory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json)
                    ?? new AppSettings();
        }
        catch (JsonException) { }
        catch (IOException) { }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary><see cref="Save"/> for callers that can live with the change not being kept.</summary>
    public bool TrySave(AppSettings settings)
    {
        try
        {
            Save(settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not save settings: {0}", ex.Message);
            return false;
        }
    }
}
