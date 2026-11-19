using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 狼蛛 0xFFA8 协议族（昆仑 L5 Ultra / X5 Max / X5 Ultra / L9 Ultra / SC880 / SC900 V2 / V9 Ultra 等）：
/// 物理端点 UsagePage = 0xFFA8, Usage = 0x0002；下行 ReportID = 0x0B (64B)，上行 ReportID = 0x0A (64B)。
/// </summary>
public static class AulaFfa8Protocol
{
    private const byte OutRid = 0x0B;
    private const byte InRid = 0x0A;

    private const byte CmdGetDevInfo = 0x81;
    private const byte CmdGetMouseCfg = 0x85;
    private const byte CmdSetMouseCfg = 0x86;
    private const byte CmdGetMouseDpiV1 = 0x87;
    private const byte CmdSetMouseDpiV1 = 0x88;
    private const byte CmdSwitchProfile = 0x8F;
    private const byte CmdGetMouseDpiV2 = 0x9A;
    private const byte CmdSetMouseDpiV2 = 0x9B;
    private const byte CmdGetMouseConfigExt = 0xA0;
    private const byte CmdGetAxisCfg = 0xA1;
    private const byte CmdSetAxisCfg = 0xA2;
    private const byte CmdGetCurDevType = 0xC4;

    private static readonly Dictionary<int, int> CodeToHz = new()
    {
        [1] = 1000,
        [2] = 500,
        [4] = 250,
        [8] = 125,
        [16] = 2000,
        [32] = 4000,
        [64] = 8000,
    };

    private static readonly Dictionary<int, byte> HzToCode = new()
    {
        [125] = 8,
        [250] = 4,
        [500] = 2,
        [1000] = 1,
        [2000] = 16,
        [4000] = 32,
        [8000] = 64,
    };

