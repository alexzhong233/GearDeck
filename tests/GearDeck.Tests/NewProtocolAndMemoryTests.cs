using System.IO;
using GearDeck.Core.Models;
using GearDeck.Core.Providers;
using GearDeck.Core.Services;
using GearDeck.Metrics;
using Xunit;

namespace GearDeck.Tests;

public class NewProtocolAndMemoryTests
{
    [Fact]
    public void ProviderIdHelper_ParsesVidPidAndPidFormats()
    {
        Assert.True(ProviderIdHelper.TryParseVidPid("compx:3710-0010", out ushort vid, out ushort pid));
        Assert.Equal((ushort)0x3710, vid);
        Assert.Equal((ushort)0x0010, pid);

        Assert.True(ProviderIdHelper.TryParseVidPid("openmouse:046D-C54F", out vid, out pid));
        Assert.Equal((ushort)0x046D, vid);
        Assert.Equal((ushort)0xC54F, pid);

        Assert.True(ProviderIdHelper.TryParseVidPid("razer:00C1", out vid, out pid, defaultVid: 0x1532));
        Assert.Equal((ushort)0x1532, vid);
        Assert.Equal((ushort)0x00C1, pid);

        Assert.True(ProviderIdHelper.TryParsePid("audeze:3329-4B18", out ushort singlePid));
        Assert.Equal((ushort)0x4B18, singlePid);

        Assert.False(ProviderIdHelper.TryParseVidPid("", out _, out _));
        Assert.False(ProviderIdHelper.TryParsePid("invalid", out _));
    }

    [Fact]
    public void EggWeProtocol_EncodesChecksumDpiAndDecodesProfile()
    {
        byte[] frame = EggWeProtocol.BuildCommandFrame(EggWeProtocol.CmdGetPower);
        Assert.Equal(1 + EggWeProtocol.PayloadLength, frame.Length);
        Assert.Equal(EggWeProtocol.ReportIdCmd, frame[0]);
        Assert.Equal(EggWeProtocol.CmdGetPower, frame[1]);
        int sum = 0;
        foreach (byte b in frame) sum += b;
        Assert.Equal(EggWeProtocol.ChecksumTarget, sum & 0xFF);

        byte[] packed = EggWeProtocol.PackDpiStage(1600, 800, 0x01);
        var unpacked = EggWeProtocol.UnpackDpiStage(packed);
        Assert.NotNull(unpacked);
        Assert.Equal((1600, 800), unpacked!.Value);

        Assert.Equal((byte)1, EggWeProtocol.EncodePollingRate(1000));
        Assert.Equal(500, EggWeProtocol.DecodePollingRate(2));

        byte[] powerReply = new byte[1 + EggWeProtocol.PayloadLength];
        powerReply[0] = EggWeProtocol.ReportIdNotify;
        powerReply[1] = EggWeProtocol.CmdGetPower;
        powerReply[6] = 78;
        powerReply[7] = 1;
        var power = EggWeProtocol.ParseGetPowerResponse(powerReply);
        Assert.Equal((78, true), power!.Value);

        byte[] mem = new byte[EggWeProtocol.ProfileEndOffset];
        var (rrVal, rrCrc) = EggWeProtocol.PackScalarPair(2);
        mem[EggWeProtocol.OffReportRate] = rrVal;
        mem[EggWeProtocol.OffReportRate + 1] = rrCrc;
        var (scVal, scCrc) = EggWeProtocol.PackScalarPair(2);
        mem[EggWeProtocol.OffDpiStageCount] = scVal;
        mem[EggWeProtocol.OffDpiStageCount + 1] = scCrc;
        var (actVal, actCrc) = EggWeProtocol.PackScalarPair(1);
        mem[EggWeProtocol.OffActiveDpiLevel] = actVal;
        mem[EggWeProtocol.OffActiveDpiLevel + 1] = actCrc;
        Array.Copy(EggWeProtocol.PackDpiStage(400, 400), 0, mem, EggWeProtocol.OffDpiStages, 4);
        Array.Copy(EggWeProtocol.PackDpiStage(1600, 1600), 0, mem, EggWeProtocol.OffDpiStages + 4, 4);

        var prof = EggWeProtocol.DecodeProfile(mem);
        Assert.Equal(500, prof.PollingRateHz);
        Assert.Equal(2, prof.DpiStageCount);
        Assert.Equal(1, prof.ActiveDpiLevel);
        Assert.Equal((1600, 1600), prof.Stages[1]);
    }

