using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using GearDeck.Core;
using GearDeck.Core.Services;

namespace GearDeck.App.Services;

public sealed record ScannedGameEntry(
    string Name,
    string Platform,
    string InstallDir,
    string PrimaryExe,
    List<string> CandidateExes,
    string? IconPath);

/// <summary>
/// OpenMouse Bridge + 本地游戏库混合自主扫描器：
/// 1) 自动加载内置 65+ 热门游戏签名库并合并 OpenMouse Desktop 开源 CDN games.json（本地 24h 缓存，断网无阻塞回退）；
/// 2) 扫描 Steam (VDF/ACF，已过滤 appid_xxx 占位符与 Steam++/WallpaperEngine/Ficket 等工具)、Epic、Battle.net、Riot、WeGame、HoYoPlay、Xbox、EA、Ubisoft、GOG 与注册表；
/// 3) 对照当前运行中进程与扫描目录匹配标准游戏可执行文件并提取 32-bit BGRA 高清图标。
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

    public static string? ExtractExeIcon(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;
        try
        {
            Directory.CreateDirectory(IconCacheDir);
            return TryExtractExeIconBmp(exePath);
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
        var seenExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Directory.CreateDirectory(IconCacheDir);
            ScanSteam(results, seenExe, catalog);
            ScanEpic(results, seenExe, catalog);
            ScanDrivePlatformDirs(results, seenExe, catalog);
            ScanUninstallRegistry(results, seenExe, catalog);
            ScanRunningKnownGames(results, seenExe, catalog);
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
        TryAddSteamRoot(roots, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        TryAddSteamRoot(roots, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        TryAddSteamRoot(roots, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");

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
        {
            string steamapps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamapps)) continue;
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"); }
            catch { continue; }

            foreach (string acf in manifests)
            {
                try
                {
                    var parsed = GameLibraryParser.ParseSteamAppManifestAcf(File.ReadAllText(acf));
                    if (parsed == null) continue;
                    string gameDir = Path.Combine(steamapps, "common", parsed.InstallDir);
                    if (!Directory.Exists(gameDir)) continue;
                    TryAddGameFromDir(results, seen, parsed.Name, "Steam", gameDir, preferredExe: null, catalog);
                }
                catch { }
            }
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
        var hivePaths = new (RegistryKey Hive, string SubKey)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, subPath) in hivePaths)
        {
            using var root = hive.OpenSubKey(subPath);
            if (root == null) continue;
            foreach (string subName in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(subName);
                    if (k == null) continue;
                    string? disp = k.GetValue("DisplayName") as string;
                    string? loc = (k.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                    string? pub = k.GetValue("Publisher") as string;
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
                string exeName = proc.ProcessName + ".exe";
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
                string? iconPath = !string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath)
                    ? TryExtractExeIconBmp(fullPath)
                    : null;

                var cands = entry.Executables.ToList();
                if (!cands.Contains(primary, StringComparer.OrdinalIgnoreCase))
                    cands.Insert(0, primary);

                results.Add(new ScannedGameEntry(entry.Name, "OpenMouse 运行检测", dir, primary, cands, iconPath));
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
        var pickedNames = GameLibraryParser.SelectGameExecutables(dir, gameName, exes);

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
        string? iconPath = primaryFullPath != null ? TryExtractExeIconBmp(primaryFullPath) : null;

        results.Add(new ScannedGameEntry(gameName, platform, dir, primaryName, pickedNames, iconPath));
    }

    private static List<string> EnumerateTopExesBounded(string rootDir, int maxDepth)
    {
        var found = new List<string>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((rootDir, 0));
        while (queue.Count > 0 && found.Count < 48)
        {
            var (cur, depth) = queue.Dequeue();
            foreach (string f in SafeEnumerateFiles(cur, "*.exe", SearchOption.TopDirectoryOnly))
                found.Add(f);

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

    private static void TryAddSteamRoot(HashSet<string> roots, RegistryKey hive, string subKey, string valName)
    {
        try
        {
            using var k = hive.OpenSubKey(subKey);
            string? path = (k?.GetValue(valName) as string)?.Replace('/', '\\').Trim();
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

    private static string? TryExtractExeIconBmp(string exePath)
    {
        try
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exePath)))[..12];
            string outPath = Path.Combine(IconCacheDir, $"{hash}.bmp");
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 100)
                return outPath;

            var shfi = new SHFILEINFO();
            const uint SHGFI_ICON = 0x100;
            const uint SHGFI_LARGEICON = 0x0;
            if (SHGetFileInfoW(exePath, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero ||
                shfi.hIcon == IntPtr.Zero)
                return null;

            try
            {
                if (!GetIconInfo(shfi.hIcon, out ICONINFO ii)) return null;
                try
                {
                    IntPtr hBmp = ii.hbmColor != IntPtr.Zero ? ii.hbmColor : ii.hbmMask;
                    if (hBmp == IntPtr.Zero) return null;

                    var bm = new BITMAP();
                    if (GetObjectW(hBmp, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
                        return null;

                    int w = bm.bmWidth;
                    int h = bm.bmHeight;
                    var bmi = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = w,
                        biHeight = h,
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
                    }
                    finally { ReleaseDC(IntPtr.Zero, hdc); }

                    bool anyAlpha = false;
                    for (int i = 3; i < pixels.Length; i += 4)
                    {
                        if (pixels[i] != 0) { anyAlpha = true; break; }
                    }
                    if (!anyAlpha)
                    {
                        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                    }

                    using var fs = File.Create(outPath);
                    using var bw = new BinaryWriter(fs);
                    int imgSize = pixels.Length;
                    bw.Write((ushort)0x4D42);
                    bw.Write(54 + imgSize);
                    bw.Write(0);
                    bw.Write(54);
                    bw.Write(40);
                    bw.Write(w);
                    bw.Write(h);
                    bw.Write((ushort)1);
                    bw.Write((ushort)32);
                    bw.Write(0);
                    bw.Write(imgSize);
                    bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
                    bw.Write(pixels);
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
                DestroyIcon(shfi.hIcon);
            }
        }
        catch
        {
            return null;
        }
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
