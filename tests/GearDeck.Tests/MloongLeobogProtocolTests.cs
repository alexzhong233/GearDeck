using GearDeck.Core.Models;
using GearDeck.Core.Providers;
using GearDeck.Core.Services;
using Xunit;

public class MloongLeobogProtocolTests
{
    [Fact]
    public void MloongMx301Pro_BatteryQueryAndParse_MatchesWasmSpec()
    {
        var req = MloongProvider.BuildMx301BatteryQuery();
        Assert.Equal(64, req.Length);
        Assert.Equal(new byte[] { 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01 }, req[..8]);

        var reply64 = new byte[64];
        reply64[0] = 0x55;
        reply64[1] = 0x30;
        reply64[8] = 87;
        reply64[9] = 1;
        Assert.True(MloongProvider.ParseMx301BatteryReply(reply64, out byte lvl, out bool chg));
        Assert.Equal(87, lvl);
        Assert.True(chg);

        var reply65 = new byte[65];
        Array.Copy(reply64, 0, reply65, 1, 64);
        var normalized = MloongProvider.NormalizeMx301Reply(reply65);
        Assert.NotNull(normalized);
        Assert.Equal(64, normalized!.Length);
        Assert.Equal((byte)87, MloongProvider.ParseMx301BatteryReply(normalized)!.Value.Level);

        reply64[8] = 101;
        Assert.False(MloongProvider.ParseMx301BatteryReply(reply64, out _, out _));
    }

    [Fact]
    public void MloongMx301Pro_ConfigRoundTrip_AndWriteHeaderMatchesWasm()
    {
        var query = MloongProvider.BuildMx301ConfigQuery();
        Assert.Equal(new byte[] { 0x55, 0x0E, 0xA5, 0x0B, 0x30, 0x01, 0x01, 0x01 }, query[..8]);

        var reply = new byte[64];
        reply[0] = 0x55;
        reply[1] = 0x0E;
        reply[9] = 2;
        reply[10] = 4; // 1000 Hz
        reply[11] = 4; // dpiCount
        reply[12] = 2; // activeStage=1 (1-based=2)
        reply[13] = 0x20; reply[14] = 0x03; // 800
        reply[15] = 0x40; reply[16] = 0x06; // 1600
        reply[17] = 0x80; reply[18] = 0x0C; // 3200
        reply[19] = 0x00; reply[20] = 0x19; // 6400
        reply[48] = 1; // scrollReverse
        reply[49] = 2; // LOD 2mm
        reply[50] = 0x31; // Angle|Ripple|MotionSync
        reply[51] = 6; // debounceMs
        reply[52] = 15; // sleepMin
        reply[53] = 1; // highSpeed
        reply[54] = 1; // wakeupFlag

        var cfg = MloongProvider.ParseMx301ConfigReply(reply);
        Assert.NotNull(cfg);
        Assert.Equal(4, cfg!.RateCode);
        Assert.Equal(1000, MloongProvider.Mx301RateCodeToHz(cfg.RateCode));
        Assert.Equal(1, cfg.ActiveStage);
        Assert.Equal(800, cfg.DpiStages[0]);
        Assert.Equal(6400, cfg.DpiStages[3]);
        Assert.Equal((byte)2, cfg.Lod);
        Assert.Equal((byte)0x31, cfg.SensorFlag);

        cfg.DpiStages[0] = 850;
        var write = MloongProvider.BuildMx301ConfigWrite(reply, cfg);
        Assert.Equal(64, write.Length);
        Assert.Equal(new byte[] { 0x55, 0x0F, 0xAE, 0x0A, 0x2F, 0x01, 0x01, 0x01, 0x00 }, write[..9]);
        Assert.Equal((byte)0x52, write[13]); // 850 & 0xFF
        Assert.Equal((byte)0x03, write[14]); // 850 >> 8

        write[1] = 0x0E;
        var reparsed = MloongProvider.ParseMx301ConfigReply(write);
        Assert.NotNull(reparsed);
        Assert.Equal(850, reparsed!.DpiStages[0]);
        Assert.Equal(15, reparsed.SleepMin);
    }

