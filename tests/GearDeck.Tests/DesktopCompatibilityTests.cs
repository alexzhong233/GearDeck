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
}
