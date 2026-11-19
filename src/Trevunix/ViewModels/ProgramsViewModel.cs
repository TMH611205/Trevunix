using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Apps;
using Trevunix.Core.Common;
using Trevunix.Localization;
using Trevunix.Services;

namespace Trevunix.ViewModels;

public sealed partial class ProgramItem(InstalledProgram program) : ObservableObject
{
    public InstalledProgram Program { get; } = program;
    public string Name => Program.DisplayName;

    /// <summary>Ticked for uninstalling several programs in one go.</summary>
    [ObservableProperty]
    private bool _isChecked;
    public ImageSource? Icon => IconService.ForFile(Program.IconPath);

    public string Details =>
        string.Join(
            " · ",
            new[]
            {
                Program.IsHidden ? Tr.Get("Programs.Hidden") : "",
                Program.Publisher ?? "",
                Program.Version ?? "",
                Program.SizeBytes > 0 ? ByteSize.Format(Program.SizeBytes, Tr.Culture) : "",
                Program.InstalledOn?.ToString("d", Tr.Culture) ?? "",
            }.Where(s => s.Length > 0)
        );
}

public sealed partial class LeftoverItem(Leftover leftover) : ObservableObject
{
    public Leftover Leftover { get; } = leftover;

    /// <summary>What to show as the main line: the path, or for a startup entry its name.</summary>
    public string Path =>
        Leftover.Kind == LeftoverKind.StartupEntry
            ? $@"{Leftover.Path}\{Leftover.Detail}"
            : Leftover.Path;

    public string Details =>
        string.Join(
            " · ",
            new[]
            {
                Tr.Get($"Programs.Leftover.{Leftover.Kind}"),
                Leftover.SizeBytes > 0 ? ByteSize.Format(Leftover.SizeBytes, Tr.Culture) : "",
                // A shortcut's target or a service's display name tells what it belongs to.
                Leftover.Kind
                    is LeftoverKind.Shortcut
                        or LeftoverKind.Service
                    ? Leftover.Detail ?? ""
                    : "",
                Leftover.Sure ? "" : Tr.Get("Programs.Leftover.ByName"),
            }.Where(s => s.Length > 0)
        );

    [ObservableProperty]
    private bool _isSelected = true;
}

/// <summary>
///     The "Programs" tab: desktop programs with a deep uninstall. The program's own
///     uninstaller runs first; then Trevunix looks for what it left behind and the owner
///     picks what to remove.
/// </summary>
public sealed partial class ProgramsViewModel : ObservableObject
{
    private readonly Leftovers _leftovers = new();
    private readonly RestorePointGuard _restorePoints;
    private IReadOnlyList<InstalledProgram> _all = [];

    /// <summary>The programs of a batch uninstall; leftovers are looked for across all of them.</summary>
    private List<InstalledProgram>? _batch;

