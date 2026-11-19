using System.Diagnostics;
using System.Windows.Threading;
using Trevunix.Core.Cleanup;
using Trevunix.Core.Maintenance;
using Trevunix.Core.Monitoring;
using Trevunix.Core.Settings;
using Trevunix.Localization;

namespace Trevunix.Services;

/// <summary>
///     Work done while Trevunix sits in the tray: cleaning a browser's cache once it is closed,
///     and a daily look for app updates and driver problems that ends in a tray notification.
/// </summary>
public sealed class BackgroundChecks
{
    private static readonly TimeSpan DailyEvery = TimeSpan.FromHours(24);

    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly Action<string, string> _notify;

    /// <summary>Raised on the UI thread when a check found the apps with updates.</summary>
    public event Action<IReadOnlyList<AppUpdate>>? AppUpdatesFound;
    private readonly BrowserCloseWatch _browsers = new();
    private readonly DispatcherTimer _browserTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    // The first look comes a minute after start, so a stale list is refreshed soon.
    private readonly DispatcherTimer _dailyTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _busy;

    public BackgroundChecks(
        AppSettings settings,
        SettingsStore store,
        Action<string, string> notify
    )
    {
        _settings = settings;
        _store = store;
        _notify = notify;
        _browserTimer.Tick += async (_, _) => await CheckBrowsersAsync();
        _browserTimer.Start();
        _dailyTimer.Tick += async (_, _) =>
        {
            _dailyTimer.Interval = TimeSpan.FromMinutes(30);
            await DailyAsync();
        };
        _dailyTimer.Start();
    }

    private async Task CheckBrowsersAsync()
    {
        if (!_settings.CleanBrowsersOnClose || _busy)
            return;
        _busy = true;
        try
        {
            var closed = _browsers.Update(RunningNames());
            if (closed.Count == 0)
                return;

            var targets = CleanupCatalog.All.Where(t => closed.Contains(t.Id)).ToList();
            var freed = await Task.Run(() =>
            {
                var engine = new CleanupEngine();
                return engine.Clean([.. targets.Select(t => engine.Scan(t))]).FreedBytes;
            });
            if (freed > 0)
                new ActivityLog().Add(HistoryKind.Cleanup, freed);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DailyAsync()
    {
        if (
            !_settings.UpdateNotices
            || _settings.LastNoticeCheck is { } last
                && DateTime.Now - last < DailyEvery
                // Settings from 3.4.0 have a check time but no list yet.
                && _settings.AppUpdates is not null
        )
            return;

        var (apps, drivers) = await Task.Run(() =>
            (HealthScore.ListAppUpdates(), HealthScore.CountDriverProblems())
        );
        _settings.LastNoticeCheck = DateTime.Now;
        if (apps is not null)
        {
            _settings.AppUpdates = [.. apps];
            AppUpdatesFound?.Invoke(apps);
        }
        _store.TrySave(_settings);

        if (apps is { Count: > 0 })
            _notify(Tr.Get("Notice.AppUpdates.Title"), Tr.Format("Notice.AppUpdates", apps.Count));
        if (drivers > 0)
            _notify(Tr.Get("Notice.Drivers.Title"), Tr.Format("Notice.Drivers", drivers));
    }

    private static List<string> RunningNames()
    {
        var names = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            names.Add(process.ProcessName);
            process.Dispose();
        }
        return names;
    }
}
