using System.Text.Json;
using Trevunix.Core.Settings;

namespace Trevunix.Core.Tweaks;

public enum TweakCategory
{
    AdsAndSuggestions,
    DataCollection,
    Ai,
    Performance,
    Gaming,
    Updates,
    FileExplorer,
    Taskbar,
    Appearance,
}

public enum TweakRisk
{
    /// <summary>Safe for everyone; part of "apply recommended".</summary>
    Recommended,

    /// <summary>Useful but changes behaviour some people rely on.</summary>
    Optional,
}

public enum TweakAfter
{
    Nothing,
    RestartExplorer,
    Reboot,
}

public enum TweakStatus
{
    NotApplied,
    Applied,
    Partial,
}

public sealed record Tweak(
    string Id,
    TweakCategory Category,
    TweakRisk Risk,
    IReadOnlyList<ITweakAction> Actions,
    TweakAfter After = TweakAfter.Nothing,
    int MinBuild = 0
)
{
    public bool IsSupported => Environment.OSVersion.Version.Build >= MinBuild;
}

public sealed record TweakOutcome(bool Success, IReadOnlyList<string> Errors);

/// <summary>
///     Applies and undoes tweaks. Before the first apply, the original state of every action
///     is written to an undo journal; undo restores exactly that state and removes the journal.
/// </summary>
public sealed class TweakEngine(string? journalDirectory = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string JournalDirectory { get; } =
        journalDirectory ?? Path.Combine(AppPaths.DataDirectory, "Undo");

    public TweakStatus GetStatus(Tweak tweak)
    {
        var relevant = tweak.Actions.Where(SafeIsAvailable).ToList();
        if (relevant.Count == 0)
            return TweakStatus.NotApplied;

        var desired = relevant.Count(SafeIsDesired);
        return desired == relevant.Count ? TweakStatus.Applied
            : desired == 0 ? TweakStatus.NotApplied
            : TweakStatus.Partial;
    }

    public bool HasUndo(Tweak tweak) => File.Exists(JournalPath(tweak));

    public TweakOutcome Apply(Tweak tweak)
    {
        // Keep the very first snapshot: re-applying must not overwrite the true original.
        if (!HasUndo(tweak))
        {
            var snapshots = tweak.Actions.Select(SafeCapture).ToList();
            Directory.CreateDirectory(JournalDirectory);
            var temp = JournalPath(tweak) + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshots, Json));
            File.Move(temp, JournalPath(tweak), overwrite: true);
        }

        var errors = new List<string>();
        foreach (var action in tweak.Actions)
        {
            try
            {
                if (action.IsAvailable)
                    action.Apply();
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                errors.Add(ex.Message);
            }
        }

        return new TweakOutcome(errors.Count == 0, errors);
    }

    public TweakOutcome Undo(Tweak tweak)
    {
        var path = JournalPath(tweak);
        if (!File.Exists(path))
            return new TweakOutcome(false, ["No undo data."]);

        List<Snapshot>? snapshots;
        try
        {
            snapshots = JsonSerializer.Deserialize<List<Snapshot>>(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            return new TweakOutcome(false, [ex.Message]);
        }

        if (snapshots is null || snapshots.Count != tweak.Actions.Count)
            return new TweakOutcome(false, ["Undo data does not match this tweak."]);

        var errors = new List<string>();
        for (var i = tweak.Actions.Count - 1; i >= 0; i--)
        {
            try
            {
                tweak.Actions[i].Restore(snapshots[i]);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                errors.Add(ex.Message);
            }
        }

        if (errors.Count == 0)
            File.Delete(path);

        return new TweakOutcome(errors.Count == 0, errors);
    }

    private string JournalPath(Tweak tweak) => Path.Combine(JournalDirectory, tweak.Id + ".json");

    private static Snapshot SafeCapture(ITweakAction action)
    {
        try
        {
            return action.Capture();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new Snapshot();
        }
    }

    private static bool SafeIsAvailable(ITweakAction action)
    {
        try
        {
            return action.IsAvailable;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return false;
        }
    }

    private static bool SafeIsDesired(ITweakAction action)
    {
        try
        {
            return action.IsDesired();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return false;
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex
            is UnauthorizedAccessException
                or System.Security.SecurityException
                or IOException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception;
}
