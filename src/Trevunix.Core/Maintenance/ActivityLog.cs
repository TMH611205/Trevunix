using System.Text.Json;
using Trevunix.Core.Settings;

namespace Trevunix.Core.Maintenance;

public enum HistoryKind
{
    Cleanup,
    QuickOptimize,
    AutoClean,
    TweakApplied,
    TweakUndone,
    DuplicatesRemoved,
}

/// <param name="Detail">A tweak id, or empty.</param>
public sealed record ActivityEntry(
    DateTime At,
    HistoryKind Kind,
    long FreedBytes = 0,
    string Detail = ""
);

/// <summary>
///     What Trevunix did and when, one JSON object per line in <c>history.jsonl</c>. Only the
///     newest <see cref="Keep"/> entries are kept.
/// </summary>
public sealed class ActivityLog(string? path = null)
{
    public const int Keep = 500;

    public string FilePath { get; } = path ?? Path.Combine(AppPaths.DataDirectory, "history.jsonl");

    /// <summary>Never throws: losing a history line must not fail the action it records.</summary>
    public void Add(HistoryKind kind, long freedBytes = 0, string detail = "")
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var entry = new ActivityEntry(DateTime.Now, kind, freedBytes, detail);
            File.AppendAllText(FilePath, JsonSerializer.Serialize(entry) + Environment.NewLine);

            // Trim rarely: only once the file holds twice what is kept.
            var lines = File.ReadAllLines(FilePath);
            if (lines.Length > Keep * 2)
                File.WriteAllLines(FilePath, lines[^Keep..]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Newest first; lines that do not parse are skipped.</summary>
    public IReadOnlyList<ActivityEntry> Read()
    {
        string[] lines;
        try
        {
            lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var entries = new List<ActivityEntry>();
        foreach (var line in lines.Reverse().Take(Keep))
        {
            try
            {
                if (JsonSerializer.Deserialize<ActivityEntry>(line) is { } entry)
                    entries.Add(entry);
            }
            catch (JsonException) { }
        }
        return entries;
    }
}
