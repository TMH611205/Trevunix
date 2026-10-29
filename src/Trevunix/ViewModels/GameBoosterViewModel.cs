using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Common;
using Trevunix.Core.Memory;
using Trevunix.Core.Performance;
using Trevunix.Core.Settings;
using Trevunix.Localization;
using Trevunix.Services;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

public sealed partial class BoosterAppItem(BoosterApp app, bool selected) : ObservableObject
{
    public string Name { get; } = app.Name;
    public string Path { get; } = app.Path ?? "";
    public string MemoryText { get; } = ByteSize.Format(app.MemoryBytes, Tr.Culture);

    [ObservableProperty]
    private bool _isSelected = selected;
}

/// <summary>
///     Game mode on the resources page. Created with the main window so that quitting
///     Trevunix while it is on can put the power plan back.
/// </summary>
public sealed partial class GameBoosterViewModel(AppSettings settings, SettingsStore store)
    : PageSection("Booster", SymbolRegular.Games24)
{
    private BoostSession? _session;

    public ObservableCollection<BoosterAppItem> Apps { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(LoadCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isActive;

    [ObservableProperty]
    private string _status = "";

    private bool CanLoad => !IsBusy;
    private bool CanStart => !IsBusy && !IsActive;
    private bool CanStop => !IsBusy && IsActive;

    public void OnShown()
    {
        if (Apps.Count == 0 && !IsActive && LoadCommand.CanExecute(null))
            LoadCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var apps = await Task.Run(GameBooster.ListApps);
            var chosen = new HashSet<string>(
                settings.BoosterApps,
                StringComparer.OrdinalIgnoreCase
            );
            Apps.Clear();
            foreach (var app in apps)
                Apps.Add(new BoosterAppItem(app, chosen.Contains(app.Name)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var names = Apps.Where(a => a.IsSelected).Select(a => a.Name).ToList();
        if (
            names.Count > 0
            && !await Dialogs.ConfirmAsync(
                Tr.Get("Booster.Confirm.Title"),
                Tr.Format("Booster.Confirm.Message", string.Join(", ", names)),
                Tr.Get("Booster.Start")
            )
        )
            return;

        settings.BoosterApps = names;
        store.TrySave(settings);

        IsBusy = true;
        Status = Tr.Get("Booster.Starting");
        try
        {
            var result = await Task.Run(() => GameBooster.Start(names));
            var purge = settings.PurgeStandbyCache;
            var memory = await Task.Run(() => MemoryCleaner.Clean(purge));
            _session = result.Session;
            IsActive = true;

            Status = Tr.Format(
                "Booster.On",
                result.Session.ClosedAppPaths.Count,
                ByteSize.Format(memory.Freed, Tr.Culture)
            );
            if (!result.HighPerformanceOn)
                Status += " " + Tr.Get("Booster.NoPowerPlan");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (_session is not { } session)
            return;

        IsBusy = true;
        try
        {
            var reopened = await Task.Run(() =>
            {
                GameBooster.RestorePowerPlan(session);
                return GameBooster.ReopenApps(session);
            });
            _session = null;
            IsActive = false;
            Status = Tr.Format("Booster.Off", reopened);
        }
        finally
        {
            IsBusy = false;
        }
        await LoadAsync();
    }

    /// <summary>Trevunix is quitting: put the power plan back, but do not reopen apps behind the owner's back.</summary>
    public void RestoreOnExit()
    {
        if (_session is { } session)
            GameBooster.RestorePowerPlan(session);
    }
}
