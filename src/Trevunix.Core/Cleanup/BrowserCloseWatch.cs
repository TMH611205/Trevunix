namespace Trevunix.Core.Cleanup;

/// <summary>
///     Notices when a browser has been closed, so its cache can be cleaned right away (while it
///     runs, its cache files are locked). Feed it the running process names every so often.
/// </summary>
public sealed class BrowserCloseWatch
{
    /// <summary>Browser process name to its cleanup target id.</summary>
    public static IReadOnlyDictionary<string, string> Browsers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = "Chrome",
            ["msedge"] = "Edge",
            ["brave"] = "Brave",
            ["opera"] = "Opera",
            ["firefox"] = "Firefox",
        };

    private HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cleanup target ids of browsers that were running last time and are not now.</summary>
    public IReadOnlyList<string> Update(IEnumerable<string> runningProcessNames)
    {
        var now = runningProcessNames
            .Where(Browsers.ContainsKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var closed = _running.Where(b => !now.Contains(b)).Select(b => Browsers[b]).ToList();
        _running = now;
        return closed;
    }
}
