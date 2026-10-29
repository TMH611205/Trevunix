using System.Management;
using System.Runtime.InteropServices;

namespace Trevunix.Core.SystemInfo;

/// <summary>
///     WMI is optional for Trevunix: every feature that reads it has a fallback. Besides the
///     usual query errors, System.Management throws when the PC's .NET Framework copy of
///     wminet_utils.dll is too old (seen on outdated Windows 10), and that must not reach the UI.
/// </summary>
internal static class Wmi
{
    public static bool IsUnavailable(Exception ex) =>
        ex
            is ManagementException
                or UnauthorizedAccessException
                or COMException
                or PlatformNotSupportedException
                or TypeInitializationException
                or InvalidOperationException
                or DllNotFoundException
                or EntryPointNotFoundException;
}