    [Fact]
    public void NinjutsoProtocol_RoundTripsDpiPollingAndResponses()
    {
        byte[] req = NinjutsoProtocol.BuildCurrentRequest(NinjutsoProtocol.CmdDpi, profile: 1, [0x02]);
        Assert.Equal(NinjutsoProtocol.CurrentReportId, req[0]);
        Assert.Equal(NinjutsoProtocol.CmdDpi, req[1]);
        Assert.Equal((byte)1, req[6]);
        Assert.Equal((byte)1, req[7]);
        Assert.Equal((byte)0x02, req[8]);

        byte[]? extracted = NinjutsoProtocol.ExtractCurrentResponseValue(
            [0x06, NinjutsoProtocol.CmdBatteryPercent, 0, 0, 1, 0, 1, 0, 88],
            NinjutsoProtocol.CmdBatteryPercent);
        Assert.NotNull(extracted);
        Assert.Equal((byte)88, extracted![0]);

        Assert.Equal((byte)4, NinjutsoProtocol.EncodePollingRate(8000));
        Assert.Equal(4000, NinjutsoProtocol.DecodePollingRate(3));

        var direct = NinjutsoProtocol.EncodeDpi(1600, direct: true);
        Assert.Equal(1600, NinjutsoProtocol.DecodeDpi(direct.Low, direct.High, direct.Tenth, direct: true));

        var stepped = NinjutsoProtocol.EncodeDpi(800, direct: false);
        Assert.Equal(800, NinjutsoProtocol.DecodeDpi(stepped.Low, stepped.High, stepped.Tenth, direct: false));

        var legacy = NinjutsoProtocol.EncodeLegacyDpi(1600);
        Assert.Equal(1600, NinjutsoProtocol.DecodeLegacyDpi(legacy.Low, legacy.High));
    }

    [Fact]
    public void FinalmouseProtocol_EncodesAndDecodesTelemetry()
    {
        byte[] pkt = FinalmouseProtocol.BuildReport(FinalmouseProtocol.ReportUlxOut, FinalmouseProtocol.CmdDpi, [0x20, 0x03]);
        Assert.Equal(1 + FinalmouseProtocol.PayloadLen, pkt.Length);
        Assert.Equal(FinalmouseProtocol.ReportUlxOut, pkt[0]);
        Assert.Equal((byte)(0x80 | FinalmouseProtocol.CmdDpi), pkt[2]);

        var tel = new FinalmouseProtocol.Telemetry();
        Assert.True(FinalmouseProtocol.TryDecodeReport([FinalmouseProtocol.ReportUlxIn, 4, FinalmouseProtocol.CmdDpi, 2, 0x40, 0x06], tel));
        Assert.Equal(1600, tel.Dpi);

        Assert.True(FinalmouseProtocol.TryDecodeReport([FinalmouseProtocol.ReportUlxIn, 4, FinalmouseProtocol.CmdPollingRate, 2, 0xA0, 0x0F], tel));
        Assert.Equal(4000, tel.PollingRateHz);

        Assert.InRange(FinalmouseProtocol.BatteryPercentFromMv(4170), 70, 80);
        Assert.Equal(100, FinalmouseProtocol.BatteryPercentFromMv(4400));
    }

    [Fact]
    public void RapooProtocol_BuildsAndParsesFrames()
    {
        byte[] pkt = RapooProtocol.BuildCommandReport(RapooProtocol.CmdRead, RapooProtocol.AddrDpiX);
        Assert.Equal(RapooProtocol.ConfigReportId, pkt[0]);
        Assert.Equal(RapooProtocol.ConnReceiver, pkt[1]);
        Assert.Equal(RapooProtocol.CmdRead, pkt[2]);
        Assert.Equal(90, RapooProtocol.ParseStatusFeatureBattery([RapooProtocol.StatusReportId, 0, 0, 0, 0, 0, 0, 0, 0, 90])!.Value);

        var bat = RapooProtocol.ParseBatteryAnswer([RapooProtocol.ConfigReportId, RapooProtocol.StatusOk, 2, 74]);
        Assert.Equal((74, true), bat!.Value);

        Assert.Equal((byte)0x81, RapooProtocol.EncodePollingRateCode(8000));
        Assert.Equal(4000, RapooProtocol.DecodePollingRateHz(0x82));
    }

    [Fact]
    public void AudezeProvider_FragmentsAndParsesBattery()
    {
        byte[] msg = AudezeProvider.BuildMessage(AudezeProvider.TypeRequest, [0xD6, 0x0C, 0x00]);
        var reports = AudezeProvider.BuildReports(msg);
        Assert.Single(reports);
        Assert.Equal(AudezeProvider.ReportIdOut, reports[0][0]);

        byte[] rawResp = AudezeProvider.BuildMessage(AudezeProvider.TypeResult, [0xD6, 0x0C, 0x00, 0x00, 83]);
        var stream = new List<byte> { 0xFF, 0x00 };
        stream.AddRange(rawResp);
        byte[]? extracted = AudezeProvider.TryExtractMessage(stream);
        Assert.NotNull(extracted);
        Assert.Equal(83, AudezeProvider.ParseBatteryMessage(extracted)!.Value);
    }

