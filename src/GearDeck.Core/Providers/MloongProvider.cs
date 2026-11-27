using System.Diagnostics;
using System.Globalization;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 迈龙 MLOONG MX301 系列：
/// - MX301 PRO / PRO V2 / MX301 / MX300 / MX302 (0xA8A4:0x2255 有线 / 0xA8A5:0x2255 2.4G)
///   使用 64 字节 ReportId 0x00 私有协议 (UsagePage 0xFF01 / Usage 0x10)，
///   兼容 65 字节报表（首字节 0 为 ReportId，读取时剥离）。
/// - MX301 PRO X (0xA8A4:0xFB36 有线 / 0xA8A5:0xFB35 2.4G，UsagePage 0xFF02 / Usage 0x02)
///   走通用 Compx08Protocol (0x08 / 0x55 校验) 读写。
/// 未读到设备时不返回任何推算电量（BatteryPercent 保持 null）。
/// </summary>
public sealed class MloongProvider : ITunablePeripheralProvider
{
    public const string ProviderName = "MLOONG";
    public string Name => ProviderName;

    private const ushort VidWired = 0xA8A4;
    private const ushort VidWireless = 0xA8A5;
    private const ushort PidMx301Native = 0x2255;
    private const ushort PidMx301ProXWired = 0xFB36;
    private const ushort PidMx301ProXWireless = 0xFB35;
    private const byte CompxReportId = 0x08;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("mloong:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevices()
            .Where(IsMloongDevice)
            .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID))
            .ToList();

        if (OperatingSystem.IsMacOS())
        {
            foreach (var g in MacIOKitHid.Enumerate()
                         .Where(IsMloongMacInterface)
                         .GroupBy(d => (d.VendorId, d.ProductId)))
            {
                try
                {
                    var (vid, pid) = g.Key;
                    bool compxFamily = IsCompxPid(pid);
                    bool wireless = vid == VidWireless;
                    string? rawName = g.Select(x => x.Product).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                    string cleanName = CleanName(rawName) ?? FallbackName(pid, wireless);

                    (byte Level, bool Charging)? hit = null;
                    foreach (var iface in SelectMacInterfaces(g, compxFamily))
                    {
                        using var conn = MacIOKitHid.MacHidConnection.TryOpen(iface, out _);
                        if (conn == null) continue;
                        if (!compxFamily)
                        {
                            var reply = ExchangeMx301Mac(conn, BuildMx301BatteryQuery(), 650);
                            if (ParseMx301BatteryReply(reply, out byte level, out bool charging))
                            {
                                hit = (level, charging);
                                break;
                            }
                        }
                    }

                    if (hit == null && compxFamily)
                    {
                        var hidGroup = groups.FirstOrDefault(x => x.Key == (vid, pid));
                        if (hidGroup != null)
                        {
                            foreach (var iface in SelectInterfaces(hidGroup, compxFamily: true))
                            {
                                using var ch = HidChannel.TryOpen(iface);
                                if (ch == null) continue;
                                hit = Compx08Protocol.QueryBattery(ch, CompxReportId, 650);
                                if (hit != null) break;
                            }
                        }
                    }

                    results.Add(new PeripheralDevice
                    {
                        Id = $"mloong:{vid:X4}-{pid:X4}",
                        VendorId = vid,
                        ProductId = pid,
                        Name = cleanName,
                        Kind = DeviceKind.Mouse,
                        Provider = ProviderName,
                        Connected = true,
                        IsSleeping = hit == null && wireless,
                        Transport = wireless ? "2.4G" : "USB",
                        BatteryPercent = hit?.Level,
                        State = hit is { Charging: true } ? BatteryState.Charging
                              : hit == null ? BatteryState.Unknown : BatteryState.Normal,
                        Note = hit == null
                            ? (wireless ? "MLOONG 接收器已连接（鼠标休眠中，晃动唤醒）" : "未读到真实电量，不显示估算值")
                            : null,
                        LastUpdated = DateTimeOffset.Now,
                    });
                }
                catch (Exception ex)
                {
                    Diag.Log($"MLOONG(mac) {g.Key.Item1:X4}:{g.Key.Item2:X4}: {ex.Message}");
                }
            }
            return results;
        }

        foreach (var g in groups)
        {
            try
            {
                var (vid, pid) = g.Key;
                bool compxFamily = IsCompxPid(pid);
                bool wireless = vid == VidWireless;
                string? rawName = g.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                string cleanName = CleanName(rawName) ?? FallbackName(pid, wireless);

                (byte Level, bool Charging)? hit = null;
                foreach (var iface in SelectInterfaces(g, compxFamily))
                {
                    using var ch = HidChannel.TryOpen(iface);
                    if (ch == null) continue;

                    if (compxFamily)
                    {
                        hit = Compx08Protocol.QueryBattery(ch, CompxReportId, 650);
                    }
                    else
                    {
                        var reply = ExchangeMx301(ch, BuildMx301BatteryQuery(), 650);
                        if (ParseMx301BatteryReply(reply, out byte level, out bool charging))
                            hit = (level, charging);
                    }
                    if (hit != null) break;
                }

                results.Add(new PeripheralDevice
                {
                    Id = $"mloong:{vid:X4}-{pid:X4}",
                    VendorId = vid,
                    ProductId = pid,
                    Name = cleanName,
                    Kind = DeviceKind.Mouse,
                    Provider = ProviderName,
                    Connected = true,
                    IsSleeping = hit == null && wireless,
                    Transport = wireless ? "2.4G" : "USB",
                    BatteryPercent = hit?.Level,
                    State = hit is { Charging: true } ? BatteryState.Charging
                          : hit == null ? BatteryState.Unknown : BatteryState.Normal,
                    Note = hit == null
                        ? (wireless ? "MLOONG 接收器已连接（鼠标休眠中，晃动唤醒）" : "未读到真实电量，不显示估算值")
                        : null,
                    LastUpdated = DateTimeOffset.Now,
                });
            }
            catch (Exception ex)
            {
                Diag.Log($"MLOONG {g.Key.Item1:X4}:{g.Key.Item2:X4}: {ex.Message}");
            }
        }
        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
        {
            caps.StatusMessage = "无效的 MLOONG 设备 ID。";
            return caps;
        }

        bool compxFamily = IsCompxPid(pid);
        if (OperatingSystem.IsMacOS() && !compxFamily)
        {
            var macIfaces = MacIOKitHid.Enumerate(vid, pid);
            if (macIfaces.Count == 0)
            {
                caps.StatusMessage = "未找到 MLOONG 设备控制接口。";
                return caps;
            }
            foreach (var iface in SelectMacInterfaces(macIfaces, compxFamily: false))
            {
                using var conn = MacIOKitHid.MacHidConnection.TryOpen(iface, out _);
                if (conn == null) continue;
                var macCfg = ReadNativeConfigMac(conn, out _);
                if (macCfg != null)
                    return BuildNativeCapabilities(deviceId, macCfg);
            }
            caps.StatusMessage = "MLOONG 鼠标未响应 MX301 配置读取，请唤醒后重试。";
            return caps;
        }

        var list = HidSafe.EnumerateDevices(vid, pid);
        if (list.Count == 0)
        {
            caps.StatusMessage = "未找到 MLOONG 设备控制接口。";
            return caps;
        }

        string? rawName = list.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        bool wireless = vid == VidWireless;

        if (compxFamily)
        {
            foreach (var iface in SelectInterfaces(list, compxFamily: true))
            {
                using var ch = HidChannel.TryOpen(iface);
                if (ch == null) continue;
                var res = Compx08Protocol.QueryCapabilities(deviceId, ch, CompxReportId, wireless, rawName, vid, pid);
                if (res.IsOnline && (res.SupportsDpi || res.SupportedPollingRates.Count > 0))
                    return res;
            }
            caps.StatusMessage = "MLOONG MX301 PRO X 未响应 0x08 控制协议（可能休眠）。";
            return caps;
        }

        foreach (var iface in SelectInterfaces(list, compxFamily: false))
        {
            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;
            var cfg = ReadNativeConfig(ch, out _);
            if (cfg == null) continue;
            return BuildNativeCapabilities(deviceId, cfg);
        }

        caps.StatusMessage = "MLOONG 鼠标未响应 MX301 配置读取，请唤醒后重试。";
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new DeviceTuningResult(false, "无效的 MLOONG 设备 ID。");

        var list = HidSafe.EnumerateDevices(vid, pid);
        bool compxFamily = IsCompxPid(pid);
        string? rawName = list.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        bool wireless = vid == VidWireless;

        if (compxFamily)
        {
            foreach (var iface in SelectInterfaces(list, compxFamily: true))
            {
                using var ch = HidChannel.TryOpen(iface);
                if (ch == null) continue;
                var res = Compx08Protocol.ApplySettings(deviceId, ch, update, CompxReportId, wireless, rawName, vid, pid);
                if (res.Success) return res;
            }
            return new DeviceTuningResult(false, "MLOONG MX301 PRO X 未响应写入（可能休眠）。");
        }

        if (OperatingSystem.IsMacOS() && !compxFamily)
        {
            var macIfaces = MacIOKitHid.Enumerate(vid, pid);
            foreach (var iface in SelectMacInterfaces(macIfaces, compxFamily: false))
            {
                using var conn = MacIOKitHid.MacHidConnection.TryOpen(iface, out _);
                if (conn == null) continue;

                // 必须先读到当前配置再构造写入包，禁止用全零缓存覆盖未知字节。
                var macCfg = ReadNativeConfigMac(conn, out byte[]? current);
                if (macCfg == null || current == null) continue;
                ApplyUpdate(macCfg, update);

                var packet = BuildMx301ConfigWrite(current, macCfg);
                if (!conn.WriteOutputReport(0, packet)) continue;
                Thread.Sleep(50);

                var verified = ReadNativeConfigMac(conn, out _);
                if (verified == null || !ConfigMatches(macCfg, verified))
                    return new DeviceTuningResult(false, "MLOONG 写入后回读校验失败，未确认配置已保存。");

                return new DeviceTuningResult(true, "已写入 MLOONG 硬件并回读校验。", BuildNativeCapabilities(deviceId, verified));
            }
        }

        foreach (var iface in SelectInterfaces(list, compxFamily: false))
        {
            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;

            // 必须先读到当前配置再构造写入包，禁止用全零缓存覆盖未知字节。
            var cfg = ReadNativeConfig(ch, out byte[]? current);
            if (cfg == null || current == null) continue;
            ApplyUpdate(cfg, update);

            var packet = BuildMx301ConfigWrite(current, cfg);
            if (!WriteMx301(ch, packet)) continue;
            Thread.Sleep(40);

            var verified = ReadNativeConfig(ch, out _);
            if (verified == null || !ConfigMatches(cfg, verified))
                return new DeviceTuningResult(false, "MLOONG 写入后回读校验失败，未确认配置已保存。");

            return new DeviceTuningResult(true, "已写入 MLOONG 硬件并回读校验。", BuildNativeCapabilities(deviceId, verified));
        }

        return new DeviceTuningResult(false, "MLOONG 鼠标未响应 MX301 写入指令（可能休眠）。");
    }

    // =========================================================================
    //  MX301 64B 私有协议（internal 供单测）
    // =========================================================================

    internal static byte[] BuildMx301BatteryQuery()
    {
        var req = new byte[64];
        req[0] = 0x55; req[1] = 0x30; req[2] = 0xA5; req[3] = 0x0B;
        req[4] = 0x2E; req[5] = 0x01; req[6] = 0x01; req[7] = 0x01;
        return req;
    }

    internal static bool ParseMx301BatteryReply(byte[]? reply, out byte level, out bool charging)
    {
        level = 0;
        charging = false;
        if (reply == null || reply.Length < 10) return false;
        if (reply[0] is not (0x55 or 0xAA) || reply[1] != 0x30) return false;
        if (reply[8] > 100) return false;

        level = reply[8];
        charging = reply[9] != 0;
        return true;
    }

    internal static (byte Level, bool Charging)? ParseMx301BatteryReply(byte[]? reply)
        => ParseMx301BatteryReply(reply, out byte level, out bool charging) ? (level, charging) : null;

    internal static byte[] BuildMx301ConfigQuery()
    {
        var req = new byte[64];
        req[0] = 0x55; req[1] = 0x0E; req[2] = 0xA5; req[3] = 0x0B;
        req[4] = 0x30; req[5] = 0x01; req[6] = 0x01; req[7] = 0x01;
        return req;
    }

    internal static Mx301Config? ParseMx301ConfigReply(byte[]? reply)
    {
        if (reply == null || reply.Length < 55) return null;
        if (reply[0] is not (0x55 or 0xAA) || reply[1] != 0x0E) return null;

        int rateCode = reply[10];
        int dpiCount = reply[11];
        if (rateCode is < 1 or > 4 || dpiCount is < 1 or > 6) return null;

        var cfg = new Mx301Config
        {
            LightMode = reply[9],
            RateCode = (byte)rateCode,
            DpiCount = (byte)dpiCount,
            ActiveStage = Math.Clamp(reply[12] - 1, 0, dpiCount - 1),
            ScrollReverse = reply[48] != 0,
            Lod = reply[49] is 1 or 2 ? reply[49] : (byte)1,
            SensorFlag = reply[50],
            DebounceMs = reply[51],
            SleepMin = reply[52],
            HighSpeed = reply[53] != 0,
            WakeupRaw = reply[54],
        };
        for (int i = 0; i < 6; i++)
            cfg.DpiStages[i] = reply[13 + i * 2] | (reply[14 + i * 2] << 8);
        return cfg;
    }

    /// <summary>基于当前 64B 配置回包替换命令头并写回已变更的配置字节，其余字节原样保留。</summary>
    internal static byte[] BuildMx301ConfigWrite(byte[] currentReply, Mx301Config config)
    {
        if (currentReply == null) throw new ArgumentNullException(nameof(currentReply));
        if (config == null) throw new ArgumentNullException(nameof(config));

        var source = NormalizeMx301Reply(currentReply) ?? currentReply;
        var packet = new byte[64];
        Array.Copy(source, packet, Math.Min(64, source.Length));

        packet[0] = 0x55; packet[1] = 0x0F; packet[2] = 0xAE; packet[3] = 0x0A;
        packet[4] = 0x2F; packet[5] = 0x01; packet[6] = 0x01; packet[7] = 0x01; packet[8] = 0x00;

        packet[9] = config.LightMode;
        packet[10] = config.RateCode;
        packet[11] = config.DpiCount;
        packet[12] = (byte)Math.Clamp(config.ActiveStage + 1, 1, Math.Max(1, (int)config.DpiCount));
        for (int i = 0; i < 6; i++)
        {
            int dpi = Math.Clamp(config.DpiStages[i], 0, 0xFFFF);
            packet[13 + i * 2] = (byte)(dpi & 0xFF);
            packet[14 + i * 2] = (byte)((dpi >> 8) & 0xFF);
        }
        packet[48] = (byte)(config.ScrollReverse ? 1 : 0);
        packet[49] = config.Lod is 1 or 2 ? config.Lod : (byte)1;
        packet[50] = config.SensorFlag;
        packet[51] = config.DebounceMs;
        packet[52] = config.SleepMin;
        packet[53] = (byte)(config.HighSpeed ? 1 : 0);
        packet[54] = config.WakeupRaw;
        return packet;
    }

    /// <summary>65B 报表首字节为 ReportId 0，剥离后才是 64B 有效载荷。</summary>
    internal static byte[]? NormalizeMx301Reply(byte[]? raw)
    {
        if (raw == null) return null;
        return raw.Length == 65 && raw[0] == 0 ? raw.AsSpan(1).ToArray() : raw;
    }

    internal static int? Mx301RateCodeToHz(int code) => code switch
    {
        1 => 125,
        2 => 250,
        3 => 500,
        4 => 1000,
        _ => null,
    };

    internal static byte? Mx301HzToRateCode(int hz) => hz switch
    {
        125 => 1,
        250 => 2,
        500 => 3,
        1000 => 4,
        _ => null,
    };

    internal sealed class Mx301Config
    {
        public byte LightMode { get; set; }
        public byte RateCode { get; set; } = 1;
        public byte DpiCount { get; set; } = 1;
        public int ActiveStage { get; set; }
        public int[] DpiStages { get; set; } = new int[6];
        public bool ScrollReverse { get; set; }
        public byte Lod { get; set; } = 1;
        public byte SensorFlag { get; set; }
        public byte DebounceMs { get; set; }
        public byte SleepMin { get; set; }
        public bool HighSpeed { get; set; }
        public byte WakeupRaw { get; set; }
        public bool WakeupFlag
        {
            get => WakeupRaw != 0;
            set => WakeupRaw = value ? (byte)1 : (byte)0;
        }
    }

    // =========================================================================
    //  传输辅助
    // =========================================================================

    private static Mx301Config? ReadNativeConfig(HidChannel ch, out byte[]? rawReply)
    {
        rawReply = ExchangeMx301(ch, BuildMx301ConfigQuery(), 900);
        return rawReply != null ? ParseMx301ConfigReply(rawReply) : null;
    }

    private static Mx301Config? ReadNativeConfigMac(MacIOKitHid.MacHidConnection conn, out byte[]? rawReply)
    {
        rawReply = ExchangeMx301Mac(conn, BuildMx301ConfigQuery(), 900);
        return rawReply != null ? ParseMx301ConfigReply(rawReply) : null;
    }

    private static byte[]? ExchangeMx301Mac(MacIOKitHid.MacHidConnection conn, byte[] request, int timeoutMs)
    {
        byte cmd = request[1];
        var reply = conn.ExchangeOutput(0, request, timeoutMs,
            r => r != null && r.Length >= 10 && r[0] is 0x55 or 0xAA && r[1] == cmd);
        return NormalizeMx301Reply(reply);
    }

    private static byte[]? ExchangeMx301(HidChannel ch, byte[] request, int timeoutMs)
    {
        for (int i = 0; i < 3; i++)
            if (ch.Read(5) == null) break;

        byte cmd = request[1];
        bool valid(byte[]? r) => r != null && r.Length >= 10 && r[0] is 0x55 or 0xAA && r[1] == cmd;

        if (ch.OutputLength >= 64)
        {
            if (!ch.WriteOutput(ch.OutputLength == 65 ? PrependZero(request) : request)) return null;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var reply = NormalizeMx301Reply(ch.Read(100));
                if (valid(reply)) return reply;
            }
        }

        if (ch.FeatureLength >= 64)
        {
            if (!ch.WriteFeature(ch.FeatureLength == 65 ? PrependZero(request) : request)) return null;
            Thread.Sleep(60);
            var reply = NormalizeMx301Reply(ch.ReadFeature(0));
            if (valid(reply)) return reply;
        }
        return null;
    }

    private static bool WriteMx301(HidChannel ch, byte[] packet)
    {
        if (ch.OutputLength >= 64)
            return ch.WriteOutput(ch.OutputLength == 65 ? PrependZero(packet) : packet);
        if (ch.FeatureLength >= 64)
            return ch.WriteFeature(ch.FeatureLength == 65 ? PrependZero(packet) : packet);
        return false;
    }

    private static byte[] PrependZero(byte[] packet)
    {
        var buf = new byte[Math.Max(65, packet.Length + 1)];
        Array.Copy(packet, 0, buf, 1, packet.Length);
        return buf;
    }

    // =========================================================================
    //  能力映射
    // =========================================================================

    private static DeviceTuningCapabilities BuildNativeCapabilities(string deviceId, Mx301Config cfg)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId, IsOnline = true };

        caps.SupportsDpi = true;
        caps.SupportsDpiStagesWrite = true;
        caps.MinDpi = 100;
        caps.MaxDpi = 20000;
        caps.DpiStep = 50;
        caps.SupportsIndependentXY = false;
        for (int i = 0; i < cfg.DpiCount && i < cfg.DpiStages.Length; i++)
        {
            int dpi = Math.Clamp(cfg.DpiStages[i] > 0 ? cfg.DpiStages[i] : 100, 100, 20000);
            caps.DpiStages.Add((dpi, dpi));
        }
        caps.ActiveDpiStage = Math.Clamp(cfg.ActiveStage, 0, Math.Max(0, caps.DpiStages.Count - 1));
        if (caps.DpiStages.Count > 0)
        {
            caps.CurrentDpiX = caps.DpiStages[caps.ActiveDpiStage].X;
            caps.CurrentDpiY = caps.DpiStages[caps.ActiveDpiStage].Y;
        }

        caps.SupportedPollingRates = [125, 250, 500, 1000];
        caps.CurrentPollingRate = Mx301RateCodeToHz(cfg.RateCode) ?? 0;

        caps.SupportedLodOptions = [new(1, "1.0 mm (低)"), new(2, "2.0 mm (高)")];
        caps.CurrentLod = cfg.Lod is 1 or 2 ? cfg.Lod : 1;

        caps.SupportsAngleSnapping = true;
        caps.AngleSnapping = (cfg.SensorFlag & 0x01) != 0;
        caps.SupportsRippleControl = true;
        caps.RippleControl = (cfg.SensorFlag & 0x10) != 0;
        caps.SupportsMotionSync = true;
        caps.MotionSync = (cfg.SensorFlag & 0x20) != 0;

        caps.SupportsDebounce = true;
        caps.MaxDebounceMs = 20;
        caps.CurrentDebounceMs = Math.Clamp((int)cfg.DebounceMs, 0, 20);

        caps.SupportsSleepTimeout = true;
        caps.MinSleepSeconds = 60;
        caps.MaxSleepSeconds = 255 * 60;
        caps.SupportedSleepSeconds = [60, 120, 180, 300, 600, 1800];
        caps.CurrentSleepSeconds = cfg.SleepMin * 60;

        caps.SupportedPerformanceModes = [new(0, "标准模式", "常规续航"), new(1, "高速模式", "增强追踪响应")];
        caps.CurrentPerformanceMode = cfg.HighSpeed ? 1 : 0;

        caps.StatusMessage = "已读取 MX301 硬件配置（DPI 档位 / 回报率 / LOD / 传感器选项）。";
        return caps;
    }

    private static void ApplyUpdate(Mx301Config cfg, DeviceTuningUpdate update)
    {
        if (update.PollingRateHz is { } hz && Mx301HzToRateCode(hz) is { } rateCode)
            cfg.RateCode = rateCode;

        if (update.ActiveDpiStage is { } active)
            cfg.ActiveStage = Math.Clamp(active, 0, Math.Max(0, cfg.DpiCount - 1));

        if (update.DpiStages is { Count: > 0 } stages)
        {
            int count = Math.Clamp(stages.Count, 1, 6);
            cfg.DpiCount = (byte)count;
            for (int i = 0; i < 6; i++)
            {
                var src = i < count ? stages[i] : stages[^1];
                cfg.DpiStages[i] = Math.Clamp(src.X, 100, 20000);
            }
        }
        else if (update.Dpi is { } dpi)
        {
            int idx = Math.Clamp(update.ActiveDpiStage ?? cfg.ActiveStage, 0, Math.Max(0, cfg.DpiCount - 1));
            cfg.DpiStages[idx] = Math.Clamp(dpi.X, 100, 20000);
        }

        if (update.LodCode is { } lod)
            cfg.Lod = (byte)Math.Clamp(lod, 1, 2);

        if (update.AngleSnapping is { } snap)
            cfg.SensorFlag = (byte)(snap ? cfg.SensorFlag | 0x01 : cfg.SensorFlag & ~0x01);
        if (update.RippleControl is { } ripple)
            cfg.SensorFlag = (byte)(ripple ? cfg.SensorFlag | 0x10 : cfg.SensorFlag & ~0x10);
        if (update.MotionSync is { } motionSync)
            cfg.SensorFlag = (byte)(motionSync ? cfg.SensorFlag | 0x20 : cfg.SensorFlag & ~0x20);

        if (update.DebounceMs is { } debounce)
            cfg.DebounceMs = (byte)Math.Clamp(debounce, 0, 20);
        if (update.SleepSeconds is { } sleep)
            cfg.SleepMin = (byte)Math.Clamp((int)Math.Round(sleep / 60.0), 0, 255);
        if (update.PerformanceMode is { } perf)
            cfg.HighSpeed = perf > 0;
    }

    private static bool ConfigMatches(Mx301Config expected, Mx301Config actual)
        => expected.RateCode == actual.RateCode
        && expected.DpiCount == actual.DpiCount
        && expected.ActiveStage == actual.ActiveStage
        && expected.DpiStages.AsSpan(0, expected.DpiCount).SequenceEqual(actual.DpiStages.AsSpan(0, actual.DpiCount))
        && expected.Lod == actual.Lod
        && expected.SensorFlag == actual.SensorFlag
        && expected.DebounceMs == actual.DebounceMs
        && expected.SleepMin == actual.SleepMin
        && expected.HighSpeed == actual.HighSpeed;

    // =========================================================================
    //  设备识别
    // =========================================================================

    private static bool IsMloongDevice(HidDevice d)
    {
        if ((d.VendorID is VidWired or VidWireless) &&
            d.ProductID is PidMx301Native or PidMx301ProXWired or PidMx301ProXWireless)
            return true;
        return HidSafe.ProductName(d).Contains("MLOONG", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMloongMacInterface(MacHidInterfaceInfo d)
    {
        if ((d.VendorId is VidWired or VidWireless) &&
            d.ProductId is PidMx301Native or PidMx301ProXWired or PidMx301ProXWireless)
            return true;
        return d.Product.Contains("MLOONG", StringComparison.OrdinalIgnoreCase);
    }

    private static List<MacHidInterfaceInfo> SelectMacInterfaces(IEnumerable<MacHidInterfaceInfo> group, bool compxFamily)
    {
        int minLen = compxFamily ? 17 : 64;
        var candidates = group
            .Where(d => d.MaxOutputReportSize >= minLen || d.MaxFeatureReportSize >= minLen)
            .ToList();
        var exact = candidates
            .Where(d => compxFamily ? d.PrimaryUsagePage == 0xFF02 && d.PrimaryUsage == 0x02 : d.PrimaryUsagePage == 0xFF01 && d.PrimaryUsage == 0x10)
            .OrderByDescending(d => d.MaxOutputReportSize)
            .ToList();
        return exact.Count > 0 ? exact : candidates.OrderByDescending(d => d.MaxOutputReportSize).ToList();
    }

    private static bool IsCompxPid(ushort pid) => pid is PidMx301ProXWired or PidMx301ProXWireless;

    private static List<HidDevice> SelectInterfaces(IEnumerable<HidDevice> group, bool compxFamily)
    {
        int minLen = compxFamily ? 17 : 64;
        var candidates = group
            .Where(d => HidSafe.MaxOutputLen(d) >= minLen || HidSafe.MaxFeatureLen(d) >= minLen)
            .ToList();
        var exact = candidates
            .Where(d =>
            {
                var (page, usage) = HidSafe.PrimaryUsage(d);
                return compxFamily ? page == 0xFF02 && usage == 0x02 : page == 0xFF01 && usage == 0x10;
            })
            .OrderByDescending(HidSafe.MaxOutputLen)
            .ToList();
        return exact.Count > 0 ? exact : candidates.OrderByDescending(HidSafe.MaxOutputLen).ToList();
    }

    private static string FallbackName(ushort pid, bool wireless)
    {
        string model = pid switch
        {
            PidMx301ProXWired or PidMx301ProXWireless => "MX301 PRO X",
            _ => "MX301 PRO",
        };
        return $"MLOONG {model}{(wireless ? " (2.4G)" : "")}";
    }

    private static string? CleanName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (var suffix in new[] { " Dongle", " Receiver", " RECEIVER", " 8K RECEIVER", " 4K RECEIVER" })
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                s = s[..^suffix.Length];
        return s.Trim();
    }

    private static bool TryParseVidPid(string deviceId, out ushort vid, out ushort pid)
        => ProviderIdHelper.TryParseVidPid(deviceId, out vid, out pid, VidWired);
}