    [Fact]
    public void LeobogHi98CPro_WirelessChecksumAndBatteryAndConfig_MatchesProtocol()
    {
        var req = LeobogProvider.BuildWirelessReadPacket(0x0A);
        Assert.Equal(20, req.Length);
        Assert.Equal(0x13, req[0]);
        Assert.Equal(0x4A, req[1]);
        Assert.Equal(LeobogProvider.ComputeWirelessChecksum(req), req[19]);
        Assert.True(LeobogProvider.ParseWirelessPacket(req));

        var reply = new byte[20];
        reply[0] = 0x13;
        reply[1] = 0x4A;
        reply[2] = 1;
        reply[3] = 0;
        reply[4] = 0x01;
        reply[5] = 76;
        reply[19] = LeobogProvider.ComputeWirelessChecksum(reply);

        Assert.True(LeobogProvider.ParseWirelessPacket(reply, out var payload));
        Assert.Equal(76, LeobogProvider.ParseWirelessBatteryPayload(payload));

        reply[19] ^= 0xFF;
        Assert.False(LeobogProvider.ParseWirelessPacket(reply, out _));
    }

    [Fact]
    public void LeobogHi98CPro_WiredHeaderAndSleepClamping_AndWirelessWriteRefused()
    {
        var req = LeobogProvider.BuildWiredFeatureRequest(0x84, 0x00, 1, 0, 128);
        Assert.Equal(520, req.Length);
        Assert.Equal(0x06, req[0]);
        Assert.Equal(0x84, req[1]); // cmd at byte[1]
        Assert.Equal(0x00, req[2]); // sub at byte[2]
        Assert.Equal(0x00, req[3]);
        Assert.Equal(0x01, req[4]);
        Assert.Equal(0x00, req[5]);
        Assert.Equal(0x80, req[6]);
        Assert.Equal(0x00, req[7]);

        var cfg = new byte[128];
        cfg[126] = 0x5A;
        cfg[127] = 0xA5;
        LeobogProvider.PatchConfigSleepSeconds(cfg, 0);
        Assert.Equal((byte)1, cfg[0x18]); // Never writes 0!
        LeobogProvider.PatchConfigSleepSeconds(cfg, 600);
        Assert.Equal((byte)20, cfg[0x18]); // 600 / 30 = 20

        var provider = new LeobogProvider();
        var ref24 = provider.ApplySettings("leobog:3554-FA09", new DeviceTuningUpdate { SleepSeconds = 300 });
        Assert.False(ref24.Success);
        Assert.Contains("仅允许在 USB-C 有线模式", ref24.Message);

        var refBt = provider.ApplySettings("leobog:3554-FA07", new DeviceTuningUpdate { SleepSeconds = 300 });
        Assert.False(refBt.Success);
        Assert.Contains("只读", refBt.Message);
    }

    [Fact]
    public void DeviceUnifier_MergesCrossVidPairs_ForMloongAndLeobog()
    {
        var mxWired = new PeripheralDevice
        {
            Id = "mloong:A8A4-2255",
            VendorId = 0xA8A4,
            ProductId = 0x2255,
            Name = "MLOONG MX301 PRO",
            Kind = DeviceKind.Mouse,
            Provider = "MLOONG",
            Connected = true,
            Transport = "USB",
            BatteryPercent = 90,
            State = BatteryState.Charging,
        };
        var mxDongle = new PeripheralDevice
        {
            Id = "mloong:A8A5-2255",
            VendorId = 0xA8A5,
            ProductId = 0x2255,
            Name = "MLOONG MX301 PRO (2.4G)",
            Kind = DeviceKind.Mouse,
            Provider = "MLOONG",
            Connected = true,
            IsSleeping = true,
            Transport = "2.4G",
        };
        var res1 = DeviceUnifier.Unify([mxWired, mxDongle]);
        Assert.Single(res1.Devices);
        Assert.Equal("mloong:A8A5-2255", res1.Devices[0].Id);
        Assert.Equal("mloong:A8A4-2255", res1.ActiveTransportRouting["mloong:A8A5-2255"]);

        var lbWired = new PeripheralDevice
        {
            Id = "leobog:258A-010C",
            VendorId = 0x258A,
            ProductId = 0x010C,
            Name = "LEOBOG Hi98C Pro (USB-C)",
            Kind = DeviceKind.Keyboard,
            Provider = "LEOBOG",
            Connected = true,
            Transport = "USB",
            State = BatteryState.Charging,
        };
        var lbDongle = new PeripheralDevice
        {
            Id = "leobog:3554-FA09",
            VendorId = 0x3554,
            ProductId = 0xFA09,
            Name = "LEOBOG Hi98C Pro (2.4G)",
            Kind = DeviceKind.Keyboard,
            Provider = "LEOBOG",
            Connected = true,
            IsSleeping = true,
            Transport = "2.4G",
        };
        var res2 = DeviceUnifier.Unify([lbWired, lbDongle]);
        Assert.Single(res2.Devices);
        Assert.Equal("leobog:3554-FA09", res2.Devices[0].Id);
        Assert.Equal("leobog:258A-010C", res2.ActiveTransportRouting["leobog:3554-FA09"]);
    }

