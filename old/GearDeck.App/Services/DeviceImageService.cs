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
/// 全线上产品图自动匹配与本地缓存服务（移除全部手绘 SVG 剪影）：
/// 1) 读取并自动升级外置规则清单 (%LOCALAPPDATA%\GearDeck\device-images.json, version >= 3)；
/// 2) 按精确 VID:PID -> 厂商+关键词 -> 品牌家族/品类 匹配官方 CDN 产品图 / libratbag/piper 轮廓图并缓存到本地；
/// 3) 首次启用 v3 自动清理旧版手绘 preset-gallery/*.svg，支持用户手动上传本地图片或从线上目录替换。
/// </summary>
public static class DeviceImageService
{
    public const string PiperLookupIniUrl = "https://cdn.jsdelivr.net/gh/libratbag/piper@master/data/svgs/svg-lookup.ini";
    public const string PiperSvgBaseUrl = "https://cdn.jsdelivr.net/gh/libratbag/piper@master/data/svgs/";
    private const string PresetVersionTag = "v3-online-images-only";

    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GearDeck");

    public static string LocalManifestPath => Path.Combine(AppDataDir, "device-images.json");
    private static string BundledManifestPath => Path.Combine(AppContext.BaseDirectory, "Assets", "device-images.json");
    private static string RemoteManifestCachePath => Path.Combine(CloudCacheDir, "remote-manifest.json");

    private static readonly string PresetDir = Path.Combine(AppDataDir, "preset-gallery");
    private static readonly string UserImagesDir = Path.Combine(AppDataDir, "device-images");
    private static readonly string CloudCacheDir = Path.Combine(AppDataDir, "image-cache");

    private static readonly HttpClient Http = CreateImageHttpClient();

