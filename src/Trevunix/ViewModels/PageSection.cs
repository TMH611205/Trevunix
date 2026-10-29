using CommunityToolkit.Mvvm.ComponentModel;
using Trevunix.Localization;
using Wpf.Ui.Controls;

namespace Trevunix.ViewModels;

/// <summary>
///     One entry in a page's left section list. The page shows the selected section's view model
///     through a DataTemplate, so each section is just its own view model.
/// </summary>
public abstract partial class PageSection(string key, SymbolRegular icon) : ObservableObject
{
    public string Title { get; } = Tr.Get($"Section.{key}");
    public string Description { get; } = Tr.Get($"Section.{key}.Description");
    public SymbolRegular Icon { get; } = icon;

    /// <summary>Short live value shown at the right of the list entry, such as "42%".</summary>
    [ObservableProperty]
    private string _badge = "";
}
