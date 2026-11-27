using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GearDeck.Core;
using GearDeck.Core.Services;
using SkiaSharp;

namespace GearDeck.Desktop.Services;

public sealed record ScannedGameEntry(
    string Name,
    string Platform,
    string InstallDir,
    string PrimaryExe,
    List<string> CandidateExes,
    string? IconPath,
    string? ExePath);

/// <summary>
/// 跨平台本地游戏库扫描器：Windows 扫描 Steam/Epic/各平台目录/卸载注册表/运行中进程并提取透明 PNG 图标缓存；
/// macOS 扫描 /Applications 与 Steam libraryfolders.vdf。
/// </summary>
public static class InstalledGameScanner
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GearDeck");

    private static readonly string IconCacheDir = Path.Combine(AppDataDir, "game-icons");
    private static readonly string OpenMouseGamesCachePath = Path.Combine(AppDataDir, "openmouse-games.json");
    private static readonly HttpClient CatalogHttp = new() { Timeout = TimeSpan.FromSeconds(4) };

    private static readonly string[] IgnoredPublishers =
    [
        "Microsoft", "NVIDIA", "AMD", "Intel", "Realtek", "Valve", "Google",
        "Mozilla", "Adobe", "Python", "Git", "7-Zip", "Notepad++", "WinRAR",
    ];

    public static async Task<List<ScannedGameEntry>> ScanAsync()
    {
        var catalog = await LoadMergedCatalogAsync();
        return await Task.Run(() => ScanInternal(catalog));
    }

    /// <summary>从 Windows .exe 提取高分辨率图标并缓存为透明 PNG；非 Windows 平台返回 null。</summary>
    public static string? ExtractExeIcon(string? exePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return null;

        try
        {
            Directory.CreateDirectory(IconCacheDir);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exePath.ToLowerInvariant())))[..16];
            string outPath = Path.Combine(IconCacheDir, $"{hash}.png");
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 100)
                return outPath;

            IntPtr hIcon = IntPtr.Zero;
            var hIcons = new IntPtr[1];
            var iconIds = new uint[1];
            uint extracted = PrivateExtractIconsW(exePath, 0, 64, 64, hIcons, iconIds, 1, 0);
            if (extracted >= 1 && hIcons[0] != IntPtr.Zero)
            {
                hIcon = hIcons[0];
            }
            else
            {
                var shfi = new SHFILEINFO();
                const uint SHGFI_ICON = 0x100;
                const uint SHGFI_LARGEICON = 0x0;
                if (SHGetFileInfoW(exePath, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) != IntPtr.Zero)
                    hIcon = shfi.hIcon;
            }

            if (hIcon == IntPtr.Zero)
                return null;

            try
            {
                if (!GetIconInfo(hIcon, out ICONINFO ii))
                    return null;

                try
                {
                    IntPtr hBmp = ii.hbmColor != IntPtr.Zero ? ii.hbmColor : ii.hbmMask;
                    if (hBmp == IntPtr.Zero)
                        return null;

                    var bm = new BITMAP();
                    if (GetObjectW(hBmp, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
                        return null;

                    int w = bm.bmWidth;
                    int h = ii.hbmColor != IntPtr.Zero ? bm.bmHeight : Math.Max(1, bm.bmHeight / 2);
                    var bmi = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = w,
                        biHeight = -h, // top-down DIB
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0,
                    };

                    byte[] pixels = new byte[w * h * 4];
                    IntPtr hdc = GetDC(IntPtr.Zero);
                    try
                    {
                        if (GetDIBits(hdc, hBmp, 0, (uint)h, pixels, ref bmi, 0) == 0)
                            return null;

                        bool anyAlpha = false;
                        for (int i = 3; i < pixels.Length; i += 4)
                        {
                            if (pixels[i] != 0)
                            {
                                anyAlpha = true;
                                break;
                            }
                        }

                        if (!anyAlpha)
                        {
                            byte[]? maskPixels = null;
                            if (ii.hbmMask != IntPtr.Zero)
                            {
                                var maskBm = new BITMAP();
                                if (GetObjectW(ii.hbmMask, Marshal.SizeOf<BITMAP>(), ref maskBm) != 0 &&
                                    maskBm.bmWidth == w && maskBm.bmHeight >= h)
                                {
                                    var maskBmi = new BITMAPINFOHEADER
                                    {
                                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                                        biWidth = w,
                                        biHeight = -h,
                                        biPlanes = 1,
                                        biBitCount = 32,
                                        biCompression = 0,
                                    };
                                    var buf = new byte[w * h * 4];
                                    if (GetDIBits(hdc, ii.hbmMask, 0, (uint)h, buf, ref maskBmi, 0) != 0)
                                        maskPixels = buf;
                                }
                            }

                            for (int i = 0; i < pixels.Length; i += 4)
                            {
                                bool transparent = maskPixels != null && maskPixels[i] > 128;
                                pixels[i + 3] = transparent ? (byte)0 : (byte)255;
                            }
                        }
                    }
                    finally
                    {
                        ReleaseDC(IntPtr.Zero, hdc);
                    }

                    using var skBmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                    Marshal.Copy(pixels, 0, skBmp.GetPixels(), pixels.Length);
                    using var img = SKImage.FromBitmap(skBmp);
                    using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
                    if (encoded == null)
                        return null;

                    using var fs = File.Create(outPath);
                    encoded.SaveTo(fs);
                    return outPath;
                }
                finally
                {
                    if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                    if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                }
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<OpenMouseGameCatalogEntry>> LoadMergedCatalogAsync()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            string? json = null;
            if (File.Exists(OpenMouseGamesCachePath) &&
                (DateTime.UtcNow - File.GetLastWriteTimeUtc(OpenMouseGamesCachePath)).TotalHours < 24)
            {
                json = await File.ReadAllTextAsync(OpenMouseGamesCachePath);
            }
            else
            {
                try
                {
                    json = await CatalogHttp.GetStringAsync(OpenMouseGameCatalog.GamesJsonCdnUrl);
                    if (!string.IsNullOrWhiteSpace(json))
                        await File.WriteAllTextAsync(OpenMouseGamesCachePath, json);
                }
                catch
                {
                    if (File.Exists(OpenMouseGamesCachePath))
                        json = await File.ReadAllTextAsync(OpenMouseGamesCachePath);
                }
            }

            var remote = OpenMouseGameCatalog.ParseGamesJson(json);
            return OpenMouseGameCatalog.MergeWithBuiltIn(remote);
        }
        catch
        {
            return OpenMouseGameCatalog.BuiltInEntries;
        }
    }

    private static List<ScannedGameEntry> ScanInternal(IReadOnlyList<OpenMouseGameCatalogEntry> catalog)
    {
        var results = new List<ScannedGameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                ScanSteam(results, seen, catalog);
                ScanEpic(results, seen, catalog);
                ScanDrivePlatformDirs(results, seen, catalog);
                ScanUninstallRegistry(results, seen, catalog);
            }
            else if (OperatingSystem.IsMacOS())
            {
                ScanMacApplications(results, seen, catalog);
                ScanMacSteam(results, seen, catalog);
            }
            ScanRunningKnownGames(results, seen, catalog);
        }
        catch (Exception ex)
        {
            Diag.Log($"游戏库扫描异常: {ex.Message}");
        }

        return results
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanSteam(
        List<ScannedGameEntry> results,
        HashSet<string> seen,
        IReadOnlyList<OpenMouseGameCatalogEntry> catalog)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryAddSteamRoot(roots, Win32RegistryRoot.HKCU, @"Software\Valve\Steam", "SteamPath");
        TryAddSteamRoot(roots, Win32RegistryRoot.HKLM, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        TryAddSteamRoot(roots, Win32RegistryRoot.HKLM, @"SOFTWARE\Valve\Steam", "InstallPath");

        foreach (string drive in GetFixedDriveRoots())
        {
            string candidate = Path.Combine(drive, "SteamLibrary");
            if (Directory.Exists(candidate)) roots.Add(candidate);
        }

        var libFolders = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (string r in roots)
        {
            string vdf = Path.Combine(r, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (string p in GameLibraryParser.ParseSteamLibraryFoldersVdf(File.ReadAllText(vdf)))
                {
                    if (Directory.Exists(p)) libFolders.Add(p);
                }
            }
            catch { }
        }

        foreach (string lib in libFolders)
            ScanSteamLibrary(results, seen, catalog, lib, "Steam");
    }

    private static void ScanMacSteam(
        List<ScannedGameEntry> results,
        HashSet<string> seen,
        IReadOnlyList<OpenMouseGameCatalogEntry> catalog)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] vdfCandidates =
        [
            Path.Combine(home, "Library", "Application Support", "Steam", "steamapps", "libraryfolders.vdf"),
            Path.Combine(home, ".steam", "steam", "steamapps", "libraryfolders.vdf"),
            "/Applications/Steam.app/Contents/MacOS/steamapps/libraryfolders.vdf",
        ];

        var libs = new HashSet<string>(StringComparer.Ordinal);
        foreach (string vdf in vdfCandidates)
        {
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (string p in GameLibraryParser.ParseSteamLibraryFoldersVdf(File.ReadAllText(vdf)))
                {
                    if (Directory.Exists(p)) libs.Add(p);
                }
                string steamRoot = Path.GetDirectoryName(Path.GetDirectoryName(vdf)!)!;
                if (Directory.Exists(steamRoot)) libs.Add(steamRoot);
            }
            catch { }
        }

        foreach (string lib in libs)
            ScanSteamLibrary(results, seen, catalog, lib, "Steam (macOS)");
    }

    private static void ScanSteamLibrary(
        List<ScannedGameEntry> results,
        HashSet<string> seen,
        IReadOnlyList<OpenMouseGameCatalogEntry> catalog,
        string lib,
        string platform)
    {
        string steamapps = Path.Combine(lib, "steamapps");
        if (!Directory.Exists(steamapps)) return;
        IEnumerable<string> manifests;
        try { manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"); }
        catch { return; }

        foreach (string acf in manifests)
        {
            try
            {
                var parsed = GameLibraryParser.ParseSteamAppManifestAcf(File.ReadAllText(acf));
                if (parsed == null) continue;
                string gameDir = Path.Combine(steamapps, "common", parsed.InstallDir);
                if (!Directory.Exists(gameDir)) continue;
                TryAddGameFromDir(results, seen, parsed.Name, platform, gameDir, preferredExe: null, catalog);
            }
            catch { }
        }
    }

    private static void ScanEpic(
        List<ScannedGameEntry> results,
        HashSet<string> seen,
        IReadOnlyList<OpenMouseGameCatalogEntry> catalog)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) return;

        foreach (string file in SafeEnumerateFiles(dir, "*.item", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var parsed = GameLibraryParser.ParseEpicManifestJson(File.ReadAllText(file));
                if (parsed == null || !Directory.Exists(parsed.InstallLocation)) continue;
                string? pref = !string.IsNullOrWhiteSpace(parsed.LaunchExecutable)
                    ? Path.Combine(parsed.InstallLocation, parsed.LaunchExecutable)
                    : null;
                TryAddGameFromDir(results, seen, parsed.Name, "Epic Games", parsed.InstallLocation, pref, catalog);
            }
            catch { }
        }
    }

    private static void ScanMacApplications(
        List<ScannedGameEntry> results,
        HashSet<string> seen,
        IReadOnlyList<OpenMouseGameCatalogEntry> catalog)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { "/Applications", Path.Combine(home, "Applications") };

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (string app in SafeEnumerateDirs(root))
            {
                if (!app.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
                string macosDir = Path.Combine(app, "Contents", "MacOS");
                if (!Directory.Exists(macosDir)) continue;

                var exes = SafeEnumerateFiles(macosDir, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsMacCandidateExecutable)
                    .ToList();
                if (exes.Count == 0) continue;

                string name = Path.GetFileNameWithoutExtension(app);
                string primary = Path.GetFileName(exes[0]);
                if (!seen.Add($"{primary}|{app}")) continue;

                string? icon = SafeEnumerateFiles(Path.Combine(app, "Contents", "Resources"), "*.icns", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                var candidates = exes.Take(4).Select(Path.GetFileName).OfType<string>().Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                results.Add(new ScannedGameEntry(name, "macOS", app, primary, candidates, icon, exes[0]));
            }
        }
    }

    private static void ScanDrivePlatformDirs(
        List<ScannedGameEntry> results,
        HashSet<string> seen,
        IReadOnlyList<OpenMouseGameCatalogEntry> catalog)
    {
        var relDirs = new (string RelPath, string Platform)[]
        {
            (@"Program Files\Riot Games", "Riot Games"),
            ("Riot Games", "Riot Games"),
            ("XboxGames", "Xbox / Game Pass"),
            (@"Program Files\EA Games", "EA App"),
            ("EA Games", "EA App"),
            (@"Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games", "Ubisoft"),
            (@"Program Files (x86)\GOG Galaxy\Games", "GOG"),
            ("WeGameApps", "WeGame"),
            (@"Program Files\WeGameApps", "WeGame"),
            (@"HoYoPlay\games", "HoYoPlay"),
            (@"Program Files\HoYoPlay\games", "HoYoPlay"),
            (@"Program Files (x86)\Battle.net", "Battle.net"),
            ("Battle.net", "Battle.net"),
        };

        foreach (string drive in GetFixedDriveRoots())
        {
            foreach (var (rel, platform) in relDirs)
            {
                string container = Path.Combine(drive, rel);
                if (!Directory.Exists(container)) continue;
                foreach (string sub in SafeEnumerateDirs(container))
                {
                    string name = Path.GetFileName(sub);
                    if (name.Equals("Riot Client", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Battle.net", StringComparison.OrdinalIgnoreCase))
                        continue;
                    TryAddGameFromDir(results, seen, name, platform, sub, preferredExe: null, catalog);
                }
            }
        }
    }

    private static void ScanUninstallRegistry(
        List<ScannedGameEntry> results,
        HashSet<string> seen,
        IReadOnlyList<OpenMouseGameCatalogEntry> catalog)
    {
        var hivePaths = new (Win32RegistryRoot Hive, string SubKey)[]
        {
            (Win32RegistryRoot.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Win32RegistryRoot.HKLM, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Win32RegistryRoot.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, subPath) in hivePaths)
        {
            foreach (string subName in Win32RegistryHelper.GetSubKeyNames(hive, subPath))
            {
                try
                {
                    string fullKey = subPath + "\\" + subName;
                    string? disp = Win32RegistryHelper.ReadString(hive, fullKey, "DisplayName");
                    string? loc = Win32RegistryHelper.ReadString(hive, fullKey, "InstallLocation")?.Trim().Trim('"');
                    string? pub = Win32RegistryHelper.ReadString(hive, fullKey, "Publisher");
                    if (string.IsNullOrWhiteSpace(disp) || string.IsNullOrWhiteSpace(loc) || !Directory.Exists(loc))
                        continue;

                    if (pub != null && IgnoredPublishers.Any(p => pub.Contains(p, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    if (disp.Contains("Watt Toolkit", StringComparison.OrdinalIgnoreCase) ||
                        disp.Contains("Steam++", StringComparison.OrdinalIgnoreCase) ||
                        disp.Contains("Wallpaper Engine", StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool looksLikeGame =
                        (pub?.Contains("miHoYo", StringComparison.OrdinalIgnoreCase) == true) ||
                        (pub?.Contains("Cognosphere", StringComparison.OrdinalIgnoreCase) == true) ||
                        (pub?.Contains("Tencent", StringComparison.OrdinalIgnoreCase) == true) ||
                        (pub?.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) == true) ||
                        (pub?.Contains("Riot", StringComparison.OrdinalIgnoreCase) == true) ||
                        (pub?.Contains("EA", StringComparison.OrdinalIgnoreCase) == true) ||
                        (pub?.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase) == true) ||
                        (pub?.Contains("Kuro", StringComparison.OrdinalIgnoreCase) == true) ||
                        loc.Contains("Games", StringComparison.OrdinalIgnoreCase) ||
                        subName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase);
                    if (!looksLikeGame) continue;

                    TryAddGameFromDir(results, seen, disp.Trim(), "本地游戏", loc, preferredExe: null, catalog);
                }
                catch { }
            }
        }
    }

    private static void ScanRunningKnownGames(
        List<ScannedGameEntry> results,
        HashSet<string> seen,
        IReadOnlyList<OpenMouseGameCatalogEntry> catalog)
    {
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                string exeName = proc.ProcessName + (OperatingSystem.IsWindows() ? ".exe" : "");
                var entry = OpenMouseGameCatalog.MatchByProcessName(exeName, catalog);
                if (entry == null) continue;

                string? fullPath = null;
                try { fullPath = proc.MainModule?.FileName; }
                catch { }

                string primary = !string.IsNullOrWhiteSpace(fullPath) ? Path.GetFileName(fullPath) : exeName;
                if (seen.Any(k => k.StartsWith(primary + "|", StringComparison.OrdinalIgnoreCase)))
                    continue;

                string dir = !string.IsNullOrWhiteSpace(fullPath) ? (Path.GetDirectoryName(fullPath) ?? "") : "";
                seen.Add($"{primary}|{dir}");
                var cands = entry.Executables.ToList();
                if (!cands.Contains(primary, StringComparer.OrdinalIgnoreCase))
                    cands.Insert(0, primary);

                string? iconPath = ExtractExeIcon(fullPath);
                results.Add(new ScannedGameEntry(entry.Name, "运行检测", dir, primary, cands, iconPath, fullPath));
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static void TryAddGameFromDir(
        List<ScannedGameEntry> results,
        HashSet<string> seen,
        string gameName,
        string platform,
        string dir,
        string? preferredExe,
        IReadOnlyList<OpenMouseGameCatalogEntry> catalog)
    {
        var exes = EnumerateTopExesBounded(dir, maxDepth: 3);
        if (preferredExe != null && File.Exists(preferredExe) && !exes.Contains(preferredExe, StringComparer.OrdinalIgnoreCase))
            exes.Insert(0, preferredExe);

        if (exes.Count == 0) return;

        var catHit = OpenMouseGameCatalog.MatchByExecutables(exes, catalog);
        var pickedNames = OperatingSystem.IsWindows()
            ? GameLibraryParser.SelectGameExecutables(dir, gameName, exes)
            : exes.Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => !string.IsNullOrWhiteSpace(n) && !GameLibraryParser.IsIgnoredExecutableName(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();

        if (catHit is { } ch)
        {
            foreach (string ce in ch.MatchedExes.AsEnumerable().Reverse())
            {
                pickedNames.RemoveAll(p => string.Equals(p, ce, StringComparison.OrdinalIgnoreCase));
                pickedNames.Insert(0, ce);
            }
            if (platform == "本地游戏" || gameName.Length < 4)
                gameName = ch.Entry.Name;
        }

        if (preferredExe != null && File.Exists(preferredExe))
        {
            string prefName = Path.GetFileName(preferredExe);
            if (!GameLibraryParser.IsIgnoredExecutableName(prefName) &&
                !pickedNames.Contains(prefName, StringComparer.OrdinalIgnoreCase))
                pickedNames.Insert(0, prefName);
        }

        if (pickedNames.Count == 0) return;
        string primaryName = pickedNames[0];
        if (!seen.Add($"{primaryName}|{dir}")) return;

        string? primaryFullPath = exes.FirstOrDefault(e =>
            string.Equals(Path.GetFileName(e), primaryName, StringComparison.OrdinalIgnoreCase));
        string? iconPath = ExtractExeIcon(primaryFullPath);
        results.Add(new ScannedGameEntry(gameName, platform, dir, primaryName, pickedNames, iconPath, primaryFullPath));
    }

    private static List<string> EnumerateTopExesBounded(string rootDir, int maxDepth)
    {
        var found = new List<string>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((rootDir, 0));
        string pattern = OperatingSystem.IsWindows() ? "*.exe" : "*";
        while (queue.Count > 0 && found.Count < 48)
        {
            var (cur, depth) = queue.Dequeue();
            foreach (string f in SafeEnumerateFiles(cur, pattern, SearchOption.TopDirectoryOnly))
            {
                if (OperatingSystem.IsWindows() || IsMacCandidateExecutable(f))
                    found.Add(f);
            }

            if (depth >= maxDepth) continue;
            foreach (string sub in SafeEnumerateDirs(cur))
            {
                string folderName = Path.GetFileName(sub);
                if (folderName.Equals("__Installer", StringComparison.OrdinalIgnoreCase) ||
                    folderName.Equals("Redist", StringComparison.OrdinalIgnoreCase) ||
                    folderName.Equals("DirectX", StringComparison.OrdinalIgnoreCase) ||
                    folderName.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase))
                    continue;
                queue.Enqueue((sub, depth + 1));
            }
        }
        return found;
    }

    private static bool IsMacCandidateExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(Path.GetExtension(path))) return true;
        return path.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryAddSteamRoot(HashSet<string> roots, Win32RegistryRoot root, string subKey, string valName)
    {
        try
        {
            string? path = Win32RegistryHelper.ReadString(root, subKey, valName)?.Replace('/', '\\').Trim();
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                roots.Add(path);
        }
        catch { }
    }

    private static List<string> GetFixedDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch { return []; }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern, SearchOption opt)
    {
        try { return Directory.GetFiles(dir, pattern, opt); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch { return []; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(
        string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetObjectW(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}