    private static readonly Dictionary<ushort, string> MousePidNames = new()
    {
        [0x2F0C] = "AULA 昆仑 L5 Ultra",
        [0x2F1A] = "AULA 昆仑 L5 Ultra",
        [0x28F7] = "AULA L9",
        [0x28FB] = "AULA L9 Ultra",
        [0x28FD] = "AULA L9 Ultra",
        [0x28FF] = "AULA SC880 Ultra",
        [0x28E7] = "AULA SC880 Max",
        [0x2EE0] = "AULA SC800 Max",
        [0x28E9] = "AULA SC800 Max",
        [0x28E8] = "AULA X5 Max",
        [0x2F08] = "AULA X5 Ultra",
        [0x2F10] = "AULA X5 Mini",
        [0x2EF4] = "AULA SC900 V2 Ultra",
        [0x2EF6] = "AULA SC900 V2 Master",
        [0x2EFE] = "AULA SC900 V2 Pro",
        [0x2EEE] = "AULA V9 Ultra+",
        [0x2EF2] = "AULA V9 Ultra",
        [0x2F14] = "AULA X9 Ultra",
        [0x2F18] = "AULA V3 Max",
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ReceiverModelCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsFfa8Interface(HidDevice d)
    {
        var u = HidSafe.PrimaryUsage(d);
        return u.UsagePage == 0xFFA8 && HidSafe.MaxOutputLen(d) >= 64 && HidSafe.MaxInputLen(d) >= 24;
    }

    public static HidDevice? FindEndpoint(IEnumerable<HidDevice> devs)
        => devs.FirstOrDefault(IsFfa8Interface);

    public static string? ResolveModelName(ushort receiverVid, ushort receiverPid, ushort mousePid = 0)
    {
        if (mousePid != 0 && MousePidNames.TryGetValue(mousePid, out string? byMousePid))
        {
            ReceiverModelCache[$"{receiverVid:X4}:{receiverPid:X4}"] = byMousePid;
            return byMousePid;
        }

        if (ReceiverModelCache.TryGetValue($"{receiverVid:X4}:{receiverPid:X4}", out string? cached))
            return cached;

        return MousePidNames.TryGetValue(receiverPid, out string? byRecPid) ? byRecPid : null;
    }

    public static (int Battery, bool Charging, string? ModelName)? QueryBatteryAndIdentity(HidDevice dev, ushort vid, ushort pid)
    {
        using var ch = HidChannel.TryOpen(dev);
        if (ch == null) return null;

        var info = Exchange(ch, CmdGetDevInfo, null, 240);
        if (info == null || info.Length < 19) return null;

        // 0A 81 len 00 [payload 0..]: [17]=charging (payload[13]), [18]=battery% (payload[14])
        // Wait: payload starts at index 4 => rx[4+13]=rx[17], rx[4+14]=rx[18]
        int pct = info[18];
        bool charging = info[17] != 0;
        if (pct is < 1 or > 100) return null;

        string? model = ResolveModelName(vid, pid);
        if (model == null || pid is 0x2F1A or 0x28F9 or 0x28FD or 0x28E9)
        {
            var ext = Exchange(ch, CmdGetMouseConfigExt, null, 200);
            if (ext != null && ext.Length >= 8)
            {
                ushort mPid = (ushort)(ext[6] | (ext[7] << 8));
                model = ResolveModelName(vid, pid, mPid) ?? model;
            }
        }

        return (pct, charging, model);
    }

    public static DeviceTuningCapabilities QueryCapabilities(
        string deviceId, HidDevice dev, ushort vid, ushort pid, bool isWireless)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        using var ch = HidChannel.TryOpen(dev);
        if (ch == null)
        {
            caps.StatusMessage = "无法打开狼蛛 0xFFA8 控制通道。";
            return caps;
        }

        _ = Exchange(ch, CmdGetCurDevType, null, 120);
        var info = Exchange(ch, CmdGetDevInfo, null, 260);
        var cfg = Exchange(ch, CmdGetMouseCfg, null, 260);
        if (info == null && cfg == null)
        {
            caps.IsOnline = false;
            caps.StatusMessage = "狼蛛鼠标当前休眠或未唤醒，请移动鼠标后重试。";
            return caps;
        }

        var ext = Exchange(ch, CmdGetMouseConfigExt, null, 220);
        bool dpiV2 = ext != null && ext.Length >= 20 && ext[19] != 0;
        int maxRpt = ext != null && ext.Length >= 15 ? (ext[13] | (ext[14] << 8)) : 8000;
        byte indAxisMask = ext != null && ext.Length >= 16 ? ext[15] : (byte)0;
        if (ext != null && ext.Length >= 8)
        {
            ushort mPid = (ushort)(ext[6] | (ext[7] << 8));
            ResolveModelName(vid, pid, mPid);
        }

        caps.IsOnline = true;
        caps.SupportsDpi = true;
        caps.SupportsIndependentXY = true;
        caps.MinDpi = 100;
        caps.MaxDpi = ext != null && ext.Length >= 13 && (ext[11] | (ext[12] << 8)) >= 12000
            ? Math.Clamp((ext[11] | (ext[12] << 8)), 10000, 50000)
            : 42000;
        caps.DpiStep = 50;

        caps.OnboardProfileCount = 4;
        caps.OnboardProfileNames = ["板载配置 1", "板载配置 2", "板载配置 3", "板载配置 4"];
        if (info != null && info.Length >= 21)
            caps.ActiveOnboardProfile = Math.Clamp((int)info[20], 0, 3);

        caps.SupportedPollingRates = maxRpt >= 8000
            ? [125, 250, 500, 1000, 2000, 4000, 8000]
            : maxRpt >= 4000
                ? [125, 250, 500, 1000, 2000, 4000]
                : [125, 250, 500, 1000];

        if (cfg != null && cfg.Length >= 32)
        {
            byte rateByte = cfg[isWireless ? 5 : 4];
            if (CodeToHz.TryGetValue(rateByte, out int hz))
                caps.CurrentPollingRate = hz;

            int stageCount = Math.Clamp((int)cfg[6], 1, 8);
            int curIdx = Math.Clamp((int)cfg[7], 0, stageCount - 1);
            caps.ActiveDpiStage = curIdx;

            caps.SupportedLodOptions = [new(1, "1.0 mm (低)"), new(2, "2.0 mm (高)")];
            caps.CurrentLod = cfg[10] == 2 ? 2 : 1;

            caps.SupportsDebounce = true;
            caps.MaxDebounceMs = 20;
            caps.CurrentDebounceMs = Math.Clamp((int)cfg[11], 0, 20);

            caps.SupportsMotionSync = true;
            caps.MotionSync = cfg[12] != 0;

            caps.SupportsAngleSnapping = true;
            caps.AngleSnapping = cfg[14] != 0;

            caps.SupportsRippleControl = true;
            caps.RippleControl = cfg[15] != 0;

            caps.SupportsSleepTimeout = true;
            caps.MinSleepSeconds = 30;
            caps.MaxSleepSeconds = 1800;
            caps.SupportedSleepSeconds = [30, 60, 120, 300, 600, 1200, 1800];
            caps.CurrentSleepSeconds = Math.Clamp(cfg[29] * 10, 30, 1800);

            ReadDpiStages(ch, dpiV2, stageCount, caps);
        }

        // 读取 SSHS 2.0 电磁微动与 Rapid Trigger (0xA1 btnId=1/2)
        var leftRx = Exchange(ch, CmdGetAxisCfg, [0x01], 220);
        var rightRx = Exchange(ch, CmdGetAxisCfg, [0x02], 220);
        if ((indAxisMask & 0x03) != 0 || leftRx != null || rightRx != null)
        {
            var leftCfg = ParseAxisCfgReply(0, leftRx);
            var rightCfg = ParseAxisCfgReply(1, rightRx);
            if (leftCfg != null || rightCfg != null)
            {
                int maxAct = ExtractAxisMaxAct(leftRx ?? rightRx);
                int maxRt = ExtractAxisMaxRt(leftRx ?? rightRx);
                int maxHap = ExtractAxisMaxHap(leftRx ?? rightRx);
                caps.SupportsAnalogSwitchTuning = true;
                caps.AnalogSwitchTechName = "SSHS 2.0 电磁微动与 RT (AULA 0xFFA8)";
                caps.AnalogMaxActuation = maxAct;
                caps.AnalogMaxRapidTrigger = maxRt;
                caps.AnalogMaxHaptics = maxHap;
                caps.SupportsAnalogHapticsSwitch = true;
                caps.LeftAnalogButton = leftCfg ?? rightCfg! with { ButtonIndex = 0 };
                caps.RightAnalogButton = rightCfg ?? leftCfg! with { ButtonIndex = 1 };
            }
        }

        caps.StatusMessage = caps.SupportsAnalogSwitchTuning
            ? "已读取狼蛛 0xFFA8 硬件参数与 SSHS 2.0 电磁微动/RT 配置。"
            : "已读取狼蛛 0xFFA8 实时硬件参数。";
        return caps;
    }

