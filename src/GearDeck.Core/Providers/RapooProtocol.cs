using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// Rapoo 雷柏 VT 系列（VT9 Pro / VT3 Pro / VT1 Pro Max，VID 0x24AE）协议编解码与读写（基于 OpenMouse mouse-protocol）：
/// - 0xFF0B:0x0104 Feature Report 0x2A 读取电量；
/// - 0xFF00:0x000E Output Report 0xBA + GET_REPORT(Input) 进行寄存器读写与有损信道重试。
/// </summary>
public static class RapooProtocol
{
    public const ushort RapooVid = 0x24AE;
    public const byte ConfigReportId = 0xBA;
    public const byte StatusReportId = 0x2A;
    public const int FrameLength = 31;
    public const byte CmdRead = 0xA4;
    public const byte CmdWrite = 0xA5;
    public const byte CmdBattery = 0xAA;
    public const byte StatusOk = 0x01;
    public const byte ConnReceiver = 0xA5;
    public const byte ConnWired = 0xFF;

    public const uint AddrPerformance = 0x880;
    public const uint AddrSensor = 0x884;
    public const uint AddrDpiX = 0x888;
    public const uint AddrActiveStage = 0x898;
    public const uint AddrTiming = 0x8C0;
    public const uint AddrDpiY = 0x8C8;

    public static readonly int[] DebounceTableMs = [1, 2, 4, 8, 16, 24, 32];
    public static readonly int[] SupportedPollingRates = [125, 250, 500, 1000, 2000, 4000];

    public static byte[] BuildCommandReport(byte command, uint address, ReadOnlySpan<byte> data = default, byte? lengthOverride = null, byte connection = ConnReceiver)
    {
        var pkt = new byte[1 + FrameLength];
        pkt[0] = ConfigReportId;
        pkt[1] = connection;
        pkt[2] = command;
        pkt[3] = lengthOverride ?? (byte)data.Length;
        pkt[4] = (byte)(address & 0xFF);
        pkt[5] = (byte)((address >> 8) & 0xFF);
        pkt[6] = (byte)((address >> 16) & 0xFF);
        pkt[7] = (byte)((address >> 24) & 0xFF);
        int copy = Math.Min(data.Length, FrameLength - 7);
        if (copy > 0)
            data[..copy].CopyTo(pkt.AsSpan(8));
        return pkt;
    }

    public static int? ParseStatusFeatureBattery(ReadOnlySpan<byte> bytes)
    {
        int offset = (bytes.Length > 0 && bytes[0] == StatusReportId) ? 1 : 0;
        if (bytes.Length < offset + 9) return null;
        byte pct = bytes[offset + 8];
        return pct <= 100 ? pct : null;
    }

    public static (bool Ok, byte Marker, byte[] Payload) DecodeAnswer(ReadOnlySpan<byte> bytes)
    {
        int offset = (bytes.Length > 0 && bytes[0] == ConfigReportId) ? 1 : 0;
        var data = bytes[offset..];
        if (data.Length < 4) return (false, 0, []);
        return (data[0] == StatusOk, data[1], data[4..].ToArray());
    }

    public static (int Percent, bool Charging)? ParseBatteryAnswer(ReadOnlySpan<byte> bytes)
    {
        int offset = (bytes.Length > 0 && bytes[0] == ConfigReportId) ? 1 : 0;
        var data = bytes[offset..];
        if (data.Length < 3 || data[0] != StatusOk) return null;
        byte marker = data[1];
        if (marker is not (1 or 2)) return null;
        byte pct = data[2];
        return pct <= 100 ? (pct, marker == 2) : null;
    }

    public static int? DecodePollingRateHz(byte code) => code switch
    {
        0x08 => 125,
        0x04 => 250,
        0x02 => 500,
        0x01 => 1000,
        0x84 => 2000,
        0x82 => 4000,
        0x81 => 8000,
        _ => null,
    };

    public static byte? EncodePollingRateCode(int hz) => hz switch
    {
        125 => 0x08,
        250 => 0x04,
        500 => 0x02,
        1000 => 0x01,
        2000 => 0x84,
        4000 => 0x82,
        8000 => 0x81,
        _ => null,
    };