    private static HttpClient CreateImageHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 GearDeck/1.0");
        return http;
    }
    private static readonly object InitLock = new();
    private static readonly object ManifestLock = new();
    private static readonly SemaphoreSlim AutoPullLock = new(1, 1);
    private static readonly HashSet<string> CheckedAutoIds = new(StringComparer.OrdinalIgnoreCase);

    private static bool _onlineV3Cleaned;
    private static DeviceImageManifest? _cachedManifest;
    private static DateTime _cachedLocalWriteUtc;
    private static DateTime _cachedRemoteWriteUtc;

    public static event Action? ImagesUpdated;

    public static IReadOnlyList<CloudPresetImageItem> CuratedCloudPresets { get; } =
    [
        new("cloud_gpx3", "Logitech G PRO X / Superlight Series", "Logitech", "usb:046d:c54f;usb:046d:c094;pro x3;superstrike;superlight", "https://resource.logitech.com/w_692,c_lpad,ar_4:3,q_auto,f_auto,dpr_1.0/d_transparent.gif/content/dam/gaming/en/products/pro-x-superlight-2/gallery-1-pro-x-superlight-2-gaming-mouse-black.png", false),
        new("cloud_gpw_svg", "Logitech G PRO Wireless (Piper)", "Logitech", "usb:046d:c088;g pro wireless;gpw", $"{PiperSvgBaseUrl}logitech-g-pro-wireless.svg", true),
        new("cloud_g502x", "Logitech G502 X Plus (Piper)", "Logitech", "usb:046d:c08b;usb:046d:c08d;g502", $"{PiperSvgBaseUrl}logitech-g502-x-plus.svg", true),
        new("cloud_viper_v3", "Razer Viper V3 Pro 毒蝰 V3 Pro", "Razer", "usb:1532:00c1;viper v3 pro;viper", "https://assets2.razerzone.com/images/pnx.assets/05b2ecb314fa052d217b254d5a3c86c9/viper-v3-pro-black-500x500.webp", false),
        new("cloud_am_97", "Angry Miao AM Infinity .97 怒喵无限鼠标", "Angry Miao", "usb:0e8d:0703;usb:0e8d:0880;usb:35a1:0035;infinity;怒喵", "https://ammaster.angrymiao.com/mouse/assets/device_am35_a-3df1ebac.png", false),
        new("cloud_am_100r2", "Angry Miao AM INFINITY .100R2", "Angry Miao", "usb:0e8d:0704;usb:0e8d:0881;100r2", "https://ammaster.angrymiao.com/mouse/am30r2-mouse.png", false),
        new("cloud_am_30", "Angry Miao AM Infinity Mouse", "Angry Miao", "usb:3151:5007;usb:3151:402a;usb:3151:3010", "https://ammaster.angrymiao.com/mouse/am30-mouse.png", false),
        new("cloud_aula_l5", "AULA 狼蛛昆仑 L5 Ultra", "AULA", "usb:38a6:2f1a;usb:38a6:2f0c;usb:258a:0138;狼蛛;昆仑", "https://cdn2.aulacn.com/storage/20261015/UGa1792055906WIAjQC.jpg", false),
        new("cloud_wlmouse", "WLmouse 镁合金系列", "WLmouse", "usb:36a7:a856;usb:36a7:a860;beast;wlmouse;万灵", "https://www.wlmouse.com/cdn/shop/files/9ed5786037e96b6a3eb76ee579be7e4c.png", false),
        new("cloud_lamzu", "Lamzu Maya / Inca 系列", "Lamzu", "usb:373e:;usb:37b0:;lamzu;maya;inca;兰族", "https://lamzu.com/cdn/shop/files/3_44a7403b-e6a7-419c-9910-a7679e7315fd.png", false),
        new("cloud_rog_harpe", "ASUS ROG Harpe Ace / 鼠标 (Piper)", "ROG", "usb:0b05:1ace;usb:0b05:;rog;harpe;keris", $"{PiperSvgBaseUrl}asus-rog-harpe-wireless.svg", true),
        new("cloud_rog_scope_98", "ROG 游侠 2 RX 98 无线键盘", "ROG", "usb:0b05:1b7a;游侠;scope", "https://press.asus.com/assets/w_640,h_640/172952e2-c635-4e01-b4a0-5634dd55142b/ROG%20Strix%20Scope%20II%2096%20Wireless%2001.png", false),
        new("cloud_rog_falchion", "ROG 魔导士 Ace HFX 75", "ROG", "usb:0b05:1c7d;魔导士;falchion", "https://dlcdnwebimgs.asus.com/gain/21597AB2-8BDD-4E12-83EC-45C6B2DC9B03/w717/h525/fwebp", false),
        new("cloud_rog_delta2", "ROG Delta II 棱镜 2 无线耳机", "ROG", "usb:0b05:1afa;delta", "https://press.asus.com/assets/w_640,h_640/b15882bc-d8fe-4638-98ea-01c66296de9b/A701_2400x2400_1.png", false),
        new("cloud_sony_buds", "Sony INZONE Buds WF-G700N", "Sony", "usb:054c:0ec2;inzone;sony;wf-g700n", "https://helpguide.sony.net/mdr/2977/v1/en/contents/image/i0010_wfg700n.png", false),
        new("cloud_apex5", "飞智八爪鱼 5 (Flydigi Apex 5)", "Flydigi", "usb:37d7:2501;usb:37d7:6001;apex 5;八爪鱼 5;cd2", "https://cdn.jsdelivr.net/gh/ReynArts/ApexSenseBridge@main/assets/controller-apex5.png", false),
        new("cloud_apex6", "飞智八爪鱼 6 Pro (Flydigi Apex 6 Pro)", "Flydigi", "usb:37d7:2502;apex 6;八爪鱼 6", "https://cdn.jsdelivr.net/gh/ReynArts/ApexSenseBridge@main/assets/controller-apex6-pro.png", false),
        new("cloud_apex4", "飞智八爪鱼 4 (Flydigi Apex 4)", "Flydigi", "usb:04b4:2412;apex 4;八爪鱼 4", "https://cdn.jsdelivr.net/gh/ReynArts/ApexSenseBridge@main/assets/controller-apex4.png", false),
    ];

    public static IReadOnlyList<PresetImageEntry> EnsureBuiltInPresets()
    {
        lock (InitLock)
        {
            if (_onlineV3Cleaned)
                return [];

            _onlineV3Cleaned = true;
            try
            {
                string stampFile = Path.Combine(PresetDir, ".version");
                bool alreadyV3 = File.Exists(stampFile) && File.ReadAllText(stampFile).Trim() == PresetVersionTag;
                if (!alreadyV3)
                {
                    if (Directory.Exists(PresetDir))
                    {
                        foreach (string svg in Directory.EnumerateFiles(PresetDir, "*.svg"))
                        {
                            try { File.Delete(svg); }
                            catch { }
                        }
                    }

                    bool dirty = false;
                    var ovKeys = App.Settings.Current.DeviceImageOverrides
                        .Where(kv => kv.Value.StartsWith("preset:", StringComparison.OrdinalIgnoreCase) ||
                                     kv.Value.Contains("preset-gallery", StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (string k in ovKeys)
                    {
                        App.Settings.Current.DeviceImageOverrides.Remove(k);
                        dirty = true;
                    }

                    var staleAutoKeys = App.Settings.Current.AutoCloudDeviceImages
                        .Where(kv => !File.Exists(kv.Value) || kv.Value.Contains("preset-gallery", StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (string k in staleAutoKeys)
                    {
                        App.Settings.Current.AutoCloudDeviceImages.Remove(k);
                        dirty = true;
                    }

                    if (dirty) App.Settings.Save();
                    Directory.CreateDirectory(PresetDir);
                    File.WriteAllText(stampFile, PresetVersionTag, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Diag.Log($"清理旧版手绘 SVG 预设失败: {ex.Message}");
            }

            return [];
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
            if (!File.Exists(BundledManifestPath)) return;

            if (!File.Exists(LocalManifestPath))
            {
                File.Copy(BundledManifestPath, LocalManifestPath, overwrite: false);
                return;
            }

            var bundled = DeviceImageRuleMatcher.ParseManifest(TryReadAllText(BundledManifestPath));
            var local = DeviceImageRuleMatcher.ParseManifest(TryReadAllText(LocalManifestPath));
            if (bundled.Version > local.Version)
            {
                var bundledIds = bundled.Rules
                    .Select(r => r.Id)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var customUserRules = local.Rules
                    .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !bundledIds.Contains(r.Id))
                    .ToList();

                var upgraded = new DeviceImageManifest
                {
                    Version = bundled.Version,
                    Rules = [.. customUserRules, .. bundled.Rules],
                    Gallery = bundled.Gallery.Count > 0 ? bundled.Gallery : local.Gallery,
                };
                File.WriteAllText(LocalManifestPath, DeviceImageRuleMatcher.SerializeManifest(upgraded), Encoding.UTF8);
                CheckedAutoIds.Clear();

                string oldAmGroupHash = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes("https://cdn-www.angrymiao.com/products/products_am_infinity_mouse_97.png")))[..16];
                var staleAutoKeys = App.Settings.Current.AutoCloudDeviceImages
                    .Where(kv => Path.GetFileName(kv.Value).StartsWith(oldAmGroupHash, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key)
                    .ToList();
                if (staleAutoKeys.Count > 0)
                {
                    foreach (string k in staleAutoKeys)
                        App.Settings.Current.AutoCloudDeviceImages.Remove(k);
                    App.Settings.Save();
                }

                Diag.Log($"[DeviceImages] 已将本地图像规则清单自动升级至 v{bundled.Version}");
            }
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
        EnsureBuiltInPresets();

        if (App.Settings.Current.DeviceImageOverrides.TryGetValue(device.Id, out string? ov) &&
            !string.IsNullOrWhiteSpace(ov) &&
            !ov.StartsWith("preset:", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(ov))
        {
            return ov;
        }

        if (App.Settings.Current.AutoCloudDeviceImages.TryGetValue(device.Id, out string? autoCloud) &&
            !string.IsNullOrWhiteSpace(autoCloud) &&
            File.Exists(autoCloud))
        {
            return autoCloud;
        }

        return null;
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
        => await DownloadAndCacheUrlAsync(item.ImageUrl, item.IsPiperSvg);

    public static async Task<string?> DownloadAndCacheUrlAsync(string url, bool stripPiperLayers = false)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            Directory.CreateDirectory(CloudCacheDir);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];

            foreach (string existingExt in new[] { ".png", ".jpg", ".webp", "_v2.svg" })
            {
                string existingPath = Path.Combine(CloudCacheDir, $"{hash}{existingExt}");
                if (File.Exists(existingPath) && new FileInfo(existingPath).Length > 64)
                    return existingPath;
            }

            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            string? mediaType = resp.Content.Headers.ContentType?.MediaType;
            bool isSvg = url.Contains(".svg", StringComparison.OrdinalIgnoreCase) ||
                         (mediaType?.Contains("svg", StringComparison.OrdinalIgnoreCase) == true);

            if (isSvg)
            {
                string rawSvg = await resp.Content.ReadAsStringAsync();
                string cleaned = stripPiperLayers ? SanitizePiperSvg(rawSvg) : rawSvg;
                string svgPath = Path.Combine(CloudCacheDir, $"{hash}_v2.svg");
                await File.WriteAllTextAsync(svgPath, cleaned, Encoding.UTF8);
                return svgPath;
            }

            string ext = (mediaType?.ToLowerInvariant()) switch
            {
                "image/webp" => ".webp",
                "image/jpeg" or "image/jpg" => ".jpg",
                _ => url.Contains(".webp", StringComparison.OrdinalIgnoreCase) || url.EndsWith("/fwebp", StringComparison.OrdinalIgnoreCase)
                    ? ".webp"
                    : (url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) || url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png"),
            };

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 64) return null;
            string cachedPath = Path.Combine(CloudCacheDir, $"{hash}{ext}");
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
            EnsureBuiltInPresets();
            if (forceRecheck)
            {
                CheckedAutoIds.Clear();
                InvalidateManifestCache();
            }

            await RefreshRemoteManifestCacheIfNeededAsync(forceRecheck);
            var manifest = GetEffectiveManifest();

            var toFetch = devices
                .Where(d => !App.Settings.Current.DeviceImageOverrides.ContainsKey(d.Id))
                .Where(d => forceRecheck || !CheckedAutoIds.Contains(d.Id))
                .ToList();

            if (toFetch.Count == 0) return 0;

            List<CloudPresetImageItem>? cloudCatalog = null;
            bool settingsDirty = false;
            object catalogLock = new();
            object dirtyLock = new();

            async Task<List<CloudPresetImageItem>> GetCatalogAsync()
            {
                if (cloudCatalog != null) return cloudCatalog;
                var fetched = await FetchCloudCatalogAsync();
                lock (catalogLock)
                    cloudCatalog ??= fetched;
                return cloudCatalog;
            }

            try
            {
                using var throttle = new SemaphoreSlim(8, 8);
                var tasks = toFetch.Select(async dev =>
                {
                    await throttle.WaitAsync();
                    try
                    {
                        string? existingCached;
                        lock (dirtyLock)
                        {
                            CheckedAutoIds.Add(dev.Id);
                            App.Settings.Current.AutoCloudDeviceImages.TryGetValue(dev.Id, out existingCached);
                        }

                        var match = DeviceImageRuleMatcher.MatchRule(dev, manifest.Rules);
                        string? targetUrl = !string.IsNullOrWhiteSpace(match.CloudUrl)
                            ? match.CloudUrl
                            : (!string.IsNullOrWhiteSpace(match.PiperSvg) ? $"{PiperSvgBaseUrl}{match.PiperSvg}" : null);

                        string? pulledPath = null;
                        if (!string.IsNullOrWhiteSpace(targetUrl))
                        {
                            bool isSvg = targetUrl.Contains(".svg", StringComparison.OrdinalIgnoreCase);
                            pulledPath = await DownloadAndCacheUrlAsync(targetUrl, stripPiperLayers: isSvg);
                        }

                        if (string.IsNullOrWhiteSpace(pulledPath) &&
                            DeviceImageRuleMatcher.TryExtractVidPid(dev, out string vidHex, out string pidHex))
                        {
                            var cat = await GetCatalogAsync();
                            string usbExact = $"usb:{vidHex}:{pidHex}";
                            var exactCloud = cat.FirstOrDefault(c =>
                                c.MatchHint.Contains(usbExact, StringComparison.OrdinalIgnoreCase));
                            if (exactCloud != null)
                                pulledPath = await DownloadAndCacheCloudImageAsync(exactCloud);
                        }

                        if (!string.IsNullOrWhiteSpace(pulledPath) &&
                            File.Exists(pulledPath) &&
                            !string.Equals(existingCached, pulledPath, StringComparison.OrdinalIgnoreCase))
                        {
                            lock (dirtyLock)
                            {
                                App.Settings.Current.AutoCloudDeviceImages[dev.Id] = pulledPath;
                                App.Settings.Save();
                                settingsDirty = true;
                                updatedCount++;
                            }
                            Diag.Log($"[AutoCloudImage] 已为 {dev.Name} ({dev.Id}) 自动缓存线上图片: {Path.GetFileName(pulledPath)}");
                            ImagesUpdated?.Invoke();
                        }
                    }
                    finally
                    {
                        throttle.Release();
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
            }
            finally
            {
                if (settingsDirty)
                {
                    lock (dirtyLock)
                        App.Settings.Save();
                    ImagesUpdated?.Invoke();
                }
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

    internal static string MatchDefaultPresetId(PeripheralDevice d)
    {
        var manifest = GetEffectiveManifest();
        return DeviceImageRuleMatcher.MatchRule(d, manifest.Rules).PresetId;
    }

    private static bool TryExtractVidPid(PeripheralDevice d, out string vidHex, out string pidHex)
        => DeviceImageRuleMatcher.TryExtractVidPid(d, out vidHex, out pidHex);
}
