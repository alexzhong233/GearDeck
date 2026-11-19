using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using GearDeck.Core;
using GearDeck.Core.Models;
using GearDeck.Core.Services;

namespace GearDeck.App.Services;

public sealed record PresetImageEntry(string Id, string Name, string Category, string LocalSvgPath);

public sealed record CloudPresetImageItem(
    string Id,
    string Name,
    string Brand,
    string MatchHint,
    string ImageUrl,
    bool IsPiperSvg);

/// <summary>
/// 参照 OpenMouse-Project (@openmouse/protocol + OpenMouse Desktop) 的外置 JSON 规则 + 自动云端匹配体系：
/// 1) 优先读取外置规则清单 (%LOCALAPPDATA%\GearDeck\device-images.json + 远程清单合并，免重编译直接扩展新设备)；
/// 2) 首次接入未精确映射的新 VID:PID 设备或规则附带 cloudUrl 时，后台静默按 usb:vid:pid 从 Piper / 云端图库拉取缓存；
/// 3) 兜底使用内置 19 套俯视高精硬件矢量剪影，同时支持用户手动上传本地图片或挑选云端图样。
/// </summary>
public static class DeviceImageService
{
    public const string PiperLookupIniUrl = "https://cdn.jsdelivr.net/gh/libratbag/piper@master/data/svgs/svg-lookup.ini";
    public const string PiperSvgBaseUrl = "https://cdn.jsdelivr.net/gh/libratbag/piper@master/data/svgs/";
    private const string PresetVersionTag = "v2-openmouse-silhouettes";

    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GearDeck");

    public static string LocalManifestPath => Path.Combine(AppDataDir, "device-images.json");
    private static string BundledManifestPath => Path.Combine(AppContext.BaseDirectory, "Assets", "device-images.json");
    private static string RemoteManifestCachePath => Path.Combine(CloudCacheDir, "remote-manifest.json");

    private static readonly string PresetDir = Path.Combine(AppDataDir, "preset-gallery");
    private static readonly string UserImagesDir = Path.Combine(AppDataDir, "device-images");
    private static readonly string CloudCacheDir = Path.Combine(AppDataDir, "image-cache");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly object InitLock = new();
    private static readonly object ManifestLock = new();
    private static readonly SemaphoreSlim AutoPullLock = new(1, 1);
    private static readonly HashSet<string> CheckedAutoIds = new(StringComparer.OrdinalIgnoreCase);

    private static List<PresetImageEntry>? _presets;
    private static DeviceImageManifest? _cachedManifest;
    private static DateTime _cachedLocalWriteUtc;
    private static DateTime _cachedRemoteWriteUtc;

    public static event Action? ImagesUpdated;

    public static IReadOnlyList<CloudPresetImageItem> CuratedCloudPresets { get; } =
    [
        new("cloud_gpx", "Logitech G PRO X Superlight", "Logitech", "usb:046d:c094;g pro x;superlight;logi", $"{PiperSvgBaseUrl}logitech-g-pro-x-superlight.svg", true),
        new("cloud_gpw", "Logitech G PRO Wireless", "Logitech", "usb:046d:c088;g pro wireless;gpw", $"{PiperSvgBaseUrl}logitech-g-pro-wireless.svg", true),
        new("cloud_g502", "Logitech G502 HERO / Lightspeed", "Logitech", "usb:046d:c08b;usb:046d:c08d;g502", $"{PiperSvgBaseUrl}logitech-g502-hero.svg", true),
        new("cloud_g305", "Logitech G305 / G304", "Logitech", "usb:046d:c53f;g305;g304", $"{PiperSvgBaseUrl}logitech-g305.svg", true),
        new("cloud_viper", "Razer Viper 系列官方轮廓图", "Razer", "usb:1532:00c1;usb:1532:00a5;usb:1532:0078;viper", $"{PiperSvgBaseUrl}razer-viper.svg", true),
        new("cloud_da", "Razer DeathAdder 炼狱蝰蛇", "Razer", "usb:1532:00b6;usb:1532:0084;deathadder", $"{PiperSvgBaseUrl}razer-deathadder-v2.svg", true),
        new("cloud_basilisk", "Razer Basilisk 巴塞利斯蛇", "Razer", "usb:1532:0086;basilisk", $"{PiperSvgBaseUrl}razer-basilisk.svg", true),
        new("cloud_gladius", "ASUS ROG Gladius / 战刃", "ROG", "usb:0b05:;rog;gladius", $"{PiperSvgBaseUrl}asus-rog-gladius-iii.svg", true),
        new("cloud_aerox", "轻量化镂空对称鼠 (Aerox 造型)", "WLmouse / Symm", "usb:36a7:;usb:1038:;beast;aerox;wlmouse", $"{PiperSvgBaseUrl}steelseries-aerox-3.svg", true),
        new("cloud_apex5", "飞智八爪鱼 5 (Flydigi Apex 5)", "Flydigi", "usb:37d7:2501;apex 5;八爪鱼 5", "https://cdn.jsdelivr.net/gh/ReynArts/ApexSenseBridge@main/assets/controller-apex5.png", false),
        new("cloud_apex6", "飞智八爪鱼 6 Pro (Flydigi Apex 6 Pro)", "Flydigi", "usb:37d7:2502;apex 6;八爪鱼 6", "https://cdn.jsdelivr.net/gh/ReynArts/ApexSenseBridge@main/assets/controller-apex6-pro.png", false),
        new("cloud_apex4", "飞智八爪鱼 4 (Flydigi Apex 4)", "Flydigi", "usb:04b4:2412;apex 4;八爪鱼 4", "https://cdn.jsdelivr.net/gh/ReynArts/ApexSenseBridge@main/assets/controller-apex4.png", false),
    ];

