using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Apps;
using Trevunix.Core.Startup;
using Trevunix.Localization;
using Trevunix.Services;

namespace Trevunix.ViewModels;

public sealed partial class StartupItem(StartupEntry entry, StartupImpactLevel impact)
    : ObservableObject
{
    public StartupEntry Entry { get; private set; } = entry;
    public StartupImpactLevel ImpactLevel { get; } = impact;
    public string Impact { get; } = Tr.Get($"Startup.Impact.{impact}");
    public bool IsHighImpact => ImpactLevel == StartupImpactLevel.High;

    [ObservableProperty]
    private bool _isDelayed = StartupDelay.IsDelayed(entry);
    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string Publisher => Entry.Publisher ?? Tr.Get("Startup.UnknownPublisher");
    public ImageSource? Icon => IconService.ForFile(Entry.FilePath);

    public string Scope =>
        Entry.Source switch
        {
            StartupSource.UserFolder or StartupSource.MachineFolder => Tr.Get("Startup.Folder")
                + " · "
                + (Entry.IsMachineWide ? Tr.Get("Startup.AllUsers") : Tr.Get("Startup.ThisUser")),
            _ => Entry.IsMachineWide ? Tr.Get("Startup.AllUsers") : Tr.Get("Startup.ThisUser"),
        };

    public bool IsEnabled => Entry.Enabled;

    [ObservableProperty]
    private string _error = "";

    public void Update(StartupEntry entry)
    {
        Entry = entry;
        OnPropertyChanged(nameof(IsEnabled));
    }

    public void NotifyState() => OnPropertyChanged(nameof(IsEnabled));
}

