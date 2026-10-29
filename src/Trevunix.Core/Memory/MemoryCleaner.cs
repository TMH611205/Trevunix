using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Trevunix.Core.Memory;

public sealed record MemoryStatus(int LoadPercent, long Total, long Available)
{
    public long Used => Total - Available;
}

public sealed record MemoryCleanResult(
    long AvailableBefore,
    long AvailableAfter,
    int ProcessesTrimmed,
    bool StandbyPurged
)
{
    public long Freed => Math.Max(0, AvailableAfter - AvailableBefore);
}

/// <summary>
///     Frees physical memory the way Windows itself does under pressure: asks every process to
///     hand back pages it is not using (they come back from the page file on demand), and
///     optionally drops the standby cache. Nothing is closed and no data is lost. Needs admin.
/// </summary>
public static class MemoryCleaner
{
    /// <summary>Thresholds the owner can pick for automatic cleaning, in percent of RAM in use.</summary>
    public static IReadOnlyList<int> AutoThresholds { get; } = [60, 65, 70, 75, 80, 85, 90];

    public static MemoryStatus Read()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return new MemoryStatus(0, 0, 0);
        return new MemoryStatus(
            (int)status.MemoryLoad,
            (long)status.TotalPhys,
            (long)status.AvailPhys
        );
    }

    public static MemoryCleanResult Clean(bool purgeStandby)
    {
        var before = Read().Available;
        var trimmed = TrimWorkingSets();
        var purged = purgeStandby && PurgeStandbyList();
        var after = Read().Available;
        return new MemoryCleanResult(before, after, trimmed, purged);
    }

    /// <summary>
    ///     Whether an automatic clean is due: memory use reached the threshold and the last clean
    ///     was long enough ago that cleaning again would not just thrash the page file.
    /// </summary>
    public static bool ShouldAutoClean(
        int loadPercent,
        int threshold,
        DateTime now,
        DateTime? lastClean,
        TimeSpan cooldown
    ) =>
        threshold > 0
        && loadPercent >= threshold
        && (lastClean is not { } last || now - last >= cooldown);

    private static int TrimWorkingSets()
    {
        var count = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id is 0 or 4)
                    continue;

                var handle = OpenProcess(
                    ProcessSetQuota | ProcessQueryLimitedInformation,
                    false,
                    process.Id
                );
                if (handle == IntPtr.Zero)
                    continue;
                try
                {
                    if (EmptyWorkingSet(handle))
                        count++;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }
        return count;
    }

    private static bool PurgeStandbyList()
    {
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
            return false;

        var command = MemoryPurgeStandbyList;
        return NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int)) == 0;
    }

    private static bool EnablePrivilege(string name)
    {
        if (
            !OpenProcessToken(
                GetCurrentProcess(),
                TokenAdjustPrivileges | TokenQuery,
                out var token
            )
        )
            return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid))
                return false;

            var privileges = new TokenPrivileges
            {
                Count = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };
            return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token,
        bool disableAll,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength
    );

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
}
