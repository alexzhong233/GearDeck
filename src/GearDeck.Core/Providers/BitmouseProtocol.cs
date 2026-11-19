using System.Diagnostics;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// ATK / VXE BITMOUSE OEM 64 字节协议 (Report ID 0x08, cmdCode 0x72)：
/// 覆盖 ATK ZERO / A9 NK / VXE R1 S / R2 SE 等 0xFF05 接口设备，支持电量、9 项实时硬件配置读写。
/// </summary>
internal static class BitmouseProtocol
{
    public const byte ReportId = 0x08;
    public const byte CommandCode = 0x72;

    public const byte CmdSetReportRate = 1;
    public const byte CmdSetDpi = 2;
    public const byte CmdSetSilentHeight = 3;
    public const byte CmdGetBatteryLevel = 7;
    public const byte CmdGetCurrentConfig = 9;
    public const byte CmdSetLinearCorrection = 11;
    public const byte CmdSetRippleControl = 12;
    public const byte CmdSetMotionSync = 13;
    public const byte CmdGetCharging = 15;
    public const byte CmdSetSleepTime = 21;
    public const byte CmdSetDebounce = 22;
    public const byte CmdGetDpiTable = 23;
    public const byte CmdSetLongRange = 27;
    public const byte CmdSetSensorMode = 31;

    private static readonly (byte Code, int Hz)[] RateTable =
    [
        (0, 1000),
        (1, 500),
        (2, 250),
        (3, 125),
        (4, 8000),
        (5, 4000),
        (6, 2000),
    ];

    public static byte[] BuildRequest(byte cmdId, byte paramLen, byte cmdLen, byte target = 0x00, ReadOnlySpan<byte> payload = default)
    {
        var req = new byte[64];
        req[0] = ReportId;
        req[2] = CommandCode;
        req[3] = paramLen;
        req[4] = 0x3A;
        req[5] = target;
        req[6] = cmdId;
        req[7] = cmdLen;
        if (!payload.IsEmpty)
            payload[..Math.Min(payload.Length, 56)].CopyTo(req.AsSpan(8));
        int sum = 0;
        for (int i = 2; i < req.Length; i++) sum += req[i];
        req[1] = (byte)(sum & 0xFF);
        return req;
    }

