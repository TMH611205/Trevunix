using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Maintenance;
using Trevunix.Core.Tweaks;
using Trevunix.Localization;
using Trevunix.Services;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

public sealed partial class TweakItem(Tweak tweak) : ObservableObject
{
    public Tweak Tweak { get; } = tweak;
    public string Name { get; } = Tr.Get($"Tweak.{tweak.Id}");
    public string Description { get; } = Tr.Get($"Tweak.{tweak.Id}.Description");
    public bool Recommended { get; } = tweak.Risk == TweakRisk.Recommended;

    public string AfterText { get; } =
        tweak.After switch
        {
            TweakAfter.RestartExplorer => Tr.Get("Optimize.NeedsRestartExplorer"),
            TweakAfter.Reboot => Tr.Get("Optimize.NeedsReboot"),
            _ => "",
        };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOn), nameof(IsPartial))]
    private TweakStatus _status;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _error = "";

    public bool IsOn => Status == TweakStatus.Applied;
    public bool IsPartial => Status == TweakStatus.Partial;

    /// <summary>The switch flips on click; this snaps it back to the real state.</summary>
    public void NotifyState()
    {
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(IsPartial));
    }
}

/// <summary>One section in the left list of the Optimize page.</summary>
public sealed partial class TweakGroup(TweakCategory category, IReadOnlyList<TweakItem> items)
    : ObservableObject
{
    public TweakCategory Category { get; } = category;
    public string Name { get; } = Tr.Get($"Optimize.Category.{category}");
    public string Description { get; } = Tr.Get($"Optimize.Category.{category}.Description");
    public IReadOnlyList<TweakItem> Items { get; } = items;

    public SymbolRegular Icon { get; } =
        category switch
        {
            TweakCategory.AdsAndSuggestions => SymbolRegular.Megaphone24,
            TweakCategory.DataCollection => SymbolRegular.ShieldLock24,
            TweakCategory.Ai => SymbolRegular.BrainCircuit24,
            TweakCategory.Performance => SymbolRegular.TopSpeed24,
            TweakCategory.Gaming => SymbolRegular.Games24,
            TweakCategory.FileExplorer => SymbolRegular.Folder24,
            TweakCategory.Appearance => SymbolRegular.PaintBrush24,
            _ => SymbolRegular.PanelBottom20,
        };

    public string Count => Tr.Format("Optimize.GroupCount", Items.Count(i => i.IsOn), Items.Count);

    public void Refresh() => OnPropertyChanged(nameof(Count));
}

public sealed partial class OptimizeViewModel : ObservableObject
{
    private readonly TweakEngine _engine;
    private readonly RestorePointGuard _restorePoints;

    /// <param name="appearance">
    ///     The Appearance page (colors, taskbar, File Explorer) instead of the Optimize page.
    ///     Both are this view model over different categories.
    /// </param>
    public OptimizeViewModel(
        RestorePointGuard restorePoints,
        bool appearance = false,
        TweakEngine? engine = null
    )
    {
        _restorePoints = restorePoints;
        _engine = engine ?? new TweakEngine();
        var page = appearance ? "Appearance" : "Optimize";
        Title = Tr.Get($"{page}.Title");
        Subtitle = Tr.Get($"{page}.Subtitle");

        foreach (
            var tweak in TweakCatalog.All.Where(t =>
                t.IsSupported && TweakCatalog.IsAppearance(t.Category) == appearance
            )
        )
            Items.Add(new TweakItem(tweak));

        foreach (var group in Items.GroupBy(i => i.Tweak.Category).OrderBy(g => g.Key))
            Groups.Add(new TweakGroup(group.Key, group.ToList()));
        _selectedGroup = Groups[0];

        _ = RefreshAsync();
    }

    public string Title { get; }

    public string Subtitle { get; }

    public bool HasRecommended => Items.Any(i => i.Recommended);

    public ObservableCollection<TweakItem> Items { get; } = [];

    public ObservableCollection<TweakGroup> Groups { get; } = [];

    [ObservableProperty]
    private TweakGroup _selectedGroup;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>What is happening before tweaks run, such as making the restore point.</summary>
    [ObservableProperty]
    private string _notice = "";

    [ObservableProperty]
    private bool _needsExplorerRestart;

    [ObservableProperty]
    private bool _needsReboot;

    public async Task RefreshAsync()
    {
        foreach (var item in Items)
            item.Status = await Task.Run(() => _engine.GetStatus(item.Tweak));
        UpdateSummary();
    }

    [RelayCommand]
    private async Task Toggle(TweakItem item)
    {
        if (item.IsBusy)
            return;

        if (item.IsOn)
            await UndoAsync(item);
        else if (await EnsureRestorePointAsync())
            await ApplyAsync(item);
        else
            item.NotifyState();

        UpdateSummary();
    }

    [RelayCommand]
    private async Task ApplyRecommended()
    {
        if (!await EnsureRestorePointAsync())
            return;
        IsBusy = true;
        try
        {
            foreach (var item in Items.Where(i => i.Recommended && !i.IsOn))
                await ApplyAsync(item);
        }
        finally
        {
            IsBusy = false;
            UpdateSummary();
        }
    }

    [RelayCommand]
    private async Task ApplyGroup(TweakGroup group)
    {
        if (!await EnsureRestorePointAsync())
            return;
        IsBusy = true;
        try
        {
            foreach (var item in group.Items.Where(i => i.Recommended && !i.IsOn))
                await ApplyAsync(item);
        }
        finally
        {
            IsBusy = false;
            UpdateSummary();
        }
    }

