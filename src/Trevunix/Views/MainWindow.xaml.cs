using System.ComponentModel;
using System.Windows.Controls;
using Trevunix.Core.Updates;
using Trevunix.Localization;
using Trevunix.ViewModels;
using Wpf.Ui.Controls;

namespace Trevunix.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private bool _syncing;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        SettingsNav.ItemsSource = new[] { vm.SettingsItem };
        MainNav.SelectedItem = vm.Selected;
        vm.PropertyChanged += OnViewModelChanged;
    }

    public string VersionText =>
        $"v{UpdateService.CurrentVersion.ToString(3)} · {SettingsViewModel.Credit}";

    // The two sidebar lists act as one selection: picking in one clears the other.
    private void OnMainSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || MainNav.SelectedItem is not NavItem item)
            return;
        Select(item);
    }

    private void OnSettingsSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SettingsNav.SelectedItem is not NavItem item)
            return;
        Select(item);
    }

    private void Select(NavItem item)
    {
        _vm.Navigate(item);
        SyncLists();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Selected))
            SyncLists();
    }

    private void SyncLists()
    {
        _syncing = true;
        var isSettings = ReferenceEquals(_vm.Selected, _vm.SettingsItem);
        MainNav.SelectedItem = isSettings ? null : _vm.Selected;
        SettingsNav.SelectedItem = isSettings ? _vm.SettingsItem : null;
        _syncing = false;
    }
}
