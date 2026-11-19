using GearDeck.Core.Providers;
using Xunit;

public class BatteryMathTests
{
    [Theory]
    [InlineData(255, 100)]
    [InlineData(0, 0)]
    [InlineData(128, 50)]
    public void RazerScale(int raw, int expected)
        => Assert.Equal(expected, (raw * 100 + 127) / 255);
}

public class AtkFrameTests
{
    [Fact]
    public void BatteryQueryFrame_HasValidChecksum()
    {
        var frame = new byte[16];
        frame[0] = 0x04;
        int sum = 0x08;
        foreach (var b in frame[..15]) sum += b;
        frame[^1] = (byte)(0x55 - (sum & 0xFF));
        Assert.Equal(0x55 - (0x08 + 0x04), frame[15]);
    }
}

public class CompXParseTests
{
    [Fact]
    public void ParseFrame_LayoutA()
    {
        var reply = new byte[65];
        reply[1] = 0xA1;
        reply[3] = 0x02; reply[4] = 0x02; reply[6] = 0x83;
        reply[7] = 0x00; reply[8] = 0x64;
        var hit = CompXProvider.ParseFrame(reply);
        Assert.NotNull(hit);
        Assert.Equal(100, hit!.Value.Battery);
        Assert.False(hit.Value.Charging);
    }

    [Fact]
    public void ParseFrame_LayoutB_Wlmouse()
    {
        var reply = new byte[65];
        reply[1] = 0xA2;
        reply[2] = 0x02; reply[3] = 0x02; reply[5] = 0x83;
        reply[6] = 0x01; reply[7] = 0x50;
        var hit = CompXProvider.ParseFrame(reply);
        Assert.NotNull(hit);
        Assert.Equal(80, hit!.Value.Battery);
        Assert.True(hit.Value.Charging);
    }

    [Fact]
    public void ParseFrame_AsleepReturnsNull()
    {
        var reply = new byte[65];
        reply[1] = 0xA0;
        reply[2] = 0x02; reply[3] = 0x02; reply[5] = 0x83;
        Assert.Null(CompXProvider.ParseFrame(reply));
    }
}

public class LogitechFrameTests
{
    [Fact]
    public void LongReportLayout()
    {
        var msg = new byte[20];
        msg[0] = 0x11;
        msg[1] = 0x01;
        msg[2] = 0x00;
        msg[3] = (byte)((0 << 4) | 0x0D);
        Assert.Equal(0x11, msg[0]);
        Assert.Equal(0x0D, msg[3]);
    }
}
