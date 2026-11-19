using System.Net.Http.Headers;
using System.Text.Json;

namespace GearDeck.Core.Services;

/// <summary>GitHub 镜像站 URL 标准化与重写工具（支持直连、前缀加速与 {url} 模板）。</summary>
public static class GitHubMirrorHelper
{
    private static readonly string[] ProxiedHostPrefixes =
    [
        "https://github.com/",
        "http://github.com/",
        "https://raw.githubusercontent.com/",
        "https://objects.githubusercontent.com/",
        "https://codeload.github.com/",
        "https://gist.githubusercontent.com/",
        "https://release-assets.githubusercontent.com/",
    ];

    public static string NormalizeMirror(string? customMirror)
    {
        if (string.IsNullOrWhiteSpace(customMirror))
            return string.Empty;

        string trimmed = customMirror.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed.TrimStart('/');
        }
        return trimmed;
    }

    public static string RewriteUrl(string originalUrl, string? customMirror)
    {
        if (string.IsNullOrWhiteSpace(originalUrl))
            return originalUrl;

        string mirror = NormalizeMirror(customMirror);
        if (mirror.Length == 0)
            return originalUrl;

        if (mirror.Contains("{url_encoded}", StringComparison.OrdinalIgnoreCase))
            return mirror.Replace("{url_encoded}", Uri.EscapeDataString(originalUrl), StringComparison.OrdinalIgnoreCase);

        if (mirror.Contains("{url}", StringComparison.OrdinalIgnoreCase))
            return mirror.Replace("{url}", originalUrl, StringComparison.OrdinalIgnoreCase);

        string prefix = mirror.TrimEnd('/') + "/";
        if (originalUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return originalUrl;

        bool isGitHubContentUrl = ProxiedHostPrefixes.Any(h =>
            originalUrl.StartsWith(h, StringComparison.OrdinalIgnoreCase));

        if (!isGitHubContentUrl &&
            !mirror.Contains("api.github", StringComparison.OrdinalIgnoreCase))
        {
            return originalUrl;
        }

        return prefix + originalUrl;
    }
}

public sealed record ReleaseUpdateInfo(
    string Version,
    string Tag,
    string PublishedAt,
    string HtmlUrl,
    string Notes,
    string RawDownloadUrl,
    string DownloadUrl,
    string FullSha256,
    long FullSize,
    string RawPatchUrl,
    string PatchUrl,
    string PatchSha256,
    long PatchSize);

public sealed record UpdateCheckOutcome(
    bool Success,
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string UsedEndpoint,
    bool UsedMirror,
    string? MirrorFallbackWarning,
    string? ErrorMessage,
    ReleaseUpdateInfo? Release);

/// <summary>支持直连 GitHub 与自定义镜像站的版本检测与清单解析器。</summary>
public static class UpdateClient
{
    public const string DefaultRepoSlug = "alexzhong233/GearDeck";

    public static string NormalizeVersionString(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "0.0.0";

        string s = version.Trim().TrimStart('v', 'V');
        int suffixIdx = s.IndexOfAny(['-', '+']);
        if (suffixIdx > 0)
            s = s[..suffixIdx];
        return s.Trim();
    }

    public static int CompareVersions(string? current, string? candidate)
    {
        int[] a = ParseParts(NormalizeVersionString(current));
        int[] b = ParseParts(NormalizeVersionString(candidate));
        for (int i = 0; i < 4; i++)
        {
            if (a[i] != b[i])
                return a[i].CompareTo(b[i]);
        }
        return 0;
    }

