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

public sealed class ProgramItem(InstalledProgram program)
{
    public InstalledProgram Program { get; } = program;
    public string Name => Program.DisplayName;
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

    public ProgramsViewModel(RestorePointGuard restorePoints)
    {
        _restorePoints = restorePoints;
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
        nameof(ForceUninstallCommand)
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
            Items.Clear();
            foreach (var program in _all)
                Items.Add(new ProgramItem(program));
            OnPropertyChanged(nameof(Summary));
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
            var program = item.Program;
            var all = _all;
            var (found, still) = await Task.Run(() =>
                (_leftovers.Find(program, all), InstalledPrograms.IsStillInstalled(program))
            );
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
