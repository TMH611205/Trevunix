namespace Trevunix.Core.Network;

/// <summary>An app Trevunix has cut off from the network.</summary>
public sealed record BlockedApp(string Path);

/// <summary>
///     Blocks programs from the network with Windows Firewall rules (inbound and outbound) in a
///     "Trevunix" group, through the firewall's COM API. Unblocking removes exactly those rules.
/// </summary>
public static class FirewallBlock
{
    public const string Group = "Trevunix";
    private const int Block = 0;
    private const int Inbound = 1;
    private const int Outbound = 2;
    private const int AllProfiles = 0x7FFFFFFF;

    /// <summary>Rule names carry the direction so each program has one rule per direction.</summary>
    internal static string RuleName(string path, int direction) =>
        $"Trevunix block {(direction == Inbound ? "in" : "out")}: {path}";

    public static IReadOnlyList<BlockedApp> List()
    {
        var apps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (dynamic rule in Policy().Rules)
        {
            if ((string?)rule.Grouping == Group && rule.ApplicationName is string app)
                apps.Add(app);
        }
        return [.. apps.Select(a => new BlockedApp(a))];
    }

    public static void BlockApp(string exePath)
    {
        var policy = Policy();
        Unblock(policy, exePath);
        foreach (var direction in new[] { Inbound, Outbound })
        {
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
            rule.Name = RuleName(exePath, direction);
            rule.Grouping = Group;
            rule.ApplicationName = exePath;
            rule.Direction = direction;
            rule.Action = Block;
            rule.Profiles = AllProfiles;
            rule.Enabled = true;
            policy.Rules.Add(rule);
        }
    }

    public static void UnblockApp(string exePath) => Unblock(Policy(), exePath);

    private static void Unblock(dynamic policy, string exePath)
    {
        var names = new List<string>();
        foreach (dynamic rule in policy.Rules)
        {
            if (
                (string?)rule.Grouping == Group
                && string.Equals(
                    (string?)rule.ApplicationName,
                    exePath,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                names.Add((string)rule.Name);
        }
        foreach (var name in names)
            policy.Rules.Remove(name);
    }

    private static dynamic Policy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
}
