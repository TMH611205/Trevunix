using Trevunix.Core.Monitoring;
using Trevunix.Core.Network;
using Trevunix.Core.Storage;
using Trevunix.Core.Tools;

namespace Trevunix.Core.Tests;

public class LineAssemblerTests
{
    private static List<(string Text, bool Replace)> Feed(params string[] chunks)
    {
        var lines = new List<(string, bool)>();
        var assembler = new LineAssembler((t, r) => lines.Add((t, r)));
        foreach (var chunk in chunks)
            assembler.Push(chunk);
        assembler.Flush();
        return lines;
    }

    [Fact]
    public void Push_PlainLines_EmitsEachOnce()
    {
        Assert.Equal([("one", false), ("two", false)], Feed("one\r\ntwo\n"));
    }

    [Fact]
    public void Push_ChunksSplitMidLine_JoinsThem()
    {
        Assert.Equal([("hello world", false)], Feed("hel", "lo wo", "rld\r", "\n"));
    }

    [Fact]
    public void Push_ProgressRedraws_ReplaceThePreviousLine()
    {
        var lines = Feed("[=  10%]\r[==  20%]\r[===  30%]\r\nDone\r\n");

        Assert.Equal(
            [("[=  10%]", false), ("[==  20%]", true), ("[===  30%]", true), ("Done", false)],
            lines
        );
    }

    [Fact]
    public void Push_BlankLinesAndNuls_AreDropped()
    {
        Assert.Equal([("a", false), ("b", false)], Feed("a\r\n\r\n\0\0b\r\n"));
    }

    [Fact]
    public async Task RunAsync_RealCommand_StreamsOutputAndExitCode()
    {
        var lines = new List<string>();

        var code = await CommandRunner.RunAsync(
            "cmd.exe",
            "/c echo first& echo second& exit /b 3",
            OutputEncoding.Oem,
            (t, _) => lines.Add(t),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(3, code);
        Assert.Equal(["first", "second"], lines);
    }

    [Fact]
    public async Task RunAsync_Cancelled_KillsTheProcess()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cts.CancelAfter(300);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CommandRunner.RunAsync(
                "ping.exe",
                "-n 30 127.0.0.1",
                OutputEncoding.Oem,
                (_, _) => { },
                cts.Token
            )
        );
    }

    [Fact]
    public void RepairCatalog_IdsUnique()
    {
        var ids = RepairCatalog.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}

public class MonitorTests
{
    [Fact]
    public void SystemMonitor_SecondSample_HasSaneValues()
    {
        var monitor = new SystemMonitor();
        monitor.Sample();
        Thread.Sleep(200);
        var sample = monitor.Sample();

        Assert.InRange(sample.CpuPercent, 0, 100);
        Assert.True(sample.MemoryTotal > 0);
        Assert.InRange(sample.MemoryUsed, 1, sample.MemoryTotal);
        Assert.True(sample.NetworkReceivePerSecond >= 0);
    }

    [Fact]
    public void ProcessSampler_ListsThisProcess()
    {
        var sampler = new ProcessSampler();
        sampler.Sample();
        var samples = sampler.Sample();

        var me = Assert.Single(samples, p => p.Id == Environment.ProcessId);
        Assert.True(me.MemoryBytes > 0);
        Assert.InRange(me.CpuPercent, 0, 100);
    }

    [Fact]
    public void ProcessSampler_KillUnknownProcess_ReturnsFalse()
    {
        Assert.False(ProcessSampler.Kill(int.MaxValue));
    }
}

public class DnsTests
{
    [Fact]
    public void NetshArguments_Preset_SetsPrimaryThenAddsSecondary()
    {
        var cloudflare = DnsManager.Presets.Single(p => p.Id == "Cloudflare");

        var args = DnsManager.NetshArguments("Wi-Fi", cloudflare);

        Assert.Equal(
            [
                "interface ipv4 set dnsservers name=\"Wi-Fi\" static 1.1.1.1 primary validate=no",
                "interface ipv4 add dnsservers name=\"Wi-Fi\" 1.0.0.1 index=2 validate=no",
            ],
            args
        );
    }

    [Fact]
    public void NetshArguments_Automatic_UsesDhcpAndStripsQuotes()
    {
        var auto = DnsManager.Presets.Single(p => p.IsAutomatic);

        Assert.Equal(
            ["interface ipv4 set dnsservers name=\"Eth 2\" source=dhcp"],
            DnsManager.NetshArguments("Eth\" 2", auto)
        );
    }

    [Fact]
    public void Match_KnownPrimary_FindsPreset()
    {
        Assert.Equal("Google", DnsManager.Match(["8.8.8.8", "8.8.4.4"])?.Id);
        Assert.Null(DnsManager.Match(["192.168.1.1"]));
        Assert.Null(DnsManager.Match([]));
    }
}

public class DriveHealthTests
{
    [Fact]
    public void Parse_SingleDisk_ReadsCounters()
    {
        var disks = DriveHealth.Parse(
            """{"Name":"Acer SSD","Media":"SSD","Bus":"SATA","Size":1024209543168,"Health":"Healthy","Temp":33,"Wear":2,"Hours":259,"ReadErr":0,"WriteErr":null}"""
        );

        var disk = Assert.Single(disks);
        Assert.Equal(HealthState.Healthy, disk.Health);
        Assert.Equal(33, disk.TemperatureC);
        Assert.Equal(2, disk.WearPercent);
        Assert.Equal(259, disk.PowerOnHours);
        Assert.Null(disk.WriteErrors);
    }

    [Fact]
    public void Parse_ArrayWithMissingCounters_StillLists()
    {
        var disks = DriveHealth.Parse(
            """[{"Name":"A","Media":"HDD","Bus":"SATA","Size":1,"Health":"Warning","Temp":0},{"Name":"B","Media":"Unspecified","Bus":"USB","Size":2,"Health":"Unhealthy"}]"""
        );

        Assert.Equal([HealthState.Warning, HealthState.Unhealthy], disks.Select(d => d.Health));
        Assert.Null(disks[0].TemperatureC);
    }
}
