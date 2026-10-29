using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Trevunix.Core.Tools;

namespace Trevunix.Core.Network;

public sealed record DnsPreset(string Id, string Name, IReadOnlyList<string> Servers)
{
    public bool IsAutomatic => Servers.Count == 0;
}

public sealed record NetworkAdapter(
    string Name,
    string Description,
    IReadOnlyList<string> DnsServers
);

public static class DnsManager
{
    public static IReadOnlyList<DnsPreset> Presets { get; } =
    [
        new("Automatic", "Automatic (DHCP)", []),
        new("Cloudflare", "Cloudflare", ["1.1.1.1", "1.0.0.1"]),
        new("Google", "Google", ["8.8.8.8", "8.8.4.4"]),
        new("Quad9", "Quad9", ["9.9.9.9", "149.112.112.112"]),
        new("AdGuard", "AdGuard (blocks ads)", ["94.140.14.14", "94.140.15.15"]),
        new("OpenDns", "OpenDNS", ["208.67.222.222", "208.67.220.220"]),
    ];

    /// <summary>Connected Ethernet and Wi-Fi adapters with their IPv4 DNS servers.</summary>
    public static IReadOnlyList<NetworkAdapter> ActiveAdapters() =>
        NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(n =>
                n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType
                    is NetworkInterfaceType.Ethernet
                        or NetworkInterfaceType.Wireless80211
                        or NetworkInterfaceType.GigabitEthernet
                && n.GetIPProperties().GatewayAddresses.Count > 0
            )
            .Select(n => new NetworkAdapter(
                n.Name,
                n.Description,
                n.GetIPProperties()
                    .DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToList()
            ))
            .ToList();

    /// <summary>Which preset an adapter's servers match, or null for a custom setup.</summary>
    public static DnsPreset? Match(IReadOnlyList<string> servers) =>
        Presets.FirstOrDefault(p =>
            !p.IsAutomatic && p.Servers.Count > 0 && servers.Count > 0 && servers[0] == p.Servers[0]
        );

    /// <summary>The netsh commands that set an adapter's IPv4 DNS to a preset.</summary>
    public static IReadOnlyList<string> NetshArguments(string adapter, DnsPreset preset)
    {
        var name = adapter.Replace("\"", "", StringComparison.Ordinal);
        if (preset.IsAutomatic)
            return [$"interface ipv4 set dnsservers name=\"{name}\" source=dhcp"];

        var commands = new List<string>
        {
            $"interface ipv4 set dnsservers name=\"{name}\" static {preset.Servers[0]} primary validate=no",
        };
        for (var i = 1; i < preset.Servers.Count; i++)
        {
            commands.Add(
                $"interface ipv4 add dnsservers name=\"{name}\" {preset.Servers[i]} index={i + 1} validate=no"
            );
        }
        return commands;
    }

    public static async Task<bool> ApplyAsync(
        string adapter,
        DnsPreset preset,
        Action<string, bool> onLine,
        CancellationToken ct = default
    )
    {
        foreach (var args in NetshArguments(adapter, preset))
        {
            if (
                await CommandRunner.RunAsync("netsh.exe", args, OutputEncoding.Oem, onLine, ct) != 0
            )
                return false;
        }

        await CommandRunner.RunAsync("ipconfig.exe", "/flushdns", OutputEncoding.Oem, onLine, ct);
        return true;
    }

    /// <summary>Round-trip time to a DNS server, or null when it does not answer.</summary>
    public static async Task<long?> PingAsync(string server, int timeoutMs = 1500)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(IPAddress.Parse(server), timeoutMs);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception ex)
            when (ex is PingException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
