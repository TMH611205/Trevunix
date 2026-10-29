using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Trevunix.Core.Monitoring;

public sealed record SystemSample(
    double CpuPercent,
    long MemoryUsed,
    long MemoryTotal,
    long NetworkReceivePerSecond,
    long NetworkSendPerSecond
)
{
    public double MemoryPercent => MemoryTotal > 0 ? 100d * MemoryUsed / MemoryTotal : 0;
}

/// <summary>
///     Whole-machine CPU, memory and network usage. Rates are measured between two calls,
///     so the first sample reports zero CPU and network.
/// </summary>
public sealed class SystemMonitor
{
    private ulong _lastIdle;
    private ulong _lastTotal;
    private long _lastReceived;
    private long _lastSent;
    private long _lastTicks;

    public SystemSample Sample()
    {
        var cpu = SampleCpu();
        var (used, total) = SampleMemory();
        var (receive, send) = SampleNetwork();
        return new SystemSample(cpu, used, total, receive, send);
    }

    private double SampleCpu()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            return 0;

        var idle = ToUlong(idleTime);
        // Kernel time already includes idle time.
        var total = ToUlong(kernelTime) + ToUlong(userTime);
        var idleDelta = idle - _lastIdle;
        var totalDelta = total - _lastTotal;
        var first = _lastTotal == 0;
        _lastIdle = idle;
        _lastTotal = total;

        if (first || totalDelta == 0)
            return 0;
        return Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100);
    }

    private static (long Used, long Total) SampleMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return (0, 0);
        return ((long)(status.TotalPhys - status.AvailPhys), (long)status.TotalPhys);
    }

    private (long Receive, long Send) SampleNetwork()
    {
        long received = 0;
        long sent = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (
                nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType
                    is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel
            )
                continue;

            try
            {
                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
            catch (NetworkInformationException) { }
        }

        var now = Stopwatch.GetTimestamp();
        var seconds = _lastTicks == 0 ? 0 : (double)(now - _lastTicks) / Stopwatch.Frequency;
        var result =
            seconds > 0
                ? (
                    (long)(Math.Max(0, received - _lastReceived) / seconds),
                    (long)(Math.Max(0, sent - _lastSent) / seconds)
                )
                : (0L, 0L);

        _lastReceived = received;
        _lastSent = sent;
        _lastTicks = now;
        return result;
    }

    private static ulong ToUlong(FileTime time) => ((ulong)time.High << 32) | time.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idle,
        out FileTime kernel,
        out FileTime user
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

public sealed record ProcessSample(
    int Id,
    string Name,
    long MemoryBytes,
    double CpuPercent,
    string? Path
);

/// <summary>Per-process memory and CPU, with CPU measured between two calls.</summary>
public sealed class ProcessSampler
{
    private readonly Dictionary<int, TimeSpan> _lastCpu = [];
    private readonly Dictionary<int, string?> _paths = [];
    private long _lastTicks;

    public IReadOnlyList<ProcessSample> Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastTicks == 0 ? 0 : (double)(now - _lastTicks) / Stopwatch.Frequency;
        var cores = Environment.ProcessorCount;
        var seen = new Dictionary<int, TimeSpan>();
        var result = new List<ProcessSample>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == 0)
                        continue;

                    double cpu = 0;
                    try
                    {
                        var total = process.TotalProcessorTime;
                        seen[process.Id] = total;
                        if (elapsed > 0 && _lastCpu.TryGetValue(process.Id, out var before))
                            cpu = Math.Clamp(
                                100 * (total - before).TotalSeconds / (elapsed * cores),
                                0,
                                100
                            );
                    }
                    catch (Exception ex)
                        when (ex
                                is System.ComponentModel.Win32Exception
                                    or InvalidOperationException
                        ) { }

                    result.Add(
                        new ProcessSample(
                            process.Id,
                            process.ProcessName,
                            process.WorkingSet64,
                            cpu,
                            PathOf(process)
                        )
                    );
                }
                catch (InvalidOperationException)
                {
                    // Exited while we were reading it.
                }
            }
        }

        foreach (var gone in _paths.Keys.Where(id => !seen.ContainsKey(id)).ToList())
            _paths.Remove(gone);

        _lastCpu.Clear();
        foreach (var (id, cpu) in seen)
            _lastCpu[id] = cpu;
        _lastTicks = now;

        return result;
    }

    public static bool Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(5000);
        }
        catch (Exception ex)
            when (ex
                    is ArgumentException
                        or InvalidOperationException
                        or System.ComponentModel.Win32Exception
            )
        {
            return false;
        }
    }

    // Reading the main module is slow, so each process is asked once.
    private string? PathOf(Process process)
    {
        if (!_paths.TryGetValue(process.Id, out var path))
            _paths[process.Id] = path = TryPath(process);
        return path;
    }

    internal static string? TryPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex)
            when (ex
                    is System.ComponentModel.Win32Exception
                        or InvalidOperationException
                        or NotSupportedException
            )
        {
            return null;
        }
    }
}
