using System.Globalization;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 飞智 Flydigi 手柄与充电底座协议驱动：
/// - 新一代 VID 0x37D7 (`0x5A 0xA5`)：覆盖 八爪鱼 5 (PID 0x2501)、八爪鱼 6 / 6 Pro (PID 0x2502)、黑武士 5 Pro (PID 0x2401)；
///   支持电量/充电状态、板载 1–4 号配置切换 (`0xA1/0xA2`) 及 ForceAdapt 双扳机反馈模式切换 (`0x51`)；
/// - 经典 DInput 协议 (`04B4:2412` / `05 EC` / `05 50 05`)：覆盖 八爪鱼 4 / 黑武士 4 Pro；
/// - 飞智幻影充电底座 CD2 (`37D7:6001`) 连接状态识别。
/// </summary>
public sealed class FlydigiProvider : ITunablePeripheralProvider
{
    public const string ProviderName = "Flydigi";
    public const ushort FlydigiVid = 0x37D7;
    public const ushort DockCd2Pid = 0x6001;
    public const ushort Apex5Pid = 0x2501;
    public const ushort Apex6Pid = 0x2502;
    public const ushort Vader5Pid = 0x2401;
    public const ushort LegacyDInputVid = 0x04B4;
    public const ushort LegacyDInputPid = 0x2412;

    internal enum GenVariant { Apex6V3, Apex5V2, ClassicDInput }

    internal sealed record FlydigiInfoParsed(
        GenVariant Variant,
        byte DeviceType,
        string ModelName,
        int? BatteryPercent,
        BatteryState ChargeState,
        string Transport);

