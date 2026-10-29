using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Network;
using Trevunix.Localization;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

public sealed partial class SpeedTestViewModel()
    : PageSection("SpeedTest", SymbolRegular.TopSpeed24)
{
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _phaseText = "";

    [ObservableProperty]
    private string _liveText = "0";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _ping = "–";

    [ObservableProperty]
    private string _jitter = "–";

    [ObservableProperty]
    private string _download = "–";

    [ObservableProperty]
    private string _upload = "–";

    [ObservableProperty]
    private string _serverText = "";

    [ObservableProperty]
    private string _error = "";

    private bool CanStart => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Error = "";
        Ping = Jitter = Download = Upload = "–";
        ServerText = "";
        LiveText = "0";
        Progress = 0;
        PhaseText = Tr.Get("Speed.Phase.Connecting");

        var progress = new Progress<SpeedProgress>(OnProgress);
        try
        {
            SpeedTestResult result;
            try
            {
                using var test = new SpeedTest();
                result = await Task.Run(() => test.RunAsync(progress, _cts.Token));
            }
            catch (SpeedTestLimitedException)
            {
                // The server throttles per address; the IPv4 address of the same line is
                // usually still allowed. If it is throttled too, the outer catch reports it.
                using var test = new SpeedTest(ipv4Only: true);
                result = await Task.Run(() => test.RunAsync(progress, _cts.Token));
            }
            Ping = Ms(result.PingMs);
            Jitter = Ms(result.JitterMs);
            Download = Mbps(result.DownloadMbps);
            Upload = Mbps(result.UploadMbps);
            LiveText = Mbps(result.DownloadMbps);
            Badge = Download + " Mbps";
            if (result.Server is { } s)
                ServerText = Tr.Format("Speed.Server", s.City, s.Colo, s.Asn, s.ClientIp);
            PhaseText = Tr.Get("Speed.Phase.Done");
            Progress = 100;
        }
        catch (OperationCanceledException)
        {
            PhaseText = Tr.Get("Speed.Phase.Cancelled");
        }
        catch (SpeedTestLimitedException)
        {
            PhaseText = "";
            Error = Tr.Get("Speed.Limited");
        }
        catch (HttpRequestException ex)
        {
            PhaseText = "";
            Error = Tr.Format("Speed.Failed", ex.Message);
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private void OnProgress(SpeedProgress p)
    {
        // Ping takes ~10% of the bar, download and upload ~45% each.
        var (start, span) = p.Phase switch
        {
            SpeedPhase.Latency => (0d, 10d),
            SpeedPhase.Download => (10d, 45d),
            _ => (55d, 45d),
        };
        Progress = start + span * Math.Clamp(p.Fraction, 0, 1);
        PhaseText = Tr.Get($"Speed.Phase.{p.Phase}");
        if (p.Phase != SpeedPhase.Latency)
            LiveText = Mbps(p.Mbps);
    }

    private static string Mbps(double value) =>
        value.ToString(value >= 100 ? "0" : "0.0", Tr.Culture);

    private static string Ms(double value) => value.ToString("0", Tr.Culture) + " ms";
}