    public static DeviceTuningResult ApplySettings(
        string deviceId, HidDevice dev, ushort vid, ushort pid, bool isWireless, DeviceTuningUpdate update)
    {
        using var ch = HidChannel.TryOpen(dev);
        if (ch == null)
            return new DeviceTuningResult(false, "无法打开狼蛛 0xFFA8 控制通道。");

        var errors = new List<string>();
        if (update.ActiveOnboardProfile is { } slot)
        {
            var sw = Exchange(ch, CmdSwitchProfile, [(byte)Math.Clamp(slot, 0, 3)], 220);
            if (sw == null) errors.Add("切换板载配置失败");
            Thread.Sleep(35);
        }

        var ext = Exchange(ch, CmdGetMouseConfigExt, null, 180);
        bool dpiV2 = ext != null && ext.Length >= 20 && ext[19] != 0;

        bool needCfgWrite = update.PollingRateHz != null
                            || update.ActiveDpiStage != null
                            || update.DpiStages != null
                            || update.LodCode != null
                            || update.DebounceMs != null
                            || update.MotionSync != null
                            || update.AngleSnapping != null
                            || update.RippleControl != null
                            || update.SleepSeconds != null;

        if (needCfgWrite)
        {
            var curCfgRx = Exchange(ch, CmdGetMouseCfg, null, 240);
            if (curCfgRx == null || curCfgRx.Length < 42)
            {
                errors.Add("读取当前狼蛛配置块失败");
            }
            else
            {
                var block = curCfgRx.AsSpan(4, 38).ToArray();
                if (update.PollingRateHz is { } hz && HzToCode.TryGetValue(hz, out byte rateCode))
                {
                    block[0] = rateCode;
                    block[1] = rateCode;
                }
                if (update.DpiStages is { Count: > 0 } newStages)
                    block[2] = (byte)Math.Clamp(newStages.Count, 1, 8);
                if (update.ActiveDpiStage is { } actIdx)
                    block[3] = (byte)Math.Clamp(actIdx, 0, Math.Max(0, (int)block[2] - 1));
                if (update.LodCode is { } lod)
                    block[6] = (byte)(lod >= 2 ? 2 : 1);
                if (update.DebounceMs is { } deb)
                    block[7] = (byte)Math.Clamp(deb, 0, 20);
                if (update.MotionSync is { } ms)
                    block[8] = (byte)(ms ? 1 : 0);
                if (update.AngleSnapping is { } ang)
                    block[10] = (byte)(ang ? 1 : 0);
                if (update.RippleControl is { } rip)
                    block[11] = (byte)(rip ? 1 : 0);
                if (update.SleepSeconds is { } sleep)
                    block[25] = (byte)Math.Clamp((int)Math.Round(sleep / 10.0), 1, 255);

                var wr = Exchange(ch, CmdSetMouseCfg, block, 240);
                if (wr == null) errors.Add("狼蛛配置参数写入失败");
            }
        }

        if (update.Dpi != null || update.DpiStages != null)
        {
            if (!WriteDpiStages(ch, dpiV2, update))
                errors.Add("狼蛛 DPI 写入失败");
        }

        if (update.AnalogSwitches is { } analog)
        {
            bool okL = WriteAxisCfg(ch, 1, analog.Left);
            var rightTarget = analog.IndependentButtons ? analog.Right : analog.Left;
            bool okR = WriteAxisCfg(ch, 2, rightTarget);
            if (!okL || !okR)
                errors.Add("SSHS 2.0 电磁微动 / RT 写入失败");
        }

        Thread.Sleep(35);
        var refreshed = QueryCapabilities(deviceId, dev, vid, pid, isWireless);
        string msg = errors.Count == 0 ? "已写入狼蛛昆仑 0xFFA8 硬件参数。" : string.Join("；", errors);
        return new DeviceTuningResult(errors.Count == 0, msg, refreshed);
    }

