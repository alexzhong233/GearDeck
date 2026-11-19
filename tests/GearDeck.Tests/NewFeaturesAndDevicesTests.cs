using GearDeck.Core.Models;
using GearDeck.Core.Providers;
using GearDeck.Core.Services;
using Xunit;

namespace GearDeck.Tests;

public class NewFeaturesAndDevicesTests
{
    [Fact]
    public void SonyInzone_BuildHubRequest_Formats64BytePacketAndChecksum()
    {
        byte[] req = SonyInzoneProvider.BuildHubRequest(0x04, 0x15);

        Assert.Equal(64, req.Length);
        Assert.Equal(0x02, req[0]);
        Assert.Equal(0x0C, req[1]);
        Assert.Equal(0x96, req[6]);
        Assert.Equal(0xC3, req[7]);
        Assert.Equal(0x41, req[8]);
        Assert.Equal(0x04, req[9]);
        Assert.Equal(0x01, req[10]);
        Assert.Equal(0x15, req[11]);
        Assert.Equal(unchecked((byte)(0x9B + 0x04 + 0x15)), req[13]);
    }

    [Fact]
    public void SonyInzone_TryParseHubBatteryReport_ParsesActiveAndInCaseStates()
    {
        var report = new byte[64];
        report[0] = 0x02;
        report[8] = 0x14;
        report[9] = 0x04;
        report[0x0D] = 0x00; // Left not charging
        report[0x0E] = 84;   // Left 84%
        report[0x0F] = 0x01; // Right charging
        report[0x10] = 0xFF; // Right in-case / unavailable
        report[0x11] = 0x00; // Case not charging
        report[0x12] = 62;   // Case 62%

        var parsed = SonyInzoneProvider.TryParseHubBatteryReport(report);

        Assert.NotNull(parsed);
        Assert.Equal(84, parsed!.Value.Left);
        Assert.False(parsed.Value.LeftChg);
        Assert.Null(parsed.Value.Right);
        Assert.True(parsed.Value.RightChg);
        Assert.Equal(62, parsed.Value.Case);
        Assert.False(parsed.Value.CaseChg);
    }

    [Fact]
    public void SonyInzone_TryParseHubBatteryReport_RejectsWrongHeaderOrAllMissing()
    {
        var report = new byte[64];
        report[0] = 0x02;
        report[8] = 0x14;
        report[9] = 0x04;
        report[0x0E] = 0xFF;
        report[0x10] = 0xFF;
        report[0x12] = 0xFF;
        Assert.Null(SonyInzoneProvider.TryParseHubBatteryReport(report));

        report[0x0E] = 90;
        report[9] = 0x05;
        Assert.Null(SonyInzoneProvider.TryParseHubBatteryReport(report));
    }

    [Fact]
    public void Flydigi_BuildApex5Command_IncludesReportIdAndChecksum()
    {
        byte[] pkt = FlydigiProvider.BuildApex5Command(0x03, 0x01, [0xAA]);

        Assert.Equal(32, pkt.Length);
        Assert.Equal(0x03, pkt[0]);
        Assert.Equal(0x5A, pkt[1]);
        Assert.Equal(0xA5, pkt[2]);
        Assert.Equal(0x01, pkt[3]);
        Assert.Equal(0x03, pkt[4]); // pLen(1) + 2
        Assert.Equal(0xAA, pkt[5]);
        Assert.Equal(unchecked((byte)(0x01 + 0x03 + 0xAA)), pkt[6]);
    }

    [Fact]
    public void Flydigi_BuildApex6Command_Includes33ByteReportAndChecksum()
    {
        byte[] pkt = FlydigiProvider.BuildApex6Command(0xA1, [0x02]);

        Assert.Equal(33, pkt.Length);
        Assert.Equal(0x00, pkt[0]);
        Assert.Equal(0x5A, pkt[1]);
        Assert.Equal(0xA5, pkt[2]);
        Assert.Equal(0xA1, pkt[3]);
        Assert.Equal(0x03, pkt[4]);
        Assert.Equal(0x02, pkt[5]);

        byte expected = 0;
        for (int i = 3; i < 32; i++) expected += pkt[i];
        Assert.Equal(expected, pkt[32]);
    }

