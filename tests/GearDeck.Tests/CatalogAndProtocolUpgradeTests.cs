using GearDeck.Core;
using GearDeck.Core.Models;
using GearDeck.Core.Providers;
using GearDeck.Core.Services;
using Xunit;

namespace GearDeck.Tests;

public sealed class CatalogAndProtocolUpgradeTests
{
    [Fact]
    public void DeviceCatalogService_ParsesAndLooksUpEntriesCleanly()
    {
        string json = """
        {
          "version": 1,
          "entries": [
            { "vid": "3710", "pid": "3414", "brand": "Pulsar", "model": "Pulsar X2V2 Wireless", "kind": "Mouse", "transport": "2.4G", "protocol": "openmouse" },
            { "vid": "0x31E3", "pid": "0x1320", "brand": "Wooting", "model": "Wooting 60HE+", "kind": "Keyboard", "transport": "USB", "protocol": "via" },
            { "vid": "057e", "pid": "2009", "brand": "Nintendo", "model": "Nintendo Switch Pro Controller", "kind": "Controller", "transport": "Bluetooth", "protocol": "gamepad" }
          ]
        }
        """;

        var manifest = DeviceCatalogService.ParseManifest(json);
        DeviceCatalogService.SeedForTest(manifest);
        try
        {
            Assert.True(DeviceCatalogService.TryLookup(0x3710, 0x3414, out var pulsar));
            Assert.NotNull(pulsar);
            Assert.Equal("Pulsar", pulsar!.Brand);
            Assert.Equal(DeviceKind.Mouse, pulsar.ParsedKind);

            Assert.True(DeviceCatalogService.TryLookup(0x31E3, 0x1320, out var wooting));
            Assert.Equal(DeviceKind.Keyboard, wooting!.ParsedKind);

            Assert.True(DeviceCatalogService.TryLookup(0x057E, 0x2009, out var swPro));
            Assert.Equal(DeviceKind.Controller, swPro!.ParsedKind);

            Assert.False(DeviceCatalogService.TryLookup(0x9999, 0x9999, out _));
        }
        finally
        {
            DeviceCatalogService.SeedForTest(null);
        }
    }

    [Fact]
    public void DeviceImageRuleMatcher_FuzzyNormalizesNoiseWordsAndPrefixes()
    {
        var rules = new List<DeviceImageRule>
        {
            new()
            {
                Id = "pulsar_x2v2",
                Name = "Pulsar X2V2",
                Keywords = ["x2v2"],
                Kind = "Mouse",
                CloudUrl = "https://example.invalid/x2v2.png",
            },
            new()
            {
                Id = "logi_superlight2",
                Name = "PRO X Superlight 2",
                Keywords = ["superlight2"],
                Kind = "Mouse",
                CloudUrl = "https://example.invalid/gpx2.png",
            },
        };

        var pulsar = new PeripheralDevice
        {
            Id = "pulsar:3710-3414",
            Name = "Pulsar X2 V2 Wireless 4K",
            Kind = DeviceKind.Mouse,
            Provider = "OpenMouse",
        };
        var m1 = DeviceImageRuleMatcher.MatchRule(pulsar, rules);
        Assert.Equal("pulsar_x2v2", m1.Rule?.Id);

        var logi = new PeripheralDevice
        {
            Id = "logi:c547",
            Name = "Logitech PRO X SUPERLIGHT 2 Gaming Mouse",
            Kind = DeviceKind.Mouse,
            Provider = "Logitech",
        };
        var m2 = DeviceImageRuleMatcher.MatchRule(logi, rules);
        Assert.Equal("logi_superlight2", m2.Rule?.Id);

        Assert.True(DeviceImageRuleMatcher.TryExtractVidPid(pulsar, out string vh, out string ph));
        Assert.Equal("3710", vh);
        Assert.Equal("3414", ph);
    }

