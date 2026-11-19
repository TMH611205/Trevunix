using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Common;
using Trevunix.Core.Maintenance;
using Trevunix.Core.Settings;
using Trevunix.Core.Updates;
using Trevunix.Localization;
using Trevunix.Services;

namespace Trevunix.ViewModels;

public sealed record Choice<T>(T Value, string Name);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly string _startupLanguage;

    public SettingsViewModel(AppSettings settings, SettingsStore store)
    {
        _settings = settings;
        _store = store;
        _startupLanguage = settings.Language;

        Languages = Tr.Languages.Select(l => new Choice<string>(l.Code, l.Name)).ToList();
        Themes =
        [
            new(AppTheme.System, Tr.Get("Theme.System")),
            new(AppTheme.Light, Tr.Get("Theme.Light")),
            new(AppTheme.Dark, Tr.Get("Theme.Dark")),
        ];

        _language = Languages.FirstOrDefault(l => l.Value == settings.Language) ?? Languages[0];
        _theme = Themes.First(t => t.Value == settings.Theme);
        _checkForUpdates = settings.CheckForUpdates;

        Frequencies =
        [
            .. Enum.GetValues<CleanFrequency>()
                .Select(f => new Choice<CleanFrequency>(f, Tr.Get($"AutoClean.{f}"))),
        ];
        _autoClean = Frequencies.First(f => f.Value == settings.AutoClean);
        _createRestorePoint = settings.CreateRestorePoint;
        _closeToTray = settings.CloseToTray;
        _startWithWindows = settings.StartWithWindows;
        _healthAlerts = settings.HealthAlerts;
        _cleanBrowsersOnClose = settings.CleanBrowsersOnClose;
        _updateNotices = settings.UpdateNotices;
        _autoCleanStatus = LastRunText();
    }

    public IReadOnlyList<Choice<CleanFrequency>> Frequencies { get; }

    public IReadOnlyList<Choice<string>> Languages { get; }
    public IReadOnlyList<Choice<AppTheme>> Themes { get; }

    public string Version => Tr.Format("Settings.Version", UpdateService.CurrentVersion);
    public string Copyright =>
        typeof(SettingsViewModel)
            .Assembly.GetCustomAttributes(
                typeof(System.Reflection.AssemblyCopyrightAttribute),
                false
            )
            .OfType<System.Reflection.AssemblyCopyrightAttribute>()
            .FirstOrDefault()
            ?.Copyright
        ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsRestart))]
    private Choice<string> _language;

    [ObservableProperty]
    private Choice<AppTheme> _theme;

    [ObservableProperty]
    private bool _checkForUpdates;

    [ObservableProperty]
    private Choice<CleanFrequency> _autoClean;

    [ObservableProperty]
    private string _autoCleanStatus;

    [ObservableProperty]
    private bool _createRestorePoint;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private string _startupError = "";

    [ObservableProperty]
    private bool _healthAlerts;

    [ObservableProperty]
    private bool _cleanBrowsersOnClose;

    [ObservableProperty]
    private bool _updateNotices;

    partial void OnCleanBrowsersOnCloseChanged(bool value)
    {
        _settings.CleanBrowsersOnClose = value;
        Save();
    }

    partial void OnUpdateNoticesChanged(bool value)
    {
        _settings.UpdateNotices = value;
        Save();
    }

    public bool NeedsRestart => Language.Value != _startupLanguage;

    partial void OnAutoCleanChanged(Choice<CleanFrequency> value) => _ = ScheduleAsync(value.Value);

    partial void OnCreateRestorePointChanged(bool value)
    {
        _settings.CreateRestorePoint = value;
        Save();
    }

    partial void OnStartWithWindowsChanged(bool value) => _ = SetStartupAsync(value);

    private async Task SetStartupAsync(bool enabled)
    {
        if (enabled == _settings.StartWithWindows || Environment.ProcessPath is not { } exe)
            return;

        StartupError = "";
        if (await Task.Run(() => LaunchAtLogon.Set(exe, enabled)))
        {
            _settings.StartWithWindows = enabled;
            Save();
        }
        else
        {
            StartupError = Tr.Get("Settings.StartWithWindows.Failed");
            StartWithWindows = _settings.StartWithWindows;
        }
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        _settings.CloseToTray = value;
        Save();
    }

    partial void OnHealthAlertsChanged(bool value)
    {
        _settings.HealthAlerts = value;
        Save();
    }

    private async Task ScheduleAsync(CleanFrequency frequency)
    {
        if (Environment.ProcessPath is not { } exe)
            return;

        AutoCleanStatus = Tr.Get("AutoClean.Saving");
        var ok = await Task.Run(() => AutoCleanSchedule.Register(exe, frequency));
        if (ok)
        {
            _settings.AutoClean = frequency;
            Save();
            AutoCleanStatus = LastRunText();
        }
        else
        {
            AutoCleanStatus = Tr.Get("AutoClean.Failed");
        }
    }

    private string LastRunText() =>
        AutoCleanSchedule.LastRun() is { } run
            ? Tr.Format(
                "AutoClean.LastRun",
                run.FinishedAt.ToString("g", Tr.Culture),
                ByteSize.Format(run.FreedBytes, Tr.Culture)
            )
        : _settings.AutoClean == CleanFrequency.Off ? Tr.Get("AutoClean.Description")
        : Tr.Get("AutoClean.NotRunYet");

    partial void OnLanguageChanged(Choice<string> value)
    {
        _settings.Language = value.Value;
        Save();
    }

    partial void OnThemeChanged(Choice<AppTheme> value)
    {
        _settings.Theme = value.Value;
        ThemeService.Apply(value.Value, Application.Current.MainWindow);
        Save();
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _settings.CheckForUpdates = value;
        Save();
    }

    public const string Author = "Bii";
    public const string Website = "https://github.com/TMH611205/Trevunix";

    /// <summary>"Made by Bii", also shown under the version in the sidebar.</summary>
    public static string Credit => Tr.Format("Settings.MadeBy", Author);

    public string MadeBy => Credit;

    [RelayCommand]
    private static void OpenWebsite() =>
        Process.Start(new ProcessStartInfo(Website) { UseShellExecute = true });

    [RelayCommand]
    private static void Restart() => ((App)Application.Current).Restart();

    private void Save() => _store.TrySave(_settings);
}
