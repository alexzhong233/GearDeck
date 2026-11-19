using GearDeck.Core.Models;
using GearDeck.Core.Providers;
using Xunit;

public class OpenMouseProtocolTests
{
    [Fact]
    public void CompXPage_RequestAndReply_EncodeAndDecodeCleanly()
    {
        byte[] pkt = CompXPageProtocol.BuildRequest(0x02, 0x01, 0x81, [0x01, 0x06], lengthOverride: 0x0A);
        Assert.Equal(65, pkt.Length);
        Assert.Equal(0x00, pkt[0]); // Report ID
        Assert.Equal(0x02, pkt[3]); // target=Mouse
        Assert.Equal(0x0A, pkt[4]); // lengthOverride
        Assert.Equal(0x01, pkt[5]); // page
        Assert.Equal(0x81, pkt[6]); // command
        Assert.Equal(0x01, pkt[7]);
        Assert.Equal(0x06, pkt[8]);

        var reply = new byte[65];
        reply[1] = CompXPageProtocol.StatusOk; // 0xA1
        reply[4] = 0x04; // payload length
        reply[5] = 0x01; // page
        reply[6] = 0x81; // cmd
        reply[7] = 0x03; reply[8] = 0x20; // 800
        reply[9] = 0x06; reply[10] = 0x40; // 1600
        var decodedReply = CompXPageProtocol.TryDecodeReply(reply, 0x01, 0x81);
        Assert.True(decodedReply.Ok);
        Assert.Equal(4, decodedReply.Payload.Length);

        // Big-endian stages with [profile=1, count=3] header: 800/800, 1600/1200, 32000/32000
        byte[] stagePayload =
        [
            0x01, 0x03,
            0x03, 0x20, 0x03, 0x20,
            0x06, 0x40, 0x04, 0xB0,
            0x7D, 0x00, 0x7D, 0x00,
        ];
        var decoded = CompXPageProtocol.DecodeDpiStages(stagePayload);
        Assert.Equal(3, decoded.Count);
        Assert.Equal((800, 800), decoded[0]);
        Assert.Equal((1600, 1200), decoded[1]);
        Assert.Equal((32000, 32000), decoded[2]);

        Assert.Equal(8000, CompXPageProtocol.DecodePollingRate(0x80));
        Assert.Equal(4000, CompXPageProtocol.DecodePollingRate(0x40));
        Assert.Equal(300, CompXPageProtocol.DecodeSleepSeconds([0x01, 0x01, 0x2C]));
    }

    [Fact]
    public void Compx08_Ultra42000DpiAndSensorMode_RoundTrip()
    {
        var cases = new[] { (50, 50), (800, 800), (1600, 1200), (26000, 26000), (42000, 38000) };
        foreach (var (x, y) in cases)
        {
            byte[] encoded = Compx08Protocol.EncodeDpiStageUltra(x, y);
            Assert.Equal(4, encoded.Length);
            Assert.Equal(0x55, (encoded[0] + encoded[1] + encoded[2] + encoded[3]) & 0xFF);
            var decoded = Compx08Protocol.DecodeDpiStageUltra(encoded);
            Assert.Equal(x, decoded.X);
            Assert.Equal(y, decoded.Y);
        }

        byte[] modeRow = [0x01, 0x54, 0x06, 0x4F, 0x02, 0x53];
        Assert.Equal(2, Compx08Protocol.DecodeSensorMode(modeRow));
    }

    [Fact]
    public void Bitmouse_BuildRequest_ComputesAdditiveByte1Checksum()
    {
        byte[] req = BitmouseProtocol.BuildRequest(BitmouseProtocol.CmdSetReportRate, 0x01, 0x01, 0x00, [0x04]);
        Assert.Equal(64, req.Length);
        Assert.Equal(0x08, req[0]);
        Assert.Equal(0x72, req[2]);
        int expectedSum = 0;
        for (int i = 2; i < 64; i++) expectedSum += req[i];
        Assert.Equal((byte)(expectedSum & 0xFF), req[1]);
    }

    [Fact]
    public void MchoseV3_EncodeSettingsPreservesExtraTailAndSwapsDpiWriteOrder()
    {
        byte[] extraTail = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA];
        byte[] raw = [0x01, 0x52, 0x62, 0x05, 0x01, 0x07, 0xFD, 0x02, 0x03, .. extraTail];
        var parsed = MchoseProvider.ParseV3Settings(raw);
        Assert.Equal(1, parsed.ProfileIndex);
        Assert.Equal(2, parsed.DpiIndex);
        Assert.Equal(5, parsed.RawWiredRateSlot);
        Assert.Equal(6, parsed.RawWirelessRateSlot);
        Assert.Equal(-3, parsed.AngleTuning);
        Assert.Equal(extraTail, parsed.ExtraTail);

