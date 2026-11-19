using System.Diagnostics;
using System.Globalization;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 迈从 MCHOSE 全系（自动覆盖 0x3837 与 0x5253 RealTek，兼容 V3 0x4D 协议与 V2 0x11/0x12 取反协议）：
/// 支持电量、板载配置切换（Config 1~3+）、多档位 XY 独立 DPI 读写、125~8000 Hz 回报率、
/// 性能模式（Performance 常规 / eSports 电竞 / Ultra 狂暴）、LOD、移动同步、直线修正、波纹控制、防抖、休眠及角度微调。
/// </summary>
public sealed class MchoseProvider : ITunablePeripheralProvider
{
    public const string ProviderName = "MCHOSE";
    public string Name => ProviderName;

    internal static readonly int[] V3DeviceRateTable = [125, 250, 500, 1000, 2000, 4000, 8000];
    internal static readonly int[] V3UiRateList = [125, 500, 1000, 2000, 4000, 8000];
    private static readonly int[] V2UiRateList = [125, 500, 1000, 2000, 4000, 8000];

    private static readonly (string Label, byte Type, int Value)[] V2ButtonActions =
    [
        ("左键点击", 1, 0x010000),
        ("右键点击", 1, 0x020000),
        ("中键点击", 1, 0x040000),
        ("后退", 1, 0x080000),
        ("前进", 1, 0x100000),
        ("DPI 循环", 5, 0x010000),
        ("DPI +", 5, 0x020000),
        ("DPI -", 5, 0x030000),
        ("播放/暂停", 3, 0xCD0000),
        ("音量 +", 3, 0xE90000),
        ("音量 -", 3, 0xEA0000),
        ("静音", 3, 0xE20000),
        ("切换板载配置 1", 10, 0x010000),
        ("切换板载配置 2", 10, 0x020000),
        ("切换板载配置 3", 10, 0x030000),
        ("禁用按键", 9, 0xFFFFFF),
    ];

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("mchose:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();

        var candidateGroups = HidSafe.EnumerateDevices()
            .Where(IsMchoseDevice)
            .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID));

        foreach (var g in candidateGroups)
        {
            var (vid, pid) = g.Key;
            string? rawName = g.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            string cleanName = CleanName(rawName) ?? $"MCHOSE {vid:X4}:{pid:X4}";
            var kind = InferKind(cleanName);

            (int Percent, bool Charging)? hit = null;

            if (vid is 0x3837 or 0x5253)
            {
                // Prefer vendor config interfaces (output>=64 / feature>=64)
                foreach (var iface in g.Where(d => HidSafe.MaxOutputLen(d) >= 64 || HidSafe.MaxFeatureLen(d) >= 64)
                                       .OrderByDescending(d => HidSafe.PrimaryUsage(d).UsagePage == 0xFF01 ? 1 : 0)
                                       .ThenByDescending(HidSafe.MaxOutputLen))
                {
                    if (HitBatteryOnInterface(iface, vid, pid) is { } bHit)
                    {
                        hit = bHit;
                        break;
                    }
                }
            }
            else
            {
                foreach (var iface in g.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17))
                {
                    using var ch = HidChannel.TryOpen(iface);
                    if (ch == null) continue;
                    if (Compx08Protocol.QueryBattery(ch, 0x08, 450) is { } c08)
                    {
                        hit = (c08.Level, c08.Charging);
                        break;
                    }
                }
            }

            results.Add(new PeripheralDevice
            {
                Id = $"mchose:{vid:X4}-{pid:X4}",
                VendorId = vid,
                ProductId = pid,
                Name = cleanName,
                Kind = kind,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = hit == null && rawName?.Contains("Wired", StringComparison.OrdinalIgnoreCase) != true,
                Transport = rawName?.Contains("Wired", StringComparison.OrdinalIgnoreCase) == true ? "USB" : "2.4G",
                BatteryPercent = hit?.Percent,
                State = hit is { Charging: true } ? BatteryState.Charging : BatteryState.Normal,
                Note = hit == null ? "迈从接收器已连接（鼠标休眠中，晃动唤醒）" : null,
                LastUpdated = DateTimeOffset.Now,
            });
        }

        return results;
    }

    private static (int Percent, bool Charging)? HitBatteryOnInterface(HidDevice iface, ushort vid, ushort pid)
    {
        // Try Gen 3 (0x4D output) if output>=64 & input>=21
        if (HidSafe.MaxOutputLen(iface) >= 64 && HidSafe.MaxInputLen(iface) >= 21)
        {
            using var ch = HidChannel.TryOpen(iface);
            if (ch != null)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (attempt > 0) Thread.Sleep(110);
                    var payload = SendMchoseV3Command(ch, 0x0900, default, 750);
                    if (payload is { Length: >= 14 } && (payload[0] | (payload[1] << 8)) is 0x3837 or 0x5253 &&
                        payload[10] != 0 && payload[12] <= 100)
                    {
                        return (payload[12], payload[11] == 1);
                    }
                }
            }
        }

        // Try Gen 2 (0x11/0x12 inverted Feature Report)
        if (HidSafe.MaxFeatureLen(iface) >= 20)
        {
            using var raw = RawHidFeature.TryOpen(iface.DevicePath);
            if (raw != null)
            {
                var bat = QueryV2Battery(raw);
                if (bat != null) return bat;
            }
        }

        return null;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
        {
            caps.StatusMessage = "无效的迈从设备 ID。";
            return caps;
        }

        var devs = HidSafe.EnumerateDevices(vid, pid);
        if (devs.Count == 0)
        {
            caps.StatusMessage = "未找到迈从设备控制接口。";
            return caps;
        }

        string? rawName = devs.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        bool isWired = rawName?.Contains("Wired", StringComparison.OrdinalIgnoreCase) == true || pid == 0x1011;

        if (vid is not (0x3837 or 0x5253))
        {
            foreach (var d in devs.Where(x => HidSafe.MaxOutputLen(x) >= 17 || HidSafe.MaxFeatureLen(x) >= 17))
            {
                using var ch = HidChannel.TryOpen(d);
                if (ch == null) continue;
                var c = Compx08Protocol.QueryCapabilities(deviceId, ch, isWireless: !isWired, productName: rawName);
                if (c.SupportsDpi || c.SupportedPollingRates.Count > 0) return c;
            }
            caps.IsOnline = false;
            caps.StatusMessage = "当前旧版迈从型号未响应 0x08 协议或处于休眠状态。";
            return caps;
        }

        var candidates = devs
            .Where(d => HidSafe.MaxOutputLen(d) >= 64 || HidSafe.MaxFeatureLen(d) >= 20)
            .OrderByDescending(d => HidSafe.PrimaryUsage(d).UsagePage == 0xFF01 ? 1 : 0)
            .ThenByDescending(HidSafe.MaxOutputLen)
            .ToList();

        // 1. Try Gen 3 (0x4D)
        foreach (var iface in candidates.Where(d => HidSafe.MaxOutputLen(d) >= 64 && HidSafe.MaxInputLen(d) >= 21))
        {
            using var mch = HidChannel.TryOpen(iface);
            if (mch == null) continue;
            var res = QueryV3Capabilities(deviceId, mch, isWired, rawName);
            if (res.IsOnline) return res;
        }

        // 2. Try Gen 2 (0x11/0x12 bit-inverted)
        foreach (var iface in candidates.Where(d => HidSafe.MaxFeatureLen(d) >= 20))
        {
            using var raw = RawHidFeature.TryOpen(iface.DevicePath);
            if (raw == null) continue;
            var res = QueryV2Capabilities(deviceId, raw, isWired, rawName);
            if (res.IsOnline) return res;
        }

        caps.IsOnline = false;
        caps.StatusMessage = "迈从鼠标当前休眠或未开启，请晃动鼠标唤醒后点击「读取当前硬件参数」。";
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new DeviceTuningResult(false, "无效的迈从设备 ID。");

        var devs = HidSafe.EnumerateDevices(vid, pid);
        string? rawName = devs.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        bool isWired = rawName?.Contains("Wired", StringComparison.OrdinalIgnoreCase) == true || pid == 0x1011;

        if (vid is not (0x3837 or 0x5253))
        {
            foreach (var d in devs.Where(x => HidSafe.MaxOutputLen(x) >= 17 || HidSafe.MaxFeatureLen(x) >= 17))
            {
                using var lch = HidChannel.TryOpen(d);
                if (lch == null) continue;
                var res = Compx08Protocol.ApplySettings(deviceId, lch, update, isWireless: !isWired, productName: rawName);
                if (res.Success) return res;
            }
            return new DeviceTuningResult(false, "该旧版迈从型号未响应写入。");
        }

        var candidates = devs
            .Where(d => HidSafe.MaxOutputLen(d) >= 64 || HidSafe.MaxFeatureLen(d) >= 20)
            .OrderByDescending(d => HidSafe.PrimaryUsage(d).UsagePage == 0xFF01 ? 1 : 0)
            .ThenByDescending(HidSafe.MaxOutputLen)
            .ToList();

        foreach (var iface in candidates.Where(d => HidSafe.MaxOutputLen(d) >= 64 && HidSafe.MaxInputLen(d) >= 21))
        {
            using var mch = HidChannel.TryOpen(iface);
            if (mch == null) continue;
            if (SendMchoseV3Command(mch, 0x0002, default, 900) != null)
                return ApplyV3Settings(deviceId, mch, isWired, rawName, update);
        }

        foreach (var iface in candidates.Where(d => HidSafe.MaxFeatureLen(d) >= 20))
        {
            using var raw = RawHidFeature.TryOpen(iface.DevicePath);
            if (raw == null) continue;
            if (QueryV2Battery(raw) != null || ExchangeV2Feature(raw, 0x12, [0x67]) != null)
                return ApplyV2Settings(deviceId, raw, isWired, rawName, update);
        }

        return new DeviceTuningResult(false, "迈从鼠标当前休眠中，请先晃动唤醒后重试。");
    }

    // =========================================================================
    //  MCHOSE Gen 3 (0x4D Output Report)
    // =========================================================================

    private static DeviceTuningCapabilities QueryV3Capabilities(string deviceId, HidChannel ch, bool isWired, string? rawName)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        var cfgReply = SendMchoseV3Command(ch, 0x0002, default, 1150);
        if (cfgReply == null || cfgReply.Length < 9)
            return caps;

        var infoReply = SendMchoseV3Command(ch, 0x0900, default, 750);
        int profCount = infoReply is { Length: >= 5 } && infoReply[4] is >= 1 and <= 5 ? infoReply[4] : 3;

        caps.IsOnline = true;
        var parsedCfg = ParseV3Settings(cfgReply);
        caps.OnboardProfileCount = profCount;
        caps.ActiveOnboardProfile = Math.Clamp((int)parsedCfg.ProfileIndex, 0, profCount - 1);
        caps.OnboardProfileNames = Enumerable.Range(1, profCount).Select(i => $"板载配置 {i}").ToList();

        byte profile = parsedCfg.ProfileIndex;
        var xReply = SendMchoseV3Command(ch, 0x0003, [profile, 0], 1150);
        var xTable = xReply != null ? ParseV3DpiTable(xReply) : null;
        if (xTable != null)
        {
            caps.SupportsDpi = true;
            caps.SupportsIndependentXY = true;
            caps.SupportsDpiStagesWrite = true;
            caps.MinDpi = 200;
            caps.MaxDpi = 42000;
            caps.DpiStep = 50;
            caps.ActiveDpiStage = Math.Clamp(xTable.ActiveStage, 0, Math.Max(0, xTable.EnabledStages.Count - 1));

            List<int>? yValues = null;
            if (xTable.HasSeparateY)
            {
                var yReply = SendMchoseV3Command(ch, 0x0003, [profile, 1], 1150);
                var yTable = yReply != null ? ParseV3DpiTable(yReply) : null;
                if (yTable != null) yValues = yTable.EnabledStages;
            }

            caps.DpiStages = new List<(int X, int Y)>(xTable.EnabledStages.Count);
            for (int i = 0; i < xTable.EnabledStages.Count; i++)
                caps.DpiStages.Add((xTable.EnabledStages[i], yValues != null && i < yValues.Count ? yValues[i] : xTable.EnabledStages[i]));

            if (caps.DpiStages.Count > 0)
            {
                caps.CurrentDpiX = caps.DpiStages[caps.ActiveDpiStage].X;
                caps.CurrentDpiY = caps.DpiStages[caps.ActiveDpiStage].Y;
            }
        }

        int devRateSlot = isWired ? parsedCfg.RawWiredRateSlot : parsedCfg.RawWirelessRateSlot;
        caps.SupportedPollingRates = V3UiRateList.ToList();
        if ((uint)devRateSlot < (uint)V3DeviceRateTable.Length)
            caps.CurrentPollingRate = V3DeviceRateTable[devRateSlot];

        // Sensor byte layout (V3: bits0-1 mode [0=Perf, 1=eSports, 2=Ultra], bit2 ripple, bit3 linear, bit4 motionSync, bits5-6 lod)
        caps.SupportedPerformanceModes =
        [
            new(0, "性能模式 (Performance)", "均衡性能与常规续航"),
            new(1, "电竞模式 (eSports)", "提升传感器响应与无线抗干扰"),
            new(2, "狂暴竞技模式 (Ultra)", "20K FPS 满血高敏追踪"),
        ];
        caps.CurrentPerformanceMode = Math.Clamp(parsedCfg.Sensor & 0x03, 0, 2);

        caps.SupportsRippleControl = true;
        caps.RippleControl = (parsedCfg.Sensor & 0x04) != 0;

        caps.SupportsAngleSnapping = true;
        caps.AngleSnapping = (parsedCfg.Sensor & 0x08) != 0;

        caps.SupportsMotionSync = true;
        caps.MotionSync = (parsedCfg.Sensor & 0x10) != 0;

        caps.SupportsAngleTuning = true;
        caps.AngleTuningDeg = Math.Clamp((int)parsedCfg.AngleTuning, -30, 30);

        caps.SupportsDebounce = true;
        caps.MaxDebounceMs = 20;
        caps.CurrentDebounceMs = Math.Clamp((int)parsedCfg.LeftDebounceMs, 0, 20);

        caps.SupportsSleepTimeout = true;
        caps.MinSleepSeconds = 0;
        caps.MaxSleepSeconds = 1800;
        caps.SupportedSleepSeconds = [0, 60, 120, 180, 300, 600, 1800];
        caps.CurrentSleepSeconds = parsedCfg.SleepMinutes * 60;

        // Check if 0x0009 discrete LOD command answers, else use sensor bits 5-6
        var lodReply = SendMchoseV3Command(ch, 0x0009, [profile], 750);
        if (lodReply is { Length: >= 1 } && lodReply[0] <= 4)
        {
            caps.SupportedLodOptions = [new(0, "0.7 mm (低)"), new(1, "1.0 mm (中)"), new(2, "1.4 mm"), new(3, "2.0 mm (高)")];
            caps.CurrentLod = Math.Clamp((int)lodReply[0], 0, 3);
        }
        else
        {
            caps.SupportedLodOptions = [new(0, "0.7 mm (低)"), new(1, "1.0 mm (中)"), new(2, "2.0 mm (高)")];
            caps.CurrentLod = Math.Clamp((parsedCfg.Sensor >> 5) & 0x03, 0, 2);
        }

        caps.StatusMessage = $"已读取迈从 V3 硬件配置（配置 {caps.ActiveOnboardProfile + 1} · {caps.SupportedPerformanceModes[caps.CurrentPerformanceMode].Label}）。";
        return caps;
    }

    private DeviceTuningResult ApplyV3Settings(string deviceId, HidChannel ch, bool isWired, string? rawName, DeviceTuningUpdate update)
    {
        var cfgReply = SendMchoseV3Command(ch, 0x0002, default, 1150);
        if (cfgReply == null || cfgReply.Length < 9)
            return new DeviceTuningResult(false, "迈从 V3 鼠标未响应配置读取。");

        var settings = ParseV3Settings(cfgReply);
        bool writeSettingsBlock = false;

        if (update.ActiveOnboardProfile is { } newProf)
        {
            settings.ProfileIndex = (byte)Math.Clamp(newProf, 0, 4);
            writeSettingsBlock = true;
        }

        if (update.PollingRateHz is { } hz)
        {
            int devSlot = Array.IndexOf(V3DeviceRateTable, hz);
            if (devSlot >= 0)
            {
                if (isWired) settings.RawWiredRateSlot = devSlot;
                else settings.RawWirelessRateSlot = devSlot;
                writeSettingsBlock = true;
            }
        }

        if (update.ActiveDpiStage is { } actStage && update.DpiStages == null && update.Dpi == null)
        {
            settings.DpiIndex = Math.Clamp(actStage, 0, 5);
            writeSettingsBlock = true;
        }

        if (update.SleepSeconds is { } slp)
        {
            settings.SleepMinutes = (byte)Math.Clamp((int)Math.Round(slp / 60.0), 0, 255);
            writeSettingsBlock = true;
        }

        if (update.AngleTuningDeg is { } deg)
        {
            settings.AngleTuning = unchecked((sbyte)Math.Clamp(deg, -30, 30));
            writeSettingsBlock = true;
        }

        if (update.DebounceMs is { } deb)
        {
            byte d = (byte)Math.Clamp(deb, 0, 20);
            settings.LeftDebounceMs = d;
            settings.RightDebounceMs = d;
            writeSettingsBlock = true;
        }

        if (update.PerformanceMode is { } mode)
        {
            settings.Sensor = (byte)((settings.Sensor & ~0x03) | (Math.Clamp(mode, 0, 2) & 0x03));
            writeSettingsBlock = true;
        }

        if (update.RippleControl is { } ripple)
        {
            settings.Sensor = (byte)(ripple ? (settings.Sensor | 0x04) : (settings.Sensor & ~0x04));
            writeSettingsBlock = true;
        }

        if (update.AngleSnapping is { } snap)
        {
            settings.Sensor = (byte)(snap ? (settings.Sensor | 0x08) : (settings.Sensor & ~0x08));
            writeSettingsBlock = true;
        }

        if (update.MotionSync is { } ms)
        {
            settings.Sensor = (byte)(ms ? (settings.Sensor | 0x10) : (settings.Sensor & ~0x10));
            writeSettingsBlock = true;
        }

        var lodCheck = SendMchoseV3Command(ch, 0x0009, [settings.ProfileIndex], 500);
        bool hasSeparateLodCmd = lodCheck is { Length: >= 1 } && lodCheck[0] <= 4;

        if (update.LodCode is { } lod && !hasSeparateLodCmd)
        {
            int idx = Math.Clamp(lod, 0, 2);
            settings.Sensor = (byte)((settings.Sensor & ~0x60) | ((idx << 5) & 0x60));
            writeSettingsBlock = true;
        }

        if (writeSettingsBlock)
        {
            var encoded = EncodeV3Settings(settings);
            SendMchoseV3Command(ch, 0x0102, encoded, 1150);
            Thread.Sleep(50);
        }

        if (update.LodCode is { } lod2 && hasSeparateLodCmd)
        {
            SendMchoseV3Command(ch, 0x0109, [settings.ProfileIndex, (byte)Math.Clamp(lod2, 0, 4)], 1150);
        }

        if (update.DpiStages is { Count: > 0 } newStages)
        {
            var curX = ParseV3DpiTable(SendMchoseV3Command(ch, 0x0003, [settings.ProfileIndex, 0], 1150) ?? []);
            int count = Math.Clamp(newStages.Count, 1, 6);
            int act = Math.Clamp(update.ActiveDpiStage ?? curX?.ActiveStage ?? 0, 0, count - 1);
            bool sepY = newStages.Any(s => s.X != s.Y) || (curX?.HasSeparateY == true);

            var all6X = new int[6];
            var all6Y = new int[6];
            for (int i = 0; i < 6; i++)
            {
                (int X, int Y) src = i < newStages.Count
                    ? newStages[i]
                    : (curX != null && i < curX.RawAllStages.Length ? (X: curX.RawAllStages[i], Y: curX.RawAllStages[i]) : newStages[^1]);
                all6X[i] = Math.Clamp(src.X, 200, 42000);
                all6Y[i] = Math.Clamp(src.Y, 200, 42000);
            }

            // V3 write 0x0103 swaps field order vs read: [profile, axis, hasSeparateY, count, active, ...stages]
            byte[] payloadX = EncodeV3DpiTableWrite(settings.ProfileIndex, 0, sepY, count, act, all6X);
            SendMchoseV3Command(ch, 0x0103, payloadX, 1150);
            if (sepY)
            {
                byte[] payloadY = EncodeV3DpiTableWrite(settings.ProfileIndex, 1, true, count, act, all6Y);
                SendMchoseV3Command(ch, 0x0103, payloadY, 1150);
            }
        }
        else if (update.Dpi is { } dpi)
        {
            var curX = ParseV3DpiTable(SendMchoseV3Command(ch, 0x0003, [settings.ProfileIndex, 0], 1150) ?? []);
            int stage = Math.Clamp(update.ActiveDpiStage ?? curX?.ActiveStage ?? 0, 0, 5);
            int xVal = Math.Clamp(dpi.X, 200, 42000);
            int yVal = Math.Clamp(dpi.Y, 200, 42000);
            SendMchoseV3Command(ch, 0x0104, [settings.ProfileIndex, (byte)stage, 0, (byte)(xVal & 0xFF), (byte)(xVal >> 8)], 1150);
            if (curX?.HasSeparateY == true || xVal != yVal)
                SendMchoseV3Command(ch, 0x0104, [settings.ProfileIndex, (byte)stage, 1, (byte)(yVal & 0xFF), (byte)(yVal >> 8)], 1150);
        }

        Thread.Sleep(60);
        var verified = QueryCapabilities(deviceId);
        return new DeviceTuningResult(true, "已安全写入迈从 V3 硬件（完整保留原有尾部寄存器）。", verified);
    }

    internal static byte[] BuildMchoseCommand(ushort cmd, ReadOnlySpan<byte> payload, int outputLen = 64)
    {
        int len = Math.Clamp(payload.Length, 0, 50);
        var req = new byte[Math.Max(outputLen, 64)];
        req[0] = 0x4D;
        req[1] = 0x01;
        req[2] = 0x01;
        req[3] = (byte)len;
        req[4] = (byte)(cmd & 0xFF);
        req[5] = (byte)(cmd >> 8);
        if (len > 0)
            payload[..len].CopyTo(req.AsSpan(8));
        byte xor = 0;
        for (int i = 2; i <= 7 + len; i++) xor ^= req[i];
        req[8 + len] = xor;
        return req;
    }

    private static byte[]? SendMchoseV3Command(HidChannel ch, ushort cmd, ReadOnlySpan<byte> payload, int timeoutMs)
    {
        for (int i = 0; i < 3; i++)
            if (ch.Read(5) == null) break;

        var req = BuildMchoseCommand(cmd, payload, ch.OutputLength);
        if (!ch.WriteOutput(req)) return null;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var r = ch.Read(100);
            if (r == null || r.Length < 8 || r[0] != 0x4D) continue;
            ushort replyCmd = (ushort)(r[4] | (r[5] << 8));
            if (replyCmd == 0x0000 && r[2] == 0 && r[3] == 0)
                return null; // Explicit firmware rejection
            if (replyCmd != cmd) continue;
            int len = r[3];
            const int dataStart = 8;
            if (len == 1 && r[dataStart] == 0xFF)
            {
                // Busy ("ask again")
                Thread.Sleep(35);
                ch.WriteOutput(req);
                continue;
            }
            if (len == 0) return [];
            if (dataStart + len >= r.Length) continue;
            if (r[2] == 0x01)
            {
                byte check = 0;
                for (int i = 2; i <= 7 + len; i++) check ^= r[i];
                if (r[dataStart + len] != check) continue;
            }
            return r.AsSpan(dataStart, len).ToArray();
        }
        return null;
    }

    internal sealed class MchoseV3SettingsBlock
    {
        public byte ProfileIndex { get; set; }
        public int DpiIndex { get; set; }
        public int RawWiredRateSlot { get; set; }
        public int RawWirelessRateSlot { get; set; }
        public byte SleepMinutes { get; set; }
        public byte SleepMode { get; set; }
        public byte Sensor { get; set; }
        public sbyte AngleTuning { get; set; }
        public byte LeftDebounceMs { get; set; }
        public byte RightDebounceMs { get; set; }
        public byte[] ExtraTail { get; set; } = [];
    }

    internal static MchoseV3SettingsBlock ParseV3Settings(byte[] data)
    {
        byte wired = data[1];
        byte wireless = data[2];
        return new MchoseV3SettingsBlock
        {
            ProfileIndex = data[0],
            DpiIndex = wired & 0x0F,
            RawWiredRateSlot = (wired >> 4) & 0x0F,
            RawWirelessRateSlot = (wireless >> 4) & 0x0F,
            SleepMinutes = data[3],
            SleepMode = (byte)(data[4] & 1),
            Sensor = data[5],
            AngleTuning = unchecked((sbyte)data[6]),
            LeftDebounceMs = data[7],
            RightDebounceMs = data[8],
            ExtraTail = data.Length > 9 ? data[9..] : new byte[10],
        };
    }

    internal static byte[] EncodeV3Settings(MchoseV3SettingsBlock s)
    {
        byte wired = (byte)(((s.RawWiredRateSlot & 0x0F) << 4) | (s.DpiIndex & 0x0F));
        byte wireless = (byte)(((s.RawWirelessRateSlot & 0x0F) << 4) | (s.DpiIndex & 0x0F));
        var outList = new List<byte>(9 + s.ExtraTail.Length)
        {
            s.ProfileIndex,
            wired,
            wireless,
            s.SleepMinutes,
            s.SleepMode,
            s.Sensor,
            unchecked((byte)s.AngleTuning),
            s.LeftDebounceMs,
            s.RightDebounceMs,
        };
        outList.AddRange(s.ExtraTail.Length > 0 ? s.ExtraTail : new byte[10]);
        return outList.ToArray();
    }

    internal static int RateOptionToV3DeviceSlot(int optionIdx) => optionIdx > 0 ? optionIdx + 1 : optionIdx;
    internal static int V3DeviceSlotToRateOption(int deviceSlot) => deviceSlot > 1 ? deviceSlot - 1 : deviceSlot;

    internal sealed record MchoseV3DpiParsed(int ActiveStage, bool HasSeparateY, List<int> EnabledStages, int[] RawAllStages);

    internal static MchoseV3DpiParsed? ParseV3DpiTable(byte[] p)
    {
        if (p.Length < 7) return null;
        int count = Math.Clamp((int)p[2], 1, 6);
        int active = Math.Clamp((int)p[3], 0, count - 1);
        bool sepY = p[4] == 1;
        var all6 = new int[6];
        var enabled = new List<int>(count);
        for (int i = 0; i < 6; i++)
        {
            int off = 5 + i * 2;
            if (off + 1 >= p.Length) break;
            int val = p[off] | (p[off + 1] << 8);
            all6[i] = val;
            if (i < count && val is >= 50 and <= 50000)
                enabled.Add(val);
        }
        return enabled.Count > 0 ? new MchoseV3DpiParsed(active, sepY, enabled, all6) : null;
    }

    internal static byte[] EncodeV3DpiTableWrite(byte profile, byte axis, bool hasSepY, int count, int active, int[] all6)
    {
        var list = new List<byte>(5 + 12)
        {
            profile,
            axis,
            (byte)(hasSepY ? 1 : 0),
            (byte)Math.Clamp(count, 1, 6),
            (byte)Math.Clamp(active, 0, Math.Max(0, count - 1)),
        };
        for (int i = 0; i < 6; i++)
        {
            int v = i < all6.Length && all6[i] > 0 ? all6[i] : 800;
            list.Add((byte)(v & 0xFF));
            list.Add((byte)((v >> 8) & 0xFF));
        }
        return list.ToArray();
    }

    // Compatible wrappers for existing unit tests
    internal sealed record MchoseConfigParsed(byte Profile, int ActiveStage, int? RateHz);

    internal static MchoseConfigParsed ParseConfigReply(byte[] p)
    {
        byte profile = p[0] <= 2 ? p[0] : (byte)0;
        int active = p.Length > 1 ? (p[1] & 0x0F) : 0;
        int rateCode = p.Length > 2 && p[2] > 0 ? p[2] : (p.Length > 1 ? (p[1] >> 4) & 0x0F : -1);
        int? hz = (uint)rateCode < (uint)V3DeviceRateTable.Length ? V3DeviceRateTable[rateCode] : null;
        return new MchoseConfigParsed(profile, active, hz);
    }

    internal sealed record MchoseDpiTableParsed(int ActiveStage, bool DualAxis, List<int> Values);

    internal static MchoseDpiTableParsed? ParseDpiTableReply(byte[] p)
    {
        var t = ParseV3DpiTable(p);
        return t == null ? null : new MchoseDpiTableParsed(t.ActiveStage, t.HasSeparateY, t.EnabledStages);
    }

    // =========================================================================
    //  MCHOSE Gen 2 (0x11 / 0x12 Bit-Inverted Feature Reports)
    // =========================================================================

    internal static byte[] EncodeV2FeatureCommand(byte reportId, ReadOnlySpan<byte> tokens)
    {
        var packet = new byte[64];
        packet[0] = reportId;
        int maxTokens = Math.Min(tokens.Length, 63);
        for (int i = 0; i < maxTokens; i++)
            packet[1 + i] = (byte)((tokens[i] ^ 0xFF) & 0xFF);
        return packet;
    }

    internal static (byte Command, byte[] Payload)? DecodeV2FeatureReply(byte[] raw)
    {
        if (raw.Length < 3) return null;
        byte cmd = (byte)((raw[1] ^ 0xFF) & 0xFF);
        var payload = new byte[raw.Length - 2];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)((raw[i + 2] ^ 0xFF) & 0xFF);
        return (cmd, payload);
    }

    private static byte[]? ExchangeV2Feature(RawHidFeature raw, byte reportId, ReadOnlySpan<byte> tokens, bool verifyStable = false)
    {
        var req = EncodeV2FeatureCommand(reportId, tokens);
        byte expectedCmd = tokens[0];
        byte[]? prevPayload = null;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt == 0 || !verifyStable)
            {
                if (!raw.SetFeature(req)) continue;
            }
            Thread.Sleep(attempt == 0 ? 25 : 55);
            var reply = raw.GetFeature(64, reportId);
            if (reply == null) continue;
            var decoded = DecodeV2FeatureReply(reply);
            if (decoded == null || decoded.Value.Command != expectedCmd) continue;
            if (!verifyStable) return decoded.Value.Payload;

            var cur = decoded.Value.Payload;
            if (prevPayload != null && cur.AsSpan(0, Math.Min(20, cur.Length)).SequenceEqual(prevPayload.AsSpan(0, Math.Min(20, prevPayload.Length))))
                return cur;
            prevPayload = cur;
        }
        return prevPayload;
    }

    private static (int Battery, bool Charging)? QueryV2Battery(RawHidFeature raw)
    {
        var p = ExchangeV2Feature(raw, 0x11, [0x06]);
        if (p == null || p.Length < 11) return null;
        ushort vid = (ushort)(p[0] | (p[1] << 8));
        if (vid is not (0x3837 or 0x5253)) return null;
        bool connected = ((p[8] >> 4) & 0x01) != 0;
        if (!connected) return null;
        int pct = p[9];
        if (pct > 100) return null;
        return (pct, p[10] != 0);
    }

    internal static int DecodeV2ModeFromSensor(byte sensor)
    {
        int stored = (sensor & 0xC0) >> 6;
        return stored == 0 ? 1 : stored; // 1=Performance, 2=eSports, 3=Ultra
    }

    private static DeviceTuningCapabilities QueryV2Capabilities(string deviceId, RawHidFeature raw, bool isWired, string? rawName)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        var cfg = ExchangeV2Feature(raw, 0x12, [0x67], verifyStable: true);
        if (cfg == null || cfg.Length < 20) return caps;

        caps.IsOnline = true;
        caps.OnboardProfileCount = 3;
        caps.ActiveOnboardProfile = Math.Clamp((int)cfg[0], 0, 2);
        var names = new List<string>(3);
        for (int i = 0; i < 3; i++)
        {
            var nr = ExchangeV2Feature(raw, 0x12, [0x68, (byte)i]);
            string n = $"板载配置 {i + 1}";
            if (nr is { Length: >= 2 } && nr[0] == i)
            {
                int end = Array.IndexOf(nr, (byte)0, 1);
                if (end < 0) end = nr.Length;
                string txt = System.Text.Encoding.UTF8.GetString(nr, 1, end - 1).Trim();
                if (!string.IsNullOrWhiteSpace(txt)) n = txt;
            }
            names.Add(n);
        }
        caps.OnboardProfileNames = names;

        byte wiredByte = cfg[1];
        byte wirelessByte = cfg[2];
        int activeDpiIdx = isWired ? (wiredByte & 0x0F) : (wirelessByte & 0x0F);
        int rateIdx = isWired ? ((wiredByte >> 4) & 0x0F) : ((wirelessByte >> 4) & 0x0F);

        int count = Math.Clamp((int)cfg[16], 1, 6);
        caps.SupportsDpi = true;
        caps.SupportsDpiStagesWrite = true;
        caps.MinDpi = 100;
        caps.MaxDpi = 42000;
        caps.DpiStep = 50;
        for (int i = 0; i < count; i++)
        {
            int dpi = cfg[4 + i * 2] | (cfg[4 + i * 2 + 1] << 8);
            if (dpi is >= 50 and <= 50000)
                caps.DpiStages.Add((dpi, dpi));
        }
        if (caps.DpiStages.Count > 0)
        {
            caps.ActiveDpiStage = Math.Clamp(activeDpiIdx, 0, caps.DpiStages.Count - 1);
            caps.CurrentDpiX = caps.DpiStages[caps.ActiveDpiStage].X;
            caps.CurrentDpiY = caps.CurrentDpiX;
        }

        caps.SupportedPollingRates = V2UiRateList.ToList();
        if ((uint)rateIdx < (uint)V2UiRateList.Length)
            caps.CurrentPollingRate = V2UiRateList[rateIdx];

        byte sensor = cfg[17];
        caps.SupportedPerformanceModes =
        [
            new(1, "性能模式 (Performance)", "标准低延迟均衡模式"),
            new(2, "电竞模式 (eSports)", "提高无线追踪稳定度"),
            new(3, "狂暴竞技模式 (Ultra)", "20K FPS 竞技高性能模式"),
        ];
        caps.CurrentPerformanceMode = Math.Clamp(DecodeV2ModeFromSensor(sensor), 1, 3);

        caps.SupportedLodOptions = [new(0, "0.7 mm (低)"), new(1, "1.0 mm (中)"), new(2, "2.0 mm (高)")];
        caps.CurrentLod = Math.Clamp(sensor & 0x03, 0, 2);

        caps.SupportsRippleControl = true;
        caps.RippleControl = (sensor & 0x04) != 0;
        caps.SupportsAngleSnapping = true;
        caps.AngleSnapping = (sensor & 0x08) != 0;
        caps.SupportsMotionSync = true;
        caps.MotionSync = (sensor & 0x10) != 0;

        caps.SupportsDebounce = true;
        caps.MaxDebounceMs = 20;
        caps.CurrentDebounceMs = Math.Clamp((int)cfg[18], 0, 20);

        caps.SupportsSleepTimeout = true;
        caps.MinSleepSeconds = 0;
        caps.MaxSleepSeconds = 1800;
        caps.SupportedSleepSeconds = [0, 60, 120, 180, 300, 600, 1800];
        caps.CurrentSleepSeconds = cfg[19] * 60;

        if (cfg.Length > 49)
        {
            caps.SupportsAngleTuning = true;
            caps.AngleTuningDeg = Math.Clamp((int)unchecked((sbyte)cfg[49]), -30, 30);
        }

        // Button table at offsets 20..43 (6 x 4 bytes)
        if (cfg.Length >= 44)
        {
            string[] v2BtnNames = ["左键 (Left)", "中键 (Middle)", "右键 (Right)", "前进键 (Forward)", "后退键 (Back)", "DPI 键"];
            var avail = V2ButtonActions.Select(a => a.Label).ToList();
            for (int b = 0; b < 6; b++)
            {
                int at = 20 + b * 4;
                byte type = (byte)(cfg[at] & 0x0F);
                int val = (cfg[at + 1] << 16) | (cfg[at + 2] << 8) | cfg[at + 3];
                string cur = DecodeV2Button(type, val);
                caps.ButtonBindings.Add(new ButtonBindingSlot(b, v2BtnNames[b], cur, avail, IsReadOnly: b == 0));
            }
        }

        caps.StatusMessage = $"已读取迈从 V2 硬件配置（{caps.OnboardProfileNames[caps.ActiveOnboardProfile]} · {caps.SupportedPerformanceModes.FirstOrDefault(m => m.Code == caps.CurrentPerformanceMode)?.Label}）。";
        return caps;
    }

    private static DeviceTuningResult ApplyV2Settings(string deviceId, RawHidFeature raw, bool isWired, string? rawName, DeviceTuningUpdate update)
    {
        if (update.ActiveOnboardProfile is { } profIdx)
        {
            ExchangeV2Feature(raw, 0x11, [0x58, (byte)Math.Clamp(profIdx, 0, 2)]);
            Thread.Sleep(60);
        }

        var cfg = ExchangeV2Feature(raw, 0x12, [0x67], verifyStable: true);
        if (cfg == null || cfg.Length < 44)
            return new DeviceTuningResult(false, "未能稳定读取迈从 V2 当前配置块，已取消写入以防覆盖按键与宏定义。");

        bool writeConfigBlob = false;
        var outBlob = new List<byte>(1 + cfg.Length) { 0x57 };
        outBlob.AddRange(cfg); // +1 index shift

        if (update.PollingRateHz is { } hz)
        {
            int rateIdx = Array.IndexOf(V2UiRateList, hz);
            if (rateIdx >= 0)
            {
                int wiredDpiIdx = outBlob[1 + 1] & 0x0F;
                int wiredRate = isWired ? rateIdx : ((outBlob[1 + 1] >> 4) & 0x0F);
                int wlDpiIdx = outBlob[1 + 2] & 0x0F;
                int wlRate = isWired ? ((outBlob[1 + 2] >> 4) & 0x0F) : rateIdx;
                outBlob[1 + 1] = (byte)((wiredRate << 4) | wiredDpiIdx);
                outBlob[1 + 2] = (byte)((wlRate << 4) | wlDpiIdx);
                writeConfigBlob = true;
            }
        }

        if (update.ActiveDpiStage is { } actStage)
        {
            int stg = Math.Clamp(actStage, 0, 5);
            outBlob[1 + 1] = (byte)((outBlob[1 + 1] & 0xF0) | stg);
            outBlob[1 + 2] = (byte)((outBlob[1 + 2] & 0xF0) | stg);
            writeConfigBlob = true;
        }

        if (update.DpiStages is { Count: > 0 } stages)
        {
            outBlob[1 + 16] = (byte)Math.Clamp(stages.Count, 1, 6);
            for (int i = 0; i < Math.Min(6, stages.Count); i++)
            {
                int val = Math.Clamp(stages[i].X, 100, 42000);
                outBlob[1 + 4 + i * 2] = (byte)(val & 0xFF);
                outBlob[1 + 4 + i * 2 + 1] = (byte)((val >> 8) & 0xFF);
            }
            writeConfigBlob = true;
        }
        else if (update.Dpi is { } singleDpi)
        {
            int act = (isWired ? outBlob[1 + 1] : outBlob[1 + 2]) & 0x0F;
            act = Math.Clamp(act, 0, 5);
            int val = Math.Clamp(singleDpi.X, 100, 42000);
            outBlob[1 + 4 + act * 2] = (byte)(val & 0xFF);
            outBlob[1 + 4 + act * 2 + 1] = (byte)((val >> 8) & 0xFF);
            writeConfigBlob = true;
        }

        if (update.DebounceMs is { } deb)
        {
            outBlob[1 + 18] = (byte)Math.Clamp(deb, 0, 20);
            writeConfigBlob = true;
        }

        if (writeConfigBlob)
        {
            ExchangeV2Feature(raw, 0x12, outBlob.Take(63).ToArray());
            Thread.Sleep(55);
        }

        if (update.LodCode != null || update.PerformanceMode != null || update.MotionSync != null ||
            update.AngleSnapping != null || update.RippleControl != null || update.AngleTuningDeg != null)
        {
            byte sensor = cfg[17];
            byte lodIdx = (byte)Math.Clamp(update.LodCode ?? (sensor & 0x03), 0, 3);
            byte ripple = (byte)(update.RippleControl is { } rc ? (rc ? 1 : 2) : 0);
            byte snap = (byte)(update.AngleSnapping is { } sn ? (sn ? 1 : 2) : 0);
            byte ms = (byte)(update.MotionSync is { } m ? (m ? 1 : 2) : 0);
            byte mode = (byte)(update.PerformanceMode is { } pm ? Math.Clamp(pm, 1, 3) : 0);
            byte rotOpen = (byte)(update.AngleTuningDeg != null ? 1 : 0);
            byte rotVal = update.AngleTuningDeg is { } dg ? unchecked((byte)(sbyte)Math.Clamp(dg, -30, 30)) : (byte)0;

            ExchangeV2Feature(raw, 0x11, [0x42, lodIdx, ripple, snap, ms, 0, 0, mode, rotOpen, rotVal]);
            Thread.Sleep(55);
        }

        if (update.SleepSeconds is { } sleep)
        {
            int mins = Math.Clamp((int)Math.Round(sleep / 60.0), 0, 255);
            ExchangeV2Feature(raw, 0x11, [0x0A, (byte)(mins > 0 ? 1 : 0), (byte)mins]);
            Thread.Sleep(150);
        }

        if (update.ButtonBinding is { } bind && bind.SlotIndex is >= 1 and <= 5)
        {
            if (EncodeV2Button(bind.Action) is { } encoded)
            {
                ExchangeV2Feature(raw, 0x12,
                [
                    0x52,
                    (byte)bind.SlotIndex,
                    0x00,
                    (byte)(encoded.Type & 0x0F),
                    (byte)((encoded.Value >> 16) & 0xFF),
                    (byte)((encoded.Value >> 8) & 0xFF),
                    (byte)(encoded.Value & 0xFF),
                ]);
            }
        }

        Thread.Sleep(55);
        var updated = QueryV2Capabilities(deviceId, raw, isWired, rawName);
        return new DeviceTuningResult(true, "已安全写入迈从 V2 硬件并回读确认。", updated);
    }

    private static string DecodeV2Button(byte type, int val)
    {
        if (type == 0) return "默认功能";
        if (type == 4) return "板载宏";
        foreach (var (label, t, v) in V2ButtonActions)
            if (t == type && v == val) return label;
        if (type == 2)
        {
            byte mod = (byte)((val >> 16) & 0xFF);
            byte usage = (byte)((val >> 8) & 0xFF);
            return HidKeyCatalog.FormatKeyboardBinding(mod, usage);
        }
        return $"自定义 ({type:X1}:{val:X6})";
    }

    private static (byte Type, int Value)? EncodeV2Button(string action)
    {
        if (action == "默认功能") return (0, 0);
        foreach (var (label, t, v) in V2ButtonActions)
            if (label == action) return (t, v);
        if (HidKeyCatalog.TryParseKeyboardBinding(action, out byte mod, out byte usage))
            return (2, (mod << 16) | (usage << 8));
        return null;
    }

    private static bool IsMchoseDevice(HidDevice d)
    {
        if (d.VendorID == 0x3837) return true;
        if (d.VendorID == 0x5253 && d.ProductID is 0x1011 or 0x1020) return true;
        string name = HidSafe.ProductName(d);
        return name.Contains("MCHOSE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVidPid(string deviceId, out ushort vid, out ushort pid)
    {
        vid = 0x3837; pid = 0;
        var parts = deviceId.Split(':');
        if (parts.Length < 2) return false;
        if (parts[1].Contains('-'))
        {
            var vp = parts[1].Split('-');
            return vp.Length == 2
                && ushort.TryParse(vp[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid)
                && ushort.TryParse(vp[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
        }
        return ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
    }

    private static DeviceKind InferKind(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("k99") || lower.Contains("g75") || lower.Contains("g87") || lower.Contains("jet") ||
            lower.Contains("ace") || lower.Contains("keyboard"))
            return DeviceKind.Keyboard;
        if (lower.Contains("v9") || lower.Contains("g9") || lower.Contains("s9") || lower.Contains("headset"))
            return DeviceKind.Headset;
        return DeviceKind.Mouse;
    }

    private static string? CleanName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (var suffix in new[] { " Dongle", " Receiver", " RECEIVER", " 8K RECEIVER", " 4K RECEIVER" })
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                s = s[..^suffix.Length];
        return s.Trim();
    }
}