    internal static AnalogButtonConfig? ParseAxisCfgReply(int btnIdx, byte[]? rx)
    {
        // rx[0]=0A, rx[1]=A1, rx[2]=len, rx[3]=status(0), rx[4..12]=9B AxisCfg struct
        if (rx == null || rx.Length < 13 || rx[3] != 0)
            return null;

        int maxAct = rx[5] > 0 ? rx[5] : 10;
        int act = Math.Clamp((int)rx[6], 1, maxAct);
        bool rtOn = rx[7] == 2;
        int maxRt = rx[8] > 0 ? rx[8] : 5;
        int rt = Math.Clamp((int)rx[9], 1, maxRt);
        int maxHap = rx[10] > 0 ? rx[10] : 100;
        int hap = Math.Clamp((int)rx[11], 0, maxHap);
        bool hapOn = rx[12] == 2;
        return new AnalogButtonConfig(btnIdx, act, rtOn, rt, hapOn, hap);
    }

    internal static byte[] BuildAxisCfgWritePayload(int hwBtnId, AnalogButtonConfig cfg, byte[]? existingBlock = null)
    {
        var block = existingBlock is { Length: >= 9 } ? existingBlock.AsSpan(0, 9).ToArray() : new byte[9];
        int maxAct = block[1] > 0 ? block[1] : 10;
        int maxRt = block[4] > 0 ? block[4] : 5;
        int maxHap = block[6] > 0 ? block[6] : 100;

        block[0] = (byte)hwBtnId;
        block[1] = (byte)maxAct;
        block[2] = (byte)Math.Clamp(cfg.Actuation, 1, maxAct);
        block[3] = (byte)(cfg.RapidTriggerEnabled ? 2 : 1);
        block[4] = (byte)maxRt;
        block[5] = (byte)Math.Clamp(cfg.RapidTrigger, 1, maxRt);
        block[6] = (byte)maxHap;
        block[7] = (byte)Math.Clamp(cfg.Haptics, 0, maxHap);
        block[8] = (byte)(cfg.HapticsEnabled ? 2 : 1);
        return block;
    }

    private static int ExtractAxisMaxAct(byte[]? rx) => rx is { Length: >= 13 } && rx[5] > 0 ? rx[5] : 10;
    private static int ExtractAxisMaxRt(byte[]? rx) => rx is { Length: >= 13 } && rx[8] > 0 ? rx[8] : 5;
    private static int ExtractAxisMaxHap(byte[]? rx) => rx is { Length: >= 13 } && rx[10] > 0 ? rx[10] : 100;

    private static bool WriteAxisCfg(HidChannel ch, int hwBtnId, AnalogButtonConfig cfg)
    {
        var cur = Exchange(ch, CmdGetAxisCfg, [(byte)hwBtnId], 200);
        byte[]? existing = cur is { Length: >= 13 } && cur[3] == 0 ? cur.AsSpan(4, 9).ToArray() : null;
        byte[] payload = BuildAxisCfgWritePayload(hwBtnId, cfg, existing);
        var wr = Exchange(ch, CmdSetAxisCfg, payload, 220);
        return wr != null && wr[3] == 0;
    }

