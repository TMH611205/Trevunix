using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Startup;
using Trevunix.Localization;

namespace Trevunix.ViewModels;

/// <summary>One switch in the tasks or services list; flipping it applies the change at once.</summary>
public sealed partial class AutorunRow : ObservableObject
{
    private readonly Action<bool> _apply;
    private bool _applied;

    public AutorunRow(string name, string detail, bool on, Action<bool> apply)
    {
        Name = name;
        Detail = detail;
        _isOn = on;
        _applied = on;
        _apply = apply;
    }

    public string Name { get; }
    public string Detail { get; }

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private string _error = "";

    partial void OnIsOnChanged(bool value)
    {
        if (value == _applied)
            return;
        try
        {
            _apply(value);
            _applied = value;
            Error = "";
        }
        catch (Exception ex)
            when (ex
                    is UnauthorizedAccessException
                        or System.Security.SecurityException
                        or System.Runtime.InteropServices.COMException
                        or System.IO.IOException
            )
        {
            Error = ex.Message;
            IsOn = _applied;
        }
    }
}

/// <summary>The "Tasks and services" tab: third-party scheduled tasks and services.</summary>
public sealed partial class AutorunsViewModel : ObservableObject
{
    public ObservableCollection<AutorunRow> Tasks { get; } = [];
    public ObservableCollection<AutorunRow> Services { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _status = "";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var (tasks, services) = await Task.Run(() =>
            {
                IReadOnlyList<AutorunTask> t;
                try
                {
                    t = Autoruns.ListTasks();
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    t = [];
                }
                return (t, Autoruns.ListServices());
            });

            Tasks.Clear();
            foreach (var task in tasks)
                Tasks.Add(
                    new AutorunRow(
                        task.Path,
                        task.Command,
                        task.Enabled,
                        on => Autoruns.SetTaskEnabled(task.Path, on)
                    )
                );
            Services.Clear();
            foreach (var service in services)
                Services.Add(
                    new AutorunRow(
                        service.DisplayName,
                        string.Join(
                            " · ",
                            new[] { service.Company ?? "", service.Command }.Where(s =>
                                s.Length > 0
                            )
                        ),
                        service.Automatic,
                        on => Autoruns.SetServiceAutomatic(service.Name, on)
                    )
                );
            Status = Tr.Format("Autoruns.Summary", Tasks.Count, Services.Count);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
