using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Cleanup;
using Trevunix.Core.Common;
using Trevunix.Localization;
using Trevunix.Services;

namespace Trevunix.ViewModels;

public sealed partial class CleanupItem : ObservableObject
{
    public CleanupItem(CleanupTarget target)
    {
        Target = target;
        Name = Tr.Get($"Cleanup.Target.{target.Id}");
        Description = Tr.Get($"Cleanup.Target.{target.Id}.Description");
        GroupName = Tr.Get($"Cleanup.Group.{target.Group}");
        Recommended = target.SelectedByDefault;
        _isSelected = target.SelectedByDefault;
    }

    public CleanupTarget Target { get; }
    public string Name { get; }
    public string Description { get; }
    public string GroupName { get; }
    public bool Recommended { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FoundText), nameof(HasFiles), nameof(KeptText))]
    private TargetScan? _scan;

    public bool HasFiles => Scan is { Count: > 0 };

    public string KeptText =>
        Scan is { KeptFiles: > 0 } s
            ? Tr.Format("Cleanup.Kept", s.KeptFiles.ToString("N0", Tr.Culture))
            : "";

    public string FoundText =>
        Scan switch
        {
            null => "",
            { Count: 0 } => Tr.Get("Cleanup.FoundNothing"),
            var s => Tr.Format(
                "Cleanup.Found",
                ByteSize.Format(s.Size, Tr.Culture),
                s.Count.ToString("N0", Tr.Culture)
            ),
        };
}

public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly CleanupEngine _engine = new();

    public CleanupViewModel()
    {
        foreach (var target in CleanupCatalog.All)
        {
            var item = new CleanupItem(target);
            item.PropertyChanged += OnItemChanged;
            Items.Add(item);
        }

        _ = ScanAsync();
    }

    public ObservableCollection<CleanupItem> Items { get; } = [];

    public string BrowsersHint => Tr.Get("Cleanup.BrowsersHint");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CleanCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "";

    [ObservableProperty]
    private string _result = "";

    public long SelectedSize =>
        Items.Where(i => i.IsSelected && i.Scan is not null).Sum(i => i.Scan!.Size);

    public string SelectedText =>
        Tr.Format("Cleanup.Selected", ByteSize.Format(SelectedSize, Tr.Culture));

    private bool CanRun => !IsBusy;

    private bool CanClean => !IsBusy && Items.Any(i => i.IsSelected && i.HasFiles);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        BusyText = Tr.Get("Cleanup.Scanning");
        try
        {
            foreach (var item in Items)
                item.Scan = await Task.Run(() => _engine.Scan(item.Target));
        }
        finally
        {
            IsBusy = false;
            RefreshTotals();
        }
    }

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var chosen = Items.Where(i => i.IsSelected && i.HasFiles).ToList();

        var bin = chosen.FirstOrDefault(i => i.Target.IsRecycleBin);
        if (bin is not null)
        {
            var ok = await Dialogs.ConfirmAsync(
                Tr.Get("Cleanup.ConfirmRecycleBin.Title"),
                Tr.Format(
                    "Cleanup.ConfirmRecycleBin.Message",
                    ByteSize.Format(bin.Scan!.Size, Tr.Culture)
                ),
                Tr.Get("Cleanup.ConfirmRecycleBin.Primary")
            );
            if (!ok)
                return;
        }

        IsBusy = true;
        BusyText = Tr.Get("Cleanup.Cleaning");
        Result = "";
        CleanupResult outcome;
        try
        {
            outcome = await _engine.CleanAsync(chosen.Select(i => i.Scan!));
        }
        finally
        {
            IsBusy = false;
        }

        var freed = ByteSize.Format(outcome.FreedBytes, Tr.Culture);
        var files = outcome.DeletedFiles.ToString("N0", Tr.Culture);
        Result =
            outcome.SkippedFiles > 0
                ? Tr.Format(
                    "Cleanup.DoneSkipped",
                    freed,
                    files,
                    outcome.SkippedFiles.ToString("N0", Tr.Culture)
                )
                : Tr.Format("Cleanup.Done", freed, files);

        await ScanAsync();
    }

    [RelayCommand]
    private static async Task Preview(CleanupItem item)
    {
        if (item.Scan is not { } scan)
            return;

        const int shown = 500;
        var lines = scan
            .Files.OrderByDescending(f => f.Size)
            .Take(shown)
            .Select(f => $"{ByteSize.Format(f.Size, Tr.Culture), 10}   {f.Path}")
            .ToList();
        if (scan.Files.Count > shown)
            lines.Add(
                Tr.Format(
                    "Cleanup.PreviewMore",
                    (scan.Files.Count - shown).ToString("N0", Tr.Culture)
                )
            );

        await Dialogs.ListAsync(Tr.Format("Cleanup.PreviewTitle", item.Name), lines);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items.Where(i => !i.Target.IsRecycleBin))
            item.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in Items)
            item.IsSelected = false;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CleanupItem.IsSelected) or nameof(CleanupItem.Scan))
            RefreshTotals();
    }

    private void RefreshTotals()
    {
        OnPropertyChanged(nameof(SelectedSize));
        OnPropertyChanged(nameof(SelectedText));
        CleanCommand.NotifyCanExecuteChanged();
    }
}
