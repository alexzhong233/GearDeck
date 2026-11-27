using System.Text.Json;
using GearDeck.Core.Models;
using GearDeck.Core.Services;
using GearDeck.Desktop.Services;
using Xunit;

public class DesktopCompatibilityTests
{
    [Fact]
    public void LegacySettingsJson_DeserializesCleanlyIntoDesktopAppSettings()
    {
        const string legacyJson = """
        {
          "WidgetVisible": true,
          "WidgetEmbedded": false,
          "WidgetOffsetX": 24,
          "WidgetAlignment": "left",
          "WidgetGroupId": "builtin:keyboard",
          "PollIntervalSeconds": 2,
          "GameAutoPollIntervalMs": 9000,
          "LowBatteryThreshold": 20,
          "CloseToTray": true,
          "LaunchMinimizedToTray": true,
          "GitHubMirrorUrl": "https://ghfast.top/",
          "UpdateRepoSlug": "alexzhong233/GearDeck",
          "LastKnownBatteries": {
            "mloong:A8A5-2255": {
              "BatteryPercent": 82,
              "UpdatedUtc": "2026-11-26T10:00:00Z"
            }
          },
          "SensitivityPrefs": {
            "BaseGameId": "cs2",
            "BaseSensitivity": 1.25,
            "TargetGameId": "valorant"
          }
        }
        """;

        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson);
        Assert.NotNull(settings);
        Assert.False(settings!.WidgetEmbedded);
        Assert.Equal(24, settings.WidgetOffsetX);
        Assert.Equal("left", settings.WidgetAlignment);
        Assert.Equal("builtin:keyboard", settings.WidgetGroupId);
        Assert.True(settings.LastKnownBatteries.TryGetValue("mloong:A8A5-2255", out var bat));
        Assert.Equal(82, bat!.BatteryPercent);
        Assert.Equal(1.25, settings.SensitivityPrefs.BaseSensitivity);