    public static IReadOnlyList<PresetImageEntry> EnsureBuiltInPresets()
    {
        lock (InitLock)
        {
            string stampFile = Path.Combine(PresetDir, ".version");
            bool versionMatches = File.Exists(stampFile) && File.ReadAllText(stampFile).Trim() == PresetVersionTag;
            if (_presets != null && versionMatches && _presets.All(p => File.Exists(p.LocalSvgPath)))
                return _presets;

            Directory.CreateDirectory(PresetDir);
            var defs = BuildBuiltInSvgDefinitions();
            var list = new List<PresetImageEntry>(defs.Count);
            foreach (var (id, name, cat, svgContent) in defs)
            {
                string path = Path.Combine(PresetDir, $"{id}.svg");
                try { File.WriteAllText(path, svgContent, Encoding.UTF8); }
                catch { }
                list.Add(new PresetImageEntry(id, name, cat, path));
            }
            try { File.WriteAllText(stampFile, PresetVersionTag, Encoding.UTF8); }
            catch { }

            _presets = list;
            return _presets;
        }
    }

    public static DeviceImageManifest GetEffectiveManifest()
    {
        lock (ManifestLock)
        {
            EnsureLocalManifestSeeded();
            DateTime localWrite = File.Exists(LocalManifestPath) ? File.GetLastWriteTimeUtc(LocalManifestPath) : DateTime.MinValue;
            DateTime remoteWrite = File.Exists(RemoteManifestCachePath) ? File.GetLastWriteTimeUtc(RemoteManifestCachePath) : DateTime.MinValue;

            if (_cachedManifest != null && localWrite == _cachedLocalWriteUtc && remoteWrite == _cachedRemoteWriteUtc)
                return _cachedManifest;

            DeviceImageManifest bundled = File.Exists(BundledManifestPath)
                ? DeviceImageRuleMatcher.ParseManifest(TryReadAllText(BundledManifestPath))
                : new DeviceImageManifest();

            DeviceImageManifest? localUser = File.Exists(LocalManifestPath)
                ? DeviceImageRuleMatcher.ParseManifest(TryReadAllText(LocalManifestPath))
                : null;

            DeviceImageManifest? remote = File.Exists(RemoteManifestCachePath)
                ? DeviceImageRuleMatcher.ParseManifest(TryReadAllText(RemoteManifestCachePath))
                : null;

            var merged = DeviceImageRuleMatcher.MergeManifests(bundled, remote);
            merged = DeviceImageRuleMatcher.MergeManifests(merged, localUser);

            _cachedManifest = merged;
            _cachedLocalWriteUtc = localWrite;
            _cachedRemoteWriteUtc = remoteWrite;
            return _cachedManifest;
        }
    }

