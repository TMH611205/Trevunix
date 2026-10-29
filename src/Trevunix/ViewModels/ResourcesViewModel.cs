using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Common;
using Trevunix.Core.Monitoring;
using Trevunix.Core.Performance;
using Trevunix.Core.SystemInfo;
using Trevunix.Localization;
using Trevunix.Services;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

/// <summary>The "Free up resources" page: RAM, CPU, GPU and game mode sections in one place.</summary>
public sealed partial class ResourcesViewModel : ObservableObject
{
    public ResourcesViewModel(MemoryViewModel memory, GameBoosterViewModel booster)
    {
        Memory = memory;
        Booster = booster;
        Sections = [memory, Cpu, Gpu, booster];
        _selected = memory;
    }

    public MemoryViewModel Memory { get; }
    public GameBoosterViewModel Booster { get; }
    public CpuFreeViewModel Cpu { get; } = new();
    public GpuFreeViewModel Gpu { get; } = new();
    public IReadOnlyList<PageSection> Sections { get; }

    [ObservableProperty]
    private PageSection _selected;

    public void OnShown()
    {
        Cpu.Start();
        Gpu.Start();
        Booster.OnShown();
    }

    public void OnHidden()
    {
        Cpu.Stop();
        Gpu.Stop();
    }
}

/// <summary>Priorities Trevunix lowered, so they can be put back. Shared by the CPU and GPU sections.</summary>
internal static class LoweredPriorities
{
    private static readonly Dictionary<int, ProcessPriorityClass> Original = [];

    public static bool IsLowered(int id) => Original.ContainsKey(id);

    public static bool Lower(int id)
    {
        if (Original.ContainsKey(id))
            return true;
        if (ProcessControl.SetPriority(id, ProcessPriorityClass.BelowNormal) is not { } before)
            return false;
        Original[id] = before;
        return true;
    }

    public static void Restore(int id)
    {
        if (Original.Remove(id, out var before))
            ProcessControl.SetPriority(id, before);
    }

    public static int RestoreAll()
    {
        var ids = Original.Keys.ToList();
        foreach (var id in ids)
            Restore(id);
        return ids.Count;
    }

    public static int Count => Original.Count;
}

public sealed class ResourceRow(
    int id,
    string name,
    string? path,
    string usage,
    double usageValue,
    long memory
)
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public string Path { get; } = path ?? "";
    public string Usage { get; } = usage;
    public double UsageValue { get; } = usageValue;
    public long Memory { get; } = memory;
    public bool IsProtected { get; } = ProcessControl.IsProtected(name);
    public bool CanAct => !IsProtected;
    public bool IsLowered { get; } = LoweredPriorities.IsLowered(id);
    public bool CanLower => !IsProtected && !IsLowered;
    public ImageSource? Icon => IconService.ForFile(path);
}

