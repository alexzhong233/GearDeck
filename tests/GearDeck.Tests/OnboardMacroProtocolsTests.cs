using System.Text;
using GearDeck.Core.Models;
using GearDeck.Core.Providers;
using Xunit;

namespace GearDeck.Tests;

public class OnboardMacroProtocolsTests
{
    private static OnboardMacroDefinition SampleMacro(MacroLoopMode mode = MacroLoopMode.RepeatCount)
    {
        return new OnboardMacroDefinition
        {
            Id = "sample1",
            Name = "连招测试",
            LoopMode = mode,
            RepeatCount = 2,
            Steps =
            [
                new MacroStep(0x04, true, 0),   // Key A Down
                new MacroStep(0x00, false, 25), // 25ms Delay
                new MacroStep(0x04, false, 0),  // Key A Up
                new MacroStep(HidKeyCatalog.MouseLeftCode, true, 0),
                new MacroStep(HidKeyCatalog.MouseLeftCode, false, 10),
            ],
        };
    }

    [Fact]
    public void ViaMacro_EncodeAndSpliceBuffer_PreservesNullDelimitersAndTrailingZero()
    {
        var encoded = ViaKeyboardProvider.EncodeViaMacroBytes(SampleMacro());
        // SS_QMK_PREFIX(0x01), SS_DOWN(0x02), 'A'(0x04), SS_DELAY(0x01,0x04,"25|"), SS_UP(0x01,0x03,0x04)
        Assert.DoesNotContain((byte)0x00, encoded);
        Assert.Equal([0x01, 0x02, 0x04, 0x01, 0x04, (byte)'2', (byte)'5', (byte)'|', 0x01, 0x03, 0x04], encoded);

        var existing = new byte[64];
        existing[0] = (byte)'H';
        existing[1] = (byte)'i';
        existing[2] = 0x00; // slot 0 = "Hi"
        existing[3] = 0x00; // slot 1 = empty
        existing[4] = (byte)'Z';
        existing[5] = 0x00; // slot 2 = "Z"

        byte[]? updated = ViaKeyboardProvider.BuildUpdatedViaMacroBuffer(existing, 1, 4, encoded, 64, out string? err);
        Assert.Null(err);
        Assert.NotNull(updated);
        Assert.Equal(64, updated!.Length);
        Assert.Equal(0x00, updated[^1]);
        Assert.Equal((byte)'H', updated[0]);
        Assert.Equal((byte)'i', updated[1]);
        Assert.Equal((byte)0x00, updated[2]);
        Assert.Equal(encoded, updated[3..(3 + encoded.Length)]);
        Assert.Equal((byte)0x00, updated[3 + encoded.Length]);
        Assert.Equal((byte)'Z', updated[4 + encoded.Length]);
    }

