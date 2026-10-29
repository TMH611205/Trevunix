using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Trevunix.Core.Apps;

public sealed record StoreApp(
    string Name,
    string FullName,
    string Publisher,
    string Version,
    string DisplayName,
    bool Suggested,
    string? LogoPath = null
);

public sealed record RemoveResult(string FullName, bool Success, string? Error);

/// <summary>
///     Lists and removes Microsoft Store (AppX) apps through Windows PowerShell.
///     Frameworks, system apps and apps Windows marks as non-removable are never listed.
/// </summary>
public static partial class StoreApps
{
    /// <summary>Preinstalled consumer apps that are safe to remove and can be reinstalled from the Store.</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.BingNews"] = "Microsoft News",
        ["Microsoft.BingWeather"] = "Weather",
        ["Microsoft.BingSearch"] = "Bing Search",
        ["Microsoft.GetHelp"] = "Get Help",
        ["Microsoft.Getstarted"] = "Tips",
        ["Microsoft.MicrosoftSolitaireCollection"] = "Solitaire Collection",
        ["Microsoft.People"] = "People",
        ["Microsoft.WindowsFeedbackHub"] = "Feedback Hub",
        ["Microsoft.WindowsMaps"] = "Maps",
        ["Microsoft.ZuneVideo"] = "Movies & TV",
        ["Microsoft.MicrosoftOfficeHub"] = "Microsoft 365 (Office)",
        ["Microsoft.SkypeApp"] = "Skype",
        ["Microsoft.Todos"] = "Microsoft To Do",
        ["Microsoft.PowerAutomateDesktop"] = "Power Automate",
        ["Microsoft.MixedReality.Portal"] = "Mixed Reality Portal",
        ["Microsoft.Microsoft3DViewer"] = "3D Viewer",
        ["Microsoft.549981C3F5F10"] = "Cortana",
        ["Microsoft.WindowsAlarms"] = "Clock",
        ["Microsoft.YourPhone"] = "Phone Link",
        ["Microsoft.Copilot"] = "Copilot",
        ["Microsoft.Windows.DevHome"] = "Dev Home",
        ["MicrosoftTeams"] = "Microsoft Teams (personal)",
        ["MSTeams"] = "Microsoft Teams",
        ["Clipchamp.Clipchamp"] = "Clipchamp",
        ["MicrosoftCorporationII.MicrosoftFamily"] = "Microsoft Family",
        ["Microsoft.OutlookForWindows"] = "Outlook (new)",
        ["Microsoft.GamingApp"] = "Xbox",
        ["Microsoft.XboxApp"] = "Xbox Console Companion",
        ["Microsoft.ZuneMusic"] = "Media Player",
        ["Microsoft.WindowsSoundRecorder"] = "Sound Recorder",
        ["Microsoft.WindowsCamera"] = "Camera",
        ["Microsoft.Windows.Photos"] = "Photos",
        ["Microsoft.WindowsCalculator"] = "Calculator",
        ["Microsoft.WindowsNotepad"] = "Notepad",
        ["Microsoft.Paint"] = "Paint",
        ["Microsoft.ScreenSketch"] = "Snipping Tool",
        ["Microsoft.WindowsStore"] = "Microsoft Store",
        ["Microsoft.WindowsTerminal"] = "Terminal",
        ["Microsoft.XboxGamingOverlay"] = "Xbox Game Bar",
    };

    /// <summary>Apps recommended for removal. Everyday tools such as Photos or Calculator are not.</summary>
    private static readonly HashSet<string> SuggestedIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.BingNews",
        "Microsoft.BingWeather",
        "Microsoft.BingSearch",
        "Microsoft.GetHelp",
        "Microsoft.Getstarted",
        "Microsoft.MicrosoftSolitaireCollection",
        "Microsoft.People",
        "Microsoft.WindowsFeedbackHub",
        "Microsoft.WindowsMaps",
        "Microsoft.ZuneVideo",
        "Microsoft.MicrosoftOfficeHub",
        "Microsoft.SkypeApp",
        "Microsoft.PowerAutomateDesktop",
        "Microsoft.MixedReality.Portal",
        "Microsoft.Microsoft3DViewer",
        "Microsoft.549981C3F5F10",
        "MicrosoftTeams",
        "Clipchamp.Clipchamp",
        "MicrosoftCorporationII.MicrosoftFamily",
    };

    /// <summary>Third-party promoted apps Windows installs on its own.</summary>
    private static readonly string[] SuggestedPrefixes =
    [
        "king.com.",
        "SpotifyAB.",
        "Disney.",
        "Facebook.",
        "BytedancePte.",
        "AmazonVideo.",
        "5319275A.WhatsAppDesktop",
    ];

    /// <summary>Apps Windows needs; never listed even when Windows allows removing them.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.WindowsStore",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.StorePurchaseApp",
        "Microsoft.SecHealthUI",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.WindowsTerminal",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl",
    };

    private const string ListScript =
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8;"
        + "Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable "
        + "-and $_.SignatureKind -ne 'System' -and -not $_.IsResourcePackage } | "
        + "Select-Object Name,PackageFullName,Publisher,InstallLocation,@{n='Version';e={$_.Version.ToString()}} | "
        + "ConvertTo-Json -Compress";

    public static async Task<IReadOnlyList<StoreApp>> ListAsync(CancellationToken ct = default)
    {
        var (code, output, error) = await RunPowerShellAsync(ListScript, ct);
        if (code != 0)
            throw new InvalidOperationException(error.Trim());
        return Parse(output);
    }

    public static async Task<RemoveResult> RemoveAsync(
        StoreApp app,
        bool alsoForNewUsers,
        CancellationToken ct = default
    )
    {
        var name = Quote(app.Name);
        var script = new StringBuilder()
            .Append("$ErrorActionPreference='Stop';")
            .Append($"Remove-AppxPackage -Package {Quote(app.FullName)};");
        if (alsoForNewUsers)
        {
            script.Append(
                $"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq {name} | "
                    + "Remove-AppxProvisionedPackage -Online | Out-Null;"
            );
        }

        var (code, _, error) = await RunPowerShellAsync(script.ToString(), ct);
        return new RemoveResult(app.FullName, code == 0, code == 0 ? null : error.Trim());
    }

    internal static IReadOnlyList<StoreApp> Parse(string json)
    {
        json = json.Trim();
        if (json.Length == 0)
            return [];

        using var doc = JsonDocument.Parse(json);
        // ConvertTo-Json returns a bare object when there is only one package.
        var items =
            doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];

        var apps = new List<StoreApp>();
        foreach (var item in items)
        {
            var name = Str(item, "Name");
            var full = Str(item, "PackageFullName");
            if (name.Length == 0 || full.Length == 0 || IsProtected(name))
                continue;

            apps.Add(
                new StoreApp(
                    name,
                    full,
                    PublisherName(Str(item, "Publisher")),
                    Str(item, "Version"),
                    DisplayNameFor(name),
                    IsSuggested(name),
                    FindLogo(Str(item, "InstallLocation"))
                )
            );
        }

        return apps.DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(a => a.Suggested)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    ///     The app's logo: the manifest names e.g. "Assets\StoreLogo.png", but the file on
    ///     disk usually carries a qualifier such as "StoreLogo.scale-100.png".
    /// </summary>
    internal static string? FindLogo(string installLocation)
    {
        try
        {
            var manifest = Path.Combine(installLocation, "AppxManifest.xml");
            if (installLocation.Length == 0 || !File.Exists(manifest))
                return null;

            var doc = XDocument.Load(manifest);
            var logo = doc.Descendants()
                .FirstOrDefault(e =>
                    e.Name.LocalName == "Logo" && e.Parent?.Name.LocalName == "Properties"
                )
                ?.Value.Trim();
            return logo is { Length: > 0 }
                ? ResolveQualified(Path.Combine(installLocation, logo))
                : null;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    internal static string? ResolveQualified(string path)
    {
        if (File.Exists(path))
            return path;

        var folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder))
            return null;

        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var candidates = Directory.EnumerateFiles(folder, $"{name}.*{ext}").ToList();
        string[] preferred = ["scale-200", "scale-100", "targetsize-48", "scale-150", "scale-400"];
        return preferred
                .Select(q =>
                    candidates.FirstOrDefault(c =>
                        c.Contains(q, StringComparison.OrdinalIgnoreCase)
                    )
                )
                .FirstOrDefault(c => c is not null)
            ?? candidates.FirstOrDefault();
    }

    /// <summary>Runtime and library packages other apps depend on; never offered for removal.</summary>
    private static readonly string[] ProtectedPrefixes =
    [
        "Microsoft.WinAppRuntime",
        "MicrosoftCorporationII.WinAppRuntime",
        "Microsoft.WindowsAppRuntime",
        "Microsoft.VCLibs",
        "Microsoft.NET.",
        "Microsoft.UI.Xaml",
        "Microsoft.Services.Store",
        "Microsoft.DirectXRuntime",
        "Microsoft.Advertising.Xaml",
        "Microsoft.Windows.",
        "MicrosoftWindows.",
        "windows.",
        "Windows.",
    ];

    internal static bool IsProtected(string name) =>
        Protected.Contains(name)
        || ProtectedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            && !Known.ContainsKey(name);

    internal static bool IsSuggested(string name) =>
        SuggestedIds.Contains(name)
        || SuggestedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>A readable name: a known one, or the package name without its vendor prefix.</summary>
    internal static string DisplayNameFor(string name)
    {
        if (Known.TryGetValue(name, out var known))
            return known;

        var last = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        return SplitWords().Replace(last, " $1").Trim();
    }

    /// <summary>"CN=Microsoft Corporation, O=..." becomes "Microsoft Corporation".</summary>
    internal static string PublisherName(string distinguished)
    {
        var cn = distinguished
            .Split(',')
            .Select(p => p.Trim())
            .FirstOrDefault(p => p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));
        return cn is null ? distinguished : cn[3..].Trim('"');
    }

    private static string Str(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static async Task<(int Code, string Output, string Error)> RunPowerShellAsync(
        string script,
        CancellationToken ct
    )
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(script);

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout, await stderr);
    }

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex SplitWords();
}
