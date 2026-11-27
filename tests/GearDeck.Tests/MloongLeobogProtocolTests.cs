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
}
