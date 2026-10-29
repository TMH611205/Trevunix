using System.Windows;
using System.Windows.Controls;
using Trevunix.ViewModels;

namespace Trevunix.Views;

public partial class ResourcesView : UserControl
{
    public ResourcesView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        (DataContext as ResourcesViewModel)?.OnShown();

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        (DataContext as ResourcesViewModel)?.OnHidden();
}
