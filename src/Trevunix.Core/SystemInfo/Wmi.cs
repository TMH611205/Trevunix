using System.Management;
using System.Runtime.InteropServices;

namespace Trevunix.Core.SystemInfo;

/// <summary>
///     The one way into WMI. WMI is optional for Trevunix: every feature that reads it has a
///     fallback, so a failed query is an empty result. Besides the usual query errors,
///     System.Management throws when the PC's .NET Framework copy of wminet_utils.dll is too old
///     (seen on outdated Windows 10); that never fixes itself, so WMI is then skipped for good.
/// </summary>
internal static class Wmi
{
    private static volatile bool _broken;

    /// <summary>Every result of <paramref name="wql"/>, or none when WMI cannot answer.</summary>
    public static List<ManagementBaseObject> Query(string wql, string? scope = null)
    {
        if (_broken)
            return [];
        try
        {
            using var searcher = scope is null
                ? new ManagementObjectSearcher(wql)
                : new ManagementObjectSearcher(scope, wql);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return [];
        }
    }

    /// <summary>Runs a static method of a WMI class; null when WMI cannot.</summary>
    public static ManagementBaseObject? Invoke(
        string scope,
        string className,
        string method,
        IReadOnlyDictionary<string, object> parameters
    )
    {
        if (_broken)
            return null;
        try
        {
            using var target = new ManagementClass(scope, className, null);
            using var input = target.GetMethodParameters(method);
            foreach (var (name, value) in parameters)
                input[name] = value;
            return target.InvokeMethod(method, input, null);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return null;
        }
    }

    internal static bool IsUnavailable(Exception ex)
    {
        if (ex is PlatformNotSupportedException or TypeInitializationException)
            _broken = true;
        return ex
            is ManagementException
                or UnauthorizedAccessException
                or COMException
                or PlatformNotSupportedException
                or TypeInitializationException
                or DllNotFoundException
                or EntryPointNotFoundException;
    }
}