    [Fact]
    public void Flydigi_ParseApex5InfoReply_ParsesBatteryBarsAndDeviceType()
    {
        // [0x04 reportId, 0x5A, 0xA5, cmd=0x01, len, b4, devType, conn, ...]
        var pkt = new byte[32];
        pkt[0] = 0x04;
        pkt[1] = 0x5A;
        pkt[2] = 0xA5;
        pkt[3] = 0x01; // frame[2] = cmd
        pkt[6] = 128;  // frame[5] = Apex 5
        pkt[7] = 0x01; // frame[6] = wireless 2.4G
        pkt[12] = 0x14; // frame[11] = chg=1 (charging), bars=4 (80%)

        var info = FlydigiProvider.ParseApex5InfoReply(pkt);

        Assert.NotNull(info);
        Assert.Equal("飞智八爪鱼 5", info!.ModelName);
        Assert.Equal(80, info.BatteryPercent);
        Assert.Equal(BatteryState.Charging, info.ChargeState);
        Assert.Equal("2.4G", info.Transport);
    }

    [Fact]
    public void Flydigi_ParseApex6InfoReply_ValidatesChecksumAndParsesProModel()
    {
        var pkt = new byte[33];
        pkt[0] = 0x00;
        pkt[1] = 0x5A;
        pkt[2] = 0xA5;
        pkt[3] = 0x01; // frame[2] = cmd
        pkt[6] = 0x96; // frame[5] = Apex 6 Pro
        pkt[7] = 0x00; // frame[6] = USB wired
        pkt[12] = 0x29; // frame[11] = chg=2 (full -> 100%)
        byte sum = 0;
        for (int i = 3; i < 32; i++) sum += pkt[i];
        pkt[32] = sum;

        var info = FlydigiProvider.ParseApex6InfoReply(pkt);

        Assert.NotNull(info);
        Assert.Equal("飞智八爪鱼 6 Pro", info!.ModelName);
        Assert.Equal(100, info.BatteryPercent);
        Assert.Equal(BatteryState.FullyCharged, info.ChargeState);
        Assert.Equal("USB", info.Transport);

        // Corrupt checksum -> rejected
        pkt[32] ^= 0xFF;
        Assert.Null(FlydigiProvider.ParseApex6InfoReply(pkt));
    }

    [Fact]
    public void Flydigi_ParseClassicInfoReply_AndProfileStatusReply()
    {
        var classic = new byte[32];
        classic[3] = 84;   // Apex 4
        classic[11] = 73;  // 73%
        classic[13] = 0;   // 2.4G
        classic[15] = 236; // 0xEC signature

        var parsed = FlydigiProvider.ParseClassicInfoReply(classic);
        Assert.NotNull(parsed);
        Assert.Equal("飞智八爪鱼 4", parsed!.ModelName);
        Assert.Equal(73, parsed.BatteryPercent);
        Assert.Equal("2.4G", parsed.Transport);

        var prof = new byte[32];
        prof[0] = 0x04;
        prof[1] = 0x5A;
        prof[2] = 0xA5;
        prof[3] = 0xA1;
        prof[6] = 6; // % 4 == 2
        Assert.Equal(2, FlydigiProvider.ParseProfileStatusReply(prof));
    }

