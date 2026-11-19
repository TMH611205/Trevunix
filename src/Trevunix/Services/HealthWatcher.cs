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
    private DateTime _disksAt;
    private bool _cpuReported = true;
    private bool _checking;

    public HealthWatcher()
    {
        // The disk check starts PowerShell; leave it until a minute after start-up.
        _disksAt = DateTime.Now - DiskEvery + TimeSpan.FromMinutes(1);
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        _ = CheckAsync();
    }

    public ObservableCollection<string> Active { get; } = [];

    /// <summary>The last disk health reading; empty until the first check has run.</summary>
    public IReadOnlyList<DiskHealth> Disks => _disks;

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
                    _disks = await Task.Run(() => DriveHealth.ReadAsync());
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
            // Most PCs never report a CPU temperature; stop asking after the first empty answer.
            int? cpu = null;
            if (_cpuReported)
            {
                cpu = await Task.Run(HealthCheck.ReadCpuTemperature);
                _cpuReported = cpu is not null;
            }

            var alerts = HealthCheck.Evaluate(_drives, _disks, cpu);
            var messages = alerts.Select(Describe).ToList();
            if (!messages.SequenceEqual(Active))
            {
                Active.Clear();
                foreach (var message in messages)
                    Active.Add(message);
            }

            for (var i = 0; i < alerts.Count; i++)
            {
                var key = alerts[i].Key;
                if (_notified.TryGetValue(key, out var last) && now - last < RepeatAfter)
                    continue;
                _notified[key] = now;
                Raised?.Invoke(Tr.Get($"Health.{alerts[i].Kind}.Title"), messages[i]);
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
