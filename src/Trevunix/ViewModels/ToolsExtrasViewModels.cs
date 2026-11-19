using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Common;
using Trevunix.Core.Files;
using Trevunix.Core.Maintenance;
using Trevunix.Core.Storage;
using Trevunix.Core.SystemInfo;
using Trevunix.Core.Updates;
using Trevunix.Localization;
using Trevunix.Services;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

public sealed partial class DuplicateFile(string path, bool isChecked) : ObservableObject
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    [ObservableProperty]
    private bool _isChecked = isChecked;
}

public sealed class DuplicateGroupItem(DuplicateGroup group)
{
    public long Size { get; } = group.Size;

    // Every copy but the first is ticked, so one always stays by default.
    public ObservableCollection<DuplicateFile> Files { get; } =
        new(group.Paths.Select((p, i) => new DuplicateFile(p, i > 0)));

    public string Header =>
        Tr.Format("Duplicates.Group", Files.Count, ByteSize.Format(Size, Tr.Culture));
}

public sealed partial class DuplicatesViewModel()
    : PageSection("Duplicates", SymbolRegular.DocumentCopy24)
{
    private const int MaxGroups = 300;
    private CancellationTokenSource? _cts;

    public ObservableCollection<DuplicateGroupItem> Groups { get; } = [];

    [ObservableProperty]
    private string _folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(ScanCommand),
        nameof(CancelCommand),
        nameof(RemoveSelectedCommand)
    )]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    private bool CanAct => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Folder };
        if (dialog.ShowDialog() == true)
            Folder = dialog.FolderName;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Scan()
    {
        if (!Directory.Exists(Folder))
        {
            Status = Tr.Get("Duplicates.NoFolder");
            return;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        Groups.Clear();
        var progress = new Progress<long>(n =>
            Status = Tr.Format("Duplicates.Scanning", n.ToString("N0", Tr.Culture))
        );
        try
        {
            var found = await DuplicateFinder.FindAsync(Folder, 1024, progress, _cts.Token);
            foreach (var group in found.Take(MaxGroups))
                Groups.Add(new DuplicateGroupItem(group));
            Status =
                found.Count == 0
                    ? Tr.Get("Duplicates.None")
                    : Tr.Format(
                        "Duplicates.Found",
                        found.Count,
                        ByteSize.Format(found.Sum(g => g.WastedBytes), Tr.Culture)
                    );
        }
        catch (OperationCanceledException)
        {
            Status = Tr.Get("Duplicates.Cancelled");
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RemoveSelected()
    {
        var chosen = Groups.SelectMany(g => g.Files.Where(f => f.IsChecked)).ToList();
        if (chosen.Count == 0)
            return;
        if (Groups.Any(g => g.Files.All(f => f.IsChecked)))
        {
            await Dialogs.InfoAsync(Tr.Get("Duplicates.Remove"), Tr.Get("Duplicates.KeepOne"));
            return;
        }

        var bytes = Groups.Sum(g => g.Size * g.Files.Count(f => f.IsChecked));
        var confirmed = await Dialogs.ConfirmAsync(
            Tr.Get("Duplicates.Remove"),
            Tr.Format("Duplicates.Confirm", chosen.Count, ByteSize.Format(bytes, Tr.Culture)),
            Tr.Get("Duplicates.Remove")
        );
        if (!confirmed)
            return;

        IsBusy = true;
        try
        {
            long freed = 0;
            var failed = 0;
            foreach (var group in Groups.ToList())
            {
                foreach (var file in group.Files.Where(f => f.IsChecked).ToList())
                {
                    if (await Task.Run(() => RecycleBin.Send(file.Path)))
                    {
                        freed += group.Size;
                        group.Files.Remove(file);
                    }
                    else
                        failed++;
                }
                if (group.Files.Count < 2)
                    Groups.Remove(group);
            }

            new ActivityLog().Add(HistoryKind.DuplicatesRemoved, freed);
            Status = Tr.Format("Duplicates.Removed", ByteSize.Format(freed, Tr.Culture));
            if (failed > 0)
                Status += " " + Tr.Format("Duplicates.Failed", failed);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private static void OpenLocation(DuplicateFile? file)
    {
        if (file is not null && File.Exists(file.Path))
            Process.Start("explorer.exe", $"/select,\"{file.Path}\"");
    }
}

public sealed partial class ReportViewModel() : PageSection("Report", SymbolRegular.DocumentText24)
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(CopyCommand), nameof(SaveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand), nameof(SaveCommand))]
    private string _text = "";

    private bool CanCreate => !IsBusy;
    private bool HasText => !IsBusy && Text.Length > 0;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task Create()
    {
        IsBusy = true;
        try
        {
            var hardware = Task.Run(HardwareInfo.Read);
            var disks = DriveHealth.ReadAsync();
            Text = SystemReport.Build(
                await hardware,
                await disks,
                SystemReport.ReadyFixedDrives(),
                UpdateService.CurrentVersion.ToString(),
                DateTime.Now
            );
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasText))]
    private void Copy() => Clipboard.SetText(Text);

    [RelayCommand(CanExecute = nameof(HasText))]
    private async Task Save()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Trevunix-report-{DateTime.Now:yyyyMMdd}.txt",
            Filter = "Text|*.txt",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.InfoAsync(Tr.Get("Report.Save"), ex.Message);
        }
    }
}