    [Fact]
    public void SonyPulse3DAndKeychronMouseExclusion_WorkAsExpected()
    {
        var pulse = SonyInzoneProvider.ParsePulse3DReport([0xB0, 0x00, 0x00, 85, 0x08, 0x00, 0x00, 0x00]);
        Assert.NotNull(pulse);
        Assert.Equal(85, pulse!.Value.Level);
        Assert.False(pulse.Value.Charging);
        Assert.True(pulse.Value.Connected);

        var pulseChg = SonyInzoneProvider.ParsePulse3DReport([0xB0, 0x00, 0x00, 0x80, 0x08, 0x00, 0x00, 0x00]);
        Assert.NotNull(pulseChg);
        Assert.True(pulseChg!.Value.Charging);
        Assert.True(pulseChg.Value.Connected);

        Assert.True(ViaKeyboardProvider.IsKeychronMouseName("Keychron M6 8K Wireless Mouse"));
        Assert.False(ViaKeyboardProvider.IsKeychronMouseName("Keychron Q1 Pro Mechanical Keyboard"));
    }

    [Fact]
    public void DeviceImageRuleMatcher_PrefersNameRegexOverSharedReceiver()
    {
        string jsonPath = Path.Combine(AppContext.BaseDirectory, "Assets", "device-images.json");
        if (!File.Exists(jsonPath))
            jsonPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GearDeck.Desktop", "Assets", "device-images.json"));

        var manifest = DeviceImageRuleMatcher.ParseManifest(File.ReadAllText(jsonPath));
        Assert.NotNull(manifest);
        Assert.True(manifest.Version >= 6);

        var superlightDev = new PeripheralDevice
        {
            Id = "logi:c54f-1",
            VendorId = 0x046D,
            ProductId = 0xC54F,
            Name = "Logitech G PRO X Superlight 2",
            Kind = DeviceKind.Mouse,
            Provider = "Logitech",
        };
        var superlightByName = DeviceImageRuleMatcher.MatchRule(superlightDev, manifest.Rules);
        Assert.NotNull(superlightByName.Rule);
        Assert.Equal("https://img.openmouse.app/logitech-pro-x-superlight-2c.png",
            superlightByName.CloudUrl ?? DeviceImageRuleMatcher.ResolveRuleCloudUrl(superlightByName.Rule));

        var superstrikeDev = new PeripheralDevice
        {
            Id = "logi:c54f-2",
            VendorId = 0x046D,
            ProductId = 0xC54F,
            Name = "PRO X3 SUPERSTRIKE",
            Kind = DeviceKind.Mouse,
            Provider = "Logitech",
        };
        var superstrikeByName = DeviceImageRuleMatcher.MatchRule(superstrikeDev, manifest.Rules);
        Assert.NotNull(superstrikeByName.Rule);
        Assert.Equal("https://img.openmouse.app/logitech-pro-x3-superstrike.png",
            superstrikeByName.CloudUrl ?? DeviceImageRuleMatcher.ResolveRuleCloudUrl(superstrikeByName.Rule));
    }

    [Fact]
    public void SystemMetricsEngine_FromMetricIds_AvoidsGpuForNetworkOnly()
    {
        var netScope = SystemMetricsEngine.FromMetricIds("NET_DOWN", "NET_UP");
        Assert.Equal(MetricScope.Network, netScope);

        var gpuScope = SystemMetricsEngine.FromMetricIds("CPU", "VRAM");
        Assert.True(gpuScope.HasFlag(MetricScope.Cpu));
        Assert.True(gpuScope.HasFlag(MetricScope.Gpu) || gpuScope.HasFlag(MetricScope.Vram));
    }

    [Fact]
    public void HidSafe_BeginScanSnapshot_SupportsNestedScopesAndCleanDisposal()
    {
        using var outer = GearDeck.Core.Hid.HidSafe.BeginScanSnapshot();
        using var inner = GearDeck.Core.Hid.HidSafe.BeginScanSnapshot();
        var byVid = GearDeck.Core.Hid.HidSafe.EnumerateDevicesByVendor(0xFFFF);
        var exact = GearDeck.Core.Hid.HidSafe.EnumerateDevices(0xFFFF, 0xFFFF);
        Assert.NotNull(byVid);
        Assert.NotNull(exact);
    }

    [Fact]
    public void HidKeyCatalog_WheelCodes_DescribeAndIdentifyCorrectly()
    {
        Assert.True(GearDeck.Core.Models.HidKeyCatalog.IsWheelCode(GearDeck.Core.Models.HidKeyCatalog.MouseWheelUpCode));
        Assert.True(GearDeck.Core.Models.HidKeyCatalog.IsWheelCode(GearDeck.Core.Models.HidKeyCatalog.MouseWheelDownCode));
        Assert.False(GearDeck.Core.Models.HidKeyCatalog.IsWheelCode(GearDeck.Core.Models.HidKeyCatalog.MouseLeftCode));
        Assert.Contains("滚轮向上", GearDeck.Core.Models.HidKeyCatalog.DescribeKeyCode(GearDeck.Core.Models.HidKeyCatalog.MouseWheelUpCode));
        Assert.Contains("滚轮向下", GearDeck.Core.Models.HidKeyCatalog.DescribeKeyCode(GearDeck.Core.Models.HidKeyCatalog.MouseWheelDownCode));
    }
}