        // 验证 Clamp 与默认兜底边界
        Assert.Equal(5, Math.Clamp(settings.PollIntervalSeconds, 5, 300));
        Assert.Equal(5000, Math.Clamp(settings.GameAutoPollIntervalMs, 250, 5000));
    }

    [Fact]
    public void DesktopMetricsEngine_Exposes12MetricsAndFormatsAll()
    {
        Assert.Equal(12, DesktopMetricsEngine.AllMetrics.Count);
        var snap = new SystemMetricsSnapshot
        {
            CpuPercent = 18.5,
            CpuFreqGhz = 4.2,
            RamPercent = 48.0,
            MemUsedGb = 15.4,
            MemTotalGb = 32.0,
            GpuPercent = 22.0,
            GpuTempC = 51,
            VramUsedGb = 4.5,
            VramTotalGb = 16.0,
            NetDownBytesPerSec = 1_500_000,
            NetUpBytesPerSec = 250_000,
            DiskActivePercent = 3.0,
            SystemDriveLabel = "C:",
            DiskSpaceUsedPercent = 60.0,
            DiskFreeGb = 250.0,
            Uptime = TimeSpan.FromMinutes(90),
        };

        foreach (var def in DesktopMetricsEngine.AllMetrics)
        {
            Assert.False(string.IsNullOrWhiteSpace(DesktopMetricsEngine.FormatTaskbar(def.Id, snap)));
            Assert.False(string.IsNullOrWhiteSpace(DesktopMetricsEngine.FormatDetailed(def.Id, snap)));
        }
        Assert.Equal("", DesktopMetricsEngine.FormatTaskbar("NONE", snap));
    }

    [Fact]
    public void GroupIconCatalog_ResolvesDefaultAndCustomGlyphs()
    {
        var mouseCfg = new TaskbarGroupConfig { GroupId = "builtin:mouse" };
        Assert.Equal("\uE962", GroupIconCatalog.ResolveEffectiveGlyph(mouseCfg, []));

        var customCfg = new TaskbarGroupConfig { GroupId = "custom:1", CustomGlyph = "\uE7FC" };
        Assert.Equal("\uE7FC", GroupIconCatalog.ResolveEffectiveGlyph(customCfg, []));

        var keyboardDev = new PeripheralDevice
        {
            Id = "leobog:258A-010C",
            Name = "LEOBOG Hi98C Pro",
            Kind = DeviceKind.Keyboard,
            Provider = "LEOBOG",
        };
        Assert.Equal("\uE765", GroupIconCatalog.ResolveDefaultGlyph("custom:kb", [keyboardDev]));
    }

    [Fact]
    public void GroupIconCatalog_UsesOnlyFluentAvaloniaPresentGlyphs()
    {
        var missingGlyphs = new HashSet<string>
        {
            "\uE7F5", "\uE95F", "\uE950", "\uE964", "\uE704", "\uE836", "\uE977", "\uE7F6"
        };
        foreach (var preset in GroupIconCatalog.Presets)
            Assert.DoesNotContain(preset.Glyph, missingGlyphs);
        foreach (var metric in DesktopMetricsEngine.AllMetrics)
            Assert.DoesNotContain(metric.Glyph, missingGlyphs);
    }

    [Fact]
    public void DesktopMetricsEngine_ParsesBraceWrappedMacLoadAvg()
    {
        double pct = DesktopMetricsEngine.ParseMacLoadAvgPercent("{ 4.00 5.45 6.80 }", 8);
        Assert.Equal(50.0, pct, precision: 2);
        Assert.Equal(0.0, DesktopMetricsEngine.ParseMacLoadAvgPercent("invalid", 8));
    }

    [Fact]
    public void MacStatusBarFormatter_BuildsFlatTiledAndCompactTitlesWithoutFakingBattery()
    {
        var mouse = new PeripheralDevice
        {
            Id = "mloong:A8A5-2255",
            Name = "MLOONG MX301 PRO",
            Kind = DeviceKind.Mouse,
            Provider = "MLOONG",
            Connected = true,
            BatteryPercent = 63,
            State = BatteryState.Normal,
        };
        var kbd = new PeripheralDevice
        {
            Id = "leobog:3554-FA09",
            Name = "LEOBOG Hi98C Pro (2.4G)",
            Kind = DeviceKind.Keyboard,
            Provider = "LEOBOG",
            Connected = true,
            BatteryPercent = null,
            State = BatteryState.Unknown,
        };

        var settings = new AppSettings
        {
            MacStatusBarTiled = true,
            TaskbarGroups =
            [
                new TaskbarGroupConfig { GroupId = "builtin:mouse", Visible = true },
                new TaskbarGroupConfig { GroupId = "builtin:keyboard", Visible = true },
            ],
        };

        string tiled = MacStatusBarFormatter.BuildTitle([mouse, kbd], settings);
        Assert.Contains("63%", tiled);
        Assert.Contains("—", tiled); // Unobserved keyboard shows placeholder, never a fake %

        settings.MacStatusBarTiled = false;
        string compact = MacStatusBarFormatter.BuildTitle([mouse, kbd], settings);
        Assert.Equal("GD 63%", compact);
    }

    [Fact]
    public void MacAccessibilityGuard_ReplacesFluentAvaloniaItemsRepeaterPeerSafely()
    {
        var repeater = new FluentAvalonia.UI.Controls.ItemsRepeater();
        MacAccessibilityGuard.InstallSafePeer(repeater);
        var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(repeater);
        Assert.IsType<MacAccessibilityGuard.SafeItemsRepeaterAutomationPeer>(peer);
        var children = peer.GetChildren();
        Assert.NotNull(children);
    }

    [Fact]
    public void MacCommandQ_CancelsShutdownOnlyWhenCloseToTrayEnabledAndNotExiting()
    {
        Assert.True(GearDeck.Desktop.App.ShouldCancelMacShutdown(isMacOs: true, isExiting: false, systemShuttingDown: false, closeToTray: true));
        Assert.False(GearDeck.Desktop.App.ShouldCancelMacShutdown(isMacOs: true, isExiting: true, systemShuttingDown: false, closeToTray: true));
        Assert.False(GearDeck.Desktop.App.ShouldCancelMacShutdown(isMacOs: true, isExiting: false, systemShuttingDown: true, closeToTray: true));
        Assert.False(GearDeck.Desktop.App.ShouldCancelMacShutdown(isMacOs: true, isExiting: false, systemShuttingDown: false, closeToTray: false));
        Assert.False(GearDeck.Desktop.App.ShouldCancelMacShutdown(isMacOs: false, isExiting: false, systemShuttingDown: false, closeToTray: true));
    }

    [Fact]
    public void DeviceImageRuleMatcher_PreservesAssetFileAcrossMergeAndMatch()
    {
        var remote = DeviceImageRuleMatcher.ParseManifest("""
        {
          "version": 5,
          "rules": [
            {
              "id": "logi_gpw",
              "name": "Logitech G PRO Wireless",
              "vidPid": "046d:c088",
              "preset": "logitech_gpx",
              "piperSvg": "logitech-g-pro-wireless.svg",
              "assetFile": "images/abc123.png"
            }
          ],
          "gallery": [
            {
              "id": "g_gpw",
              "name": "GPW",
              "brand": "Logitech",
              "matchHint": "usb:046d:c088",
              "imageUrl": "https://cdn.jsdelivr.net/gh/libratbag/piper@master/data/svgs/logitech-g-pro-wireless.svg",
              "isPiperSvg": true,
              "assetFile": "images/abc123.png"
            }
          ]
        }
        """);

        var localUser = DeviceImageRuleMatcher.ParseManifest("""
        {
          "version": 5,
          "rules": [
            {
              "id": "logi_gpw",
              "name": "Logitech G PRO Wireless",
              "vidPid": "046d:c088",
              "preset": "logitech_gpx",
              "piperSvg": "logitech-g-pro-wireless.svg"
            }
          ]
        }
        """);

        var merged = DeviceImageRuleMatcher.MergeManifests(remote, localUser);
        var dev = new PeripheralDevice
        {
            Id = "logi:046D-C088",
            VendorId = 0x046D,
            ProductId = 0xC088,
            Name = "G PRO Wireless",
            Kind = DeviceKind.Mouse,
            Provider = "Logitech",
        };
        var matched = DeviceImageRuleMatcher.MatchRule(dev, merged.Rules);
        Assert.Equal("images/abc123.png", matched.AssetFile);
        Assert.Equal("images/abc123.png", merged.Gallery[0].AssetFile);
    }

    [Fact]
    public void DeviceImageService_BuildDownloadCandidates_PrefersAssetsBranchAndSupportsMirror()
    {
        string testUrl = "https://cdn.jsdelivr.net/gh/libratbag/piper@master/data/svgs/logitech-g-pro-wireless.svg";
        string expectedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(testUrl)))[..16].ToLowerInvariant();
        Assert.Equal(expectedHash, DeviceImageService.ComputeAssetHash16(testUrl));

        var mirrored = DeviceImageService.BuildDownloadCandidates(
            testUrl,
            assetFile: null,
            repoSlug: "alexzhong233/GearDeck",
            mirrorUrl: "https://ghfast.top/");

        Assert.Equal(3, mirrored.Count);
        Assert.Equal($"https://ghfast.top/https://raw.githubusercontent.com/alexzhong233/GearDeck/assets/images/{expectedHash}.png", mirrored[0]);
        Assert.Equal($"https://cdn.jsdelivr.net/gh/alexzhong233/GearDeck@assets/images/{expectedHash}.png", mirrored[1]);
        Assert.Equal(testUrl, mirrored[2]);

        var direct = DeviceImageService.BuildDownloadCandidates(
            testUrl,
            assetFile: "images/custom_asset.png",
            repoSlug: "alexzhong233/GearDeck",
            mirrorUrl: "");

        Assert.Equal(3, direct.Count);
        Assert.Equal("https://cdn.jsdelivr.net/gh/alexzhong233/GearDeck@assets/images/custom_asset.png", direct[0]);
        Assert.Equal("https://raw.githubusercontent.com/alexzhong233/GearDeck/assets/images/custom_asset.png", direct[1]);
        Assert.Equal(testUrl, direct[2]);
    }
}
