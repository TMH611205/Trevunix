using Trevunix.Core.Monitoring;
using Trevunix.Core.Network;
using Trevunix.Core.Performance;

namespace Trevunix.Core.Tests;

public class PerformanceTests
{
    private static ProcessSample P(int id, string name, double cpu) => new(id, name, 0, cpu, null);

    [Fact]
    public void Cpu_hogs_skip_windows_processes_and_this_app()
    {
        var samples = new[]
        {
            P(1, "chrome", 30),
            P(2, "svchost", 50),
            P(3, "dwm", 20),
            P(4, "game", 12),
            P(5, "idle-tool", 1),
            P(99, "Trevunix", 40),
        };

        var hogs = ProcessControl.PickCpuHogs(samples, minCpuPercent: 5, selfId: 99);

        Assert.Equal(["chrome", "game"], hogs.Select(h => h.Name));
    }

    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000D1A5_phys_0_eng_0_engtype_3D", 1234, "3D")]
    [InlineData("pid_8_luid_0x0_0x1_phys_0_eng_12_engtype_VideoDecode", 8, "VideoDecode")]
    public void Gpu_engine_instance_names_are_parsed(string instance, int pid, string type)
    {
        var parsed = GpuMonitor.ParseEngine(instance);

        Assert.Equal((pid, type), parsed);
    }

    [Fact]
    public void Gpu_usage_per_process_is_its_busiest_engine_type()
    {
        var byType = new Dictionary<string, Dictionary<int, double>>
        {
            ["3D"] = new() { [1] = 40, [2] = 5 },
            ["Copy"] = new() { [1] = 10, [2] = 30 },
        };

        var combined = GpuMonitor.Combine(byType);

        Assert.Equal(40, combined[1]);
        Assert.Equal(30, combined[2]);
    }

    [Fact]
    public void Median_and_jitter()
    {
        Assert.Equal(15, SpeedMath.Median([20, 10, 15]));
        Assert.Equal(12.5, SpeedMath.Median([10, 20, 15, 10]));
        Assert.Equal(5, SpeedMath.Jitter([10, 15, 10]));
    }

    [Fact]
    public void Steady_speed_leaves_out_the_warmup()
    {
        // Slow first two seconds, then 12.5 MB/s = 100 Mbps.
        var samples = new List<(double, long)>
        {
            (1.0, 1_000_000),
            (2.0, 3_000_000),
            (3.0, 15_500_000),
            (4.0, 28_000_000),
        };

        Assert.Equal(100, SpeedMath.SteadyMbps(samples, warmupSeconds: 2), precision: 6);
    }

    [Fact]
    public void Server_timing_gives_server_time_and_tcp_round_trip()
    {
        var header =
            "cfSpeedEdge;dur=7, cfSpeedWorker;dur=25,"
            + "cfL4;desc=\"?proto=TCP&rtt=30221&min_rtt=29478&rtt_var=9712\"";

        var (server, rtt) = SpeedMath.ParseServerTiming(header);

        Assert.Equal(32, server);
        Assert.Equal(30.221, rtt!.Value, precision: 3);
    }

    private static List<(double, long)> Ramp(params long[] bytesPerSecond)
    {
        // One sample every 200 ms, spreading each second's bytes evenly.
        var samples = new List<(double, long)>();
        long total = 0;
        for (var s = 0; s < bytesPerSecond.Length; s++)
        for (var i = 1; i <= 5; i++)
        {
            total += bytesPerSecond[s] / 5;
            samples.Add((s + i * 0.2, total));
        }
        return samples;
    }

    [Fact]
    public void Steady_when_the_last_three_seconds_agree() =>
        Assert.True(SpeedMath.IsSteady(Ramp(10_000, 50_000, 100_000, 101_000, 99_000, 100_000)));

    [Fact]
    public void Not_steady_while_still_speeding_up() =>
        Assert.False(SpeedMath.IsSteady(Ramp(10_000, 50_000, 80_000, 90_000, 100_000, 120_000)));

    [Fact]
    public void Not_steady_before_three_seconds_of_data() =>
        Assert.False(SpeedMath.IsSteady(Ramp(100_000, 100_000)));

    [Fact]
    public void Server_timing_without_round_trip()
    {
        Assert.Equal((12.5, null), SpeedMath.ParseServerTiming("cfRequestDuration;dur=12.5"));
        Assert.Equal((0, null), SpeedMath.ParseServerTiming(null));
    }
}