    public static (int Level, bool Charging)? QueryBattery(List<HidDevice> ifaces)
    {
        // 1. 先查 0xFF00:0x000E 命令 0xAA（可返回充电状态）
        if (TryQueryBattery0xBA(ifaces) is { } baHit)
            return baHit;

        // 2. 回退 0xFF0B:0x0104 Feature Report 0x2A
        var statusDev = ifaces.FirstOrDefault(d => HidSafe.PrimaryUsage(d) == (0xFF0B, 0x0104) && HidSafe.MaxFeatureLen(d) >= 9);
        if (statusDev != null)
        {
            using var raw = RawHidFeature.TryOpen(statusDev.DevicePath);
            var buf = raw?.GetFeature(Math.Max(HidSafe.MaxFeatureLen(statusDev), 10), StatusReportId);
            if (buf != null && ParseStatusFeatureBattery(buf) is { } pct)
                return (pct, false);
        }
        return null;
    }

    public static DeviceTuningCapabilities QueryCapabilities(string deviceId, ushort pid, List<HidDevice> ifaces, string? modelName)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryOpenConfigPair(ifaces, out var ch, out var raw))
        {
            caps.StatusMessage = "未检测到 Rapoo 0xFF00:0x000E 配置通道。";
            return caps;
        }

        using (ch)
        using (raw)
        {
            bool wired = pid == 0x4405;
            byte conn = wired ? ConnWired : ConnReceiver;
            byte[]? dpiX = ReadBlock(ch!, raw!, AddrDpiX, 16, conn);
            byte[]? perf = ReadBlock(ch!, raw!, AddrPerformance, 4, conn);
            if (dpiX == null && perf == null)
            {
                caps.StatusMessage = "Rapoo 鼠标休眠或未响应寄存器读取，请晃动唤醒后重试。";
                return caps;
            }

            byte[]? dpiY = ReadBlock(ch!, raw!, AddrDpiY, 16, conn);
            byte[]? activeBlk = ReadBlock(ch!, raw!, AddrActiveStage, 4, conn);
            byte[]? sensor = ReadBlock(ch!, raw!, AddrSensor, 4, conn);
            byte[]? timing = ReadBlock(ch!, raw!, AddrTiming, 4, conn);

            var stages = new List<(int X, int Y)>();
            int count = dpiX is { Length: >= 15 } ? Math.Clamp(dpiX[14] + 1, 1, 7) : 1;
            for (int i = 0; i < count; i++)
            {
                int x = dpiX is { Length: >= 14 } ? (dpiX[i * 2] | (dpiX[i * 2 + 1] << 8)) : 800;
                int y = dpiY is { Length: >= 14 } ? (dpiY[i * 2] | (dpiY[i * 2 + 1] << 8)) : x;
                stages.Add((Math.Clamp(x, 50, 26000), Math.Clamp(y, 50, 26000)));
            }
            int activeIdx = activeBlk is { Length: >= 1 } ? Math.Clamp((int)activeBlk[0], 0, stages.Count - 1) : 0;

            caps.IsOnline = true;
            caps.StatusMessage = $"{modelName ?? "Rapoo VT"} · 雷柏寄存器通道";
            caps.SupportsDpi = true;
            caps.SupportsIndependentXY = true;
            caps.MinDpi = 50;
            caps.MaxDpi = 26000;
            caps.DpiStep = 50;
            caps.DpiStages = stages;
            caps.ActiveDpiStage = activeIdx;
            caps.CurrentDpiX = stages[activeIdx].X;
            caps.CurrentDpiY = stages[activeIdx].Y;

            if (perf is { Length: >= 3 })
            {
                byte rCode = wired ? perf[2] : perf[0];
                caps.SupportedPollingRates = wired ? [125, 250, 500, 1000] : [.. SupportedPollingRates];
                caps.CurrentPollingRate = DecodePollingRateHz(rCode) ?? 1000;
            }
            if (sensor is { Length: >= 2 })
            {
                caps.SupportedLodOptions = [new LodOption(0, "低 (1.0mm)"), new LodOption(1, "中 (1.5mm)"), new LodOption(2, "高 (2.0mm)")];
                caps.CurrentLod = Math.Clamp((int)sensor[0], 0, 2);
                caps.SupportsMotionSync = true;
                caps.MotionSync = sensor[1] == 1;
            }
            if (timing is { Length: >= 4 })
            {
                int dbIdx = Math.Clamp((int)timing[0], 0, DebounceTableMs.Length - 1);
                caps.SupportsDebounce = true;
                caps.MaxDebounceMs = 32;
                caps.CurrentDebounceMs = DebounceTableMs[dbIdx];
                caps.SupportsAngleSnapping = true;
                caps.AngleSnapping = (timing[3] & 0x01) == 0;
                caps.SupportsRippleControl = true;
                caps.RippleControl = (timing[3] & 0x02) == 0;
                if (timing[2] > 0)
                {
                    caps.SupportsSleepTimeout = true;
                    caps.MinSleepSeconds = 60;
                    caps.MaxSleepSeconds = 1800;
                    caps.CurrentSleepSeconds = timing[2] * 60;
                    caps.SupportedSleepSeconds = [60, 180, 300, 600, 900, 1800];
                }
            }
            return caps;
        }
    }

    public static DeviceTuningResult ApplySettings(string deviceId, ushort pid, List<HidDevice> ifaces, string? modelName, DeviceTuningUpdate update)
    {
        if (!TryOpenConfigPair(ifaces, out var ch, out var raw))
            return new DeviceTuningResult(false, "无法打开 Rapoo 配置通道。");

        bool wired = pid == 0x4405;
        byte conn = wired ? ConnWired : ConnReceiver;
        var applied = new List<string>();
        var errors = new List<string>();

        using (ch)
        using (raw)
        {
            if (update.ActiveDpiStage is { } act)
            {
                byte s = (byte)Math.Clamp(act, 0, 6);
                if (WriteBlock(ch!, raw!, AddrActiveStage, [s, 0x00, 0x02, 0x01], conn))
                    applied.Add($"档位 {s + 1}");
                else
                    errors.Add("活动档位");
            }

            if (update.Dpi is var (dx, dy))
            {
                byte[]? xBlk = ReadBlock(ch!, raw!, AddrDpiX, 16, conn);
                byte[]? yBlk = ReadBlock(ch!, raw!, AddrDpiY, 16, conn);
                byte[]? aBlk = ReadBlock(ch!, raw!, AddrActiveStage, 4, conn);
                if (xBlk is { Length: >= 16 } && yBlk is { Length: >= 16 })
                {
                    int slot = update.ActiveDpiStage ?? (aBlk is { Length: >= 1 } ? Math.Clamp((int)aBlk[0], 0, 6) : 0);
                    int ux = Math.Clamp(dx, 50, 26000);
                    int uy = Math.Clamp(dy, 50, 26000);
                    xBlk[slot * 2] = (byte)(ux & 0xFF);
                    xBlk[slot * 2 + 1] = (byte)(ux >> 8);
                    yBlk[slot * 2] = (byte)(uy & 0xFF);
                    yBlk[slot * 2 + 1] = (byte)(uy >> 8);
                    if (WriteBlock(ch!, raw!, AddrDpiX, xBlk[..16], conn) && WriteBlock(ch!, raw!, AddrDpiY, yBlk[..16], conn))
                        applied.Add(ux == uy ? $"DPI {ux}" : $"DPI {ux}/{uy}");
                    else
                        errors.Add("DPI");
                }
                else
                {
                    errors.Add("DPI");
                }
            }

            if (update.PollingRateHz is { } hz && EncodePollingRateCode(hz) is { } rCode)
            {
                byte[]? perf = ReadBlock(ch!, raw!, AddrPerformance, 4, conn);
                if (perf is { Length: >= 4 })
                {
                    if (wired) perf[2] = rCode;
                    else perf[0] = rCode;
                    if (WriteBlock(ch!, raw!, AddrPerformance, perf[..4], conn))
                        applied.Add($"{hz}Hz");
                    else
                        errors.Add("回报率");
                }
                else errors.Add("回报率");
            }

            if (update.LodCode.HasValue || update.MotionSync.HasValue)
            {
                byte[]? sensor = ReadBlock(ch!, raw!, AddrSensor, 4, conn);
                if (sensor is { Length: >= 4 })
                {
                    if (update.LodCode is { } lod) sensor[0] = (byte)Math.Clamp(lod, 0, 2);
                    if (update.MotionSync is { } ms) sensor[1] = (byte)(ms ? 1 : 0);
                    if (WriteBlock(ch!, raw!, AddrSensor, sensor[..4], conn))
                        applied.Add("传感器参数");
                    else
                        errors.Add("传感器参数");
                }
                else errors.Add("传感器参数");
            }

            if (update.DebounceMs.HasValue || update.SleepSeconds.HasValue || update.AngleSnapping.HasValue || update.RippleControl.HasValue)
            {
                byte[]? timing = ReadBlock(ch!, raw!, AddrTiming, 4, conn);
                if (timing is { Length: >= 4 })
                {
                    if (update.DebounceMs is { } db)
                    {
                        byte idx = (byte)Array.FindIndex(DebounceTableMs, m => m >= db);
                        if (idx == 255) idx = (byte)(DebounceTableMs.Length - 1);
                        timing[0] = idx;
                        timing[1] = idx;
                    }
                    if (update.SleepSeconds is { } sl && sl > 0)
                        timing[2] = (byte)Math.Clamp((int)Math.Round(sl / 60.0), 1, 120);
                    if (update.AngleSnapping is { } ang)
                        timing[3] = ang ? (byte)(timing[3] & ~0x01) : (byte)(timing[3] | 0x01);
                    if (update.RippleControl is { } rip)
                        timing[3] = rip ? (byte)(timing[3] & ~0x02) : (byte)(timing[3] | 0x02);

                    if (WriteBlock(ch!, raw!, AddrTiming, timing[..4], conn))
                        applied.Add("高级参数");
                    else
                        errors.Add("高级参数");
                }
                else errors.Add("高级参数");
            }
        }

        var refreshed = QueryCapabilities(deviceId, pid, ifaces, modelName);
        if (applied.Count == 0 && errors.Count == 0)
            return new DeviceTuningResult(true, "未变更 Rapoo 参数", refreshed);
        return errors.Count > 0
            ? new DeviceTuningResult(applied.Count > 0, $"部分设置失败 ({string.Join("、", errors)})", refreshed)
            : new DeviceTuningResult(true, $"已写入 {string.Join(" / ", applied)}", refreshed);
    }

    private static bool TryOpenConfigPair(List<HidDevice> ifaces, out HidChannel? ch, out RawHidFeature? raw)
    {
        ch = null;
        raw = null;
        var cfgDev = ifaces.FirstOrDefault(d => HidSafe.PrimaryUsage(d) == (0xFF00, 0x000E) && HidSafe.MaxOutputLen(d) >= 31);
        if (cfgDev == null) return false;
        ch = HidChannel.TryOpen(cfgDev);
        raw = RawHidFeature.TryOpen(cfgDev.DevicePath);
        if (ch == null || raw == null)
        {
            ch?.Dispose();
            raw?.Dispose();
            ch = null;
            raw = null;
            return false;
        }
        return true;
    }

    private static (int Level, bool Charging)? TryQueryBattery0xBA(List<HidDevice> ifaces)
    {
        if (!TryOpenConfigPair(ifaces, out var ch, out var raw))
            return null;
        using (ch)
        using (raw)
        {
            byte[] req = BuildCommandReport(CmdBattery, 0, lengthOverride: 0);
            for (int round = 0; round < 3; round++)
            {
                if (!ch!.WriteOutput(req)) return null;
                for (int poll = 0; poll < 15; poll++)
                {
                    Thread.Sleep(5);
                    var ans = raw!.GetInputReport(1 + FrameLength, ConfigReportId);
                    if (ans != null && ParseBatteryAnswer(ans) is { } hit)
                        return hit;
                }
            }
            return null;
        }
    }

    private static byte[]? ReadBlock(HidChannel ch, RawHidFeature raw, uint addr, int len, byte conn)
    {
        byte[] req = BuildCommandReport(CmdRead, addr, lengthOverride: (byte)len, connection: conn);
        for (int round = 0; round < 4; round++)
        {
            bool sawBusy = false;
            if (!ch.WriteOutput(req)) return null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                long dl = Environment.TickCount64 + 90;
                while (Environment.TickCount64 < dl)
                {
                    Thread.Sleep(4);
                    var buf = raw.GetInputReport(1 + FrameLength, ConfigReportId);
                    if (buf == null) continue;
                    var (ok, marker, payload) = DecodeAnswer(buf);
                    if (!ok) { sawBusy = true; continue; }
                    if (marker == 0 && (sawBusy || attempt > 0 || round > 0) && payload.Length >= len)
                        return payload[..len];
                }
            }
            Thread.Sleep(40);
        }
        return null;
    }

    private static bool WriteBlock(HidChannel ch, RawHidFeature raw, uint addr, ReadOnlySpan<byte> data, byte conn)
    {
        byte[] req = BuildCommandReport(CmdWrite, addr, data, connection: conn);
        for (int round = 0; round < 3; round++)
        {
            if (!ch.WriteOutput(req)) return false;
            Thread.Sleep(25);
            var buf = raw.GetInputReport(1 + FrameLength, ConfigReportId);
            if (buf != null && DecodeAnswer(buf).Ok)
                return true;
        }
        return false;
    }
}
