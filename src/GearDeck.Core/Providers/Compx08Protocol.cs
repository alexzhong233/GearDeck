using System.Diagnostics;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 通用的 17 字节 0x08 协议（0x55 校验和，广泛用于 ATK / VXE / VGN / Lamzu Atlantis / 部分狼蛛与迈从）。
/// 支持 CID/MID 识别、Ultra 10-42000 步进编码、4 组板载配置切换 (0x0E/0x0F)、8K 回报率、
/// 高级寄存器行 (0x00A9 消抖/移动同步/休眠/直线修正/波纹修正) 与传感器竞技模式 (0x00B5 Shard/MAX)。
/// </summary>
internal static class Compx08Protocol
{
    public sealed class ParsedConfig
    {
        public int PollingRateHz { get; set; }
        public int ActiveStageIndex { get; set; }
        public int StageCount { get; set; } = 4;
        public List<(int X, int Y)> Stages { get; set; } = [];
        public int? LodLevel { get; set; }
        public int? SleepMinutes { get; set; }
    }

    public static byte[] BuildFrame(byte reportId, byte cmd, ReadOnlySpan<byte> payload14)
    {
        var packet = new byte[17];
        packet[0] = reportId;
        packet[1] = cmd;
        if (!payload14.IsEmpty)
            payload14[..Math.Min(payload14.Length, 14)].CopyTo(packet.AsSpan(2));
        int sum = 0;
        for (int i = 0; i < 16; i++) sum += packet[i];
        packet[16] = (byte)((0x55 - (sum & 0xFF)) & 0xFF);
        return packet;
    }

    private static byte[] BuildOutBuffer(HidChannel ch, byte[] pkt)
    {
        if (ch.OutputLength > 0 && ch.OutputLength != pkt.Length)
        {
            var buf = new byte[ch.OutputLength];
            Array.Copy(pkt, buf, Math.Min(pkt.Length, buf.Length));
            return buf;
        }
        return pkt;
    }

