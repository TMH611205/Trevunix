using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Trevunix.Core.Updates;

/// <summary>A newer release published on GitHub, with the asset's SHA-256 as lowercase hex.</summary>
public sealed record UpdateInfo(Version Version, string DownloadUrl, string Sha256);

/// <summary>
///     Self-update from the GitHub releases of the repository. The new executable is
///     downloaded next to the running one and swapped in by rename (Windows allows
///     renaming a running image), so the next launch runs the new version.
/// </summary>
public static class UpdateService
{
    public const string LatestReleaseUrl =
        "https://api.github.com/repos/TMH611205/Trevunix/releases/latest";

    public const string AssetName = "Trevunix.exe";

    private const string NewSuffix = ".new";
    private const string OldSuffix = ".old";

    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>
    ///     Only a published single-file build updates itself. A build output or
    ///     <c>dotnet run</c> keeps <c>Trevunix.dll</c> beside the exe and must never be replaced.
    /// </summary>
    public static bool IsSelfUpdatable =>
        !File.Exists(Path.Combine(AppContext.BaseDirectory, "Trevunix.dll"))
        && string.Equals(
            Path.GetFileName(Environment.ProcessPath),
            AssetName,
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>Parses a release tag such as <c>v3.0.0.42</c>.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var text = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out var version) ? version : null;
    }

    public static UpdateInfo? ParseLatestRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var release = doc.RootElement;

        var version = ParseTag(
            release.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null
        );
        if (version is null || Normalize(version) <= Normalize(current))
            return null;

        if (!release.TryGetProperty("assets", out var assets))
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                continue;

            var url = asset.TryGetProperty("browser_download_url", out var u)
                ? u.GetString()
                : null;
            var sha256 = ParseDigest(
                asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null
            );
            // No digest means nothing to check the download against, so no update.
            return string.IsNullOrEmpty(url) || sha256 is null
                ? null
                : new UpdateInfo(version, url, sha256);
        }

        return null;
    }

    /// <summary>Parses GitHub's asset digest (<c>sha256:&lt;64 hex&gt;</c>) into lowercase hex.</summary>
    public static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var hex = digest[prefix.Length..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>True when the file's SHA-256 equals <paramref name="sha256"/> (hex).</summary>
    public static async Task<bool> HasSha256Async(
        string path,
        string sha256,
        CancellationToken cancellationToken = default
    )
    {
        await using var stream = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        return string.Equals(
            Convert.ToHexStringLower(actual),
            sha256,
            StringComparison.OrdinalIgnoreCase
        );
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateClient();
            var json = await client.GetStringAsync(LatestReleaseUrl, cancellationToken);
            return ParseLatestRelease(json, CurrentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Downloads and swaps in the update; false leaves the running exe untouched.</summary>
    public static async Task<bool> DownloadAndStageAsync(
        UpdateInfo update,
        string exePath,
        CancellationToken cancellationToken = default
    )
    {
        var newPath = exePath + NewSuffix;
        try
        {
            using (var client = CreateClient())
            await using (
                var source = await client.GetStreamAsync(update.DownloadUrl, cancellationToken)
            )
            await using (var target = new FileStream(newPath, FileMode.Create, FileAccess.Write))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            if (new FileInfo(newPath).Length == 0)
                throw new IOException("Downloaded update is empty.");

            if (!await HasSha256Async(newPath, update.Sha256, cancellationToken))
                throw new IOException("Downloaded update does not match its SHA-256 digest.");

            return StageDownloadedFile(exePath);
        }
        catch (Exception ex)
            when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(newPath);
            return false;
        }
    }

    /// <summary>Moves <c>{exe}.new</c> into place, keeping the old image as <c>{exe}.old</c>.</summary>
    public static bool StageDownloadedFile(string exePath)
    {
        var newPath = exePath + NewSuffix;
        var oldPath = exePath + OldSuffix;

        if (!File.Exists(newPath))
            return false;

        TryDelete(oldPath);
        File.Move(exePath, oldPath);
        try
        {
            File.Move(newPath, exePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(oldPath, exePath);
            TryDelete(newPath);
            return false;
        }
    }

    public static void CleanupPreviousVersion(string exePath)
    {
        TryDelete(exePath + OldSuffix);
        TryDelete(exePath + NewSuffix);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Trevunix", CurrentVersion.ToString())
        );
        return client;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
