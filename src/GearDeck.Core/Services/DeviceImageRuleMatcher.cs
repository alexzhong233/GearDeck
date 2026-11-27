using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GearDeck.Core.Models;

namespace GearDeck.Core.Services;

public sealed class DeviceImageRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? VidPid { get; set; }
    public string? Vid { get; set; }
    public List<string> Keywords { get; set; } = [];
    public string? NameRegex { get; set; }
    public bool SharedReceiver { get; set; }
    public string? Kind { get; set; }
    public string? Preset { get; set; }
    public string? OpenMouseSlug { get; set; }
    public string? CloudUrl { get; set; }
    public string? PiperSvg { get; set; }
    public string? AssetFile { get; set; }
}

public sealed class DeviceImageGalleryEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Brand { get; set; } = "";
    public string MatchHint { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public bool IsPiperSvg { get; set; }
    public string? AssetFile { get; set; }
}

public sealed class DeviceImageManifest
{
    public int Version { get; set; } = 1;
    public List<DeviceImageRule> Rules { get; set; } = [];
    public List<DeviceImageGalleryEntry> Gallery { get; set; } = [];
}

public sealed record DeviceImageMatchResult(
    DeviceImageRule? Rule,
    string PresetId,
    string? CloudUrl,
    string? PiperSvg,
    bool IsExactModelMatch,
    string? AssetFile = null);

/// <summary>
/// 外置 JSON 设备造型匹配引擎（对应 OpenMouse VID:PID 目录与型号正则分层匹配）：
/// 1) 精确 VID:PID（IsExactModelMatch = true）
/// 2) 厂商 VID + 型号关键词
/// 3) 型号关键词 + 设备品类
/// 4) 厂商 VID 同家族兜底
/// 5) 设备品类兜底
/// 本地/云端新增规则可直接通过修改 JSON 扩展新外设，无需改动或重编 C# 代码。
/// </summary>
public static class DeviceImageRuleMatcher
{
    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static DeviceImageManifest ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new DeviceImageManifest();

