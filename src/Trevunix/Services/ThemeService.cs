using System.Windows;
using Trevunix.Core.Settings;
using Wpf.Ui.Appearance;

namespace Trevunix.Services;

public static class ThemeService
{
    public static void Apply(AppTheme mode, Window? window = null)
    {
        switch (mode)
        {
            case AppTheme.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            case AppTheme.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                break;
        }

        if (window is null)
            return;

        if (mode == AppTheme.System)
            SystemThemeWatcher.Watch(window);
        // A window that has not loaded yet was never watched, and UnWatch throws on it.
        else if (window.IsLoaded)
            SystemThemeWatcher.UnWatch(window);
    }
}