    [Fact]
    public void MloongMx301Pro_Accepts0xAAReplyAndPreservesWakeupRaw()
    {
        var batReply = new byte[64];
        batReply[0] = 0xAA;
        batReply[1] = 0x30;
        batReply[8] = 63;
        batReply[9] = 0;
        Assert.True(MloongProvider.ParseMx301BatteryReply(batReply, out byte lvl, out bool chg));
        Assert.Equal(63, lvl);
        Assert.False(chg);

        var cfgReply = new byte[64];
        cfgReply[0] = 0xAA;
        cfgReply[1] = 0x0E;
        cfgReply[10] = 3; // 500 Hz
        cfgReply[11] = 1; // 1 stage
        cfgReply[12] = 1;
        cfgReply[13] = 0x20; cfgReply[14] = 0x03; // 800 DPI
        cfgReply[48] = 1;
        cfgReply[49] = 1;
        cfgReply[50] = 0x20; // MotionSync
        cfgReply[51] = 3;
        cfgReply[52] = 2;
        cfgReply[53] = 0;
        cfgReply[54] = 0x10; // Observed real hardware byte[54]

        var parsed = MloongProvider.ParseMx301ConfigReply(cfgReply);
        Assert.NotNull(parsed);
        Assert.Equal((byte)0x10, parsed!.WakeupRaw);
        Assert.Equal(800, parsed.DpiStages[0]);

        var write = MloongProvider.BuildMx301ConfigWrite(cfgReply, parsed);
        Assert.Equal((byte)0x10, write[54]);
    }

    [Fact]
    public void LeobogHi98CPro_ExtractsLenFromHeaderAndAssemblesOutOfOrderPackets()
    {
        // Mock 21-byte frame where byte[4]&0x0F == 1 and trailing padding exists
        var batReply21 = new byte[21];
        batReply21[0] = 0x13;
        batReply21[1] = 0x4A;
        batReply21[2] = 1;
        batReply21[3] = 0;
        batReply21[4] = 0x01;
        batReply21[5] = 88;
        batReply21[19] = LeobogProvider.ComputeWirelessChecksum(batReply21);
        batReply21[20] = 0xFF; // 21st buffer byte ignored

        Assert.True(LeobogProvider.ParseWirelessPacket(batReply21, out var batPayload));
        Assert.Single(batPayload);
        Assert.Equal(88, LeobogProvider.ParseWirelessBatteryPayload(batPayload));

        // 128B config split across 10 packets (9 * 14 + 2 = 128), supplied out-of-order
        var cfg128 = new byte[128];
        cfg128[0x0A] = 10; // Snake
        cfg128[0x0F] = 1;  // WinLock
        cfg128[0x18] = 12; // 360s sleep
        cfg128[0x1B] = 2;  // macOS
        cfg128[126] = 0x5A;
        cfg128[127] = 0xA5;

        var packets = new List<byte[]>();
        for (int idx = 0; idx < 10; idx++)
        {
            int offset = idx * 14;
            int len = Math.Min(14, cfg128.Length - offset);
            var pkt = new byte[21];
            pkt[0] = 0x13;
            pkt[1] = 0x44;
            pkt[2] = 10;
            pkt[3] = (byte)idx;
            pkt[4] = (byte)len;
            Array.Copy(cfg128, offset, pkt, 5, len);
            pkt[19] = LeobogProvider.ComputeWirelessChecksum(pkt);
            packets.Add(pkt);
        }
        packets.Reverse();

        var assembled = LeobogProvider.AssembleWirelessConfig(packets);
        Assert.NotNull(assembled);
        Assert.Equal(cfg128, assembled);
        Assert.Equal("Windows", LeobogProvider.MapOsMode(0));
        Assert.Equal("Android", LeobogProvider.MapOsMode(1));
        Assert.Equal("macOS", LeobogProvider.MapOsMode(2));

        // 实机 0x3554:0xFA09 接收器 PrimaryUsage 为 0x0001:0x0006 + 0xFF02:0x0002，即使配置接口 UsagePairs 含鼠标也应识别为 LEOBOG
        var fa09Kbd = new GearDeck.Core.Hid.MacHidInterfaceInfo(
            1, 0x3554, 0xFA09, 0x0001, 0x0006, 8, 1, 0, 0, "USB", "2.4G Wireless Receiver", "Compx", "", [], false, true, []);
        var fa09Cfg = new GearDeck.Core.Hid.MacHidInterfaceInfo(
            2, 0x3554, 0xFA09, 0xFF02, 0x0002, 21, 20, 8, 0, "USB", "2.4G Wireless Receiver", "Compx", "", [], true, true, []);
        Assert.True(LeobogProvider.IsLeobogFa09Dongle([fa09Kbd, fa09Cfg]));

        var aulaMouse = new GearDeck.Core.Hid.MacHidInterfaceInfo(
            3, 0x3554, 0xFA09, 0x0001, 0x0002, 8, 1, 0, 0, "USB", "AULA Wireless Mouse", "Compx", "", [], true, false, []);
        Assert.False(LeobogProvider.IsLeobogFa09Dongle([aulaMouse]));
    }

