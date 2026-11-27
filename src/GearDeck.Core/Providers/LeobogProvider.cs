using System.Globalization;
using System.Text;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// LEOBOG Hi98C Pro 三模键盘：
/// - USB-C 有线：0x258A:0x010C，UsagePage 0xFF00，520B Feature Report ID 0x06（绝不伪造电量）；
/// - 2.4G：0x3554:0xFA09，蓝牙：0x3554:0xFA07，均使用 20B Report ID 0x13 协议。
/// 无线模式严格只读；只有有线 258A:010C 允许写入板载配置。
/// </summary>
public sealed class LeobogProvider : ITunablePeripheralProvider
{
    public const string ProviderName = "LEOBOG";
    public string Name => ProviderName;

    private const byte WirelessReportId = 0x13;
    private const int WirelessPacketLength = 20;
    private const byte CmdBattery = 0x0A;
    private const byte CmdWirelessConfig = 0x44;

    private const byte WiredReportId = 0x06;
    private const int WiredReportLength = 520;
    private const byte CmdModel = 0x82;
    private const byte CmdWiredConfig = 0x84;
    private const byte CmdWriteConfig = 0x04;

    private const int ConfigLength = 128;
    private const int SleepOffset = 0x18;
    private const int WinLockOffset = 0x0F;
    private const int OsModeOffset = 0x1B;
    private const int LedEffectOffset = 0x0A;
    private const byte ConfigTail0 = 0x5A;
    private const byte ConfigTail1 = 0xA5;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("leobog:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevices()
            .Where(IsLeobogDevice)
            .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID));

        foreach (var g in groups)
        {
            try
            {
                var (vid, pid) = g.Key;
                bool wired = IsWired(vid, pid);
                string transport = wired ? "USB" : pid == 0xFA07 ? "bluetooth" : "2.4g";
                string name = wired ? "LEOBOG Hi98C Pro (USB-C)"
                    : pid == 0xFA07 ? "LEOBOG Hi98C Pro (Bluetooth)"
                    : "LEOBOG Hi98C Pro (2.4G)";

                int? battery = null;
                BatteryState state = BatteryState.Unknown;

                if (wired)
                {
                    // 有线 USB-C 始终视为充电中；绝不读取/伪造电量百分比。
                    battery = null;
                    state = BatteryState.Charging;
                }
                else
                {
                    battery = ProbeWirelessBattery(g);
                    state = battery.HasValue ? BatteryState.Normal : BatteryState.Unknown;
                }

                results.Add(new PeripheralDevice
                {
                    Id = $"leobog:{vid:X4}-{pid:X4}",
                    VendorId = vid,
                    ProductId = pid,
                    Name = name,
                    Kind = DeviceKind.Keyboard,
                    Provider = ProviderName,
                    Connected = true,
                    IsSleeping = !wired && !battery.HasValue,
                    Transport = transport,
                    BatteryPercent = battery,
                    State = state,
                    Note = wired ? "USB-C 有线模式：电量显示为充电中（不提供伪造百分比）" : null,
                    LastUpdated = DateTimeOffset.Now,
                });
            }
            catch (Exception ex)
            {
                Diag.Log($"LEOBOG {g.Key.Item1:X4}:{g.Key.Item2:X4}: {ex.Message}");
            }
        }
        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
        {
            caps.StatusMessage = "无效的 LEOBOG 设备 ID。";
            return caps;
        }

        var list = HidSafe.EnumerateDevices(vid, pid);
        if (list.Count == 0)
        {
            caps.StatusMessage = "未找到 LEOBOG 设备接口。";
            return caps;
        }

        bool wired = IsWired(vid, pid);
        caps.IsOnline = true;
        caps.SupportsSleepTimeout = true;
        caps.MinSleepSeconds = 30;
        caps.MaxSleepSeconds = 1800;
        caps.SupportedSleepSeconds = Enumerable.Range(1, 60).Select(u => u * 30).ToList();

        byte[]? cfg;
        string? model = null;

        if (wired)
        {
            var dev = FindWiredInterface(list);
            if (dev == null)
            {
                caps.IsOnline = false;
                caps.StatusMessage = "未找到支持 520B Feature 0x06 的 LEOBOG 有线配置接口。";
                return caps;
            }
            using var raw = RawHidFeature.TryOpen(dev.DevicePath);
            if (raw == null)
            {
                caps.IsOnline = false;
                caps.StatusMessage = "无法打开 LEOBOG 有线配置接口。";
                return caps;
            }
            model = ReadWiredModel(raw);
            cfg = ReadWiredConfig(raw);
        }
        else
        {
            // 2.4G / 蓝牙：仅通过 0x44 分包拼接读取配置用于显示，绝不写入。
            cfg = ReadWirelessConfig(list);
        }

        if (cfg != null && IsValidConfig(cfg))
        {
            caps.CurrentSleepSeconds = cfg[SleepOffset] * 30;
            caps.StatusMessage = BuildConfigSummary(model, cfg, wired);
        }
        else
        {
            caps.StatusMessage = wired
                ? "已连接 LEOBOG Hi98C Pro 有线接口，但板载配置读取/校验未通过。"
                : "LEOBOG Hi98C Pro 无线模式严格只读；板载配置当前不可读取（设备可能休眠）。";
        }

        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new DeviceTuningResult(false, "无效的 LEOBOG 设备 ID。");

        // 安全保护铁律：2.4G / 蓝牙一律拒绝写入。
        if (!IsWired(vid, pid))
            return new DeviceTuningResult(false,
                "安全保护：LEOBOG Hi98C Pro 仅允许在 USB-C 有线模式下写入板载配置，无线模式严格只读。");

        var list = HidSafe.EnumerateDevices(vid, pid);
        var dev = FindWiredInterface(list);
        if (dev == null)
            return new DeviceTuningResult(false, "未找到支持 520B Feature 0x06 的 LEOBOG 有线配置接口。");

        using var raw = RawHidFeature.TryOpen(dev.DevicePath);
        if (raw == null)
            return new DeviceTuningResult(false, "无法打开 LEOBOG 有线配置接口。");

        // 先读 0x84 校验，再写 0x04，延时 400ms 回读校验。
        byte[]? cfg = ReadWiredConfig(raw);
        if (cfg == null || !IsValidConfig(cfg))
            return new DeviceTuningResult(false, "读取板载配置失败或校验不通过，已中止写入。");

        if (update.SleepSeconds is { } sec)
            PatchConfigSleepSeconds(cfg, sec);

        var request = BuildWiredFeatureRequest(CmdWriteConfig, 0, 1, 0, ConfigLength, cfg);
        if (!raw.SetFeature(request))
            return new DeviceTuningResult(false, "写入板载配置失败。");

        Thread.Sleep(400);

        byte[]? verify = ReadWiredConfig(raw);
        if (verify == null || !IsValidConfig(verify))
            return new DeviceTuningResult(false, "回读板载配置失败，无法确认写入结果。");
        if (verify[SleepOffset] != cfg[SleepOffset])
            return new DeviceTuningResult(false, "回读校验失败：休眠时间未正确写入。");

        return new DeviceTuningResult(true, "已写入 LEOBOG Hi98C Pro 板载配置（回读校验通过）。",
            QueryCapabilities(deviceId));
    }

    // ── 无线 0x13 / 20B 协议 ────────────────────────────────────────────────

    /// <summary>前 19 字节求和取低 8 位，返回应写入 pkt[19] 的校验字节。</summary>
    internal static byte ComputeWirelessChecksum(ReadOnlySpan<byte> pkt)
    {
        int sum = 0;
        int n = Math.Min(pkt.Length, WirelessPacketLength - 1);
        for (int i = 0; i < n; i++) sum += pkt[i];
        return (byte)(sum & 0xFF);
    }

    internal static byte[] BuildWirelessReadPacket(byte cmd, byte sub = 0)
    {
        var pkt = new byte[WirelessPacketLength];
        pkt[0] = WirelessReportId;
        pkt[1] = (byte)(cmd | 0x40);
        pkt[2] = 1;
        pkt[3] = 0;
        pkt[4] = (byte)((sub & 0x0F) << 4);
        pkt[WirelessPacketLength - 1] = ComputeWirelessChecksum(pkt);
        return pkt;
    }

    internal static bool ParseWirelessPacket(ReadOnlySpan<byte> pkt)
        => ParseWirelessPacket(pkt, out _);

    internal static bool ParseWirelessPacket(ReadOnlySpan<byte> pkt, out byte[] payload)
    {
        payload = [];
        if (pkt.Length < WirelessPacketLength) return false;
        if (pkt[0] != WirelessReportId) return false;
        if ((pkt[2] & 0x80) != 0) return false;
        if (pkt[WirelessPacketLength - 1] != ComputeWirelessChecksum(pkt)) return false;
        payload = ExtractWirelessPayload(pkt);
        return true;
    }

    internal static byte[] ExtractWirelessPayload(ReadOnlySpan<byte> pkt)
    {
        // 5 字节头 + 1 字节校验 => 20B 包携带 14B 有效载荷。
        if (pkt.Length <= 6) return [];
        return pkt.Slice(5, pkt.Length - 6).ToArray();
    }

    internal static int? ParseWirelessBatteryPayload(ReadOnlySpan<byte> payload)
        => payload.Length > 0 && payload[0] <= 100 ? payload[0] : null;

    internal static byte[] BuildWirelessConfigReadPacket(int sub)
        => BuildWirelessReadPacket(CmdWirelessConfig, (byte)sub);

    /// <summary>将 0x44 分包返回的有效载荷依次拼接为 128B 板载配置。</summary>
    internal static byte[]? AssembleWirelessConfig(IEnumerable<byte[]> packets)
    {
        var cfg = new byte[ConfigLength];
        int filled = 0;
        foreach (var pkt in packets)
        {
            if (pkt == null || !ParseWirelessPacket(pkt)) continue;
            var payload = ExtractWirelessPayload(pkt);
            if (payload.Length == 0) continue;
            int n = Math.Min(payload.Length, ConfigLength - filled);
            Array.Copy(payload, 0, cfg, filled, n);
            filled += n;
            if (filled >= ConfigLength) break;
        }
        return filled == ConfigLength && IsValidConfig(cfg) ? cfg : null;
    }

    private static int? ProbeWirelessBattery(IEnumerable<HidDevice> group)
    {
        var request = BuildWirelessReadPacket(CmdBattery);
        foreach (var dev in group)
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;

            byte[]? reply = null;
            if (ch.OutputLength >= WirelessPacketLength && ch.WriteOutput(request))
                reply = ch.Read(200);
            if (reply == null && ch.FeatureLength >= WirelessPacketLength && ch.WriteFeature(request))
            {
                Thread.Sleep(45);
                reply = ch.ReadFeature(WirelessReportId);
            }
            if (reply == null || !ParseWirelessPacket(reply, out var payload)) continue;
            if (ParseWirelessBatteryPayload(payload) is { } pct) return pct;
        }
        return null;
    }

    private static byte[]? ReadWirelessConfig(IEnumerable<HidDevice> group)
    {
        foreach (var dev in group)
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;

            var packets = new List<byte[]>();
            for (int sub = 0; sub < 16 && packets.Count < 16; sub++)
            {
                var request = BuildWirelessConfigReadPacket(sub);
                byte[]? reply = null;
                if (ch.OutputLength >= WirelessPacketLength && ch.WriteOutput(request))
                    reply = ch.Read(200);
                if (reply == null && ch.FeatureLength >= WirelessPacketLength && ch.WriteFeature(request))
                {
                    Thread.Sleep(45);
                    reply = ch.ReadFeature(WirelessReportId);
                }
                if (reply == null || !ParseWirelessPacket(reply)) break;
                if (reply[1] != (CmdWirelessConfig | 0x40) && reply[1] != CmdWirelessConfig) break;
                packets.Add(reply);
            }
            if (AssembleWirelessConfig(packets) is { } cfg) return cfg;
        }
        return null;
    }

    // ── 有线 520B Feature 0x06 协议 ─────────────────────────────────────────

    internal static byte[] BuildWiredFeatureRequest(byte cmd, byte sub, int packetCount,
        int packetIndex, int payloadLen, byte[]? payload = null)
    {
        var report = new byte[WiredReportLength];
        report[0] = WiredReportId;
        report[1] = cmd;
        report[2] = sub;
        report[3] = 0x00;
        report[4] = (byte)packetCount;
        report[5] = (byte)packetIndex;
        report[6] = (byte)(payloadLen & 0xFF);
        report[7] = (byte)((payloadLen >> 8) & 0xFF);
        if (payload != null && payloadLen > 0)
            Array.Copy(payload, 0, report, 8, Math.Min(payloadLen, payload.Length));
        return report;
    }

    internal static byte[]? ExtractWiredReplyPayload(ReadOnlySpan<byte> reply)
        => ExtractWiredReplyPayload(reply, 0, null);

    internal static byte[]? ExtractWiredReplyPayload(ReadOnlySpan<byte> reply, int expectedLen, byte? expectedCmd = null)
    {
        if (reply.Length < 8) return null;
        if (reply[0] != WiredReportId) return null;
        if (expectedCmd is { } cmd && reply[1] != cmd) return null;

        int len = reply[6] | (reply[7] << 8);
        if (len <= 0 || reply.Length < 8 + len) return null;
        if (expectedLen > 0 && len < expectedLen) return null;
        return reply.Slice(8, len).ToArray();
    }

    internal static byte[] PatchConfigSleepSeconds(byte[] cfg, int sec)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        int units = (int)Math.Round(sec / 30.0);
        units = Math.Clamp(units, 1, 60); // 严禁写 0
        if (cfg.Length > SleepOffset)
            cfg[SleepOffset] = (byte)units;
        return cfg;
    }

    private static string? ReadWiredModel(RawHidFeature raw)
    {
        var request = BuildWiredFeatureRequest(CmdModel, 0x01, 1, 0, 6);
        if (!raw.SetFeature(request)) return null;
        Thread.Sleep(30);
        var reply = raw.GetFeature(WiredReportLength, WiredReportId);
        var payload = reply == null ? null : ExtractWiredReplyPayload(reply, 6, CmdModel);
        if (payload == null || payload.Length < 6) return null;
        byte modelId = payload[5];
        return modelId != 0 ? $"LEOBOG Hi98C Pro (0x{modelId:X2})" : null;
    }

    private static byte[]? ReadWiredConfig(RawHidFeature raw)
    {
        var request = BuildWiredFeatureRequest(CmdWiredConfig, 0, 1, 0, ConfigLength);
        if (!raw.SetFeature(request)) return null;
        Thread.Sleep(30);
        var reply = raw.GetFeature(WiredReportLength, WiredReportId);
        if (reply == null) return null;
        var payload = ExtractWiredReplyPayload(reply, ConfigLength, CmdWiredConfig);
        return payload != null && IsValidConfig(payload) ? payload : null;
    }

    // ── 公共辅助 ─────────────────────────────────────────────────────────────

    private static bool IsWired(ushort vid, ushort pid) => vid == 0x258A && pid == 0x010C;

    private static bool IsLeobogDevice(HidDevice d)
    {
        string upper = HidSafe.ProductName(d).ToUpperInvariant();
        if (d.VendorID == 0x258A && d.ProductID == 0x010C) return true;
        if (upper.Contains("AULA")) return false;
        if (upper.Contains("LEOBOG") || upper.Contains("HI98")) return true;
        if (d.VendorID == 0x3554 && d.ProductID == 0xFA07) return true;
        if (d.VendorID == 0x3554 && d.ProductID == 0xFA09)
            return HidSafe.EnumerateDevices(0x258A, 0x010C).Count > 0;
        return false;
    }

    private static HidDevice? FindWiredInterface(IEnumerable<HidDevice> devs)
        => devs.FirstOrDefault(d => HidSafe.MaxFeatureLen(d) >= WiredReportLength
            && HidReportInspector.GetReportIds(d).FeatureIds.Contains(WiredReportId));

    private static bool IsValidConfig(byte[] cfg)
        => cfg.Length >= ConfigLength && cfg[126] == ConfigTail0 && cfg[127] == ConfigTail1;

    private static string BuildConfigSummary(string? model, byte[] cfg, bool wired)
    {
        string head = string.IsNullOrWhiteSpace(model) ? "LEOBOG Hi98C Pro" : model!;
        string lockText = cfg[WinLockOffset] != 0 ? "锁定" : "未锁定";
        string osText = cfg[OsModeOffset] == 0 ? "Win" : "Mac";
        string mode = wired ? "USB-C 有线（可写）" : "无线只读";
        return $"{head} 板载配置：休眠 {cfg[SleepOffset] * 30}s · WinLock {lockText} · 系统模式 {osText} · 灯效 0x{cfg[LedEffectOffset]:X2}（{mode}）。";
    }

    private static bool TryParseVidPid(string deviceId, out ushort vid, out ushort pid)
    {
        vid = 0; pid = 0;
        var p = deviceId.Split(':');
        if (p.Length < 2) return false;
        var vp = p[1].Split('-');
        return vp.Length == 2
            && ushort.TryParse(vp[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid)
            && ushort.TryParse(vp[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
    }
}
