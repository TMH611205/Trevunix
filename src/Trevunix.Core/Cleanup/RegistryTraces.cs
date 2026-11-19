using Trevunix.Core.Tweaks;

namespace Trevunix.Core.Cleanup;

/// <summary>Registry history lists, such as the Run box. The key stays; its values go.</summary>
internal static class RegistryTraces
{
    public static int Count(string keyPath)
    {
        var (root, sub) = RegistryValueAction.Split(keyPath);
        using var key = root.OpenSubKey(sub);
        return key?.GetValueNames().Count(n => n.Length > 0) ?? 0;
    }

    /// <summary>Removes every named value; returns how many went.</summary>
    public static int Clear(string keyPath)
    {
        var (root, sub) = RegistryValueAction.Split(keyPath);
        try
        {
            using var key = root.OpenSubKey(sub, writable: true);
            if (key is null)
                return 0;
            var removed = 0;
            foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                removed++;
            }
            return removed;
        }
        catch (Exception ex)
            when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 0;
        }
    }
}
