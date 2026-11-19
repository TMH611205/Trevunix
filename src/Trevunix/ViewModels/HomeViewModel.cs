using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Cleanup;
using Trevunix.Core.Common;
using Trevunix.Core.DiskUsage;
using Trevunix.Core.Maintenance;
using Trevunix.Core.Memory;
using Trevunix.Core.SystemInfo;
using Trevunix.Core.Tweaks;
using Trevunix.Localization;
using Trevunix.Services;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

public sealed record DriveCard(DriveSummary Drive)
{
    public string Title =>
        string.IsNullOrWhiteSpace(Drive.Label)
            ? Drive.RootPath
            : $"{Drive.Label} ({Drive.RootPath.TrimEnd('\\')})";

    public string FreeText =>
        Tr.Format(
            "Home.DriveFree",
            ByteSize.Format(Drive.FreeBytes, Tr.Culture),
            ByteSize.Format(Drive.TotalBytes, Tr.Culture)
        );

    public double UsedPercent => Drive.UsedShare * 100;

    /// <summary>Over 90% used is shown as a warning.</summary>
    public bool AlmostFull => Drive.UsedShare >= 0.9;
}

/// <summary>A label and value; a row with an empty label is a sub-heading (e.g. one RAM stick).</summary>
public sealed record InfoRow(string Label, string Value)
{
    public bool IsHeading => Label.Length == 0;
}

