using System.IO;
using Trevunix.Core.Updates;

namespace Trevunix.Core.Tests;

public class UpdateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "TrevunixUpdateTests_" + Guid.NewGuid().ToString("N")
    );

    public UpdateServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Ignore: a leftover temp folder affects nothing else.
        }
    }

    private const string Hash = "9d0182d2837d4f30d5924c67b8f21303935a0b8494a39546e42e0158094eef2f";

    private static string Release(
        string tag,
        string assetName = UpdateService.AssetName,
        string digest = "\"sha256:" + Hash + "\""
    ) =>
        $$"""
            {
              "tag_name": "{{tag}}",
              "assets": [
                { "name": "{{assetName}}", "browser_download_url": "https://example.test/{{assetName}}", "digest": {{digest}} }
              ]
            }
            """;

    [Fact]
    public void ParseLatestRelease_CarriesDigest()
    {
        var update = UpdateService.ParseLatestRelease(Release("v9.0.0"), new Version(2, 0));

        Assert.Equal(Hash, update?.Sha256);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"md5:abc\"")]
    [InlineData("\"sha256:1234\"")]
    [InlineData("\"sha256:zz82d2837d4f30d5924c67b8f21303935a0b8494a39546e42e0158094eef2f\"")]
    public void ParseLatestRelease_MissingOrBadDigest_ReturnsNull(string digest)
    {
        Assert.Null(
            UpdateService.ParseLatestRelease(Release("v9.0.0", digest: digest), new Version(2, 0))
        );
    }

    [Fact]
    public void ParseDigest_UppercaseHex_ReturnsLowercase()
    {
        Assert.Equal(Hash, UpdateService.ParseDigest("SHA256:" + Hash.ToUpperInvariant()));
    }

    [Fact]
    public async Task HasSha256Async_MatchingAndTamperedFile()
    {
        var path = Path.Combine(_dir, "file.bin");
        File.WriteAllText(path, "abc");
        // SHA-256 of "abc".
        const string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        Assert.True(
            await UpdateService.HasSha256Async(path, abc, TestContext.Current.CancellationToken)
        );
        File.WriteAllText(path, "abd");
        Assert.False(
            await UpdateService.HasSha256Async(path, abc, TestContext.Current.CancellationToken)
        );
    }

    [Theory]
    [InlineData("v2.29.0.42", "2.29.0.42")]
    [InlineData("V2.30.1", "2.30.1")]
    [InlineData(" 3.0 ", "3.0")]
    public void ParseTag_ValidTag_ReturnsVersion(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateService.ParseTag(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void ParseTag_InvalidTag_ReturnsNull(string? tag)
    {
        Assert.Null(UpdateService.ParseTag(tag));
    }

    [Fact]
    public void ParseLatestRelease_NewerRevision_ReturnsUpdate()
    {
        var update = UpdateService.ParseLatestRelease(
            Release("v2.29.0.5"),
            new Version(2, 29, 0, 4)
        );

        Assert.NotNull(update);
        Assert.Equal(new Version(2, 29, 0, 5), update.Version);
        Assert.Equal("https://example.test/Trevunix.exe", update.DownloadUrl);
    }

    [Fact]
    public void ParseLatestRelease_ThreePartTagNewerThanDevBuild_ReturnsUpdate()
    {
        var update = UpdateService.ParseLatestRelease(Release("v2.30.0"), new Version(2, 29, 0, 0));

        Assert.NotNull(update);
    }

    [Theory]
    [InlineData("v2.29.0.4")]
    [InlineData("v2.29.0")]
    [InlineData("v2.28.9.99")]
    public void ParseLatestRelease_SameOrOlder_ReturnsNull(string tag)
    {
        Assert.Null(UpdateService.ParseLatestRelease(Release(tag), new Version(2, 29, 0, 4)));
    }

    [Fact]
    public void ParseLatestRelease_MissingExeAsset_ReturnsNull()
    {
        Assert.Null(
            UpdateService.ParseLatestRelease(
                Release("v9.0.0", "Trevunix.zip"),
                new Version(2, 29, 0, 0)
            )
        );
    }

    [Fact]
    public void StageDownloadedFile_NewFilePresent_SwapsAndKeepsOld()
    {
        var exe = Path.Combine(_dir, "Trevunix.exe");
        File.WriteAllText(exe, "old");
        File.WriteAllText(exe + ".new", "new");

        Assert.True(UpdateService.StageDownloadedFile(exe));

        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public void StageDownloadedFile_NoNewFile_ReturnsFalseAndLeavesExe()
    {
        var exe = Path.Combine(_dir, "Trevunix.exe");
        File.WriteAllText(exe, "old");

        Assert.False(UpdateService.StageDownloadedFile(exe));
        Assert.Equal("old", File.ReadAllText(exe));
    }

    [Fact]
    public void CleanupPreviousVersion_RemovesLeftovers()
    {
        var exe = Path.Combine(_dir, "Trevunix.exe");
        File.WriteAllText(exe, "current");
        File.WriteAllText(exe + ".old", "old");
        File.WriteAllText(exe + ".new", "partial");

        UpdateService.CleanupPreviousVersion(exe);

        Assert.True(File.Exists(exe));
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public void IsSelfUpdatable_UnderTestHost_IsFalse()
    {
        Assert.False(UpdateService.IsSelfUpdatable);
    }
}
