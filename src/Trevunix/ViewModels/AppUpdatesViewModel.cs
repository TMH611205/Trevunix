using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Monitoring;
using Trevunix.Core.Tools;
using Trevunix.Localization;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

public sealed partial class AppUpdateRow(AppUpdate update) : ObservableObject
{
    public AppUpdate Update { get; } = update;
    public string Name => Update.Name;
    public string Versions => $"{Update.Version} → {Update.Available} · {Update.Id}";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isUpdating;
}

/// <summary>
///     Tools > App updates: the apps winget can update, each with its own Update button, plus
///     update all. The home page card shows the same list and stays in step with this one.
/// </summary>
public sealed partial class AppUpdatesViewModel : PageSection
{
    private const int MaxLines = 2000;
    private readonly Action<IReadOnlyList<AppUpdate>> _publish;
    private CancellationTokenSource? _cts;

    public AppUpdatesViewModel(
        IEnumerable<AppUpdate> known,
        Action<IReadOnlyList<AppUpdate>> publish
    )
        : base("AppUpdates", SymbolRegular.ArrowSync24)
    {
        _publish = publish;
        Fill(known);
    }

    public ObservableCollection<AppUpdateRow> Rows { get; } = [];
    public ObservableCollection<string> Output { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(RefreshCommand),
        nameof(UpdateCommand),
        nameof(UpdateAllCommand),
        nameof(CancelCommand)
    )]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    private bool CanAct => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        Status = Tr.Get("AppUpdates.Checking");
        try
        {
            var updates = await Task.Run(HealthScore.ListAppUpdates);
            if (updates is null)
            {
                Status = Tr.Get("AppUpdates.NoWinget");
                return;
            }
            Fill(updates);
            _publish(updates);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task UpdateAsync(AppUpdateRow row)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Output.Clear();
        try
        {
            await RunOneAsync(row, _cts.Token);
        }
        finally
        {
            Done();
        }
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task UpdateAllAsync()
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Output.Clear();
        try
        {
            foreach (var row in Rows.ToList())
            {
                if (_cts.IsCancellationRequested)
                    break;
                await RunOneAsync(row, _cts.Token);
            }
        }
        finally
        {
            Done();
        }
        // winget is the judge of what is left: some installers report failure yet update fine.
        await RefreshAsync();
    }

    private bool CanCancel => IsBusy && _cts is not null;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private async Task RunOneAsync(AppUpdateRow row, CancellationToken ct)
    {
        if (RepairCatalog.UpgradeOne(row.Update.Id) is not { } command)
        {
            row.Status = Tr.Get("AppUpdates.Failed");
            return;
        }

        row.IsUpdating = true;
        row.Status = Tr.Get("AppUpdates.Updating");
        Status = Tr.Format("AppUpdates.UpdatingName", row.Name);
        var dispatcher = Application.Current.Dispatcher;
        Output.Add($"> winget {command.Arguments}");
        try
        {
            var code = await CommandRunner.RunAsync(
                command.FileName,
                command.Arguments,
                command.Encoding,
                (line, replace) => dispatcher.InvokeAsync(() => AddLine(line, replace)),
                ct
            );
            row.Status = Tr.Get(code == 0 ? "AppUpdates.Done" : "AppUpdates.Failed");
        }
        catch (OperationCanceledException)
        {
            row.Status = Tr.Get("Repair.Cancelled");
        }
        catch (Exception ex)
            when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            row.Status = ex.Message;
        }
        finally
        {
            row.IsUpdating = false;
        }
    }

    private void Done()
    {
        IsBusy = false;
        _cts?.Dispose();
        _cts = null;
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void Fill(IEnumerable<AppUpdate> updates)
    {
        Rows.Clear();
        foreach (var update in updates)
            Rows.Add(new AppUpdateRow(update));
        Status =
            Rows.Count == 0 ? Tr.Get("AppUpdates.None") : Tr.Format("Home.AppUpdates", Rows.Count);
    }

    private void AddLine(string line, bool replace)
    {
        if (replace && Output.Count > 1)
            Output[^1] = line;
        else
            Output.Add(line);
        while (Output.Count > MaxLines)
            Output.RemoveAt(1);
    }
}