    [Fact]
    public void ViaKeyboardProvider_ParsesProtocolVersionAndLayers()
    {
        var ver32 = new byte[32];
        ver32[0] = 0x01;
        ver32[1] = 0x00;
        ver32[2] = 0x0C;
        Assert.Equal((ushort)0x000C, ViaKeyboardProvider.ParseViaProtocolVersion(ver32));

        var ver33 = new byte[33];
        ver33[0] = 0x00;
        ver33[1] = 0x01;
        ver33[2] = 0x00;
        ver33[3] = 0x09;
        Assert.Equal((ushort)0x0009, ViaKeyboardProvider.ParseViaProtocolVersion(ver33));

        var layers32 = new byte[32];
        layers32[0] = 0x11;
        layers32[1] = 4;
        Assert.Equal(4, ViaKeyboardProvider.ParseViaLayerCount(layers32));

        Assert.Null(ViaKeyboardProvider.ParseViaProtocolVersion([0x01]));
        Assert.Null(ViaKeyboardProvider.ParseViaLayerCount([0x11, 0x00]));
    }

    [Fact]
    public void GamepadBatteryProvider_ParsesNintendoSwitchBatteryNibble()
    {
        // High nibble: bits 3..1 = level (4=Full), bit 0 = charging
        Assert.Equal((100, false), GamepadBatteryProvider.ParseSwitchBatteryReport([0x30, 0x01, 0x80, 0x00]));
        Assert.Equal((100, true), GamepadBatteryProvider.ParseSwitchBatteryReport([0x21, 0x01, 0x90, 0x00]));
        Assert.Equal((75, false), GamepadBatteryProvider.ParseSwitchBatteryReport([0x30, 0x01, 0x60, 0x00]));
        Assert.Equal((45, true), GamepadBatteryProvider.ParseSwitchBatteryReport([0x31, 0x01, 0x50, 0x00]));
        Assert.Equal((20, false), GamepadBatteryProvider.ParseSwitchBatteryReport([0x30, 0x01, 0x20, 0x00]));
        Assert.Equal((5, false), GamepadBatteryProvider.ParseSwitchBatteryReport([0x30, 0x01, 0x00, 0x00]));
        Assert.Null(GamepadBatteryProvider.ParseSwitchBatteryReport([0x3F, 0x00, 0x80, 0x00]));
    }

    [Fact]
    public void SteelSeriesProvider_ParsesGameBudsDualEarBattery()
    {
        // Report 0xB0, len>=7, r[3]==0x03 (Left active) r[5]=84, r[4]==0x03 (Right active) r[6]=78
        byte[] pkt = [0xB0, 0x03, 0x00, 0x03, 0x03, 84, 78];
        var parsed = SteelSeriesProvider.ParseNovaReport(0x230A, pkt);
        Assert.NotNull(parsed);
        Assert.Equal(78, parsed!.Value.Level);
        Assert.Equal(84, parsed.Value.Left);
        Assert.Equal(78, parsed.Value.Right);
    }

    [Fact]
    public void DeviceManager_ApplyWirelessSleepGuard_GuardsWirelessWithoutBattery()
    {
        var wirelessNull = new PeripheralDevice
        {
            Id = "pulsar:3710-3414",
            Name = "Pulsar X2V2 Wireless",
            Kind = DeviceKind.Mouse,
            Provider = "OpenMouse",
            Connected = true,
            IsSleeping = false,
            Transport = "2.4G USB-C",
            BatteryPercent = null,
            State = BatteryState.Unknown,
        };
        var wiredUsb = new PeripheralDevice
        {
            Id = "wooting:31E3-1320",
            Name = "Wooting 60HE+",
            Kind = DeviceKind.Keyboard,
            Provider = "VIA/QMK",
            Connected = true,
            IsSleeping = false,
            Transport = "USB",
            BatteryPercent = null,
            State = BatteryState.Unknown,
        };
        var wirelessCharging = new PeripheralDevice
        {
            Id = "razer:1532-00C1",
            Name = "Razer Viper V3 Pro",
            Kind = DeviceKind.Mouse,
            Provider = "Razer",
            Connected = true,
            IsSleeping = false,
            Transport = "2.4G",
            BatteryPercent = null,
            State = BatteryState.Charging,
        };

        DeviceManager.ApplyWirelessSleepGuard([wirelessNull, wiredUsb, wirelessCharging]);

        Assert.True(wirelessNull.IsSleeping);
        Assert.False(wiredUsb.IsSleeping);
        Assert.False(wirelessCharging.IsSleeping);
    }