    public static void EnsureLocalManifestSeeded()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            if (!File.Exists(LocalManifestPath) && File.Exists(BundledManifestPath))
                File.Copy(BundledManifestPath, LocalManifestPath, overwrite: false);
        }
        catch { }
    }

    public static void InvalidateManifestCache()
    {
        lock (ManifestLock)
            _cachedManifest = null;
    }

    private static string? TryReadAllText(string path)
    {
        try { return File.ReadAllText(path, Encoding.UTF8); }
        catch { return null; }
    }

    public static string? ResolveImagePath(PeripheralDevice device)
    {
        var presets = EnsureBuiltInPresets();

        // 1. 用户手动指定的本地上传或手工选择的造型预设
        if (App.Settings.Current.DeviceImageOverrides.TryGetValue(device.Id, out string? ov) &&
            !string.IsNullOrWhiteSpace(ov))
        {
            if (ov.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
            {
                string pid = ov[7..];
                var found = presets.FirstOrDefault(p => p.Id.Equals(pid, StringComparison.OrdinalIgnoreCase));
                if (found != null && File.Exists(found.LocalSvgPath))
                    return found.LocalSvgPath;
            }
            else if (File.Exists(ov))
            {
                return ov;
            }
        }

        // 2. 后台静默自动从云端拉取缓存的设备专属图片
        if (App.Settings.Current.AutoCloudDeviceImages.TryGetValue(device.Id, out string? autoCloud) &&
            !string.IsNullOrWhiteSpace(autoCloud) && File.Exists(autoCloud))
        {
            return autoCloud;
        }

        // 3. 外置 JSON 规则匹配 -> 本地高清矢量造型图
        string autoId = MatchDefaultPresetId(device);
        return presets.FirstOrDefault(p => p.Id == autoId)?.LocalSvgPath ?? presets[0].LocalSvgPath;
    }

    public static ImageSource? LoadImageSource(PeripheralDevice device, double decodeSize = 96)
    {
        string? path = ResolveImagePath(device);
        return path != null ? CreateSourceFromFile(path, decodeSize) : null;
    }

    public static ImageSource? CreateSourceFromFile(string path, double decodeSize = 96)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var uri = new Uri(path);
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                return new SvgImageSource(uri)
                {
                    RasterizePixelWidth = decodeSize * 2,
                    RasterizePixelHeight = decodeSize * 2,
                };
            }
            return new BitmapImage(uri)
            {
                DecodePixelWidth = (int)Math.Round(decodeSize * 2),
            };
        }
        catch
        {
            return null;
        }
    }

    public static string SaveUserUploadedImage(string deviceId, string sourceFilePath)
    {
        Directory.CreateDirectory(UserImagesDir);
        string safeId = string.Concat(deviceId.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        string ext = Path.GetExtension(sourceFilePath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
        string dest = Path.Combine(UserImagesDir, $"{safeId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}");
        File.Copy(sourceFilePath, dest, overwrite: true);

        App.Settings.Current.DeviceImageOverrides[deviceId] = dest;
        App.Settings.Save();
        return dest;
    }

    public static void SetPresetOverride(string deviceId, string presetId)
    {
        App.Settings.Current.DeviceImageOverrides[deviceId] = $"preset:{presetId}";
        App.Settings.Save();
    }

    public static void ClearOverride(string deviceId)
    {
        App.Settings.Current.DeviceImageOverrides.Remove(deviceId);
        App.Settings.Save();
    }

    public static async Task<string?> DownloadAndCacheCloudImageAsync(CloudPresetImageItem item)
    {
        return await DownloadAndCacheUrlAsync(item.ImageUrl, item.IsPiperSvg);
    }

    public static async Task<string?> DownloadAndCacheUrlAsync(string url, bool stripPiperLayers = false)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            Directory.CreateDirectory(CloudCacheDir);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
            bool isSvg = url.Contains(".svg", StringComparison.OrdinalIgnoreCase);
            string ext = isSvg ? ".svg" : (url.Contains(".webp", StringComparison.OrdinalIgnoreCase) ? ".webp" : ".png");
            string cachedPath = Path.Combine(CloudCacheDir, $"{hash}{(isSvg ? "_v2" : "")}{ext}");
            if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 64)
                return cachedPath;

            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            if (isSvg || resp.Content.Headers.ContentType?.MediaType?.Contains("svg", StringComparison.OrdinalIgnoreCase) == true)
            {
                string rawSvg = await resp.Content.ReadAsStringAsync();
                string cleaned = stripPiperLayers ? SanitizePiperSvg(rawSvg) : rawSvg;
                cachedPath = Path.Combine(CloudCacheDir, $"{hash}_v2.svg");
                await File.WriteAllTextAsync(cachedPath, cleaned, Encoding.UTF8);
                return cachedPath;
            }

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 64) return null;
            await File.WriteAllBytesAsync(cachedPath, bytes);
            return cachedPath;
        }
        catch (Exception ex)
        {
            Diag.Log($"云端设备图片下载失败 ({url}): {ex.Message}");
            return null;
        }
    }

    public static async Task<int> EnsureAutoCloudImagesAsync(IReadOnlyList<PeripheralDevice> devices, bool forceRecheck = false)
    {
        if (devices.Count == 0) return 0;
        await AutoPullLock.WaitAsync();
        int updatedCount = 0;
        try
        {
            if (forceRecheck)
            {
                CheckedAutoIds.Clear();
                InvalidateManifestCache();
            }

            // 如果配置了远程 JSON 规则清单，则后台刷新缓存（支持 { "rules": [...] } 或直接图库数组）
            await RefreshRemoteManifestCacheIfNeededAsync(forceRecheck);
            var manifest = GetEffectiveManifest();

            var toFetch = devices
                .Where(d => !App.Settings.Current.DeviceImageOverrides.ContainsKey(d.Id))
                .Where(d => forceRecheck || !CheckedAutoIds.Contains(d.Id))
                .ToList();

            if (toFetch.Count == 0) return 0;

            List<CloudPresetImageItem>? cloudCatalog = null;
            bool settingsDirty = false;

            foreach (var dev in toFetch)
            {
                CheckedAutoIds.Add(dev.Id);
                if (!forceRecheck &&
                    App.Settings.Current.AutoCloudDeviceImages.TryGetValue(dev.Id, out string? cachedExisting) &&
                    File.Exists(cachedExisting))
                {
                    continue;
                }

                var match = DeviceImageRuleMatcher.MatchRule(dev, manifest.Rules);
                string? pulledPath = null;

                // 若外置规则直接配置了 cloudUrl，则静默拉取并绑定
                if (!string.IsNullOrWhiteSpace(match.CloudUrl))
                {
                    bool isSvg = match.CloudUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
                    pulledPath = await DownloadAndCacheUrlAsync(match.CloudUrl, stripPiperLayers: isSvg);
                }
                else if (!match.IsExactModelMatch)
                {
                    // 新插入设备未命中本地/JSON 精确 VID:PID 时，按 usb:vid:pid 在 Piper 与云端目录自动搜索拉取
                    cloudCatalog ??= await FetchCloudCatalogAsync();
                    if (DeviceImageRuleMatcher.TryExtractVidPid(dev, out string vidHex, out string pidHex))
                    {
                        string usbExact = $"usb:{vidHex}:{pidHex}";
                        var exactCloud = cloudCatalog.FirstOrDefault(c =>
                            c.MatchHint.Contains(usbExact, StringComparison.OrdinalIgnoreCase));
                        if (exactCloud != null)
                        {
                            pulledPath = await DownloadAndCacheCloudImageAsync(exactCloud);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(pulledPath) && File.Exists(pulledPath))
                {
                    App.Settings.Current.AutoCloudDeviceImages[dev.Id] = pulledPath;
                    settingsDirty = true;
                    updatedCount++;
                    Diag.Log($"[AutoCloudImage] 已为 {dev.Name} ({dev.Id}) 自动缓存云端图片: {Path.GetFileName(pulledPath)}");
                }
            }

            if (settingsDirty)
            {
                App.Settings.Save();
                ImagesUpdated?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"后台自动拉取设备图片异常: {ex.Message}");
        }
        finally
        {
            AutoPullLock.Release();
        }
        return updatedCount;
    }

    private static async Task RefreshRemoteManifestCacheIfNeededAsync(bool force)
    {
        string customManifest = App.Settings.Current.CustomCloudGalleryManifestUrl;
        if (string.IsNullOrWhiteSpace(customManifest)) return;

        try
        {
            Directory.CreateDirectory(CloudCacheDir);
            if (!force && File.Exists(RemoteManifestCachePath) &&
                (DateTime.UtcNow - File.GetLastWriteTimeUtc(RemoteManifestCachePath)).TotalHours < 12)
            {
                return;
            }

            string json = await Http.GetStringAsync(customManifest);
            if (string.IsNullOrWhiteSpace(json)) return;

            var parsed = DeviceImageRuleMatcher.ParseManifest(json);
            if (parsed.Rules.Count > 0 || parsed.Gallery.Count > 0)
            {
                await File.WriteAllTextAsync(RemoteManifestCachePath, json, Encoding.UTF8);
                InvalidateManifestCache();
            }
        }
        catch { }
    }

    public static async Task<List<CloudPresetImageItem>> FetchCloudCatalogAsync()
    {
        var list = new List<CloudPresetImageItem>(CuratedCloudPresets);
        var seenUrls = list.Select(x => x.ImageUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var g in GetEffectiveManifest().Gallery)
        {
            if (!string.IsNullOrWhiteSpace(g.ImageUrl) && seenUrls.Add(g.ImageUrl))
                list.Add(new CloudPresetImageItem(g.Id, g.Name, g.Brand, g.MatchHint, g.ImageUrl, g.IsPiperSvg));
        }

        try
        {
            Directory.CreateDirectory(CloudCacheDir);
            string iniCache = Path.Combine(CloudCacheDir, "piper-lookup.ini");
            string iniContent;
            if (File.Exists(iniCache) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(iniCache)).TotalHours < 24)
            {
                iniContent = await File.ReadAllTextAsync(iniCache);
            }
            else
            {
                iniContent = await Http.GetStringAsync(PiperLookupIniUrl);
                if (!string.IsNullOrWhiteSpace(iniContent))
                    await File.WriteAllTextAsync(iniCache, iniContent);
            }

            foreach (var item in ParsePiperLookupIni(iniContent))
            {
                if (seenUrls.Add(item.ImageUrl))
                    list.Add(item);
            }
        }
        catch { }

        string customManifest = App.Settings.Current.CustomCloudGalleryManifestUrl;
        if (!string.IsNullOrWhiteSpace(customManifest))
        {
            try
            {
                string json = await Http.GetStringAsync(customManifest);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        string? name = el.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                        string? url = el.TryGetProperty("url", out var uEl) ? uEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                        string brand = el.TryGetProperty("brand", out var bEl) ? (bEl.GetString() ?? "Custom") : "Custom";
                        string match = el.TryGetProperty("match", out var mEl) ? (mEl.GetString() ?? "") : "";
                        if (seenUrls.Add(url))
                            list.Add(new CloudPresetImageItem($"custom_{list.Count}", name, brand, match, url, false));
                    }
                }
            }
            catch { }
        }

        return list;
    }

    /// <summary>
    /// 参照 OpenMouse VID:PID + Model Catalog 评分逻辑，从云端预设目录中筛选与当前实体设备最匹配的项。
    /// </summary>
    public static List<CloudPresetImageItem> FindRecommendedCloudPresets(
        PeripheralDevice device,
        IReadOnlyList<CloudPresetImageItem> catalog)
    {
        TryExtractVidPid(device, out string vidHex, out string pidHex);
        string usbExact = !string.IsNullOrEmpty(vidHex) && !string.IsNullOrEmpty(pidHex)
            ? $"usb:{vidHex}:{pidHex}"
            : "";
        string usbVidPrefix = !string.IsNullOrEmpty(vidHex) ? $"usb:{vidHex}:" : "";

        string devName = device.Name.ToLowerInvariant();
        string prov = device.Provider.ToLowerInvariant();
        var nameTokens = devName
            .Split([' ', '-', '_', '(', ')', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .ToList();

        var scored = new List<(CloudPresetImageItem Item, int Score)>();
        foreach (var item in catalog)
        {
            int score = 0;
            string hint = item.MatchHint.ToLowerInvariant();
            string itemName = item.Name.ToLowerInvariant();
            string itemBrand = item.Brand.ToLowerInvariant();

            if (!string.IsNullOrEmpty(usbExact) && hint.Contains(usbExact, StringComparison.Ordinal))
                score += 100;
            else if (!string.IsNullOrEmpty(usbVidPrefix) && hint.Contains(usbVidPrefix, StringComparison.Ordinal))
                score += 40;

            if (!string.IsNullOrEmpty(prov) && (itemBrand.Contains(prov, StringComparison.Ordinal) || hint.Contains(prov, StringComparison.Ordinal)))
                score += 25;

            foreach (string tok in nameTokens)
            {
                if (itemName.Contains(tok, StringComparison.Ordinal) || hint.Contains(tok, StringComparison.Ordinal))
                    score += 18;
            }

            if (score > 0)
                scored.Add((item, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Take(8)
            .Select(x => x.Item)
            .ToList();
    }

    private static List<CloudPresetImageItem> ParsePiperLookupIni(string ini)
    {
        var items = new List<CloudPresetImageItem>();
        string currentSection = "";
        string currentMatch = "";
        foreach (string rawLine in ini.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                currentMatch = "";
            }
            else if (line.StartsWith("DeviceMatch=", StringComparison.OrdinalIgnoreCase))
            {
                currentMatch = line[12..].Trim();
            }
            else if (line.StartsWith("Svg=", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(currentSection))
            {
                string svgFile = line[4..].Trim();
                if (!svgFile.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
                string brand = currentSection.Split(' ')[0];
                items.Add(new CloudPresetImageItem(
                    $"piper_{svgFile}", currentSection, brand, currentMatch,
                    $"{PiperSvgBaseUrl}{svgFile}", IsPiperSvg: true));
            }
        }
        return items;
    }

    private static string SanitizePiperSvg(string rawSvg)
    {
        try
        {
            var doc = XDocument.Parse(rawSvg);
            var toRemove = doc.Descendants()
                .Where(el =>
                {
                    string id = el.Attribute("id")?.Value ?? "";
                    return id.Equals("Buttons", StringComparison.OrdinalIgnoreCase)
                        || id.Equals("LEDs", StringComparison.OrdinalIgnoreCase)
                        || id.EndsWith("-path", StringComparison.OrdinalIgnoreCase)
                        || id.EndsWith("-leader", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            foreach (var r in toRemove) r.Remove();

            // WinUI SvgImageSource (Direct2D) 对部分 SVG inline style CSS 解析有限，将其提升为标准 XML 属性
            foreach (var el in doc.Descendants())
            {
                var styleAttr = el.Attribute("style");
                if (styleAttr == null) continue;
                foreach (string decl in styleAttr.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int idx = decl.IndexOf(':');
                    if (idx <= 0) continue;
                    string prop = decl[..idx].Trim().ToLowerInvariant();
                    string val = decl[(idx + 1)..].Trim();
                    if (prop is "fill" or "stroke" or "stroke-width" or "fill-opacity" or "stroke-opacity" or "opacity")
                    {
                        if (el.Attribute(prop) == null && !string.IsNullOrEmpty(val))
                            el.SetAttributeValue(prop, val);
                    }
                }
            }

            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            return rawSvg;
        }
    }

    /// <summary>
    /// 通过外置 JSON 规则目录匹配默认矢量造型 ID（不再硬编码在 C# switch 中，随时编辑 device-images.json 即可扩展）。
    /// </summary>
    internal static string MatchDefaultPresetId(PeripheralDevice d)
    {
        var manifest = GetEffectiveManifest();
        return DeviceImageRuleMatcher.MatchRule(d, manifest.Rules).PresetId;
    }

    private static bool TryExtractVidPid(PeripheralDevice d, out string vidHex, out string pidHex)
        => DeviceImageRuleMatcher.TryExtractVidPid(d, out vidHex, out pidHex);

    private static List<(string Id, string Name, string Category, string Svg)> BuildBuiltInSvgDefinitions() =>
    [
        ("am_infinity_97", "Angry Miao AM Infinity .97 (镁合金镂空热插拔)", "鼠标", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <defs>
                <linearGradient id="amBody" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#475569"/><stop offset="50%" stop-color="#1E293B"/><stop offset="100%" stop-color="#0B0F19"/></linearGradient>
                <linearGradient id="amNeon" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#22D3EE"/><stop offset="100%" stop-color="#A855F7"/></linearGradient>
              </defs>
              <path d="M40 12 L88 12 L94 42 L91 96 L74 114 L54 114 L37 96 L34 42 Z" fill="url(#amBody)" stroke="#94A3B8" stroke-width="2"/>
              <path d="M40 14 L61 14 L61 48 L36 46 Z" fill="#273244" stroke="#64748B" stroke-width="1"/>
              <path d="M67 14 L88 14 L92 46 L67 48 Z" fill="#273244" stroke="#64748B" stroke-width="1"/>
              <rect x="60" y="21" width="8" height="20" rx="3" fill="#0F172A" stroke="#22D3EE" stroke-width="1.5"/>
              <rect x="62.5" y="24" width="3" height="14" rx="1.5" fill="url(#amNeon)"/>
              <polygon points="44,54 84,54 78,94 64,104 50,94" fill="#090D16" stroke="url(#amNeon)" stroke-width="2"/>
              <polygon points="52,60 76,60 71,89 64,95 57,89" fill="#162033" stroke="#38BDF8" stroke-width="1.2"/>
              <circle cx="64" cy="75" r="6" fill="url(#amNeon)"/>
              <rect x="31" y="54" width="3.5" height="14" rx="1.5" fill="#38BDF8"/>
              <rect x="31" y="71" width="3.5" height="14" rx="1.5" fill="#A855F7"/>
            </svg>
            """),
        ("wlmouse_beast_x_v2", "WLmouse Beast X V2 8K (镁合金银灰镂空)", "鼠标", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <defs>
                <linearGradient id="bxShell" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#E2E8F0"/><stop offset="55%" stop-color="#64748B"/><stop offset="100%" stop-color="#1E293B"/></linearGradient>
                <linearGradient id="bxRed" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#F97316"/><stop offset="100%" stop-color="#EF4444"/></linearGradient>
              </defs>
              <path d="M36 22 C36 13, 50 10, 64 10 C78 10, 92 13, 92 22 L90 52 C94 74, 90 108, 64 116 C38 108, 34 74, 38 52 Z" fill="url(#bxShell)" stroke="#CBD5E1" stroke-width="2"/>
              <rect x="40" y="14" width="21" height="33" rx="6" fill="#334155" opacity="0.8"/>
              <rect x="67" y="14" width="21" height="33" rx="6" fill="#334155" opacity="0.8"/>
              <rect x="60.5" y="20" width="7" height="18" rx="3.5" fill="url(#bxRed)"/>
              <polygon points="44,54 57,52 54,84 41,78" fill="#0F172A"/>
              <polygon points="84,54 71,52 74,84 87,78" fill="#0F172A"/>
              <polygon points="56,88 64,62 72,88 64,104" fill="#0F172A" stroke="url(#bxRed)" stroke-width="1.5"/>
              <polygon points="46,85 53,89 58,105 46,97" fill="#0F172A"/>
              <polygon points="82,85 75,89 70,105 82,97" fill="#0F172A"/>
              <rect x="33.5" y="54" width="3" height="13" rx="1.5" fill="url(#bxRed)"/>
              <rect x="33.5" y="70" width="3" height="13" rx="1.5" fill="url(#bxRed)"/>
            </svg>
            """),
        ("wlmouse_beast_g", "WLmouse Beast G 8K (香槟金黑镁合金)", "鼠标", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <defs>
                <linearGradient id="bgGold" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#FDE68A"/><stop offset="50%" stop-color="#D97706"/><stop offset="100%" stop-color="#78350F"/></linearGradient>
              </defs>
              <path d="M35 24 C35 14, 50 11, 64 11 C78 11, 93 14, 93 24 L91 54 C94 76, 89 108, 64 116 C39 108, 34 76, 37 54 Z" fill="#18181B" stroke="url(#bgGold)" stroke-width="2.5"/>
              <path d="M39 17 L61 17 L61 48 L39 48 Z" fill="#27272A"/>
              <path d="M67 17 L89 17 L89 48 L67 48 Z" fill="#27272A"/>
              <rect x="60.5" y="21" width="7" height="18" rx="3.5" fill="url(#bgGold)"/>
              <polygon points="46,56 59,56 56,74 43,70" fill="url(#bgGold)" opacity="0.88"/>
              <polygon points="82,56 69,56 72,74 85,70" fill="url(#bgGold)" opacity="0.88"/>
              <polygon points="45,78 60,82 57,101 44,94" fill="url(#bgGold)" opacity="0.72"/>
              <polygon points="83,78 68,82 71,101 84,94" fill="url(#bgGold)" opacity="0.72"/>
              <circle cx="64" cy="92" r="5" fill="url(#bgGold)"/>
              <rect x="32.5" y="54" width="3" height="13" rx="1.5" fill="url(#bgGold)"/>
              <rect x="32.5" y="70" width="3" height="13" rx="1.5" fill="url(#bgGold)"/>
            </svg>
            """),
        ("razer_viper_v3_pro", "Razer Viper V3 Pro (哑光黑竞技对称鼠)", "鼠标", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <defs>
                <linearGradient id="vpBody" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#27272A"/><stop offset="100%" stop-color="#09090B"/></linearGradient>
                <linearGradient id="vpGlow" x1="0" y1="0" x2="1" y2="0"><stop offset="0%" stop-color="#22C55E"/><stop offset="100%" stop-color="#10B981"/></linearGradient>
              </defs>
              <path d="M35 20 C35 12, 50 11, 64 11 C78 11, 93 12, 93 20 L89 56 C92 78, 88 109, 64 116 C40 109, 36 78, 39 56 Z" fill="url(#vpBody)" stroke="#52525B" stroke-width="2"/>
              <path d="M37 49 C37 22, 48 13, 61.5 13 L61.5 49 Z" fill="#323238"/>
              <path d="M91 49 C91 22, 80 13, 66.5 13 L66.5 49 Z" fill="#323238"/>
              <line x1="64" y1="13" x2="64" y2="51" stroke="#09090B" stroke-width="2.5"/>
              <rect x="60.5" y="21" width="7" height="19" rx="3.5" fill="#18181B" stroke="#71717A" stroke-width="1.2"/>
              <rect x="62.5" y="24" width="3" height="13" rx="1.5" fill="#52525B"/>
              <circle cx="64" cy="92" r="7" fill="none" stroke="url(#vpGlow)" stroke-width="2"/>
              <circle cx="64" cy="92" r="2.5" fill="url(#vpGlow)"/>
              <rect x="34" y="55" width="3" height="13" rx="1.5" fill="#71717A"/>
              <rect x="34" y="71" width="3" height="13" rx="1.5" fill="#71717A"/>
            </svg>
            """),
        ("aula_sc_symm", "AULA SC 无线鼠标 (冰川白对称轻量化)", "鼠标", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <defs>
                <linearGradient id="auWhite" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#FFFFFF"/><stop offset="100%" stop-color="#CBD5E1"/></linearGradient>
                <linearGradient id="auIce" x1="0" y1="0" x2="1" y2="0"><stop offset="0%" stop-color="#38BDF8"/><stop offset="100%" stop-color="#2563EB"/></linearGradient>
              </defs>
              <path d="M36 22 C36 13, 50 11, 64 11 C78 11, 92 13, 92 22 L90 54 C92 78, 88 109, 64 115 C40 109, 36 78, 38 54 Z" fill="url(#auWhite)" stroke="#94A3B8" stroke-width="2"/>
              <path d="M38 49 Q64 46 90 49" fill="none" stroke="#94A3B8" stroke-width="1.6"/>
              <line x1="64" y1="13" x2="64" y2="48" stroke="#94A3B8" stroke-width="1.6"/>
              <rect x="60.5" y="21" width="7" height="18" rx="3.5" fill="#1E293B"/>
              <rect x="62.5" y="24" width="3" height="12" rx="1.5" fill="url(#auIce)"/>
              <rect x="34" y="54" width="3.5" height="13" rx="1.5" fill="url(#auIce)"/>
              <rect x="34" y="70" width="3.5" height="13" rx="1.5" fill="#64748B"/>
              <path d="M55 96 L73 96" stroke="url(#auIce)" stroke-width="3" stroke-linecap="round"/>
            </svg>
            """),
        ("rog_delta_ii", "ROG Delta II (标志性 D 形耳罩双模耳机)", "耳机", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <defs>
                <linearGradient id="rogBand" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#3F3F46"/><stop offset="100%" stop-color="#18181B"/></linearGradient>
                <linearGradient id="rogSlash" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#EF4444"/><stop offset="50%" stop-color="#EC4899"/><stop offset="100%" stop-color="#8B5CF6"/></linearGradient>
              </defs>
              <path d="M26 58 C26 22, 102 22, 102 58" fill="none" stroke="url(#rogBand)" stroke-width="11" stroke-linecap="round"/>
              <path d="M29 58 C29 28, 99 28, 99 58" fill="none" stroke="#71717A" stroke-width="2"/>
              <path d="M18 54 L36 52 C40 52, 40 98, 34 98 L20 94 C13 91, 13 56, 18 54 Z" fill="#18181B" stroke="#71717A" stroke-width="2"/>
              <path d="M110 54 L92 52 C88 52, 88 98, 94 98 L108 94 C115 91, 115 56, 110 54 Z" fill="#18181B" stroke="#71717A" stroke-width="2"/>
              <polygon points="21,62 30,62 24,89 19,86" fill="url(#rogSlash)"/>
              <polygon points="107,62 98,62 104,89 109,86" fill="url(#rogSlash)"/>
              <path d="M24 94 Q38 112 60 107" fill="none" stroke="#71717A" stroke-width="3.5" stroke-linecap="round"/>
              <circle cx="62" cy="107" r="4" fill="#EF4444"/>
            </svg>
            """),
        ("sony_inzone_buds", "Sony INZONE Buds (充电盒与环形双耳降噪豆)", "耳机", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <defs>
                <linearGradient id="snCase" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#F8FAFC"/><stop offset="100%" stop-color="#CBD5E1"/></linearGradient>
              </defs>
              <rect x="20" y="58" width="88" height="54" rx="22" fill="url(#snCase)" stroke="#64748B" stroke-width="2"/>
              <path d="M21 76 L107 76" stroke="#94A3B8" stroke-width="2"/>
              <rect x="54" y="83" width="20" height="3.5" rx="1.7" fill="#38BDF8"/>
              <circle cx="44" cy="37" r="17" fill="#1E293B" stroke="#94A3B8" stroke-width="2"/>
              <circle cx="44" cy="36" r="10" fill="#F8FAFC"/>
              <circle cx="44" cy="36" r="4.5" fill="#1E293B"/>
              <circle cx="84" cy="37" r="17" fill="#1E293B" stroke="#94A3B8" stroke-width="2"/>
              <circle cx="84" cy="36" r="10" fill="#F8FAFC"/>
              <circle cx="84" cy="36" r="4.5" fill="#1E293B"/>
            </svg>
            """),
        ("flydigi_cd2_dock", "飞智磁吸充电底座 CD2", "手柄", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <defs>
                <linearGradient id="cdBody" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#334155"/><stop offset="100%" stop-color="#0F172A"/></linearGradient>
                <linearGradient id="cdLed" x1="0" y1="0" x2="1" y2="0"><stop offset="0%" stop-color="#22D3EE"/><stop offset="100%" stop-color="#3B82F6"/></linearGradient>
              </defs>
              <polygon points="46,24 82,24 94,86 34,86" fill="url(#cdBody)" stroke="#64748B" stroke-width="2"/>
              <polygon points="22,94 34,82 94,82 106,94 102,106 26,106" fill="#1E293B" stroke="#64748B" stroke-width="2"/>
              <rect x="30" y="96" width="68" height="4.5" rx="2.2" fill="url(#cdLed)"/>
              <rect x="52" y="34" width="24" height="6" rx="2" fill="#0F172A"/>
              <circle cx="57" cy="48" r="2.5" fill="#FBBF24"/>
              <circle cx="64" cy="48" r="2.5" fill="#FBBF24"/>
              <circle cx="71" cy="48" r="2.5" fill="#FBBF24"/>
            </svg>
            """),
        ("flydigi_apex5", "飞智八爪鱼 5 / 黑武士 5 Pro (机械白彩屏手柄)", "手柄", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <defs>
                <linearGradient id="a5Body" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#F8FAFC"/><stop offset="100%" stop-color="#CBD5E1"/></linearGradient>
                <linearGradient id="a5Scr" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#38BDF8"/><stop offset="100%" stop-color="#6366F1"/></linearGradient>
              </defs>
              <path d="M28 34 C16 34, 10 66, 12 92 C14 106, 28 110, 36 96 L46 80 L82 80 L92 96 C100 110, 114 106, 116 92 C118 66, 112 34, 100 34 Z" fill="url(#a5Body)" stroke="#64748B" stroke-width="2"/>
              <rect x="50" y="37" width="28" height="18" rx="4" fill="#0F172A" stroke="#38BDF8" stroke-width="1.5"/>
              <rect x="53" y="40" width="22" height="12" rx="2" fill="url(#a5Scr)"/>
              <circle cx="34" cy="54" r="9" fill="#1E293B" stroke="#38BDF8" stroke-width="1.5"/>
              <circle cx="78" cy="70" r="8" fill="#1E293B" stroke="#38BDF8" stroke-width="1.5"/>
              <path d="M44 65 L54 65 M49 60 L49 70" stroke="#1E293B" stroke-width="4" stroke-linecap="round"/>
              <circle cx="94" cy="47" r="3" fill="#22C55E"/><circle cx="101" cy="53" r="3" fill="#EF4444"/>
              <circle cx="87" cy="53" r="3" fill="#3B82F6"/><circle cx="94" cy="59" r="3" fill="#F59E0B"/>
            </svg>
            """),
        ("flydigi_apex6_pro", "飞智八爪鱼 6 Pro (黑金旗舰智能屏手柄)", "手柄", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <defs>
                <linearGradient id="a6Body" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#334155"/><stop offset="100%" stop-color="#090D16"/></linearGradient>
                <linearGradient id="a6Gold" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#FDE68A"/><stop offset="100%" stop-color="#D97706"/></linearGradient>
              </defs>
              <path d="M28 34 C16 34, 10 66, 12 92 C14 106, 28 110, 36 96 L46 80 L82 80 L92 96 C100 110, 114 106, 116 92 C118 66, 112 34, 100 34 Z" fill="url(#a6Body)" stroke="url(#a6Gold)" stroke-width="2.2"/>
              <rect x="49" y="37" width="30" height="19" rx="4" fill="#0F172A" stroke="url(#a6Gold)" stroke-width="1.8"/>
              <rect x="53" y="41" width="22" height="11" rx="2" fill="url(#a6Gold)" opacity="0.85"/>
              <circle cx="34" cy="54" r="9" fill="#090D16" stroke="url(#a6Gold)" stroke-width="1.8"/>
              <circle cx="78" cy="70" r="8" fill="#090D16" stroke="url(#a6Gold)" stroke-width="1.8"/>
              <path d="M44 65 L54 65 M49 60 L49 70" stroke="#E2E8F0" stroke-width="4" stroke-linecap="round"/>
              <circle cx="94" cy="47" r="3" fill="#22C55E"/><circle cx="101" cy="53" r="3" fill="#EF4444"/>
              <circle cx="87" cy="53" r="3" fill="#3B82F6"/><circle cx="94" cy="59" r="3" fill="#F59E0B"/>
            </svg>
            """),
        ("rog_falchion_75he", "ROG 魔导士 Ace 75 HE 紧凑磁轴键盘", "键盘", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <defs>
                <linearGradient id="fKb" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#27272A"/><stop offset="100%" stop-color="#09090B"/></linearGradient>
                <linearGradient id="fRgb" x1="0" y1="0" x2="1" y2="0"><stop offset="0%" stop-color="#EF4444"/><stop offset="50%" stop-color="#A855F7"/><stop offset="100%" stop-color="#38BDF8"/></linearGradient>
              </defs>
              <rect x="10" y="30" width="108" height="68" rx="9" fill="url(#fKb)" stroke="#71717A" stroke-width="2"/>
              <rect x="14" y="34" width="100" height="4" rx="2" fill="url(#fRgb)"/>
              <rect x="16" y="42" width="96" height="8" rx="2" fill="#3F3F46"/>
              <rect x="16" y="53" width="96" height="9" rx="2" fill="#52525B"/>
              <rect x="16" y="65" width="96" height="9" rx="2" fill="#52525B"/>
              <rect x="16" y="77" width="96" height="9" rx="2" fill="#3F3F46"/>
              <rect x="34" y="88" width="46" height="6" rx="2" fill="#A1A1AA"/>
              <rect x="86" y="88" width="26" height="6" rx="2" fill="#EF4444"/>
            </svg>
            """),
        ("kb_tkl", "80% 客制化无线机械键盘", "键盘", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <rect x="8" y="32" width="112" height="64" rx="8" fill="#E2E8F0" stroke="#64748B" stroke-width="2"/>
              <rect x="14" y="38" width="100" height="52" rx="4" fill="#1E293B"/>
              <rect x="18" y="42" width="66" height="8" rx="1.5" fill="#94A3B8"/>
              <rect x="88" y="42" width="22" height="8" rx="1.5" fill="#38BDF8"/>
              <rect x="18" y="53" width="66" height="22" rx="2" fill="#475569"/>
              <rect x="88" y="53" width="22" height="22" rx="2" fill="#334155"/>
              <rect x="32" y="79" width="38" height="7" rx="2" fill="#CBD5E1"/>
              <rect x="92" y="79" width="14" height="7" rx="2" fill="#F97316"/>
            </svg>
            """),
        ("razer_deathadder", "Razer DeathAdder / 炼狱蝰蛇 (右手工学)", "鼠标", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <path d="M35 42 C35 18, 52 11, 68 11 C88 11, 95 27, 94 55 C94 89, 85 116, 62 116 C40 116, 33 94, 34 68 Z" fill="#18181B" stroke="#71717A" stroke-width="2"/>
              <path d="M36 48 Q65 44 94 50" fill="none" stroke="#52525B" stroke-width="2"/>
              <line x1="65" y1="12" x2="65" y2="48" stroke="#09090B" stroke-width="2.5"/>
              <rect x="61" y="22" width="8" height="19" rx="4" fill="#27272A" stroke="#22C55E" stroke-width="1.2"/>
              <rect x="32" y="54" width="3.5" height="14" rx="1.5" fill="#22C55E"/>
              <rect x="31" y="71" width="3.5" height="14" rx="1.5" fill="#71717A"/>
            </svg>
            """),
        ("lamzu_maya_symm", "Lamzu Maya / Atlantis (轻量化对称)", "鼠标", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <rect x="35" y="12" width="58" height="104" rx="28" fill="#1E293B" stroke="#38BDF8" stroke-width="2"/>
              <line x1="37" y1="49" x2="91" y2="49" stroke="#334155" stroke-width="2"/>
              <line x1="64" y1="13" x2="64" y2="49" stroke="#0F172A" stroke-width="2"/>
              <rect x="60.5" y="22" width="7" height="18" rx="3.5" fill="#38BDF8"/>
              <rect x="32.5" y="54" width="3" height="13" rx="1.5" fill="#94A3B8"/>
              <rect x="32.5" y="70" width="3" height="13" rx="1.5" fill="#94A3B8"/>
            </svg>
            """),
        ("lamzu_inca_ergo", "Lamzu Inca / Thorn (高背工学无线)", "鼠标", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <path d="M36 42 C36 18, 52 12, 68 12 C88 12, 95 28, 95 54 C95 88, 86 116, 62 116 C40 116, 33 94, 34 68 Z" fill="#F8FAFC" stroke="#64748B" stroke-width="2"/>
              <path d="M36 48 Q66 44 95 50" fill="none" stroke="#94A3B8" stroke-width="2"/>
              <line x1="65" y1="13" x2="65" y2="48" stroke="#94A3B8" stroke-width="2"/>
              <rect x="61" y="23" width="8" height="19" rx="4" fill="#1E293B"/>
              <rect x="32" y="54" width="4" height="14" rx="2" fill="#0EA5E9"/>
              <rect x="31" y="71" width="4" height="14" rx="2" fill="#64748B"/>
            </svg>
            """),
        ("atk_blazing_sky", "ATK F1 / X1 / VXE R1 烈空竞技鼠", "鼠标", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <rect x="34" y="12" width="60" height="104" rx="29" fill="#18181B" stroke="#F97316" stroke-width="2"/>
              <line x1="36" y1="49" x2="92" y2="49" stroke="#3F3F46" stroke-width="2"/>
              <line x1="64" y1="13" x2="64" y2="49" stroke="#09090B" stroke-width="2"/>
              <rect x="60.5" y="22" width="7" height="18" rx="3.5" fill="#F97316"/>
              <rect x="31.5" y="54" width="3" height="13" rx="1.5" fill="#F97316"/>
              <rect x="31.5" y="70" width="3" height="13" rx="1.5" fill="#A1A1AA"/>
            </svg>
            """),
        ("mchose_wireless", "MCHOSE 迈从 A5 / A7 / L7 无线鼠", "鼠标", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <rect x="34" y="12" width="60" height="104" rx="29" fill="#E2E8F0" stroke="#64748B" stroke-width="2"/>
              <line x1="36" y1="49" x2="92" y2="49" stroke="#94A3B8" stroke-width="2"/>
              <line x1="64" y1="13" x2="64" y2="49" stroke="#94A3B8" stroke-width="2"/>
              <rect x="60.5" y="22" width="7" height="18" rx="3.5" fill="#4F46E5"/>
              <rect x="31.5" y="54" width="3" height="13" rx="1.5" fill="#4F46E5"/>
              <rect x="31.5" y="70" width="3" height="13" rx="1.5" fill="#64748B"/>
            </svg>
            """),
        ("logitech_gpx", "Logitech G PRO X Superlight (狗屁王造型)", "鼠标", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <ellipse cx="64" cy="64" rx="29" ry="52" fill="#F8FAFC" stroke="#64748B" stroke-width="2"/>
              <line x1="36" y1="48" x2="92" y2="48" stroke="#94A3B8" stroke-width="2"/>
              <line x1="64" y1="13" x2="64" y2="48" stroke="#94A3B8" stroke-width="2"/>
              <rect x="60.5" y="21" width="7" height="18" rx="3.5" fill="#1E293B"/>
              <circle cx="64" cy="88" r="7" fill="none" stroke="#0EA5E9" stroke-width="2.5"/>
              <rect x="33" y="54" width="3" height="13" rx="1.5" fill="#334155"/>
              <rect x="33" y="70" width="3" height="13" rx="1.5" fill="#334155"/>
            </svg>
            """),
        ("headset_pro", "头戴式无线电竞耳机 (通用圆罩)", "耳机", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <path d="M26 68 C26 30, 102 30, 102 68" fill="none" stroke="#475569" stroke-width="11" stroke-linecap="round"/>
              <rect x="16" y="58" width="20" height="42" rx="10" fill="#0F172A" stroke="#64748B" stroke-width="2"/>
              <rect x="92" y="58" width="20" height="42" rx="10" fill="#0F172A" stroke="#64748B" stroke-width="2"/>
              <path d="M26 96 Q38 112 58 106" fill="none" stroke="#64748B" stroke-width="4" stroke-linecap="round"/>
              <circle cx="60" cy="105" r="4" fill="#38BDF8"/>
            </svg>
            """),
        ("dongle_8k", "8K 高速无线接收器 / 通用外设", "通用", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
              <rect x="34" y="24" width="60" height="82" rx="14" fill="#1E293B" stroke="#64748B" stroke-width="2"/>
              <rect x="44" y="16" width="40" height="12" rx="3" fill="#94A3B8"/>
              <circle cx="64" cy="60" r="10" fill="#06B6D4"/>
              <rect x="52" y="84" width="24" height="5" rx="2.5" fill="#38BDF8"/>
            </svg>
            """),
    ];
}
