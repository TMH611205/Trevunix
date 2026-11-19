using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.SystemInfo;
using Trevunix.Localization;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

public sealed class BatteryCard(BatteryInfo battery)
{
    public BatteryInfo Battery { get; } = battery;

    public string Title =>
        string.Join(" · ", new[] { Battery.Name, Battery.Manufacturer }.Where(s => s.Length > 0))
            is { Length: > 0 } title
            ? title
            : Tr.Get("Battery.Unnamed");

    public double HealthValue => Battery.HealthPercent ?? 0;

    public string Health => Battery.HealthPercent is { } h ? $"{h}%" : Tr.Get("Drives.NotReported");

    /// <summary>Under 80% of the original capacity is where batteries are usually called worn.</summary>
    public bool IsWorn => Battery.HealthPercent is < 80;

    public string HealthHint =>
        Battery.HealthPercent switch
        {
            null => "",
            >= 80 => Tr.Get("Battery.Good"),
            >= 60 => Tr.Get("Battery.Worn"),
            _ => Tr.Get("Battery.Replace"),
        };

    public string Capacity =>
        Battery.FullChargeCapacity is { } full && Battery.DesignCapacity is { } design
            ? Tr.Format(
                "Battery.CapacityValue",
                (full / 1000d).ToString("0.#", Tr.Culture),
                (design / 1000d).ToString("0.#", Tr.Culture)
            )
            : Tr.Get("Drives.NotReported");

    public string Cycles =>
        Battery.CycleCount is { } c ? c.ToString("N0", Tr.Culture) : Tr.Get("Drives.NotReported");

    public string Charge =>
        (Battery.ChargePercent is { } p ? $"{p}%" : "?")
        + " · "
        + Tr.Get(
            Battery.Charging ? "Battery.Charging"
            : Battery.PluggedIn ? "Battery.PluggedIn"
            : "Battery.OnBattery"
        );
}

public sealed partial class BatteryViewModel() : PageSection("Battery", SymbolRegular.Battery524)
{
    public ObservableCollection<BatteryCard> Batteries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    private bool CanLoad => !IsBusy;

    public void OnShown()
    {
        if (Batteries.Count == 0 && Status.Length == 0 && LoadCommand.CanExecute(null))
            LoadCommand.Execute(null);
    }

    /// <summary>Windows' own detailed battery history, written to an HTML file and opened.</summary>
    [RelayCommand]
    private async Task OpenReport()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "battery-report.html");
        var (code, _) = await Task.Run(() =>
            Trevunix.Core.Tools.CommandRunner.Run("powercfg.exe", "/batteryreport", "/output", path)
        );
        if (code == 0 && System.IO.File.Exists(path))
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }
            );
        else
            Status = Tr.Get("Battery.ReportFailed");
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var batteries = await Task.Run(BatteryInfo.Read);
            Batteries.Clear();
            foreach (var b in batteries)
                Batteries.Add(new BatteryCard(b));
            Status = Batteries.Count == 0 ? Tr.Get("Battery.None") : "";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
