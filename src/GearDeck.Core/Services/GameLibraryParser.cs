using System.Text.Json;
using System.Text.RegularExpressions;

namespace GearDeck.Core.Services;

public sealed record SteamAppManifestEntry(string AppId, string Name, string InstallDir);

public sealed record EpicManifestEntry(string Name, string InstallLocation, string LaunchExecutable);

public sealed record OpenMouseGameCatalogEntry(
    string Id,
    string Name,
    List<string> Executables,
    string? IconUrl = null);

/// <summary>
/// 纯 .NET 本地游戏平台清单解析与游戏主进程筛选器（支持 Steam VDF/ACF、Epic Manifest、
/// OpenMouse public/games.json 签名目录及本地可执行文件启发式筛选）。
/// </summary>
public static partial class GameLibraryParser
{
    private static readonly HashSet<string> NonGameSteamAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "228980",  // Steamworks Common Redistributables
        "1070560", // Steam Linux Runtime
        "1391110", // Steam Linux Runtime - Soldier
        "1628350", // Steam Linux Runtime - Sniper
        "480",     // Spacewar SDK test app
        "1634680", // Watt Toolkit / Steam++ / Ficket utility
        "431960",  // Wallpaper Engine
        "250820",  // SteamVR
        "993090",  // Lossless Scaling
        "629520",  // Soundpad
        "1366800", // Crosshair X
        "223850",  // 3DMark
        "1905180", // OBS Studio
        "388080",  // Borderless Gaming
    };

    private static readonly string[] NonGameExeTokens =
    [
        "crashhandler", "crashreport", "crashsender", "unitycrashhandler", "crashpad",
        "unins", "uninstall", "setup", "redist", "vcredist", "dxsetup",
        "dotnet", "easyanticheat", "eac_launcher", "beservice", "battleye",
        "webview", "webview2", "cefprocess", "cefhtml", "bootstrap", "updater",
        "prerequisite", "steamerrorreporter", "zfgamebrowser", "overlay",
        "helper", "diagnostics", "perfmon", "vc_redist", "ndp4", "physx",
        "openal", "repair", "launcher_helper",
        "ficket", "steam++", "watttoolkit", "wallpaper32", "wallpaper64",
        "losslessscaling", "vrserver", "vrmonitor", "soundpad", "obs64", "obs32",
        "uu_launcher", "leigod", "crosshairx", "3dmark",
    ];

    public static List<string> ParseSteamLibraryFoldersVdf(string? content)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(content)) return paths;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in SteamPathRegex().Matches(content))
        {
            string raw = m.Groups[1].Value.Replace(@"\\", @"\").Trim();
            if (!string.IsNullOrWhiteSpace(raw) && seen.Add(raw))
                paths.Add(raw);
        }
        return paths;
    }

    public static SteamAppManifestEntry? ParseSteamAppManifestAcf(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        string? appId = MatchKv(content, "appid")?.Trim();
        string? name = MatchKv(content, "name")?.Trim();
        string? installDir = MatchKv(content, "installdir")?.Trim();

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir))
            return null;

        if (NonGameSteamAppIds.Contains(appId))
            return null;

        // 过滤未本地化/失效的占位清单名（如 appid_1634680）或运行库/工具包
        if (PlaceholderAppIdRegex().IsMatch(name) ||
            name.Contains("Steamworks", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Dedicated Server", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Wallpaper Engine", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Watt Toolkit", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Steam++", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Lossless Scaling", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("SteamVR", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new SteamAppManifestEntry(appId, name, installDir);
    }

    public static EpicManifestEntry? ParseEpicManifestJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? name = root.TryGetProperty("DisplayName", out var nEl) ? nEl.GetString() : null;
            string? installLoc = root.TryGetProperty("InstallLocation", out var lEl) ? lEl.GetString() : null;
            string? launchExe = root.TryGetProperty("LaunchExecutable", out var eEl) ? eEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installLoc))
                return null;
            if (name.StartsWith("Unreal Engine", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Quixel", StringComparison.OrdinalIgnoreCase) ||
                PlaceholderAppIdRegex().IsMatch(name.Trim()))
                return null;
            return new EpicManifestEntry(name.Trim(), installLoc.Trim(), launchExe?.Trim() ?? "");
        }
        catch
        {
            return null;
        }
    }

    public static List<string> SelectGameExecutables(string installDir, string gameName, IEnumerable<string> exeFullPaths)
    {
        string normGame = NormalizeAlnum(gameName);
        var normWords = gameName
            .Split([' ', '-', ':', '_', '(', ')', '™', '®'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeAlnum)
            .Where(w => w.Length >= 3)
            .ToList();

        var scored = new List<(string ExeName, string FullPath, int Score)>();
        foreach (string fullPath in exeFullPaths)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) continue;
            string exeName = ExtractFileName(fullPath);
            if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            string lower = exeName.ToLowerInvariant();
            if (IsIgnoredExecutableName(lower)) continue;

            int score = 0;
            if (lower.Contains("-win64-shipping.exe") || lower.Contains("-wingdk-shipping.exe"))
                score += 120;
            else if (lower.EndsWith("shipping.exe"))
                score += 80;

            string stem = NormalizeAlnum(Path.GetFileNameWithoutExtension(exeName));
            if (!string.IsNullOrEmpty(normGame) && (normGame.Contains(stem) || stem.Contains(normGame)))
                score += 65;
            else if (normWords.Any(w => stem.Contains(w)))
                score += 35;

            if (fullPath.Contains("Binaries\\Win64", StringComparison.OrdinalIgnoreCase))
                score += 30;
            if (lower.Contains("launcher") || lower.Contains("bootstrap"))
                score -= 25;

            int relDepth = ComputeRelativeDepth(installDir, fullPath);
            score -= relDepth * 5;

            scored.Add((exeName, fullPath, score));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.ExeName.Length)
            .Select(s => s.ExeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
    }

    public static bool IsIgnoredExecutableName(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return true;
        string lower = ExtractFileName(exeName).ToLowerInvariant();
        foreach (string token in NonGameExeTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static int ComputeRelativeDepth(string rootDir, string filePath)
    {
        if (string.IsNullOrWhiteSpace(rootDir)) return 1;
        string rel = filePath.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase)
            ? filePath[rootDir.Length..].TrimStart('\\', '/')
            : ExtractFileName(filePath);
        int depth = 0;
        foreach (char c in rel)
            if (c is '\\' or '/') depth++;
        return depth;
    }

    internal static string ExtractFileName(string path) => Path.GetFileName(path.Replace('\\', '/'));

    private static string NormalizeAlnum(string s)
        => string.Concat(s.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string? MatchKv(string content, string key)
    {
        var m = Regex.Match(content, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathRegex();

    [GeneratedRegex(@"^appid_\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderAppIdRegex();
}

/// <summary>
/// 参照 OpenMouse Desktop / OpenMouse-Bridge 的 games.json 预设目录架构：
/// 内置 65+ 热门端游/单机主进程签名目录，并支持在线合并 OpenMouse CDN games.json。
/// </summary>
public static class OpenMouseGameCatalog
{
    public const string GamesJsonCdnUrl = "https://cdn.jsdelivr.net/gh/OpenMouse-Project/Desktop@main/public/games.json";

    public static IReadOnlyList<OpenMouseGameCatalogEntry> BuiltInEntries { get; } =
    [
        new("counter-strike-2", "Counter-Strike 2", ["cs2.exe"]),
        new("valorant", "VALORANT", ["VALORANT-Win64-Shipping.exe", "VALORANT.exe"]),
        new("apex-legends", "Apex Legends", ["r5apex_dx12.exe", "r5apex.exe"]),
        new("pubg", "PUBG: BATTLEGROUNDS", ["TslGame.exe"]),
        new("overwatch-2", "Overwatch 2", ["Overwatch.exe"]),
        new("league-of-legends", "League of Legends", ["League of Legends.exe"]),
        new("dota-2", "Dota 2", ["dota2.exe"]),
        new("delta-force", "三角洲行动 (Delta Force)", ["DeltaForceClient-Win64-Shipping.exe", "DFClient-Win64-Shipping.exe"]),
        new("naraka-bladepoint", "永劫无间 (NARAKA: BLADEPOINT)", ["NarakaBladepoint.exe"]),
        new("marvel-rivals", "漫威争锋 (Marvel Rivals)", ["Marvel-Win64-Shipping.exe"]),
        new("rainbow-six-siege", "Tom Clancy's Rainbow Six Siege", ["RainbowSix.exe", "RainbowSix_Vulkan.exe", "RainbowSix_DX11.exe"]),
        new("the-finals", "THE FINALS", ["Discovery.exe"]),
        new("deadlock", "Deadlock", ["deadlock.exe", "project8.exe"]),
        new("fortnite", "Fortnite", ["FortniteClient-Win64-Shipping.exe"]),
        new("call-of-duty", "Call of Duty", ["cod.exe", "cod23-cod.exe"]),
        new("battlefield-2042", "Battlefield 2042", ["BF2042.exe"]),
        new("escape-from-tarkov", "Escape from Tarkov", ["EscapeFromTarkov.exe"]),
        new("rust", "Rust", ["RustClient.exe"]),
        new("destiny-2", "Destiny 2", ["destiny2.exe"]),
        new("warframe", "Warframe", ["Warframe.x64.exe", "Warframe.exe"]),
        new("team-fortress-2", "Team Fortress 2", ["tf_win64.exe", "hl2.exe"]),
        new("halo-infinite", "Halo Infinite", ["HaloInfinite.exe"]),
        new("black-myth-wukong", "黑神话：悟空 (Black Myth: Wukong)", ["b1-Win64-Shipping.exe", "b1.exe"]),
        new("genshin-impact", "原神 (Genshin Impact)", ["YuanShen.exe", "GenshinImpact.exe"]),
        new("honkai-star-rail", "崩坏：星穹铁道", ["StarRail.exe"]),
        new("zenless-zone-zero", "绝区零 (Zenless Zone Zero)", ["ZenlessZoneZero.exe"]),
        new("wuthering-waves", "鸣潮 (Wuthering Waves)", ["Client-Win64-Shipping.exe"]),
        new("elden-ring", "Elden Ring", ["eldenring.exe"]),
        new("cyberpunk-2077", "Cyberpunk 2077", ["Cyberpunk2077.exe"]),
        new("helldivers-2", "HELLDIVERS 2", ["helldivers2.exe"]),
        new("monster-hunter-wilds", "Monster Hunter Wilds", ["MonsterHunterWilds.exe"]),
        new("monster-hunter-world", "Monster Hunter: World", ["MonsterHunterWorld.exe"]),
        new("monster-hunter-rise", "Monster Hunter Rise", ["MonsterHunterRise.exe"]),
        new("sekiro", "Sekiro: Shadows Die Twice", ["sekiro.exe"]),
        new("baldurs-gate-3", "Baldur's Gate 3", ["bg3.exe", "bg3_dx11.exe"]),
        new("red-dead-redemption-2", "Red Dead Redemption 2", ["RDR2.exe"]),
        new("gta-v", "Grand Theft Auto V", ["GTA5.exe", "GTA5_Enhanced.exe"]),
        new("path-of-exile-2", "Path of Exile 2", ["PathOfExile.exe", "PathOfExileSteam.exe", "PathOfExile_x64.exe"]),
        new("diablo-4", "Diablo IV", ["Diablo IV.exe"]),
        new("world-of-warcraft", "World of Warcraft", ["Wow.exe", "WowClassic.exe"]),
        new("hearthstone", "Hearthstone", ["Hearthstone.exe"]),
        new("minecraft", "Minecraft", ["Minecraft.Windows.exe", "javaw.exe"]),
        new("rocket-league", "Rocket League", ["RocketLeague.exe"]),
        new("osu", "osu!", ["osu!.exe"]),
        new("palworld", "Palworld", ["Palworld-Win64-Shipping.exe"]),
        new("once-human", "七日世界 (Once Human)", ["ONCE_HUMAN.exe"]),
        new("crossfire", "穿越火线 (CrossFire)", ["crossfire.exe"]),
        new("dnf", "地下城与勇士 (DNF)", ["DNF.exe"]),
        new("lost-ark", "Lost Ark", ["LOSTARK.exe"]),
        new("spectre-divide", "Spectre Divide", ["SpectreClient-Win64-Shipping.exe"]),
        new("fragpunk", "界外狂潮 (FragPunk)", ["FragPunk.exe"]),
        new("arena-breakout-infinite", "暗区突围：无限", ["UAGame.exe"]),
        new("strinova", "卡拉彼丘 (Strinova)", ["CalabiYau-Win64-Shipping.exe", "Strinova-Win64-Shipping.exe"]),
        new("doom-eternal", "DOOM Eternal", ["DOOMEternalx64vk.exe"]),
        new("witcher-3", "The Witcher 3: Wild Hunt", ["witcher3.exe"]),
        new("terraria", "Terraria", ["Terraria.exe"]),
        new("hollow-knight", "Hollow Knight", ["hollow_knight.exe"]),
        new("stardew-valley", "Stardew Valley", ["Stardew Valley.exe"]),
        new("left-4-dead-2", "Left 4 Dead 2", ["left4dead2.exe"]),
        new("payday-3", "PAYDAY 3", ["PAYDAY3Client-Win64-Shipping.exe"]),
        new("deep-rock-galactic", "Deep Rock Galactic", ["FSD-Win64-Shipping.exe"]),
        new("forza-horizon-5", "Forza Horizon 5", ["ForzaHorizon5.exe"]),
        new("ea-sports-fc", "EA SPORTS FC", ["FC25.exe", "FC24.exe"]),
        new("kovaaks", "KovaaK's", ["FPSAimTrainer-Win64-Shipping.exe", "FPSAimTrainer.exe"]),
        new("aimlabs", "Aimlabs", ["AimLab_tb.exe"]),
    ];

    public static List<OpenMouseGameCatalogEntry> ParseGamesJson(string? json)
    {
        var parsed = new List<OpenMouseGameCatalogEntry>();
        if (string.IsNullOrWhiteSpace(json)) return parsed;
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement arr;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                arr = doc.RootElement;
            else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                     doc.RootElement.TryGetProperty("games", out var gArr) &&
                     gArr.ValueKind == JsonValueKind.Array)
                arr = gArr;
            else
                return parsed;

            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                string? name = el.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;

                string id = el.TryGetProperty("id", out var iEl) && !string.IsNullOrWhiteSpace(iEl.GetString())
                    ? iEl.GetString()!.Trim()
                    : name.Trim().ToLowerInvariant().Replace(' ', '-');

                var exes = new List<string>();
                if (el.TryGetProperty("executables", out var eArr) && eArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in eArr.EnumerateArray())
                    {
                        string? ex = item.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(ex))
                            exes.Add(GameLibraryParser.ExtractFileName(ex));
                    }
                }
                else if (el.TryGetProperty("executable", out var single) && !string.IsNullOrWhiteSpace(single.GetString()))
                {
                    exes.Add(GameLibraryParser.ExtractFileName(single.GetString()!.Trim()));
                }

                if (exes.Count == 0) continue;
                string? icon = el.TryGetProperty("icon", out var icEl) ? icEl.GetString() : null;
                parsed.Add(new OpenMouseGameCatalogEntry(id, name.Trim(), exes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), icon));
            }
        }
        catch
        {
        }
        return parsed;
    }

    public static List<OpenMouseGameCatalogEntry> MergeWithBuiltIn(IEnumerable<OpenMouseGameCatalogEntry>? remote)
    {
        var map = new Dictionary<string, OpenMouseGameCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in BuiltInEntries)
            map[entry.Id] = entry;

        if (remote != null)
        {
            foreach (var entry in remote)
            {
                if (!map.ContainsKey(entry.Id))
                    map[entry.Id] = entry;
            }
        }
        return map.Values.ToList();
    }

    public static OpenMouseGameCatalogEntry? MatchByProcessName(
        string? processExeName,
        IReadOnlyList<OpenMouseGameCatalogEntry>? catalog = null)
    {
        if (string.IsNullOrWhiteSpace(processExeName)) return null;
        string file = GameLibraryParser.ExtractFileName(processExeName.Trim());
        var list = catalog ?? BuiltInEntries;
        foreach (var entry in list)
        {
            foreach (var exe in entry.Executables)
            {
                if (string.Equals(file, exe, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
        }
        return null;
    }

    public static (OpenMouseGameCatalogEntry Entry, List<string> MatchedExes)? MatchByExecutables(
        IEnumerable<string> exeFullPaths,
        IReadOnlyList<OpenMouseGameCatalogEntry>? catalog = null)
    {
        var fileSet = exeFullPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => GameLibraryParser.ExtractFileName(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (fileSet.Count == 0) return null;

        var list = catalog ?? BuiltInEntries;
        foreach (var entry in list)
        {
            var matched = entry.Executables.Where(fileSet.Contains).ToList();
            if (matched.Count > 0)
                return (entry, matched);
        }
        return null;
    }
}
