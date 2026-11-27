using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// Ninjutso (Sora V2 / V3 / TEN) 协议编解码与控制（基于 OpenMouse mouse-protocol NinjaForce）：
/// 支持现行 0x06 (15B Feature) 与旧版 Sora V2 0x05 (31B Feature) 电量、DPI、1K~8K 回报率、LOD、Motion Sync 与休眠。
/// </summary>
public static class NinjutsoProtocol
{
    public const ushort LegacyVid = 0x1915;
    public const ushort CurrentVid = 0x093A;

    public const byte CurrentReportId = 6;
    public const int CurrentPayloadLen = 15;
    public const byte LegacyReportId = 5;
    public const int LegacyPayloadLen = 31;

    public const byte CmdDpi = 4;
    public const byte CmdSetDpi = 3;
    public const byte CmdPollingRate = 6;
    public const byte CmdSetPollingRate = 5;
    public const byte CmdLod = 8;
    public const byte CmdSetLod = 7;
    public const byte CmdMotionSync = 14;
    public const byte CmdSetMotionSync = 13;
    public const byte CmdProfile = 16;
    public const byte CmdBatteryCharging = 17;
    public const byte CmdBatteryPercent = 18;
    public const byte CmdSleepMinutes = 25;
    public const byte CmdSetSleepMinutes = 24;
    public const byte CmdActiveDpiStage = 28;
    public const byte CmdSetActiveDpiStage = 27;
    public const byte CmdDpiStageCount = 30;
    public const byte CmdPairedProductId = 167;

    public static readonly int[] PollingRates = [1000, 2000, 4000, 8000];

    public static readonly HashSet<ushort> LegacyPids =
    [
        0xAE11, 0xAE12, 0xAE13, 0xAE14, 0xAE15, 0xAE16,
        0xAE1C, 0xAE8C, 0xAE8A,
    ];

    public static readonly HashSet<ushort> CurrentPids =
    [
        0xE010, 0xE020, 0xEA01, 0xEB01, 0xEB02,
    ];

    public static byte[] BuildCurrentRequest(byte command, byte profile = 0, ReadOnlySpan<byte> args = default)
    {
        var buf = new byte[1 + CurrentPayloadLen];
        buf[0] = CurrentReportId;
        buf[1] = command;
        buf[2] = 0;
        buf[3] = 0;
        buf[4] = 1;
        buf[5] = 0;
        int argCount = Math.Min(8, args.Length);
        buf[6] = (byte)argCount;
        buf[7] = profile;
        if (argCount > 0)
            args[..argCount].CopyTo(buf.AsSpan(8));
        return buf;
    }

    public static byte[]? ExtractCurrentResponseValue(ReadOnlySpan<byte> response, byte expectedCommand)
    {
        if (response.Length < 9) return null;
        int offset = response[1] == expectedCommand
            ? 0
            : (response[0] == expectedCommand && response.Length >= 8 ? -1 : int.MinValue);
        if (offset == int.MinValue) return null;
        int start = 8 + offset;
        return start <= response.Length ? response[start..].ToArray() : null;
    }

    public static byte[] BuildLegacyRequest(byte command, byte profile = 0, byte? value = null)
    {
        var buf = new byte[1 + LegacyPayloadLen];
        buf[0] = LegacyReportId;
        buf[1] = command;
        buf[2] = 0;
        buf[3] = 0;
        buf[4] = 1;
        buf[5] = 0;
        buf[6] = profile;
        buf[7] = 22;
        if (value.HasValue)
            buf[9] = value.Value;
        return buf;
    }

    public static byte EncodePollingRate(int hz) => hz switch
    {
        1000 => 1,
        2000 => 2,
        4000 => 3,
        8000 => 4,
        _ => 1,
    };

    public static int? DecodePollingRate(byte code)
        => code is >= 1 and <= 4 ? PollingRates[code - 1] : null;

    public static (byte Low, byte High, byte Tenth) EncodeDpi(int dpi, bool direct)
    {
        int max = direct ? 45000 : 30000;
        int clamped = Math.Clamp(dpi, direct ? 1 : 50, max);
        int encoded = direct ? clamped : (int)Math.Round(clamped / 50.0) - 1;
        return ((byte)(encoded & 0xFF), (byte)((encoded >> 8) & 0xFF), 0);
    }

    public static int DecodeDpi(byte low, byte high, byte tenth, bool direct)
    {
        int encoded = low | (high << 8);
        return direct ? encoded + tenth / 10 : (encoded + 1) * 50;
    }

    public static (byte Low, byte High) EncodeLegacyDpi(int dpi)
    {
        int encoded = Math.Clamp((int)Math.Round(dpi / 50.0) - 1, 0, 599);
        return ((byte)(encoded & 0xFF), (byte)((encoded >> 8) & 0xFF));
    }

