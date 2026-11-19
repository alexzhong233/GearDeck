using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GearDeck.Core.Models;

namespace GearDeck.Core.Services;

public sealed class DeviceImageRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? VidPid { get; set; }
    public string? Vid { get; set; }
    public List<string> Keywords { get; set; } = [];
    public string? Kind { get; set; }
    public string? Preset { get; set; }
    public string? CloudUrl { get; set; }
    public string? PiperSvg { get; set; }
}

public sealed class DeviceImageGalleryEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Brand { get; set; } = "";
    public string MatchHint { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public bool IsPiperSvg { get; set; }
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
    bool IsExactModelMatch);

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

        foreach (var r in overrideManifest.Rules)
            rules.Add(r);

        foreach (var b in baseManifest.Rules)
        {
            if (!string.IsNullOrWhiteSpace(b.Id) && overrideIds.Contains(b.Id.Trim()))
                continue;
            rules.Add(b);
        }

        var gallery = new List<DeviceImageGalleryEntry>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in overrideManifest.Gallery.Concat(baseManifest.Gallery))
        {
            if (!string.IsNullOrWhiteSpace(g.ImageUrl) && seenUrls.Add(g.ImageUrl.Trim()))
                gallery.Add(g);
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

            // 1. 精确 VID:PID 匹配
            if (!string.IsNullOrEmpty(rVidPid))
            {
                if (!string.IsNullOrEmpty(devVidPid) && string.Equals(rVidPid, devVidPid, StringComparison.OrdinalIgnoreCase))
                {
                    int score = 1000;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestRule = rule;
                        bestExact = true;
                    }
                }
                continue;
            }

            int kwHits = CountKeywordHits(rule.Keywords, devName, devId, devProv);
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
        string? cloudUrl = !string.IsNullOrWhiteSpace(bestRule?.CloudUrl) ? bestRule.CloudUrl : null;
        string? piperSvg = !string.IsNullOrWhiteSpace(bestRule?.PiperSvg) ? bestRule.PiperSvg : null;
        return new DeviceImageMatchResult(bestRule, preset, cloudUrl, piperSvg, bestExact);
    }

    public static bool TryExtractVidPid(PeripheralDevice d, out string vidHex, out string pidHex)
    {
        vidHex = "";
        pidHex = "";
        int colon = d.Id.IndexOf(':');
        if (colon < 0) return false;
        string tail = d.Id[(colon + 1)..];
        var parts = tail.Split('-');
        if (parts.Length < 2) return false;
        if (ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) &&
            ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            vidHex = parts[0].ToLowerInvariant();
            pidHex = parts[1].ToLowerInvariant();
            return true;
        }
        return false;
    }

    private static int CountKeywordHits(List<string> keywords, string devName, string devId, string devProv)
    {
        int hits = 0;
        foreach (string raw in keywords)
        {
            string kw = raw.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(kw)) continue;
            if (devName.Contains(kw, StringComparison.Ordinal) ||
                devId.Contains(kw, StringComparison.Ordinal) ||
                devProv.Contains(kw, StringComparison.Ordinal))
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