public sealed partial class StoreAppItem(StoreApp app) : ObservableObject
{
    public StoreApp App { get; } = app;
    public string DisplayName => App.DisplayName;
    public string Details => $"{App.Name} · {App.Publisher} · {App.Version}";
    public bool Suggested => App.Suggested;
    public ImageSource? Logo => IconService.ForImage(App.LogoPath);

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class AppsViewModel : ObservableObject
{
    private readonly StartupManager _startup = new();

    public AppsViewModel(RestorePointGuard restorePoints)
    {
        Programs = new ProgramsViewModel(restorePoints);
        StoreView = CollectionViewSource.GetDefaultView(StoreItems);
        StoreView.Filter = FilterApp;
        LoadStartup();
        Programs.OnShown();
    }

    public ProgramsViewModel Programs { get; }

    public BrowserExtensionsViewModel Extensions { get; } = new();

    public ObservableCollection<StartupItem> StartupItems { get; } = [];
    public ObservableCollection<StoreAppItem> StoreItems { get; } = [];
    public ICollectionView StoreView { get; }

    [ObservableProperty]
    private string _startupSummary = "";

    [ObservableProperty]
    private bool _storeLoaded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand), nameof(LoadStoreCommand))]
    private bool _storeBusy;

    [ObservableProperty]
    private string _storeStatus = "";

    [ObservableProperty]
    private string _storeResult = "";

    [ObservableProperty]
    private bool _alsoForNewUsers;

    [ObservableProperty]
    private bool _onlySuggested = true;

    [ObservableProperty]
    private string _search = "";

    public string RemoveText => Tr.Format("Store.Remove", StoreItems.Count(i => i.IsSelected));

    partial void OnOnlySuggestedChanged(bool value) => StoreView.Refresh();

    partial void OnSearchChanged(string value) => StoreView.Refresh();

    [RelayCommand]
    private void LoadStartup()
    {
        StartupItems.Clear();
        var impacts = StartupImpact.Read();
        foreach (var entry in _startup.List())
        {
            var impact =
                entry.FilePath is { } path && impacts.TryGetValue(path, out var level)
                    ? level
                    : StartupImpactLevel.NotMeasured;
            StartupItems.Add(new StartupItem(entry, impact));
        }
        UpdateStartupSummary();
    }

    [RelayCommand]
    private void ToggleStartup(StartupItem item)
    {
        var enable = !item.Entry.Enabled;
        try
        {
            // A delayed program turned back on starts normally again, not twice.
            if (enable && item.IsDelayed && StartupDelay.Undo(item.Entry, _startup))
                item.IsDelayed = false;
            _startup.SetEnabled(item.Entry, enable);
            item.Error = "";
            item.Update(item.Entry with { Enabled = enable });
        }
        catch (Exception ex)
            when (ex
                    is UnauthorizedAccessException
                        or IOException
                        or System.Security.SecurityException
            )
        {
            item.Error = Tr.Format("Startup.Failed", item.Name, ex.Message);
            item.NotifyState();
        }

        UpdateStartupSummary();
    }

    [RelayCommand]
    private async Task ToggleDelay(StartupItem item)
    {
        var entry = item.Entry;
        var delayed = item.IsDelayed;
        var ok = await Task.Run(() =>
            delayed ? StartupDelay.Undo(entry, _startup) : StartupDelay.Delay(entry, _startup)
        );
        if (ok)
        {
            item.IsDelayed = !delayed;
            item.Update(entry with { Enabled = delayed });
            item.Error = "";
        }
        else
            item.Error = Tr.Format("Startup.Failed", item.Name, Tr.Get("Startup.DelayFailed"));
        UpdateStartupSummary();
    }

    [RelayCommand]
    private static void OpenLocation(StartupItem item)
    {
        var path = item.Entry.FilePath;
        if (path is not null && File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    private bool CanUseStore => !StoreBusy;

    [RelayCommand(CanExecute = nameof(CanUseStore))]
    private async Task LoadStoreAsync()
    {
        StoreBusy = true;
        StoreStatus = Tr.Get("Store.Loading");
        try
        {
            var apps = await StoreApps.ListAsync();
            foreach (var old in StoreItems)
                old.PropertyChanged -= OnStoreItemChanged;
            StoreItems.Clear();
            foreach (var app in apps)
            {
                var item = new StoreAppItem(app);
                item.PropertyChanged += OnStoreItemChanged;
                StoreItems.Add(item);
            }
            StoreLoaded = true;
        }
        catch (Exception ex)
            when (ex
                    is InvalidOperationException
                        or System.Text.Json.JsonException
                        or System.ComponentModel.Win32Exception
            )
        {
            StoreResult = Tr.Format("Store.LoadFailed", ex.Message);
        }
        finally
        {
            StoreBusy = false;
            StoreStatus = "";
            OnPropertyChanged(nameof(RemoveText));
        }
    }

    private bool CanRemove => !StoreBusy && StoreItems.Any(i => i.IsSelected);

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveSelectedAsync()
    {
        var chosen = StoreItems.Where(i => i.IsSelected).ToList();
        var confirmed = await Dialogs.ConfirmAsync(
            Tr.Get("Store.Confirm.Title"),
            Tr.Format("Store.Confirm.Message", chosen.Count),
            Tr.Get("Store.Confirm.Primary")
        );
        if (!confirmed)
            return;

        StoreBusy = true;
        StoreResult = "";
        var failed = new List<string>();
        var removed = 0;
        try
        {
            foreach (var item in chosen)
            {
                StoreStatus = Tr.Format("Store.Removing", item.DisplayName);
                var result = await StoreApps.RemoveAsync(item.App, AlsoForNewUsers);
                if (result.Success)
                {
                    removed++;
                    item.PropertyChanged -= OnStoreItemChanged;
                    StoreItems.Remove(item);
                }
                else
                {
                    failed.Add(item.DisplayName);
                }
            }
        }
        finally
        {
            StoreBusy = false;
            StoreStatus = "";
            OnPropertyChanged(nameof(RemoveText));
        }

        StoreResult =
            failed.Count == 0
                ? Tr.Format("Store.Done", removed)
                : Tr.Format(
                    "Store.DoneWithErrors",
                    removed,
                    failed.Count,
                    string.Join(", ", failed)
                );
    }

    private bool FilterApp(object obj)
    {
        if (obj is not StoreAppItem item)
            return false;
        if (OnlySuggested && !item.Suggested)
            return false;
        return Search.Length == 0
            || item.DisplayName.Contains(Search, StringComparison.CurrentCultureIgnoreCase)
            || item.App.Name.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    private void OnStoreItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StoreAppItem.IsSelected))
            return;
        OnPropertyChanged(nameof(RemoveText));
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    private void UpdateStartupSummary() =>
        StartupSummary = Tr.Format(
            "Startup.Summary",
            StartupItems.Count(i => i.IsEnabled),
            StartupItems.Count
        );
}
