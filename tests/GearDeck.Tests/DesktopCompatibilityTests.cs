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
    public void DesktopMetricsEngine_Exposes13MetricsAndFormatsAll()
    {
        Assert.Equal(13, DesktopMetricsEngine.AllMetrics.Count);
        var snap = new SystemMetricsSnapshot
        {
            CpuPercent = 18.5,
            CpuFreqGhz = 4.2,
            CpuTempC = 62,
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

        var snap = new SystemMetricsSnapshot
        {
            CpuPercent = 24.0,
            RamPercent = 52.0,
            NetDownBytesPerSec = 1_500_000,
            NetUpBytesPerSec = 250_000,
        };
        settings.MacStatusBarTiled = true;
        settings.MonitorEnabled = true;
        settings.MonitorTopMetric = "CPU";
        settings.MonitorBottomMetric = "RAM";
        string withDual = MacStatusBarFormatter.BuildTitle([mouse, kbd], settings, snap);
        Assert.Contains("CPU 24%", withDual);
        Assert.Contains("RAM 52%", withDual);

        settings.MonitorBottomMetric = "NONE";
        string withSingle = MacStatusBarFormatter.BuildTitle([mouse, kbd], settings, snap);
        Assert.Contains("CPU 24%", withSingle);
        Assert.DoesNotContain("RAM", withSingle);

        // Hiding all groups should not force-restore mouse when groups are explicitly configured
        settings.TaskbarGroups.ForEach(g => g.Visible = false);
        settings.MonitorEnabled = false;
        Assert.Equal("GearDeck", MacStatusBarFormatter.BuildTitle([mouse, kbd], settings, snap));

        // Custom group glyph maps cleanly
        settings.TaskbarGroups[0].Visible = true;
        settings.TaskbarGroups[0].CustomGlyph = "\uE734";
        string customGlyphTitle = MacStatusBarFormatter.BuildTitle([mouse], settings);
        Assert.Contains("★ 63%", customGlyphTitle);
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

    [Fact]
    public void InstalledGameScanner_ExtractExeIcon_CachesValidPngOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string sampleExe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (!File.Exists(sampleExe))
            return;

        string? pngPath = InstalledGameScanner.ExtractExeIcon(sampleExe);
        Assert.False(string.IsNullOrWhiteSpace(pngPath));
        Assert.True(File.Exists(pngPath));
        Assert.EndsWith(".png", pngPath!, StringComparison.OrdinalIgnoreCase);

        byte[] bytes = File.ReadAllBytes(pngPath!);
        Assert.True(bytes.Length > 100);
        Assert.Equal((byte)0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }

    [Fact]
    public void InstalledGameScanner_ExtractExeIcon_CachesValidPngFromMacAppBundle()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string? sampleApp = new[] { "/System/Applications/Calculator.app", "/System/Applications/Notes.app" }
            .FirstOrDefault(Directory.Exists);
        if (sampleApp == null)
            return;

        string? pngPath = InstalledGameScanner.ExtractExeIcon(sampleApp);
        Assert.False(string.IsNullOrWhiteSpace(pngPath));
        Assert.True(File.Exists(pngPath));
        Assert.EndsWith(".png", pngPath!, StringComparison.OrdinalIgnoreCase);

        byte[] bytes = File.ReadAllBytes(pngPath!);
        Assert.True(bytes.Length > 100);
        Assert.Equal((byte)0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }

    [Fact]
    public void MacrosView_ParseImportedMacrosJson_SupportsSingleArrayAndLegacyBundle()
    {
        const string singleJson = """
        {
          "Name": "压枪连点",
          "LoopMode": 0,
          "RepeatCount": 1,
          "Steps": [
            { "KeyCode": 240, "IsPress": true, "DelayMs": 20 }
          ]
        }
        """;
        var single = GearDeck.Desktop.Views.MacrosView.ParseImportedMacrosJson(singleJson);
        Assert.Single(single);
        Assert.Equal("压枪连点", single[0].Name);
        Assert.Single(single[0].Steps);

        const string arrayJson = """
        [
          { "Name": "宏A", "LoopMode": 0, "RepeatCount": 1, "Steps": [] },
          { "Name": "宏B", "LoopMode": 1, "RepeatCount": 3, "Steps": [] }
        ]
        """;
        var arr = GearDeck.Desktop.Views.MacrosView.ParseImportedMacrosJson(arrayJson);
        Assert.Equal(2, arr.Count);

        const string bundleJson = """
        {
          "Version": 1,
          "Macros": [
            { "Name": "旧包宏", "LoopMode": 0, "RepeatCount": 1, "Steps": [] }
          ]
        }
        """;
        var bundled = GearDeck.Desktop.Views.MacrosView.ParseImportedMacrosJson(bundleJson);
        Assert.Single(bundled);
        Assert.Equal("旧包宏", bundled[0].Name);
    }

    [Fact]
    public void DeviceImageManifest_V7MatchesMloongMx301ProAndLeobogHi98CPro()
    {
        string path = FindDesktopFile("Assets", "device-images.json");
        var manifest = DeviceImageRuleMatcher.ParseManifest(File.ReadAllText(path));
        Assert.True(manifest.Version >= 7);

        var mx301 = new PeripheralDevice
        {
            Id = "mloong:A8A5-2255",
            VendorId = 0xA8A5,
            ProductId = 0x2255,
            Name = "MX301 PRO_2.4G",
            Kind = DeviceKind.Mouse,
            Provider = "MLOONG",
        };
        var m1 = DeviceImageRuleMatcher.MatchRule(mx301, manifest.Rules);
        Assert.True(m1.IsExactModelMatch);
        Assert.Equal("https://hub.mloongplanet.com/old/mx-301.webp", m1.CloudUrl);

        var hi98 = new PeripheralDevice
        {
            Id = "leobog:3554-FA09",
            VendorId = 0x3554,
            ProductId = 0xFA09,
            Name = "LEOBOG Hi98C Pro (2.4G)",
            Kind = DeviceKind.Keyboard,
            Provider = "LEOBOG",
        };
        var m2 = DeviceImageRuleMatcher.MatchRule(hi98, manifest.Rules);
        Assert.True(m2.IsExactModelMatch);
        Assert.Contains("Leobog-Hi98-hi98c-pro", m2.CloudUrl);
    }

    [Fact]
    public void DesktopUi_DoesNotUseMissingFluentAvaloniaGlyphs()
    {
        string desktopRoot = FindDesktopFile();
        string[] banned = ["\\uEB9F", "&#xEB9F;", "\\uE950", "&#xE950;"];
        foreach (string file in Directory.GetFiles(desktopRoot, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
                continue;

            string text = File.ReadAllText(file);
            foreach (string token in banned)
                Assert.DoesNotContain(token, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MacStatusBarFormatter_BuildsStructuredStackedLayout()
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
        var snap = new GearDeck.Desktop.Services.SystemMetricsSnapshot
        {
            NetDownBytesPerSec = 2_400_000,
            NetUpBytesPerSec = 350_000,
        };
        var settings = new AppSettings
        {
            MacStatusBarTiled = true,
            MonitorEnabled = true,
            MonitorTopMetric = "NET_DOWN",
            MonitorBottomMetric = "NET_UP",
            TaskbarGroups = [new TaskbarGroupConfig { GroupId = "builtin:mouse", Visible = true }],
        };

        var dual = MacStatusBarFormatter.BuildLayout([mouse], settings, snap);
        Assert.Single(dual.Devices);
        Assert.Equal("63%", dual.Devices[0].ValueText);
        Assert.StartsWith("↓ ", dual.MonitorTopText);
        Assert.StartsWith("↑ ", dual.MonitorBottomText);

        settings.MonitorBottomMetric = "NONE";
        var single = MacStatusBarFormatter.BuildLayout([mouse], settings, snap);
        Assert.StartsWith("↓ ", single.MonitorTopText);
        Assert.Null(single.MonitorBottomText);

        settings.MonitorEnabled = false;
        var off = MacStatusBarFormatter.BuildLayout([mouse], settings, snap);
        Assert.Null(off.MonitorTopText);
        Assert.Null(off.MonitorBottomText);

        settings.MonitorEnabled = true;
        settings.MonitorCollectionEnabled = false;
        var colOff = MacStatusBarFormatter.BuildLayout([mouse], settings, snap);
        Assert.Null(colOff.MonitorTopText);
        Assert.Null(colOff.MonitorBottomText);

        settings.MonitorCollectionEnabled = true;
        settings.MonitorBottomMetric = "NET_UP";
        settings.TaskbarGroups =
        [
            new TaskbarGroupConfig { GroupId = "builtin:monitor", Visible = true },
            new TaskbarGroupConfig { GroupId = "builtin:mouse", Visible = true },
        ];
        var monitorFirst = MacStatusBarFormatter.BuildLayout([mouse], settings, snap);
        Assert.Equal(2, monitorFirst.Slots.Count);
        Assert.True(monitorFirst.Slots[0].IsMonitor);
        Assert.False(monitorFirst.Slots[1].IsMonitor);
        Assert.True(MacStatusBarFormatter.HasStackedMonitor(monitorFirst));
    }

    [Fact]
    public void MacStatusBarService_RendersRetinaDualLineTemplatePng()
    {
        var layout = new MacStatusBarLayout(
            [
                new MacStatusBarDeviceItem("🖱", "63%", DeviceKind.Mouse, null),
                new MacStatusBarDeviceItem("⌨", "88%", DeviceKind.Keyboard, null),
            ],
            "↓ 2.3M",
            "↑ 342K",
            "GearDeck");

        var rendered = MacStatusBarService.RenderStatusBarTemplatePng(layout);
        Assert.NotNull(rendered);
        Assert.True(rendered!.Value.PngBytes.Length > 200);
        Assert.Equal(22.0, rendered.Value.HeightPt, precision: 2);
        Assert.True(rendered.Value.WidthPt > 40.0);
        Assert.Equal((byte)0x89, rendered.Value.PngBytes[0]);
        Assert.Equal((byte)'P', rendered.Value.PngBytes[1]);

        var darkPreview = MacStatusBarService.RenderStatusBarTemplatePng(
            layout,
            new SkiaSharp.SKColor(246, 246, 246, 255));
        Assert.NotNull(darkPreview);
        Assert.True(darkPreview!.Value.PngBytes.Length > 200);
    }

    [Fact]
    public void SystemMetricsEngine_MacMemoryUsesNativeHostStatistics64()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var snap = DesktopMetricsEngine.Sample(GearDeck.Desktop.Services.MetricScope.Memory);
        Assert.True(snap.MemTotalGb >= 4.0);
        Assert.InRange(snap.MemUsedGb, 0.5, snap.MemTotalGb);
        Assert.InRange(snap.RamPercent, 1.0, 100.0);
    }

    [Fact]
    public void SystemMetricsEngine_MacSamplesCpuAndGpuTempWithoutSudo()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        bool isCi = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));
        var snap = DesktopMetricsEngine.Sample(
            GearDeck.Desktop.Services.MetricScope.CpuFreq |
            GearDeck.Desktop.Services.MetricScope.CpuTemp |
            GearDeck.Desktop.Services.MetricScope.GpuTemp);

        if (!isCi)
        {
            Assert.NotNull(snap.CpuTempC);
            Assert.NotNull(snap.GpuTempC);
        }
        if (snap.CpuTempC is { } ct)
            Assert.InRange(ct, 10, 125);
        if (snap.GpuTempC is { } gt)
            Assert.InRange(gt, 10, 125);

        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
        {
            if (!isCi)
                Assert.NotNull(snap.CpuFreqGhz);
            if (snap.CpuFreqGhz is { } freq)
                Assert.InRange(freq, 0.3, 6.0);
        }
    }

    [Fact]
    public void HwInfoSharedMemoryReader_ParsesAmdRyzenAndDualGpuPreferringDiscrete()
    {
        byte[] blob = BuildMockHwInfoBlob(
            sensors:
            [
                "CPU [#0]: AMD Ryzen 7 7800X3D: Enhanced",
                "GPU [#0]: Intel(R) UHD Graphics 770",
                "GPU [#1]: AMD Radeon RX 7800 XT",
            ],
            readings:
            [
                (1, 0, "CPU (Tctl/Tdie)", "°C", 68.4),
                (6, 0, "Core 0 T0 Effective Clock", "MHz", 4850.0),
                (6, 0, "Core 1 T0 Effective Clock", "MHz", 4750.0),
                (1, 1, "GPU Temperature", "°C", 41.0),
                (7, 1, "GPU Utilization", "%", 8.0),
                (1, 2, "GPU Temperature", "°C", 63.6),
                (7, 2, "GPU Core Load", "%", 74.0),
                (8, 2, "GPU Memory Allocated", "MB", 6144.0),
                (8, 2, "GPU Memory Total", "MB", 16384.0),
            ]);

        var snap = GearDeck.Desktop.Services.HwInfoSharedMemoryReader.Parse(blob);
        Assert.True(snap.Connected);
        Assert.Equal(68, snap.CpuTempC);
        Assert.Equal(4.80, snap.CpuFreqGhz!.Value, precision: 2);
        Assert.Equal("AMD Radeon RX 7800 XT", snap.GpuName);
        Assert.Equal(74.0, snap.GpuPercent!.Value, precision: 1);
        Assert.Equal(64, snap.GpuTempC);
        Assert.Equal(6.0, snap.VramUsedGb!.Value, precision: 2);
        Assert.Equal(16.0, snap.VramTotalGb!.Value, precision: 2);
    }

    [Fact]
    public void HwInfoSharedMemoryReader_ParsesIntelCpuAndNvidiaDgpuIgnoringLocalizedUserLabels()
    {
        byte[] blob = BuildMockHwInfoBlob(
            sensors:
            [
                "CPU [#0]: Intel Core i7-14700K",
                "GPU [#0]: NVIDIA GeForce RTX 4070 Ti",
            ],
            readings:
            [
                (1, 0, "CPU Package", "°C", 59.2),
                (6, 0, "P-core 0 Clock", "MHz", 5300.0),
                (1, 1, "GPU Temperature", "°C", 54.8),
                (7, 1, "GPU Core Load", "%", 42.0),
                (8, 1, "GPU D3D Memory Dedicated", "MB", 4096.0),
            ],
            userLabelPrefix: "自定义译名 ");

        var snap = GearDeck.Desktop.Services.HwInfoSharedMemoryReader.Parse(blob);
        Assert.True(snap.Connected);
        Assert.Equal(59, snap.CpuTempC);
        Assert.Equal(5.30, snap.CpuFreqGhz!.Value, precision: 2);
        Assert.Equal("NVIDIA GeForce RTX 4070 Ti", snap.GpuName);
        Assert.Equal(55, snap.GpuTempC);
        Assert.Equal(42.0, snap.GpuPercent!.Value, precision: 1);
        Assert.Equal(4.0, snap.VramUsedGb!.Value, precision: 2);
    }

    [Fact]
    public void SystemMetricsEngine_SelectPrimaryGpuByLuidSeparatesIgpuAndDgpu()
    {
        const double Gb = 1073741824.0;
        var dgpu = new GearDeck.Desktop.Services.GpuAdapterInfo(
            "AMD Radeon RX 7800 XT", "luid_0x00000000_0x00010002", 0x1002, 16.0, 16.0, true);
        var igpu = new GearDeck.Desktop.Services.GpuAdapterInfo(
            "Intel(R) UHD Graphics 770", "luid_0x00000000_0x00010001", 0x8086, 0.125, 16.0, false);

        // 场景1：独显活跃时只统计独显 3D 和显存，不累加集显
        var active = GearDeck.Desktop.Services.SystemMetricsEngine.SelectPrimaryGpuByLuid(
            new Dictionary<string, double> { [dgpu.LuidKey] = 45.0, [igpu.LuidKey] = 15.0 },
            new Dictionary<string, double> { [dgpu.LuidKey] = 6.0 * Gb, [igpu.LuidKey] = 0.0 },
            new Dictionary<string, double> { [igpu.LuidKey] = 1.5 * Gb },
            [dgpu, igpu]);
        Assert.Equal("AMD Radeon RX 7800 XT", active.GpuName);
        Assert.Equal(45.0, active.GpuPct!.Value, precision: 1);
        Assert.Equal(6.0, active.VramUsedGb!.Value, precision: 2);
        Assert.Equal(16.0, active.VramTotalGb!.Value, precision: 1);

        // 场景2：独显休眠 (<1%) 且集显高负载 (>=5%) 时切换到集显并回退 Shared Usage
        var igpuOnly = GearDeck.Desktop.Services.SystemMetricsEngine.SelectPrimaryGpuByLuid(
            new Dictionary<string, double> { [dgpu.LuidKey] = 0.0, [igpu.LuidKey] = 28.0 },
            new Dictionary<string, double> { [igpu.LuidKey] = 0.0 },
            new Dictionary<string, double> { [igpu.LuidKey] = 2.5 * Gb },
            [dgpu, igpu]);
        Assert.Equal("Intel(R) UHD Graphics 770", igpuOnly.GpuName);
        Assert.Equal(28.0, igpuOnly.GpuPct!.Value, precision: 1);
        Assert.Equal(2.5, igpuOnly.VramUsedGb!.Value, precision: 2);
        Assert.Equal(16.0, igpuOnly.VramTotalGb!.Value, precision: 1);
    }

    [Fact]
    public void SystemMetricsEngine_FromMetricIdsRespectsSelectedSubset()
    {
        var scope = SystemMetricsEngine.FromMetricIds("CPU", "RAM", "NET_DOWN");
        Assert.True(scope.HasFlag(GearDeck.Desktop.Services.MetricScope.Cpu));
        Assert.True(scope.HasFlag(GearDeck.Desktop.Services.MetricScope.Memory));
        Assert.True(scope.HasFlag(GearDeck.Desktop.Services.MetricScope.Network));
        Assert.False(scope.HasFlag(GearDeck.Desktop.Services.MetricScope.CpuFreq));
        Assert.False(scope.HasFlag(GearDeck.Desktop.Services.MetricScope.CpuTemp));
        Assert.False(scope.HasFlag(GearDeck.Desktop.Services.MetricScope.Gpu));
        Assert.False(scope.HasFlag(GearDeck.Desktop.Services.MetricScope.GpuTemp));
        Assert.False(scope.HasFlag(GearDeck.Desktop.Services.MetricScope.Vram));
    }

    private static byte[] BuildMockHwInfoBlob(
        string[] sensors,
        (int Type, uint SensorIdx, string LabelOrig, string Unit, double Value)[] readings,
        string userLabelPrefix = "")
    {
        const int headerSize = 44;
        const int sensorSize = 264;
        const int readingSize = 312;
        int sensorOffset = headerSize;
        int readingOffset = sensorOffset + sensors.Length * sensorSize;
        int totalSize = readingOffset + readings.Length * readingSize;
        byte[] buf = new byte[totalSize];

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 0x53695748);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20, 4), (uint)sensorOffset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24, 4), sensorSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), (uint)sensors.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(32, 4), (uint)readingOffset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(36, 4), readingSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40, 4), (uint)readings.Length);

        for (int i = 0; i < sensors.Length; i++)
        {
            int pos = sensorOffset + i * sensorSize;
            byte[] b = System.Text.Encoding.UTF8.GetBytes(sensors[i]);
            Array.Copy(b, 0, buf, pos + 8, Math.Min(b.Length, 127));
        }

        for (int i = 0; i < readings.Length; i++)
        {
            int pos = readingOffset + i * readingSize;
            var r = readings[i];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), r.Type);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos + 4, 4), r.SensorIdx);
            byte[] lb = System.Text.Encoding.UTF8.GetBytes(r.LabelOrig);
            Array.Copy(lb, 0, buf, pos + 12, Math.Min(lb.Length, 127));
            if (!string.IsNullOrEmpty(userLabelPrefix))
            {
                byte[] ulb = System.Text.Encoding.UTF8.GetBytes(userLabelPrefix + r.LabelOrig);
                Array.Copy(ulb, 0, buf, pos + 140, Math.Min(ulb.Length, 127));
            }
            byte[] ub = System.Text.Encoding.UTF8.GetBytes(r.Unit);
            Array.Copy(ub, 0, buf, pos + 268, Math.Min(ub.Length, 15));
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
                buf.AsSpan(pos + 284, 8), BitConverter.DoubleToInt64Bits(r.Value));
        }
        return buf;
    }

    [Fact]
    public void AppSettings_MonitorUncheckActionDefaultsToAskAndRoundTrips()
    {
        var def = new AppSettings();
        Assert.Equal("ask", def.MonitorUncheckAction);

        def.MonitorUncheckAction = "disable_all";
        string json = System.Text.Json.JsonSerializer.Serialize(def);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.Equal("disable_all", restored!.MonitorUncheckAction);
    }

    private static string FindDesktopFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GearDeck.slnx")))
            {
                string root = Path.Combine(dir.FullName, "src", "GearDeck.Desktop");
                return relative.Length == 0 ? root : Path.Combine([root, .. relative]);
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find GearDeck.slnx root.");
    }
}
