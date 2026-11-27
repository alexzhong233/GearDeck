using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// Finalmouse (UltralightX / Starlight X，VID 0x361D) 协议编解码与控制（基于 OpenMouse mouse-protocol）：
/// ULX 使用 Output 0x04 / Input 0x05 (63B)，SLX 使用 Output 0x01 / Input 0x02 (63B)。
/// </summary>
public static class FinalmouseProtocol
{
    public const ushort VendorId = 0x361D;
    public const byte ReportDongleOut = 0x02;
    public const byte ReportDongleIn = 0x03;
    public const byte ReportUlxOut = 0x04;
    public const byte ReportUlxIn = 0x05;
    public const byte ReportSlxOut = 0x01;
    public const byte ReportSlxIn = 0x02;
    public const int PayloadLen = 63;

    public const byte CmdDpi = 16;
    public const byte CmdPollingRate = 17;
    public const byte CmdMotionSync = 18;
    public const byte CmdLod = 21;
    public const byte CmdPawLod = 25;
    public const byte CmdWakeAll = 96;
    public const byte CmdDongleInfo = 10;

    public static readonly int[] UlxPollingRates = [500, 1000, 2000, 4000, 8000];

    public sealed class Telemetry
    {
        public int? Dpi { get; set; }
        public int? PollingRateHz { get; set; }
        public int? BatteryPercent { get; set; }
        public int? BatterySoc { get; set; }
        public int? ChargingState { get; set; }
        public int? LodMm { get; set; }
        public double? PawLodMm { get; set; }
        public bool? MotionSync { get; set; }
    }

    public static bool IsSlxPid(ushort pid) => pid is 0x0300 or 0x0301 or 0x0310 or 0x0311;

    public static byte[] BuildReport(byte reportId, byte command, ReadOnlySpan<byte> payload = default)
    {
        var pkt = new byte[1 + PayloadLen];
        pkt[0] = reportId;
        int pLen = Math.Min(payload.Length, PayloadLen - 3);
        pkt[1] = (byte)(2 + pLen);
        pkt[2] = (byte)(0x80 | (command & 0x7F));
        pkt[3] = (byte)pLen;
        if (pLen > 0)
            payload[..pLen].CopyTo(pkt.AsSpan(4));
        return pkt;
    }

    public static int BatteryPercentFromMv(int millivolts)
    {
        if (millivolts <= 0) return 0;
        double v = millivolts / 1000.0;
        double[] volts = [3.0, 3.62, 3.66, 3.74, 3.88, 4.17, 4.38];
        double[] pcts = [0.2, 5.0, 10.0, 25.0, 50.0, 75.0, 100.0];
        if (v >= volts[^1]) return 100;
        if (v <= volts[0]) return 0;
        for (int i = 0; i < volts.Length - 1; i++)
        {
            if (v < volts[i] || v > volts[i + 1]) continue;
            double frac = (v - volts[i]) / (volts[i + 1] - volts[i]);
            return (int)Math.Round(pcts[i] + (pcts[i + 1] - pcts[i]) * frac);
        }
        return 0;
    }

    public static bool TryDecodeReport(ReadOnlySpan<byte> raw, Telemetry target)
    {
        if (raw.Length < 4) return false;
        byte reportId = raw[0];
        if (reportId != ReportUlxIn && reportId != ReportSlxIn && reportId != ReportDongleIn)
            return false;

        ReadOnlySpan<byte> data = raw[1..];
        int innerLen = Math.Min(data[0], data.Length - 1);
        if (innerLen < 2) return false;
        ReadOnlySpan<byte> body = data.Slice(1, innerLen);
        byte cmd = body[0];
        int pLen = Math.Min(body[1], Math.Max(0, body.Length - 2));
        ReadOnlySpan<byte> payload = body.Slice(2, pLen);
        if (reportId == ReportDongleIn) return false;

        switch (cmd)
        {
            case 3 or CmdDpi when payload.Length >= 2:
                target.Dpi = payload[0] | (payload[1] << 8);
                return true;
            case 4 or CmdPollingRate when payload.Length >= 2:
                target.PollingRateHz = payload[0] | (payload[1] << 8);
                return true;
            case 5 when payload.Length >= 2:
                target.BatteryPercent = BatteryPercentFromMv(payload[0] | (payload[1] << 8));
                return true;
            case 18 when payload.Length >= 1:
                target.MotionSync = payload[0] == 1;
                return true;
            case 21 when payload.Length >= 1:
                target.LodMm = payload[0];
                return true;
            case 25 when payload.Length >= 1:
                target.PawLodMm = payload[0] switch
                {
                    1 => 0.7,
                    2 => 1.0,
                    3 => 2.0,
                    >= 7 and <= 17 => payload[0] / 10.0,
                    _ => null,
                };
                return true;
            case 37 when payload.Length >= 1:
                target.ChargingState = payload[0];
                return true;
            case 38 when payload.Length >= 3:
                target.BatterySoc = payload[0];
                return true;
            default:
                return false;
        }
    }

    public static (int Level, bool Charging)? QueryBattery(ushort pid, List<HidDevice> ifaces)
    {
        var tel = ProbeTelemetry(pid, ifaces);
        if (tel == null) return null;
        int? pct = tel.BatterySoc ?? tel.BatteryPercent;
        if (pct == null || pct is < 0 or > 100) return null;
        return (pct.Value, tel.ChargingState == 1);
    }