    [Fact]
    public void BatteryHistoryHelper_RecordsCaseOnlyAndPreservesOutOfCase()
    {
        var cache = new Dictionary<string, CachedBatteryRecord>(StringComparer.OrdinalIgnoreCase);

        var inCaseOnly = new PeripheralDevice
        {
            Id = "sony:054C-0EC2",
            Name = "Sony INZONE Buds",
            Kind = DeviceKind.Headset,
            Provider = "Sony INZONE",
            CaseBatteryPercent = 88,
            CaseCharging = true,
        };

        Assert.True(BatteryHistoryHelper.RecordObservedDevices(cache, [inCaseOnly]));
        Assert.Equal(88, cache["sony:054C-0EC2"].CaseBatteryPercent);
        Assert.Contains("盒 88%⚡", new PeripheralDevice
        {
            Id = "sony:054C-0EC2",
            Name = "Sony INZONE Buds",
            Kind = DeviceKind.Headset,
            Provider = "Sony INZONE",
            LeftEarPercent = 95,
            RightEarPercent = 92,
            CaseBatteryPercent = 88,
            CaseCharging = true,
        }.FormatDualEarSummary());

        var budsOutNoCase = new PeripheralDevice
        {
            Id = "sony:054C-0EC2",
            Name = "Sony INZONE Buds",
            Kind = DeviceKind.Headset,
            Provider = "Sony INZONE",
            LeftEarPercent = 90,
            RightEarPercent = 89,
            CaseBatteryPercent = null,
        };

        Assert.True(BatteryHistoryHelper.RecordObservedDevices(cache, [budsOutNoCase]));
        Assert.Equal(90, cache["sony:054C-0EC2"].LeftEarPercent);
        Assert.Equal(88, cache["sony:054C-0EC2"].CaseBatteryPercent);
    }

    [Fact]
    public void BatteryHistoryHelper_RecordsAngryMiaoSpareDockBattery()
    {
        var cache = new Dictionary<string, CachedBatteryRecord>(StringComparer.OrdinalIgnoreCase);

        var amAwake = new PeripheralDevice
        {
            Id = "am:0E8D-0703",
            Name = "Angry Miao AM Infinity .97",
            Kind = DeviceKind.Mouse,
            Provider = "AngryMiao",
            BatteryPercent = 82,
            CaseBatteryPercent = 94,
            CaseCharging = false,
        };
        Assert.True(BatteryHistoryHelper.RecordObservedDevices(cache, [amAwake]));
        Assert.Equal(82, cache["am:0E8D-0703"].BatteryPercent);
        Assert.Equal(94, cache["am:0E8D-0703"].CaseBatteryPercent);

        var amSleepingOnlyDock = new PeripheralDevice
        {
            Id = "am:0E8D-0703",
            Name = "Angry Miao AM Infinity .97",
            Kind = DeviceKind.Mouse,
            Provider = "AngryMiao",
            IsSleeping = true,
            BatteryPercent = null,
            CaseBatteryPercent = 91,
        };
        Assert.True(BatteryHistoryHelper.RecordObservedDevices(cache, [amSleepingOnlyDock]));
        Assert.Equal(82, cache["am:0E8D-0703"].BatteryPercent);
        Assert.Equal(91, cache["am:0E8D-0703"].CaseBatteryPercent);
    }

