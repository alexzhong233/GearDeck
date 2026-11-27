using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GearDeck.Core;
using GearDeck.Core.Models;
using GearDeck.Core.Services;
using SkiaSharp;
using Svg.Skia;

namespace GearDeck.Desktop.Services;

public sealed record PresetImageEntry(string Id, string Name, string Category, string LocalSvgPath);

public sealed record CloudPresetImageItem(
    string Id,
    string Name,
    string Brand,
    string MatchHint,
    string ImageUrl,
    bool IsPiperSvg,
    string? AssetFile = null);

/// <summary>
/// 全线上产品图自动匹配与本地缓存服务（Avalonia 移植版）：
/// 1) 读取并自动升级外置规则清单 (%LOCALAPPDATA%\GearDeck\device-images.json, version >= 3)；
/// 2) 按精确 VID:PID -> 厂商+关键词 -> 品牌家族/品类 优先从 GitHub assets 分支拉取预渲染 PNG（支持镜像与原图兜底）；
/// 3) 首次启用 v3 自动清理旧版手绘 preset-gallery/*.svg，支持用户手动上传本地图片或从线上目录替换。
/// </summary>
public static class DeviceImageService
{
    public const string DefaultAssetsRepoSlug = "alexzhong233/GearDeck";
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
    private static readonly object BitmapCacheLock = new();
    private static readonly Dictionary<string, Bitmap> BitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> BitmapCacheOrder = [];
    private const int MaxCachedBitmaps = 48;
    private static readonly SemaphoreSlim AutoPullLock = new(1, 1);
    private static readonly Dictionary<string, (DateTimeOffset LastAttemptUtc, bool Succeeded)> AutoCheckHistory = new(StringComparer.OrdinalIgnoreCase);
    private static string _lastMirrorSnapshot = "";

    private static bool _onlineV3Cleaned;
    private static DeviceImageManifest? _cachedManifest;
    private static DateTime _cachedLocalWriteUtc;
    private static DateTime _cachedRemoteWriteUtc;

    public static event Action? ImagesUpdated;

