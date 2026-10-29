using System.Windows;
using Trevunix.Localization;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Trevunix.Services;

public static class Dialogs
{
    public static async Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primary,
            CloseButtonText = Tr.Get("Common.Cancel"),
            Owner = Application.Current.MainWindow,
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    public static async Task InfoAsync(string title, string message)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            Owner = Application.Current.MainWindow,
        };
        await box.ShowDialogAsync();
    }

    /// <summary>A scrollable read-only list, e.g. the files a cleanup would remove.</summary>
    public static async Task ListAsync(string title, IEnumerable<string> lines)
    {
        var list = new System.Windows.Controls.ListBox
        {
            ItemsSource = lines.ToList(),
            MaxHeight = 420,
            MinWidth = 640,
            FontSize = 12,
        };
        var box = new MessageBox
        {
            Title = title,
            Content = list,
            CloseButtonText = "OK",
            Owner = Application.Current.MainWindow,
            MaxWidth = 900,
        };
        await box.ShowDialogAsync();
    }
}