    public static int DecodeLegacyDpi(byte low, byte high)
        => (low | (high << 8)) * 50 + 50;

    public static (int Level, bool Charging)? QueryBattery(ushort vid, ushort pid, List<HidDevice> ifaces)
    {
        bool isLegacy = vid == LegacyVid;
        byte wantReport = isLegacy ? LegacyReportId : CurrentReportId;
        var dev = ifaces.FirstOrDefault(d => HidReportInspector.GetReportIds(d).FeatureIds.Contains(wantReport))
            ?? ifaces.FirstOrDefault(d => HidSafe.MaxFeatureLen(d) >= (isLegacy ? 32 : 16));
        if (dev == null) return null;

        using var raw = RawHidFeature.TryOpen(dev.DevicePath);
        if (raw == null) return null;

        if (isLegacy)
        {
            var data = ReadLegacySettings(raw);
            if (data == null || data.Length < 10) return null;
            int pct = Math.Clamp((int)data[7], 0, 100);
            bool charging = data[8] == 1;
            return pct > 0 || charging ? (pct, charging) : null;
        }

        var bat = ReadCurrent(raw, CmdBatteryPercent);
        if (bat == null || bat.Length == 0 || bat[0] > 100) return null;
        var chg = ReadCurrent(raw, CmdBatteryCharging);
        return (bat[0], chg is { Length: > 0 } && chg[0] == 1);
    }

    public static DeviceTuningCapabilities QueryCapabilities(string deviceId, ushort vid, ushort pid, List<HidDevice> ifaces, string? modelName)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        bool isLegacy = vid == LegacyVid;
        byte wantReport = isLegacy ? LegacyReportId : CurrentReportId;
        var dev = ifaces.FirstOrDefault(d => HidReportInspector.GetReportIds(d).FeatureIds.Contains(wantReport))
            ?? ifaces.FirstOrDefault(d => HidSafe.MaxFeatureLen(d) >= (isLegacy ? 32 : 16));
        if (dev == null)
        {
            caps.StatusMessage = "未检测到 Ninjutso Feature 控制通道。";
            return caps;
        }

        using var raw = RawHidFeature.TryOpen(dev.DevicePath);
        if (raw == null)
        {
            caps.StatusMessage = "无法打开 Ninjutso Feature 控制通道。";
            return caps;
        }

        if (isLegacy)
            return QueryLegacyCaps(deviceId, pid, raw, modelName);

        byte profile = ReadCurrent(raw, CmdProfile) is { Length: > 0 } p && p[0] > 0 ? p[0] : (byte)1;
        int stage = ReadCurrent(raw, CmdActiveDpiStage) is { Length: > 0 } st ? st[0] : 0;
        int stageCount = Math.Clamp(ReadCurrent(raw, CmdDpiStageCount) is { Length: > 0 } sc ? sc[0] : 1, 1, 4);
        bool direct = pid is 0xE010 or 0xEB02;
        if (ReadCurrent(raw, CmdPairedProductId) is { Length: >= 2 } pp)
        {
            ushort paired = (ushort)(pp[0] | (pp[1] << 8));
            if (paired is 0xE010 or 0xEB02) direct = true;
        }

        var stages = new List<(int X, int Y)>(stageCount);
        for (int i = 0; i < stageCount; i++)
        {
            var r = ReadCurrent(raw, CmdDpi, [(byte)i]);
            if (r is { Length: >= 3 })
            {
                int d = DecodeDpi(r[0], r[1], r[2], direct);
                stages.Add((d, d));
            }
            else
            {
                stages.Add((800, 800));
            }
        }

        int activeIdx = Math.Clamp(stage, 0, Math.Max(0, stages.Count - 1));
        int rateHz = ReadCurrent(raw, CmdPollingRate) is { Length: > 0 } pr ? (DecodePollingRate(pr[0]) ?? 1000) : 1000;
        int lod = ReadCurrent(raw, CmdLod) is { Length: > 0 } lr ? lr[0] : 1;
        bool? motionSync = ReadCurrent(raw, CmdMotionSync) is { Length: > 0 } ms ? ms[0] == 1 : null;
        int? sleepMin = ReadCurrent(raw, CmdSleepMinutes) is { Length: > 0 } sm && sm[0] > 0 ? sm[0] : null;

