using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Network;
using Trevunix.Core.Recovery;
using Trevunix.Core.Tweaks;
using Trevunix.Core.Updates;
using Trevunix.Localization;
using Trevunix.Services;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

public sealed class BlockedAppRow(BlockedApp app)
{
    public string Path { get; } = app.Path;
    public string Name { get; } = System.IO.Path.GetFileName(app.Path);
    public System.Windows.Media.ImageSource? Icon => IconService.ForFile(app.Path);
}

public sealed partial class FirewallViewModel()
    : PageSection("Firewall", SymbolRegular.ShieldProhibited24)
{
    public ObservableCollection<BlockedAppRow> Apps { get; } = [];

    [ObservableProperty]
    private string _status = "";

    public async Task RefreshAsync()
    {
        try
        {
            var apps = await Task.Run(FirewallBlock.List);
            Apps.Clear();
            foreach (var app in apps)
                Apps.Add(new BlockedAppRow(app));
            Status = Apps.Count == 0 ? Tr.Get("Firewall.None") : "";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private async Task Add()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Tr.Get("Firewall.Filter") + "|*.exe",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            await Task.Run(() => FirewallBlock.BlockApp(dialog.FileName));
        }
        catch (Exception ex)
            when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            await Dialogs.InfoAsync(Title, ex.Message);
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task Remove(BlockedAppRow? row)
    {
        if (row is null)
            return;
        try
        {
            await Task.Run(() => FirewallBlock.UnblockApp(row.Path));
        }
        catch (Exception ex)
            when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            await Dialogs.InfoAsync(Title, ex.Message);
        }
        await RefreshAsync();
    }
}

public sealed partial class ContextMenuRow(ContextMenuEntry entry) : ObservableObject
{
    public ContextMenuEntry Entry { get; private set; } = entry;
    public string Name => Entry.Name;
    public string Place { get; } = Tr.Get($"ContextMenu.Place.{entry.Place}");
    public string Detail => Entry.Clsid ?? Entry.KeyPath;

    [ObservableProperty]
    private bool _isOn = entry.Enabled;

    [ObservableProperty]
    private string _error = "";

    partial void OnIsOnChanged(bool value)
    {
        if (value == Entry.Enabled)
            return;
        try
        {
            ContextMenu.SetEnabled(Entry, value);
            Entry = Entry with { Enabled = value };
            Error = "";
        }
        catch (Exception ex)
            when (ex
                    is UnauthorizedAccessException
                        or System.Security.SecurityException
                        or IOException
            )
        {
            Error = ex.Message;
            IsOn = Entry.Enabled;
        }
    }
}

public sealed partial class ContextMenuViewModel()
    : PageSection("ContextMenu", SymbolRegular.TextBulletListSquare24)
{
    public ObservableCollection<ContextMenuRow> Rows { get; } = [];

    public async Task RefreshAsync()
    {
        var entries = await Task.Run(ContextMenu.List);
        Rows.Clear();
        foreach (var entry in entries)
            Rows.Add(new ContextMenuRow(entry));
    }

    [RelayCommand]
    private static void RestartExplorer()
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
    }
}

public sealed record RestorePointRow(RestorePointInfo Point)
{
    public string When => Point.Created.ToString("g", Tr.Culture);
    public string Description => Point.Description;
}

public sealed partial class RestorePointsViewModel()
    : PageSection("RestorePoints", SymbolRegular.History24)
{
    public ObservableCollection<RestorePointRow> Points { get; } = [];

    public IReadOnlyList<int> SizeChoices { get; } = [3, 5, 10, 15, 20];

    [ObservableProperty]
    private int _maxPercent = 10;

    [ObservableProperty]
    private RestorePointRow? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(CreateCommand),
        nameof(DeleteCommand),
        nameof(ApplySizeCommand)
    )]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    private bool CanAct => !IsBusy;

    public async Task RefreshAsync()
    {
        var points = await Task.Run(RestorePoints.List);
        Points.Clear();
        foreach (var p in points)
            Points.Add(new RestorePointRow(p));
        if (Points.Count == 0)
            Status = Tr.Get("RestorePoints.None");
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Create()
    {
        IsBusy = true;
        Status = Tr.Get("RestorePoint.Creating");
        try
        {
            var ok = await Task.Run(() => RestorePoints.CreateNow(Tr.Get("RestorePoints.Manual")));
            Status = Tr.Get(ok ? "RestorePoints.Created" : "RestorePoints.Failed");
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Delete()
    {
        if (Selected is not { } row)
            return;
        var confirmed = await Dialogs.ConfirmAsync(
            Title,
            Tr.Format("RestorePoints.ConfirmDelete", row.Description, row.When),
            Tr.Get("RestorePoints.Delete")
        );
        if (!confirmed)
            return;

        IsBusy = true;
        try
        {
            var ok = await Task.Run(() => RestorePoints.Delete(row.Point.Sequence));
            Status = ok ? "" : Tr.Get("RestorePoints.DeleteFailed");
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ApplySize()
    {
        IsBusy = true;
        try
        {
            var percent = MaxPercent;
            var ok = await Task.Run(() => RestorePoints.SetMaxPercent(percent));
            Status = ok
                ? Tr.Format("RestorePoints.SizeSet", percent)
                : Tr.Get("RestorePoints.SizeFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Restoring needs Windows' own wizard: it reboots and cannot run inside an app.</summary>
    [RelayCommand]
    private static void OpenSystemRestore() =>
        Process.Start(new ProcessStartInfo("rstrui.exe") { UseShellExecute = true });
}

public sealed partial class WindowsUpdateViewModel()
    : PageSection("WindowsUpdate", SymbolRegular.ArrowSync24)
{
    private readonly WindowsUpdatePause _pause = new();

    [ObservableProperty]
    private string _status = "";

    public void Refresh()
    {
        Status = _pause.PausedUntil(DateTime.UtcNow) is { } until
            ? Tr.Format("WindowsUpdate.PausedUntil", until.ToLocalTime().ToString("D", Tr.Culture))
            : Tr.Get("WindowsUpdate.Running");
    }

    [RelayCommand]
    private void Pause(string days)
    {
        Run(() =>
            _pause.Pause(
                int.Parse(days, System.Globalization.CultureInfo.InvariantCulture),
                DateTime.UtcNow
            )
        );
    }

    [RelayCommand]
    private void Resume() => Run(_pause.Resume);

    [RelayCommand]
    private static void OpenSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true });

    private void Run(Action action)
    {
        try
        {
            action();
            Refresh();
        }
        catch (Exception ex)
            when (ex
                    is UnauthorizedAccessException
                        or System.Security.SecurityException
                        or IOException
            )
        {
            Status = ex.Message;
        }
    }
}