    [Fact]
    public void ReceiverModelRegistry_LearnsAndAppliesCustomOrRealNames()
    {
        ReceiverModelRegistry.ClearForTest();
        try
        {
            Assert.True(ReceiverModelRegistry.IsGenericReceiverName("LAMZU Crystal 8K"));
            Assert.True(ReceiverModelRegistry.IsGenericReceiverName("2.4G Wireless Receiver"));
            Assert.True(ReceiverModelRegistry.IsGenericReceiverName("CompX 37B0:0008"));
            Assert.False(ReceiverModelRegistry.IsGenericReceiverName("LAMZU ORCUS"));
            Assert.False(ReceiverModelRegistry.IsGenericReceiverName("WLmouse Beast X V2 8K"));

            // 第 1 次插有线或读到真实型号 -> 自动记忆 KV
            var awakeMerged = new PeripheralDevice
            {
                Id = "lamzu:37B0-0008",
                Name = "LAMZU ORCUS",
                Kind = DeviceKind.Mouse,
                Provider = "Lamzu",
            };
            ReceiverModelRegistry.ApplyAndLearn([awakeMerged]);

            // 第 2 次只剩接收器休眠状态 -> 回填真实型号
            var sleepingDongle = new PeripheralDevice
            {
                Id = "lamzu:37B0-0008",
                Name = "LAMZU Crystal 8K",
                Kind = DeviceKind.Mouse,
                Provider = "Lamzu",
                IsSleeping = true,
            };
            ReceiverModelRegistry.ApplyAndLearn([sleepingDongle]);
            Assert.Equal("LAMZU ORCUS", sleepingDongle.Name);

            // 自定义重命名优先级最高，且可恢复默认
            ReceiverModelRegistry.SetCustomName("lamzu:37B0-0008", "LAMZU Maya X");
            ReceiverModelRegistry.ApplyAndLearn([sleepingDongle]);
            Assert.Equal("LAMZU Maya X", sleepingDongle.Name);

            ReceiverModelRegistry.SetCustomName("lamzu:37B0-0008", null);
            sleepingDongle.Name = "LAMZU Crystal 8K";
            ReceiverModelRegistry.ApplyAndLearn([sleepingDongle]);
            Assert.Equal("LAMZU ORCUS", sleepingDongle.Name);
        }
        finally
        {
            ReceiverModelRegistry.ClearForTest();
        }
    }

    [Fact]
    public void Logitech_ParseBatteryVoltageReply_CalculatesPercentAndCharging()
    {
        // 4200 mV (0x1068) discharging -> 100%
        var full = LogitechProvider.ParseBatteryVoltageReply([0x10, 0x68, 0x00]);
        Assert.NotNull(full);
        Assert.Equal(100, full!.Value.Percent);
        Assert.False(full.Value.Charging);

        // 3920 mV (0x0F50) + status 0x01 charging -> 60%, Charging
        var chg = LogitechProvider.ParseBatteryVoltageReply([0x0F, 0x50, 0x01]);
        Assert.NotNull(chg);
        Assert.Equal(60, chg!.Value.Percent);
        Assert.True(chg.Value.Charging);

        // Out of range voltage -> null
        Assert.Null(LogitechProvider.ParseBatteryVoltageReply([0x05, 0x00, 0x00]));
    }

    [Fact]
    public void Rog_PacketAndSleepHelpers_BuildExpectedFrames()
    {
        Assert.Equal((byte)0, RogProvider.MapPowerOffCode(60));
        Assert.Equal((byte)2, RogProvider.MapPowerOffCode(180));
        Assert.Equal((byte)4, RogProvider.MapPowerOffCode(600));

        byte[] prof = RogProvider.BuildKeyboardProfilePacket(0x5E, 2);
        Assert.Equal([0x5E, 0x51, 0x00, 0x00, 0x00, 0x02], prof);

        byte[] lit = RogProvider.BuildKeyboardLightingPacket(0x5E, 1, 75);
        Assert.Equal(19, lit.Length);
        Assert.Equal((byte)0x5E, lit[0]);
        Assert.Equal((byte)0x51, lit[1]);
        Assert.Equal((byte)0x2C, lit[2]);
        Assert.Equal((byte)1, lit[3]);
        Assert.Equal((byte)75, lit[6]);

        byte[] dpiReply = new byte[8];
        dpiReply[1] = 0x12; dpiReply[2] = 0x04;
        dpiReply[5] = 31; dpiReply[6] = 0; // (31 + 1) * 50 = 1600
        Assert.Equal(1600, RogProvider.ParseMouseDpi(dpiReply));
    }