        caps.IsOnline = true;
        caps.StatusMessage = $"{modelName ?? "Ninjutso"} · NinjaForce 通道 (Profile {profile})";
        caps.SupportsDpi = true;
        caps.MinDpi = direct ? 100 : 50;
        caps.MaxDpi = direct ? 45000 : 30000;
        caps.DpiStep = direct ? 50 : 50;
        caps.DpiStages = stages;
        caps.ActiveDpiStage = activeIdx;
        caps.CurrentDpiX = stages[activeIdx].X;
        caps.CurrentDpiY = stages[activeIdx].Y;
        caps.SupportedPollingRates = [.. PollingRates];
        caps.CurrentPollingRate = rateHz;
        caps.SupportedLodOptions = [new LodOption(0, "低 (Low)"), new LodOption(1, "中 (Medium)"), new LodOption(2, "高 (High)")];
        caps.CurrentLod = Math.Clamp(lod, 0, 2);
        if (motionSync.HasValue)
        {
            caps.SupportsMotionSync = true;
            caps.MotionSync = motionSync.Value;
        }
        if (sleepMin.HasValue)
        {
            caps.SupportsSleepTimeout = true;
            caps.MinSleepSeconds = 60;
            caps.MaxSleepSeconds = 900;
            caps.CurrentSleepSeconds = sleepMin.Value * 60;
            caps.SupportedSleepSeconds = [60, 180, 300, 600, 900];
        }
        return caps;
    }

    public static DeviceTuningResult ApplySettings(string deviceId, ushort vid, ushort pid, List<HidDevice> ifaces, string? modelName, DeviceTuningUpdate update)
    {
        bool isLegacy = vid == LegacyVid;
        byte wantReport = isLegacy ? LegacyReportId : CurrentReportId;
        var dev = ifaces.FirstOrDefault(d => HidReportInspector.GetReportIds(d).FeatureIds.Contains(wantReport))
            ?? ifaces.FirstOrDefault(d => HidSafe.MaxFeatureLen(d) >= (isLegacy ? 32 : 16));
        if (dev == null)
            return new DeviceTuningResult(false, "未找到 Ninjutso 控制通道。");

        using var raw = RawHidFeature.TryOpen(dev.DevicePath);
        if (raw == null)
            return new DeviceTuningResult(false, "无法打开 Ninjutso 控制通道。");

        var applied = new List<string>();
        var errors = new List<string>();

        if (isLegacy)
        {
            var data = ReadLegacySettings(raw);
            if (data == null || data.Length < 26)
                return new DeviceTuningResult(false, "读取 Sora V2 配置失败。");
            byte profile = 1;
            int stage = Math.Clamp((int)data[20], 0, 3);

            if (update.ActiveDpiStage is { } act)
            {
                byte st = (byte)Math.Clamp(act, 0, 3);
                if (raw.SetFeature(BuildLegacyRequest(8, profile, st)))
                {
                    stage = st;
                    applied.Add($"档位 {st + 1}");
                }
                else errors.Add("活动档位");
            }
            if (update.Dpi is var (dx, _))
            {
                var (lo, hi) = EncodeLegacyDpi(dx);
                var req = BuildLegacyRequest(9, profile);
                req[9] = (byte)stage;
                req[10] = lo;
                req[11] = hi;
                if (raw.SetFeature(req)) applied.Add($"DPI {dx}");
                else errors.Add("DPI");
            }
            if (update.PollingRateHz is { } hz && hz is 1000 or 2000 or 4000 or 8000)
            {
                byte code = hz switch { 2000 => 1, 4000 => 2, 8000 => 3, _ => 0 };
                if (raw.SetFeature(BuildLegacyRequest(10, profile, code))) applied.Add($"{hz}Hz");
                else errors.Add("回报率");
            }
            if (update.LodCode is { } lod)
            {
                byte code = (byte)Math.Clamp(lod, 0, 2);
                if (raw.SetFeature(BuildLegacyRequest(11, profile, code))) applied.Add("LOD");
                else errors.Add("LOD");
            }
            if (update.MotionSync is { } ms)
            {
                if (raw.SetFeature(BuildLegacyRequest(6, profile, (byte)(ms ? 1 : 0)))) applied.Add($"MotionSync {(ms ? "开" : "关")}");
                else errors.Add("MotionSync");
            }
        }
        else
        {
            byte profile = ReadCurrent(raw, CmdProfile) is { Length: > 0 } p && p[0] > 0 ? p[0] : (byte)1;
            int stage = ReadCurrent(raw, CmdActiveDpiStage) is { Length: > 0 } st ? st[0] : 0;
            bool direct = pid is 0xE010 or 0xEB02;

            if (update.ActiveDpiStage is { } act)
            {
                byte s = (byte)Math.Clamp(act, 0, 3);
                if (raw.SetFeature(BuildCurrentRequest(CmdSetActiveDpiStage, profile, [s])))
                {
                    stage = s;
                    applied.Add($"档位 {s + 1}");
                }
                else errors.Add("活动档位");
            }
            if (update.Dpi is var (dx, _))
            {
                var (lo, hi, t) = EncodeDpi(dx, direct);
                if (raw.SetFeature(BuildCurrentRequest(CmdSetDpi, profile, [(byte)stage, lo, hi, t])))
                    applied.Add($"DPI {dx}");
                else errors.Add("DPI");
            }
            if (update.PollingRateHz is { } hz)
            {
                if (raw.SetFeature(BuildCurrentRequest(CmdSetPollingRate, profile, [EncodePollingRate(hz)])))
                    applied.Add($"{hz}Hz");
                else errors.Add("回报率");
            }
            if (update.LodCode is { } lod)
            {
                byte code = (byte)Math.Clamp(lod, 0, 2);
                if (raw.SetFeature(BuildCurrentRequest(CmdSetLod, profile, [code])))
                    applied.Add("LOD");
                else errors.Add("LOD");
            }
            if (update.MotionSync is { } ms)
            {
                if (raw.SetFeature(BuildCurrentRequest(CmdSetMotionSync, profile, [(byte)(ms ? 1 : 0)])))
                    applied.Add($"MotionSync {(ms ? "开" : "关")}");
                else errors.Add("MotionSync");
            }
            if (update.SleepSeconds is { } sl && sl > 0)
            {
                byte mins = (byte)Math.Clamp((int)Math.Round(sl / 60.0), 1, 15);
                if (raw.SetFeature(BuildCurrentRequest(CmdSetSleepMinutes, profile, [mins])))
                    applied.Add($"休眠 {mins} 分钟");
                else errors.Add("休眠");
            }
        }

        var refreshed = QueryCapabilities(deviceId, vid, pid, ifaces, modelName);
        if (applied.Count == 0 && errors.Count == 0)
            return new DeviceTuningResult(true, "未变更 Ninjutso 参数", refreshed);
        return errors.Count > 0
            ? new DeviceTuningResult(applied.Count > 0, $"部分设置失败 ({string.Join("、", errors)})", refreshed)
            : new DeviceTuningResult(true, $"已写入 {string.Join(" / ", applied)}", refreshed);
    }

    private static DeviceTuningCapabilities QueryLegacyCaps(string deviceId, ushort pid, RawHidFeature raw, string? modelName)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        var data = ReadLegacySettings(raw);
        if (data == null || data.Length < 26)
        {
            caps.StatusMessage = "Sora V2 当前休眠或未返回配置块。";
            return caps;
        }

        int stageCount = Math.Clamp((int)data[11], 1, 4);
        int stage = Math.Clamp((int)data[20], 0, stageCount - 1);
        var stages = new List<(int X, int Y)>(stageCount);
        for (int i = 0; i < stageCount; i++)
        {
            int d = DecodeLegacyDpi(data[12 + i * 2], data[13 + i * 2]);
            stages.Add((d, d));
        }
        int rateHz = pid is 0xAE8C or 0xAE8A
            ? data[24] switch { 1 => 2000, 2 => 4000, 3 => 8000, _ => 1000 }
            : 1000;

        caps.IsOnline = true;
        caps.StatusMessage = $"{modelName ?? "Ninjutso Sora V2"} · Legacy NinjaForce 通道";
        caps.SupportsDpi = true;
        caps.MinDpi = 50;
        caps.MaxDpi = 30000;
        caps.DpiStep = 50;
        caps.DpiStages = stages;
        caps.ActiveDpiStage = stage;
        caps.CurrentDpiX = stages[stage].X;
        caps.CurrentDpiY = stages[stage].Y;
        caps.SupportedPollingRates = pid is 0xAE8C or 0xAE8A ? [.. PollingRates] : [1000];
        caps.CurrentPollingRate = rateHz;
        caps.SupportedLodOptions = [new LodOption(0, "低 (Low)"), new LodOption(1, "中 (Medium)"), new LodOption(2, "高 (High)")];
        caps.CurrentLod = Math.Clamp((int)data[22], 0, 2);
        caps.SupportsMotionSync = true;
        caps.MotionSync = data[23] == 1;
        return caps;
    }

    private static byte[]? ReadCurrent(RawHidFeature raw, byte command, ReadOnlySpan<byte> args = default)
    {
        if (!raw.SetFeature(BuildCurrentRequest(command, 0, args)))
            return null;
        Thread.Sleep(25);
        var resp = raw.GetFeature(1 + CurrentPayloadLen, CurrentReportId);
        return resp != null ? ExtractCurrentResponseValue(resp, command) : null;
    }

    private static byte[]? ReadLegacySettings(RawHidFeature raw)
    {
        if (!raw.SetFeature(BuildLegacyRequest(2, 1)))
            return null;
        Thread.Sleep(35);
        return raw.GetFeature(1 + LegacyPayloadLen, LegacyReportId);
    }
}
