using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trevunix.Core.Browsers;
using Trevunix.Localization;
using Trevunix.Services;

namespace Trevunix.ViewModels;

public sealed partial class ExtensionItem(BrowserExtension extension) : ObservableObject
{
    public BrowserExtension Extension { get; } = extension;
    public string Name => Extension.Name;

    public string Details =>
        string.Join(
            " · ",
            new[] { Extension.Browser.Name, Extension.ProfileName, Extension.Version }.Where(s =>
                s.Length > 0
            )
        );

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
///     The "Browser extensions" tab: what the owner installed in Chrome, Edge, Brave, Vivaldi,
///     Cốc Cốc, Opera and Firefox, removable in one go. Browsers must be closed for that, so
///     Trevunix offers to close the ones that are open.
/// </summary>
public sealed partial class BrowserExtensionsViewModel : ObservableObject
{
    public BrowserExtensionsViewModel()
    {
        View = CollectionViewSource.GetDefaultView(Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription("Extension.Browser.Name"));
    }

    public ObservableCollection<ExtensionItem> Items { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(RemoveSelectedCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _result = "";

    [ObservableProperty]
    private bool _loaded;

    public string RemoveText => Tr.Format("Extensions.Remove", Items.Count(i => i.IsSelected));

    private bool CanWork => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task LoadAsync()
    {
        IsBusy = true;
        Status = Tr.Get("Extensions.Loading");
        try
        {
            var found = await Task.Run(BrowserExtensions.List);
            foreach (var old in Items)
                old.PropertyChanged -= OnItemChanged;
            Items.Clear();
            foreach (var extension in found)
            {
                var item = new ExtensionItem(extension);
                item.PropertyChanged += OnItemChanged;
                Items.Add(item);
            }
            Loaded = true;
            if (Items.Count == 0 && Result.Length == 0)
                Result = Tr.Get("Extensions.None");
        }
        finally
        {
            Status = "";
            IsBusy = false;
            OnPropertyChanged(nameof(RemoveText));
        }
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task RemoveSelectedAsync()
    {
        var chosen = Items.Where(i => i.IsSelected).Select(i => i.Extension).ToList();
        if (chosen.Count == 0)
            return;

        var browsers = chosen.Select(e => e.Browser).DistinctBy(b => b.ProcessName).ToList();
        var open = await Task.Run(() => browsers.Where(BrowserExtensions.IsRunning).ToList());
        var message = Tr.Format("Extensions.Confirm.Message", chosen.Count);
        if (open.Count > 0)
            message +=
                Environment.NewLine
                + Environment.NewLine
                + Tr.Format(
                    "Extensions.Confirm.Close",
                    string.Join(", ", open.Select(b => b.Name))
                );
        if (
            !await Dialogs.ConfirmAsync(
                Tr.Get("Extensions.Confirm.Title"),
                message,
                Tr.Get("Extensions.Confirm.Primary")
            )
        )
            return;

        IsBusy = true;
        Result = "";
        Status = Tr.Get("Extensions.Removing");
        int removed = 0,
            failed = 0;
        try
        {
            await Task.Run(() =>
            {
                foreach (var browser in open)
                    BrowserExtensions.Close(browser);
                foreach (var extension in chosen)
                {
                    if (BrowserExtensions.Remove(extension))
                        removed++;
                    else
                        failed++;
                }
            });
        }
        finally
        {
            Status = "";
            IsBusy = false;
        }

        Result =
            failed == 0
                ? Tr.Format("Extensions.Done", removed)
                : Tr.Format("Extensions.DoneSome", removed, failed);
        await LoadAsync();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExtensionItem.IsSelected))
            OnPropertyChanged(nameof(RemoveText));
    }
}
