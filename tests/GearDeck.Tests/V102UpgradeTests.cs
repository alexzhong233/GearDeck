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
        Assert.Null(am.Local);
        Assert.Equal("https://ammaster.angrymiao.com/mouse/devices", am.Web?.Url);

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
    public void DeviceImageManifest_BundledV4RulesMatchAllCoreDevicesWithSingleMouseUrls()
    {
        string path = Path.Combine(FindRepoRoot(), "src", "GearDeck.Desktop", "Assets", "device-images.json");
        var manifest = DeviceImageRuleMatcher.ParseManifest(File.ReadAllText(path));
        Assert.True(manifest.Version >= 4);

        var targets = new PeripheralDevice[]
        {
            new() { Id = "logi:C54F:01", Name = "Logitech PRO X3 SUPERSTRIKE", Kind = DeviceKind.Mouse, Provider = "Logitech", VendorId = 0x046D, ProductId = 0xC54F },
            new() { Id = "razer:00C1", Name = "Razer Viper V3 Pro", Kind = DeviceKind.Mouse, Provider = "Razer", VendorId = 0x1532, ProductId = 0x00C1 },
            new() { Id = "aula:38A6:2F1A", Name = "AULA 昆仑 L5 Ultra", Kind = DeviceKind.Mouse, Provider = "AULA", VendorId = 0x38A6, ProductId = 0x2F1A },
            new() { Id = "am:0E8D-0703", Name = "Angry Miao AM Infinity .97", Kind = DeviceKind.Mouse, Provider = "AngryMiao", VendorId = 0x0E8D, ProductId = 0x0703 },
            new() { Id = "am:0E8D-0880", Name = "Angry Miao AM Infinity .97", Kind = DeviceKind.Mouse, Provider = "AngryMiao", VendorId = 0x0E8D, ProductId = 0x0880 },
            new() { Id = "rog:1B7A", Name = "ROG 游侠 2 RX 98 无线", Kind = DeviceKind.Keyboard, Provider = "ROG", VendorId = 0x0B05, ProductId = 0x1B7A },
            new() { Id = "sony:0EC2", Name = "Sony INZONE Buds", Kind = DeviceKind.Headset, Provider = "Sony", VendorId = 0x054C, ProductId = 0x0EC2 },
        };

        foreach (var d in targets)
        {
            var m = DeviceImageRuleMatcher.MatchRule(d, manifest.Rules);
            Assert.True(m.IsExactModelMatch, $"Expected exact VID:PID match for {d.Name} ({d.Id})");
            Assert.False(string.IsNullOrWhiteSpace(m.CloudUrl) && string.IsNullOrWhiteSpace(m.PiperSvg), $"Expected online URL for {d.Name}");
        }

        var amMatch = DeviceImageRuleMatcher.MatchRule(targets[3], manifest.Rules);
        Assert.Equal("https://ammaster.angrymiao.com/mouse/assets/device_am35_a-3df1ebac.png", amMatch.CloudUrl);
    }

    [Fact]
    public void AngryMiaoRace_ParsesLiveDongleCaptures_BatteryDpiAndDebounce()
    {
        // Live 0E8D:0703 getDongleBattery (12303 / 0x300F) => 100% full dock spare battery (both 62B HID report and trimmed)
        byte[] liveDockBat = [0x14, 0x0D, 0x00, 0x05, 0x5B, 0x09, 0x00, 0x0F, 0x30, 0x00, 0x02, 0x64, 0xFF, 0x01, 0x22, 0x01];
        byte[] liveDockBat62 = new byte[62];
        liveDockBat.CopyTo(liveDockBat62, 0);
        Assert.Single(AngryMiaoRaceProtocol.ParseFeature14Frames(liveDockBat62));
        var dockFrames = AngryMiaoRaceProtocol.ParseFeature14Frames(liveDockBat);
        Assert.Single(dockFrames);
        Assert.Equal(12303, dockFrames[0].RaceId);
        var dockBat = AngryMiaoRaceProtocol.ParseBatteryPayload(dockFrames[0].Payload);
        Assert.NotNull(dockBat);
        Assert.True(dockBat!.Exists);
        Assert.Equal(100, dockBat.Percent);
        Assert.True(dockBat.Full);
        Assert.False(dockBat.Charging);

        // Live 0E8D:0703 getDonglePairedList (11414 / 0x2C96) => deviceId 0x81
        byte[] livePaired = [0x14, 0x11, 0x00, 0x05, 0x5B, 0x0D, 0x00, 0x96, 0x2C, 0x00, 0x00, 0xBF, 0xA3, 0x5F, 0x45, 0xB3, 0x6C, 0x81, 0x01, 0x01];
        var pairedFrames = AngryMiaoRaceProtocol.ParseFeature14Frames(livePaired);
        var entries = AngryMiaoRaceProtocol.ParseDongleDeviceList(pairedFrames[0].Payload);
        Assert.Single(entries);
        Assert.Equal(0x81, entries[0].DeviceId);

        // Live 0E8D:0703 getFwVersion dual-frame (ignores [0x00] ack and keeps v1.8.4 payload)
        byte[] liveFw = [0x14, 0x16, 0x00, 0x05, 0x5B, 0x03, 0x00, 0x07, 0x1C, 0x00, 0x05, 0x5D, 0x0B, 0x00, 0x07, 0x1C, 0x00, 0x00, 0x06, 0x76, 0x31, 0x2E, 0x38, 0x2E, 0x34];
        var fwFrames = AngryMiaoRaceProtocol.ParseFeature14Frames(liveFw);
        Assert.Single(fwFrames);
        Assert.Equal(7175, fwFrames[0].RaceId);
        Assert.Contains("v1.8.4", System.Text.Encoding.ASCII.GetString(fwFrames[0].Payload));

        // Frame builder + 8-stage DPI payload parse + 8000 Hz debounce scaling
        byte[] race = AngryMiaoRaceProtocol.BuildRaceCommand(12484, [1, 0x20, 0x03, 0x40, 0x06]); // stage 1: 800/1600
        byte[] feat14 = AngryMiaoRaceProtocol.BuildFeature14Report(0x80, race, 62);
        Assert.Equal(62, feat14.Length);
        Assert.Equal(0x14, feat14[0]);
        Assert.Equal(11, feat14[1]);
        Assert.Equal(0x80, feat14[2]);

        var dpiPay = new byte[3 + 8 * 4]; // status=0, count=2, active=1, 8-slot stride
        dpiPay[0] = 0; dpiPay[1] = 2; dpiPay[2] = 1;
        dpiPay[3] = 0x90; dpiPay[4] = 0x01; // stage 0 X = 400
        dpiPay[5] = 0x20; dpiPay[6] = 0x03; // stage 1 X = 800
        dpiPay[19] = 0x90; dpiPay[20] = 0x01; // stage 0 Y = 400
        dpiPay[21] = 0x40; dpiPay[22] = 0x06; // stage 1 Y = 1600
        var dpi = AngryMiaoRaceProtocol.ParseDpiPayload(dpiPay);
        Assert.NotNull(dpi);
        Assert.Equal(1, dpi!.CurrentIndex);
        Assert.False(dpi.SyncXY);
        Assert.Equal((800, 1600), dpi.Stages[1]);

        var (w8k, rf8k, bt8k) = AngryMiaoRaceProtocol.EncodeDebounceBytes(8000, 15);
        Assert.Equal(31, w8k);
        Assert.Equal(31, rf8k);
        Assert.Equal(4, bt8k);
        Assert.Equal(15, AngryMiaoRaceProtocol.DecodeDebounceMs(8000, w8k));
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

    [Fact]
    public void AtkModelCatalog_DisambiguatesCompxAndBitmouseCidMid_AndPersistsReceiverModels()
    {
        // Same (1, 4) CID/MID resolves to different models under Bitmouse vs CompX
        string? bmMiniPlus = AtkModelCatalog.ResolveModelName(
            0x373B, 0x1303, (1, 4), "Wireless mouse", AtkProtocolFamily.Bitmouse);
        Assert.Equal("ATK A9 Mini +", bmMiniPlus);
        Assert.Equal(AtkSensorKind.Paw3955Master,
            AtkModelCatalog.ResolveSensor(0x373B, 0x1303, (1, 4), AtkProtocolFamily.Bitmouse, bmMiniPlus));

        string? cmxZeroEx = AtkModelCatalog.ResolveModelName(
            0x373B, 0x11CC, (1, 4), "Wireless mouse", AtkProtocolFamily.Compx);
        Assert.Equal("ATK ZERO EX", cmxZeroEx);
        Assert.Equal(AtkSensorKind.Paw3950Ultra,
            AtkModelCatalog.ResolveSensor(0x373B, 0x11CC, (1, 4), AtkProtocolFamily.Compx, cmxZeroEx));

        // Bitmouse (1, 16) -> ATK A9 Mini (PAW3950Ultra)
        string? bmMini = AtkModelCatalog.ResolveModelName(
            0x373B, 0x1303, (1, 16), "Wireless mouse", AtkProtocolFamily.Bitmouse);
        Assert.Equal("ATK A9 Mini", bmMini);
        Assert.Equal(AtkSensorKind.Paw3950Ultra,
            AtkModelCatalog.ResolveSensor(0x373B, 0x1303, (1, 16), AtkProtocolFamily.Bitmouse, bmMini));

        Assert.True(AtkModelCatalog.IsGenericReceiverName("Wireless mouse"));
        Assert.True(AtkModelCatalog.IsGenericReceiverName("ATK 373B:1303"));
        Assert.False(AtkModelCatalog.IsGenericReceiverName("ATK A9 Mini"));

        AtkModelCatalog.SeedPersistentReceiverModels(new Dictionary<string, string>
        {
            ["atk:373B-13FE"] = "ATK A9 Mini",
            ["atk:373B-13FF"] = "Wireless mouse",
        });
        Assert.Equal("ATK A9 Mini",
            AtkModelCatalog.ResolveModelName(0x373B, 0x13FE, null, "Wireless mouse"));
        Assert.Null(
            AtkModelCatalog.ResolveModelName(0x373B, 0x13FF, null, null));
        Assert.Equal("Wireless mouse",
            AtkModelCatalog.ResolveModelName(0x373B, 0x13FF, null, "Wireless mouse"));
    }

    [Fact]
    public void Compx08_UltraAndPaw3955Stages_And01MmLod_RoundTripAccurately()
    {
        foreach (int targetDpi in new[] { 400, 850, 12500, 36000 })
        {
            byte[] encoded = Compx08Protocol.EncodeDpiStageUltra(targetDpi, targetDpi);
            var (x, y) = Compx08Protocol.DecodeDpiStageUltra(encoded);
            Assert.Equal((targetDpi, targetDpi), (x, y));
        }

        byte[] p3955 = Compx08Protocol.EncodePaw3955Stage(42000, 42000);
        Assert.Equal((42000, 42000), Compx08Protocol.DecodePaw3955Stage(p3955));

        byte rateCode = Compx08Protocol.EncodePollingRate(4000)!.Value;
        byte[] hdr =
        [
            rateCode, Compx08Protocol.ChecksumByte(rateCode),
            0x05, Compx08Protocol.ChecksumByte(0x05), // StageCount = 5 (0x0002)
            0x03, Compx08Protocol.ChecksumByte(0x03), // ActiveStageIndex = 3 (0x0004)
            0, 0, 0, 0,
            11, Compx08Protocol.ChecksumByte(11),     // 1.1 mm LOD
        ];
        byte[] stg0 = Compx08Protocol.EncodeDpiStageUltra(400, 400);
        byte[] stg1 = Compx08Protocol.EncodeDpiStageUltra(800, 800);
        byte[] stg2 = Compx08Protocol.EncodeDpiStageUltra(1600, 1600);
        byte[] stg3 = Compx08Protocol.EncodeDpiStageUltra(3200, 3200);
        byte[] stg4 = Compx08Protocol.EncodeDpiStageUltra(12000, 12000);
        byte[] allStages = [.. stg0, .. stg1, .. stg2, .. stg3, .. stg4];

        Assert.True(Compx08Protocol.TryParseVerifiedConfig(
            hdr, allStages, out var cfg, useUltraDpi: true, sensor: AtkSensorKind.Paw3950Ultra));
        Assert.Equal(5, cfg.StageCount);
        Assert.Equal(3, cfg.ActiveStageIndex);
        Assert.Equal((3200, 3200), cfg.Stages[3]);
        Assert.Equal(11, cfg.LodLevel);
    }

    [Fact]
    public void VendorDriverCatalog_RogUwpShellUriAndFlydigiWeb_ResolveCleanly()
    {
        var rogLinks = VendorDriverCatalog.Resolve(new PeripheralDevice
        {
            Id = "rog:0b05-1b7a",
            Name = "ROG Strix Scope II 96 RX",
            Kind = DeviceKind.Keyboard,
            VendorId = 0x0B05,
            Provider = "ROG",
        });
        Assert.NotNull(rogLinks.Local);
        Assert.Equal(@"shell:AppsFolder\B9ECED6F.ArmouryCrate_qmba6cd70vzyy!App", rogLinks.Local!.ShellUri);
        Assert.Empty(rogLinks.Local.ExeCandidatePaths);
        Assert.Equal("https://gearlink.asus.com", rogLinks.Web?.Url);

        var flyLinks = VendorDriverCatalog.Resolve(new PeripheralDevice
        {
            Id = "flydigi:cd2",
            Name = "飞智充电底座 CD2",
            Kind = DeviceKind.Controller,
            VendorId = 0x37D7,
            Provider = "Flydigi",
        });
        Assert.Equal("https://space_station.flydigi.com/", flyLinks.Web?.Url);
    }

    [Fact]
    public void BatteryHistoryAndDualEarChipText_UsesHistoryWhenSleepingAndStacksLeftRight()
    {
        var cache = new Dictionary<string, CachedBatteryRecord>(StringComparer.OrdinalIgnoreCase);

        var liveMouse = new PeripheralDevice { Id = "m:1", Name = "Mouse", Kind = DeviceKind.Mouse, Provider = "Mock", BatteryPercent = 84 };
        var liveBuds = new PeripheralDevice
        {
            Id = "sony:buds",
            Name = "Sony INZONE Buds",
            Kind = DeviceKind.Headset,
            Provider = "Sony",
            BatteryPercent = 82,
            LeftEarPercent = 88,
            RightEarPercent = 82,
            RightEarCharging = true,
        };
        var neverSeen = new PeripheralDevice { Id = "k:never", Name = "New Keyboard", Kind = DeviceKind.Keyboard, Provider = "Mock", BatteryPercent = null };

        Assert.True(BatteryHistoryHelper.RecordObservedDevices(cache, [liveMouse, liveBuds, neverSeen]));
        Assert.False(cache.ContainsKey("k:never"));
        Assert.False(BatteryHistoryHelper.RecordObservedDevices(cache, [liveMouse, liveBuds]));

        var liveChip = BatteryHistoryHelper.BuildChipText(liveBuds, "", showPct: true, cache["sony:buds"]);
        Assert.Equal("L 88%", liveChip.TextTop);
        Assert.Equal("R 82%\u26A1", liveChip.TextBottom);
        Assert.False(liveChip.UsedHistoricalFallback);

        var sleepingBuds = new PeripheralDevice
        {
            Id = "sony:buds",
            Name = "Sony INZONE Buds",
            Kind = DeviceKind.Headset,
            Provider = "Sony",
            BatteryPercent = null,
            LeftEarPercent = null,
            RightEarPercent = null,
            RightEarCharging = false,
            IsSleeping = true,
            State = BatteryState.Unknown,
        };
        var sleepingChip = BatteryHistoryHelper.BuildChipText(sleepingBuds, "", showPct: true, cache["sony:buds"]);
        Assert.Equal("L 88%", sleepingChip.TextTop);
        Assert.Equal("R 82%", sleepingChip.TextBottom);
        Assert.True(sleepingChip.UsedHistoricalFallback);

        var neverChip = BatteryHistoryHelper.BuildChipText(neverSeen, "", showPct: true, null);
        Assert.Equal("--", neverChip.Text);
        Assert.Equal("", neverChip.TextTop);
        Assert.False(neverChip.UsedHistoricalFallback);
    }
}