    public static byte[]? SendSimpleCommand(HidChannel ch, byte cmd, ReadOnlySpan<byte> payload, byte reportId = 0x08, int timeoutMs = 450)
    {
        var pkt = BuildFrame(reportId, cmd, payload);
        var outBuf = BuildOutBuffer(ch, pkt);
        var padded65 = new byte[Math.Max(65, pkt.Length)];
        Array.Copy(pkt, padded65, pkt.Length);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            for (int d = 0; d < 3; d++)
                if (ch.Read(5) == null) break;

            bool sent = ch.OutputLength > 0 && (ch.WriteOutput(outBuf) || ch.WriteOutput(padded65) || ch.WriteOutput(pkt));
            if (sent)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    var r = ch.Read(70);
                    if (TryExtractSimplePayload(r, reportId, cmd, out var data))
                        return data;
                }
            }

            if (ch.FeatureLength >= 17 && ch.WriteFeature(pkt))
            {
                Thread.Sleep(45);
                var fr = ch.ReadFeature(reportId);
                if (TryExtractSimplePayload(fr, reportId, cmd, out var data))
                    return data;
            }
        }
        return null;
    }

    private static bool TryExtractSimplePayload(byte[]? buf, byte reportId, byte cmd, out byte[] data)
    {
        data = [];
        if (buf == null || buf.Length < 6) return false;
        ReadOnlySpan<byte> f;
        if (buf[0] == cmd)
            f = buf;
        else if (buf[0] == reportId && buf[1] == cmd)
            f = buf.AsSpan(1);
        else
            return false;

        if (f.Length < 6) return false;
        int len = Math.Clamp((int)f[4], 0, Math.Min(10, f.Length - 5));
        data = f.Slice(5, len).ToArray();
        return true;
    }

    public static (byte Cid, byte Mid)? QueryCidMid(HidChannel ch, byte reportId = 0x08, int timeoutMs = 350)
    {
        var data = SendSimpleCommand(ch, 0x10, default, reportId, timeoutMs);
        if (data is { Length: >= 2 } && (data[0] != 0 || data[1] != 0))
            return (data[0], data[1]);
        return null;
    }

    /// <summary>向 8K 接收器下发 DownLoadData (0x01) 查询已配对鼠标的 CID/MID（即使鼠标休眠中，部分接收器也会直接应答）。</summary>
    public static (byte Cid, byte Mid)? QueryDonglePairedCidMid(HidChannel ch, byte reportId = 0x08, int timeoutMs = 280)
    {
        ReadOnlySpan<byte> payload = [0x00, 0x00, 0x00, 0x08, 0xA5, 0x5A, 0x3C, 0xC3];
        var data = SendSimpleCommand(ch, 0x01, payload, reportId, timeoutMs);
        if (data is { Length: >= 6 } && (data[4] != 0 || data[5] != 0))
            return (data[4], data[5]);
        return null;
    }

    public static (byte Level, bool Charging)? QueryBattery(HidChannel ch, byte reportId = 0x08, int timeoutMs = 1000)
    {
        var packet = BuildFrame(reportId, 0x04, default);
        var outBuf = BuildOutBuffer(ch, packet);
        var padded65 = new byte[Math.Max(65, packet.Length)];
        Array.Copy(packet, padded65, packet.Length);

        bool sent = false;
        if (ch.OutputLength > 0)
            sent = ch.WriteOutput(outBuf) || ch.WriteOutput(padded65) || ch.WriteOutput(packet);

        if (sent)
        {
            var sw = Stopwatch.StartNew();
            bool resent = false;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!resent && sw.ElapsedMilliseconds >= 260)
                {
                    if (!ch.WriteOutput(outBuf))
                        ch.WriteOutput(packet);
                    resent = true;
                }
                if (TryParseBatteryFrame(ch.Read(80), reportId, out byte lvl, out bool chg))
                    return (lvl, chg);
            }
        }

        if (ch.FeatureLength >= 17)
        {
            if (ch.WriteFeature(packet))
            {
                Thread.Sleep(55);
                if (TryParseBatteryFrame(ch.ReadFeature(reportId), reportId, out byte lvl, out bool chg))
                    return (lvl, chg);
            }
        }
        return null;
    }

    public static bool TryParseBatteryFrame(byte[]? buf, out byte level, out bool charging)
        => TryParseBatteryFrame(buf, 0x08, out level, out charging);

    public static bool TryParseBatteryFrame(byte[]? buf, byte reportId, out byte level, out bool charging)
    {
        level = 0;
        charging = false;
        if (buf == null || buf.Length < 9)
            return false;

        ReadOnlySpan<byte> f;
        if (buf[0] == 0x04)
            f = buf;
        else if (buf.Length >= 10 && buf[0] == reportId && buf[1] == 0x04)
            f = buf.AsSpan(1);
        else
            return false;

        if (f.Length < 9 || f[5] is 0 or > 100)
            return false;

        bool isChg = f[6] == 0x01;
        int mv = (f[7] << 8) | f[8];
        if (mv == 0 && !isChg && f[4] == 0)
            return false;

        level = f[5];
        charging = isChg;
        return true;
    }

    public static byte[]? ReadEeprom(HidChannel ch, ushort addr, byte len, byte reportId = 0x08)
    {
        byte readLen = (byte)Math.Clamp((int)len, 1, 10);
        byte[] payload = [0x00, (byte)(addr >> 8), (byte)(addr & 0xFF), readLen];
        var pkt = BuildFrame(reportId, 0x08, payload);
        var outBuf = BuildOutBuffer(ch, pkt);
        var padded65 = new byte[Math.Max(65, pkt.Length)];
        Array.Copy(pkt, padded65, pkt.Length);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            for (int d = 0; d < 3; d++)
                if (ch.Read(5) == null) break;

            bool sent = ch.OutputLength > 0 && (ch.WriteOutput(outBuf) || ch.WriteOutput(padded65) || ch.WriteOutput(pkt));
            if (sent)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 450)
                {
                    var r = ch.Read(70);
                    if (TryExtractEepromData(r, reportId, addr, readLen, out var extracted))
                        return extracted;
                }
            }

            if (ch.FeatureLength >= 17 && ch.WriteFeature(pkt))
            {
                Thread.Sleep(45);
                var fr = ch.ReadFeature(reportId);
                if (TryExtractEepromData(fr, reportId, addr, readLen, out var extracted))
                    return extracted;
            }
        }
        return null;
    }

    public static bool WriteEeprom(HidChannel ch, ushort addr, ReadOnlySpan<byte> data, byte reportId = 0x08)
    {
        if (data.IsEmpty || data.Length > 10) return false;
        var payload = new byte[4 + data.Length];
        payload[0] = 0x00;
        payload[1] = (byte)(addr >> 8);
        payload[2] = (byte)(addr & 0xFF);
        payload[3] = (byte)data.Length;
        data.CopyTo(payload.AsSpan(4));

        var pkt = BuildFrame(reportId, 0x07, payload);
        var outBuf = BuildOutBuffer(ch, pkt);
        var padded65 = new byte[Math.Max(65, pkt.Length)];
        Array.Copy(pkt, padded65, pkt.Length);

        if (ch.OutputLength > 0 && (ch.WriteOutput(outBuf) || ch.WriteOutput(padded65) || ch.WriteOutput(pkt)))
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 400)
            {
                var r = ch.Read(70);
                if (IsAck(r, reportId, 0x07))
                    return true;
            }
            return true;
        }
        if (ch.FeatureLength >= 17 && ch.WriteFeature(pkt))
            return true;
        return false;
    }

    private static bool TryExtractEepromData(byte[]? buf, byte reportId, ushort addr, int expectedLen, out byte[] data)
    {
        data = [];
        if (buf == null || buf.Length < 6 + expectedLen) return false;
        ReadOnlySpan<byte> f;
        if (buf[0] == 0x08 && buf[1] != 0x08)
            f = buf;
        else if (buf[0] == reportId && buf[1] == 0x08)
            f = buf.AsSpan(1);
        else if (buf[0] == 0x08)
            f = buf;
        else
            return false;

        if (f.Length < 5 + expectedLen) return false;
        if (f[2] != (byte)(addr >> 8) || f[3] != (byte)(addr & 0xFF))
        {
            if (f[4] < expectedLen) return false;
        }
        data = f.Slice(5, expectedLen).ToArray();
        return true;
    }

    private static bool IsAck(byte[]? buf, byte reportId, byte cmd)
    {
        if (buf == null || buf.Length < 2) return false;
        return buf[0] == cmd || (buf[0] == reportId && buf[1] == cmd);
    }

    public static bool SwitchProfile(HidChannel ch, int zeroBasedProfile, byte reportId = 0x08)
    {
        byte p = (byte)Math.Clamp(zeroBasedProfile, 0, 3);
        var pkt = BuildFrame(reportId, 0x0F, [0x00, 0x00, 0x00, 0x01, p]);
        var outBuf = BuildOutBuffer(ch, pkt);
        var padded65 = new byte[Math.Max(65, pkt.Length)];
        Array.Copy(pkt, padded65, pkt.Length);

        bool sent = ch.OutputLength > 0 && (ch.WriteOutput(outBuf) || ch.WriteOutput(padded65) || ch.WriteOutput(pkt));
        if (!sent && ch.FeatureLength >= 17)
            sent = ch.WriteFeature(pkt);
        if (!sent) return false;
        Thread.Sleep(90);
        return true;
    }

    public static DeviceTuningCapabilities QueryCapabilities(
        string deviceId,
        HidChannel ch,
        byte reportId = 0x08,
        bool isWireless = true,
        string? productName = null,
        ushort vid = 0,
        ushort pid = 0,
        AtkSensorKind sensor = AtkSensorKind.Unknown)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        var bat = QueryBattery(ch, reportId, 750);
        byte[]? hdrA = ReadEeprom(ch, 0x0000, 8, reportId);
        byte[]? hdrB = ReadEeprom(ch, 0x0008, 4, reportId);
        byte[]? stgA = ReadEeprom(ch, 0x000C, 8, reportId);
        byte[]? stgB = ReadEeprom(ch, 0x0014, 8, reportId);

        if (hdrA == null || hdrB == null || stgA == null || stgB == null)
        {
            caps.IsOnline = bat != null;
            caps.StatusMessage = bat == null
                ? "设备当前休眠或未唤醒，请晃动鼠标后点击「读取当前硬件参数」。"
                : "已读取设备电量，但当前固件未开放 0x08 EEPROM 配置读取。";
            return caps;
        }

        // 查询 CID/MID（支持直连 0x10 与接收器 0x01）并解析真实传感器型号
        var cidMid = QueryCidMid(ch, reportId, 300) ?? QueryDonglePairedCidMid(ch, reportId, 260);
        string? resolvedName = AtkModelCatalog.ResolveModelName(vid, pid, cidMid, productName, AtkProtocolFamily.Compx) ?? productName;
        AtkSensorKind effectiveSensor = sensor != AtkSensorKind.Unknown
            ? sensor
            : AtkModelCatalog.ResolveSensor(vid, pid, cidMid, AtkProtocolFamily.Compx, resolvedName);
        if (effectiveSensor == AtkSensorKind.Unknown && IsUltraSensorFamily(resolvedName, null))
            effectiveSensor = AtkSensorKind.Paw3950Ultra;
        bool preferUltraDpi = effectiveSensor is AtkSensorKind.Paw3950Ultra or AtkSensorKind.Paw3955Master;

        byte[] hdr12 = [.. hdrA, .. hdrB];
        var stageBytesList = new List<byte>(32);
        stageBytesList.AddRange(stgA);
        stageBytesList.AddRange(stgB);
        int rawStageCount = ((hdr12[2] + hdr12[3]) & 0xFF) == 0x55 ? Math.Clamp((int)hdr12[2], 1, 8) : 4;
        if (rawStageCount > 4)
        {
            if (ReadEeprom(ch, 0x001C, 8, reportId) is { } stgC) stageBytesList.AddRange(stgC);
            if (ReadEeprom(ch, 0x0024, 8, reportId) is { } stgD) stageBytesList.AddRange(stgD);
        }

        if (!TryParseVerifiedConfig(hdr12, [.. stageBytesList], out var parsed, preferUltraDpi, effectiveSensor))
        {
            caps.IsOnline = true;
            caps.StatusMessage = "已连通鼠标控制接口，但 EEPROM 校验签名前置结构与已知 0x55 方案不一致；为防误写已自动保护。";
            return caps;
        }

        // PAW3955MASTER 将 8 组独立 DPI 存在 0x1B00..0x1B2A (每档 6 字节)
        bool used3955Table = false;
        if (effectiveSensor == AtkSensorKind.Paw3955Master)
        {
            var masterStages = new List<(int X, int Y)>(rawStageCount);
            for (int i = 0; i < rawStageCount; i++)
            {
                var row = ReadEeprom(ch, (ushort)(0x1B00 + i * 6), 6, reportId);
                if (DecodePaw3955Stage(row) is { } pair)
                    masterStages.Add(pair);
                else
                    break;
            }
            if (masterStages.Count > 0)
            {
                parsed.Stages = masterStages;
                used3955Table = true;
            }
        }

        caps.IsOnline = true;

        // 1. Onboard profiles (0x0E)
        var profReply = SendSimpleCommand(ch, 0x0E, default, reportId, 300);
        if (profReply is { Length: >= 1 } && profReply[0] <= 3)
        {
            caps.OnboardProfileCount = 4;
            caps.ActiveOnboardProfile = profReply[0];
            caps.OnboardProfileNames = ["板载配置 1", "板载配置 2", "板载配置 3", "板载配置 4"];
        }

        // 2. DPI stages
        caps.SupportsDpi = true;
        caps.SupportsIndependentXY = true;
        caps.MinDpi = 50;
        caps.MaxDpi = used3955Table || effectiveSensor == AtkSensorKind.Paw3955Master
            ? 45000
            : (preferUltraDpi ? 42000 : 30000);
        caps.DpiStep = preferUltraDpi ? 10 : 50;
        caps.DpiStages = parsed.Stages;
        caps.ActiveDpiStage = Math.Clamp(parsed.ActiveStageIndex, 0, Math.Max(0, parsed.Stages.Count - 1));
        if (caps.DpiStages.Count > 0)
        {
            caps.CurrentDpiX = caps.DpiStages[caps.ActiveDpiStage].X;
            caps.CurrentDpiY = caps.DpiStages[caps.ActiveDpiStage].Y;
        }
        caps.SupportsDpiStagesWrite = true;

        // 3. Polling rates
        caps.SupportedPollingRates = isWireless || (resolvedName ?? "").Contains("8K", StringComparison.OrdinalIgnoreCase)
            ? [125, 250, 500, 1000, 2000, 4000, 8000]
            : [125, 250, 500, 1000];
        caps.CurrentPollingRate = parsed.PollingRateHz;

        // 4. LOD (支持传统 1/2 档与 0.7..1.7 mm 细分)
        if (parsed.LodLevel is { } lod)
        {
            if (lod is >= 7 and <= 17)
            {
                caps.SupportedLodOptions = Enumerable.Range(7, 11)
                    .Select(v => new LodOption(v, $"{v / 10.0:0.0} mm"))
                    .ToList();
                caps.CurrentLod = lod;
            }
            else
            {
                caps.SupportedLodOptions = [new(1, "1.0 mm (低)"), new(2, "2.0 mm (高)")];
                caps.CurrentLod = lod;
            }
        }

        // 5. Advanced row 0x00A9 (10 bytes: debounce, motionSync, sleep*10s, linearCorrection, rippleControl)
        var adv = ReadEeprom(ch, 0x00A9, 10, reportId);
        if (adv != null && adv.Length >= 10)
        {
            if (((adv[0] + adv[1]) & 0xFF) == 0x55)
            {
                caps.SupportsDebounce = true;
                caps.MaxDebounceMs = 20;
                caps.CurrentDebounceMs = Math.Clamp((int)adv[0], 0, 20);
            }
            if (((adv[2] + adv[3]) & 0xFF) == 0x55)
            {
                caps.SupportsMotionSync = true;
                caps.MotionSync = adv[2] == 1;
            }
            if (((adv[4] + adv[5]) & 0xFF) == 0x55 && adv[4] > 0)
            {
                caps.SupportsSleepTimeout = true;
                caps.MinSleepSeconds = 30;
                caps.MaxSleepSeconds = 1800;
                caps.SupportedSleepSeconds = [30, 60, 120, 300, 600, 1800];
                caps.CurrentSleepSeconds = adv[4] * 10;
            }
            if (((adv[6] + adv[7]) & 0xFF) == 0x55)
            {
                caps.SupportsAngleSnapping = true;
                caps.AngleSnapping = adv[6] == 1;
            }
            if (((adv[8] + adv[9]) & 0xFF) == 0x55)
            {
                caps.SupportsRippleControl = true;
                caps.RippleControl = adv[8] == 1;
            }
        }

        // 6. Sensor performance mode 0x00B5 or 185 (0x00B9)
        var perfRow = ReadEeprom(ch, 0x00B5, 6, reportId);
        if (DecodeSensorMode(perfRow) is { } sensorMode)
        {
            caps.SupportedPerformanceModes =
            [
                new(0, "标准模式 (Basic)", "标准传感追踪采样"),
                new(1, "疾速模式 (Shard / HP)", "增强传感器扫描率与响应"),
                new(2, "狂暴竞技模式 (Shard MAX)", "极限 20K+ FPS 竞技传感性能"),
            ];
            caps.CurrentPerformanceMode = sensorMode;
        }
        else
        {
            var hpPair = ReadEeprom(ch, 185, 2, reportId);
            if (hpPair != null && hpPair.Length >= 2 && ((hpPair[0] + hpPair[1]) & 0xFF) == 0x55 && hpPair[0] <= 1)
            {
                caps.SupportedPerformanceModes =
                [
                    new(0, "标准模式 (LP)", "标准续航模式"),
                    new(1, "高性能竞技 (HP)", "开启高频传感引擎"),
                ];
                caps.CurrentPerformanceMode = hpPair[0];
            }
        }

        // 7. Long range mode 0x17
        var lr = SendSimpleCommand(ch, 0x17, default, reportId, 200);
        if (lr is { Length: >= 1 } && lr[0] <= 1)
        {
            caps.SupportsLongRangeMode = true;
            caps.LongRangeMode = lr[0] == 1;
        }

        caps.StatusMessage = "已通过 0x55 校验并读取实时硬件参数（配置/DPI/模式/传感器）。";
        return caps;
    }

    public static DeviceTuningResult ApplySettings(
        string deviceId,
        HidChannel ch,
        DeviceTuningUpdate update,
        byte reportId = 0x08,
        bool isWireless = true,
        string? productName = null,
        ushort vid = 0,
        ushort pid = 0,
        AtkSensorKind sensor = AtkSensorKind.Unknown)
    {
        if (update.ActiveOnboardProfile is { } profIdx)
            SwitchProfile(ch, profIdx, reportId);

        byte[]? hdrA = ReadEeprom(ch, 0x0000, 8, reportId);
        byte[]? hdrB = ReadEeprom(ch, 0x0008, 4, reportId);
        byte[]? stgA = ReadEeprom(ch, 0x000C, 8, reportId);
        byte[]? stgB = ReadEeprom(ch, 0x0014, 8, reportId);

        if (hdrA == null || hdrB == null || stgA == null || stgB == null)
            return new DeviceTuningResult(false, "无法读取设备当前 EEPROM 配置，写入已取消以确保硬件安全。");

        var cidMid = QueryCidMid(ch, reportId, 300) ?? QueryDonglePairedCidMid(ch, reportId, 260);
        string? resolvedName = AtkModelCatalog.ResolveModelName(vid, pid, cidMid, productName, AtkProtocolFamily.Compx) ?? productName;
        AtkSensorKind effectiveSensor = sensor != AtkSensorKind.Unknown
            ? sensor
            : AtkModelCatalog.ResolveSensor(vid, pid, cidMid, AtkProtocolFamily.Compx, resolvedName);
        if (effectiveSensor == AtkSensorKind.Unknown && IsUltraSensorFamily(resolvedName, null))
            effectiveSensor = AtkSensorKind.Paw3950Ultra;
        bool preferUltraDpi = effectiveSensor is AtkSensorKind.Paw3950Ultra or AtkSensorKind.Paw3955Master;

        byte[] hdr12 = [.. hdrA, .. hdrB];
        var stageBytesList = new List<byte>(32);
        stageBytesList.AddRange(stgA);
        stageBytesList.AddRange(stgB);
        int rawStageCount = ((hdr12[2] + hdr12[3]) & 0xFF) == 0x55 ? Math.Clamp((int)hdr12[2], 1, 8) : 4;
        if (rawStageCount > 4)
        {
            if (ReadEeprom(ch, 0x001C, 8, reportId) is { } stgC) stageBytesList.AddRange(stgC);
            if (ReadEeprom(ch, 0x0024, 8, reportId) is { } stgD) stageBytesList.AddRange(stgD);
        }

        if (!TryParseVerifiedConfig(hdr12, [.. stageBytesList], out var parsed, preferUltraDpi, effectiveSensor))
            return new DeviceTuningResult(false, "EEPROM 校验签名不匹配，已跳过写入以保护板载配置。");

        if (update.PollingRateHz is { } hz && EncodePollingRate(hz) is { } rCode)
            WriteEeprom(ch, 0x0000, [rCode, ChecksumByte(rCode)], reportId);

        // 0x0004 = currentDpi (当前激活档位索引), 0x0002 = maxDpi (总档位数)
        if (update.ActiveDpiStage is { } act)
        {
            byte stg = (byte)Math.Clamp(act, 0, 7);
            WriteEeprom(ch, 0x0004, [stg, ChecksumByte(stg)], reportId);
        }

        if (update.LodCode is { } lod && ((hdr12[10] + hdr12[11]) & 0xFF) == 0x55)
        {
            byte lodByte = hdr12[10] is >= 7 and <= 17
                ? (byte)Math.Clamp(lod, 7, 17)
                : (byte)Math.Clamp(lod, 1, 2);
            WriteEeprom(ch, 0x000A, [lodByte, ChecksumByte(lodByte)], reportId);
        }

        bool use3955Table = effectiveSensor == AtkSensorKind.Paw3955Master &&
                            DecodePaw3955Stage(ReadEeprom(ch, 0x1B00, 6, reportId)) != null;

        if (update.DpiStages is { Count: > 0 } newStages)
        {
            byte cnt = (byte)Math.Clamp(newStages.Count, 1, 8);
            if (((hdr12[2] + hdr12[3]) & 0xFF) == 0x55)
                WriteEeprom(ch, 0x0002, [cnt, ChecksumByte(cnt)], reportId);
            for (int i = 0; i < cnt; i++)
            {
                if (use3955Table)
                {
                    WriteEeprom(ch, (ushort)(0x1B00 + i * 6), EncodePaw3955Stage(newStages[i].X, newStages[i].Y), reportId);
                }
                else
                {
                    byte[] encoded = preferUltraDpi
                        ? EncodeDpiStageUltra(newStages[i].X, newStages[i].Y)
                        : EncodeDpiStage(newStages[i].X, newStages[i].Y);
                    WriteEeprom(ch, (ushort)(0x000C + i * 4), encoded, reportId);
                }
            }
        }
        else if (update.Dpi is { } dpi)
        {
            int idx = Math.Clamp(update.ActiveDpiStage ?? parsed.ActiveStageIndex, 0, Math.Max(0, parsed.Stages.Count - 1));
            if (use3955Table)
            {
                WriteEeprom(ch, (ushort)(0x1B00 + idx * 6), EncodePaw3955Stage(dpi.X, dpi.Y), reportId);
            }
            else
            {
                byte[] encoded = preferUltraDpi
                    ? EncodeDpiStageUltra(dpi.X, dpi.Y)
                    : EncodeDpiStage(dpi.X, dpi.Y);
                WriteEeprom(ch, (ushort)(0x000C + idx * 4), encoded, reportId);
            }
        }

        // Write individual fields inside 0x00A9 row when checksum pairs exist
        var adv = ReadEeprom(ch, 0x00A9, 10, reportId);
        if (adv != null && adv.Length >= 10)
        {
            if (update.DebounceMs is { } deb && ((adv[0] + adv[1]) & 0xFF) == 0x55)
            {
                byte v = (byte)Math.Clamp(deb, 0, 20);
                WriteEeprom(ch, 0x00A9, [v, ChecksumByte(v)], reportId);
            }
            if (update.MotionSync is { } ms && ((adv[2] + adv[3]) & 0xFF) == 0x55)
            {
                byte v = (byte)(ms ? 1 : 0);
                WriteEeprom(ch, 0x00AB, [v, ChecksumByte(v)], reportId);
            }
            if (update.SleepSeconds is { } sleep && ((adv[4] + adv[5]) & 0xFF) == 0x55)
            {
                byte v = (byte)Math.Clamp((int)Math.Round(sleep / 10.0), 1, 180);
                WriteEeprom(ch, 0x00AD, [v, ChecksumByte(v)], reportId);
            }
            if (update.AngleSnapping is { } snap && ((adv[6] + adv[7]) & 0xFF) == 0x55)
            {
                byte v = (byte)(snap ? 1 : 0);
                WriteEeprom(ch, 0x00AF, [v, ChecksumByte(v)], reportId);
            }
            if (update.RippleControl is { } ripple && ((adv[8] + adv[9]) & 0xFF) == 0x55)
            {
                byte v = (byte)(ripple ? 1 : 0);
                WriteEeprom(ch, 0x00B1, [v, ChecksumByte(v)], reportId);
            }
        }

        if (update.PerformanceMode is { } perfMode)
        {
            var perfRow = ReadEeprom(ch, 0x00B5, 6, reportId);
            if (DecodeSensorMode(perfRow) != null)
            {
                byte m = (byte)Math.Clamp(perfMode, 0, 2);
                WriteEeprom(ch, 0x00B5, [0x00, 0x55, 0x06, 0x4F, m, ChecksumByte(m)], reportId);
            }
            else
            {
                var hpPair = ReadEeprom(ch, 185, 2, reportId);
                if (hpPair != null && hpPair.Length >= 2 && ((hpPair[0] + hpPair[1]) & 0xFF) == 0x55)
                {
                    byte hp = (byte)(perfMode > 0 ? 1 : 0);
                    WriteEeprom(ch, 185, [hp, ChecksumByte(hp)], reportId);
                }
            }
        }

        if (update.LongRangeMode is { } lr)
            SendSimpleCommand(ch, 0x16, [(byte)(lr ? 1 : 0)], reportId, 200);

        Thread.Sleep(45);
        var verified = QueryCapabilities(deviceId, ch, reportId, isWireless, resolvedName, vid, pid, effectiveSensor);
        return new DeviceTuningResult(true, "已写入硬件并校验保存。", verified);
    }

    internal static int? DecodeSensorMode(byte[]? row)
    {
        if (row == null || row.Length < 6) return null;
        if (row[2] != 0x06 || row[3] != 0x4F) return null;
        if (((row[4] + row[5]) & 0xFF) != 0x55) return null;
        return row[4] <= 2 ? row[4] : null;
    }

    internal static bool TryParseVerifiedConfig(
        byte[] hdr12,
        byte[] stagesBytes,
        out ParsedConfig config,
        bool useUltraDpi = false,
        AtkSensorKind sensor = AtkSensorKind.Unknown)
    {
        config = new ParsedConfig();
        if (hdr12.Length < 12 || stagesBytes.Length < 4)
            return false;

        if (((hdr12[0] + hdr12[1]) & 0xFF) != 0x55 || ((hdr12[2] + hdr12[3]) & 0xFF) != 0x55)
            return false;

        int? rateHz = DecodePollingRate(hdr12[0]);
        if (rateHz == null) return false;

        bool ultra = useUltraDpi || sensor is AtkSensorKind.Paw3950Ultra or AtkSensorKind.Paw3955Master;
        config.PollingRateHz = rateHz.Value;
        // EEPROM [2..3] = maxDpi (总档位数 1..8); [4..5] = currentDpi (当前档位索引 0..7)
        config.StageCount = Math.Clamp((int)hdr12[2], 1, 8);
        config.ActiveStageIndex = ((hdr12[4] + hdr12[5]) & 0xFF) == 0x55
            ? Math.Clamp((int)hdr12[4], 0, Math.Max(0, config.StageCount - 1))
            : 0;
        if (((hdr12[10] + hdr12[11]) & 0xFF) == 0x55 && (hdr12[10] is 1 or 2 || hdr12[10] is >= 7 and <= 17))
            config.LodLevel = hdr12[10];

        int maxSlots = Math.Min(config.StageCount, stagesBytes.Length / 4);
        for (int i = 0; i < maxSlots; i++)
        {
            var slice = stagesBytes.AsSpan(i * 4, 4);
            int sum = (slice[0] + slice[1] + slice[2] + slice[3]) & 0xFF;
            if (sum != 0x55) break;
            config.Stages.Add(ultra ? DecodeDpiStageUltra(slice) : DecodeDpiStage(slice));
        }

        return config.Stages.Count > 0;
    }

    internal static byte ChecksumByte(byte v) => (byte)((0x55 - v) & 0xFF);

    internal static (int X, int Y) DecodeDpiStage(ReadOnlySpan<byte> fourBytes)
    {
        int xSteps = fourBytes[0] | ((fourBytes[2] & 0x0C) << 6);
        int ySteps = fourBytes[1] | ((fourBytes[2] & 0xC0) << 2);
        int x = (xSteps + 1) * 50;
        int y = (ySteps + 1) * 50;
        if ((fourBytes[2] & 0x01) != 0) x *= 2;
        if ((fourBytes[2] & 0x10) != 0) y *= 2;
        return (x, y);
    }

    internal static byte[] EncodeDpiStage(int dpiX, int dpiY)
    {
        byte flags = 0;
        int xStepSize = dpiX <= 30000 ? 50 : 100;
        int yStepSize = dpiY <= 30000 ? 50 : 100;
        if (dpiX > 30000) flags |= 0x01;
        if (dpiY > 30000) flags |= 0x10;
        int xs = Math.Clamp((int)Math.Round(dpiX / (double)xStepSize) - 1, 0, 1023);
        int ys = Math.Clamp((int)Math.Round(dpiY / (double)yStepSize) - 1, 0, 1023);
        byte xLo = (byte)(xs & 0xFF);
        byte yLo = (byte)(ys & 0xFF);
        byte hi = (byte)(((xs >> 8) & 0x03) << 2 | ((ys >> 8) & 0x03) << 6 | flags);
        byte chk = (byte)((0x55 - ((xLo + yLo + hi) & 0xFF)) & 0xFF);
        return [xLo, yLo, hi, chk];
    }

    private static int DecodeUltraAxis(byte b, byte nibble)
    {
        int code = (((nibble >> 2) & 0x03) << 8) | b;
        int baseDpi = (nibble & 2) != 0 ? 10050 + code * 50 : (code + 1) * 10;
        return (nibble & 1) != 0 ? baseDpi * 2 : baseDpi;
    }

    private static (byte Byte, byte Nibble) EncodeUltraAxis(int dpi)
    {
        int v = Math.Clamp(dpi, 10, 42000);
        byte doubled = 0;
        if (v > 30000)
        {
            doubled = 1;
            v = (int)Math.Round(v / 100.0) * 50;
        }
        if (v <= 10000)
        {
            int code = Math.Max(0, (int)Math.Round(v / 10.0) - 1);
            return ((byte)(code & 0xFF), (byte)(((code >> 8) & 0x03) << 2 | doubled));
        }
        else
        {
            int snapped = Math.Max(10050, (int)Math.Round(v / 50.0) * 50);
            int code = Math.Max(0, (snapped - 10050) / 50);
            return ((byte)(code & 0xFF), (byte)(((code >> 8) & 0x03) << 2 | 2 | doubled));
        }
    }

    internal static (int X, int Y) DecodeDpiStageUltra(ReadOnlySpan<byte> fourBytes)
        => (DecodeUltraAxis(fourBytes[0], (byte)(fourBytes[2] & 0x0F)),
            DecodeUltraAxis(fourBytes[1], (byte)((fourBytes[2] >> 4) & 0x0F)));

    internal static byte[] EncodeDpiStageUltra(int dpiX, int dpiY)
    {
        var (xb, xn) = EncodeUltraAxis(dpiX);
        var (yb, yn) = EncodeUltraAxis(dpiY);
        byte mode = (byte)(((yn & 0x0F) << 4) | (xn & 0x0F));
        byte chk = (byte)((0x55 - ((xb + yb + mode) & 0xFF)) & 0xFF);
        return [xb, yb, mode, chk];
    }

    internal static (int X, int Y)? DecodePaw3955Stage(byte[]? sixBytes)
    {
        if (sixBytes == null || sixBytes.Length < 6) return null;
        int sum = (sixBytes[0] + sixBytes[1] + sixBytes[2] + sixBytes[3] + sixBytes[4] + sixBytes[5]) & 0xFF;
        if (sum != 0x55) return null;
        int xBase = sixBytes[0] | (sixBytes[1] << 8);
        int yBase = sixBytes[2] | (sixBytes[3] << 8);
        int xMult = (sixBytes[4] & 0x01) != 0 ? 2 : 1;
        int yMult = (sixBytes[4] & 0x10) != 0 ? 2 : 1;
        return ((xBase + 1) * xMult, (yBase + 1) * yMult);
    }

    internal static byte[] EncodePaw3955Stage(int dpiX, int dpiY)
    {
        static (int Base, bool Mult) PackAxis(int dpi)
        {
            int clamped = Math.Clamp(dpi, 10, 45000);
            if (clamped < 40000)
                return (Math.Max(0, clamped - 1), false);
            int half = (clamped % 2 == 0 ? clamped : clamped - 1) / 2;
            return (Math.Clamp(half - 1, 0, 65535), true);
        }

        var (xb, xm) = PackAxis(dpiX);
        var (yb, ym) = PackAxis(dpiY);
        byte flags = (byte)((xm ? 0x01 : 0) | (ym ? 0x10 : 0));
        byte b0 = (byte)(xb & 0xFF);
        byte b1 = (byte)((xb >> 8) & 0xFF);
        byte b2 = (byte)(yb & 0xFF);
        byte b3 = (byte)((yb >> 8) & 0xFF);
        byte crc = (byte)((0x55 - ((b0 + b1 + b2 + b3 + flags) & 0xFF)) & 0xFF);
        return [b0, b1, b2, b3, flags, crc];
    }

    private static bool IsUltraSensorFamily(string? productName, byte[]? cidMid)
    {
        string upper = (productName ?? "").ToUpperInvariant();
        return upper.Contains("3950 ULTRA") || upper.Contains("A9 MINI") ||
               upper.Contains("F1 ULTIMATE") || upper.Contains("F1 EXTREME") ||
               upper.Contains("X1 ULTIMATE") || upper.Contains("Z1 PRO MAX");
    }

    internal static int? DecodePollingRate(byte code) => code switch
    {
        0x01 => 1000,
        0x02 => 500,
        0x04 => 250,
        0x08 => 125,
        0x10 => 2000,
        0x20 => 4000,
        0x40 => 8000,
        _ => null,
    };

    internal static byte? EncodePollingRate(int hz) => hz switch
    {
        1000 => 0x01,
        500 => 0x02,
        250 => 0x04,
        125 => 0x08,
        2000 => 0x10,
        4000 => 0x20,
        8000 => 0x40,
        _ => null,
    };
}
