using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Trevunix.Core.Common;
using Trevunix.Core.DiskUsage;
using Trevunix.Core.Files;
using Trevunix.Localization;
using Trevunix.Services;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

/// <summary>One row in the folders tab, the largest-files tab, or the file-types tab.</summary>
public sealed class UsageRow
{
    private UsageRow(
        string name,
        string? path,
        long size,
        double share,
        long files,
        DateTime? modified,
        SymbolRegular icon,
        UsageNode? node,
        string? folder = null
    )
    {
        Name = name;
        Path = path;
        Size = size;
        Share = share;
        Files = files;
        Modified = modified?.ToLocalTime().ToString("g", Tr.Culture) ?? "";
        Icon = icon;
        Node = node;
        Folder = folder ?? "";
    }

    public string Name { get; }
    public string? Path { get; }
    public long Size { get; }
    public double Share { get; }
    public double SharePercent => Share * 100;
    public long Files { get; }
    public string Modified { get; }
    public SymbolRegular Icon { get; }
    public string Folder { get; }
    public UsageNode? Node { get; }

    public bool CanDrillDown => Node is { IsDirectory: true, Children.Count: > 0 };
    public bool HasPath => Path is not null && Node?.Kind != UsageNodeKind.Group;

    public static UsageRow FromNode(UsageNode node) =>
        new(
            node.Kind == UsageNodeKind.Group ? Tr.Format("Disk.SmallFiles", node.Name) : node.Name,
            node.FullPath,
            node.Size,
            node.ShareOfParent,
            node.FileCount,
            node.Kind == UsageNodeKind.Group ? null : node.LastWriteUtc,
            node.Kind switch
            {
                UsageNodeKind.Directory => SymbolRegular.Folder24,
                UsageNodeKind.Group => SymbolRegular.DocumentMultiple24,
                _ => SymbolRegular.Document24,
            },
            node
        );

    public static UsageRow FromFile(LargeFile file, long total) =>
        new(
            file.Name,
            file.FullPath,
            file.Size,
            total > 0 ? (double)file.Size / total : 0,
            1,
            file.LastWriteUtc,
            SymbolRegular.Document24,
            null,
            file.Folder
        );

    public static UsageRow FromType(FileTypeUsage type, long total) =>
        new(
            type.Extension.Length == 0 ? Tr.Get("Disk.NoExtension") : type.Extension,
            null,
            type.Size,
            total > 0 ? (double)type.Size / total : 0,
            type.Count,
            null,
            SymbolRegular.DocumentCopy24,
            null
        );
}

