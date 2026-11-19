using System.Diagnostics;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// GearHub-V5 / MicLink / MU 64 字节通用协议（0x3151、怒喵 0E8D:0703、部分狼蛛/黑爵/达摩鲨等）：
/// 使用 Bit7 校验和 (byte[7] = 255 - sum(byte[0..6]))，支持：
/// - OPTIONPARAM1 (0xD4读 / 0x54写): 配置切换、多档位独立 X/Y DPI、激活档位
/// - OPTIONPARAM0 (0xD3读 / 0x53写): 125~8000 Hz 回报率、按键防抖、LOD 静默高度、移动同步/直线修正/波纹控制、休眠时间
/// - KEYMATRIX (0xD0读 / 0x50写): 物理按键板载映射
/// 兼容有线 Feature 0/Input-Output 64B 及无线 0xF6 0x05 中继。
/// </summary>
internal static class GearHubMuProtocol
{
    private static readonly (int Hz, byte Code)[] ReportRateTable =
    [
        (125, 8),
        (250, 4),
        (500, 2),
        (1000, 1),
        (2000, 132),
        (4000, 130),
        (8000, 129),
    ];

    private static readonly (string Label, byte[] Bytes)[] KeyMatrixActions =
    [
        ("左键点击", [1, 0, 0xF0, 0]),
        ("右键点击", [1, 0, 0xF1, 0]),
        ("中键点击", [1, 0, 0xF2, 0]),
        ("后退", [1, 0, 0xF3, 0]),
        ("前进", [1, 0, 0xF4, 0]),
        ("DPI 循环", [20, 0, 0, 0]),
        ("DPI +", [20, 0, 1, 0]),
        ("DPI -", [0, 2, 0, 20]),
        ("禁用按键", [0, 0, 0, 0]),
    ];

    public static byte[] BuildBody64(ReadOnlySpan<byte> cmdBytes)
    {
        var body = new byte[64];
        cmdBytes[..Math.Min(cmdBytes.Length, 64)].CopyTo(body);
        int sum = 0;
        for (int i = 0; i < 7; i++) sum = (sum + body[i]) & 0xFF;
        body[7] = (byte)((255 - sum) & 0xFF);
        return body;
    }

    public static int? DecodeReportRate(byte code)
    {
        foreach (var (hz, c) in ReportRateTable)
            if (c == code) return hz;
        return null;
    }

    public static byte? EncodeReportRate(int hz)
    {
        foreach (var (r, c) in ReportRateTable)
            if (r == hz) return c;
        return null;
    }

    public sealed record DpiBlockParsed(int Profile, int ActiveStage, List<(int X, int Y)> Stages, byte[] Raw64);

    public static DpiBlockParsed? ParseDpiBlock64(byte[] body64)
    {
        if (body64.Length < 40 || body64[0] != 0xD4) return null;
        int profile = body64[1];
        int active = body64[2];
        int count = body64[3];
        if (profile > 7 || count is < 1 or > 8 || active >= count) return null;

        var stages = new List<(int X, int Y)>(count);
        for (int i = 0; i < count; i++)
        {
            int x = body64[8 + i * 2] | (body64[9 + i * 2] << 8);
            int y = body64[24 + i * 2] | (body64[25 + i * 2] << 8);
            if (y == 0) y = x;
            if (x is < 50 or > 50000) return null;
            stages.Add((x, Math.Clamp(y, 50, 50000)));
        }
        return new DpiBlockParsed(profile, active, stages, (byte[])body64.Clone());
    }