    private static void ReadDpiStages(HidChannel ch, bool dpiV2, int stageCount, DeviceTuningCapabilities caps)
    {
        var rx = Exchange(ch, dpiV2 ? CmdGetMouseDpiV2 : CmdGetMouseDpiV1, null, 240);
        if (rx == null) return;

        int offset = dpiV2 ? 4 : 5;
        int stride = dpiV2 ? 7 : 6;
        var stages = new List<(int X, int Y)>();
        for (int i = 0; i < stageCount && offset + i * stride + 4 <= rx.Length; i++)
        {
            int p = offset + i * stride;
            int x = rx[p] | (rx[p + 1] << 8);
            int y = rx[p + 2] | (rx[p + 3] << 8);
            if (!dpiV2)
            {
                x = (x + 1) * 10;
                y = (y + 1) * 10;
            }
            if (x is >= 50 and <= 50000 && y is >= 50 and <= 50000)
                stages.Add((x, y));
        }

        if (stages.Count > 0)
        {
            caps.DpiStages = stages;
            caps.SupportsDpiStagesWrite = true;
            int active = Math.Clamp(caps.ActiveDpiStage, 0, stages.Count - 1);
            caps.ActiveDpiStage = active;
            caps.CurrentDpiX = stages[active].X;
            caps.CurrentDpiY = stages[active].Y;
        }
    }

    private static bool WriteDpiStages(HidChannel ch, bool dpiV2, DeviceTuningUpdate update)
    {
        byte readCmd = dpiV2 ? CmdGetMouseDpiV2 : CmdGetMouseDpiV1;
        byte writeCmd = dpiV2 ? CmdSetMouseDpiV2 : CmdSetMouseDpiV1;
        int blockLen = dpiV2 ? 56 : 49;
        int offset = dpiV2 ? 0 : 1;
        int stride = dpiV2 ? 7 : 6;

        var rx = Exchange(ch, readCmd, null, 220);
        var cfgRx = Exchange(ch, CmdGetMouseCfg, null, 180);
        int activeIdx = update.ActiveDpiStage ?? (cfgRx is { Length: >= 8 } ? Math.Clamp((int)cfgRx[7], 0, 7) : 0);

        byte[] payload = rx != null && rx.Length >= 4 + blockLen
            ? rx.AsSpan(4, blockLen).ToArray()
            : new byte[blockLen];

        if (!dpiV2)
            payload[0] = (byte)(update.DpiStages?.Count is > 0 and <= 8 ? update.DpiStages.Count : Math.Max(1, (int)payload[0]));

        if (update.DpiStages is { Count: > 0 } stages)
        {
            for (int i = 0; i < Math.Min(8, stages.Count); i++)
                EncodeDpiSlot(payload, offset + i * stride, stages[i].X, stages[i].Y, dpiV2);
        }
        else if (update.Dpi is { } single)
        {
            EncodeDpiSlot(payload, offset + activeIdx * stride, single.X, single.Y, dpiV2);
        }

        var wr = Exchange(ch, writeCmd, payload, 240);
        return wr != null && wr[3] == 0;
    }

    private static void EncodeDpiSlot(byte[] buf, int pos, int x, int y, bool dpiV2)
    {
        if (pos + 4 > buf.Length) return;
        int vx = dpiV2 ? Math.Clamp(x, 50, 50000) : Math.Clamp(x / 10 - 1, 0, 4999);
        int vy = dpiV2 ? Math.Clamp(y, 50, 50000) : Math.Clamp(y / 10 - 1, 0, 4999);
        buf[pos] = (byte)(vx & 0xFF);
        buf[pos + 1] = (byte)(vx >> 8);
        buf[pos + 2] = (byte)(vy & 0xFF);
        buf[pos + 3] = (byte)(vy >> 8);
    }

    private static byte[]? Exchange(HidChannel ch, byte cmd, byte[]? args, int timeoutMs)
    {
        while (ch.Read(3) != null) { }

        var req = new byte[Math.Max(ch.OutputLength, 64)];
        req[0] = OutRid;
        req[1] = cmd;
        req[2] = (byte)(args?.Length ?? 0);
        if (args is { Length: > 0 })
            Array.Copy(args, 0, req, 3, Math.Min(args.Length, req.Length - 3));

        if (!ch.WriteOutput(req))
            return null;

        long dl = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < dl)
        {
            var r = ch.Read(75);
            if (r != null && r.Length >= 8 && r[0] == InRid && r[1] == cmd)
                return r;
        }
        return null;
    }
}