    private static int[] ParseParts(string normalized)
    {
        int[] parts = new int[4];
        string[] segs = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < Math.Min(segs.Length, 4); i++)
        {
            if (int.TryParse(segs[i], out int val))
                parts[i] = val;
        }
        return parts;
    }

    public static ReleaseUpdateInfo? ParseLatestManifestJson(string json, string? customMirror)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        string version = NormalizeVersionString(GetStringCI(root, "version"));
        string tag = GetStringCI(root, "tag");
        if (version == "0.0.0" && !string.IsNullOrWhiteSpace(tag))
            version = NormalizeVersionString(tag);
        if (version == "0.0.0")
            return null;

        if (string.IsNullOrWhiteSpace(tag))
            tag = "v" + version;

        string rawDownload = GetStringCI(root, "downloadUrl");
        string rawPatch = GetStringCI(root, "patchUrl");
        if (string.IsNullOrWhiteSpace(rawDownload) && string.IsNullOrWhiteSpace(rawPatch))
            return null;

        return new ReleaseUpdateInfo(
            Version: version,
            Tag: tag,
            PublishedAt: GetStringCI(root, "publishedAt"),
            HtmlUrl: GetStringCI(root, "htmlUrl"),
            Notes: GetStringCI(root, "notes"),
            RawDownloadUrl: rawDownload,
            DownloadUrl: GitHubMirrorHelper.RewriteUrl(rawDownload, customMirror),
            FullSha256: GetStringCI(root, "fullSha256").ToUpperInvariant(),
            FullSize: GetInt64CI(root, "fullSize"),
            RawPatchUrl: rawPatch,
            PatchUrl: GitHubMirrorHelper.RewriteUrl(rawPatch, customMirror),
            PatchSha256: GetStringCI(root, "patchSha256").ToUpperInvariant(),
            PatchSize: GetInt64CI(root, "patchSize"));
    }

    public static ReleaseUpdateInfo? ParseGitHubReleaseApiJson(string json, string? customMirror)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        string tag = GetStringCI(root, "tag_name");
        string version = NormalizeVersionString(tag);
        if (version == "0.0.0")
            return null;

        string htmlUrl = GetStringCI(root, "html_url");
        string publishedAt = GetStringCI(root, "published_at");
        string notes = GetStringCI(root, "body");

        string rawDownload = "";
        long fullSize = 0;
        string fullSha = "";
        string rawPatch = "";
        long patchSize = 0;
        string patchSha = "";

        if (TryGetPropertyCI(root, "assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsEl.EnumerateArray())
            {
                string name = GetStringCI(asset, "name");
                string url = GetStringCI(asset, "browser_download_url");
                long size = GetInt64CI(asset, "size");
                string sha = ExtractSha256Digest(GetStringCI(asset, "digest"));

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    continue;

                if (name.Contains("patch", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    rawPatch = url;
                    patchSize = size;
                    patchSha = sha;
                }
                else if (name.EndsWith("win-x64.zip", StringComparison.OrdinalIgnoreCase) ||
                         (string.IsNullOrEmpty(rawDownload) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    rawDownload = url;
                    fullSize = size;
                    fullSha = sha;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(rawDownload) && string.IsNullOrWhiteSpace(rawPatch))
            return null;

        return new ReleaseUpdateInfo(
            Version: version,
            Tag: tag,
            PublishedAt: publishedAt,
            HtmlUrl: htmlUrl,
            Notes: notes,
            RawDownloadUrl: rawDownload,
            DownloadUrl: GitHubMirrorHelper.RewriteUrl(rawDownload, customMirror),
            FullSha256: fullSha,
            FullSize: fullSize,
            RawPatchUrl: rawPatch,
            PatchUrl: GitHubMirrorHelper.RewriteUrl(rawPatch, customMirror),
            PatchSha256: patchSha,
            PatchSize: patchSize);
    }

    public static async Task<UpdateCheckOutcome> CheckForUpdateAsync(
        string repoSlug,
        string currentVersion,
        string? customMirror,
        bool allowDirectFallback = true,
        HttpClient? client = null,
        CancellationToken ct = default)
    {
        string slug = string.IsNullOrWhiteSpace(repoSlug) ? DefaultRepoSlug : repoSlug.Trim().Trim('/');
        string normalizedCurrent = NormalizeVersionString(currentVersion);
        string mirror = GitHubMirrorHelper.NormalizeMirror(customMirror);

        string latestReleaseDownloadJson = $"https://github.com/{slug}/releases/latest/download/latest.json";
        string rawMainJson = $"https://raw.githubusercontent.com/{slug}/main/latest.json";
        string apiLatestJson = $"https://api.github.com/repos/{slug}/releases/latest";

        bool disposeClient = client == null;
        HttpClient http = client ?? CreateDefaultHttpClient();

        try
        {
            string? mirrorError = null;
            if (mirror.Length > 0)
            {
                var mirrorCandidates = new[]
                {
                    GitHubMirrorHelper.RewriteUrl(latestReleaseDownloadJson, mirror),
                    GitHubMirrorHelper.RewriteUrl(rawMainJson, mirror),
                };

                foreach (string url in mirrorCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var (release, err) = await TryFetchManifestAsync(http, url, mirror, isApi: false, ct);
                    if (release != null)
                    {
                        return BuildSuccessOutcome(normalizedCurrent, release, url, usedMirror: true, fallbackWarning: null);
                    }
                    mirrorError = err;
                }

                if (!allowDirectFallback)
                {
                    return new UpdateCheckOutcome(
                        Success: false,
                        HasUpdate: false,
                        CurrentVersion: normalizedCurrent,
                        LatestVersion: normalizedCurrent,
                        UsedEndpoint: mirrorCandidates[0],
                        UsedMirror: true,
                        MirrorFallbackWarning: null,
                        ErrorMessage: $"自定义镜像站检测失败：{mirrorError ?? "无法解析版本清单"}",
                        Release: null);
                }
            }

            string? fallbackWarning = mirror.Length > 0
                ? $"镜像站暂不可用（{mirrorError ?? "请求失败"}），已自动回退直连 GitHub"
                : null;

            var directCandidates = new (string Url, bool IsApi)[]
            {
                (latestReleaseDownloadJson, false),
                (apiLatestJson, true),
                (rawMainJson, false),
            };

            string? lastError = mirrorError;
            foreach (var (url, isApi) in directCandidates)
            {
                var (release, err) = await TryFetchManifestAsync(http, url, mirror, isApi, ct);
                if (release != null)
                {
                    return BuildSuccessOutcome(normalizedCurrent, release, url, usedMirror: false, fallbackWarning);
                }
                lastError = err;
            }

            return new UpdateCheckOutcome(
                Success: false,
                HasUpdate: false,
                CurrentVersion: normalizedCurrent,
                LatestVersion: normalizedCurrent,
                UsedEndpoint: latestReleaseDownloadJson,
                UsedMirror: false,
                MirrorFallbackWarning: fallbackWarning,
                ErrorMessage: $"检查更新失败：{lastError ?? "无法连接 GitHub"}",
                Release: null);
        }
        finally
        {
            if (disposeClient)
                http.Dispose();
        }
    }

    public static HttpClient CreateDefaultHttpClient(int timeoutSeconds = 12)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GearDeck-Updater/1.0 (+https://github.com/alexzhong233/GearDeck)");
        http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return http;
    }

    private static UpdateCheckOutcome BuildSuccessOutcome(
        string currentVersion,
        ReleaseUpdateInfo release,
        string usedEndpoint,
        bool usedMirror,
        string? fallbackWarning)
    {
        bool hasUpdate = CompareVersions(currentVersion, release.Version) < 0;
        return new UpdateCheckOutcome(
            Success: true,
            HasUpdate: hasUpdate,
            CurrentVersion: currentVersion,
            LatestVersion: release.Version,
            UsedEndpoint: usedEndpoint,
            UsedMirror: usedMirror,
            MirrorFallbackWarning: fallbackWarning,
            ErrorMessage: null,
            Release: release);
    }

    private static async Task<(ReleaseUpdateInfo? Release, string? Error)> TryFetchManifestAsync(
        HttpClient http,
        string url,
        string? customMirror,
        bool isApi,
        CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (isApi)
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})");

            string body = await resp.Content.ReadAsStringAsync(ct);
            ReleaseUpdateInfo? parsed = isApi
                ? ParseGitHubReleaseApiJson(body, customMirror)
                : ParseLatestManifestJson(body, customMirror);

            if (parsed == null)
                return (null, "响应内容不是有效的 GearDeck 版本清单");

            return (parsed, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static bool TryGetPropertyCI(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string GetStringCI(JsonElement obj, string name)
    {
        if (TryGetPropertyCI(obj, name, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString() ?? "";
        return "";
    }

    private static long GetInt64CI(JsonElement obj, string name)
    {
        if (TryGetPropertyCI(obj, name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long v))
            return v;
        return 0;
    }

    private static string ExtractSha256Digest(string digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return "";
        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..].Trim().ToUpperInvariant()
            : digest.Trim().ToUpperInvariant();
    }
}
