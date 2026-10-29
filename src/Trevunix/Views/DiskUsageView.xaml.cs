using System.Windows.Controls;
using System.Windows.Input;
using Trevunix.ViewModels;

namespace Trevunix.Views;

public partial class DiskUsageView : UserControl
{
    public DiskUsageView() => InitializeComponent();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (
            sender is ListView { SelectedItem: UsageRow row }
            && DataContext is DiskUsageViewModel vm
        )
            vm.OpenCommand.Execute(row);
    }
}
