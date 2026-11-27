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
public sealed partial class LeobogProvider : ITunablePeripheralProvider
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

        if (OperatingSystem.IsMacOS())
        {
            var macGroups = MacIOKitHid.Enumerate()
                .Where(IsLeobogMacInterface)
                .GroupBy(d => (d.VendorId, d.ProductId));

            foreach (var g in macGroups)
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
                    bool permDenied = false;

                    if (wired)
                    {
                        battery = null;
                        state = BatteryState.Charging;
                    }
                    else
                    {
                        battery = ProbeWirelessBatteryMac(g, out permDenied);
                        state = battery.HasValue ? BatteryState.Normal : BatteryState.Unknown;
                    }

                    string? note = wired
                        ? "USB-C 有线模式：电量显示为充电中（不提供伪造百分比）"
                        : permDenied
                            ? "需要 macOS「输入监控」权限以读取 2.4G/蓝牙电量与配置（请在设备总览顶部授权）"
                            : null;

                    results.Add(new PeripheralDevice
                    {
                        Id = $"leobog:{vid:X4}-{pid:X4}",
                        VendorId = vid,
                        ProductId = pid,
                        Name = name,
                        Kind = DeviceKind.Keyboard,
                        Provider = ProviderName,
                        Connected = true,
                        IsSleeping = !wired && !permDenied && !battery.HasValue,
                        Transport = transport,
                        BatteryPercent = battery,
                        State = state,
                        Note = note,
                        LastUpdated = DateTimeOffset.Now,
                    });
                }
                catch (Exception ex)
                {
                    Diag.Log($"LEOBOG(mac) {g.Key.Item1:X4}:{g.Key.Item2:X4}: {ex.Message}");
                }
            }
            return results;
        }

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

        if (OperatingSystem.IsMacOS())
        {
            var macList = MacIOKitHid.Enumerate(vid, pid);
            if (macList.Count == 0)
            {
                caps.StatusMessage = "未找到 LEOBOG 设备接口。";
                return caps;
            }

            bool macWired = IsWired(vid, pid);
            caps.IsOnline = true;
            caps.SupportsSleepTimeout = true;
            caps.MinSleepSeconds = 30;
            caps.MaxSleepSeconds = 1800;
            caps.SupportedSleepSeconds = Enumerable.Range(1, 60).Select(u => u * 30).ToList();

            byte[]? macCfg = null;
            string? macModel = null;
            bool permDenied = false;

            if (macWired)
            {
                var dev = FindWiredMacInterface(macList);
                if (dev == null)
                {
                    caps.IsOnline = false;
                    caps.StatusMessage = "未找到支持 520B Feature 0x06 的 LEOBOG 有线配置接口。";
                    return caps;
                }
                using var conn = MacIOKitHid.MacHidConnection.TryOpen(dev, out int openRet);
                if (conn == null)
                {
                    caps.IsOnline = false;
                    caps.StatusMessage = openRet == MacIOKitHid.kIOReturnNotPermitted
                        ? "打开 LEOBOG 有线接口被 macOS 输入监控权限拦截，请授权后重启应用。"
                        : $"无法打开 LEOBOG 有线配置接口 (0x{unchecked((uint)openRet):X})。";
                    return caps;
                }
                macModel = ReadWiredModelMac(conn);
                macCfg = ReadWiredConfigMac(conn);
            }
            else
            {
                macCfg = ReadWirelessConfigMac(macList, out permDenied);
            }

            if (macCfg != null && IsValidConfig(macCfg))
            {
                caps.CurrentSleepSeconds = macCfg[SleepOffset] * 30;
                caps.StatusMessage = BuildConfigSummary(macModel, macCfg, macWired);
            }
            else if (permDenied)
            {
                caps.StatusMessage = "需要 macOS「输入监控」权限才可读取 LEOBOG Hi98C Pro 电量与板载配置；请在设备总览顶部点击「打开系统设置」授权并重启 GearDeck。";
            }
            else
            {
                caps.StatusMessage = macWired
                    ? "已连接 LEOBOG Hi98C Pro 有线接口，但板载配置读取/校验未通过。"
                    : "LEOBOG Hi98C Pro 无线模式严格只读；板载配置当前不可读取（设备可能休眠）。";
            }

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

        if (OperatingSystem.IsMacOS())
        {
            var macList = MacIOKitHid.Enumerate(vid, pid);
            var macDev = FindWiredMacInterface(macList);
            if (macDev == null)
                return new DeviceTuningResult(false, "未找到支持 520B Feature 0x06 的 LEOBOG 有线配置接口。");

            using var conn = MacIOKitHid.MacHidConnection.TryOpen(macDev, out int openRet);
            if (conn == null)
                return new DeviceTuningResult(false, openRet == MacIOKitHid.kIOReturnNotPermitted
                    ? "打开有线配置通道需要 macOS 输入监控权限。"
                    : "无法打开 LEOBOG 有线配置接口。");

            byte[]? macCfg = ReadWiredConfigMac(conn);
            if (macCfg == null || !IsValidConfig(macCfg))
                return new DeviceTuningResult(false, "读取板载配置失败或校验不通过，已中止写入。");

            if (update.SleepSeconds is { } secMac)
                PatchConfigSleepSeconds(macCfg, secMac);

            var macReq = BuildWiredFeatureRequest(CmdWriteConfig, 0, 1, 0, ConfigLength, macCfg);
            if (!conn.SetFeatureReport(WiredReportId, macReq))
                return new DeviceTuningResult(false, "写入板载配置失败。");

            Thread.Sleep(400);

            byte[]? macVerify = ReadWiredConfigMac(conn);
            if (macVerify == null || !IsValidConfig(macVerify))
                return new DeviceTuningResult(false, "回读板载配置失败，无法确认写入结果。");
            if (macVerify[SleepOffset] != macCfg[SleepOffset])
                return new DeviceTuningResult(false, "回读校验失败：休眠时间未正确写入。");

            return new DeviceTuningResult(true, "已写入 LEOBOG Hi98C Pro 板载配置（回读校验通过）。",
                QueryCapabilities(deviceId));
        }

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
        // 固定 20B 无线帧：前 5B 为包头，byte[4] 低 4 位为有效载荷长度 (0..14)，忽略超出 20B 的缓冲区尾字节。
        if (pkt.Length < WirelessPacketLength || pkt[0] != WirelessReportId)
            return [];
        int len = Math.Min(pkt[4] & 0x0F, 14);
        if (len <= 0 || pkt.Length < 5 + len)
            return [];
        return pkt.Slice(5, len).ToArray();
    }

    internal static int? ParseWirelessBatteryPayload(ReadOnlySpan<byte> payload)
        => payload.Length > 0 && payload[0] <= 100 ? payload[0] : null;

    internal static byte[] BuildWirelessConfigReadPacket(int sub)
        => BuildWirelessReadPacket(CmdWirelessConfig, (byte)sub);

    /// <summary>按 packetIndex (pkt[3]) 将 0x44 分包拼接为 128B 板载配置。</summary>
    internal static byte[]? AssembleWirelessConfig(IEnumerable<byte[]> packets)
    {
        var byIndex = new SortedDictionary<int, byte[]>();
        var fallbackSeq = new List<byte[]>();
        int expectedTotal = 0;

        foreach (var pkt in packets)
        {
            if (pkt == null || !ParseWirelessPacket(pkt, out var payload)) continue;
            if (payload.Length == 0) continue;

            int total = pkt[2] & 0x7F;
            int idx = pkt[3];
            if (total > 0)
            {
                expectedTotal = Math.Max(expectedTotal, total);
                if (idx < total)
                    byIndex[idx] = payload;
            }
            else
            {
                fallbackSeq.Add(payload);
            }
        }

        var cfg = new byte[ConfigLength];
        int filled = 0;
        IEnumerable<byte[]> ordered = expectedTotal > 0 ? byIndex.Values : fallbackSeq;
        foreach (var chunk in ordered)
        {
            int n = Math.Min(chunk.Length, ConfigLength - filled);
            if (n <= 0) break;
            Array.Copy(chunk, 0, cfg, filled, n);
            filled += n;
            if (filled >= ConfigLength) break;
        }
        return filled == ConfigLength && IsValidConfig(cfg) ? cfg : null;
    }

    private static int? ProbeWirelessBatteryMac(IEnumerable<MacHidInterfaceInfo> group, out bool permissionDenied)
    {
        permissionDenied = false;
        var request = BuildWirelessReadPacket(CmdBattery);
        byte expectedReplyCmd = (byte)(CmdBattery | 0x40);

        foreach (var dev in SelectWirelessMacInterfaces(group))
        {
            using var conn = MacIOKitHid.MacHidConnection.TryOpen(dev, out int openRet);
            if (conn == null)
            {
                if (openRet == MacIOKitHid.kIOReturnNotPermitted)
                    permissionDenied = true;
                continue;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                conn.DrainInputReports();
                if (!conn.WriteOutputReport(WirelessReportId, request))
                    continue;
                var reply = conn.ReadInputReport(450, pkt => pkt.Length >= WirelessPacketLength
                    && pkt[0] == WirelessReportId
                    && pkt[1] == expectedReplyCmd
                    && ParseWirelessPacket(pkt));
                if (reply != null && ParseWirelessPacket(reply, out var payload) && ParseWirelessBatteryPayload(payload) is { } pct)
                    return pct;
                Thread.Sleep(25);
            }
        }
        return null;
    }

    private static byte[]? ReadWirelessConfigMac(IEnumerable<MacHidInterfaceInfo> group, out bool permissionDenied)
    {
        permissionDenied = false;
        var request = BuildWirelessConfigReadPacket(0);

        foreach (var dev in SelectWirelessMacInterfaces(group))
        {
            using var conn = MacIOKitHid.MacHidConnection.TryOpen(dev, out int openRet);
            if (conn == null)
            {
                if (openRet == MacIOKitHid.kIOReturnNotPermitted)
                    permissionDenied = true;
                continue;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                conn.DrainInputReports();
                if (!conn.WriteOutputReport(WirelessReportId, request))
                    continue;

                var byIndex = new Dictionary<int, byte[]>();
                int? expectedTotal = null;
                var collected = conn.CollectInputReports(
                    1100,
                    pkt => pkt.Length >= WirelessPacketLength && pkt[0] == WirelessReportId && pkt[1] == CmdWirelessConfig && ParseWirelessPacket(pkt),
                    pkts =>
                    {
                        var last = pkts[^1];
                        int total = last[2] & 0x7F;
                        int idx = last[3];
                        if (total > 0)
                        {
                            expectedTotal = total;
                            byIndex[idx] = last;
                            return byIndex.Count >= total;
                        }
                        return false;
                    });

                if (AssembleWirelessConfig(collected) is { } cfg)
                    return cfg;
                Thread.Sleep(35);
            }
        }
        return null;
    }

    private static int? ProbeWirelessBattery(IEnumerable<HidDevice> group)
    {
        var request = BuildWirelessReadPacket(CmdBattery);
        byte expectedReplyCmd = (byte)(CmdBattery | 0x40);
        var candidates = group
            .Where(d => HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00 && HidSafe.MaxOutputLen(d) >= 19)
            .OrderByDescending(d => HidSafe.PrimaryUsage(d) == (0xFF02, 0x0002) ? 1 : 0)
            .ToList();
        if (candidates.Count == 0)
            candidates = group.ToList();

        foreach (var dev in candidates)
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                for (int i = 0; i < 4; i++)
                    if (ch.Read(5) == null) break;

                if (ch.OutputLength >= WirelessPacketLength && ch.WriteOutput(request))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 450)
                    {
                        var reply = ch.Read(100);
                        if (reply == null) break;
                        if (reply.Length >= WirelessPacketLength && reply[0] == WirelessReportId && reply[1] == expectedReplyCmd &&
                            ParseWirelessPacket(reply, out var payload) && ParseWirelessBatteryPayload(payload) is { } pct)
                            return pct;
                    }
                }
            }

            if (ch.FeatureLength >= WirelessPacketLength && ch.WriteFeature(request))
            {
                Thread.Sleep(45);
                var reply = ch.ReadFeature(WirelessReportId);
                if (reply != null && ParseWirelessPacket(reply, out var payload) && ParseWirelessBatteryPayload(payload) is { } pct)
                    return pct;
            }
        }
        return null;
    }

    private static byte[]? ReadWirelessConfig(IEnumerable<HidDevice> group)
    {
        var request = BuildWirelessConfigReadPacket(0);
        var candidates = group
            .Where(d => HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00 && HidSafe.MaxOutputLen(d) >= 19)
            .OrderByDescending(d => HidSafe.PrimaryUsage(d) == (0xFF02, 0x0002) ? 1 : 0)
            .ToList();
        if (candidates.Count == 0)
            candidates = group.ToList();

        foreach (var dev in candidates)
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                for (int i = 0; i < 4; i++)
                    if (ch.Read(5) == null) break;

                if (ch.OutputLength >= WirelessPacketLength && ch.WriteOutput(request))
                {
                    var packets = new Dictionary<int, byte[]>();
                    int expectedTotal = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 1100)
                    {
                        var reply = ch.Read(120);
                        if (reply == null) continue;
                        if (reply.Length < WirelessPacketLength || reply[0] != WirelessReportId || reply[1] != CmdWirelessConfig)
                            continue;
                        if (!ParseWirelessPacket(reply)) continue;
                        int total = reply[2] & 0x7F;
                        if (total > 0)
                        {
                            expectedTotal = total;
                            packets[reply[3]] = reply;
                            if (packets.Count >= expectedTotal) break;
                        }
                    }
                    if (AssembleWirelessConfig(packets.Values) is { } cfg)
                        return cfg;
                }
            }
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

    internal static bool IsLeobogFa09Dongle()
    {
        if (OperatingSystem.IsMacOS())
        {
            var macIfaces = MacIOKitHid.Enumerate(0x3554, 0xFA09);
            return IsLeobogFa09Dongle(macIfaces);
        }

        var devs = HidSafe.EnumerateDevices(0x3554, 0xFA09);
        if (devs.Count == 0) return false;
        if (devs.Any(x => HidSafe.ProductName(x).Contains("AULA", StringComparison.OrdinalIgnoreCase))) return false;
        if (devs.Any(x => HidSafe.ProductName(x).Contains("LEOBOG", StringComparison.OrdinalIgnoreCase) || HidSafe.ProductName(x).Contains("HI98", StringComparison.OrdinalIgnoreCase))) return true;
        if (devs.Any(x => HidSafe.PrimaryUsage(x) == (0x0001, 0x0002))) return false;
        if (HidSafe.EnumerateDevices(0x258A, 0x010C).Count > 0) return true;
        return devs.Any(x => HidSafe.PrimaryUsage(x) == (0x0001, 0x0006));
    }

    internal static bool IsLeobogFa09Dongle(IReadOnlyList<MacHidInterfaceInfo> macIfaces)
    {
        if (macIfaces.Count == 0) return false;
        if (macIfaces.Any(i => i.Product.Contains("AULA", StringComparison.OrdinalIgnoreCase))) return false;
        if (macIfaces.Any(i => i.Product.Contains("LEOBOG", StringComparison.OrdinalIgnoreCase) || i.Product.Contains("HI98", StringComparison.OrdinalIgnoreCase))) return true;
        // 无线接收器配置接口的 DeviceUsagePairs 可能包含合成 Mouse/Consumer 集合，需按 PrimaryUsage 区分主鼠标接收器
        if (macIfaces.Any(i => i.PrimaryUsagePage == 0x0001 && i.PrimaryUsage == 0x0002)) return false;
        return macIfaces.Any(i => (i.PrimaryUsagePage == 0x0001 && i.PrimaryUsage == 0x0006)
            || (i.PrimaryUsagePage >= 0xFF00 && i.MaxOutputReportSize >= 19));
    }

    private static bool IsLeobogMacInterface(MacHidInterfaceInfo d)
    {
        string upper = d.Product.ToUpperInvariant();
        if (d.VendorId == 0x258A && d.ProductId == 0x010C) return true;
        if (upper.Contains("AULA")) return false;
        if (upper.Contains("LEOBOG") || upper.Contains("HI98")) return true;
        if (d.VendorId == 0x3554 && d.ProductId == 0xFA07) return true;
        if (d.VendorId == 0x3554 && d.ProductId == 0xFA09)
            return IsLeobogFa09Dongle();
        return false;
    }

    private static List<MacHidInterfaceInfo> SelectWirelessMacInterfaces(IEnumerable<MacHidInterfaceInfo> group)
    {
        return group
            .Where(d => (d.PrimaryUsagePage >= 0xFF00 || d.HasUsage(0xFF02, 0x0002)) && d.MaxOutputReportSize >= 19)
            .OrderByDescending(d => d.PrimaryUsagePage == 0xFF02 && d.PrimaryUsage == 0x0002 ? 1 : 0)
            .ThenByDescending(d => d.MaxOutputReportSize)
            .ToList();
    }

    private static MacHidInterfaceInfo? FindWiredMacInterface(IEnumerable<MacHidInterfaceInfo> devs)
        => devs.Where(d => d.MaxFeatureReportSize >= WiredReportLength || d.PrimaryUsagePage >= 0xFF00)
            .OrderByDescending(d => d.MaxFeatureReportSize >= WiredReportLength ? 1 : 0)
            .ThenByDescending(d => d.PrimaryUsagePage >= 0xFF00 ? 1 : 0)
            .FirstOrDefault();

    private static string? ReadWiredModelMac(MacIOKitHid.MacHidConnection conn)
    {
        var request = BuildWiredFeatureRequest(CmdModel, 0x01, 1, 0, 6);
        if (!conn.SetFeatureReport(WiredReportId, request)) return null;
        Thread.Sleep(30);
        var reply = conn.GetFeatureReport(WiredReportId, WiredReportLength);
        var payload = reply == null ? null : ExtractWiredReplyPayload(reply, 6, CmdModel);
        if (payload == null || payload.Length < 6) return null;
        byte modelId = payload[5];
        return modelId != 0 ? $"LEOBOG Hi98C Pro (0x{modelId:X2})" : null;
    }

    private static byte[]? ReadWiredConfigMac(MacIOKitHid.MacHidConnection conn)
    {
        var request = BuildWiredFeatureRequest(CmdWiredConfig, 0, 1, 0, ConfigLength);
        if (!conn.SetFeatureReport(WiredReportId, request)) return null;
        Thread.Sleep(30);
        var reply = conn.GetFeatureReport(WiredReportId, WiredReportLength);
        if (reply == null) return null;
        var payload = ExtractWiredReplyPayload(reply, ConfigLength, CmdWiredConfig);
        return payload != null && IsValidConfig(payload) ? payload : null;
    }

    private static bool IsLeobogDevice(HidDevice d)
    {
        string upper = HidSafe.ProductName(d).ToUpperInvariant();
        if (d.VendorID == 0x258A && d.ProductID == 0x010C) return true;
        if (upper.Contains("AULA")) return false;
        if (upper.Contains("LEOBOG") || upper.Contains("HI98")) return true;
        if (d.VendorID == 0x3554 && d.ProductID == 0xFA07) return true;
        if (d.VendorID == 0x3554 && d.ProductID == 0xFA09)
            return IsLeobogFa09Dongle();
        return false;
    }

    private static HidDevice? FindWiredInterface(IEnumerable<HidDevice> devs)
        => devs.FirstOrDefault(d => HidSafe.MaxFeatureLen(d) >= WiredReportLength
            && HidReportInspector.GetReportIds(d).FeatureIds.Contains(WiredReportId));

    private static bool IsValidConfig(byte[] cfg)
        => cfg.Length >= ConfigLength && cfg[126] == ConfigTail0 && cfg[127] == ConfigTail1;

    internal static string MapOsMode(byte osMode) => osMode switch
    {
        0 => "Windows",
        1 => "Android",
        2 => "macOS",
        _ => $"0x{osMode:X2}",
    };

    private static string GetEffectName(byte effectId) => effectId switch
    {
        0 => "关闭背光",
        1 => "常亮",
        2 => "呼吸",
        3 => "七彩渐变",
        4 => "行涟漪",
        5 => "雨滴落雨",
        6 => "霓虹慢波",
        7 => "全盘涟漪",
        8 => "繁星点点",
        10 => "贪吃蛇",
        11 => "随波逐流",
        12 => "单点即灭",
        13 => "中心波浪",
        15 => "彩虹轮盘",
        16 => "瀑布流光",
        17 => "中心快闪",
        21 or 32 => "单键自定义",
        _ => $"0x{effectId:X2}",
    };

    private static string BuildConfigSummary(string? model, byte[] cfg, bool wired)
    {
        string head = string.IsNullOrWhiteSpace(model) ? "LEOBOG Hi98C Pro" : model!;
        string lockText = cfg[WinLockOffset] != 0 ? "锁定" : "未锁定";
        string osText = MapOsMode(cfg[OsModeOffset]);
        string effectText = GetEffectName(cfg[LedEffectOffset]);
        string sideText = cfg.Length > 0x12 ? $" · 侧灯 {cfg[0x12]}" : "";
        string mode = wired ? "USB-C 有线（可写）" : "无线只读";
        return $"{head} 板载配置：休眠 {cfg[SleepOffset] * 30}s · WinLock {lockText} · 系统模式 {osText} · 灯效 {effectText}{sideText}（{mode}）。";
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
