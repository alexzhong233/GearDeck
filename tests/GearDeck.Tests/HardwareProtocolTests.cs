using GearDeck.Core.Providers;
using Xunit;

public class HardwareProtocolTests
{
    [Fact]
    public void Razer_BuildPacket_ComputesLength91AndXorCrc()
    {
        byte[] pkt = RazerProvider.BuildPacket(0x1F, 0x04, 0x05, 0x07, [0x01, 0x06, 0x40, 0x03, 0x20, 0x00, 0x00]);
        Assert.Equal(91, pkt.Length);
        Assert.Equal(0x1F, pkt[2]);
        Assert.Equal(0x07, pkt[6]);
        Assert.Equal(0x04, pkt[7]);
        Assert.Equal(0x05, pkt[8]);
        Assert.Equal(0x01, pkt[9]);
        Assert.Equal(0x06, pkt[10]);
        Assert.Equal(0x40, pkt[11]);

        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= pkt[i];
        Assert.Equal(crc, pkt[89]);
    }

    [Fact]
    public void Razer_DpiAndStages_RoundTripCorrectly()
    {
        var single = RazerProvider.ParseDpiReply([0x01, 0x06, 0x40, 0x03, 0x20, 0x00, 0x00]);
        Assert.Equal((1600, 800), single);

        var stages = new List<(int X, int Y)> { (400, 400), (800, 800), (1600, 1600), (3200, 3200) };
        byte[] encoded = RazerProvider.BuildDpiStagesArgs(2, stages);
        var decoded = RazerProvider.ParseDpiStagesReply(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(2, decoded.Value.ActiveIndex);
        Assert.Equal(stages, decoded.Value.Stages);
    }

    [Theory]
    [InlineData(8000, 0x01)]
    [InlineData(4000, 0x02)]
    [InlineData(2000, 0x04)]
    [InlineData(1000, 0x08)]
    [InlineData(500, 0x10)]
    [InlineData(250, 0x20)]
    [InlineData(125, 0x40)]
    public void Razer_ExtendedPollingRate_RoundTrips(int hz, byte code)
    {
        Assert.Equal(code, RazerProvider.MapExtendedPollingCode(hz));
        Assert.Equal(hz, RazerProvider.ParseExtendedPollingReply([0x00, code]));
    }

    [Theory]
    [InlineData(1000, 0x01)]
    [InlineData(500, 0x02)]
    [InlineData(250, 0x04)]
    [InlineData(125, 0x08)]
    public void Razer_ClassicPollingRate_RoundTrips(int hz, byte code)
    {
        Assert.Equal(code, RazerProvider.MapClassicPollingCode(hz));
        Assert.Equal(hz, RazerProvider.ParseClassicPollingReply([code]));
    }

    [Fact]
    public void Logitech_ParseDpiRangeList_DecodesStepToken()
    {
        // sensor=0, min=100 (0x0064), stepToken=0xE032 (step=50), max=32000 (0x7D00), end=0x0000
        byte[] data = [0x00, 0x00, 0x64, 0xE0, 0x32, 0x7D, 0x00, 0x00, 0x00];
        var range = LogitechProvider.ParseDpiRangeList(data, startOffset: 1);
        Assert.Equal((100, 32000, 50), range);
    }

    [Fact]
    public void Logitech_ParseExtendedDpiParams_DisambiguatesFormatAAndZeroPaddedFormatB()
    {
        // Format A (16-byte HID++ payload): sensor=0, curX=1600, defX=800, curY=3200, defY=800, lod=1 (Low)
        byte[] fmtA = [0x00, 0x06, 0x40, 0x03, 0x20, 0x0C, 0x80, 0x03, 0x20, 0x01, 0, 0, 0, 0, 0, 0];
        Assert.Equal((1600, 3200, 1), LogitechProvider.ParseExtendedDpiParams(fmtA));

        // Format B (16-byte zero-padded HID++ payload): sensor=0, curX=1600, curY=1600, lod=2 (Med), trailing zeros
        // Must NOT misread p[5..6] = (0x0200 = 512) as Format A!
        byte[] fmtB = [0x00, 0x06, 0x40, 0x06, 0x40, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal((1600, 1600, 2), LogitechProvider.ParseExtendedDpiParams(fmtB));
    }

    [Fact]
    public void Logitech_RateMasks_Decode8KAndClassic()
    {
        Assert.Equal(
            new[] { 125, 250, 500, 1000, 2000, 4000, 8000 },
            LogitechProvider.ParseExtendedRateBitmask(0x7F));
        Assert.Equal(
            new[] { 125, 250, 500, 1000 },
            LogitechProvider.ParseClassicRateBitmask(0x8B));
        Assert.Equal((byte)6, LogitechProvider.MapExtendedRateCode(8000));
    }

    [Fact]
    public void Compx08_EepromConfigAndStages_RoundTripWith55Checksum()
    {
        byte[] s1 = Compx08Protocol.EncodeDpiStage(800, 800);
        byte[] s2 = Compx08Protocol.EncodeDpiStage(1600, 1200);
        byte[] s3 = Compx08Protocol.EncodeDpiStage(26000, 26000);
        Assert.Equal((800, 800), Compx08Protocol.DecodeDpiStage(s1));
        Assert.Equal((1600, 1200), Compx08Protocol.DecodeDpiStage(s2));
        Assert.Equal((26000, 26000), Compx08Protocol.DecodeDpiStage(s3));

        byte rateCode = Compx08Protocol.EncodePollingRate(8000)!.Value;
        byte[] hdr =
        [
            rateCode, Compx08Protocol.ChecksumByte(rateCode), // rate=8000
            0x01, Compx08Protocol.ChecksumByte(0x01),         // active stage=1
            0x03, Compx08Protocol.ChecksumByte(0x03),         // 3 stages
            0, 0, 0, 0,
            0x01, Compx08Protocol.ChecksumByte(0x01),         // lod=1
        ];
        byte[] stages = [.. s1, .. s2, .. s3, 0, 0, 0, 0];

        bool ok = Compx08Protocol.TryParseVerifiedConfig(hdr, stages, out var cfg);
        Assert.True(ok);
        Assert.Equal(8000, cfg.PollingRateHz);
        Assert.Equal(1, cfg.ActiveStageIndex);
        Assert.Equal(3, cfg.Stages.Count);
        Assert.Equal((1600, 1200), cfg.Stages[1]);
        Assert.Equal(1, cfg.LodLevel);

        // Corrupt checksum guard must reject misparsed firmware
        hdr[1] ^= 0xFF;
        Assert.False(Compx08Protocol.TryParseVerifiedConfig(hdr, stages, out _));
    }

    [Fact]
    public void Compx08_BatteryFrame_ParsesLevelAndCharging()
    {
        var pkt = Compx08Protocol.BuildFrame(0x08, 0x04, default);
        int sum = pkt.Take(16).Sum(b => b);
        Assert.Equal(0x55, (sum + pkt[16]) & 0xFF);

        byte[] reply = [0x08, 0x04, 0, 0, 0, 0x04, 78, 0x01, 0x0F, 0xA0, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(Compx08Protocol.TryParseBatteryFrame(reply, out byte lvl, out bool chg));
        Assert.Equal(78, lvl);
        Assert.True(chg);
    }

    [Fact]
    public void Mchose_ProtocolPacketsAndTables_ParseCorrectly()
    {
        byte[] pkt = MchoseProvider.BuildMchoseCommand(0x0104, [0x00, 0x02, 0x00, 0x40, 0x06]);
        Assert.Equal(0x4D, pkt[0]);
        Assert.Equal(5, pkt[3]);
        Assert.Equal(0x04, pkt[4]);
        Assert.Equal(0x01, pkt[5]);

        var cfg = MchoseProvider.ParseConfigReply([0x01, 0x22, 0x05]);
        Assert.Equal(1, cfg.Profile);
        Assert.Equal(2, cfg.ActiveStage);
        Assert.Equal(4000, cfg.RateHz);

        byte[] tableReply = [0x01, 0x00, 0x03, 0x01, 0x01, 0x90, 0x01, 0x20, 0x03, 0x40, 0x06];
        var dpiTable = MchoseProvider.ParseDpiTableReply(tableReply);
        Assert.NotNull(dpiTable);
        Assert.True(dpiTable!.DualAxis);
        Assert.Equal(1, dpiTable.ActiveStage);
        Assert.Equal(new[] { 400, 800, 1600 }, dpiTable.Values);
    }

    [Fact]
    public void AulaAndAngryMiao_FrameParsersWorkCleanly()
    {
        byte[] aulaReq = AulaProvider.BuildAula13Frame();
        Assert.Equal(0x13, aulaReq[0]);
        Assert.Equal(0x4A, aulaReq[1]);
        int sum = aulaReq.Take(19).Sum(b => b) & 0xFF;
        Assert.Equal(sum, aulaReq[19]);

        var aulaReply = new byte[20];
        aulaReply[0] = 0x13;
        aulaReply[1] = 0xCA; // 0x4A | 0x80
        aulaReply[5] = 86;
        aulaReply[6] = 0x10;
        Assert.Equal((86, true), AulaProvider.ParseAula13Reply(aulaReply));

        var am3151 = new byte[16];
        am3151[0] = 5; am3151[1] = 1; am3151[3] = 74; am3151[11] = 95;
        Assert.Equal((74, true, 95), AngryMiaoProvider.ParseBatteryReply(am3151));

        var amF7 = new byte[16];
        amF7[0] = 0xF7; amF7[2] = 63; amF7[10] = 88;
        Assert.Equal((63, false, 88), AngryMiaoProvider.ParseBatteryReply(amF7));
    }
}