public sealed partial class DiskUsageViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private ScanResult? _result;

    public DiskUsageViewModel() => RefreshDrives();

    public ObservableCollection<DriveCard> Drives { get; } = [];
    public ObservableCollection<UsageRow> Rows { get; } = [];
    public ObservableCollection<UsageRow> LargestFiles { get; } = [];
    public ObservableCollection<UsageRow> FileTypes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _hasResult;

    [ObservableProperty]
    private string _scanTitle = "";

    [ObservableProperty]
    private string _scanStats = "";

    [ObservableProperty]
    private string _scanPath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp))]
    private UsageNode? _current;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _warning = "";

    [ObservableProperty]
    private UsageRow? _selectedRow;

    public bool IsIdle => !IsScanning && !HasResult;

    public bool CanGoUp => Current?.Parent is not null;

    public void RefreshDrives()
    {
        Drives.Clear();
        foreach (var drive in DriveSummary.GetFixedDrives())
            Drives.Add(new DriveCard(drive));
    }

    [RelayCommand]
    private Task ScanDrive(DriveCard card) => ScanAsync(card.Drive.RootPath);

    [RelayCommand]
    private Task PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = Tr.Get("Disk.PickFolder") };
        return dialog.ShowDialog() == true ? ScanAsync(dialog.FolderName) : Task.CompletedTask;
    }

    [RelayCommand]
    private Task Rescan() =>
        _result is null ? Task.CompletedTask : ScanAsync(_result.Root.FullPath);

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void Back()
    {
        HasResult = false;
        _result = null;
        Rows.Clear();
        LargestFiles.Clear();
        FileTypes.Clear();
        RefreshDrives();
    }

    public async Task ScanAsync(string rootPath)
    {
        if (IsScanning)
            return;

        _cts = new CancellationTokenSource();
        IsScanning = true;
        HasResult = false;
        ScanTitle = Tr.Format("Disk.Scanning", rootPath);
        ScanStats = "";
        ScanPath = "";

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanStats = Tr.Format(
                "Disk.ScanStats",
                p.Files.ToString("N0", Tr.Culture),
                p.Folders.ToString("N0", Tr.Culture),
                ByteSize.Format(p.Bytes, Tr.Culture)
            );
            ScanPath = p.CurrentPath;
        });

        try
        {
            _result = await new DiskUsageScanner().ScanAsync(rootPath, progress, _cts.Token);
            ShowResult(_result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.InfoAsync(Tr.Get("Disk.Title"), ex.Message);
        }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void ShowResult(ScanResult result)
    {
        var root = result.Root;
        Summary = Tr.Format(
            "Disk.Summary",
            ByteSize.Format(root.Size, Tr.Culture),
            root.FileCount.ToString("N0", Tr.Culture),
            root.FolderCount.ToString("N0", Tr.Culture),
            result.Elapsed.TotalSeconds.ToString("0.0", Tr.Culture)
        );
        Warning =
            result.Cancelled ? Tr.Get("Disk.Partial")
            : root.Incomplete ? Tr.Get("Disk.Incomplete")
            : "";

        LargestFiles.Clear();
        foreach (var file in result.LargestFiles)
            LargestFiles.Add(UsageRow.FromFile(file, root.Size));

        FileTypes.Clear();
        foreach (var type in result.FileTypes.Take(100))
            FileTypes.Add(UsageRow.FromType(type, root.Size));

        ShowFolder(root);
        HasResult = true;
    }

    private void ShowFolder(UsageNode node)
    {
        Current = node;
        Rows.Clear();
        foreach (var child in node.Children)
            Rows.Add(UsageRow.FromNode(child));
    }

    [RelayCommand]
    private void Open(UsageRow? row)
    {
        if (row is null)
            return;

        if (row.CanDrillDown)
        {
            ShowFolder(row.Node!);
            return;
        }

        if (row.HasPath)
            StartShell(row.Path!);
    }

    [RelayCommand]
    private void Up()
    {
        if (Current?.Parent is { } parent)
            ShowFolder(parent);
    }

    [RelayCommand]
    private void ShowInExplorer(UsageRow? row)
    {
        if (row?.Path is not { } path)
            return;

        if (row.Node?.Kind == UsageNodeKind.Group)
            StartShell(path);
        else
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    [RelayCommand]
    private void CopyPath(UsageRow? row)
    {
        if (row?.Path is { } path)
            Clipboard.SetText(path);
    }

    [RelayCommand]
    private async Task Delete(UsageRow? row)
    {
        if (row is null || !row.HasPath)
            return;

        var confirmed = await Dialogs.ConfirmAsync(
            Tr.Get("Disk.DeleteConfirm.Title"),
            Tr.Format(
                "Disk.DeleteConfirm.Message",
                row.Name,
                ByteSize.Format(row.Size, Tr.Culture)
            ),
            Tr.Get("Common.Delete")
        );
        if (!confirmed)
            return;

        var path = row.Path!;
        var ok = await Task.Run(() => RecycleBin.Send(path));
        if (!ok)
        {
            await Dialogs.InfoAsync(
                Tr.Get("Disk.DeleteConfirm.Title"),
                Tr.Format("Disk.DeleteFailed", row.Name)
            );
            return;
        }

        if (row.Node is { } node)
        {
            node.RemoveFromTree();
            ShowFolder(Current!);
        }

        var inLargest = LargestFiles.FirstOrDefault(r => r.Path == path);
        if (inLargest is not null)
            LargestFiles.Remove(inLargest);
    }

    private static void StartShell(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