public sealed record HistoryRow(string When, string What);

public sealed partial class HistoryViewModel() : PageSection("History", SymbolRegular.History24)
{
    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [ObservableProperty]
    private string _total = "";

    public void Refresh()
    {
        var entries = new ActivityLog().Read();
        Rows.Clear();
        foreach (var e in entries)
            Rows.Add(new HistoryRow(e.At.ToString("g", Tr.Culture), Describe(e)));

        var freed = entries.Sum(e => e.FreedBytes);
        Total =
            entries.Count == 0
                ? Tr.Get("History.Empty")
                : Tr.Format("History.Total", ByteSize.Format(freed, Tr.Culture));
    }

    private static string Describe(ActivityEntry e) =>
        e.Kind switch
        {
            HistoryKind.TweakApplied or HistoryKind.TweakUndone => Tr.Format(
                $"History.{e.Kind}",
                Tr.Get($"Tweak.{e.Detail}")
            ),
            _ => Tr.Format($"History.{e.Kind}", ByteSize.Format(e.FreedBytes, Tr.Culture)),
        };
}

public sealed partial class ShredderViewModel()
    : PageSection("Shredder", SymbolRegular.DeleteDismiss24)
{
    public ObservableCollection<string> Paths { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShredCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    [RelayCommand]
    private void AddFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
        if (dialog.ShowDialog() == true)
            Add(dialog.FileNames);
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = true };
        if (dialog.ShowDialog() == true)
            Add(dialog.FolderNames);
    }

    [RelayCommand]
    private void Clear() => Paths.Clear();

    private void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Shredder.IsRefused(path))
                Status = Tr.Format("Shredder.Refused", path);
            else if (!Paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                Paths.Add(path);
        }
        ShredCommand.NotifyCanExecuteChanged();
    }

    private bool CanShred => !IsBusy && Paths.Count > 0;

    [RelayCommand(CanExecute = nameof(CanShred))]
    private async Task Shred()
    {
        var confirmed = await Dialogs.ConfirmAsync(
            Tr.Get("Section.Shredder"),
            Tr.Format("Shredder.Confirm", Paths.Count),
            Tr.Get("Shredder.Shred")
        );
        if (!confirmed)
            return;

        IsBusy = true;
        try
        {
            var result = await Task.Run(() => Shredder.Shred(Paths.ToList()));
            Status = Tr.Format("Shredder.Done", result.Shredded, result.Failed);
            Paths.Clear();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