    [RelayCommand]
    private async Task UndoGroup(TweakGroup group)
    {
        IsBusy = true;
        try
        {
            foreach (var item in group.Items.Where(i => _engine.HasUndo(i.Tweak)))
                await UndoAsync(item);
        }
        finally
        {
            IsBusy = false;
            UpdateSummary();
        }
    }

    [RelayCommand]
    private async Task UndoAll()
    {
        var confirmed = await Dialogs.ConfirmAsync(
            Tr.Get("Optimize.ConfirmUndoAll.Title"),
            Tr.Get("Optimize.ConfirmUndoAll.Message"),
            Tr.Get("Optimize.ConfirmUndoAll.Primary")
        );
        if (!confirmed)
            return;

        IsBusy = true;
        try
        {
            foreach (var item in Items.Where(i => _engine.HasUndo(i.Tweak)))
                await UndoAsync(item);
        }
        finally
        {
            IsBusy = false;
            UpdateSummary();
        }
    }

    [RelayCommand]
    private async Task ExportProfile()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "Trevunix-tweaks.json",
            Filter = Tr.Get("Optimize.Profile.Filter") + "|*.json",
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            TweakProfile.Save(dialog.FileName, Items.Where(i => i.IsOn).Select(i => i.Tweak.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.InfoAsync(Tr.Get("Optimize.Profile.Export"), ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportProfile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Tr.Get("Optimize.Profile.Filter") + "|*.json",
        };
        if (dialog.ShowDialog() != true)
            return;

        IReadOnlyList<Tweak> tweaks;
        try
        {
            tweaks = TweakProfile.Load(dialog.FileName);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await Dialogs.InfoAsync(Tr.Get("Optimize.Profile.Import"), ex.Message);
            return;
        }

        // Only this page's tweaks; the other page (Optimize or Appearance) imports its own.
        var ids = tweaks.Select(t => t.Id).ToHashSet();
        var toApply = Items.Where(i => ids.Contains(i.Tweak.Id) && !i.IsOn).ToList();
        var confirmed = await Dialogs.ConfirmAsync(
            Tr.Get("Optimize.Profile.Import"),
            Tr.Format("Optimize.Profile.Confirm", toApply.Count),
            Tr.Get("Optimize.Profile.Apply")
        );
        if (!confirmed || toApply.Count == 0 || !await EnsureRestorePointAsync())
            return;

        IsBusy = true;
        try
        {
            foreach (var item in toApply)
                await ApplyAsync(item);
        }
        finally
        {
            IsBusy = false;
            UpdateSummary();
        }
    }

    [RelayCommand]
    private void RestartExplorer()
    {
        foreach (var explorer in Process.GetProcessesByName("explorer"))
        {
            try
            {
                explorer.Kill();
                explorer.WaitForExit(5000);
            }
            catch (Exception ex)
                when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        NeedsExplorerRestart = false;
    }

    private async Task<bool> EnsureRestorePointAsync()
    {
        IsBusy = true;
        Notice = Tr.Get("RestorePoint.Creating");
        try
        {
            return await _restorePoints.EnsureAsync();
        }
        finally
        {
            Notice = "";
            IsBusy = false;
        }
    }

    private async Task ApplyAsync(TweakItem item)
    {
        item.IsBusy = true;
        try
        {
            var outcome = await Task.Run(() => _engine.Apply(item.Tweak));
            if (outcome.Success)
                new ActivityLog().Add(HistoryKind.TweakApplied, detail: item.Tweak.Id);
            item.Error = outcome.Success
                ? ""
                : Tr.Format("Optimize.Failed", string.Join("; ", outcome.Errors.Distinct()));
            NoteFollowUp(item.Tweak);
        }
        finally
        {
            item.Status = await Task.Run(() => _engine.GetStatus(item.Tweak));
            item.NotifyState();
            item.IsBusy = false;
        }
    }

    private async Task UndoAsync(TweakItem item)
    {
        if (!_engine.HasUndo(item.Tweak))
        {
            item.Error = Tr.Get("Optimize.NoUndo");
            item.NotifyState();
            return;
        }

        item.IsBusy = true;
        try
        {
            var outcome = await Task.Run(() => _engine.Undo(item.Tweak));
            if (outcome.Success)
                new ActivityLog().Add(HistoryKind.TweakUndone, detail: item.Tweak.Id);
            item.Error = outcome.Success
                ? ""
                : Tr.Format("Optimize.Failed", string.Join("; ", outcome.Errors.Distinct()));
            NoteFollowUp(item.Tweak);
        }
        finally
        {
            item.Status = await Task.Run(() => _engine.GetStatus(item.Tweak));
            item.NotifyState();
            item.IsBusy = false;
        }
    }

    private void NoteFollowUp(Tweak tweak)
    {
        if (tweak.After == TweakAfter.RestartExplorer)
            NeedsExplorerRestart = true;
        else if (tweak.After == TweakAfter.Reboot)
            NeedsReboot = true;
    }

    private void UpdateSummary()
    {
        Summary = Tr.Format("Optimize.Summary", Items.Count(i => i.IsOn), Items.Count);
        foreach (var group in Groups)
            group.Refresh();
    }
}
