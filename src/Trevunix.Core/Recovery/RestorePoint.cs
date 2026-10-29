using System.Management;
using Trevunix.Core.SystemInfo;

namespace Trevunix.Core.Recovery;

/// <summary>
///     Windows System Restore points, through the SystemRestore WMI class. Windows makes at most
///     one point per 24 hours by default and quietly skips the request inside that window; the
///     recent point is then the one to go back to, so that counts as success too.
/// </summary>
public static class RestorePoint
{
    private const int ModifySettings = 12;
    private const int BeginSystemChange = 100;

    /// <summary>
    ///     False when Windows refused, most often because System Protection is off for the
    ///     system drive. Takes several seconds; run it off the UI thread.
    /// </summary>
    public static bool Create(string description)
    {
        try
        {
            using var restore = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            using var input = restore.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = ModifySettings;
            input["EventType"] = BeginSystemChange;

            using var output = restore.InvokeMethod("CreateRestorePoint", input, null);
            return output?["ReturnValue"] is uint and 0;
        }
        catch (Exception ex) when (Wmi.IsUnavailable(ex))
        {
            return false;
        }
    }
}