        byte[] reencoded = MchoseProvider.EncodeV3Settings(parsed);
        Assert.Equal(raw, reencoded);

        byte[] dpiWrite = MchoseProvider.EncodeV3DpiTableWrite(1, 0, true, 3, 1, [400, 800, 1600, 3200, 6400, 12800]);
        Assert.Equal(1, dpiWrite[0]); // profile
        Assert.Equal(0, dpiWrite[1]); // axis X
        Assert.Equal(1, dpiWrite[2]); // hasSeparateY (swapped before count on write!)
        Assert.Equal(3, dpiWrite[3]); // count
        Assert.Equal(1, dpiWrite[4]); // active
        Assert.Equal(800, dpiWrite[7] | (dpiWrite[8] << 8));
    }

    [Fact]
    public void GearHubMu_BuildBody64AndParseDpiBlock_WorkCleanly()
    {
        byte[] body = GearHubMuProtocol.BuildBody64([0xD4, 0x00, 0x01, 0x04]);
        int sum = 0;
        for (int i = 0; i < 7; i++) sum += body[i];
        Assert.Equal((byte)((255 - (sum & 0xFF)) & 0xFF), body[7]);

        var d4 = new byte[64];
        d4[0] = 0xD4; d4[1] = 1; d4[2] = 1; d4[3] = 2;
        d4[8] = 0x20; d4[9] = 0x03;   // Stage 1 X = 800
        d4[10] = 0x40; d4[11] = 0x06; // Stage 2 X = 1600
        d4[24] = 0x20; d4[25] = 0x03; // Stage 1 Y = 800
        d4[26] = 0xB0; d4[27] = 0x04; // Stage 2 Y = 1200
        var parsed = GearHubMuProtocol.ParseDpiBlock64(d4);
        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!.Profile);
        Assert.Equal(1, parsed.ActiveStage);
        Assert.Equal((1600, 1200), parsed.Stages[1]);
    }

    [Fact]
    public void Incott09_EncodeMacroBuffer_Encodes320BytesWithHeaderAndAsciiMarker()
    {
        var macro = new OnboardMacroDefinition
        {
            Name = "QuickScope",
            LoopMode = MacroLoopMode.RepeatCount,
            RepeatCount = 3,
            Steps =
            [
                new MacroStep(HidKeyCatalog.MouseRightCode, true, 18),
                new MacroStep(HidKeyCatalog.MouseLeftCode, true, 12),
                new MacroStep(HidKeyCatalog.MouseLeftCode, false, 10),
                new MacroStep(HidKeyCatalog.MouseRightCode, false, 20),
            ],
        };

        byte[] buf = Incott09Protocol.EncodeMacroBuffer(bufferSlot: 2, macro);
        Assert.Equal(320, buf.Length);
        Assert.Equal(2, buf[0]); // slot
        Assert.Equal(2, buf[1]); // RepeatCount mode
        Assert.Equal(3, buf[2]); // cycles low byte
        Assert.Equal(0x01, buf[4]); // step 0 press
        Assert.Equal(HidKeyCatalog.MouseRightCode, buf[5]);
        Assert.Equal(18, buf[6]);
        Assert.Equal(0x81, buf[4 + 2 * 4]); // step 2 release
        Assert.Equal((byte)'M', buf[288]);
        Assert.Equal((byte)'3', buf[293]);
        Assert.Equal(4, buf[318]);
    }

    [Fact]
    public void HidKeyCatalog_FormatAndParseKeyboardCombo_RoundTrips()
    {
        byte mod = HidKeyCatalog.ModLeftCtrl | HidKeyCatalog.ModLeftShift;
        string encoded = HidKeyCatalog.FormatKeyboardBinding(mod, 0x06); // C key
        Assert.Equal("Keyboard:3:6", encoded);
        Assert.True(HidKeyCatalog.TryParseKeyboardBinding(encoded, out byte outMod, out byte outUsage));
        Assert.Equal(mod, outMod);
        Assert.Equal(0x06, outUsage);
        Assert.Equal("Ctrl + Shift + C", HidKeyCatalog.FormatModifiersAndKey(outMod, outUsage));
    }
}