    [Fact]
    public void SonyInzone_TryParsePassiveBudsReportAndHciPacket_WorkAccurately()
    {
        var pkt = SonyInzoneProvider.BuildHciPacket(0x23, 0x02, 0x05, [0x19, 0xFF]);
        Assert.Equal(64, pkt.Length);
        Assert.Equal((byte)0x02, pkt[0]);
        Assert.Equal((byte)14, pkt[1]);
        Assert.Equal((byte)10, pkt[5]);
        Assert.Equal((byte)0x23, pkt[9]);
        Assert.Equal((byte)0x02, pkt[10]);
        Assert.Equal((byte)0x05, pkt[11]);
        Assert.Equal((byte)0x19, pkt[13]);
        Assert.Equal((byte)0xFF, pkt[14]);
        byte expectedChecksum = unchecked((byte)(0x96 + 0xC3 + 0x41 + 0x23 + 0x02 + 0x05 + 0x00 + 0x19 + 0xFF));
        Assert.Equal(expectedChecksum, pkt[15]);

        var passive = new byte[20];
        passive[0] = 0x02;
        passive[1] = 0x12;
        passive[2] = 0x04;
        passive[13] = 0x00; passive[14] = 81; // Right
        passive[15] = 0x01; passive[16] = 74; // Left charging
        passive[17] = 0x00; passive[18] = 95; // Case
        var parsed = SonyInzoneProvider.TryParsePassiveBudsReport(passive);
        Assert.NotNull(parsed);
        Assert.Equal(74, parsed!.Value.Left);
        Assert.True(parsed.Value.LeftChg);
        Assert.Equal(81, parsed.Value.Right);
        Assert.Equal(95, parsed.Value.Case);
    }

    [Fact]
    public void HyperX_CloudFlightAndAlpha2Parsers_WorkAccurately()
    {
        Assert.Equal(100, HyperXProvider.EstimateCloudFlightPercent(4000));
        Assert.True(HyperXProvider.EstimateCloudFlightPercent(3800) is >= 10 and <= 90);

        // Flight charging when voltage > 0x100B (e.g. 0x1100)
        var flightChg = new byte[20];
        flightChg[3] = 0x11; flightChg[4] = 0x00;
        var parsedFlightChg = HyperXProvider.ParseCloudFlightReport(flightChg);
        Assert.NotNull(parsedFlightChg);
        Assert.Null(parsedFlightChg!.Value.Level);
        Assert.True(parsedFlightChg.Value.Charging);

        // Alpha 2 battery reply: [0x51, 0x02, level=88, charging=1]
        var a2 = HyperXProvider.ParseAlpha2BatteryReport([0x51, 0x02, 88, 0x01]);
        Assert.NotNull(a2);
        Assert.Equal(88, a2!.Value.Level);
        Assert.True(a2.Value.Charging);
    }

    [Fact]
    public void Mchose_ParseHeadset55Battery_WorksAccurately()
    {
        Assert.Equal(76, MchoseProvider.ParseHeadset55Battery([0x55, 0x65, 76, 0x02, 0x00]));
        Assert.Null(MchoseProvider.ParseHeadset55Battery([0x55, 0x65, 101, 0x02, 0x00]));
        Assert.Null(MchoseProvider.ParseHeadset55Battery([0x55, 0x65, 50, 0x01, 0x00]));
    }

    [Fact]
    public void ViaAndSteelSeries_PacketAndReportHelpers_WorkAccurately()
    {
        byte[] pkt33 = ViaKeyboardProvider.BuildLightingPacket(33, 0x07, [0x03, 0x01, 200]);
        Assert.Equal(33, pkt33.Length);
        Assert.Equal((byte)0x00, pkt33[0]);
        Assert.Equal((byte)0x07, pkt33[1]);
        Assert.Equal((byte)0x03, pkt33[2]);
        Assert.Equal((byte)0x01, pkt33[3]);
        Assert.Equal((byte)200, pkt33[4]);

        byte[] pkt32 = ViaKeyboardProvider.BuildLightingPacket(32, 0x08, [0x02, 0x01]);
        Assert.Equal(32, pkt32.Length);
        Assert.Equal((byte)0x08, pkt32[0]);
        Assert.Equal((byte)0x02, pkt32[1]);
        Assert.Equal((byte)0x01, pkt32[2]);

        // SteelSeries Diablo IV (0x223A discrete vs 0x22A9 gen2 percentage)
        var d4 = SteelSeriesProvider.ParseNovaReport(0x223A, [0xB0, 0x03, 3, 0x03]);
        Assert.NotNull(d4);
        Assert.Equal(75, d4!.Value.Level);

        var d4Gen2 = SteelSeriesProvider.ParseNovaReport(0x22A9, [0xB0, 0x03, 83, 0x01]);
        Assert.NotNull(d4Gen2);
        Assert.Equal(83, d4Gen2!.Value.Level);
        Assert.True(d4Gen2.Value.Charging);
    }
}
