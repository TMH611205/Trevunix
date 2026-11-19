using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Apps;
using Trevunix.Core.Files;
using Trevunix.Localization;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

public sealed partial class ShortcutIssueRow(ShortcutIssue issue) : ObservableObject
{
    public ShortcutIssue Issue { get; } = issue;
    public string Name { get; } = System.IO.Path.GetFileNameWithoutExtension(issue.Path);
    public string Detail { get; } =
        issue.Problem == ShortcutProblem.EmptyFolder
            ? Tr.Format("Shortcuts.EmptyFolder", issue.Path)
            : Tr.Format("Shortcuts.Missing", issue.Target);

    [ObservableProperty]
    private bool _isChecked = true;
}

public sealed partial class ShortcutsViewModel()
    : PageSection("Shortcuts", SymbolRegular.LinkDismiss24)
{
    public ObservableCollection<ShortcutIssueRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(RemoveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    private bool CanAct => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Scan()
    {
        IsBusy = true;
        try
        {
            var issues = await Task.Run(() => BrokenShortcuts.Find());
            Rows.Clear();
            foreach (var issue in issues)
                Rows.Add(new ShortcutIssueRow(issue));
            Status =
                Rows.Count == 0
                    ? Tr.Get("Shortcuts.None")
                    : Tr.Format("Shortcuts.Found", Rows.Count);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>To the Recycle Bin, so a shortcut removed by mistake can come back.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Remove()
    {
        var chosen = Rows.Where(r => r.IsChecked).ToList();
        if (chosen.Count == 0)
            return;

        IsBusy = true;
        try
        {
            var removed = 0;
            foreach (var row in chosen)
            {
                if (await Task.Run(() => RecycleBin.Send(row.Issue.Path)))
                {
                    Rows.Remove(row);
                    removed++;
                }
            }
            Status = Tr.Format("Shortcuts.Removed", removed, chosen.Count - removed);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
