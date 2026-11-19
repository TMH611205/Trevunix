using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Trevunix.Core.Settings;
using Trevunix.Localization;
using Trevunix.Services;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

public sealed class NavItem(string titleKey, SymbolRegular icon, Func<object> createPage)
{
    private object? _page;

    public bool IsCreated => _page is not null;

    public string Title { get; } = Tr.Get(titleKey);
    public SymbolRegular Icon { get; } = icon;
    public object Page => _page ??= createPage();
}

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppSettings settings, SettingsStore store)
    {
        Settings = settings;
        RestorePoints = new RestorePointGuard(settings);
        Health = new HealthWatcher();
        DiskUsage = new DiskUsageViewModel();
        // Built now so automatic RAM cleaning runs even if the page is never opened.
        Memory = new MemoryViewModel(settings, store);
        Booster = new GameBoosterViewModel(settings, store);

        Items =
        [
            new NavItem("Nav.Home", SymbolRegular.Home24, () => new HomeViewModel(this)),
            new NavItem(
                "Nav.Optimize",
                SymbolRegular.Rocket24,
                () => new OptimizeViewModel(RestorePoints)
            ),
            new NavItem(
                "Nav.Appearance",
                SymbolRegular.PaintBrush24,
                () => new OptimizeViewModel(RestorePoints, appearance: true)
            ),
            new NavItem(
                "Nav.Resources",
                SymbolRegular.Flash24,
                () => new ResourcesViewModel(Memory, Booster)
            ),
            new NavItem("Nav.Cleanup", SymbolRegular.Broom24, () => new CleanupViewModel()),
            new NavItem("Nav.DiskUsage", SymbolRegular.DataPie24, () => DiskUsage),
            new NavItem("Nav.Apps", SymbolRegular.Apps24, () => new AppsViewModel(RestorePoints)),
            new NavItem("Nav.Tools", SymbolRegular.Toolbox24, () => new SystemToolsViewModel()),
        ];

        SettingsItem = new NavItem(
            "Nav.Settings",
            SymbolRegular.Settings24,
            () => new SettingsViewModel(settings, store)
        );

        AppUpdates = new(settings.AppUpdates ?? []);
        _selected = Items[0];
    }

    public ObservableCollection<NavItem> Items { get; }

    public NavItem SettingsItem { get; }

    public DiskUsageViewModel DiskUsage { get; }

    public AppSettings Settings { get; }

    public RestorePointGuard RestorePoints { get; }

    public HealthWatcher Health { get; }

    public MemoryViewModel Memory { get; }

    public GameBoosterViewModel Booster { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    private NavItem _selected;

    public object CurrentPage => Selected.Page;

    public void Navigate(NavItem item) => Selected = item;

    /// <summary>After the one-click optimize, pages that are already open show the new state.</summary>
    public void RefreshOpenPages()
    {
        foreach (var item in Items.Where(i => i.IsCreated))
        {
            if (item.Page is OptimizeViewModel optimize)
                _ = optimize.RefreshAsync();
            else if (item.Page is CleanupViewModel cleanup && cleanup.ScanCommand.CanExecute(null))
                cleanup.ScanCommand.Execute(null);
        }
    }

    /// <summary>Apps with a newer version, from the last check; the home page lists them.</summary>
    public ObservableCollection<Core.Monitoring.AppUpdate> AppUpdates { get; }

    public void SetAppUpdates(IReadOnlyList<Core.Monitoring.AppUpdate> updates)
    {
        AppUpdates.Clear();
        foreach (var update in updates)
            AppUpdates.Add(update);
        AppUpdatesChanged?.Invoke();
    }

    public event Action? AppUpdatesChanged;

    /// <summary>Tools > App updates, listing the updates right away.</summary>
    public void OpenAppUpdates()
    {
        var item = Items.First(i => i.Title == Tr.Get("Nav.Tools"));
        Selected = item;
        if (item.Page is SystemToolsViewModel tools)
        {
            tools.Selected = tools.AppUpdates;
            var list = tools.AppUpdates.Items[0];
            if (tools.AppUpdates.RunCommand.CanExecute(list))
                tools.AppUpdates.RunCommand.Execute(list);
        }
    }

    public void OpenDiskUsage(string? rootPath)
    {
        Selected = Items.First(i => ReferenceEquals(i.Page, DiskUsage));
        if (rootPath is not null)
            _ = DiskUsage.ScanAsync(rootPath);
    }
}
