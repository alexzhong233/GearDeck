using System.Net;
using System.Net.Http;
using System.Text;
using GearDeck.Core.Services;
using Xunit;

namespace GearDeck.Tests;

public class UpdateClientTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public List<string> RequestedUrls { get; } = [];

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri?.ToString() ?? "");
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public void RewriteUrl_HandlesEmptyPrefixTemplateAndNoDoublePrefix()
    {
        const string raw = "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.1/GearDeck-patch-v1.0.1-win-x64.zip";

        Assert.Equal(raw, GitHubMirrorHelper.RewriteUrl(raw, null));
        Assert.Equal(raw, GitHubMirrorHelper.RewriteUrl(raw, "   "));

        Assert.Equal(
            "https://ghfast.top/" + raw,
            GitHubMirrorHelper.RewriteUrl(raw, "https://ghfast.top"));

        Assert.Equal(
            "https://ghfast.top/" + raw,
            GitHubMirrorHelper.RewriteUrl(raw, "ghfast.top/"));

        string alreadyPrefixed = "https://ghfast.top/" + raw;
        Assert.Equal(
            alreadyPrefixed,
            GitHubMirrorHelper.RewriteUrl(alreadyPrefixed, "https://ghfast.top/"));

        Assert.Equal(
            "https://mirror.example.com/fetch?target=" + raw,
            GitHubMirrorHelper.RewriteUrl(raw, "https://mirror.example.com/fetch?target={url}"));

        Assert.Equal(
            "https://mirror.example.com/fetch?target=" + Uri.EscapeDataString(raw),
            GitHubMirrorHelper.RewriteUrl(raw, "https://mirror.example.com/fetch?target={url_encoded}"));

        const string apiUrl = "https://api.github.com/repos/alexzhong233/GearDeck/releases/latest";
        Assert.Equal(apiUrl, GitHubMirrorHelper.RewriteUrl(apiUrl, "https://ghfast.top/"));
    }

    [Fact]
    public void CompareVersions_HandlesTagsAndMultiSegmentVersions()
    {
        Assert.True(UpdateClient.CompareVersions("1.0.0", "v1.0.1") < 0);
        Assert.True(UpdateClient.CompareVersions("v1.0.0", "1.1.0") < 0);
        Assert.Equal(0, UpdateClient.CompareVersions("v1.0.1", "1.0.1.0"));
        Assert.True(UpdateClient.CompareVersions("1.0.2", "1.0.1") > 0);
        Assert.Equal(0, UpdateClient.CompareVersions("1.0.1+abc123", "1.0.1"));
        Assert.True(UpdateClient.CompareVersions("v1.0.1-beta", "1.0.1+abc123") < 0);
        Assert.True(UpdateClient.CompareVersions("2.0.0-beta.1", "2.0.0-beta.2") < 0);
        Assert.True(UpdateClient.CompareVersions("2.0.0-beta.2", "2.0.0") < 0);
    }

    [Fact]
    public void ParseLatestManifestJson_AppliesCustomMirrorToAssetUrls()
    {
        const string json = """
        {
          "version": "1.0.1",
          "tag": "v1.0.1",
          "publishedAt": "2026-11-19T16:00:00Z",
          "htmlUrl": "https://github.com/alexzhong233/GearDeck/releases/tag/v1.0.1",
          "downloadUrl": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.1/GearDeck-v1.0.1-win-x64.zip",
          "fullSha256": "abc123",
          "fullSize": 35000000,
          "patchUrl": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.1/GearDeck-patch-v1.0.1-win-x64.zip",
          "patchSha256": "def456",
          "patchSize": 1200000,
          "notes": "Test release notes"
        }
        """;

        var info = UpdateClient.ParseLatestManifestJson(json, "https://ghfast.top/");

        Assert.NotNull(info);
        Assert.Equal("1.0.1", info!.Version);
        Assert.Equal("v1.0.1", info.Tag);
        Assert.Equal("ABC123", info.FullSha256);
        Assert.Equal("DEF456", info.PatchSha256);
        Assert.Equal(1200000, info.PatchSize);
        Assert.StartsWith("https://ghfast.top/https://github.com/", info.DownloadUrl);
        Assert.StartsWith("https://ghfast.top/https://github.com/", info.PatchUrl);
        Assert.StartsWith("https://github.com/", info.RawPatchUrl);
    }

    [Fact]
    public void ParseGitHubReleaseApiJson_ExtractsPatchAndFullZipAssets()
    {
        const string json = """
        {
          "tag_name": "v1.0.1",
          "html_url": "https://github.com/alexzhong233/GearDeck/releases/tag/v1.0.1",
          "published_at": "2026-11-19T16:00:00Z",
          "body": "Release v1.0.1",
          "assets": [
            {
              "name": "GearDeck-patch-v1.0.1-win-x64.zip",
              "browser_download_url": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.1/GearDeck-patch-v1.0.1-win-x64.zip",
              "size": 950000,
              "digest": "sha256:001122aabb"
            },
            {
              "name": "GearDeck-v1.0.1-win-x64.zip",
              "browser_download_url": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.1/GearDeck-v1.0.1-win-x64.zip",
              "size": 34000000
            }
          ]
        }
        """;

        var info = UpdateClient.ParseGitHubReleaseApiJson(json, "https://gh-proxy.com/");

        Assert.NotNull(info);
        Assert.Equal("1.0.1", info!.Version);
        Assert.Equal("001122AABB", info.PatchSha256);
        Assert.Equal("https://gh-proxy.com/https://github.com/alexzhong233/GearDeck/releases/download/v1.0.1/GearDeck-patch-v1.0.1-win-x64.zip", info.PatchUrl);
        Assert.Equal("https://gh-proxy.com/https://github.com/alexzhong233/GearDeck/releases/download/v1.0.1/GearDeck-v1.0.1-win-x64.zip", info.DownloadUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_UsesMirrorFirstAndFallsBackToDirectWhenMirrorFails()
    {
        const string manifestJson = """
        {
          "version": "1.0.1",
          "tag": "v1.0.1",
          "downloadUrl": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.1/GearDeck-v1.0.1-win-x64.zip",
          "patchUrl": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.1/GearDeck-patch-v1.0.1-win-x64.zip"
        }
        """;

        var handler = new StubHttpMessageHandler(req =>
        {
            string url = req.RequestUri!.ToString();
            if (url.StartsWith("https://broken-mirror.example/", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.BadGateway);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifestJson, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var outcome = await UpdateClient.CheckForUpdateAsync(
            "alexzhong233/GearDeck",
            "1.0.0",
            "https://broken-mirror.example/",
            allowDirectFallback: true,
            client: http);

        Assert.True(outcome.Success);
        Assert.True(outcome.HasUpdate);
        Assert.False(outcome.UsedMirror);
        Assert.NotNull(outcome.MirrorFallbackWarning);
        Assert.Equal("1.0.1", outcome.LatestVersion);
        Assert.Contains(handler.RequestedUrls, u => u.StartsWith("https://broken-mirror.example/"));
        Assert.Contains(handler.RequestedUrls, u => u.StartsWith("https://github.com/alexzhong233/GearDeck/releases/latest/download/latest.json"));
    }

    [Fact]
    public void ParseGitHubReleaseApiJson_ExtractsMacDmgAndZipAlongsideWindowsAssets()
    {
        const string json = """
        {
          "tag_name": "v1.0.2",
          "html_url": "https://github.com/alexzhong233/GearDeck/releases/tag/v1.0.2",
          "published_at": "2026-11-27T10:00:00Z",
          "body": "Release v1.0.2",
          "assets": [
            {
              "name": "GearDeck-v1.0.2-win-x64.zip",
              "browser_download_url": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.2/GearDeck-v1.0.2-win-x64.zip",
              "size": 34000000
            },
            {
              "name": "GearDeck-v1.0.2-osx-arm64.dmg",
              "browser_download_url": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.2/GearDeck-v1.0.2-osx-arm64.dmg",
              "size": 45000000,
              "digest": "sha256:aa11bb22"
            },
            {
              "name": "GearDeck-v1.0.2-osx-x64.zip",
              "browser_download_url": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.2/GearDeck-v1.0.2-osx-x64.zip",
              "size": 46000000
            }
          ]
        }
        """;

        var info = UpdateClient.ParseGitHubReleaseApiJson(json, "https://ghfast.top/");
        Assert.NotNull(info);
        Assert.Equal("GearDeck-v1.0.2-osx-arm64.dmg", info!.MacArm64AssetName);
        Assert.Equal("AA11BB22", info.MacArm64Sha256);
        Assert.StartsWith("https://ghfast.top/https://github.com/", info.MacArm64Url);
        Assert.Equal("GearDeck-v1.0.2-osx-x64.zip", info.MacX64AssetName);
        Assert.EndsWith("win-x64.zip", info.RawDownloadUrl);
    }
}
