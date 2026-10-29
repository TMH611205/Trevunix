using Trevunix.Core.Cleanup;
using Trevunix.Core.Tweaks;

namespace Trevunix.Core.Maintenance;

public sealed record TweakBatchResult(
    int Applied,
    int Failed,
    bool NeedsExplorerRestart,
    bool NeedsReboot
);

/// <summary>
///     The safe, hands-off parts of Trevunix in one place, shared by the one-click optimize on
///     the home page and the scheduled cleanup: the cleanup targets that are on by default
///     (never the Recycle Bin, which may hold something the owner still wants) and the
///     recommended tweaks.
/// </summary>
public static class QuickMaintenance
{
    public static IReadOnlyList<CleanupTarget> CleanupTargets { get; } =
        CleanupCatalog.All.Where(t => t.SelectedByDefault && !t.IsRecycleBin).ToList();

    public static async Task<CleanupResult> CleanAsync(
        CleanupEngine engine,
        CancellationToken cancellationToken = default
    )
    {
        var scans = await engine.ScanAsync(CleanupTargets, cancellationToken);
        return await engine.CleanAsync(scans, cancellationToken: cancellationToken);
    }

    /// <summary>Recommended tweaks for this Windows build that are not fully applied yet.</summary>
    public static IReadOnlyList<Tweak> PendingTweaks(TweakEngine engine) =>
        TweakCatalog
            .All.Where(t => t.Risk == TweakRisk.Recommended && t.IsSupported)
            .Where(t => engine.GetStatus(t) != TweakStatus.Applied)
            .ToList();

    public static TweakBatchResult ApplyTweaks(TweakEngine engine, IEnumerable<Tweak> tweaks)
    {
        int applied = 0,
            failed = 0;
        bool explorer = false,
            reboot = false;
        foreach (var tweak in tweaks)
        {
            if (engine.Apply(tweak).Success)
                applied++;
            else
                failed++;
            explorer |= tweak.After == TweakAfter.RestartExplorer;
            reboot |= tweak.After == TweakAfter.Reboot;
        }
        return new TweakBatchResult(applied, failed, explorer, reboot);
    }
}
