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
    public const byte CmdSetCurrentConfig = 10;
    public const byte CmdSetLinearCorrection = 11;
    public const byte CmdSetRippleControl = 12;
    public const byte CmdSetMotionSync = 13;
    public const byte CmdGetCharging = 15;
    public const byte CmdSetSleepTime = 21;
    public const byte CmdSetDebounce = 22;
    public const byte CmdGetDpiTable = 23;
    public const byte CmdSetLongRange = 27;
    public const byte CmdSetSensorMode = 31;
    public const byte CmdGetMouseCidMid = 74;
    public const byte CmdGetDongleStatus = 129;
    public const byte CmdGetMouseCidMidDangle = 137;

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

    public static byte[] BuildRequest(byte cmdId, byte paramLen, byte cmdLen, byte target = 0x00, ReadOnlySpan<byte> payload = default, int reportLen = 64)
    {
        int len = Math.Clamp(reportLen, 17, 64);
        var req = new byte[len];
        req[0] = ReportId;
        req[2] = CommandCode;
        req[3] = paramLen;
        req[4] = 0x3A;
        req[5] = target;
        req[6] = cmdId;
        req[7] = cmdLen;
        if (!payload.IsEmpty)
            payload[..Math.Min(payload.Length, len - 8)].CopyTo(req.AsSpan(8));
        int sum = 0;
        for (int i = 2; i < req.Length; i++) sum += req[i];
        req[1] = (byte)(sum & 0xFF);
        return req;
    }

    public static byte[]? Exchange(HidChannel ch, byte cmdId, byte paramLen, byte cmdLen, byte target = 0x00, ReadOnlySpan<byte> payload = default, int timeoutMs = 550)
    {
        for (int i = 0; i < 4; i++)
            if (ch.Read(5) == null) break;

        int reportLen = ch.OutputLength is >= 17 and <= 64 ? ch.OutputLength : 64;
        var req = BuildRequest(cmdId, paramLen, cmdLen, target, payload, reportLen);
        if (!ch.WriteOutput(req))
        {
            if (reportLen != 64)
            {
                req = BuildRequest(cmdId, paramLen, cmdLen, target, payload, 64);
                if (!ch.WriteOutput(req)) return null;
            }
            else return null;
        }

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

    public static (byte Cid, byte Mid)? QueryCidMid(HidChannel ch, bool isWireless)
    {
        byte[] targets = isWireless ? [0x01, 0x00] : [0x00];
        if (isWireless)
        {
            foreach (byte t in targets)
            {
                var d = Exchange(ch, CmdGetMouseCidMidDangle, 8, 6, t, [0x00], 260);
                if (d is { Length: >= 3 } && (d[1] != 0 || d[2] != 0))
                    return (d[1], d[2]);
            }
        }
        foreach (byte t in targets)
        {
            var d = Exchange(ch, CmdGetMouseCidMid, 8, 6, t, [0x00], 260);
            if (d is { Length: >= 3 } && (d[1] != 0 || d[2] != 0))
                return (d[1], d[2]);
        }
        return null;
    }

    public static (byte Level, bool Charging)? QueryBattery(HidChannel ch, bool isWireless)
    {
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

    private static byte[]? ReadAddressData(HidChannel ch, ushort address, byte length, byte target)
    {
        byte[] p = [(byte)(address & 0xFF), (byte)(address >> 8), length];
        var res = Exchange(ch, CmdGetDpiTable, 10, 13, target, p, 450);
        if (res == null || res.Length < 3) return null;
        int actualLen = Math.Clamp((int)res[2], 0, Math.Max(0, res.Length - 3));
        return actualLen > 0 ? res.AsSpan(3, actualLen).ToArray() : null;
    }

    public static DeviceTuningCapabilities QueryCapabilities(
        string deviceId,
        HidChannel ch,
        bool isWireless,
        string? productName,
        ushort vid = 0,
        ushort pid = 0,
        AtkSensorKind sensor = AtkSensorKind.Unknown)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        byte[] targets = isWireless ? [0x01, 0x00] : [0x00];

        foreach (byte t in targets)
        {
            var cfg = Exchange(ch, CmdGetCurrentConfig, 20, 18, t, default, 550);
            if (cfg == null || cfg.Length < 10) continue;

            caps.IsOnline = true;
            var cidMid = QueryCidMid(ch, isWireless);
            string? resolvedName = AtkModelCatalog.ResolveModelName(vid, pid, cidMid, productName, AtkProtocolFamily.Bitmouse) ?? productName;
            AtkSensorKind effectiveSensor = sensor != AtkSensorKind.Unknown
                ? sensor
                : AtkModelCatalog.ResolveSensor(vid, pid, cidMid, AtkProtocolFamily.Bitmouse, resolvedName);

            caps.OnboardProfileCount = 4;
            caps.ActiveOnboardProfile = Math.Clamp((int)cfg[0], 0, 3);
            caps.OnboardProfileNames = ["板载配置 1", "板载配置 2", "板载配置 3", "板载配置 4"];

            // Read full 66-byte DPI table (addr 1..61 in 10B chunks per Pr / d1)
            var tableBytes = new List<byte>(70);
            for (int r = 0; r < 7; r++)
            {
                var chunk = ReadAddressData(ch, (ushort)(r * 10 + 1), 10, t);
                if (chunk == null) break;
                tableBytes.AddRange(chunk);
            }

            bool isUltra = effectiveSensor is AtkSensorKind.Paw3950Ultra or AtkSensorKind.Paw3955Master;
            caps.SupportsDpi = true;
            caps.SupportsIndependentXY = true;
            caps.SupportsDpiStagesWrite = true;
            caps.MinDpi = 50;
            caps.MaxDpi = effectiveSensor == AtkSensorKind.Paw3955Master ? 45000 : (isUltra ? 42000 : 30000);
            caps.DpiStep = isUltra ? 10 : 50;

            if (tableBytes.Count >= 10)
            {
                int active = tableBytes[0];
                int count = Math.Clamp((int)tableBytes[1], 1, 8);
                for (int i = 0; i < count; i++)
                {
                    int off = 2 + i * 8;
                    if (off + 3 >= tableBytes.Count) break;
                    int vx = tableBytes[off] | (tableBytes[off + 1] << 8);
                    int vy = tableBytes[off + 2] | (tableBytes[off + 3] << 8);
                    if (vx == 0) break;
                    caps.DpiStages.Add((vx, vy > 0 ? vy : vx));
                }
                if (caps.DpiStages.Count > 0)
                {
                    caps.ActiveDpiStage = Math.Clamp(active, 0, caps.DpiStages.Count - 1);
                    caps.CurrentDpiX = caps.DpiStages[caps.ActiveDpiStage].X;
                    caps.CurrentDpiY = caps.DpiStages[caps.ActiveDpiStage].Y;
                }
            }

            if (caps.DpiStages.Count == 0)
            {
                int fallbackDpi = cfg[3] | (cfg[4] << 8);
                if (fallbackDpi is >= 50 and <= 45000)
                {
                    caps.DpiStages.Add((fallbackDpi, fallbackDpi));
                    caps.CurrentDpiX = fallbackDpi;
                    caps.CurrentDpiY = fallbackDpi;
                }
            }

            // cfg[2] = reportRate per Wc.reportRate
            if (DecodePollingRate(cfg[2]) is { } hz)
            {
                caps.SupportedPollingRates = isWireless || (resolvedName ?? "").Contains("8K", StringComparison.OrdinalIgnoreCase)
                    ? [125, 250, 500, 1000, 2000, 4000, 8000]
                    : [125, 250, 500, 1000];
                caps.CurrentPollingRate = hz;
            }

            // cfg[5] = offsetCalibration => silentHeight = offsetCalibration + 1 (Ultra: 1=0.7mm, 2=1.0mm, 3=2.0mm)
            int rawLod = cfg[5] + 1;
            if (isUltra)
            {
                caps.SupportedLodOptions = [new(1, "0.7 mm (低)"), new(2, "1.0 mm (中)"), new(3, "2.0 mm (高)")];
                caps.CurrentLod = Math.Clamp(rawLod, 1, 3);
            }
            else
            {
                caps.SupportedLodOptions = [new(1, "1.0 mm (低)"), new(2, "2.0 mm (高)")];
                caps.CurrentLod = rawLod == 2 ? 2 : 1;
            }

            caps.SupportsMotionSync = true;
            caps.MotionSync = cfg[6] == 1;
            caps.SupportsAngleSnapping = true;
            caps.AngleSnapping = cfg[7] == 1;
            caps.SupportsRippleControl = true;
            caps.RippleControl = cfg[8] == 1;

            if (cfg.Length > 10)
            {
                int sleepSec = cfg[9] | (cfg[10] << 8);
                if (sleepSec > 0)
                {
                    caps.SupportsSleepTimeout = true;
                    caps.MinSleepSeconds = 30;
                    caps.MaxSleepSeconds = 1800;
                    caps.SupportedSleepSeconds = [30, 60, 120, 300, 600, 1800];
                    caps.CurrentSleepSeconds = sleepSec;
                }
            }

            if (cfg.Length > 11)
            {
                caps.SupportsDebounce = true;
                caps.MaxDebounceMs = 20;
                caps.CurrentDebounceMs = Math.Clamp((int)cfg[11], 0, 20);
            }

            var perfData = ReadAddressData(ch, 74, 1, t);
            caps.SupportedPerformanceModes =
            [
                new(0, "标准节能", "低功耗办公模式"),
                new(1, "高速模式 (HP)", "高性能模式"),
                new(2, "狂暴竞技模式", "传感极限帧率"),
            ];
            if (perfData is { Length: >= 1 })
            {
                caps.CurrentPerformanceMode = perfData[0] switch
                {
                    4 or 2 => 1,
                    5 => 2,
                    _ => 0,
                };
            }
            else
            {
                caps.CurrentPerformanceMode = 1;
            }

            caps.StatusMessage = "已通过 BITMOUSE 0x72 协议读取 ATK/VXE 硬件参数。";
            return caps;
        }

        caps.IsOnline = false;
        caps.StatusMessage = "BITMOUSE 鼠标当前休眠中，请晃动唤醒后重试。";
        return caps;
    }

    public static DeviceTuningResult ApplySettings(
        string deviceId,
        HidChannel ch,
        bool isWireless,
        string? productName,
        DeviceTuningUpdate update,
        ushort vid = 0,
        ushort pid = 0,
        AtkSensorKind sensor = AtkSensorKind.Unknown)
    {
        byte target = isWireless ? (byte)0x01 : (byte)0x00;
        var errors = new List<string>();

        if (update.ActiveOnboardProfile is { } profIdx)
        {
            if (Exchange(ch, CmdSetCurrentConfig, 3, 1, target, [(byte)Math.Clamp(profIdx, 0, 3)]) == null)
                errors.Add("切换 BITMOUSE 板载配置失败");
        }

        if (update.PollingRateHz is { } hz && EncodePollingRate(hz) is { } rateCode)
        {
            if (Exchange(ch, CmdSetReportRate, 3, 1, target, [rateCode]) == null)
                errors.Add($"写入回报率 {hz} Hz 失败");
        }

        var cur = QueryCapabilities(deviceId, ch, isWireless, productName, vid, pid, sensor);

        if (update.Dpi is { } dpi || update.ActiveDpiStage != null || update.DpiStages is { Count: > 0 })
        {
            int idx = Math.Clamp(update.ActiveDpiStage ?? cur.ActiveDpiStage, 0, Math.Max(0, (update.DpiStages?.Count ?? cur.DpiStages.Count) - 1));
            if (update.DpiStages is { Count: > 0 } stgs)
            {
                for (int i = 0; i < stgs.Count && i < 8; i++)
                {
                    int x = Math.Clamp(stgs[i].X, 50, cur.MaxDpi);
                    int y = Math.Clamp(stgs[i].Y, 50, cur.MaxDpi);
                    byte en = (byte)(i == idx ? 1 : 0);
                    byte[] p = [(byte)i, (byte)(x & 0xFF), (byte)(x >> 8), (byte)(y & 0xFF), (byte)(y >> 8), 0xFF, 0x00, 0x00, 0x00, en];
                    if (Exchange(ch, CmdSetDpi, 12, 10, target, p) == null && i == idx)
                        errors.Add("写入 BITMOUSE DPI 失败");
                }
            }
            else
            {
                int x = Math.Clamp(update.Dpi?.X ?? (cur.DpiStages.Count > idx ? cur.DpiStages[idx].X : 800), 50, cur.MaxDpi);
                int y = Math.Clamp(update.Dpi?.Y ?? (cur.DpiStages.Count > idx ? cur.DpiStages[idx].Y : x), 50, cur.MaxDpi);
                byte[] p = [(byte)idx, (byte)(x & 0xFF), (byte)(x >> 8), (byte)(y & 0xFF), (byte)(y >> 8), 0xFF, 0x00, 0x00, 0x00, 0x01];
                if (Exchange(ch, CmdSetDpi, 12, 10, target, p) == null)
                    errors.Add("写入 BITMOUSE DPI 失败");
            }
        }

        if (update.LodCode is { } lod)
        {
            bool isUltra = cur.SupportedLodOptions.Count >= 3;
            byte offsetVal = (byte)(Math.Clamp(lod, 1, isUltra ? 3 : 2) - 1);
            if (Exchange(ch, CmdSetSilentHeight, 4, 2, target, [0x00, offsetVal]) == null)
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
        {
            byte wireMode = Math.Clamp(perf, 0, 2) switch { 0 => (byte)0, 1 => (byte)4, _ => (byte)5 };
            Exchange(ch, CmdSetSensorMode, 3, 1, target, [wireMode]);
        }

        Thread.Sleep(45);
        var verified = QueryCapabilities(deviceId, ch, isWireless, productName, vid, pid, sensor);
        return new DeviceTuningResult(errors.Count == 0,
            errors.Count == 0 ? "已写入 ATK BITMOUSE 硬件并回读确认。" : string.Join("；", errors),
            verified);
    }
}
