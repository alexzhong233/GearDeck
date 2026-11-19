using GearDeck.Core.Models;
using GearDeck.Core.Providers;
using GearDeck.Core.Services;
using Xunit;

namespace GearDeck.Tests;

public class V102UpgradeTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GearDeck.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find GearDeck.slnx root.");
    }

    [Fact]
    public void Logitech_1B0C_HitsAnalogSwitchEncodeDecode_MatchesLiveProX3Capture()
    {
        // Live PRO X3 SUPERSTRIKE fn0 => 00-03-28-14-14-01
        byte[] capReply = [0x00, 0x03, 0x28, 0x14, 0x14, 0x01];
        var (mask, maxAct, maxRt, maxHap) = LogitechProvider.ParseAnalogCapabilitiesReply(capReply);
        Assert.Equal(0x03, mask);
        Assert.Equal(10, maxAct);
        Assert.Equal(5, maxRt);
        Assert.Equal(5, maxHap);

        // Live fn2(btn=0) => 00-10-0D-10 (Actuation=4, RT=true/3, Haptics=4)
        byte[] btnReply = [0x00, 0x10, 0x0D, 0x10];
        var cfg = LogitechProvider.ParseAnalogButtonReply(0, btnReply, maxAct, maxRt, maxHap);
        Assert.NotNull(cfg);
        Assert.Equal(4, cfg!.Actuation);
        Assert.True(cfg.RapidTriggerEnabled);
        Assert.Equal(3, cfg.RapidTrigger);
        Assert.True(cfg.HapticsEnabled);
        Assert.Equal(4, cfg.Haptics);

        byte[] encoded = LogitechProvider.BuildAnalogWritePayload(1, cfg with { RapidTriggerEnabled = false }, maxAct, maxRt, maxHap);
        Assert.Equal(new byte[] { 0x01, 0x10, 0x0C, 0x10 }, encoded);
    }

    [Fact]
    public void AulaFfa8_Sshs20_AxisConfig_UsesWireValueTwoForEnabledAndOneForDisabled()
    {
        // rx[0]=0A, rx[1]=A1, rx[2]=9, rx[3]=0(status), rx[4..12]=AxisCfg(btn=1, maxAct=10, act=4, rapidSw=2(ON), maxRt=5, rt=3, maxHap=100, hap=65, hapSw=2(ON))
        byte[] rx = [0x0A, 0xA1, 9, 0, 1, 10, 4, 2, 5, 3, 100, 65, 2];
        var parsed = AulaFfa8Protocol.ParseAxisCfgReply(0, rx);
        Assert.NotNull(parsed);
        Assert.Equal(4, parsed!.Actuation);
        Assert.True(parsed.RapidTriggerEnabled);
        Assert.Equal(3, parsed.RapidTrigger);
        Assert.True(parsed.HapticsEnabled);
        Assert.Equal(65, parsed.Haptics);

        byte[] offWire = AulaFfa8Protocol.BuildAxisCfgWritePayload(
            2,
            new AnalogButtonConfig(1, 2, false, 1, false, 0),
            rx.AsSpan(4, 9).ToArray());
        Assert.Equal(2, offWire[0]); // hwBtnId=2 (Right)
        Assert.Equal(2, offWire[2]); // actuation=2
        Assert.Equal(1, offWire[3]); // rapidSwitch OFF == 1
        Assert.Equal(1, offWire[8]); // feedbackSwitch OFF == 1

        Assert.Equal("AULA 昆仑 L5 Ultra", AulaFfa8Protocol.ResolveModelName(0x38A6, 0x2F1A, 0x2F0C));
    }

    [Fact]
    public void Razer_DecodeLodState_DistinguishesZeroUninitializedStoreFromExplicitSetting()
    {
        // 0x85 returns all zeros on uncalibrated Focus Pro medium store while cache holds Medium(1)
        var uncalMed = RazerProvider.DecodeLodState([0x00, 0x04, 0x01, 0x00], [0x00, 0x04, 0x00, 0x00, 0x00], explicitSymLod: 1);
        Assert.Equal(1, uncalMed.SymLod);
        Assert.False(uncalMed.AsymEnabled);

        // Explicit High(2) in 0x85[2] always takes precedence
        var explicitHigh = RazerProvider.DecodeLodState([0x00, 0x04, 0x01, 0x00], [0x00, 0x04, 0x02, 0x00, 0x00], explicitSymLod: 1);
        Assert.Equal(2, explicitHigh.SymLod);

        // Asymmetric enabled (0x8B[2] == 0x04) decodes 1-based lift/landing levels
        var asym = RazerProvider.DecodeLodState([0x00, 0x04, 0x04, 0x00], [0x00, 0x04, 0x01, 14, 7]);
        Assert.True(asym.AsymEnabled);
        Assert.Equal(15, asym.LiftOff);
        Assert.Equal(8, asym.Landing);
    }

    [Fact]
    public void Flydigi_BuildApex5TriggerPresetPair_SendsSeparateLeftAndRightPacketsWithApplyFlag()
    {
        var (left, right) = FlydigiProvider.BuildApex5TriggerPresetPair(0x03, preset: 2); // Sniper -> mode 3

        Assert.Equal(32, left.Length);
        Assert.Equal(0x03, left[0]);
        Assert.Equal(0x5A, left[1]);
        Assert.Equal(0xA5, left[2]);
        Assert.Equal(81, left[3]);   // cmd = SET_MOTOR
        Assert.Equal(10, left[4]);   // payload len = 10
        Assert.Equal(1, left[5]);    // apply immediately
        Assert.Equal(1, left[6]);    // left side
        Assert.Equal(3, left[7]);    // Space Station Sniper mode

        Assert.Equal(2, right[6]);   // right side (never 3=Both!)
        Assert.Equal(3, right[7]);
    }

    [Fact]
    public void SonyInzone_FormatDualEarSummary_FormatsCompactAndDetailedSeparately()
    {
        var buds = new PeripheralDevice
        {
            Id = "sony:0EC2",
            Name = "Sony INZONE Buds",
            Kind = DeviceKind.Headset,
            Provider = "Sony",
            Connected = true,
            LeftEarPercent = 88,
            LeftEarCharging = false,
            RightEarPercent = 82,
            RightEarCharging = true,
            CaseBatteryPercent = 64,
        };

        Assert.Equal("L88% R82%⚡", buds.FormatDualEarSummary(compact: true));
        Assert.Equal("L 88% · R 82%⚡ · 盒 64%", buds.FormatDualEarSummary(compact: false));
    }

    [Fact]
    public void VendorDriverCatalog_ResolvesExpectedLocalAndWebDrivers()
    {
        var logi = VendorDriverCatalog.Resolve(new PeripheralDevice { Id = "logi:C54F:01", Name = "PRO X3", Kind = DeviceKind.Mouse, Provider = "Logitech", VendorId = 0x046D, ProductId = 0xC54F });
        Assert.NotNull(logi.Local);
        Assert.Null(logi.Web);

        var razer = VendorDriverCatalog.Resolve(new PeripheralDevice { Id = "razer:00C1", Name = "Viper V3 Pro", Kind = DeviceKind.Mouse, Provider = "Razer", VendorId = 0x1532, ProductId = 0x00C1 });
        Assert.NotNull(razer.Local);
        Assert.Equal("https://synapse.razer.com", razer.Web?.Url);

        var rog = VendorDriverCatalog.Resolve(new PeripheralDevice { Id = "rog:1B7A", Name = "ROG 游侠 2 RX 98 无线", Kind = DeviceKind.Keyboard, Provider = "ROG", VendorId = 0x0B05, ProductId = 0x1B7A });
        Assert.NotNull(rog.Local);
        Assert.Equal("https://gearlink.asus.com", rog.Web?.Url);

        var am = VendorDriverCatalog.Resolve(new PeripheralDevice { Id = "am:0703", Name = "Angry Miao AM Infinity .97", Kind = DeviceKind.Mouse, Provider = "AngryMiao", VendorId = 0x0E8D, ProductId = 0x0703 });
        Assert.NotNull(am.Local);
        Assert.Equal("https://diy.angrymiao.com/device-connect/", am.Web?.Url);

        var aula = VendorDriverCatalog.Resolve(new PeripheralDevice { Id = "aula:38A6:2F1A", Name = "AULA 昆仑 L5 Ultra", Kind = DeviceKind.Mouse, Provider = "AULA", VendorId = 0x38A6, ProductId = 0x2F1A });
        Assert.Null(aula.Local);
        Assert.Equal("https://hub.aulacn.com", aula.Web?.Url);
    }

    [Fact]
    public void GameProfileRuleMatcher_SupportsDefaultPreEntryRollbackAndExplicitKeep()
    {
        var desk = GameProfileRuleMatcher.Resolve(
            new GameProfileMatchContext("explorer.exe", "Desktop", false),
            [],
            new Dictionary<string, string>(),
            enableFullscreenFallback: false,
            new Dictionary<string, string>(),
            restoreDesktopOnExit: true);

        Assert.Equal(ActiveSwitchReason.DesktopDefault, desk.Reason);

        Assert.True(GameProfileRuleMatcher.ParseTarget(GameProfileRuleMatcher.RestorePreviousTargetId).RestorePrevious);
        Assert.True(GameProfileRuleMatcher.ParseTarget(GameProfileRuleMatcher.KeepCurrentTargetId).KeepCurrent);
        Assert.True(GameProfileRuleMatcher.ParseTarget("").IsEmpty);
    }

    [Fact]
    public void MacroModels_SupportsPlayOnceAndExtraMouseButtons()
    {
        var once = new OnboardMacroDefinition
        {
            Name = "SingleTap",
            LoopMode = MacroLoopMode.PlayOnce,
            RepeatCount = 5,
            Steps = [new MacroStep(HidKeyCatalog.MouseXButton1Code, true, 10), new MacroStep(HidKeyCatalog.MouseXButton1Code, false, 10)],
        };

        byte[] hwBuf = Incott09Protocol.EncodeMacroBuffer(0, once);
        Assert.Equal(2, hwBuf[1]);
        Assert.Equal(1, hwBuf[2]); // PlayOnce always encodes 1 hardware cycle regardless of RepeatCount field
        Assert.Equal("鼠标侧键4 (后退)", HidKeyCatalog.DescribeKeyCode(HidKeyCatalog.MouseXButton1Code));
    }

    [Fact]
    public void DeviceImageManifest_BundledV3RulesMatchAllCoreDevicesWithOnlineUrls()
    {
        string path = Path.Combine(FindRepoRoot(), "src", "GearDeck.App", "Assets", "device-images.json");
        var manifest = DeviceImageRuleMatcher.ParseManifest(File.ReadAllText(path));
        Assert.True(manifest.Version >= 3);

        var targets = new PeripheralDevice[]
        {
            new() { Id = "logi:C54F:01", Name = "Logitech PRO X3 SUPERSTRIKE", Kind = DeviceKind.Mouse, Provider = "Logitech", VendorId = 0x046D, ProductId = 0xC54F },
            new() { Id = "razer:00C1", Name = "Razer Viper V3 Pro", Kind = DeviceKind.Mouse, Provider = "Razer", VendorId = 0x1532, ProductId = 0x00C1 },
            new() { Id = "aula:38A6:2F1A", Name = "AULA 昆仑 L5 Ultra", Kind = DeviceKind.Mouse, Provider = "AULA", VendorId = 0x38A6, ProductId = 0x2F1A },
            new() { Id = "am:0703", Name = "Angry Miao AM Infinity .97", Kind = DeviceKind.Mouse, Provider = "AngryMiao", VendorId = 0x0E8D, ProductId = 0x0703 },
            new() { Id = "rog:1B7A", Name = "ROG 游侠 2 RX 98 无线", Kind = DeviceKind.Keyboard, Provider = "ROG", VendorId = 0x0B05, ProductId = 0x1B7A },
            new() { Id = "sony:0EC2", Name = "Sony INZONE Buds", Kind = DeviceKind.Headset, Provider = "Sony", VendorId = 0x054C, ProductId = 0x0EC2 },
        };

        foreach (var d in targets)
        {
            var m = DeviceImageRuleMatcher.MatchRule(d, manifest.Rules);
            Assert.True(m.IsExactModelMatch, $"Expected exact VID:PID match for {d.Name} ({d.Id})");
            Assert.False(string.IsNullOrWhiteSpace(m.CloudUrl) && string.IsNullOrWhiteSpace(m.PiperSvg), $"Expected online URL for {d.Name}");
        }
    }

    [Fact]
    public void UpdateClient_ParsesInstallerFieldsFromLatestAndGitHubApiJson()
    {
        const string latestJson = """
        {
          "version": "1.0.2",
          "tag": "v1.0.2",
          "downloadUrl": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.2/GearDeck-v1.0.2-win-x64.zip",
          "patchUrl": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.2/GearDeck-patch-v1.0.2-win-x64.zip",
          "installerUrl": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.2/GearDeck-Setup-v1.0.2-win-x64.exe",
          "installerSha256": "deadbeef",
          "installerSize": 42000000
        }
        """;

        var info = UpdateClient.ParseLatestManifestJson(latestJson, "https://ghfast.top/");
        Assert.NotNull(info);
        Assert.Equal("DEADBEEF", info!.InstallerSha256);
        Assert.Equal(42000000, info.InstallerSize);
        Assert.StartsWith("https://ghfast.top/https://github.com/", info.InstallerUrl);

        const string ghApi = """
        {
          "tag_name": "v1.0.2",
          "assets": [
            {
              "name": "GearDeck-Setup-v1.0.2-win-x64.exe",
              "browser_download_url": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.2/GearDeck-Setup-v1.0.2-win-x64.exe",
              "size": 41500000,
              "digest": "sha256:cafe1234"
            },
            {
              "name": "GearDeck-v1.0.2-win-x64.zip",
              "browser_download_url": "https://github.com/alexzhong233/GearDeck/releases/download/v1.0.2/GearDeck-v1.0.2-win-x64.zip",
              "size": 39000000
            }
          ]
        }
        """;

        var apiInfo = UpdateClient.ParseGitHubReleaseApiJson(ghApi, null);
        Assert.NotNull(apiInfo);
        Assert.Equal("CAFE1234", apiInfo!.InstallerSha256);
        Assert.EndsWith("GearDeck-Setup-v1.0.2-win-x64.exe", apiInfo.RawInstallerUrl);
    }
}
