using System.Collections.ObjectModel;
using System.Windows.Threading;
using Trevunix.Core.Common;
using Trevunix.Core.DiskUsage;
using Trevunix.Core.Monitoring;
using Trevunix.Core.Storage;
using Trevunix.Localization;

namespace Trevunix.Services;

/// <summary>
///     Checks drive space, disk health and temperature while Trevunix runs. <see cref="Active"/>
///     lists what is wrong now (the home page shows it); <see cref="Raised"/> fires for a new
///     problem, at most once per problem every few hours, so a full drive does not nag.
/// </summary>
public sealed class HealthWatcher
{
    private static readonly TimeSpan RepeatAfter = TimeSpan.FromHours(12);
    private static readonly TimeSpan DriveEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DiskEvery = TimeSpan.FromHours(6);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Dictionary<string, DateTime> _notified = [];
    private IReadOnlyList<DriveSummary> _drives = [];
    private IReadOnlyList<DiskHealth> _disks = [];
    private DateTime _drivesAt = DateTime.MinValue;
    private DateTime _disksAt = DateTime.MinValue;
    private bool _checking;

    public HealthWatcher()
    {
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        _ = CheckAsync();
    }

    public ObservableCollection<string> Active { get; } = [];

    /// <summary>Title and message of a problem that just appeared.</summary>
    public event Action<string, string>? Raised;

    private async Task CheckAsync()
    {
        if (_checking)
            return;
        _checking = true;
        try
        {
            var now = DateTime.Now;
            if (now - _drivesAt >= DriveEvery)
            {
                _drives = await Task.Run(DriveSummary.GetFixedDrives);
                _drivesAt = now;
            }
            if (now - _disksAt >= DiskEvery)
            {
                try
                {
                    _disks = await DriveHealth.ReadAsync();
                }
                catch (Exception ex)
                    when (ex
                            is System.Text.Json.JsonException
                                or System.ComponentModel.Win32Exception
                                or InvalidOperationException
                    )
                {
                    _disks = [];
                }
                _disksAt = now;
            }
            var cpu = await Task.Run(HealthCheck.ReadCpuTemperature);

            var alerts = HealthCheck.Evaluate(_drives, _disks, cpu);
            Active.Clear();
            foreach (var alert in alerts)
                Active.Add(Describe(alert));

            foreach (var alert in alerts)
            {
                if (_notified.TryGetValue(alert.Key, out var last) && now - last < RepeatAfter)
                    continue;
                _notified[alert.Key] = now;
                Raised?.Invoke(Tr.Get($"Health.{alert.Kind}.Title"), Describe(alert));
            }
        }
        catch (Exception ex)
        {
            // A background check runs every minute; an error box each time would make the app
            // unusable, and the alerts are a nice-to-have. Note it and try again next tick.
            System.Diagnostics.Trace.TraceWarning("Health check failed: {0}", ex);
        }
        finally
        {
            _checking = false;
        }
    }

    private static string Describe(HealthAlert alert) =>
        alert.Kind switch
        {
            HealthAlertKind.DriveAlmostFull => Tr.Format(
                "Health.DriveAlmostFull",
                alert.Subject.TrimEnd('\\'),
                ByteSize.Format(alert.Value, Tr.Culture)
            ),
            _ => Tr.Format($"Health.{alert.Kind}", alert.Subject, alert.Value),
        };
}