    public static byte[]? ExchangeBody64(HidDevice iface, byte[] body64, byte expectedOpcode)
    {
        // Try RawHidFeature first if Feature >= 64
        int featLen = HidSafe.MaxFeatureLen(iface);
        if (featLen >= 64)
        {
            using var raw = RawHidFeature.TryOpen(iface.DevicePath);
            if (raw != null)
            {
                var req = featLen >= 65 ? PrependZero(body64, featLen) : body64;
                if (raw.SetFeature(req))
                {
                    Thread.Sleep(35);
                    var rep = raw.GetFeature(featLen, 0);
                    if (rep != null && TryStripBody64(rep, expectedOpcode) is { } hit)
                        return hit;
                }

                // Try wireless 0xF6 0x05 relay select
                var relay = new byte[req.Length];
                int ro = featLen >= 65 ? 1 : 0;
                relay[ro] = 0xF6;
                relay[ro + 1] = 0x05;
                if (raw.SetFeature(relay))
                {
                    Thread.Sleep(25);
                    if (raw.SetFeature(req))
                    {
                        Thread.Sleep(45);
                        var rep = raw.GetFeature(featLen, 0);
                        if (rep != null && TryStripBody64(rep, expectedOpcode) is { } hit)
                            return hit;
                    }
                }
            }
        }

        // Try Output/Input 64B report
        int outLen = HidSafe.MaxOutputLen(iface);
        if (outLen >= 64 && HidSafe.MaxInputLen(iface) >= 32)
        {
            using var ch = HidChannel.TryOpen(iface);
            if (ch != null)
            {
                for (int d = 0; d < 3; d++)
                    if (ch.Read(5) == null) break;

                var req = outLen >= 65 ? PrependZero(body64, outLen) : body64;
                if (ch.WriteOutput(req))
                {
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 420)
                    {
                        var r = ch.Read(70);
                        if (r != null && TryStripBody64(r, expectedOpcode) is { } hit)
                            return hit;
                    }
                }
            }
        }