/// <summary>Shared row actions for the CPU and GPU sections.</summary>
public abstract partial class ProcessSection(string key, SymbolRegular icon)
    : PageSection(key, icon)
{
    public ObservableCollection<ResourceRow> Rows { get; } = [];

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FreeCommand))]
    private bool _isBusy;

    protected abstract Task RefreshAsync();

    protected abstract Task FreeAsync();

    private bool CanFree => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanFree))]
    private async Task Free()
    {
        IsBusy = true;
        try
        {
            await FreeAsync();
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Lower(ResourceRow row)
    {
        Status = LoweredPriorities.Lower(row.Id)
            ? Tr.Format("Resources.Lowered", row.Name)
            : Tr.Format("Resources.LowerFailed", row.Name);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task Restore(ResourceRow row)
    {
        LoweredPriorities.Restore(row.Id);
        Status = Tr.Format("Resources.Restored", row.Name);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RestoreAll()
    {
        Status = Tr.Format("Resources.RestoredAll", LoweredPriorities.RestoreAll());
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task End(ResourceRow row)
    {
        var confirmed = await Dialogs.ConfirmAsync(
            Tr.Get("Processes.Confirm.Title"),
            Tr.Format("Processes.Confirm.Message", row.Name, row.Id),
            Tr.Get("Processes.End")
        );
        if (!confirmed)
            return;

        Status = await Task.Run(() => ProcessSampler.Kill(row.Id))
            ? Tr.Format("Resources.Ended", row.Name)
            : Tr.Format("Processes.Failed", row.Name);
        await RefreshAsync();
    }
}

public sealed partial class CpuFreeViewModel()
    : ProcessSection("Cpu", SymbolRegular.DeveloperBoard24)
{
    private const double HogPercent = 5;
    private const int MaxRows = 15;

    private readonly SystemMonitor _monitor = new();
    private readonly ProcessSampler _sampler = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IReadOnlyList<ProcessSample> _last = [];
    private bool _sampling;
    private bool _started;

    [ObservableProperty]
    private double _cpuPercent;

    [ObservableProperty]
    private string _cpuText = "";

    [ObservableProperty]
    private string _cpuName = "";

    public void Start()
    {
        if (!_started)
        {
            _started = true;
            _timer.Tick += async (_, _) => await RefreshAsync();
            _ = LoadNameAsync();
        }
        _ = RefreshAsync();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private async Task LoadNameAsync()
    {
        var info = await Task.Run(HardwareInfo.Read);
        CpuName = info.Cpu is { } cpu
            ? Tr.Format("Resources.CpuName", cpu.Name, cpu.Cores, cpu.Threads)
            : "";
    }

    protected override async Task RefreshAsync()
    {
        if (_sampling)
            return;
        _sampling = true;
        try
        {
            var (total, samples) = await Task.Run(() =>
                (_monitor.Sample().CpuPercent, _sampler.Sample())
            );
            _last = samples;
            CpuPercent = total;
            CpuText = total.ToString("0", Tr.Culture) + "%";
            Badge = CpuText;

            Rows.Clear();
            foreach (
                var s in samples
                    .Where(s => s.Name != "Idle" && s.Id != Environment.ProcessId)
                    .OrderByDescending(s => s.CpuPercent)
                    .Take(MaxRows)
            )
                Rows.Add(
                    new ResourceRow(
                        s.Id,
                        s.Name,
                        s.Path,
                        s.CpuPercent.ToString("0.0", Tr.Culture) + "%",
                        s.CpuPercent,
                        s.MemoryBytes
                    )
                );
        }
        finally
        {
            _sampling = false;
        }
    }

    protected override Task FreeAsync()
    {
        var hogs = ProcessControl.PickCpuHogs(_last, HogPercent, Environment.ProcessId);
        var lowered = hogs.Count(h => LoweredPriorities.Lower(h.Id));
        Status =
            hogs.Count == 0
                ? Tr.Get("Resources.Cpu.Nothing")
                : Tr.Format(
                    "Resources.Cpu.Freed",
                    lowered,
                    string.Join(", ", hogs.Select(h => h.Name).Distinct())
                );
        return Task.CompletedTask;
    }
}

public sealed partial class GpuFreeViewModel() : ProcessSection("Gpu", SymbolRegular.Video24)
{
    private const double BusyPercent = 1;
    private const long BusyMemory = 100L * 1024 * 1024;
    private const int MaxRows = 15;

    private readonly GpuMonitor _monitor = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IReadOnlyList<GpuProcessSample> _last = [];
    private long _totalMemory;
    private bool _sampling;
    private bool _started;

    public bool IsSupported { get; } = GpuMonitor.IsAvailable;

    [ObservableProperty]
    private double _gpuPercent;

    [ObservableProperty]
    private string _gpuText = "";

    [ObservableProperty]
    private string _gpuName = "";

    [ObservableProperty]
    private string _memoryText = "";

    [ObservableProperty]
    private double _memoryPercent;

    public void Start()
    {
        if (!IsSupported)
            return;
        if (!_started)
        {
            _started = true;
            _timer.Tick += async (_, _) => await RefreshAsync();
            _ = LoadNameAsync();
        }
        _ = RefreshAsync();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private async Task LoadNameAsync()
    {
        var info = await Task.Run(HardwareInfo.Read);
        var gpu = info.Gpus.OrderByDescending(g => g.MemoryBytes).FirstOrDefault();
        GpuName = gpu?.Name ?? "";
        _totalMemory = gpu?.MemoryBytes ?? 0;
    }

    protected override async Task RefreshAsync()
    {
        if (_sampling)
            return;
        _sampling = true;
        try
        {
            var sample = await Task.Run(_monitor.Sample);
            _last = sample.Processes;
            GpuPercent = sample.UsagePercent;
            GpuText = sample.UsagePercent.ToString("0", Tr.Culture) + "%";
            Badge = GpuText;
            MemoryPercent =
                _totalMemory > 0 ? Math.Min(100, 100d * sample.DedicatedBytes / _totalMemory) : 0;
            MemoryText =
                _totalMemory > 0
                    ? Tr.Format(
                        "Resources.Gpu.Memory",
                        ByteSize.Format(sample.DedicatedBytes, Tr.Culture),
                        ByteSize.Format(_totalMemory, Tr.Culture)
                    )
                    : ByteSize.Format(sample.DedicatedBytes, Tr.Culture);

            Rows.Clear();
            foreach (
                var p in sample
                    .Processes.Where(p => p.Id != Environment.ProcessId)
                    .Where(p => p.UsagePercent >= 0.1 || p.DedicatedBytes >= 16L * 1024 * 1024)
                    .OrderByDescending(p => p.UsagePercent)
                    .ThenByDescending(p => p.DedicatedBytes)
                    .Take(MaxRows)
            )
                Rows.Add(
                    new ResourceRow(
                        p.Id,
                        p.Name,
                        p.Path,
                        p.UsagePercent.ToString("0.0", Tr.Culture) + "%",
                        p.UsagePercent,
                        p.DedicatedBytes
                    )
                );
        }
        finally
        {
            _sampling = false;
        }
    }

    /// <summary>
    ///     VRAM only comes back when the app holding it closes, so this closes background apps
    ///     using the GPU, after showing the owner exactly which ones.
    /// </summary>
    protected override async Task FreeAsync()
    {
        var targets = _last
            .Where(p =>
                p.Id != Environment.ProcessId
                && !ProcessControl.IsProtected(p.Name)
                && (p.UsagePercent >= BusyPercent || p.DedicatedBytes >= BusyMemory)
            )
            .OrderByDescending(p => p.DedicatedBytes)
            .ToList();

        if (targets.Count == 0)
        {
            Status = Tr.Get("Resources.Gpu.Nothing");
            return;
        }

        var list = string.Join(
            "\n",
            targets.Select(t =>
                $"• {t.Name} ({ByteSize.Format(t.DedicatedBytes, Tr.Culture)}, {t.UsagePercent.ToString("0", Tr.Culture)}%)"
            )
        );
        var confirmed = await Dialogs.ConfirmAsync(
            Tr.Get("Resources.Gpu.Confirm.Title"),
            Tr.Format("Resources.Gpu.Confirm.Message", list),
            Tr.Get("Resources.Gpu.Confirm.Primary")
        );
        if (!confirmed)
            return;

        var closed = await Task.Run(() => targets.Count(t => ProcessSampler.Kill(t.Id)));
        Status = Tr.Format("Resources.Gpu.Freed", closed, targets.Count);
    }
}