    public static DeviceTuningCapabilities QueryCapabilities(string deviceId, ushort pid, List<HidDevice> ifaces, string? modelName)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        var tel = ProbeTelemetry(pid, ifaces);
        if (tel == null || tel.Dpi == null)
        {
            caps.StatusMessage = "Finalmouse 当前休眠中或未连接 0xFF00 控制通道，请晃动鼠标后重试。";
            return caps;
        }

        bool slx = IsSlxPid(pid);
        caps.IsOnline = true;
        caps.StatusMessage = $"{modelName ?? (slx ? "Finalmouse Starlight X" : "Finalmouse UltralightX")} · xpanel 协议";
        caps.SupportsDpi = true;
        caps.MinDpi = 50;
        caps.MaxDpi = 26000;
        caps.DpiStep = 50;
        caps.CurrentDpiX = tel.Dpi.Value;
        caps.CurrentDpiY = tel.Dpi.Value;

        if (!slx)
        {
            caps.SupportedPollingRates = [.. UlxPollingRates];
            caps.CurrentPollingRate = tel.PollingRateHz ?? 1000;
            caps.SupportsMotionSync = true;
            caps.MotionSync = tel.MotionSync ?? false;
            caps.SupportedLodOptions = [new LodOption(1, "1 mm (Medium)"), new LodOption(2, "2 mm (High)")];
            caps.CurrentLod = tel.LodMm is 2 ? 2 : 1;
        }
        else if (tel.PollingRateHz is { } rate)
        {
            caps.SupportedPollingRates = [rate];
            caps.CurrentPollingRate = rate;
        }
        return caps;
    }

    public static DeviceTuningResult ApplySettings(string deviceId, ushort pid, List<HidDevice> ifaces, string? modelName, DeviceTuningUpdate update)
    {
        var dev = OpenVendorDevice(ifaces);
        if (dev == null)
            return new DeviceTuningResult(false, "无法打开 Finalmouse 0xFF00 控制通道。");

        bool slx = IsSlxPid(pid);
        byte tx = slx ? ReportSlxOut : ReportUlxOut;
        var applied = new List<string>();
        var errors = new List<string>();

        using (dev)
        {
            if (update.Dpi is var (dx, _))
            {
                int clamped = Math.Clamp(dx, 50, 26000);
                if (dev.WriteOutput(BuildReport(tx, CmdDpi, [(byte)(clamped & 0xFF), (byte)(clamped >> 8)])))
                    applied.Add($"DPI {clamped}");
                else
                    errors.Add("DPI");
            }
            if (!slx && update.PollingRateHz is { } hz && UlxPollingRates.Contains(hz))
            {
                if (dev.WriteOutput(BuildReport(tx, CmdPollingRate, [(byte)(hz & 0xFF), (byte)(hz >> 8)])))
                    applied.Add($"{hz}Hz");
                else
                    errors.Add("回报率");
            }
            if (!slx && update.MotionSync is { } ms)
            {
                if (dev.WriteOutput(BuildReport(tx, CmdMotionSync, [(byte)(ms ? 1 : 0)])))
                    applied.Add($"MotionSync {(ms ? "开" : "关")}");
                else
                    errors.Add("MotionSync");
            }
            if (update.LodCode is { } lod)
            {
                byte val = (byte)(lod >= 2 ? 2 : 1);
                byte cmd = slx ? CmdPawLod : CmdLod;
                byte wireVal = slx ? (byte)(val == 2 ? 3 : 2) : val;
                if (dev.WriteOutput(BuildReport(tx, cmd, [wireVal])))
                    applied.Add($"LOD {val}mm");
                else
                    errors.Add("LOD");
            }
        }

        var refreshed = QueryCapabilities(deviceId, pid, ifaces, modelName);
        if (applied.Count == 0 && errors.Count == 0)
            return new DeviceTuningResult(true, "未变更 Finalmouse 参数", refreshed);
        return errors.Count > 0
            ? new DeviceTuningResult(applied.Count > 0, $"部分设置失败 ({string.Join("、", errors)})", refreshed)
            : new DeviceTuningResult(true, $"已写入 {string.Join(" / ", applied)}", refreshed);
    }

    private static HidChannel? OpenVendorDevice(List<HidDevice> ifaces)
    {
        var dev = ifaces
            .Where(d => HidSafe.PrimaryUsage(d).UsagePage == 0xFF00 && HidSafe.MaxOutputLen(d) >= 64)
            .OrderByDescending(d => HidSafe.PrimaryUsage(d).Usage == 0x0001 ? 1 : 0)
            .FirstOrDefault();
        return dev != null ? HidChannel.TryOpen(dev) : null;
    }

    private static Telemetry? ProbeTelemetry(ushort pid, List<HidDevice> ifaces)
    {
        using var ch = OpenVendorDevice(ifaces);
        if (ch == null) return null;
        while (ch.Read(10) != null) { }

        bool slx = IsSlxPid(pid);
        byte tx = slx ? ReportSlxOut : ReportUlxOut;
        ch.WriteOutput(BuildReport(tx, slx ? (byte)57 : CmdLod));
        ch.WriteOutput(BuildReport(tx, CmdWakeAll));
        ch.WriteOutput(BuildReport(slx ? tx : ReportDongleOut, CmdDongleInfo));

        var tel = new Telemetry();
        bool gotAny = false;
        long dl = Environment.TickCount64 + 750;
        while (Environment.TickCount64 < dl)
        {
            var r = ch.Read(90);
            if (r == null)
            {
                if (gotAny) break;
                continue;
            }
            if (TryDecodeReport(r, tel))
                gotAny = true;
        }
        return gotAny ? tel : null;
    }
}
