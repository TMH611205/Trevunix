using Trevunix.Core.Recovery;
using Trevunix.Core.Settings;
using Trevunix.Localization;

namespace Trevunix.Services;

/// <summary>
///     Makes one System Restore point per session, before the first tweak, so the owner can
///     roll Windows back if a change does not suit them. When Windows cannot make one, the
///     owner decides whether to go on without it.
/// </summary>
public sealed class RestorePointGuard(AppSettings settings)
{
    private Task<bool>? _ready;

    /// <summary>True when changes may go ahead.</summary>
    public async Task<bool> EnsureAsync()
    {
        if (!settings.CreateRestorePoint)
            return true;

        _ready ??= Task.Run(() => RestorePoint.Create(Tr.Get("RestorePoint.Description")));
        if (await _ready)
            return true;

        var proceed = await Dialogs.ConfirmAsync(
            Tr.Get("RestorePoint.Failed.Title"),
            Tr.Get("RestorePoint.Failed.Message"),
            Tr.Get("RestorePoint.Failed.Continue")
        );
        // Asked once a session: going ahead now means going ahead for the rest of it.
        _ready = proceed ? Task.FromResult(true) : null;
        return proceed;
    }
}
