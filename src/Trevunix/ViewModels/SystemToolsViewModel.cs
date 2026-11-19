using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Common;
using Trevunix.Core.Monitoring;
using Trevunix.Core.Network;
using Trevunix.Core.Storage;
using Trevunix.Core.Tools;
using Trevunix.Localization;
using Trevunix.Services;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

/// <summary>Hosts the utility sections. Timers run only while the page is on screen.</summary>
public sealed partial class SystemToolsViewModel : ObservableObject
{
    public SystemToolsViewModel()
    {
        Sections =
        [
            SpeedTest,
            Monitor,
            Processes,
            Repair,
            AppUpdates,
            Drivers,
            Dns,
            Drives,
            Battery,
            Duplicates,
            Report,
            History,
        ];
        _selected = SpeedTest;
    }

    public SpeedTestViewModel SpeedTest { get; } = new();
    public MonitorViewModel Monitor { get; } = new();
    public ProcessesViewModel Processes { get; } = new();
    public RepairViewModel Repair { get; } = new();
    public RepairViewModel AppUpdates { get; } =
        new("AppUpdates", SymbolRegular.ArrowSync24, RepairCatalog.AppUpdates);
    public RepairViewModel Drivers { get; } =
        new("Drivers", SymbolRegular.Connector24, RepairCatalog.Drivers);
    public DuplicatesViewModel Duplicates { get; } = new();
    public ReportViewModel Report { get; } = new();
    public HistoryViewModel History { get; } = new();
    public DnsViewModel Dns { get; } = new();
    public DrivesViewModel Drives { get; } = new();
    public BatteryViewModel Battery { get; } = new();
    public IReadOnlyList<PageSection> Sections { get; }

    [ObservableProperty]
    private PageSection _selected;

    public void OnShown()
    {
        Monitor.Start();
        Processes.Start();
        Dns.Refresh();
        Battery.OnShown();
        History.Refresh();
    }

    public void OnHidden()
    {
        Monitor.Stop();
        Processes.Stop();
    }
}

public sealed partial class MonitorViewModel() : PageSection("Monitor", SymbolRegular.Pulse24)
{
    private const int HistoryLength = 60;
    private const double ChartWidth = 300;
    private const double ChartHeight = 80;

    private readonly SystemMonitor _monitor = new();
    private readonly Queue<double> _cpu = new();
    private readonly Queue<double> _memory = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private bool _hooked;

    [ObservableProperty]
    private double _cpuPercent;

    [ObservableProperty]
    private double _memoryPercent;

    [ObservableProperty]
    private string _cpuText = "";

    [ObservableProperty]
    private string _memoryText = "";

    [ObservableProperty]
    private string _networkText = "";

    [ObservableProperty]
    private PointCollection _cpuHistory = [];

    [ObservableProperty]
    private PointCollection _memoryHistory = [];

    public void Start()
    {
        if (!_hooked)
        {
            _hooked = true;
            _timer.Tick += (_, _) => Tick();
        }
        Tick();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Tick()
    {
        var s = _monitor.Sample();
        CpuPercent = s.CpuPercent;
        MemoryPercent = s.MemoryPercent;
        CpuText = s.CpuPercent.ToString("0", Tr.Culture) + "%";
        MemoryText = Tr.Format(
            "Monitor.MemoryValue",
            ByteSize.Format(s.MemoryUsed, Tr.Culture),
            ByteSize.Format(s.MemoryTotal, Tr.Culture)
        );
        NetworkText = Tr.Format(
            "Monitor.NetworkValue",
            ByteSize.Format(s.NetworkReceivePerSecond, Tr.Culture),
            ByteSize.Format(s.NetworkSendPerSecond, Tr.Culture)
        );
        CpuHistory = Push(_cpu, s.CpuPercent);
        MemoryHistory = Push(_memory, s.MemoryPercent);
    }

    private static PointCollection Push(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > HistoryLength)
            history.Dequeue();

        var points = new PointCollection();
        var step = ChartWidth / (HistoryLength - 1);
        var offset = HistoryLength - history.Count;
        var i = 0;
        foreach (var v in history)
        {
            points.Add(new Point((offset + i) * step, ChartHeight - v / 100 * ChartHeight));
            i++;
        }
        points.Freeze();
        return points;
    }
}