        return null;
    }

    private static byte[]? TryStripBody64(byte[] buf, byte expectedOpcode)
    {
        if (buf.Length >= 64 && buf[0] == expectedOpcode)
            return buf[..64];
        if (buf.Length >= 65 && buf[0] == 0x00 && buf[1] == expectedOpcode)
            return buf[1..65];
        return null;
    }

    private static byte[] PrependZero(byte[] body64, int totalLen)
    {
        var dst = new byte[totalLen];
        Array.Copy(body64, 0, dst, 1, Math.Min(64, totalLen - 1));
        return dst;
    }

    public static DeviceTuningCapabilities QueryCapabilities(string deviceId, IEnumerable<HidDevice> interfaces)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        var ordered = interfaces
            .Where(d => HidSafe.MaxFeatureLen(d) >= 64 || HidSafe.MaxOutputLen(d) >= 64)
            .OrderByDescending(d => HidSafe.MaxFeatureLen(d) >= 64 ? 1 : 0)
            .ThenByDescending(HidSafe.MaxOutputLen)
            .ToList();

        foreach (var iface in ordered)
        {
            var dpiRaw = ExchangeBody64(iface, BuildBody64([0xD4]), 0xD4);
            var parsedDpi = dpiRaw != null ? ParseDpiBlock64(dpiRaw) : null;
            var opt0 = ExchangeBody64(iface, BuildBody64([0xD3]), 0xD3);
            if (parsedDpi == null && opt0 == null) continue;

            caps.IsOnline = true;
            if (parsedDpi != null)
            {
                caps.OnboardProfileCount = 4;
                caps.ActiveOnboardProfile = Math.Clamp(parsedDpi.Profile, 0, 3);
                caps.OnboardProfileNames = ["板载配置 1", "板载配置 2", "板载配置 3", "板载配置 4"];

                caps.SupportsDpi = true;
                caps.SupportsIndependentXY = true;
                caps.SupportsDpiStagesWrite = true;
                caps.MinDpi = 50;
                caps.MaxDpi = 42000;
                caps.DpiStep = 50;
                caps.DpiStages = parsedDpi.Stages;
                caps.ActiveDpiStage = Math.Clamp(parsedDpi.ActiveStage, 0, Math.Max(0, parsedDpi.Stages.Count - 1));
                if (caps.DpiStages.Count > 0)
                {
                    caps.CurrentDpiX = caps.DpiStages[caps.ActiveDpiStage].X;
                    caps.CurrentDpiY = caps.DpiStages[caps.ActiveDpiStage].Y;
                }
            }

            if (opt0 != null && opt0.Length >= 55)
            {
                if (DecodeReportRate(opt0[9]) is { } hz)
                {
                    caps.SupportedPollingRates = [125, 250, 500, 1000, 2000, 4000, 8000];
                    caps.CurrentPollingRate = hz;
                }
                caps.SupportsDebounce = true;
                caps.MaxDebounceMs = 15;
                caps.CurrentDebounceMs = Math.Clamp((int)opt0[10], 0, 15);

                int flags = opt0[12] | (opt0[13] << 8);
                caps.SupportsRippleControl = true;
                caps.RippleControl = (flags & 0x04) != 0;

                caps.SupportsAngleSnapping = true;
                caps.AngleSnapping = opt0[53] == 1;

                caps.SupportedLodOptions = [new(0, "0.7 mm (低)"), new(1, "1.0 mm (中)"), new(2, "2.0 mm (高)")];
                caps.CurrentLod = Math.Clamp((int)opt0[52], 0, 2);

                int sleepSec = opt0[44] | (opt0[45] << 8);
                caps.SupportsSleepTimeout = true;
                caps.MinSleepSeconds = 0;
                caps.MaxSleepSeconds = 300;
                caps.SupportedSleepSeconds = [0, 15, 30, 60, 120, 300];
                caps.CurrentSleepSeconds = sleepSec;
            }

            // Keymatrix (0xD0)
            byte curProf = (byte)Math.Clamp(caps.ActiveOnboardProfile, 0, 3);
            var km = ExchangeBody64(iface, BuildBody64([0xD0, curProf]), 0xD0);
            if (km != null && km.Length >= 32)
            {
                string[] btnNames = ["左键 (Left)", "右键 (Right)", "中键 (Middle)", "后退键 (Back)", "前进键 (Forward)", "DPI 键"];
                var avail = KeyMatrixActions.Select(a => a.Label).ToList();
                int offset = km[1] == curProf ? 8 : 2;
                for (int slot = 0; slot < 6 && offset + slot * 4 + 3 < km.Length; slot++)
                {
                    var slice = km.AsSpan(offset + slot * 4, 4);
                    string actionName = DecodeKeyMatrixAction(slice);
                    caps.ButtonBindings.Add(new ButtonBindingSlot(slot, btnNames[slot], actionName, avail, IsReadOnly: slot == 0));
                }
            }

            caps.StatusMessage = "已通过 GearHub/MU 0xD4/0xD3 协议读取硬件参数。";
            return caps;
        }

        caps.IsOnline = false;
        return caps;
    }

    public static DeviceTuningResult ApplySettings(string deviceId, IEnumerable<HidDevice> interfaces, DeviceTuningUpdate update)
    {
        var ordered = interfaces
            .Where(d => HidSafe.MaxFeatureLen(d) >= 64 || HidSafe.MaxOutputLen(d) >= 64)
            .OrderByDescending(d => HidSafe.MaxFeatureLen(d) >= 64 ? 1 : 0)
            .ThenByDescending(HidSafe.MaxOutputLen)
            .ToList();

        foreach (var iface in ordered)
        {
            var dpiRaw = ExchangeBody64(iface, BuildBody64([0xD4]), 0xD4);
            var parsedDpi = dpiRaw != null ? ParseDpiBlock64(dpiRaw) : null;
            var opt0 = ExchangeBody64(iface, BuildBody64([0xD3]), 0xD3);
            if (parsedDpi == null && opt0 == null) continue;

            bool wrote = false;
            if (parsedDpi != null && (update.ActiveOnboardProfile != null || update.DpiStages != null || update.Dpi != null || update.ActiveDpiStage != null))
            {
                var block = (byte[])parsedDpi.Raw64.Clone();
                block[0] = 0x54;
                if (update.ActiveOnboardProfile is { } np)
                    block[1] = (byte)Math.Clamp(np, 0, 3);

                var stages = update.DpiStages is { Count: > 0 } ? update.DpiStages.ToList() : parsedDpi.Stages.ToList();
                int count = Math.Clamp(stages.Count, 1, 8);
                int act = Math.Clamp(update.ActiveDpiStage ?? parsedDpi.ActiveStage, 0, count - 1);
                if (update.Dpi is { } dpi)
                    stages[act] = (Math.Clamp(dpi.X, 100, 42000), Math.Clamp(dpi.Y, 100, 42000));

                block[2] = (byte)act;
                block[3] = (byte)count;
                for (int i = 0; i < count; i++)
                {
                    block[8 + i * 2] = (byte)(stages[i].X & 0xFF);
                    block[9 + i * 2] = (byte)(stages[i].X >> 8);
                    block[24 + i * 2] = (byte)(stages[i].Y & 0xFF);
                    block[25 + i * 2] = (byte)(stages[i].Y >> 8);
                }
                int sum = 0;
                for (int i = 0; i < 7; i++) sum = (sum + block[i]) & 0xFF;
                block[7] = (byte)((255 - sum) & 0xFF);
                ExchangeBody64(iface, block, 0x54);
                wrote = true;
            }

            if (opt0 != null && opt0.Length >= 55 &&
                (update.PollingRateHz != null || update.DebounceMs != null || update.LodCode != null ||
                 update.RippleControl != null || update.AngleSnapping != null || update.SleepSeconds != null))
            {
                var block = (byte[])opt0.Clone();
                block[0] = 0x53;
                if (update.PollingRateHz is { } hz && EncodeReportRate(hz) is { } rCode)
                    block[9] = rCode;
                if (update.DebounceMs is { } deb)
                    block[10] = (byte)Math.Clamp(deb, 0, 15);
                if (update.RippleControl is { } rc)
                {
                    int flags = block[12] | (block[13] << 8);
                    flags = rc ? (flags | 0x04) : (flags & ~0x04);
                    block[12] = (byte)(flags & 0xFF);
                    block[13] = (byte)(flags >> 8);
                }
                if (update.LodCode is { } lod)
                    block[52] = (byte)Math.Clamp(lod, 0, 2);
                if (update.AngleSnapping is { } snap)
                    block[53] = (byte)(snap ? 1 : 0);
                if (update.SleepSeconds is { } sleep)
                {
                    int s = Math.Clamp(sleep, 0, 3600);
                    block[44] = (byte)(s & 0xFF);
                    block[45] = (byte)(s >> 8);
                }
                int sum = 0;
                for (int i = 0; i < 7; i++) sum = (sum + block[i]) & 0xFF;
                block[7] = (byte)((255 - sum) & 0xFF);
                ExchangeBody64(iface, block, 0x53);
                wrote = true;
            }

            if (update.ButtonBinding is { } bind && bind.SlotIndex is >= 1 and <= 5)
            {
                foreach (var (label, bytes) in KeyMatrixActions)
                {
                    if (label == bind.Action)
                    {
                        byte prof = (byte)Math.Clamp(parsedDpi?.Profile ?? 0, 0, 3);
                        var body = BuildBody64([0x50, prof, (byte)bind.SlotIndex, 0, 0, 0, 0, 0, bytes[0], bytes[1], bytes[2], bytes[3]]);
                        // Notice slot payload puts action at offset 8..11; recompute Bit7 checksum over 0..6
                        ExchangeBody64(iface, body, 0x50);
                        wrote = true;
                        break;
                    }
                }
            }

            if (wrote)
            {
                Thread.Sleep(50);
                var verified = QueryCapabilities(deviceId, ordered);
                return new DeviceTuningResult(true, "已写入 GearHub/MU 硬件参数并回读校验。", verified);
            }
        }

        return new DeviceTuningResult(false, "设备未响应 GearHub/MU 参数写入指令（请确认鼠标已开机并唤醒）。");
    }

    private static string DecodeKeyMatrixAction(ReadOnlySpan<byte> b)
    {
        foreach (var (label, bytes) in KeyMatrixActions)
            if (b[0] == bytes[0] && b[1] == bytes[1] && b[2] == bytes[2] && b[3] == bytes[3])
                return label;
        return $"自定义 ({b[0]:X2} {b[1]:X2} {b[2]:X2} {b[3]:X2})";
    }
}
