using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Trevunix.Core.Cleanup;
using Trevunix.Core.Common;
using Trevunix.Core.Maintenance;
using Trevunix.Core.Settings;
using Trevunix.Core.Updates;
using Trevunix.Localization;
using Trevunix.Services;
using Trevunix.ViewModels;
using Trevunix.Views;

namespace Trevunix;

public partial class App : Application
{
    private readonly SettingsStore _store = new();
    private AppSettings _settings = new();
    private TrayService? _tray;
    private BackgroundChecks? _background;
    private MainViewModel? _main;
    private SingleInstance? _instance;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                ErrorLog.Write(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            args.SetObserved();
        };

        _settings = _store.Load();
        Tr.SetLanguage(_settings.Language);

        if (e.Args.Contains(AutoCleanSchedule.Argument, StringComparer.OrdinalIgnoreCase))
        {
            _ = RunScheduledCleanupAsync();
            return;
        }

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            SingleInstance.ShowExisting();
            Shutdown();
            return;
        }
        _instance.Listen(() => Dispatcher.BeginInvoke(ShowWindow));

        _main = new MainViewModel(_settings, _store);
        var window = new MainWindow(_main);
        MainWindow = window;
        ThemeService.Apply(_settings.Theme, window);
        window.Closing += OnWindowClosing;

        // Started at sign-in: stay in the notification area unless there is no tray to stay in.
        var startHidden =
            e.Args.Contains(LaunchAtLogon.Argument, StringComparer.OrdinalIgnoreCase)
            && _settings.CloseToTray;
        if (!startHidden)
            window.Show();
        if (_store.LoadedDefaultsAfterError)
            _ = Dialogs.InfoAsync(
                Tr.Get("Settings.Reset.Title"),
                Tr.Format("Settings.Reset.Message", _store.BackupPath)
            );

        _tray = new TrayService();
        _tray.OpenRequested += ShowWindow;
        _tray.FreeRamRequested += () => _ = FreeRamFromTrayAsync();
        _tray.ExitRequested += ExitFromTray;
        _background = new BackgroundChecks(
            _settings,
            _store,
            (title, message) => _tray.Notify(title, message)
        );
        _background.AppUpdatesFound += _main.SetAppUpdates;
        _main.Health.Raised += (title, message) =>
        {
            if (_settings.HealthAlerts)
                _tray.Notify(title, message, warning: true);
        };

        // The exe may have moved since the scheduled tasks were made; point them here again.
        if (Environment.ProcessPath is { } path && path != _settings.TasksExePath)
        {
            var autoClean = _settings.AutoClean;
            var startWithWindows = _settings.StartWithWindows;
            _ = Task.Run(() =>
            {
                if (autoClean != CleanFrequency.Off)
                    AutoCleanSchedule.Register(path, autoClean);
                if (startWithWindows)
                    LaunchAtLogon.Set(path, true);
            });
            _settings.TasksExePath = path;
            _store.TrySave(_settings);
        }

#if !LOCAL_BUILD
        if (_settings.CheckForUpdates)
            _ = UpdateInBackgroundAsync();
#endif
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Booster.RestoreOnExit();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Starts a fresh Trevunix and closes this one, e.g. after a language change or update.</summary>
    public void Restart()
    {
        // Let go of the single-instance lock first, or the new process would just hand over to this one.
        _instance?.Dispose();
        _instance = null;
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        _exiting = true;
        Shutdown();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || !_settings.CloseToTray || _tray is null)
            return;

        e.Cancel = true;
        MainWindow.Hide();
        if (!_settings.TrayTipShown)
        {
            _tray.Notify(Tr.Get("Tray.StillRunning.Title"), Tr.Get("Tray.StillRunning.Message"));
            _settings.TrayTipShown = true;
            _store.TrySave(_settings);
        }
    }

    private void ShowWindow()
    {
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void ExitFromTray()
    {
        _exiting = true;
        MainWindow.Close();
    }

    private async Task FreeRamFromTrayAsync()
    {
        if (_main is null || _tray is null || !_main.Memory.CleanCommand.CanExecute(null))
            return;
        await _main.Memory.CleanCommand.ExecuteAsync(null);
        _tray.Notify(Tr.Get("Tray.FreeRam"), _main.Memory.LastResult);
    }

    /// <summary>
    ///     Started by Task Scheduler with <c>--auto-clean</c>: cleans the default targets with no
    ///     window, notes the result for the settings page and says what it freed.
    /// </summary>
    private async Task RunScheduledCleanupAsync()
    {
        try
        {
            var result = await QuickMaintenance.CleanAsync(new CleanupEngine());
            try
            {
                AutoCleanSchedule.SaveRun(new AutoCleanRun(DateTime.Now, result.FreedBytes));
            }
            catch (IOException) { }
            new ActivityLog().Add(HistoryKind.AutoClean, result.FreedBytes);

            if (result.FreedBytes >= 1024 * 1024)
            {
                using var tray = new TrayService(withMenu: false);
                tray.Notify(
                    Tr.Get("AutoClean.Done.Title"),
                    Tr.Format("AutoClean.Done", ByteSize.Format(result.FreedBytes, Tr.Culture))
                );
                // The notification disappears with the icon; leave it up long enough to read.
                await Task.Delay(TimeSpan.FromSeconds(8));
            }
        }
        finally
        {
            Shutdown();
        }
    }

    private async Task UpdateInBackgroundAsync()
    {
        if (!UpdateService.IsSelfUpdatable || Environment.ProcessPath is not { } exe)
            return;

        UpdateService.CleanupPreviousVersion(exe);

        var update = await UpdateService.CheckAsync();
        if (update is null || !await UpdateService.DownloadAndStageAsync(update, exe))
            return;

        var restart = await Dialogs.ConfirmAsync(
            Tr.Get("Update.Ready.Title"),
            Tr.Format("Update.Ready.Message", update.Version),
            Tr.Get("Update.Ready.Restart")
        );
        if (!restart)
            return;

        Restart();
    }

    /// <summary>Errors already shown this session; a timer that keeps failing is logged, not shown again.</summary>
    private static readonly HashSet<string> ShownErrors = [];

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ErrorLog.Write(e.Exception);

        if (ShownErrors.Add(e.Exception.GetType().FullName + e.Exception.Message))
            MessageBox.Show(
                e.Exception.Message,
                "Trevunix",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
    }
}
