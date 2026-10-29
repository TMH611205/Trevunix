using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Trevunix.ViewModels;

namespace Trevunix.Views;

public partial class SystemToolsView : UserControl
{
    public SystemToolsView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        (DataContext as SystemToolsViewModel)?.OnShown();

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        (DataContext as SystemToolsViewModel)?.OnHidden();

    // Keep the repair output scrolled to the newest line.
    private void OnOutputLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox list || list.Tag is not null)
            return;
        list.Tag = true;
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, _) =>
        {
            if (list.Items.Count > 0)
                list.ScrollIntoView(list.Items[^1]);
        };
    }
}