    public string Name => ProviderName;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("flydigi:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var all = HidSafe.EnumerateDevices();

        bool hasAwakeController = false;
        bool hasCd2Dock = false;

        var flydigiGroups = all
            .Where(d => d.VendorID == FlydigiVid || (d.VendorID == LegacyDInputVid && d.ProductID == LegacyDInputPid))
            .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID));

        foreach (var g in flydigiGroups)
        {
            var (vid, pid) = g.Key;
            if (vid == FlydigiVid && pid == DockCd2Pid)
            {
                hasCd2Dock = true;
                continue;
            }

            if (vid == FlydigiVid && (pid >> 12) != 2)
                continue;

            try
            {
                var info = ProbeGroupInfo(g, vid, pid);
                string cleanName = info?.ModelName ?? GuessModelByPid(vid, pid);
                bool sleeping = info == null;
                if (!sleeping) hasAwakeController = true;

                results.Add(new PeripheralDevice
                {
                    Id = $"flydigi:{vid:X4}-{pid:X4}",
                    VendorId = vid,
                    ProductId = pid,
                    Name = cleanName,
                    Kind = DeviceKind.Controller,
                    Provider = ProviderName,
                    Connected = true,
                    IsSleeping = sleeping,
                    Transport = info?.Transport ?? "2.4G / USB",
                    BatteryPercent = info?.BatteryPercent,
                    State = info?.ChargeState ?? BatteryState.Unknown,
                    Note = sleeping ? "接收器已连接（手柄关机或休眠中，唤醒手柄即可读取电量与切换板载配置）" : null,
                    LastUpdated = DateTimeOffset.Now,
                });
            }
            catch (Exception ex)
            {
                Diag.Log($"Flydigi {vid:X4}:{pid:X4}: {ex.Message}");
            }
        }

        if (hasCd2Dock && !hasAwakeController)
        {
            results.Add(new PeripheralDevice
            {
                Id = $"flydigi:{FlydigiVid:X4}-{DockCd2Pid:X4}",
                VendorId = FlydigiVid,
                ProductId = DockCd2Pid,
                Name = "飞智充电底座 CD2",
                Kind = DeviceKind.Controller,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = true,
                Transport = "USB",
                BatteryPercent = null,
                State = BatteryState.Unknown,
                Note = "底座已连接（将接收器/手柄开机连线后可读取八爪鱼 5 / 6 Pro 电量与配置）",
                LastUpdated = DateTimeOffset.Now,
            });
        }

        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
        {
            caps.StatusMessage = "无效的飞智设备 ID。";
            return caps;
        }

        if (vid == FlydigiVid && pid == DockCd2Pid)
        {
            caps.IsOnline = false;
            caps.StatusMessage = "当前检测到的是飞智 USB 充电底座（CD2）；请开启八爪鱼 5 / 6 Pro 手柄或将接收器/数据线直连电脑后再调节参数。";
            return caps;
        }

        var devs = HidSafe.EnumerateDevices(vid, pid);
        var ch = OpenVendorInterface(devs, vid, pid);
        if (ch == null)
        {
            caps.IsOnline = false;
            caps.StatusMessage = "未找到飞智 0xFFA0 HID 控制接口（手柄关机时部分接收器不会开放配置通道）。";
            return caps;
        }

        using (ch)
        {
            var info = QueryInfo(ch, vid, pid);
            if (info == null)
            {
                caps.IsOnline = false;
                caps.StatusMessage = "手柄当前处于休眠/关机状态，请按手柄按键唤醒后再点击「读取当前硬件参数」。";
                return caps;
            }

            caps.IsOnline = true;
            caps.OnboardProfileCount = 4;
            caps.OnboardProfileNames = ["板载配置 1", "板载配置 2", "板载配置 3", "板载配置 4"];
            if (info.Variant is GenVariant.Apex6V3 or GenVariant.Apex5V2)
            {
                int? slot = QueryProfileSlot(ch, info.Variant);
                if (slot != null) caps.ActiveOnboardProfile = slot.Value;
            }

            if (info.Variant == GenVariant.Apex5V2 || info.DeviceType is 84 or 86 or 87 or 92 or 93 or 102 or 103 or 104)
            {
                caps.SupportedPerformanceModes =
                [
                    new(0, "常规线性扳机", "关闭力反馈阻尼，保留顺滑原生线性手感"),
                    new(1, "赛车恒定阻尼", "模拟油门/刹车踏板连续阻尼感"),
                    new(2, "狙击清脆段落", "模拟开火前临界阻力与击发瞬间突破感"),
                    new(3, "机枪后坐震动", "按压触发脉冲式震动反馈"),
                    new(4, "微动快速锁死", "短行程瞬间触发微动式急停（适合 FPS 急停开镜）"),
                ];
            }

            caps.StatusMessage = $"已连接「{info.ModelName}」（{info.Transport}）· 支持 1~4 号板载配置实时切换" +
                                 (caps.SupportedPerformanceModes.Count > 0 ? "与力反馈扳机模式" : "");
            return caps;
        }
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new DeviceTuningResult(false, "无效的飞智设备 ID。");

        if (vid == FlydigiVid && pid == DockCd2Pid)
            return new DeviceTuningResult(false, "充电底座本身无硬件配置文件，请开启手柄电源后再试。");

        var devs = HidSafe.EnumerateDevices(vid, pid);
        var ch = OpenVendorInterface(devs, vid, pid);
        if (ch == null)
            return new DeviceTuningResult(false, "无法打开飞智 HID 控制通道。");

        using (ch)
        {
            var info = QueryInfo(ch, vid, pid);
            if (info == null)
                return new DeviceTuningResult(false, "手柄未在线或处于休眠状态。");

            var errors = new List<string>();
            if (update.ActiveOnboardProfile is { } targetSlot)
            {
                int clamped = Math.Clamp(targetSlot, 0, 3);
                bool ok = WriteProfileSlot(ch, info.Variant, (byte)clamped);
                if (!ok) errors.Add("切换手柄板载配置失败");
            }

            if (update.PerformanceMode is { } triggerMode && info.Variant == GenVariant.Apex5V2)
            {
                byte outRid = ResolveOutputReportId(ch, devs);
                var (leftPkt, rightPkt) = BuildApex5TriggerPresetPair(outRid, triggerMode, ch.OutputLength);
                while (ch.Read(3) != null) { }
                bool okL = ch.WriteOutput(leftPkt);
                Thread.Sleep(15);
                bool okR = ch.WriteOutput(rightPkt);
                _ = ch.Read(100);
                if (!okL || !okR)
                    errors.Add("力反馈扳机模式写入失败");
            }

            Thread.Sleep(50);
            var refreshed = QueryCapabilities(deviceId);
            return errors.Count == 0
                ? new DeviceTuningResult(true, "已写入飞智手柄配置。", refreshed)
                : new DeviceTuningResult(false, string.Join("；", errors), refreshed);
        }
    }

    private static HidChannel? OpenVendorInterface(IEnumerable<HidDevice> devs, ushort vid, ushort pid)
    {
        var sorted = devs
            .Where(d => HidSafe.MaxOutputLen(d) >= 12)
            .OrderByDescending(d => HidSafe.PrimaryUsage(d).UsagePage == 0xFFA0 ? 2 : 0)
            .ThenByDescending(d => HidSafe.MaxOutputLen(d) is 32 or 33 or 63 or 64 ? 1 : 0)
            .ToList();

        foreach (var d in sorted)
        {
            var ch = HidChannel.TryOpen(d);
            if (ch != null) return ch;
        }
        return null;
    }

    private static FlydigiInfoParsed? ProbeGroupInfo(IEnumerable<HidDevice> group, ushort vid, ushort pid)
    {
        using var ch = OpenVendorInterface(group, vid, pid);
        return ch == null ? null : QueryInfo(ch, vid, pid);
    }

    private static FlydigiInfoParsed? QueryInfo(HidChannel ch, ushort vid, ushort pid)
    {
        for (int i = 0; i < 6; i++)
            if (ch.Read(5) == null) break;

        if (vid == LegacyDInputVid && pid == LegacyDInputPid)
        {
            var req = new byte[Math.Max(ch.OutputLength, 12)];
            req[0] = 0x05;
            req[1] = 0xEC;
            if (!ch.WriteOutput(req)) return null;
            var deadline = Environment.TickCount64 + 350;
            while (Environment.TickCount64 < deadline)
            {
                var r = ch.Read(70);
                if (r is { Length: >= 16 } && r[15] == 236)
                    return ParseClassicInfoReply(r);
            }
            return null;
        }

        // Try 33B Apex6 V3 / 32B Apex5 V2
        if (pid == Apex6Pid || ch.OutputLength >= 33)
        {
            byte[] req6 = BuildApex6Command(0x01, []);
            if (ch.WriteOutput(req6))
            {
                var d = Environment.TickCount64 + 350;
                while (Environment.TickCount64 < d)
                {
                    var r = ch.Read(70);
                    if (r != null && ParseApex6InfoReply(r) is { } a6)
                        return a6;
                }
            }
        }

        foreach (byte outRepId in ch.OutputLength >= 33 ? new byte[] { 0x00, 0x03 } : new byte[] { 0x03, 0x00 })
        {
            byte[] req5 = BuildApex5Command(outRepId, 0x01, []);
            if (ch.OutputLength > req5.Length) Array.Resize(ref req5, ch.OutputLength);
            if (!ch.WriteOutput(req5)) continue;
            var d = Environment.TickCount64 + 320;
            while (Environment.TickCount64 < d)
            {
                var r = ch.Read(70);
                if (r == null) continue;
                if (ParseApex6InfoReply(r) is { } a6) return a6;
                if (ParseApex5InfoReply(r) is { } a5) return a5;
            }
        }
        return null;
    }

    private static int? QueryProfileSlot(HidChannel ch, GenVariant variant)
    {
        for (int i = 0; i < 4; i++)
            if (ch.Read(5) == null) break;

        byte[] req = variant == GenVariant.Apex6V3
            ? BuildApex6Command(0xA1, [])
            : BuildApex5Command((byte)(ch.OutputLength >= 33 ? 0x00 : 0x03), 0xA1, []);
        if (ch.OutputLength > req.Length) Array.Resize(ref req, ch.OutputLength);
        if (!ch.WriteOutput(req)) return null;

        var d = Environment.TickCount64 + 320;
        while (Environment.TickCount64 < d)
        {
            var r = ch.Read(70);
            if (r == null) continue;
            var slot = ParseProfileStatusReply(r);
            if (slot != null) return slot;
        }
        return null;
    }

    private static bool WriteProfileSlot(HidChannel ch, GenVariant variant, byte slot)
    {
        byte[] req = variant switch
        {
            GenVariant.Apex6V3 => BuildApex6Command(0xA2, [slot]),
            GenVariant.Apex5V2 => BuildApex5Command((byte)(ch.OutputLength >= 33 ? 0x00 : 0x03), 0xA2, [slot]),
            _ => BuildClassicProfileSwitch(slot),
        };
        if (ch.OutputLength > req.Length) Array.Resize(ref req, ch.OutputLength);
        return ch.WriteOutput(req);
    }

    private static byte[] BuildClassicProfileSwitch(byte slot)
    {
        var req = new byte[12];
        req[0] = 0x05; req[1] = 0x50; req[2] = 0x05; req[3] = slot;
        return req;
    }

    private static byte ResolveOutputReportId(HidChannel ch, IEnumerable<HidDevice> devs)
    {
        foreach (var d in devs.Where(x => HidSafe.PrimaryUsage(x).UsagePage == 0xFFA0))
        {
            var ids = HidReportInspector.GetReportIds(d);
            if (ids.OutputIds.Contains((byte)0x03)) return 0x03;
            if (ids.OutputIds.Contains((byte)0x06)) return 0x06;
            if (ids.OutputIds.Contains((byte)0x00)) return 0x00;
        }
        return (byte)(ch.OutputLength >= 33 ? 0x00 : 0x03);
    }

    internal static (byte[] LeftPacket, byte[] RightPacket) BuildApex5TriggerPresetPair(byte repId, int preset, int outputLen = 32)
    {
        return (BuildApex5SingleTriggerPacket(repId, 1, preset, outputLen),
                BuildApex5SingleTriggerPacket(repId, 2, preset, outputLen));
    }

    private static byte[] BuildApex5SingleTriggerPacket(byte repId, byte side, int preset, int outputLen)
    {
        // OpenFlydigi PROTOCOL.md §3a:
        // side=3 (Both) 被 Apex 5 固件静默忽略，必须为 Left(1) 和 Right(2) 分别下发！
        // [0]=repId, [1]=0x5A, [2]=0xA5, [3]=81, [4]=10(len), [5]=1(applyFlag), [6]=side, [7]=mode, [8..]=params
        // 注意 Space Station UI 中 Sniper(狙击)=mode 3, Recoil(机枪)=mode 2
        byte[] args = preset switch
        {
            1 => [1, side, 1, 50, 80, 0],            // Race 赛车线性阻尼
            2 => [1, side, 3, 50, 30, 120, 0, 1],    // Sniper 狙击段落突破 (MODE_RECOIL=3)
            3 => [1, side, 2, 45, 25, 140, 22, 1],   // Recoil 机枪后坐震动 (MODE_SNIPER=2)
            4 => [1, side, 4, 55, 255, 1],           // Lock 微动快速锁死 (MODE_LOCK=4)
            _ => [1, side, 0],                       // Normal 常规线性
        };
        var req = new byte[Math.Max(32, outputLen)];
        req[0] = repId;
        req[1] = 0x5A;
        req[2] = 0xA5;
        req[3] = 81;
        req[4] = 10;
        Array.Copy(args, 0, req, 5, args.Length);
        return req;
    }

    // ── Pure Protocol Helpers (Unit-Testable) ────────────────────────────────────
    internal static byte[] BuildApex6Command(byte cmd, ReadOnlySpan<byte> payload)
    {
        var req = new byte[33];
        req[0] = 0x00;
        req[1] = 0x5A;
        req[2] = 0xA5;
        req[3] = cmd;
        int pLen = Math.Min(payload.Length, 27);
        req[4] = (byte)(pLen + 2);
        if (pLen > 0) payload[..pLen].CopyTo(req.AsSpan(5));
        byte sum = 0;
        for (int i = 3; i < 32; i++) sum += req[i];
        req[32] = sum;
        return req;
    }

    internal static byte[] BuildApex5Command(byte reportId, byte cmd, ReadOnlySpan<byte> payload)
    {
        var req = new byte[32];
        req[0] = reportId;
        req[1] = 0x5A;
        req[2] = 0xA5;
        req[3] = cmd;
        int pLen = Math.Min(payload.Length, 25);
        byte total = (byte)(pLen + 2);
        req[4] = total;
        if (pLen > 0) payload[..pLen].CopyTo(req.AsSpan(5));
        byte sum = 0;
        for (int i = 3; i < 3 + total; i++) sum += req[i];
        req[3 + total] = sum;
        return req;
    }

    internal static FlydigiInfoParsed? ParseApex6InfoReply(ReadOnlySpan<byte> report)
    {
        var frame = Slice5AA5Frame(report);
        if (frame.Length < 32 || frame[2] != 0x01) return null;
        byte expected = 0;
        for (int i = 2; i < 31; i++) expected += frame[i];
        if (frame[31] != expected) return null;

        byte devType = frame[5];
        if (devType is not (0x95 or 0x96 or 0x98)) return null;

        byte conn = frame[6];
        byte rawBat = frame[11];
        byte lvl = (byte)(rawBat & 0x0F);
        byte chg = (byte)((rawBat >> 4) & 0x0F);

        int? pct = chg == 2 ? 100
            : (lvl is > 0 and <= 10 ? Math.Max(10, lvl * 10) : (chg == 1 ? 50 : null));
        BatteryState st = chg switch
        {
            1 => BatteryState.Charging,
            2 => BatteryState.FullyCharged,
            _ => BatteryState.Normal,
        };
        return new FlydigiInfoParsed(
            GenVariant.Apex6V3, devType, ResolveDeviceTypeName(devType),
            pct, st, conn == 0 ? "USB" : "2.4G");
    }

    internal static FlydigiInfoParsed? ParseApex5InfoReply(ReadOnlySpan<byte> report)
    {
        var frame = Slice5AA5Frame(report);
        if (frame.Length < 13 || frame[2] != 0x01) return null;

        byte devType = frame[5];
        byte conn = frame[6];
        byte rawBat = frame[11];
        byte chg = (byte)((rawBat >> 4) & 0x0F);
        byte bars = (byte)(rawBat & 0x0F);

        int? pct = chg == 2 ? 100
            : (bars is >= 1 and <= 5 ? bars * 20 : (bars == 6 ? 100 : (chg == 1 ? 80 : null)));
        BatteryState st = chg switch
        {
            1 => BatteryState.Charging,
            2 => BatteryState.FullyCharged,
            _ => BatteryState.Normal,
        };
        return new FlydigiInfoParsed(
            GenVariant.Apex5V2, devType, ResolveDeviceTypeName(devType),
            pct, st, conn == 0 ? "USB" : "2.4G");
    }

    internal static FlydigiInfoParsed? ParseClassicInfoReply(ReadOnlySpan<byte> report)
    {
        if (report.Length < 16 || report[15] != 236) return null;
        byte devType = report[3];
        int? pct = report[11] <= 100 ? report[11] : null;
        bool wired = report[13] == 1;
        return new FlydigiInfoParsed(
            GenVariant.ClassicDInput, devType, ResolveDeviceTypeName(devType),
            pct, BatteryState.Normal, wired ? "USB" : "2.4G");
    }

    internal static int? ParseProfileStatusReply(ReadOnlySpan<byte> report)
    {
        var frame = Slice5AA5Frame(report);
        if (frame.Length < 7 || frame[2] is not (0xA1 or 0xA2)) return null;
        byte raw = frame[5];
        return raw < 8 ? raw % 4 : null;
    }

    private static ReadOnlySpan<byte> Slice5AA5Frame(ReadOnlySpan<byte> report)
    {
        if (report.Length >= 15 && report[0] is 0x00 or 0x04 && report[1] == 0x5A && report[2] == 0xA5)
            return report[1..];
        if (report.Length >= 14 && report[0] == 0x5A && report[1] == 0xA5)
            return report;
        return [];
    }

    internal static string ResolveDeviceTypeName(byte deviceType) => deviceType switch
    {
        0x95 => "飞智八爪鱼 6",
        0x96 => "飞智八爪鱼 6 Pro",
        0x98 => "飞智八爪鱼 6 Pro 影之刃零版",
        128 or 129 or 133 or 134 or 135 or 136 => deviceType == 129 ? "飞智八爪鱼 5 EVA 联名版" : "飞智八爪鱼 5",
        130 or 144 or 145 => "飞智黑武士 5 Pro",
        84 or 86 or 87 or 92 or 93 or 102 or 103 or 104 => deviceType == 86 ? "飞智八爪鱼 4 EVA" : "飞智八爪鱼 4",
        85 or 91 or 105 or 132 or 146 or 147 or 148 => "飞智黑武士 4 Pro",
        80 or 81 => "飞智黑武士 3 Pro",
        24 or 26 or 29 => "飞智八爪鱼 3",
        _ => $"飞智手柄 (ID {deviceType})",
    };

    private static string GuessModelByPid(ushort vid, ushort pid)
    {
        if (vid == LegacyDInputVid) return "飞智八爪鱼 4 / 黑武士 4 (DInput)";
        return pid switch
        {
            Apex6Pid => "飞智八爪鱼 6 Pro",
            Apex5Pid => "飞智八爪鱼 5",
            Vader5Pid => "飞智黑武士 5 Pro",
            DockCd2Pid => "飞智充电底座 CD2",
            _ => $"飞智手柄 {vid:X4}:{pid:X4}",
        };
    }

    private static bool TryParseVidPid(string deviceId, out ushort vid, out ushort pid)
    {
        vid = 0; pid = 0;
        var parts = deviceId.Split(':');
        if (parts.Length < 2) return false;
        var vp = parts[1].Split('-');
        return vp.Length == 2
            && ushort.TryParse(vp[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid)
            && ushort.TryParse(vp[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
    }
}