public sealed record InfoCard(string Title, SymbolRegular Icon, IReadOnlyList<InfoRow> Rows);

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _uptimeTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    public HomeViewModel(MainViewModel main)
    {
        _main = main;
        Machine = MachineInfo.Read();
        Drives = DriveSummary.GetFixedDrives().Select(d => new DriveCard(d)).ToList();
        _uptimeTimer.Tick += (_, _) => OnPropertyChanged(nameof(Uptime));
        _uptimeTimer.Start();
        _ = LoadHardwareAsync();
        _ = LoadScoreAsync();
    }

    [ObservableProperty]
    private string _score = "";

    [ObservableProperty]
    private bool _scoreIsLow;

    public ObservableCollection<string> ScoreItems { get; } = [];

    /// <summary>The 0 to 100 summary at the top of the page; the parts load in the background.</summary>
    public async Task LoadScoreAsync()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory);
        var drive = Drives.FirstOrDefault(d =>
            string.Equals(d.Drive.RootPath, system, StringComparison.OrdinalIgnoreCase)
        );
        var failing = _main.Health.Disks.Count(d =>
            d.Health is Core.Storage.HealthState.Warning or Core.Storage.HealthState.Unhealthy
        );
        var inputs = await Task.Run(() =>
        {
            var impacts = Core.Startup.StartupImpact.Read();
            var heavy = new Core.Startup.StartupManager()
                .List()
                .Count(e =>
                    e.Enabled
                    && e.FilePath is { } path
                    && impacts.TryGetValue(path, out var level)
                    && level == Core.Startup.StartupImpactLevel.High
                );
            return new Core.Monitoring.HealthInputs(
                drive is null ? null : 100 - drive.UsedPercent,
                _main.Health.Disks.Count == 0 ? null : failing,
                QuickMaintenance.PendingTweaks(new TweakEngine()).Count,
                Core.Monitoring.HealthScore.CountDriverProblems(),
                _main.Settings.KnownAppUpdates,
                heavy
            );
        });

        var (score, items) = Core.Monitoring.HealthScore.Compute(inputs);
        Score = score.ToString(Tr.Culture);
        ScoreIsLow = score < 70;
        ScoreItems.Clear();
        foreach (var item in items)
            ScoreItems.Add(Tr.Format($"Score.{item.Issue}", item.Count));
        if (items.Count == 0)
            ScoreItems.Add(Tr.Get("Score.AllGood"));
    }

    public MachineInfo Machine { get; }

    /// <summary>Problems found right now: a nearly full drive, a failing disk, high temperature.</summary>
    public ObservableCollection<string> Alerts => _main.Health.Active;

    [ObservableProperty]
    private bool _includeTweaks = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QuickOptimizeCommand))]
    private bool _isOptimizing;

    [ObservableProperty]
    private string _optimizeStatus = "";

    [ObservableProperty]
    private string _optimizeResult = "";

    public IReadOnlyList<DriveCard> Drives { get; }

    public ObservableCollection<InfoCard> Cards { get; } = [];

    [ObservableProperty]
    private bool _isLoading = true;

    public string Uptime
    {
        get
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            return Tr.Format("Home.UptimeValue", (int)up.TotalDays, up.Hours, up.Minutes);
        }
    }

    [RelayCommand]
    private void Analyze(DriveCard card) => _main.OpenDiskUsage(card.Drive.RootPath);

    private bool CanOptimize => !IsOptimizing;

    /// <summary>
    ///     Cleans the default cleanup targets, frees RAM and, when chosen, applies the
    ///     recommended tweaks after a restore point. Each step is the same one the dedicated
    ///     pages run.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOptimize))]
    private async Task QuickOptimizeAsync()
    {
        var tweaks = IncludeTweaks;
        var confirmed = await Dialogs.ConfirmAsync(
            Tr.Get("Quick.Confirm.Title"),
            Tr.Get(tweaks ? "Quick.Confirm.WithTweaks" : "Quick.Confirm.Message"),
            Tr.Get("Quick.Run")
        );
        if (!confirmed)
            return;

        IsOptimizing = true;
        OptimizeResult = "";
        try
        {
            // Cleaning does not depend on the restore point, which can take half a minute, so
            // both run at the same time.
            var cleaning = QuickMaintenance.CleanAsync(new CleanupEngine());

            var applied = new TweakBatchResult(0, 0, false);
            OptimizeStatus = Tr.Get(tweaks ? "RestorePoint.Creating" : "Quick.Step.Cleanup");
            // Without a restore point the owner may decline the tweaks; the cleanup still finishes.
            if (tweaks && await _main.RestorePoints.EnsureAsync())
            {
                OptimizeStatus = Tr.Get("Quick.Step.Tweaks");
                applied = await Task.Run(() =>
                {
                    var engine = new TweakEngine();
                    return QuickMaintenance.ApplyTweaks(
                        engine,
                        QuickMaintenance.PendingTweaks(engine)
                    );
                });
            }

            OptimizeStatus = Tr.Get("Quick.Step.Cleanup");
            var cleaned = await cleaning;

            OptimizeStatus = Tr.Get("Quick.Step.Memory");
            var purge = _main.Settings.PurgeStandbyCache;
            var memory = await Task.Run(() => MemoryCleaner.Clean(purge));

            new ActivityLog().Add(HistoryKind.QuickOptimize, cleaned.FreedBytes);
            var result = Tr.Format(
                "Quick.Result",
                ByteSize.Format(cleaned.FreedBytes, Tr.Culture),
                ByteSize.Format(memory.Freed, Tr.Culture)
            );
            if (tweaks)
                result += " " + Tr.Format("Quick.Result.Tweaks", applied.Applied);
            if (applied.Failed > 0)
                result += " " + Tr.Format("Quick.Result.TweaksFailed", applied.Failed);
            if (applied.NeedsRestart)
                result += " " + Tr.Get("Quick.Result.Restart");
            OptimizeResult = result;
            _ = LoadScoreAsync();

            _main.RefreshOpenPages();
        }
        finally
        {
            OptimizeStatus = "";
            IsOptimizing = false;
        }
    }

    private async Task LoadHardwareAsync()
    {
        try
        {
            var hw = await Task.Run(HardwareInfo.Read);
            foreach (var card in BuildCards(hw))
                Cards.Add(card);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static IEnumerable<InfoCard> BuildCards(HardwareInfo hw)
    {
        var unknown = Tr.Get("Home.Value.Unknown");
        string Or(string value) => string.IsNullOrWhiteSpace(value) ? unknown : value;
        string Size(long bytes) => bytes > 0 ? ByteSize.Format(bytes, Tr.Culture) : unknown;

        yield return new InfoCard(
            Tr.Get("Home.Card.System"),
            SymbolRegular.Desktop24,
            [
                new(Tr.Get("Home.Computer"), hw.ComputerName),
                new(Tr.Get("Home.Field.Os"), hw.Os.Name),
                new(Tr.Get("Home.Field.Version"), hw.Os.Version),
                new(Tr.Get("Home.Field.Architecture"), hw.Os.Architecture),
                new(
                    Tr.Get("Home.Field.Installed"),
                    hw.Os.InstalledOn?.ToString("d", Tr.Culture) ?? unknown
                ),
            ]
        );

        var board = hw.Board;
        yield return new InfoCard(
            Tr.Get("Home.Card.Board"),
            SymbolRegular.DeveloperBoard24,
            [
                new(Tr.Get("Home.Field.Manufacturer"), Or(board.Manufacturer)),
                new(Tr.Get("Home.Field.Model"), Or(board.Product)),
                new(Tr.Get("Home.Field.Revision"), Or(board.Version)),
                new(
                    Tr.Get("Home.Field.Bios"),
                    Or($"{board.BiosVendor} {board.BiosVersion} {board.BiosDate}".Trim())
                ),
            ]
        );

        var cpu = hw.Cpu;
        yield return new InfoCard(
            Tr.Get("Home.Card.Cpu"),
            SymbolRegular.Flash24,
            [
                new(Tr.Get("Home.Field.Name"), Or(cpu.Name)),
                new(
                    Tr.Get("Home.Field.Cores"),
                    cpu.Cores > 0 ? Tr.Format("Home.Value.Cores", cpu.Cores, cpu.Threads) : unknown
                ),
                new(
                    Tr.Get("Home.Field.Clock"),
                    cpu.MaxClockMhz > 0
                        ? Tr.Format(
                            "Home.Value.Clock",
                            (cpu.MaxClockMhz / 1000d).ToString("0.0#", Tr.Culture)
                        )
                        : unknown
                ),
                new(Tr.Get("Home.Field.Socket"), Or(cpu.Socket)),
            ]
        );

        var memory = hw.Memory;
        var first = memory.Modules.FirstOrDefault();
        var memoryRows = new List<InfoRow>
        {
            new(Tr.Get("Home.Field.Total"), Size(memory.TotalBytes)),
            new(
                Tr.Get("Home.Field.TypeSpeed"),
                first is null
                    ? unknown
                    : Or(
                        $"{first.Type} {(first.SpeedMhz > 0 ? first.SpeedMhz + " MHz" : "")}".Trim()
                    )
            ),
            new(
                Tr.Get("Home.Field.Slots"),
                memory.SlotCount > 0
                    ? Tr.Format("Home.Value.Slots", memory.Modules.Count, memory.SlotCount)
                    : memory.Modules.Count.ToString(CultureInfo.InvariantCulture)
            ),
        };
        foreach (var module in memory.Modules)
        {
            var details = string.Join(
                " · ",
                new[]
                {
                    Size(module.CapacityBytes),
                    module.Type,
                    module.SpeedMhz > 0 ? module.SpeedMhz + " MHz" : "",
                    module.Manufacturer,
                    module.PartNumber,
                }.Where(p => p.Length > 0)
            );
            memoryRows.Add(new("", $"{module.Slot}: {details}"));
        }
        yield return new InfoCard(Tr.Get("Home.Card.Memory"), SymbolRegular.Ram20, memoryRows);

        var gpuRows = new List<InfoRow>();
        foreach (var gpu in hw.Gpus)
        {
            gpuRows.Add(new("", gpu.Name));
            gpuRows.Add(new(Tr.Get("Home.Field.Vram"), Size(gpu.MemoryBytes)));
            gpuRows.Add(new(Tr.Get("Home.Field.Driver"), Or(gpu.DriverVersion)));
        }
        if (gpuRows.Count == 0)
            gpuRows.Add(new(Tr.Get("Home.Field.Name"), unknown));
        yield return new InfoCard(Tr.Get("Home.Card.Gpu"), SymbolRegular.Video24, gpuRows);

        var diskRows = new List<InfoRow>();
        foreach (var disk in hw.Disks)
        {
            diskRows.Add(new("", disk.Model));
            diskRows.Add(new(Tr.Get("Home.Field.Total"), Size(disk.SizeBytes)));
            diskRows.Add(new(Tr.Get("Home.Field.Interface"), Or(disk.Interface)));
        }
        if (diskRows.Count == 0)
            diskRows.Add(new(Tr.Get("Home.Field.Model"), unknown));
        yield return new InfoCard(Tr.Get("Home.Card.Disks"), SymbolRegular.HardDrive20, diskRows);
    }
}