    public ProgramsViewModel(RestorePointGuard restorePoints)
    {
        _restorePoints = restorePoints;
        SortChoices =
        [
            .. new[] { "Name", "Size", "Date", "Publisher" }.Select(k => new Choice<string>(
                k,
                Tr.Get($"Programs.Sort.{k}")
            )),
        ];
        _sortBy = SortChoices[0];
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o =>
            o is ProgramItem item
            && (ShowHidden || !item.Program.IsHidden)
            && (
                Search.Length == 0
                || item.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase)
                || (
                    item.Program.Publisher?.Contains(
                        Search,
                        StringComparison.CurrentCultureIgnoreCase
                    ) ?? false
                )
            );
    }

    public ObservableCollection<ProgramItem> Items { get; } = [];
    public ICollectionView View { get; }
    public ObservableCollection<LeftoverItem> LeftoverItems { get; } = [];

    public IReadOnlyList<Choice<string>> SortChoices { get; }

    [ObservableProperty]
    private Choice<string> _sortBy;

    partial void OnSortByChanged(Choice<string> value)
    {
        View.SortDescriptions.Clear();
        View.SortDescriptions.Add(
            value.Value switch
            {
                "Size" => new SortDescription("Program.SizeBytes", ListSortDirection.Descending),
                "Date" => new SortDescription("Program.InstalledOn", ListSortDirection.Descending),
                "Publisher" => new SortDescription(
                    "Program.Publisher",
                    ListSortDirection.Ascending
                ),
                _ => new SortDescription("Name", ListSortDirection.Ascending),
            }
        );
    }

    public int CheckedCount => Items.Count(i => i.IsChecked);

    public string UninstallCheckedText => Tr.Format("Programs.UninstallChecked", CheckedCount);

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProgramItem.IsChecked))
            return;
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(UninstallCheckedText));
        UninstallCheckedCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private string _search = "";

    /// <summary>Also list what Windows hides: system components and entries without an uninstaller.</summary>
    [ObservableProperty]
    private bool _showHidden;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(LoadCommand),
        nameof(UninstallCommand),
        nameof(ScanLeftoversCommand),
        nameof(RemoveLeftoversCommand),
        nameof(ForceUninstallCommand),
        nameof(UninstallCheckedCommand)
    )]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _result = "";

    /// <summary>The program being uninstalled; the leftovers panel shows while it is set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrent))]
    private ProgramItem? _current;

    /// <summary>The program's uninstaller ran but its entry is still there (cancelled, or still running).</summary>
    [ObservableProperty]
    private bool _stillInstalled;

    public bool HasCurrent => Current is not null;

    public string Summary =>
        Tr.Format(
            "Programs.Summary",
            Items.Count(i => !i.Program.IsHidden),
            Items.Count(i => i.Program.IsHidden)
        );

    partial void OnSearchChanged(string value) => View.Refresh();

    partial void OnShowHiddenChanged(bool value) => View.Refresh();

    public void OnShown()
    {
        if (Items.Count == 0 && LoadCommand.CanExecute(null))
            LoadCommand.Execute(null);
    }

    private bool CanWork => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            _all = await Task.Run(InstalledPrograms.List);
            foreach (var old in Items)
                old.PropertyChanged -= OnItemChanged;
            Items.Clear();
            foreach (var program in _all)
            {
                var item = new ProgramItem(program);
                item.PropertyChanged += OnItemChanged;
                Items.Add(item);
            }
            OnPropertyChanged(nameof(Summary));
            OnItemChanged(null, new PropertyChangedEventArgs(nameof(ProgramItem.IsChecked)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task UninstallAsync(ProgramItem item)
    {
        var confirmed = await Dialogs.ConfirmAsync(
            Tr.Get("Programs.Confirm.Title"),
            Tr.Format("Programs.Confirm.Message", item.Name)
                + (
                    item.Program.IsHidden
                        ? Environment.NewLine
                            + Environment.NewLine
                            + Tr.Get("Programs.Confirm.Hidden")
                        : ""
                ),
            Tr.Get("Programs.Uninstall")
        );
        if (!confirmed)
            return;

        if (!await BeginAsync(item))
            return;

        IsBusy = true;
        Status = Tr.Format("Programs.Running", item.Name);
        try
        {
            var ran = await InstalledPrograms.RunUninstallerAsync(item.Program);
            if (ran is null)
                Result = Tr.Get("Programs.NoUninstaller");
        }
        finally
        {
            IsBusy = false;
        }

        await ScanLeftoversAsync();
        if (!StillInstalled)
            await LoadAsync();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Programs-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}.csv",
            Filter = "CSV|*.csv",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            // With a BOM, Excel opens it as UTF-8 and Vietnamese names stay readable.
            await System.IO.File.WriteAllTextAsync(
                dialog.FileName,
                ProgramListExport.ToCsv(_all.Where(p => ShowHidden || !p.IsHidden)),
                new System.Text.UTF8Encoding(true)
            );
            Result = Tr.Format("Programs.Exported", dialog.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Result = ex.Message;
        }
    }

    private bool CanUninstallChecked => !IsBusy && CheckedCount > 0;

    /// <summary>
    ///     Runs the uninstallers of all ticked programs one after another (each waits for the
    ///     previous one), then shows the leftovers of all of them together for review.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUninstallChecked))]
    private async Task UninstallCheckedAsync()
    {
        var chosen = Items.Where(i => i.IsChecked).ToList();
        var confirmed = await Dialogs.ConfirmAsync(
            Tr.Get("Programs.Confirm.Title"),
            Tr.Format(
                "Programs.ConfirmBatch",
                chosen.Count,
                string.Join(Environment.NewLine, chosen.Select(i => "• " + i.Name))
            ),
            Tr.Get("Programs.Uninstall")
        );
        if (!confirmed)
            return;

        var batch = new ProgramItem(
            new InstalledProgram(
                Tr.Format("Programs.BatchName", chosen.Count),
                null,
                null,
                null,
                null,
                null,
                null,
                0,
                null,
                Microsoft.Win32.RegistryHive.CurrentUser,
                Microsoft.Win32.RegistryView.Registry64,
                ""
            )
        );
        if (!await BeginAsync(batch))
            return;

        IsBusy = true;
        var missing = new List<string>();
        try
        {
            for (var i = 0; i < chosen.Count; i++)
            {
                Status = Tr.Format("Programs.RunningBatch", i + 1, chosen.Count, chosen[i].Name);
                if (await InstalledPrograms.RunUninstallerAsync(chosen[i].Program) is null)
                    missing.Add(chosen[i].Name);
            }
        }
        finally
        {
            Status = "";
            IsBusy = false;
        }
        if (missing.Count > 0)
            Result = Tr.Format("Programs.NoUninstallerFor", string.Join(", ", missing));

        _batch = [.. chosen.Select(c => c.Program)];
        await ScanLeftoversAsync();
        if (!StillInstalled)
            await LoadAsync();
    }

    /// <summary>
    ///     For a program that is not in the list or will not uninstall: the owner picks its
    ///     folder, and Trevunix removes everything tied to it the same way as after an uninstaller,
    ///     closing whatever still runs from there.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task ForceUninstallAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Tr.Get("Programs.Force.Pick"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dialog.ShowDialog() != true)
            return;
        var folder = dialog.FolderName.TrimEnd('\\');

        // A listed program installed exactly there keeps its name and its Uninstall entry.
        var registered = _all.FirstOrDefault(p =>
            p.InstallLocation is { Length: > 0 } location
            && string.Equals(location.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase)
        );
        var program =
            registered
            ?? new InstalledProgram(
                System.IO.Path.GetFileName(folder),
                null,
                null,
                folder,
                null,
                null,
                null,
                0,
                null,
                Microsoft.Win32.RegistryHive.CurrentUser,
                Microsoft.Win32.RegistryView.Registry64,
                ""
            );
        var item = new ProgramItem(program);

        var confirmed = await Dialogs.ConfirmAsync(
            Tr.Get("Programs.Force.Title"),
            Tr.Format("Programs.Force.Message", folder),
            Tr.Get("Programs.Force.Primary")
        );
        if (!confirmed || !await BeginAsync(item))
            return;

        await ScanLeftoversAsync();
        if (LeftoverItems.Count == 0 && Result.Length == 0)
            Result = Tr.Get("Programs.Force.Nothing");
    }

    /// <summary>Opens the leftovers panel for a program, after a restore point when that is on.</summary>
    private async Task<bool> BeginAsync(ProgramItem item)
    {
        Current = item;
        Result = "";
        LeftoverItems.Clear();
        IsBusy = true;
        Status = Tr.Get("RestorePoint.Creating");
        try
        {
            if (await _restorePoints.EnsureAsync())
                return true;
        }
        finally
        {
            Status = "";
            IsBusy = false;
        }
        Close();
        return false;
    }

    /// <summary>Looks for leftovers; also offered again when the uninstaller was still busy.</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task ScanLeftoversAsync()
    {
        if (Current is not { } item)
            return;

        IsBusy = true;
        Status = Tr.Get("Programs.Scanning");
        try
        {
            IReadOnlyList<InstalledProgram> programs = _batch ?? [item.Program];
            var all = _all;
            var (found, still) = await Task.Run(() =>
            {
                var left = new List<Leftover>();
                var anyStill = false;
                foreach (var program in programs)
                {
                    // A program whose uninstaller was cancelled keeps its files; skip it.
                    if (InstalledPrograms.IsStillInstalled(program) && _batch is not null)
                    {
                        anyStill = true;
                        continue;
                    }
                    anyStill |= InstalledPrograms.IsStillInstalled(program);
                    left.AddRange(_leftovers.Find(program, all));
                }
                return (left.DistinctBy(l => (l.Kind, l.Path, l.Detail)).ToList(), anyStill);
            });
            StillInstalled = still;
            LeftoverItems.Clear();
            foreach (var leftover in found)
                LeftoverItems.Add(new LeftoverItem(leftover));
            if (LeftoverItems.Count == 0 && !still)
                Result = Tr.Format("Programs.Clean", item.Name);
        }
        finally
        {
            Status = "";
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task RemoveLeftoversAsync()
    {
        var chosen = LeftoverItems.Where(l => l.IsSelected).Select(l => l.Leftover).ToList();
        if (chosen.Count == 0)
            return;

        IsBusy = true;
        Status = Tr.Get("Programs.Removing");
        try
        {
            var outcome = await Task.Run(() => Leftovers.Remove(chosen));
            Result =
                outcome.Failed == 0
                    ? Tr.Format("Programs.Removed", outcome.Removed)
                    : Tr.Format("Programs.RemovedSome", outcome.Removed, outcome.Failed);
            if (outcome.RegistryBackup is { } backup)
                Result += " " + Tr.Format("Programs.Backup", backup);
        }
        finally
        {
            Status = "";
            IsBusy = false;
        }

        Close();
        await LoadAsync();
    }

    [RelayCommand]
    private void Close()
    {
        _batch = null;
        Current = null;
        StillInstalled = false;
        LeftoverItems.Clear();
    }

    [RelayCommand]
    private void SelectAllLeftovers()
    {
        var select = LeftoverItems.Any(l => !l.IsSelected);
        foreach (var item in LeftoverItems)
            item.IsSelected = select;
    }
}