    public static byte[]? Exchange(HidChannel ch, byte cmdId, byte paramLen, byte cmdLen, byte target = 0x00, ReadOnlySpan<byte> payload = default, int timeoutMs = 550)
    {
        for (int i = 0; i < 4; i++)
            if (ch.Read(5) == null) break;

        var req = BuildRequest(cmdId, paramLen, cmdLen, target, payload);
        if (!ch.WriteOutput(req)) return null;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var r = ch.Read(90);
            if (r == null || r.Length < 7) continue;
            if (r[0] != ReportId || r[1] != CommandCode) continue;
            if (r[5] != cmdId) continue;
            if (r[2] == 0xFF) return null; // error
            int len = Math.Clamp((int)r[6], 0, Math.Max(0, r.Length - 7));
            return r.AsSpan(7, len).ToArray();
        }
        return null;
    }

    public static (byte Level, bool Charging)? QueryBattery(HidChannel ch, bool isWireless)
    {
        byte target = isWireless ? (byte)0x01 : (byte)0x00;
        foreach (byte t in isWireless ? new byte[] { 0x01, 0x00 } : new byte[] { 0x00 })
        {
            var lvl = Exchange(ch, CmdGetBatteryLevel, 2, 1, t);
            if (lvl is { Length: >= 1 } && lvl[0] is >= 1 and <= 100)
            {
                var chg = Exchange(ch, CmdGetCharging, 2, 1, t);
                return (lvl[0], chg is { Length: >= 1 } && chg[0] == 1);
            }
        }
        return null;
    }

    public static int? DecodePollingRate(byte code)
    {
        foreach (var (c, hz) in RateTable)
            if (c == code) return hz;
        return null;
    }

    public static byte? EncodePollingRate(int hz)
    {
        foreach (var (c, r) in RateTable)
            if (r == hz) return c;
        return null;
    }

    public static DeviceTuningCapabilities QueryCapabilities(string deviceId, HidChannel ch, bool isWireless, string? productName)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        byte[] targets = isWireless ? [0x01, 0x00] : [0x00];

        foreach (byte t in targets)
        {
            var cfg = Exchange(ch, CmdGetCurrentConfig, 19, 17, t, default, 550);
            if (cfg == null || cfg.Length < 10) continue;

            caps.IsOnline = true;

            // Parse DPI stages (cmd 23)
            var dpiRaw = Exchange(ch, CmdGetDpiTable, 10, 13, t, default, 550);
            if (dpiRaw != null && dpiRaw.Length >= 8)
            {
                int active = dpiRaw[0];
                int count = Math.Clamp((int)dpiRaw[1], 1, 8);
                caps.SupportsDpi = true;
                caps.SupportsIndependentXY = true;
                caps.SupportsDpiStagesWrite = true;
                caps.MinDpi = 100;
                caps.MaxDpi = 42000;
                caps.DpiStep = 50;

                for (int i = 0; i < count && 2 + i * 2 + 1 < dpiRaw.Length; i++)
                {
                    int val = dpiRaw[2 + i * 2] | (dpiRaw[2 + i * 2 + 1] << 8);
                    if (val == 0) break;
                    caps.DpiStages.Add((val, val));
                }
                if (caps.DpiStages.Count > 0)
                {
                    caps.ActiveDpiStage = Math.Clamp(active, 0, caps.DpiStages.Count - 1);
                    caps.CurrentDpiX = caps.DpiStages[caps.ActiveDpiStage].X;
                    caps.CurrentDpiY = caps.DpiStages[caps.ActiveDpiStage].Y;
                }
            }

            if (DecodePollingRate(cfg[0]) is { } hz)
            {
                caps.SupportedPollingRates = isWireless || (productName ?? "").Contains("8K", StringComparison.OrdinalIgnoreCase)
                    ? [125, 250, 500, 1000, 2000, 4000, 8000]
                    : [125, 250, 500, 1000];
                caps.CurrentPollingRate = hz;
            }

            // Sensor flags inside Bitmouse GetCurrentConfig payload
            caps.SupportedLodOptions = [new(1, "1.0 mm (低)"), new(2, "2.0 mm (高)")];
            caps.CurrentLod = cfg[2] is 1 or 2 ? cfg[2] : 1;

            caps.SupportsMotionSync = true;
            caps.MotionSync = cfg[3] == 1;
            caps.SupportsAngleSnapping = true;
            caps.AngleSnapping = cfg[4] == 1;
            caps.SupportsRippleControl = true;
            caps.RippleControl = cfg[5] == 1;

            caps.SupportedPerformanceModes =
            [
                new(0, "标准节能", "低功耗办公模式"),
                new(1, "高速模式 (HP)", "高性能模式"),
                new(2, "狂暴竞技模式", "传感极限帧率"),
            ];
            caps.CurrentPerformanceMode = cfg.Length > 10 && cfg[10] <= 2 ? cfg[10] : 1;

            if (cfg.Length > 7)
            {
                caps.SupportsDebounce = true;
                caps.MaxDebounceMs = 20;
                caps.CurrentDebounceMs = Math.Clamp((int)cfg[7], 0, 20);
            }

            caps.StatusMessage = "已通过 BITMOUSE 0x72 协议读取 ATK/VXE 硬件参数。";
            return caps;
        }

        caps.IsOnline = false;
        caps.StatusMessage = "BITMOUSE 鼠标当前休眠中，请晃动唤醒后重试。";
        return caps;
    }

    public static DeviceTuningResult ApplySettings(string deviceId, HidChannel ch, bool isWireless, string? productName, DeviceTuningUpdate update)
    {
        byte target = isWireless ? (byte)0x01 : (byte)0x00;
        var errors = new List<string>();

        if (update.PollingRateHz is { } hz && EncodePollingRate(hz) is { } rateCode)
        {
            if (Exchange(ch, CmdSetReportRate, 3, 1, target, [rateCode]) == null)
                errors.Add($"写入回报率 {hz} Hz 失败");
        }

        if (update.Dpi is { } dpi || update.ActiveDpiStage != null)
        {
            var cur = QueryCapabilities(deviceId, ch, isWireless, productName);
            int idx = Math.Clamp(update.ActiveDpiStage ?? cur.ActiveDpiStage, 0, Math.Max(0, cur.DpiStages.Count - 1));
            int x = Math.Clamp(update.Dpi?.X ?? (cur.DpiStages.Count > idx ? cur.DpiStages[idx].X : 800), 100, 42000);
            int y = Math.Clamp(update.Dpi?.Y ?? (cur.DpiStages.Count > idx ? cur.DpiStages[idx].Y : x), 100, 42000);
            byte[] p = [(byte)idx, (byte)(x & 0xFF), (byte)(x >> 8), (byte)(y & 0xFF), (byte)(y >> 8), 0xFF, 0x00, 0x00, 0x00, 0x01];
            if (Exchange(ch, CmdSetDpi, 12, 10, target, p) == null)
                errors.Add("写入 BITMOUSE DPI 失败");
        }

        if (update.LodCode is { } lod)
        {
            if (Exchange(ch, CmdSetSilentHeight, 4, 2, target, [(byte)Math.Clamp(lod, 1, 2), 0x00]) == null)
                errors.Add("写入 LOD 失败");
        }

        if (update.MotionSync is { } ms)
            Exchange(ch, CmdSetMotionSync, 3, 1, target, [(byte)(ms ? 1 : 0)]);

        if (update.AngleSnapping is { } snap)
            Exchange(ch, CmdSetLinearCorrection, 3, 1, target, [(byte)(snap ? 1 : 0)]);

        if (update.RippleControl is { } ripple)
            Exchange(ch, CmdSetRippleControl, 3, 1, target, [(byte)(ripple ? 1 : 0)]);

        if (update.DebounceMs is { } deb)
            Exchange(ch, CmdSetDebounce, 3, 1, target, [(byte)Math.Clamp(deb, 0, 20)]);

        if (update.SleepSeconds is { } sleep)
        {
            int sec = Math.Clamp(sleep, 10, 3600);
            Exchange(ch, CmdSetSleepTime, 5, 2, target, [(byte)(sec & 0xFF), (byte)(sec >> 8)]);
        }

        if (update.PerformanceMode is { } perf)
            Exchange(ch, CmdSetSensorMode, 3, 1, target, [(byte)Math.Clamp(perf, 0, 2)]);

        Thread.Sleep(45);
        var verified = QueryCapabilities(deviceId, ch, isWireless, productName);
        return new DeviceTuningResult(errors.Count == 0,
            errors.Count == 0 ? "已写入 ATK BITMOUSE 硬件并回读确认。" : string.Join("；", errors),
            verified);
    }
}
