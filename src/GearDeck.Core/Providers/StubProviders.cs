using System.Globalization;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 怒喵 Angry Miao 全系（3151:5007 与 AM Infinity .97 0E8D:0703 等）：
/// 兼容 AngryMiaoBatteryMgr 0x05/0xF7 与 powermiao8k 0xF7 双电池协议、
/// GearHub/MU 0xD4/0x54 (DPI+配置) + 0xD3/0x53 (回报率/LOD/修正) + 0xD0/0x50、CompX 65B 与 0x08 协议。
/// </summary>
public sealed class AngryMiaoProvider : ITunablePeripheralProvider
{
    public const string ProviderName = "AngryMiao";
    public string Name => ProviderName;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("am:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevices()
            .Where(IsAngryMiaoDevice)
            .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID));

        foreach (var g in groups)
        {
            try
            {
                var (vid, pid) = g.Key;
                string? devName = g.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

                if (AngryMiaoRaceProtocol.IsRaceVidPid(vid, pid))
                {
                    var probe = AngryMiaoRaceProtocol.ProbeStatusAsyncSafe(g, vid, pid);
                    bool awake = probe?.IsAwake == true;
                    string cleanRaceName = AngryMiaoRaceProtocol.ResolveModelName(vid, pid, devName);
                    string transport = AngryMiaoRaceProtocol.IsDongleVidPid(vid, pid) ? "2.4G" : "USB";

                    string? raceNote = null;
                    if (awake)
                    {
                        if (probe?.DockBatteryPercent is { } dp)
                            raceNote = $"接收器备用电池仓 {dp}%" + (probe.DockCharging ? "⚡" : "");
                    }
                    else
                    {
                        raceNote = probe?.DockBatteryPercent is { } dp
                            ? $"鼠标无线休眠中（接收器备用电池仓 {dp}%，晃动唤醒）"
                            : "鼠标无线休眠中（晃动即可唤醒）";
                    }

                    results.Add(new PeripheralDevice
                    {
                        Id = $"am:{vid:X4}-{pid:X4}",
                        VendorId = vid,
                        ProductId = pid,
                        Name = cleanRaceName,
                        Kind = DeviceKind.Mouse,
                        Provider = ProviderName,
                        Connected = true,
                        IsSleeping = !awake,
                        Transport = transport,
                        BatteryPercent = awake ? probe?.MouseBatteryPercent : null,
                        CaseBatteryPercent = probe?.DockBatteryPercent,
                        CaseCharging = probe?.DockCharging == true,
                        State = awake && probe?.MouseCharging == true ? BatteryState.Charging : BatteryState.Normal,
                        Note = raceNote,
                        LastUpdated = DateTimeOffset.Now,
                    });
                    continue;
                }

                string cleanName = string.IsNullOrWhiteSpace(devName) || devName.Contains("Receiver", StringComparison.OrdinalIgnoreCase)
                    ? $"Angry Miao {vid:X4}:{pid:X4}"
                    : devName.Trim();

                var hit = ProbeGroup(g, vid, pid);
                string? note = hit is { DockBattery: { } dock }
                    ? $"底座电池 {dock}%"
                    : hit == null
                        ? "已连接（有线供电无电池或无线休眠中）"
                        : null;

                results.Add(new PeripheralDevice
                {
                    Id = $"am:{vid:X4}-{pid:X4}",
                    VendorId = vid,
                    ProductId = pid,
                    Name = cleanName,
                    Kind = DeviceKind.Mouse,
                    Provider = ProviderName,
                    Connected = true,
                    IsSleeping = hit == null,
                    Transport = AngryMiaoRaceProtocol.IsDongleVidPid(vid, pid) ? "2.4G" : "USB",
                    BatteryPercent = hit?.Battery,
                    CaseBatteryPercent = hit?.DockBattery,
                    State = hit is { Charging: true } ? BatteryState.Charging : BatteryState.Normal,
                    Note = note,
                    LastUpdated = DateTimeOffset.Now,
                });
            }
            catch (Exception ex)
            {
                Diag.Log($"AngryMiao {g.Key.Item1:X4}:{g.Key.Item2:X4}: {ex.Message}");
            }
        }
        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
        {
            caps.StatusMessage = "无效的怒喵设备 ID。";
            return caps;
        }

        var devs = HidSafe.EnumerateDevices(vid, pid);
        if (devs.Count == 0)
        {
            caps.StatusMessage = "未找到怒喵 HID 设备（请确认接收器或数据线已连接）。";
            return caps;
        }

        if (AngryMiaoRaceProtocol.IsRaceVidPid(vid, pid))
            return AngryMiaoRaceProtocol.QueryCapabilities(deviceId, devs, vid, pid);

        string? rawName = devs.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

        // 1. Try MU / GearHub 0xD4/0xD3 (also handles AngryMiao 0xD4 DPI stages + profiles!)
        var ghCaps = GearHubMuProtocol.QueryCapabilities(deviceId, devs);
        if (ghCaps.IsOnline)
            return ghCaps;

        // 2. Try CompX 65B Page/Cmd
        foreach (var dev in devs.Where(d => HidSafe.MaxFeatureLen(d) >= 64)
                                .OrderBy(d => HidSafe.MaxFeatureLen(d) == 65 ? 0 : 1))
        {
            using var raw = RawHidFeature.TryOpen(dev.DevicePath);
            if (raw == null) continue;
            var res = CompXPageProtocol.QueryCapabilities(deviceId, raw, HidSafe.MaxFeatureLen(dev), true, rawName);
            if (res.IsOnline && (res.SupportsDpi || res.SupportedPollingRates.Count > 0))
                return res;
        }

        // 3. Try Compx 0x08
        foreach (var dev in devs.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17))
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;
            var compx = Compx08Protocol.QueryCapabilities(deviceId, ch, isWireless: true, productName: rawName);
            if (compx.SupportsDpi || compx.SupportedPollingRates.Count > 0)
                return compx;
        }

        bool responsive = ProbeGroup(devs, vid, pid) != null;
        caps.IsOnline = responsive;
        caps.StatusMessage = responsive
            ? "怒喵设备已识别，但未在 0xD4/CompX/0x08 寄存器接口应答调参帧（可晃动鼠标唤醒后重试）。"
            : "怒喵设备当前处于休眠或未唤醒状态，请晃动鼠标唤醒后再试。";
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new DeviceTuningResult(false, "无效的怒喵设备 ID。");

        var devs = HidSafe.EnumerateDevices(vid, pid);
        if (devs.Count == 0)
            return new DeviceTuningResult(false, "未找到怒喵 HID 设备。");

        if (AngryMiaoRaceProtocol.IsRaceVidPid(vid, pid))
            return AngryMiaoRaceProtocol.ApplySettings(deviceId, devs, vid, pid, update);

        string? rawName = devs.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

        var ghRes = GearHubMuProtocol.ApplySettings(deviceId, devs, update);
        if (ghRes.Success) return ghRes;

        foreach (var dev in devs.Where(d => HidSafe.MaxFeatureLen(d) >= 64)
                                .OrderBy(d => HidSafe.MaxFeatureLen(d) == 65 ? 0 : 1))
        {
            int fl = HidSafe.MaxFeatureLen(dev);
            using var raw = RawHidFeature.TryOpen(dev.DevicePath);
            if (raw == null) continue;
            if (CompXPageProtocol.QueryBattery(raw, fl) != null)
                return CompXPageProtocol.ApplySettings(deviceId, raw, fl, true, rawName, update);
        }

        foreach (var dev in devs.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17))
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;
            var res = Compx08Protocol.ApplySettings(deviceId, ch, update, isWireless: true, productName: rawName);
            if (res.Success) return res;
        }
        return new DeviceTuningResult(false, "怒喵设备未响应写入命令，请确认鼠标处于开机唤醒状态。");
    }

    private static bool IsAngryMiaoDevice(HidDevice d)
    {
        ushort vid = (ushort)d.VendorID;
        ushort pid = (ushort)d.ProductID;
        if (AngryMiaoRaceProtocol.IsKnownAngryMiaoVidPid(vid, pid))
            return true;
        string name = HidSafe.ProductName(d);
        return name.Contains("Angry Miao", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AM Infinity", StringComparison.OrdinalIgnoreCase);
    }

    private static (int Battery, bool Charging, int? DockBattery)? ProbeGroup(IEnumerable<HidDevice> group, ushort vid, ushort pid)
    {
        var list = group.OrderByDescending(d => Math.Max(HidSafe.MaxFeatureLen(d), HidSafe.MaxOutputLen(d))).ToList();
        foreach (var iface in list)
        {
            int featLen = HidSafe.MaxFeatureLen(iface);
            int outLen = HidSafe.MaxOutputLen(iface);
            if (featLen < 17 && outLen < 17) continue;

            var ids = HidReportInspector.GetReportIds(iface);
            if (featLen >= 60)
            {
                var featCandidateIds = ids.FeatureIds.Concat(new byte[] { 0x00, 0xF7, 0x05 }).Distinct().ToList();
                using (var raw = RawHidFeature.TryOpen(iface.DevicePath))
                {
                    if (raw != null)
                    {
                        foreach (byte setRepId in featCandidateIds)
                        {
                            var req = new byte[featLen];
                            req[0] = setRepId;
                            req[1] = 0xF7;
                            if (raw.SetFeature(req))
                            {
                                Thread.Sleep(45);
                                foreach (byte getRepId in new byte[] { 0xF7, 0x05, setRepId }.Distinct())
                                {
                                    var reply = raw.GetFeature(featLen, getRepId);
                                    if (reply == null) continue;
                                    if (ParseBatteryReply(reply) is { } hit) return hit;
                                }
                            }
                        }
                    }
                }
            }

            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;

            if (outLen >= 60 && HidSafe.MaxInputLen(iface) >= 12)
            {
                foreach (byte outId in ids.OutputIds.Concat(new byte[] { 0xF7, 0x00 }).Distinct())
                {
                    var req = new byte[outLen];
                    req[0] = outId;
                    req[1] = 0xF7;
                    if (!ch.WriteOutput(req)) continue;
                    var reply = ch.Read(140);
                    if (reply != null && ParseBatteryReply(reply) is { } hit) return hit;
                }
            }

            if (Compx08Protocol.QueryBattery(ch, 0x08, 450) is { } c08)
                return (c08.Level, c08.Charging, null);
        }
        return null;
    }

    internal static (int Battery, bool Charging, int? DockBattery)? ParseBatteryReply(byte[] reply)
    {
        if (reply.Length < 12) return null;

        if (reply[0] == 5 && reply[3] is >= 1 and <= 100)
        {
            int? dock = reply[11] is >= 0 and <= 100 ? reply[11] : null;
            return (reply[3], reply[1] == 1, dock);
        }

        if (reply[0] == 0xF7 && reply[2] is >= 1 and <= 100)
        {
            int? dock = reply[10] is >= 1 and <= 100 ? reply[10] : null;
            return (reply[2], reply[1] == 1, dock);
        }

        if (reply[1] == 0xF7 && reply[3] is >= 1 and <= 100)
        {
            int? dock = reply[11] is >= 1 and <= 100 ? reply[11] : null;
            return (reply[3], reply[2] == 1, dock);
        }

        if (CompXProvider.ParseFrame(reply) is { } compx)
            return (compx.Battery, compx.Charging, null);

        return null;
    }

    private static bool TryParseVidPid(string id, out ushort vid, out ushort pid)
    {
        vid = 0; pid = 0;
        var p = id.Split(':');
        if (p.Length < 2) return false;
        var vp = p[1].Split('-');
        return vp.Length == 2
            && ushort.TryParse(vp[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid)
            && ushort.TryParse(vp[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
    }
}