    [Fact]
    public void GameLibraryParser_ParsesSteamAndEpicManifests()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"      "C:\\Program Files (x86)\\Steam"
                }
                "1"
                {
                    "path"      "D:\\SteamLibrary"
                }
            }
            """;
        var libs = GameLibraryParser.ParseSteamLibraryFoldersVdf(vdf);
        Assert.Equal(2, libs.Count);
        Assert.Equal(@"C:\Program Files (x86)\Steam", libs[0]);
        Assert.Equal(@"D:\SteamLibrary", libs[1]);

        const string acf = """
            "AppState"
            {
                "appid"         "2358720"
                "name"          "Black Myth: Wukong"
                "installdir"    "BlackMythWukong"
            }
            """;
        var steamGame = GameLibraryParser.ParseSteamAppManifestAcf(acf);
        Assert.NotNull(steamGame);
        Assert.Equal("Black Myth: Wukong", steamGame!.Name);
        Assert.Equal("BlackMythWukong", steamGame.InstallDir);

        const string redistAcf = """
            "AppState"
            {
                "appid"         "228980"
                "name"          "Steamworks Common Redistributables"
                "installdir"    "Steamworks Shared"
            }
            """;
        Assert.Null(GameLibraryParser.ParseSteamAppManifestAcf(redistAcf));

        const string epicJson = """
            {
                "DisplayName": "Genshin Impact",
                "InstallLocation": "D:\\Games\\Genshin Impact",
                "LaunchExecutable": "GenshinImpact.exe"
            }
            """;
        var epicGame = GameLibraryParser.ParseEpicManifestJson(epicJson);
        Assert.NotNull(epicGame);
        Assert.Equal("Genshin Impact", epicGame!.Name);
        Assert.Equal("GenshinImpact.exe", epicGame.LaunchExecutable);
    }

    [Fact]
    public void GameLibraryParser_SelectGameExecutables_FiltersHelpersAndRanksMainBinaryFirst()
    {
        string dir = @"D:\SteamLibrary\steamapps\common\ApexLegends";
        string[] exes =
        [
            $@"{dir}\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe",
            $@"{dir}\CrashReportClient.exe",
            $@"{dir}\vcredist_x64.exe",
            $@"{dir}\Binaries\Win64\ApexLegends-Win64-Shipping.exe",
            $@"{dir}\r5apex.exe",
        ];

        var picked = GameLibraryParser.SelectGameExecutables(dir, "Apex Legends", exes);

        Assert.DoesNotContain("EasyAntiCheat_EOS_Setup.exe", picked, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("CrashReportClient.exe", picked, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("vcredist_x64.exe", picked, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("ApexLegends-Win64-Shipping.exe", picked[0]);
    }

    [Fact]
    public void GameLibraryParser_RejectsPlaceholderAppIdAndUtilityTools()
    {
        const string wattAcf = """
            "AppState"
            {
                "appid"         "1634680"
                "name"          "appid_1634680"
                "installdir"    "Watt Toolkit"
            }
            """;
        Assert.Null(GameLibraryParser.ParseSteamAppManifestAcf(wattAcf));

        const string placeholderOnly = """
            "AppState"
            {
                "appid"         "9999999"
                "name"          "appid_9999999"
                "installdir"    "SomeFolder"
            }
            """;
        Assert.Null(GameLibraryParser.ParseSteamAppManifestAcf(placeholderOnly));

        Assert.True(GameLibraryParser.IsIgnoredExecutableName("Ficket.exe"));
        Assert.True(GameLibraryParser.IsIgnoredExecutableName("wallpaper64.exe"));
        Assert.False(GameLibraryParser.IsIgnoredExecutableName("cs2.exe"));
    }

    [Fact]
    public void OpenMouseGameCatalog_ParsesJsonAndMatchesExecutables()
    {
        const string json = """
            [
              {
                "id": "custom-fps",
                "name": "Custom Tactical FPS",
                "executables": ["customfps_x64.exe", "customfps.exe"],
                "icon": "https://cdn.openmouse.app/icons/custom-fps.png"
              }
            ]
            """;

        var parsed = OpenMouseGameCatalog.ParseGamesJson(json);
        Assert.Single(parsed);
        Assert.Equal("Custom Tactical FPS", parsed[0].Name);

        var merged = OpenMouseGameCatalog.MergeWithBuiltIn(parsed);
        Assert.NotNull(OpenMouseGameCatalog.MatchByProcessName("cs2.exe", merged));
        Assert.NotNull(OpenMouseGameCatalog.MatchByProcessName("customfps_x64.exe", merged));

        var dirHit = OpenMouseGameCatalog.MatchByExecutables(
            [@"D:\Games\CS2\game\bin\win64\cs2.exe", @"D:\Games\CS2\unins000.exe"], merged);
        Assert.NotNull(dirHit);
        Assert.Equal("Counter-Strike 2", dirHit!.Value.Entry.Name);
        Assert.Contains("cs2.exe", dirHit.Value.MatchedExes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void GameProfileRuleMatcher_ApplyExitRetention_KeepsFullscreenUntilWindowExits()
    {
        var fsTargets = new Dictionary<string, string> { ["razer:1532-00C1"] = "preset:fps" };
        var deskTargets = new Dictionary<string, string> { ["razer:1532-00C1"] = "preset:desk" };
        var gameTargets = new Dictionary<string, string> { ["razer:1532-00C1"] = "slot:1" };

        var latchedFs = new GameProfileResolution(
            ActiveSwitchReason.ExclusiveFullscreen, "__fullscreen__", "独占全屏通用配置", fsTargets);
        var desktopCandidate = new GameProfileResolution(
            ActiveSwitchReason.DesktopDefault, "__desktop__", "桌面默认配置", deskTargets);
        var newGameCandidate = new GameProfileResolution(
            ActiveSwitchReason.ProcessRule, "rule:cs2", "CS2", gameTargets);

        // 1. Alt+Tab 切回桌面，但全屏窗口仍未退出且开启保持 -> 维持全屏配置不切回默认
        var held = GameProfileRuleMatcher.ApplyExitRetention(desktopCandidate, latchedFs, keepUntilExit: true, isLatchedAlive: true);
        Assert.Equal("__fullscreen__", held.RuleKey);

        // 2. 直接切到另一款命中规则的游戏 -> 立即切换至新游戏规则
        var switchedToGame = GameProfileRuleMatcher.ApplyExitRetention(newGameCandidate, latchedFs, keepUntilExit: true, isLatchedAlive: true);
        Assert.Equal("rule:cs2", switchedToGame.RuleKey);

        // 3. 全屏窗口真正关闭退出后 -> 恢复桌面默认配置
        var exited = GameProfileRuleMatcher.ApplyExitRetention(desktopCandidate, latchedFs, keepUntilExit: true, isLatchedAlive: false);
        Assert.Equal("__desktop__", exited.RuleKey);

        // 4. 用户关闭保持开关 -> 失焦即恢复桌面默认配置
        var focusLost = GameProfileRuleMatcher.ApplyExitRetention(desktopCandidate, latchedFs, keepUntilExit: false, isLatchedAlive: true);
        Assert.Equal("__desktop__", focusLost.RuleKey);
    }

    [Fact]
    public void DeviceImageRuleMatcher_ParsesMergesAndMatchesByPriority()
    {
        const string baseJson = """
            {
              "version": 1,
              "rules": [
                { "id": "beast_x_v2", "vidPid": "36a7:a856", "preset": "wlmouse_beast_x_v2" },
                { "id": "beast_g", "vidPid": "36a7:a860", "preset": "wlmouse_beast_g" },
                { "id": "wlmouse_fam", "vid": "36a7", "preset": "wlmouse_beast_x_v2" },
                { "id": "mouse_def", "kind": "Mouse", "preset": "razer_viper_v3_pro" }
              ]
            }
            """;

        const string userJson = """
            {
              "version": 2,
              "rules": [
                { "id": "custom_new_mouse", "vidPid": "36a7:a999", "preset": "wlmouse_beast_g", "cloudUrl": "https://cdn.jsdelivr.net/gh/example/beast-new.png" }
              ]
            }
            """;

        var baseM = DeviceImageRuleMatcher.ParseManifest(baseJson);
        var userM = DeviceImageRuleMatcher.ParseManifest(userJson);
        var merged = DeviceImageRuleMatcher.MergeManifests(baseM, userM);

        // 1. Exact VID:PID beats family & category
        var beastG = new PeripheralDevice { Id = "wlmouse:36A7-A860", Name = "WLMOUSE BEAST G", Kind = DeviceKind.Mouse, Provider = "CompX", Connected = true };
        var resG = DeviceImageRuleMatcher.MatchRule(beastG, merged.Rules);
        Assert.Equal("wlmouse_beast_g", resG.PresetId);
        Assert.True(resG.IsExactModelMatch);

        // 2. Merged user rule brings CloudUrl for newly added VID:PID without touching code
        var newDev = new PeripheralDevice { Id = "wlmouse:36A7-A999", Name = "WLmouse New Model", Kind = DeviceKind.Mouse, Provider = "CompX", Connected = true };
        var resNew = DeviceImageRuleMatcher.MatchRule(newDev, merged.Rules);
        Assert.True(resNew.IsExactModelMatch);
        Assert.Equal("https://cdn.jsdelivr.net/gh/example/beast-new.png", resNew.CloudUrl);

        // 3. Unknown PID under known VID falls back to brand family (IsExactModelMatch = false so cloud auto-pull still runs)
        var unmappedSameBrand = new PeripheralDevice { Id = "wlmouse:36A7-1234", Name = "WLmouse Unknown", Kind = DeviceKind.Mouse, Provider = "CompX", Connected = true };
        var resUnmapped = DeviceImageRuleMatcher.MatchRule(unmappedSameBrand, merged.Rules);
        Assert.Equal("wlmouse_beast_x_v2", resUnmapped.PresetId);
        Assert.False(resUnmapped.IsExactModelMatch);
    }
}
