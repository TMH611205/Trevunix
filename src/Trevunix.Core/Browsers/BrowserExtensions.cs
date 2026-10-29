using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Trevunix.Core.Apps;
using Trevunix.Core.Files;

namespace Trevunix.Core.Browsers;

public enum BrowserEngine
{
    Chromium,
    Firefox,
}

/// <summary>A browser Trevunix knows where to find, and the process name it runs as.</summary>
public sealed record Browser(
    string Name,
    BrowserEngine Engine,
    string DataFolder,
    string ProcessName
);

/// <summary>One extension installed in one browser profile.</summary>
/// <param name="Location">The extension's folder (Chromium) or .xpi file (Firefox).</param>
public sealed record BrowserExtension(
    Browser Browser,
    string ProfileName,
    string ProfileFolder,
    string Id,
    string Name,
    string Version,
    string Location
);

/// <summary>
///     Lists and removes extensions of Chromium browsers (Chrome, Edge, Brave, Vivaldi, Cốc Cốc,
///     Opera) and Firefox without the browser's help, the way an uninstaller cleans up after a
///     program that added one. The browser must be closed. Removal is undoable: the extension
///     goes to the Recycle Bin and the profile's settings files are copied first.
/// </summary>
public static class BrowserExtensions
{
    /// <summary>Chromium install locations of extensions that ship with the browser itself.</summary>
    private static readonly HashSet<int> BuiltInLocations = [5, 10];