    public static IReadOnlyList<CloudPresetImageItem> CuratedCloudPresets { get; } =
    [
        new("cloud_gpx3", "Logitech PRO X3 / Superlight Series (OpenMouse)", "Logitech", "usb:046d:c54f;usb:046d:c094;pro x3;superstrike;superlight", "https://img.openmouse.app/logitech-pro-x3-superstrike.png", false),
        new("cloud_gpw_svg", "Logitech G PRO Wireless (OpenMouse)", "Logitech", "usb:046d:c088;g pro wireless;gpw", "https://img.openmouse.app/logitech-gpro-wireless.png", false),
        new("cloud_g502x", "Logitech G502 X Plus (OpenMouse)", "Logitech", "usb:046d:c08b;usb:046d:c08d;g502", "https://img.openmouse.app/logitech-g502-x-plus.png", false),
        new("cloud_viper_v3", "Razer Viper V3 Pro 毒蝰 V3 Pro (OpenMouse)", "Razer", "usb:1532:00c1;viper v3 pro;viper", "https://img.openmouse.app/razer-viper-v3-pro.png", false),
        new("cloud_dav3_svg", "Razer DeathAdder V3 Series (OpenMouse)", "Razer", "usb:1532:00b6;usb:1532:00b7;deathadder", "https://img.openmouse.app/razer-deathadder-v3.png", false),
        new("cloud_am_97", "Angry Miao AM Infinity .97 怒喵无限鼠标", "Angry Miao", "usb:0e8d:0703;usb:0e8d:0880;usb:35a1:0035;infinity;怒喵", "https://ammaster.angrymiao.com/mouse/assets/device_am35_a-3df1ebac.png", false),
        new("cloud_am_100r2", "Angry Miao AM INFINITY .100R2", "Angry Miao", "usb:0e8d:0704;usb:0e8d:0881;100r2", "https://ammaster.angrymiao.com/mouse/am30r2-mouse.png", false),
        new("cloud_am_30", "Angry Miao AM Infinity Mouse", "Angry Miao", "usb:3151:5007;usb:3151:402a;usb:3151:3010", "https://ammaster.angrymiao.com/mouse/am30-mouse.png", false),
        new("cloud_aula_l5", "AULA 狼蛛昆仑 L5 Ultra", "AULA", "usb:38a6:2f1a;usb:38a6:2f0c;usb:258a:0138;狼蛛;昆仑", "https://cdn2.aulacn.com/storage/20261015/UGa1792055906WIAjQC.jpg", false),
        new("cloud_wlmouse", "WLmouse Beast Series (OpenMouse)", "WLmouse", "usb:36a7:a856;usb:36a7:a860;beast;wlmouse;万灵", "https://img.openmouse.app/wlmouse-beast-g.png", false),
        new("cloud_lamzu", "Lamzu Maya X Series (OpenMouse)", "Lamzu", "usb:373e:;usb:37b0:;lamzu;maya;inca;兰族", "https://img.openmouse.app/lamzu-maya-x.png", false),
        new("cloud_pulsar_x2", "Pulsar X2 V2 / X2H Series (OpenMouse)", "Pulsar", "usb:3710:;usb:25a7:;pulsar;x2v2;x2h;xlite", "https://img.openmouse.app/pulsar-x2-v2.png", false),
        new("cloud_egg_xm2", "Endgame Gear XM2 8K / WE (OpenMouse)", "Endgame Gear", "usb:3367:;endgame;xm2;op1", "https://img.openmouse.app/endgame-gear-xm2-8k.png", false),
        new("cloud_finalmouse_ulx", "Finalmouse ULX Series (OpenMouse)", "Finalmouse", "usb:361d:;finalmouse;ulx;starlight", "https://img.openmouse.app/finalmouse-ulx.png", false),
        new("cloud_rog_harpe", "ASUS ROG Harpe Ace / 鼠标 (Piper)", "ROG", "usb:0b05:1ace;usb:0b05:;rog;harpe;keris", $"{PiperSvgBaseUrl}asus-rog-harpe-wireless.svg", true),
        new("cloud_rog_scope_98", "ROG 游侠 2 RX 98 无线键盘", "ROG", "usb:0b05:1b7a;游侠;scope", "https://press.asus.com/assets/w_640,h_640/172952e2-c635-4e01-b4a0-5634dd55142b/ROG%20Strix%20Scope%20II%2096%20Wireless%2001.png", false),
        new("cloud_rog_falchion", "ROG 魔导士 Ace HFX 75", "ROG", "usb:0b05:1c7d;魔导士;falchion", "https://dlcdnwebimgs.asus.com/gain/21597AB2-8BDD-4E12-83EC-45C6B2DC9B03/w717/h525/fwebp", false),
        new("cloud_rog_delta2", "ROG Delta II 棱镜 2 无线耳机", "ROG", "usb:0b05:1afa;delta", "https://press.asus.com/assets/w_640,h_640/b15882bc-d8fe-4638-98ea-01c66296de9b/A701_2400x2400_1.png", false),
        new("cloud_steelseries_mouse", "SteelSeries Aerox / Sensei (Piper)", "SteelSeries", "usb:1038:;steelseries;aerox;rival;sensei", $"{PiperSvgBaseUrl}steelseries-sensei310.svg", true),
        new("cloud_sony_buds", "Sony INZONE Buds WF-G700N", "Sony", "usb:054c:0ec2;inzone;sony;wf-g700n", "https://helpguide.sony.net/mdr/2977/v1/en/contents/image/i0010_wfg700n.png", false),
        new("cloud_apex5", "飞智八爪鱼 5 (Flydigi Apex 5)", "Flydigi", "usb:37d7:2501;usb:37d7:6001;apex 5;八爪鱼 5;cd2", "https://cdn.jsdelivr.net/gh/ReynArts/ApexSenseBridge@main/assets/controller-apex5.png", false),
        new("cloud_apex6", "飞智八爪鱼 6 Pro (Flydigi Apex 6 Pro)", "Flydigi", "usb:37d7:2502;apex 6;八爪鱼 6", "https://cdn.jsdelivr.net/gh/ReynArts/ApexSenseBridge@main/assets/controller-apex6-pro.png", false),
        new("cloud_apex4", "飞智八爪鱼 4 (Flydigi Apex 4)", "Flydigi", "usb:04b4:2412;apex 4;八爪鱼 4", "https://cdn.jsdelivr.net/gh/ReynArts/ApexSenseBridge@main/assets/controller-apex4.png", false),
        new("cloud_wooting_kb", "Wooting / Keychron Custom Keyboard (Piper)", "Keyboard", "usb:31e3:;usb:3434:;wooting;keychron;nuphy;drunkdeer", $"{PiperSvgBaseUrl}logitech-g-pro-keyboard.svg", true),
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
                    var ovKeys = SettingsBridge.StringMap("DeviceImageOverrides")
                        .Where(kv => kv.Value.StartsWith("preset:", StringComparison.OrdinalIgnoreCase) ||
                                     kv.Value.Contains("preset-gallery", StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (string k in ovKeys)
                    {
                        SettingsBridge.StringMap("DeviceImageOverrides").Remove(k);
                        dirty = true;
                    }

                    var staleAutoKeys = SettingsBridge.StringMap("AutoCloudDeviceImages")
                        .Where(kv => !File.Exists(kv.Value) || kv.Value.Contains("preset-gallery", StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (string k in staleAutoKeys)
                    {
                        SettingsBridge.StringMap("AutoCloudDeviceImages").Remove(k);
                        dirty = true;
                    }

                    if (dirty) SettingsBridge.SaveChanges();
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
            if (remote != null && remote.Version < bundled.Version)
                remote = null;

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
                lock (AutoCheckHistory)
                    AutoCheckHistory.Clear();

                if (local.Version < 6)
                {
                    var autoMap = SettingsBridge.StringMap("AutoCloudDeviceImages");
                    if (autoMap.Count > 0)
                    {
                        autoMap.Clear();
                        SettingsBridge.SaveChanges();
                    }
                }
                else
                {
                    string oldAmGroupHash = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes("https://cdn-www.angrymiao.com/products/products_am_infinity_mouse_97.png")))[..16];
                    var staleAutoKeys = SettingsBridge.StringMap("AutoCloudDeviceImages")
                        .Where(kv => Path.GetFileName(kv.Value).StartsWith(oldAmGroupHash, StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Key)
                        .ToList();
                    if (staleAutoKeys.Count > 0)
                    {
                        foreach (string k in staleAutoKeys)
                            SettingsBridge.StringMap("AutoCloudDeviceImages").Remove(k);
                        SettingsBridge.SaveChanges();
                    }
                }

                Diag.Log($"[DeviceImages] 已将本地图像规则清单自动升级至 v{bundled.Version}");
            }
        }
        catch { }
    }

    public static void ClearBitmapCache()
    {
        lock (BitmapCacheLock)
        {
            foreach (var bmp in BitmapCache.Values)
            {
                try { bmp.Dispose(); } catch { }
            }
            BitmapCache.Clear();
            BitmapCacheOrder.Clear();
        }
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

        if (SettingsBridge.StringMap("DeviceImageOverrides").TryGetValue(device.Id, out string? ov) &&
            !string.IsNullOrWhiteSpace(ov) &&
            !ov.StartsWith("preset:", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(ov))
        {
            return ov;
        }

        if (SettingsBridge.StringMap("AutoCloudDeviceImages").TryGetValue(device.Id, out string? autoCloud) &&
            !string.IsNullOrWhiteSpace(autoCloud) &&
            File.Exists(autoCloud))
        {
            return autoCloud;
        }

        return null;
    }

    public static IImage? LoadImageSource(PeripheralDevice device, double decodeSize = 96)
    {
        string? path = ResolveImagePath(device);
        return path != null ? CreateSourceFromFile(path, decodeSize) : null;
    }

    public static IImage? CreateSourceFromFile(string path, double decodeSize = 96)
    {
        try
        {
            if (!File.Exists(path)) return null;

            int targetPx = Math.Clamp((int)Math.Ceiling(Math.Max(24, decodeSize) * 2), 48, 384);
            long writeTicks = File.GetLastWriteTimeUtc(path).Ticks;
            string cacheKey = $"{path}|{writeTicks}|{targetPx}";

            lock (BitmapCacheLock)
            {
                if (BitmapCache.TryGetValue(cacheKey, out var cached))
                    return cached;
            }

            Bitmap? decoded;
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                decoded = RasterizeSvgToBitmap(path, targetPx);
                if (decoded == null)
                    return null;
            }
            else
            {
                using var stream = File.OpenRead(path);
                try
                {
                    decoded = Bitmap.DecodeToWidth(stream, targetPx, BitmapInterpolationMode.MediumQuality);
                }
                catch
                {
                    stream.Position = 0;
                    decoded = new Bitmap(stream);
                }
            }

            lock (BitmapCacheLock)
            {
                if (BitmapCache.TryGetValue(cacheKey, out var existing))
                {
                    decoded.Dispose();
                    return existing;
                }

                if (BitmapCacheOrder.Count >= MaxCachedBitmaps)
                {
                    string oldest = BitmapCacheOrder[0];
                    BitmapCacheOrder.RemoveAt(0);
                    BitmapCache.Remove(oldest);
                }

                BitmapCache[cacheKey] = decoded;
                BitmapCacheOrder.Add(cacheKey);
                return decoded;
            }
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? RasterizeSvgToBitmap(string svgPath, int targetPx)
    {
        try
        {
            using var svg = new SKSvg();
            if (svg.Load(svgPath) == null || svg.Picture == null)
                return null;

            SKRect bounds = svg.Picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                bounds = SKRect.Create(0, 0, targetPx, targetPx);

            float pad = MathF.Max(4f, targetPx * 0.05f);
            float usable = MathF.Max(16f, targetPx - pad * 2f);
            float scale = MathF.Min(usable / bounds.Width, usable / bounds.Height);
            float drawW = bounds.Width * scale;
            float drawH = bounds.Height * scale;
            float offsetX = (targetPx - drawW) * 0.5f - bounds.Left * scale;
            float offsetY = (targetPx - drawH) * 0.5f - bounds.Top * scale;

            using var skBitmap = new SKBitmap(targetPx, targetPx, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(skBitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(offsetX, offsetY);
                canvas.Scale(scale, scale);
                canvas.DrawPicture(svg.Picture);
                canvas.Flush();
            }

            using var image = SKImage.FromBitmap(skBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null)
                return null;

            using var ms = new MemoryStream(data.ToArray());
            return new Bitmap(ms);
        }
        catch (Exception ex)
        {
            Diag.Log($"SVG 栅格化失败 ({Path.GetFileName(svgPath)}): {ex.Message}");
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

        SettingsBridge.StringMap("DeviceImageOverrides")[deviceId] = dest;
        SettingsBridge.SaveChanges();
        return dest;
    }

    public static void SetPresetOverride(string deviceId, string presetId)
    {
        SettingsBridge.StringMap("DeviceImageOverrides")[deviceId] = $"preset:{presetId}";
        SettingsBridge.SaveChanges();
    }

    public static void ClearOverride(string deviceId)
    {
        SettingsBridge.StringMap("DeviceImageOverrides").Remove(deviceId);
        SettingsBridge.SaveChanges();
    }

    public static async Task<string?> DownloadAndCacheCloudImageAsync(CloudPresetImageItem item)
        => await DownloadAndCacheUrlAsync(item.ImageUrl, item.IsPiperSvg, item.AssetFile);

    public static string ComputeAssetHash16(string url)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();

    private static string NormalizeRepoSlug(string? rawSlug)
    {
        if (string.IsNullOrWhiteSpace(rawSlug))
            return DefaultAssetsRepoSlug;

        string s = rawSlug.Trim().Trim('/');
        if (s.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            s = s["https://github.com/".Length..].Trim('/');
        else if (s.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
            s = s["http://github.com/".Length..].Trim('/');

        string[] parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? $"{parts[0]}/{parts[1]}" : DefaultAssetsRepoSlug;
    }

    public static IReadOnlyList<string> BuildDownloadCandidates(
        string originalUrl,
        string? assetFile = null,
        string? repoSlug = null,
        string? mirrorUrl = null)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddUnique(string? u)
        {
            if (!string.IsNullOrWhiteSpace(u) && seen.Add(u))
                candidates.Add(u);
        }

        if (string.IsNullOrWhiteSpace(originalUrl))
            return candidates;

        string slug = NormalizeRepoSlug(repoSlug ?? SettingsBridge.Text("UpdateRepoSlug"));
        string mirror = (mirrorUrl ?? SettingsBridge.Text("GitHubMirrorUrl")).Trim();
        bool hasMirror = mirror.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         mirror.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (originalUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            originalUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            string relAsset = !string.IsNullOrWhiteSpace(assetFile)
                ? assetFile.Trim().TrimStart('/')
                : $"images/{ComputeAssetHash16(originalUrl)}.png";

            string jsDelivr = $"https://cdn.jsdelivr.net/gh/{slug}@assets/{relAsset}";
            string rawGitHub = $"https://raw.githubusercontent.com/{slug}/assets/{relAsset}";

            if (hasMirror)
            {
                AddUnique(ApplyMirrorUrl(rawGitHub, mirror));
                AddUnique(jsDelivr);
                AddUnique(ApplyMirrorUrl(originalUrl, mirror));
            }
            else
            {
                AddUnique(jsDelivr);
                AddUnique(rawGitHub);
                AddUnique(originalUrl);
            }
        }
        else
        {
            AddUnique(ApplyMirrorUrl(originalUrl, mirror));
        }

        return candidates;
    }

    public static string ApplyMirrorUrl(string url, string? mirrorOverride = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        string mirror = (mirrorOverride ?? SettingsBridge.Text("GitHubMirrorUrl")).Trim();
        if (string.IsNullOrEmpty(mirror)) return url;
        if (!mirror.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !mirror.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;

        string normalizedMirror = mirror.EndsWith('/') ? mirror : mirror + "/";
        if (url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedMirror + url;
        }
        return url;
    }

    public static string? TryGetCachedCloudPath(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
            foreach (string existingExt in new[] { ".png", ".jpg", ".webp", "_v2.svg" })
            {
                string existingPath = Path.Combine(CloudCacheDir, $"{hash}{existingExt}");
                if (File.Exists(existingPath) && new FileInfo(existingPath).Length > 64)
                    return existingPath;
            }
        }
        catch { }
        return null;
    }

    public static async Task<string?> DownloadAndCacheUrlAsync(string url, bool stripPiperLayers = false, string? assetFile = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            Directory.CreateDirectory(CloudCacheDir);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];

            if (TryGetCachedCloudPath(url) is { } hitPath)
                return hitPath;

            foreach (string fetchUrl in BuildDownloadCandidates(url, assetFile))
            {
                try
                {
                    using var resp = await Http.GetAsync(fetchUrl);
                    if (!resp.IsSuccessStatusCode) continue;

                    string? mediaType = resp.Content.Headers.ContentType?.MediaType;
                    if (mediaType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
                        continue;

                    byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                    if (bytes.Length < 64 || LooksLikeHtmlPayload(bytes))
                        continue;

                    bool isSvg = fetchUrl.Contains(".svg", StringComparison.OrdinalIgnoreCase) ||
                                 (mediaType?.Contains("svg", StringComparison.OrdinalIgnoreCase) == true);

                    if (isSvg)
                    {
                        string rawSvg = Encoding.UTF8.GetString(bytes);
                        string cleaned = stripPiperLayers ? SanitizePiperSvg(rawSvg) : rawSvg;
                        string svgPath = Path.Combine(CloudCacheDir, $"{hash}_v2.svg");
                        await File.WriteAllTextAsync(svgPath, cleaned, Encoding.UTF8);
                        return svgPath;
                    }

                    string ext = (mediaType?.ToLowerInvariant()) switch
                    {
                        "image/webp" => ".webp",
                        "image/jpeg" or "image/jpg" => ".jpg",
                        _ => fetchUrl.Contains(".webp", StringComparison.OrdinalIgnoreCase) || fetchUrl.EndsWith("/fwebp", StringComparison.OrdinalIgnoreCase)
                            ? ".webp"
                            : (fetchUrl.Contains(".jpg", StringComparison.OrdinalIgnoreCase) || fetchUrl.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png"),
                    };

                    string cachedPath = Path.Combine(CloudCacheDir, $"{hash}{ext}");
                    await File.WriteAllBytesAsync(cachedPath, bytes);
                    return cachedPath;
                }
                catch
                {
                    // 单个镜像或 CDN 失败时继续回退下一候选源
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Diag.Log($"云端设备图片下载失败 ({url}): {ex.Message}");
            return null;
        }
    }

    private static bool LooksLikeHtmlPayload(ReadOnlySpan<byte> bytes)
    {
        int i = 0;
        while (i < bytes.Length && bytes[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;
        if (bytes.Length - i < 5)
            return false;
        string head = Encoding.ASCII.GetString(bytes.Slice(i, Math.Min(16, bytes.Length - i)));
        return head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) ||
               head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<int> EnsureAutoCloudImagesAsync(IReadOnlyList<PeripheralDevice> devices, bool forceRecheck = false)
    {
        if (devices.Count == 0) return 0;
        await AutoPullLock.WaitAsync();
        int updatedCount = 0;
        try
        {
            EnsureBuiltInPresets();
            string mirror = SettingsBridge.Text("GitHubMirrorUrl").Trim();
            if (forceRecheck || !string.Equals(mirror, _lastMirrorSnapshot, StringComparison.OrdinalIgnoreCase))
            {
                lock (AutoCheckHistory)
                    AutoCheckHistory.Clear();
                _lastMirrorSnapshot = mirror;
                if (forceRecheck)
                    InvalidateManifestCache();
            }

            await RefreshRemoteManifestCacheIfNeededAsync(forceRecheck);
            var manifest = GetEffectiveManifest();

            var nowUtc = DateTimeOffset.UtcNow;
            var toFetch = devices
                .Where(d => !SettingsBridge.StringMap("DeviceImageOverrides").ContainsKey(d.Id))
                .Where(d =>
                {
                    if (forceRecheck) return true;
                    lock (AutoCheckHistory)
                    {
                        if (!AutoCheckHistory.TryGetValue(d.Id, out var hist))
                            return true;
                        if (hist.Succeeded)
                            return false;
                        return (nowUtc - hist.LastAttemptUtc).TotalHours >= 12;
                    }
                })
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
                            SettingsBridge.StringMap("AutoCloudDeviceImages").TryGetValue(dev.Id, out existingCached);
                        }

                        var match = DeviceImageRuleMatcher.MatchRule(dev, manifest.Rules);
                        string? targetUrl = !string.IsNullOrWhiteSpace(match.CloudUrl)
                            ? match.CloudUrl
                            : (!string.IsNullOrWhiteSpace(match.PiperSvg) ? $"{PiperSvgBaseUrl}{match.PiperSvg}" : null);

                        string? pulledPath = null;
                        if (!string.IsNullOrWhiteSpace(targetUrl))
                        {
                            bool isSvg = targetUrl.Contains(".svg", StringComparison.OrdinalIgnoreCase);
                            pulledPath = await DownloadAndCacheUrlAsync(targetUrl, stripPiperLayers: isSvg, assetFile: match.AssetFile);
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

                        bool ok = !string.IsNullOrWhiteSpace(pulledPath) && File.Exists(pulledPath);
                        lock (AutoCheckHistory)
                            AutoCheckHistory[dev.Id] = (DateTimeOffset.UtcNow, ok);

                        if (ok &&
                            !string.Equals(existingCached, pulledPath, StringComparison.OrdinalIgnoreCase))
                        {
                            lock (dirtyLock)
                            {
                                SettingsBridge.StringMap("AutoCloudDeviceImages")[dev.Id] = pulledPath!;
                                SettingsBridge.SaveChanges();
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
                        SettingsBridge.SaveChanges();
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
        try
        {
            Directory.CreateDirectory(CloudCacheDir);
            if (!force && File.Exists(RemoteManifestCachePath) &&
                (DateTime.UtcNow - File.GetLastWriteTimeUtc(RemoteManifestCachePath)).TotalHours < 12)
            {
                return;
            }

            string customManifest = SettingsBridge.Text("CustomCloudGalleryManifestUrl").Trim();
            var manifestUrls = new List<string>();
            if (!string.IsNullOrWhiteSpace(customManifest))
            {
                manifestUrls.Add(ApplyMirrorUrl(customManifest));
            }
            else
            {
                string slug = NormalizeRepoSlug(SettingsBridge.Text("UpdateRepoSlug"));
                string mirror = SettingsBridge.Text("GitHubMirrorUrl").Trim();
                bool hasMirror = mirror.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 mirror.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                string rawUrl = $"https://raw.githubusercontent.com/{slug}/assets/manifest.json";
                string jsDelivrUrl = $"https://cdn.jsdelivr.net/gh/{slug}@assets/manifest.json";
                if (hasMirror)
                {
                    manifestUrls.Add(ApplyMirrorUrl(rawUrl, mirror));
                    manifestUrls.Add(jsDelivrUrl);
                    manifestUrls.Add(rawUrl);
                }
                else
                {
                    manifestUrls.Add(jsDelivrUrl);
                    manifestUrls.Add(rawUrl);
                }
            }

            int bundledVersion = File.Exists(BundledManifestPath)
                ? DeviceImageRuleMatcher.ParseManifest(TryReadAllText(BundledManifestPath)).Version
                : 0;

            foreach (string url in manifestUrls.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string json = await Http.GetStringAsync(url);
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    var parsed = DeviceImageRuleMatcher.ParseManifest(json);
                    if (parsed.Version < bundledVersion)
                    {
                        if (File.Exists(RemoteManifestCachePath))
                        {
                            try { File.Delete(RemoteManifestCachePath); } catch { }
                            InvalidateManifestCache();
                        }
                        break;
                    }
                    if (parsed.Rules.Count > 0 || parsed.Gallery.Count > 0)
                    {
                        await File.WriteAllTextAsync(RemoteManifestCachePath, json, Encoding.UTF8);
                        InvalidateManifestCache();
                        break;
                    }
                }
                catch
                {
                    // 尝试下一个清单源
                }
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
                list.Add(new CloudPresetImageItem(g.Id, g.Name, g.Brand, g.MatchHint, g.ImageUrl, g.IsPiperSvg, g.AssetFile));
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

        string customManifest = SettingsBridge.Text("CustomCloudGalleryManifestUrl");
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

    /// <summary>
    /// 直接读写 App.Settings.Current；单元测试等无宿主环境时回退到独立 sidecar 文件。
    /// </summary>
    private static class SettingsBridge
    {
        private static readonly string SidecarPath = Path.Combine(AppDataDir, "device-image-settings.json");
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };
        private static SidecarState? _sidecar;

        private static AppSettings? Current => App.Settings?.Current;

        public static IDictionary<string, string> StringMap(string name)
        {
            if (Current is { } s)
            {
                return name switch
                {
                    "AutoCloudDeviceImages" => s.AutoCloudDeviceImages,
                    _ => s.DeviceImageOverrides,
                };
            }
            return Sidecar().Map(name);
        }

        public static string Text(string name)
        {
            if (Current is { } s)
            {
                return name switch
                {
                    "CustomCloudGalleryManifestUrl" => s.CustomCloudGalleryManifestUrl,
                    "GitHubMirrorUrl" => s.GitHubMirrorUrl,
                    "UpdateRepoSlug" => s.UpdateRepoSlug,
                    _ => "",
                };
            }
            return Sidecar().Text(name);
        }

        public static void SaveChanges()
        {
            if (App.Settings != null)
            {
                App.Settings.Save();
                return;
            }
            SaveSidecar();
        }

        private static SidecarState Sidecar()
        {
            if (_sidecar != null) return _sidecar;
            try
            {
                _sidecar = File.Exists(SidecarPath)
                    ? JsonSerializer.Deserialize<SidecarState>(File.ReadAllText(SidecarPath), JsonOpts) ?? new SidecarState()
                    : new SidecarState();
            }
            catch
            {
                _sidecar = new SidecarState();
            }
            _sidecar.Normalize();
            return _sidecar;
        }

        private static void SaveSidecar()
        {
            try
            {
                var state = Sidecar();
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(SidecarPath, JsonSerializer.Serialize(state, JsonOpts));
            }
            catch { }
        }

        private sealed class SidecarState
        {
            public Dictionary<string, string> DeviceImageOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> AutoCloudDeviceImages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public string CustomCloudGalleryManifestUrl { get; set; } = "";
            public string GitHubMirrorUrl { get; set; } = "";
            public string UpdateRepoSlug { get; set; } = DefaultAssetsRepoSlug;

            public void Normalize()
            {
                DeviceImageOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                AutoCloudDeviceImages ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                CustomCloudGalleryManifestUrl ??= "";
                GitHubMirrorUrl ??= "";
                if (string.IsNullOrWhiteSpace(UpdateRepoSlug))
                    UpdateRepoSlug = DefaultAssetsRepoSlug;
            }

            public Dictionary<string, string> Map(string name) => name switch
            {
                "AutoCloudDeviceImages" => AutoCloudDeviceImages,
                _ => DeviceImageOverrides,
            };

            public string Text(string name) => name switch
            {
                "CustomCloudGalleryManifestUrl" => CustomCloudGalleryManifestUrl,
                "GitHubMirrorUrl" => GitHubMirrorUrl,
                "UpdateRepoSlug" => UpdateRepoSlug,
                _ => "",
            };
        }
    }
}