    [Fact]
    public void ViaMacro_BufferOverflow_ReturnsErrorAndNull()
    {
        byte[]? res = ViaKeyboardProvider.BuildUpdatedViaMacroBuffer(new byte[16], 0, 4, new byte[20], 16, out string? err);
        Assert.Null(res);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void LogitechOnboard_CrcCcitt_MatchesStandardVector_AndValidatesClassicLayout()
    {
        ushort crc = LogitechOnboardMacroProtocol.CrcCcitt(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal((ushort)0x29B1, crc);

        var classic = new LogitechOnboardMacroProtocol.OnboardProfilesDescriptor(
            MemoryModelId: 0x01, ProfileFormatId: 0x03, MacroFormatId: 0x01,
            ProfileCount: 3, ProfileCountOob: 1, ButtonCount: 11, SectorCount: 16, SectorSize: 256);
        Assert.True(LogitechOnboardMacroProtocol.IsSupportedClassicMacroLayout(classic));

        var newerLayout = classic with { ProfileFormatId = 0x06 };
        Assert.False(LogitechOnboardMacroProtocol.IsSupportedClassicMacroLayout(newerLayout));

        var sector = LogitechOnboardMacroProtocol.EncodeMacroSector(SampleMacro(MacroLoopMode.WhileHeld));
        Assert.Equal(256, sector.Length);
        // Key A down (0x43 0x00 0x04), Delay 25ms (0x40 0x00 0x19), Key A up (0x44 0x00 0x04)
        Assert.Equal([0x43, 0x00, 0x04, 0x40, 0x00, 0x19, 0x44, 0x00, 0x04], sector[0..9]);

        // Valid profile sector patches button binding and refreshes CRC-CCITT; invalid CRC is rejected
        var profile = new byte[256];
        ushort origCrc = LogitechOnboardMacroProtocol.CrcCcitt(profile.AsSpan(0, 254));
        profile[254] = (byte)(origCrc >> 8);
        profile[255] = (byte)(origCrc & 0xFF);
        Assert.True(LogitechOnboardMacroProtocol.TryPatchProfileButtonWithMacro(profile, 4, 0x05, 0x00));
        Assert.Equal([0x00, 0x05, 0x00, 0x00], profile[48..52]);
        ushort updatedCrc = (ushort)((profile[254] << 8) | profile[255]);
        Assert.Equal(LogitechOnboardMacroProtocol.CrcCcitt(profile.AsSpan(0, 254)), updatedCrc);

        profile[254] ^= 0xFF; // corrupt CRC
        Assert.False(LogitechOnboardMacroProtocol.TryPatchProfileButtonWithMacro(profile, 4, 0x05, 0x00));
    }

    [Fact]
    public void LogitechG600_ParsesAndPatchesProfile154Bytes()
    {
        var buf = new byte[LogitechG600Protocol.ProfileReportSize];
        buf[0] = 0xF3;
        buf[11] = 1; // 1000 / (1+1) = 500 Hz
        buf[13] = 2;
        buf[14] = 8;  // 400 DPI
        buf[15] = 16; // 800 DPI
        buf[16] = 32; // 1600 DPI
        buf[17] = 64; // 3200 DPI
        buf[31 + 3 * 3] = 0x04; // slot 3 = 后退

        var caps = new DeviceTuningCapabilities { DeviceId = "logi:C24A:FF" };
        LogitechG600Protocol.PopulateCapabilitiesFromProfile(buf, activeProfile: 0, activeDpiSlot: 1, caps);
        Assert.Equal(500, caps.CurrentPollingRate);
        Assert.Equal(800, caps.CurrentDpiX);
        Assert.Equal("后退", caps.ButtonBindings.First(b => b.SlotIndex == 3).CurrentAction);

        string shortcut = HidKeyCatalog.FormatKeyboardBinding(HidKeyCatalog.ModLeftCtrl, 0x06); // Ctrl+C
        Assert.True(LogitechG600Protocol.PatchButtonBinding(buf, 8, shortcut));
        Assert.Equal([0x00, HidKeyCatalog.ModLeftCtrl, 0x06], buf[(31 + 8 * 3)..(31 + 9 * 3)]);
    }

    [Fact]
    public void Sinowealth_EncodesDpiPollingMacroReportAndButtonBinding()
    {
        Assert.Equal((byte)7, SinowealthProvider.EncodeDpi(0x06, 800));  // PMW3360: (800/100)-1
        Assert.Equal(800, SinowealthProvider.DecodeDpi(0x06, 7));
        Assert.Equal((byte)8, SinowealthProvider.EncodeDpi(0x0F, 800));  // PMW3389: 800/100
        Assert.Equal((byte)0x04, SinowealthProvider.EncodePollingRate(1000));

        var report = SinowealthProvider.EncodeMacroReport(SinowealthProvider.ReportIdConfig, 2, SampleMacro());
        Assert.Equal(SinowealthProvider.ConfigReportSize, report.Length);
        Assert.Equal(0x04, report[0]);
        Assert.Equal(0x30, report[1]);
        Assert.Equal(0x02, report[2]);
        Assert.Equal(0x02, report[8]);
        Assert.Equal(4, report[10]); // 4 action events (delay merged onto event 0)
        // Event 0: Key Down A with 25ms delay attached
        Assert.Equal([0x50, 25, 0x04], report[11..14]);
        // Event 1: Key Up A
        Assert.Equal([0xD0, 1, 0x04], report[14..17]);
        // Event 2: Mouse Left Press
        Assert.Equal([0x10, 1, 0x01], report[17..20]);

        var btnReport = new byte[SinowealthProvider.ConfigReportSize];
        Assert.True(SinowealthProvider.PatchButtonReportWithMacro(btnReport, 4, 2, MacroLoopMode.RepeatCount, 3));
        Assert.Equal((byte)80, btnReport[3]);
        Assert.Equal([0x70, 2, 0x01, 3], btnReport[(8 + 4 * 4)..(8 + 5 * 4)]);
    }

    [Fact]
    public void Redragon_EncodesMacro256PacketAndProfilePackets()
    {
        var pkt = RedragonProvider.EncodeMacroPacket(0, SampleMacro());
        Assert.Equal(256, pkt.Length);
        Assert.Equal([0x04, 0xF3, 0x78, 0x04, 0xC8], pkt[0..5]);
        // Offset 8: Key A down (0x84, 0x04, 0x00), Delay 25ms (0x06, 25, 0x00), Key A up (0x04, 0x04, 0x00)
        Assert.Equal([0x84, 0x04, 0x00, 0x06, 25, 0x00, 0x04, 0x04, 0x00], pkt[8..17]);

        var profPkts = RedragonProvider.BuildProfileSwitchPackets(2);
        Assert.Equal(6, profPkts.Length);
        Assert.Equal((byte)2, profPkts[0][8]);
    }
}