    public static IReadOnlyList<Browser> KnownBrowsers()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var roaming = F(Environment.SpecialFolder.ApplicationData);
        return
        [
            new(
                "Google Chrome",
                BrowserEngine.Chromium,
                Path.Combine(local, @"Google\Chrome\User Data"),
                "chrome"
            ),
            new(
                "Microsoft Edge",
                BrowserEngine.Chromium,
                Path.Combine(local, @"Microsoft\Edge\User Data"),
                "msedge"
            ),
            new(
                "Brave",
                BrowserEngine.Chromium,
                Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"),
                "brave"
            ),
            new(
                "Vivaldi",
                BrowserEngine.Chromium,
                Path.Combine(local, @"Vivaldi\User Data"),
                "vivaldi"
            ),
            new(
                "Cốc Cốc",
                BrowserEngine.Chromium,
                Path.Combine(local, @"CocCoc\Browser\User Data"),
                "browser"
            ),
            new(
                "Opera",
                BrowserEngine.Chromium,
                Path.Combine(roaming, @"Opera Software\Opera Stable"),
                "opera"
            ),
            new(
                "Opera GX",
                BrowserEngine.Chromium,
                Path.Combine(roaming, @"Opera Software\Opera GX Stable"),
                "opera"
            ),
            new(
                "Firefox",
                BrowserEngine.Firefox,
                Path.Combine(roaming, @"Mozilla\Firefox\Profiles"),
                "firefox"
            ),
        ];
    }

    public static IReadOnlyList<BrowserExtension> List() =>
        KnownBrowsers()
            .Where(b => Directory.Exists(b.DataFolder))
            .SelectMany(b => b.Engine == BrowserEngine.Firefox ? Firefox(b) : Chromium(b))
            .OrderBy(e => e.Browser.Name)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static bool IsRunning(Browser browser)
    {
        var running = Process.GetProcessesByName(browser.ProcessName);
        foreach (var p in running)
            p.Dispose();
        return running.Length > 0;
    }

    /// <summary>Asks the browser's windows to close, then ends what is left after a few seconds.</summary>
    public static void Close(Browser browser)
    {
        var processes = Process.GetProcessesByName(browser.ProcessName);
        try
        {
            foreach (var p in processes)
            {
                try
                {
                    p.CloseMainWindow();
                }
                catch (InvalidOperationException) { }
            }
            var deadline = DateTime.UtcNow.AddSeconds(5);
            foreach (var p in processes)
            {
                try
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left > TimeSpan.Zero && p.WaitForExit(left))
                        continue;
                    if (!p.HasExited)
                        p.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                    when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                { }
            }
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
    }

    /// <summary>Removes one extension; the browser must be closed. False when something could not be changed.</summary>
    public static bool Remove(BrowserExtension extension)
    {
        if (IsRunning(extension.Browser))
            return false;
        return extension.Browser.Engine == BrowserEngine.Firefox
            ? RemoveFirefox(extension)
            : RemoveChromium(extension);
    }

    private static IEnumerable<BrowserExtension> Chromium(Browser browser)
    {
        var profileNames = ProfileNames(browser.DataFolder);
        // Opera keeps a single profile in the data folder itself.
        var profiles = File.Exists(Path.Combine(browser.DataFolder, "Preferences"))
            ? [browser.DataFolder]
            : SafeDirectories(browser.DataFolder)
                .Where(d => File.Exists(Path.Combine(d, "Preferences")))
                .Where(d => Path.GetFileName(d) is not ("System Profile" or "Guest Profile"));

        foreach (var profile in profiles)
        {
            var folderName = Path.GetFileName(profile);
            var profileName = profileNames.GetValueOrDefault(folderName, folderName);
            var settings = ExtensionSettings(profile);

            foreach (var extensionFolder in SafeDirectories(Path.Combine(profile, "Extensions")))
            {
                var id = Path.GetFileName(extensionFolder);
                // Built-in parts of the browser and the defaults it installs by itself (Docs
                // Offline and the like) come back on their own; only what the owner added is listed.
                if (
                    settings.TryGetValue(id, out var setting)
                    && (BuiltInLocations.Contains(setting.Location) || setting.ByDefault)
                )
                    continue;
                // Each update lands in a new version folder; the newest is the live one.
                var versionFolder = SafeDirectories(extensionFolder)
                    .Where(v => File.Exists(Path.Combine(v, "manifest.json")))
                    .OrderByDescending(v =>
                        File.GetLastWriteTimeUtc(Path.Combine(v, "manifest.json"))
                    )
                    .FirstOrDefault();
                if (versionFolder is null || ReadManifest(versionFolder) is not { } manifest)
                    continue;
                yield return new BrowserExtension(
                    browser,
                    profileName,
                    profile,
                    id,
                    manifest.Name,
                    manifest.Version,
                    extensionFolder
                );
            }
        }
    }

    private static IEnumerable<BrowserExtension> Firefox(Browser browser)
    {
        foreach (var profile in SafeDirectories(browser.DataFolder))
        {
            var file = Path.Combine(profile, "extensions.json");
            if (!File.Exists(file))
                continue;
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(file));
            }
            catch (Exception ex)
                when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var addon in root?["addons"]?.AsArray() ?? [])
            {
                // Only what the owner installed; built-in add-ons live in the app's folder.
                if (
                    Text(addon?["type"]) != "extension"
                    || Text(addon?["location"]) != "app-profile"
                )
                    continue;
                var path = Text(addon?["path"]);
                if (path.Length == 0 || !File.Exists(path))
                    continue;
                yield return new BrowserExtension(
                    browser,
                    Path.GetFileName(profile).Split('.', 2).Last(),
                    profile,
                    Text(addon?["id"]),
                    Text(addon?["defaultLocale"]?["name"]) is { Length: > 0 } name
                        ? name
                        : Text(addon?["id"]),
                    Text(addon?["version"]),
                    path
                );
            }
        }
    }

    /// <summary>
    ///     Moves the extension folder to the Recycle Bin and drops it from the profile's settings,
    ///     so the browser does not treat it as broken and restore it. Both settings files are
    ///     copied next to Trevunix's uninstall backups first.
    /// </summary>
    private static bool RemoveChromium(BrowserExtension extension)
    {
        var preferences = new[] { "Preferences", "Secure Preferences" }
            .Select(f => Path.Combine(extension.ProfileFolder, f))
            .Where(File.Exists)
            .ToList();
        if (!BackUp(extension, preferences))
            return false;

        foreach (var file in preferences)
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(file))?.AsObject();
                if (root is null)
                    continue;
                var changed = RemoveExtension(root, extension.Id);
                if (changed)
                    File.WriteAllText(file, root.ToJsonString());
            }
            catch (Exception ex)
                when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return !Directory.Exists(extension.Location) || RecycleBin.Send(extension.Location);
    }

    /// <summary>Takes an extension out of a Chromium settings file; true when anything changed.</summary>
    internal static bool RemoveExtension(JsonObject root, string id)
    {
        var changed = false;
        if (root["extensions"]?["settings"] is JsonObject settings)
            changed |= settings.Remove(id);
        // Every integrity hash kept per extension (settings, settings_encrypted_hash, ...).
        if (root["protection"]?["macs"]?["extensions"] is JsonObject macs)
        {
            foreach (var (_, group) in macs.ToList())
            {
                if (group is JsonObject hashes)
                    changed |= hashes.Remove(id);
            }
        }
        // Toolbar pins and order refer to the id.
        foreach (var list in new[] { "pinned_extensions", "toolbar" })
        {
            if (root["extensions"]?[list] is JsonArray array)
            {
                var hit = array.Where(n => n?.GetValue<string>() == id).ToList();
                foreach (var node in hit)
                    array.Remove(node);
                changed |= hit.Count > 0;
            }
        }
        return changed;
    }

    /// <summary>Firefox notices a missing .xpi at its next start and drops the add-on itself.</summary>
    private static bool RemoveFirefox(BrowserExtension extension) =>
        !File.Exists(extension.Location) || RecycleBin.Send(extension.Location);

    private static bool BackUp(BrowserExtension extension, IEnumerable<string> files)
    {
        try
        {
            var folder = Path.Combine(
                Leftovers.BackupDirectory,
                "Browser extensions",
                $"{DateTime.Now:yyyyMMdd-HHmmss}-{extension.Browser.Name}-{extension.Id}"
            );
            Directory.CreateDirectory(folder);
            foreach (var file in files)
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Install location per extension id, from both settings files of a profile.</summary>
    private static Dictionary<string, (int Location, bool ByDefault)> ExtensionSettings(
        string profile
    )
    {
        var result = new Dictionary<string, (int Location, bool ByDefault)>();
        foreach (var name in new[] { "Preferences", "Secure Preferences" })
        {
            try
            {
                var file = Path.Combine(profile, name);
                if (!File.Exists(file))
                    continue;
                var settings = JsonNode
                    .Parse(File.ReadAllText(file))
                    ?["extensions"]?["settings"]?.AsObject();
                foreach (var (id, value) in settings ?? [])
                {
                    if (
                        value?["location"] is JsonValue location
                        && location.TryGetValue<int>(out var l)
                    )
                        result[id] = (
                            l,
                            value["was_installed_by_default"] is JsonValue d
                                && d.TryGetValue<bool>(out var byDefault)
                                && byDefault
                        );
                }
            }
            catch (Exception ex)
                when (ex
                        is IOException
                            or JsonException
                            or UnauthorizedAccessException
                            or InvalidOperationException
                ) { }
        }
        return result;
    }

    /// <summary>Display names of a browser's profiles ("Default" may be called "Work"), from Local State.</summary>
    private static Dictionary<string, string> ProfileNames(string dataFolder)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = Path.Combine(dataFolder, "Local State");
            if (!File.Exists(file))
                return names;
            var cache = JsonNode
                .Parse(File.ReadAllText(file))
                ?["profile"]?["info_cache"]?.AsObject();
            foreach (var (folder, info) in cache ?? [])
            {
                if (Text(info?["name"]) is { Length: > 0 } name)
                    names[folder] = name;
            }
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or JsonException
                        or UnauthorizedAccessException
                        or InvalidOperationException
            ) { }
        return names;
    }

    private sealed record Manifest(string Name, string Version);

    /// <summary>Name and version from manifest.json, with "__MSG_name__" looked up in _locales.</summary>
    private static Manifest? ReadManifest(string folder)
    {
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            var name = Text(manifest?["name"]);
            if (
                name.StartsWith("__MSG_", StringComparison.Ordinal)
                && name.EndsWith("__", StringComparison.Ordinal)
            )
                name = Localize(folder, Text(manifest?["default_locale"]), name[6..^2]) ?? name;
            return name.Length == 0 ? null : new Manifest(name, Text(manifest?["version"]));
        }
        catch (Exception ex)
            when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Localize(string folder, string defaultLocale, string key)
    {
        var locales = new[]
        {
            System.Globalization.CultureInfo.CurrentUICulture.Name.Replace('-', '_'),
            defaultLocale,
            "en",
            "en_US",
        }
            .Where(l => l.Length > 0)
            .Distinct();
        foreach (var locale in locales)
        {
            var file = Path.Combine(folder, "_locales", locale, "messages.json");
            if (!File.Exists(file))
                continue;
            try
            {
                var messages = JsonNode.Parse(File.ReadAllText(file))?.AsObject();
                // Message keys are case-insensitive.
                var entry = messages
                    ?.FirstOrDefault(m =>
                        string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)
                    )
                    .Value;
                if (Text(entry?["message"]) is { Length: > 0 } text)
                    return text;
            }
            catch (Exception ex)
                when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s.Trim() : "";

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory
                    .EnumerateDirectories(
                        folder,
                        "*",
                        new EnumerationOptions { IgnoreInaccessible = true }
                    )
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