        try
        {
            var manifest = JsonSerializer.Deserialize<DeviceImageManifest>(json, JsonOpts) ?? new DeviceImageManifest();
            manifest.Rules ??= [];
            manifest.Gallery ??= [];
            foreach (var r in manifest.Rules)
                r.Keywords ??= [];
            return manifest;
        }
        catch
        {
            return new DeviceImageManifest();
        }
    }

    public static string SerializeManifest(DeviceImageManifest manifest)
        => JsonSerializer.Serialize(manifest, JsonOpts);

    /// <summary>
    /// 合并两份清单：overrideManifest 中的规则优先级高于 baseManifest（同 Id 或同 VidPid 替换，其余前置追加）。
    /// </summary>
    public static DeviceImageManifest MergeManifests(DeviceImageManifest baseManifest, DeviceImageManifest? overrideManifest)
    {
        if (overrideManifest == null)
            return baseManifest;

        var rules = new List<DeviceImageRule>();
        var overrideIds = overrideManifest.Rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .Select(r => r.Id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var baseById = baseManifest.Rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .GroupBy(r => r.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var r in overrideManifest.Rules)
        {
            if (string.IsNullOrWhiteSpace(r.AssetFile) &&
                !string.IsNullOrWhiteSpace(r.Id) &&
                baseById.TryGetValue(r.Id.Trim(), out var baseRule) &&
                !string.IsNullOrWhiteSpace(baseRule.AssetFile) &&
                string.Equals(r.CloudUrl ?? "", baseRule.CloudUrl ?? "", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.OpenMouseSlug ?? "", baseRule.OpenMouseSlug ?? "", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.PiperSvg ?? "", baseRule.PiperSvg ?? "", StringComparison.OrdinalIgnoreCase))
            {
                rules.Add(new DeviceImageRule
                {
                    Id = r.Id,
                    Name = r.Name,
                    VidPid = r.VidPid,
                    Vid = r.Vid,
                    Keywords = r.Keywords,
                    NameRegex = r.NameRegex,
                    SharedReceiver = r.SharedReceiver,
                    Kind = r.Kind,
                    Preset = r.Preset,
                    OpenMouseSlug = r.OpenMouseSlug,
                    CloudUrl = r.CloudUrl,
                    PiperSvg = r.PiperSvg,
                    AssetFile = baseRule.AssetFile,
                });
            }
            else
            {
                rules.Add(r);
            }
        }

        foreach (var b in baseManifest.Rules)
        {
            if (!string.IsNullOrWhiteSpace(b.Id) && overrideIds.Contains(b.Id.Trim()))
                continue;
            rules.Add(b);
        }

        var galleryByUrl = baseManifest.Gallery
            .Where(g => !string.IsNullOrWhiteSpace(g.ImageUrl))
            .GroupBy(g => g.ImageUrl.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(grp => grp.Key, grp => grp.First(), StringComparer.OrdinalIgnoreCase);

        var gallery = new List<DeviceImageGalleryEntry>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in overrideManifest.Gallery.Concat(baseManifest.Gallery))
        {
            if (!string.IsNullOrWhiteSpace(g.ImageUrl) && seenUrls.Add(g.ImageUrl.Trim()))
            {
                if (string.IsNullOrWhiteSpace(g.AssetFile) &&
                    galleryByUrl.TryGetValue(g.ImageUrl.Trim(), out var baseGallery) &&
                    !string.IsNullOrWhiteSpace(baseGallery.AssetFile))
                {
                    gallery.Add(new DeviceImageGalleryEntry
                    {
                        Id = g.Id,
                        Name = g.Name,
                        Brand = g.Brand,
                        MatchHint = g.MatchHint,
                        ImageUrl = g.ImageUrl,
                        IsPiperSvg = g.IsPiperSvg,
                        AssetFile = baseGallery.AssetFile,
                    });
                }
                else
                {
                    gallery.Add(g);
                }
            }
        }

        return new DeviceImageManifest
        {
            Version = Math.Max(baseManifest.Version, overrideManifest.Version),
            Rules = rules,
            Gallery = gallery,
        };
    }

    public static DeviceImageMatchResult MatchRule(
        PeripheralDevice device,
        IReadOnlyList<DeviceImageRule> rules,
        string fallbackPreset = "razer_viper_v3_pro")
    {
        TryExtractVidPid(device, out string devVid, out string devPid);
        string devVidPid = (!string.IsNullOrEmpty(devVid) && !string.IsNullOrEmpty(devPid))
            ? $"{devVid}:{devPid}"
            : "";

        string devName = device.Name.ToLowerInvariant();
        string devId = device.Id.ToLowerInvariant();
        string devProv = device.Provider.ToLowerInvariant();
        string devFuzzy = NormalizeFuzzyText($"{device.Name} {device.Provider}");
        string devKind = device.Kind.ToString();

        DeviceImageRule? bestRule = null;
        int bestScore = 0;
        bool bestExact = false;

        foreach (var rule in rules)
        {
            if (!string.IsNullOrWhiteSpace(rule.Kind) &&
                !string.Equals(rule.Kind.Trim(), devKind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rVidPid = NormalizeVidPid(rule.VidPid);
            string rVid = NormalizeHex4(rule.Vid);

            // 0. OpenMouse 风格：上报真实型号名称正则优先（规避共享接收器 PID 冲突）
            if (!string.IsNullOrWhiteSpace(rule.NameRegex))
            {
                bool vidOk = string.IsNullOrEmpty(rVid) || devVid == rVid;
                if (vidOk && MatchesRegex(rule.NameRegex, device.Name))
                {
                    int score = 1100;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestRule = rule;
                        bestExact = true;
                    }
                }
                continue;
            }

            // 1. 精确 VID:PID 匹配（共享接收器降权，让 NameRegex / 关键词规则优先）
            if (!string.IsNullOrEmpty(rVidPid))
            {
                if (!string.IsNullOrEmpty(devVidPid) && string.Equals(rVidPid, devVidPid, StringComparison.OrdinalIgnoreCase))
                {
                    int score = rule.SharedReceiver ? 450 : 1000;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestRule = rule;
                        bestExact = !rule.SharedReceiver;
                    }
                }
                continue;
            }

            int kwHits = CountKeywordHits(rule.Keywords, devName, devId, devProv, devFuzzy);
            bool hasVid = !string.IsNullOrEmpty(rVid);
            bool hasKeywords = rule.Keywords.Count > 0;

            // 2. 厂商 VID + 关键词
            if (hasVid && hasKeywords)
            {
                if (devVid == rVid && kwHits > 0)
                {
                    int score = 500 + kwHits * 10;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestRule = rule;
                        bestExact = true;
                    }
                }
                continue;
            }

            // 3. 仅关键词（+可选品类过滤）
            if (!hasVid && hasKeywords)
            {
                if (kwHits > 0)
                {
                    int score = (!string.IsNullOrWhiteSpace(rule.Kind) ? 350 : 300) + kwHits * 10;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestRule = rule;
                        bestExact = false;
                    }
                }
                continue;
            }

            // 4. 厂商 VID 家族兜底
            if (hasVid)
            {
                if (devVid == rVid)
                {
                    int score = !string.IsNullOrWhiteSpace(rule.Kind) ? 220 : 200;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestRule = rule;
                        bestExact = false;
                    }
                }
                continue;
            }

            // 5. 纯品类兜底
            if (!string.IsNullOrWhiteSpace(rule.Kind))
            {
                int score = 100;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestRule = rule;
                    bestExact = false;
                }
            }
        }

        string preset = !string.IsNullOrWhiteSpace(bestRule?.Preset) ? bestRule.Preset! : fallbackPreset;
        string? cloudUrl = ResolveRuleCloudUrl(bestRule);
        string? piperSvg = !string.IsNullOrWhiteSpace(bestRule?.PiperSvg) ? bestRule.PiperSvg : null;
        string? assetFile = !string.IsNullOrWhiteSpace(bestRule?.AssetFile) ? bestRule.AssetFile : null;
        return new DeviceImageMatchResult(bestRule, preset, cloudUrl, piperSvg, bestExact, assetFile);
    }

    public static string? ResolveRuleCloudUrl(DeviceImageRule? rule)
    {
        if (rule == null) return null;
        if (!string.IsNullOrWhiteSpace(rule.CloudUrl))
            return rule.CloudUrl;
        if (!string.IsNullOrWhiteSpace(rule.OpenMouseSlug))
            return $"https://img.openmouse.app/{rule.OpenMouseSlug.Trim().TrimStart('/')}";
        return null;
    }

    private static bool MatchesRegex(string pattern, string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var regex = RegexCache.GetOrAdd(pattern, static p =>
        {
            try
            {
                return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            }
            catch
            {
                return null;
            }
        });
        if (regex == null) return false;
        try { return regex.IsMatch(input); }
        catch { return false; }
    }

    public static bool TryExtractVidPid(PeripheralDevice d, out string vidHex, out string pidHex)
    {
        if (d.VendorId is { } v && v > 0 && d.ProductId is { } p && p > 0)
        {
            vidHex = v.ToString("x4", CultureInfo.InvariantCulture);
            pidHex = p.ToString("x4", CultureInfo.InvariantCulture);
            return true;
        }

        vidHex = "";
        pidHex = "";
        int colon = d.Id.IndexOf(':');
        if (colon < 0) return false;
        string prefix = d.Id[..colon].ToLowerInvariant();
        string tail = d.Id[(colon + 1)..];
        var parts = tail.Split('-');
        if (parts.Length >= 2 &&
            ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort vHex) &&
            ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort pHex))
        {
            vidHex = vHex.ToString("x4", CultureInfo.InvariantCulture);
            pidHex = pHex.ToString("x4", CultureInfo.InvariantCulture);
            return true;
        }

        var colonParts = tail.Split(':');
        if (ushort.TryParse(colonParts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort singlePid))
        {
            string? brandVid = prefix switch
            {
                "logi" => "046d",
                "razer" => "1532",
                "rog" => "0b05",
                "sony" => "054c",
                "ps" => "054c",
                "flydigi" => "37d7",
                "atk" => "373b",
                "aula" => "38a6",
                "wlmouse" => "36a7",
                "lamzu" => "373e",
                "am" => "0e8d",
                "mchose" => "24ae",
                "pulsar" => "3710",
                "egg" => "3367",
                "rapoo" => "24ae",
                "finalmouse" => "361d",
                "steelseries" => "1038",
                "corsair" => "1b1c",
                "hyperx" => "03f0",
                "audeze" => "3329",
                "wooting" => "31e3",
                "keychron" => "3434",
                "monsgeek" => "3151",
                "drunkdeer" => "352d",
                "nuphy" => "19f5",
                "nintendo" => "057e",
                "8bitdo" => "2dc8",
                _ => null,
            };
            if (brandVid != null)
            {
                vidHex = brandVid;
                pidHex = singlePid.ToString("x4", CultureInfo.InvariantCulture);
                return true;
            }
        }
        return false;
    }

    private static readonly string[] FuzzyNoiseTokens =
    [
        "wireless", "wired", "dongle", "receiver", "usb-c", "usbc", "bluetooth", "2.4ghz", "2.4g",
        "gaming", "mouse", "keyboard", "headset", "controller", "8k", "4k", "bt", "usb",
        "无线", "有线", "接收器",
    ];

    /// <summary>规范化型号文本（移除空格/连接符与 2.4G/8K/Wireless 等后缀词），用于跨命名习惯的模糊匹配。</summary>
    public static string NormalizeFuzzyText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        string text = raw.ToLowerInvariant();
        foreach (string noise in FuzzyNoiseTokens)
            text = text.Replace(noise, "", StringComparison.Ordinal);

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c) || (c >= '\u4e00' && c <= '\u9fff'))
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static int CountKeywordHits(List<string> keywords, string devName, string devId, string devProv, string devFuzzy)
    {
        int hits = 0;
        foreach (string raw in keywords)
        {
            string kw = raw.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(kw)) continue;
            string kwFuzzy = NormalizeFuzzyText(kw);
            if (devName.Contains(kw, StringComparison.Ordinal) ||
                devId.Contains(kw, StringComparison.Ordinal) ||
                devProv.Contains(kw, StringComparison.Ordinal) ||
                (!string.IsNullOrEmpty(kwFuzzy) && kwFuzzy.Length >= 3 && devFuzzy.Contains(kwFuzzy, StringComparison.Ordinal)))
            {
                hits++;
            }
        }
        return hits;
    }

    private static string NormalizeVidPid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string clean = raw.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase).Replace('-', ':');
        var parts = clean.Split(':');
        if (parts.Length != 2) return "";
        string v = NormalizeHex4(parts[0]);
        string p = NormalizeHex4(parts[1]);
        return (!string.IsNullOrEmpty(v) && !string.IsNullOrEmpty(p)) ? $"{v}:{p}" : "";
    }

    private static string NormalizeHex4(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string clean = raw.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        return ushort.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort val)
            ? val.ToString("x4", CultureInfo.InvariantCulture)
            : "";
    }
}
