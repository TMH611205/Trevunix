using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Common;
using Trevunix.Core.Memory;
using Trevunix.Core.Settings;
using Trevunix.Localization;

namespace Trevunix.ViewModels;

/// <summary>
///     RAM page. Created with the main window, not on first visit, because the automatic
///     clean has to watch memory for as long as the app is open.
/// </summary>
public sealed partial class MemoryViewModel : PageSection
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DateTime? _lastClean;

    public MemoryViewModel(AppSettings settings, SettingsStore store)
        : base("Ram", Wpf.Ui.Controls.SymbolRegular.Ram20)
    {
        _settings = settings;
        _store = store;

        Thresholds =
        [
            new(0, Tr.Get("Memory.Auto.Off")),
            .. MemoryCleaner.AutoThresholds.Select(t => new Choice<int>(t, $"{t}%")),
        ];
        _threshold =
            Thresholds.FirstOrDefault(t => t.Value == settings.AutoFreeRamThreshold)
            ?? Thresholds[0];
        _purgeStandby = settings.PurgeStandbyCache;

        _timer.Tick += async (_, _) => await TickAsync();
        Refresh(MemoryCleaner.Read());
        _timer.Start();
    }

    public IReadOnlyList<Choice<int>> Thresholds { get; }

    [ObservableProperty]
    private Choice<int> _threshold;

    [ObservableProperty]
    private bool _purgeStandby;

    [ObservableProperty]
    private double _loadPercent;

    [ObservableProperty]
    private string _loadText = "";

    [ObservableProperty]
    private string _usedText = "";

    [ObservableProperty]
    private string _availableText = "";

    [ObservableProperty]
    private string _lastResult = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    private bool _isBusy;

    public string AutoHint =>
        Threshold.Value == 0
            ? Tr.Get("Memory.Auto.HintOff")
            : Tr.Format("Memory.Auto.HintOn", Threshold.Value);

    partial void OnThresholdChanged(Choice<int> value)
    {
        _settings.AutoFreeRamThreshold = value.Value;
        OnPropertyChanged(nameof(AutoHint));
        Save();
    }

    partial void OnPurgeStandbyChanged(bool value)
    {
        _settings.PurgeStandbyCache = value;
        Save();
    }

    private bool CanClean => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanClean))]
    private Task Clean() => RunCleanAsync(automatic: false);

    private async Task TickAsync()
    {
        var status = MemoryCleaner.Read();
        Refresh(status);

        if (
            !IsBusy
            && MemoryCleaner.ShouldAutoClean(
                status.LoadPercent,
                Threshold.Value,
                DateTime.Now,
                _lastClean,
                Cooldown
            )
        )
            await RunCleanAsync(automatic: true);
    }

    private async Task RunCleanAsync(bool automatic)
    {
        IsBusy = true;
        try
        {
            var purge = PurgeStandby;
            var result = await Task.Run(() => MemoryCleaner.Clean(purge));
            _lastClean = DateTime.Now;
            LastResult = Tr.Format(
                automatic ? "Memory.Result.Auto" : "Memory.Result.Manual",
                ByteSize.Format(result.Freed, Tr.Culture),
                result.ProcessesTrimmed,
                _lastClean.Value.ToString("HH:mm:ss", Tr.Culture)
            );
            Refresh(MemoryCleaner.Read());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Refresh(MemoryStatus status)
    {
        LoadPercent = status.LoadPercent;
        LoadText = $"{status.LoadPercent}%";
        Badge = LoadText;
        UsedText = Tr.Format(
            "Memory.Used",
            ByteSize.Format(status.Used, Tr.Culture),
            ByteSize.Format(status.Total, Tr.Culture)
        );
        AvailableText = Tr.Format(
            "Memory.Available",
            ByteSize.Format(status.Available, Tr.Culture)
        );
    }

    private void Save() => _store.TrySave(_settings);
}