    [Fact]
    public void LeobogHi98CPro_StudioModelsAndWirelessKeymapAssembly_WorkCorrectly()
    {
        // 1. 101 键布局完整性与唯一 ID / MatrixIndex
        Assert.Equal(101, Hi98CLayout.Keys.Count);
        Assert.Equal(101, Hi98CLayout.Keys.Select(k => k.Id).Distinct().Count());
        Assert.All(Hi98CLayout.Keys, k => Assert.InRange(k.DefaultMatrixIndex, 0, Hi98CLayout.MatrixSlotCount - 1));

        // 2. 组装 504B (36包) 默认层无线 0x41 分包 -> 自动补齐到 512B，并通过矩阵校准 101/101
        var rawLayer0 = new byte[Hi98CLayout.LayerByteLength];
        foreach (var spec in Hi98CLayout.Keys)
            Array.Copy(spec.DefaultRecord.Bytes, 0, rawLayer0, spec.DefaultMatrixIndex * 4, 4);

        const int totalPackets = 36; // 36 * 14 = 504
        var packets = new List<byte[]>();
        for (int idx = 0; idx < totalPackets; idx++)
        {
            var pkt = new byte[20];
            pkt[0] = 0x13;
            pkt[1] = 0x41;
            pkt[2] = totalPackets;
            pkt[3] = (byte)idx;
            pkt[4] = 14;
            Array.Copy(rawLayer0, idx * 14, pkt, 5, 14);
            pkt[19] = LeobogProvider.ComputeWirelessChecksum(pkt);
            packets.Add(pkt);
        }
        packets.Reverse();

        var assembled = LeobogProvider.AssembleWirelessBlock(
            packets, expectedCmd: 0x41, targetLength: 512,
            minRequiredBytes: Hi98CLayout.LayerByteLength, verifyMagicTail: false);
        Assert.NotNull(assembled);
        Assert.Equal(512, assembled!.Length);
        Assert.Equal(rawLayer0, assembled.Take(Hi98CLayout.LayerByteLength));

        var cal = Hi98MatrixCalibrator.Calibrate(assembled);
        Assert.Equal(101, cal.MatchedCount);
        Assert.True(cal.IsFullyVerified);
        Assert.Contains(53, cal.DetectedFnMatrixIndices);

        // 3. 128B LED 配置块与槽位、防 0 休眠保护
        var cfg = Hi98LedConfigBlock.DefaultTemplate();
        cfg.EffectHardwareID = 10;
        cfg.IsPerKeyCustomMode = false;
        cfg.WinKeyLocked = true;
        cfg.SideLightMode = Hi98SideLightMode.Breathing;
        cfg.SleepTimerSeconds = 0; // 强制钳制到 >= 30s
        Assert.Equal(30, cfg.SleepTimerSeconds);
        cfg.SetSlot(new Hi98OnboardEffectSlot(4, 3, 5), 10);
        var roundtrip = cfg.SlotForEffect(10);
        Assert.NotNull(roundtrip);
        Assert.Equal((byte)4, roundtrip!.Brightness);
        Assert.Equal((byte)3, roundtrip.Speed);
        Assert.Equal((byte)5, roundtrip.ColorIndex);

        // 4. 2.4G 实时 RGB 量化限制在 <= 9 包，有线限定写入拒绝无线
        var manyColors = Hi98RgbThemePreset.AllPresets[0].ColorsByMatrix;
        var livePackets = LeobogProvider.BuildWirelessLiveColorPackets(manyColors, maxReports: 9);
        Assert.InRange(livePackets.Count, 1, 9);

        var wirelessWriteRes = LeobogProvider.WriteKeymapLayerWired(
            "leobog:3554-FA09", Hi98KeyLayer.Base,
            new Dictionary<int, Hi98KeyBindingRecord> { [0] = Hi98KeyBindingRecord.Key(0x29) }, "test");
        Assert.False(wirelessWriteRes.Success);
    }
}