public sealed class ProcessRow(ProcessSample sample)
{
    public ProcessSample Sample { get; } = sample;
    public string Name => Sample.Name;
    public int Id => Sample.Id;
    public long Memory => Sample.MemoryBytes;
    public string Cpu => Sample.CpuPercent.ToString("0.0", Tr.Culture) + "%";
    public double CpuValue => Sample.CpuPercent;
    public string Path => Sample.Path ?? "";
    public ImageSource? Icon => IconService.ForFile(Sample.Path);
}

public sealed partial class ProcessesViewModel : PageSection
{
    private readonly ProcessSampler _sampler = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _sampling;

    public ProcessesViewModel()
        : base("Processes", SymbolRegular.TaskListLtr24)
    {
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o =>
            o is ProcessRow r
            && (Search.Length == 0 || r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase));
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public ObservableCollection<ProcessRow> Rows { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private string _count = "";

    [ObservableProperty]
    private ProcessRow? _selected;

    partial void OnSearchChanged(string value) => View.Refresh();

    public void Start()
    {
        _ = RefreshAsync();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private async Task RefreshAsync()
    {
        if (_sampling)
            return;
        _sampling = true;
        try
        {
            var samples = await Task.Run(_sampler.Sample);
            var selectedId = Selected?.Id;
            Rows.Clear();
            foreach (var sample in samples.OrderByDescending(s => s.MemoryBytes))
                Rows.Add(new ProcessRow(sample));
            Selected = Rows.FirstOrDefault(r => r.Id == selectedId);
            Count = Tr.Format("Processes.Count", Rows.Count);
        }
        finally
        {
            _sampling = false;
        }
    }

    [RelayCommand]
    private async Task End(ProcessRow? row)
    {
        if (row is null)
            return;

        var confirmed = await Dialogs.ConfirmAsync(
            Tr.Get("Processes.Confirm.Title"),
            Tr.Format("Processes.Confirm.Message", row.Name, row.Id),
            Tr.Get("Processes.End")
        );
        if (!confirmed)
            return;

        if (!await Task.Run(() => ProcessSampler.Kill(row.Id)))
            await Dialogs.InfoAsync(
                Tr.Get("Processes.End"),
                Tr.Format("Processes.Failed", row.Name)
            );

        await RefreshAsync();
    }

    [RelayCommand]
    private static void OpenLocation(ProcessRow? row)
    {
        if (row is { Path.Length: > 0 } && File.Exists(row.Path))
            Process.Start("explorer.exe", $"/select,\"{row.Path}\"");
    }
}

public sealed class RepairItem(RepairCommand command)
{
    public RepairCommand Command { get; } = command;
    public string Name { get; } = Tr.Get($"Repair.{command.Id}");
    public string Description { get; } = Tr.Get($"Repair.{command.Id}.Description");
    public bool LongRunning => Command.LongRunning;
}

/// <summary>A list of console commands with a live output pane: repairs, app updates, drivers.</summary>
public sealed partial class RepairViewModel(
    string key,
    SymbolRegular icon,
    IReadOnlyList<RepairCommand> commands
) : PageSection(key, icon)
{
    private const int MaxLines = 3000;
    private CancellationTokenSource? _cts;

    public RepairViewModel()
        : this("Repair", SymbolRegular.Wrench24, RepairCatalog.All) { }

    public string Hint { get; } = Tr.Get($"{key}.Hint");

    public IReadOnlyList<RepairItem> Items { get; } =
        commands.Select(c => new RepairItem(c)).ToList();

    public ObservableCollection<string> Output { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _status = "";

    private bool CanRun => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run(RepairItem item)
    {
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Output.Clear();
        Status = item.Name;
        var dispatcher = Application.Current.Dispatcher;
        Output.Add($"> {item.Command.FileName} {item.Command.Arguments}");

        try
        {
            var code = await CommandRunner.RunAsync(
                item.Command.FileName,
                item.Command.Arguments,
                item.Command.Encoding,
                (line, replace) => dispatcher.InvokeAsync(() => AddLine(line, replace)),
                _cts.Token
            );
            Status = Tr.Format("Repair.Finished", code);
            if (item.Command.NeedsReboot)
                Status += " " + Tr.Get("Repair.NeedsReboot");
        }
        catch (OperationCanceledException)
        {
            Status = Tr.Get("Repair.Cancelled");
        }
        catch (Exception ex)
            when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = ex.Message;
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

    private void AddLine(string line, bool replace)
    {
        if (replace && Output.Count > 1)
            Output[^1] = line;
        else
            Output.Add(line);

        while (Output.Count > MaxLines)
            Output.RemoveAt(1);
    }
}

public sealed partial class DnsChoice(DnsPreset preset) : ObservableObject
{
    public DnsPreset Preset { get; } = preset;

    public string Name { get; } = preset.IsAutomatic ? Tr.Get("Dns.Automatic") : preset.Name;

    public string Servers { get; } = string.Join(", ", preset.Servers);

    [ObservableProperty]
    private string _latency = "";

    [ObservableProperty]
    private bool _inUse;
}

public sealed partial class DnsViewModel() : PageSection("Dns", SymbolRegular.Globe24)
{
    public ObservableCollection<DnsChoice> Choices { get; } =
        new(DnsManager.Presets.Select(p => new DnsChoice(p)));

    public ObservableCollection<string> Adapters { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseCommand), nameof(TestSpeedCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    public void Refresh()
    {
        var adapters = DnsManager.ActiveAdapters();
        Adapters.Clear();
        foreach (var a in adapters)
        {
            var servers =
                a.DnsServers.Count == 0
                    ? Tr.Get("Dns.CurrentAuto")
                    : string.Join(", ", a.DnsServers);
            Adapters.Add($"{a.Name}: {servers}");
        }
        if (Adapters.Count == 0)
            Adapters.Add(Tr.Get("Dns.NoAdapters"));

        var current = adapters.FirstOrDefault();
        var match = current is null ? null : DnsManager.Match(current.DnsServers);
        foreach (var choice in Choices)
            choice.InUse = match is not null && choice.Preset.Id == match.Id;
    }

    private bool CanAct => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task TestSpeed()
    {
        IsBusy = true;
        try
        {
            foreach (var choice in Choices.Where(c => !c.Preset.IsAutomatic))
            {
                choice.Latency = "…";
                var ms = await DnsManager.PingAsync(choice.Preset.Servers[0]);
                choice.Latency = ms is { } v ? Tr.Format("Dns.Ping", v) : Tr.Get("Dns.NoReply");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Use(DnsChoice choice)
    {
        IsBusy = true;
        Status = "";
        try
        {
            var failed = new List<string>();
            foreach (var adapter in DnsManager.ActiveAdapters())
            {
                if (!await DnsManager.ApplyAsync(adapter.Name, choice.Preset, (_, _) => { }))
                    failed.Add(adapter.Name);
            }

            Status =
                failed.Count == 0
                    ? Tr.Format("Dns.Applied", choice.Name)
                    : string.Join(" ", failed.Select(f => Tr.Format("Dns.Failed", f)));
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }
}

public sealed class DiskCard(DiskHealth disk)
{
    public DiskHealth Disk { get; } = disk;
    public string Title =>
        $"{Disk.Name} · {Disk.MediaType} · {Disk.BusType} · {ByteSize.Format(Disk.SizeBytes, Tr.Culture)}";
    public string HealthText => Tr.Get($"Drives.Health.{Disk.Health}");
    public bool IsHealthy => Disk.Health == HealthState.Healthy;
    public bool IsBad => Disk.Health is HealthState.Warning or HealthState.Unhealthy;

    public string Temperature =>
        Disk.TemperatureC is { } t ? $"{t} °C" : Tr.Get("Drives.NotReported");

    public string Wear => Disk.WearPercent is { } w ? $"{w}%" : Tr.Get("Drives.NotReported");

    public string PowerOn =>
        Disk.PowerOnHours is { } h
            ? Tr.Format("Drives.Hours", h.ToString("N0", Tr.Culture))
            : Tr.Get("Drives.NotReported");

    public string Errors =>
        Disk.ReadErrors is null && Disk.WriteErrors is null
            ? Tr.Get("Drives.NotReported")
            : $"{Disk.ReadErrors ?? 0} / {Disk.WriteErrors ?? 0}";
}

public sealed partial class DrivesViewModel() : PageSection("Drives", SymbolRegular.HardDrive20)
{
    public ObservableCollection<DiskCard> Disks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    private bool CanLoad => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task Load()
    {
        IsBusy = true;
        Status = Tr.Get("Drives.Loading");
        try
        {
            var disks = await DriveHealth.ReadAsync();
            Disks.Clear();
            foreach (var d in disks)
                Disks.Add(new DiskCard(d));
            Status = Disks.Count == 0 ? Tr.Get("Drives.None") : "";
        }
        catch (Exception ex)
            when (ex is System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
