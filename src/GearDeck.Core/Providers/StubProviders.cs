using System.Globalization;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>ROG 键盘/耳机/接收器（0B05）。使用 HidSafe 隔离异常接口。</summary>
public sealed class RogProvider : IPeripheralProvider
{
    public const string ProviderName = "ROG";
    public string Name => ProviderName;

    private static readonly (ushort Pid, string Name, int Offset)[] Keyboards =
    [
        (0x1B7A, "ROG Strix Scope II 96 RX", 6),
        (0x1B78, "ROG Strix Scope II 96 RX", 6),
        (0x1AB0, "ROG Strix Scope II 96", 6),
        (0x1AAE, "ROG Strix Scope II 96", 6),
        (0x1B04, "ROG Falchion RX", 11),
        (0x193E, "ROG Falchion", 11),
        (0x193C, "ROG Falchion", 11),
        (0x1A85, "ROG Azoth", 6),
        (0x1A83, "ROG Azoth", 6),
    ];

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        try { ReadHeadsets(results); } catch (Exception ex) { Diag.Log($"ROG headset: {ex.Message}"); }
        try { ReadOmni(results); } catch (Exception ex) { Diag.Log($"ROG omni: {ex.Message}"); }
        try { ReadDirectKeyboards(results); } catch (Exception ex) { Diag.Log($"ROG kb: {ex.Message}"); }
        return results;
    }

    private static void ReadHeadsets(List<PeripheralDevice> results)
    {
        foreach (var (pid, name) in new[] { (0x1AFA, "ROG Delta II"), (0x1B84, "ROG Pelta") })
        {
            var iface = HidSafe.EnumerateDevices(0x0B05, pid)
                .Where(d => HidSafe.MaxInputLen(d) >= 8 && HidSafe.MaxOutputLen(d) >= 8)
                .OrderByDescending(HidSafe.MaxOutputLen)
                .FirstOrDefault();
            if (iface == null) continue;
            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;
            var reply = Query(ch, 0xCC, 0x07);
            if (reply == null) continue;
            Diag.Log($"ROG {pid:X4} headset raw={BitConverter.ToString(reply, 0, Math.Min(16, reply.Length))}");
            int percent = reply[6];
            bool charging = false;
            var charge = Query(ch, 0xCC, 0x08);
            if (charge != null) charging = charge[5] == 1;
            bool valid = percent is >= 0 and <= 100;
            results.Add(new PeripheralDevice
            {
                Id = $"rog:{pid:X4}:hs",
                Name = name,
                Kind = DeviceKind.Headset,
                Provider = ProviderName,
                Connected = true,
                Transport = "2.4G",
                BatteryPercent = valid ? percent : null,
                State = charging ? BatteryState.Charging : BatteryState.Normal,
                Note = valid ? null : "耳机未开机或休眠中",
                LastUpdated = DateTimeOffset.Now,
            });
        }
    }

    private static void ReadOmni(List<PeripheralDevice> results)
    {
        var omni = HidSafe.EnumerateDevices(0x0B05, 0x1ACE)
            .Where(d => HidSafe.MaxOutputLen(d) >= 12)
            .OrderByDescending(HidSafe.MaxOutputLen)
            .ToList();
        if (omni.Count == 0) return;

        var listIface = omni.FirstOrDefault(d => d.DevicePath.Contains("col01", StringComparison.OrdinalIgnoreCase)) ?? omni[0];
        using var listCh = HidChannel.TryOpen(listIface);
        if (listCh == null) return;
        var listReply = Query(listCh, 0x01, 0xA0, 0x00);
        if (listReply == null) return;

        var pids = new List<ushort>();
        for (int at = 5; at + 1 < listReply.Length; at += 4)
        {
            ushort pid = (ushort)(listReply[at] | (listReply[at + 1] << 8));
            if (pid == 0) break;
            pids.Add(pid);
        }
        Diag.Log($"ROG Omni 配对设备: {string.Join(",", pids.Select(p => p.ToString("X4")))}");

        foreach (var pid in pids)
        {
            var model = Keyboards.FirstOrDefault(k => k.Pid == pid);
            int offset = model.Pid == 0 ? 6 : model.Offset;
            var iface = omni.FirstOrDefault(d => d.DevicePath.Contains("col02", StringComparison.OrdinalIgnoreCase)) ?? omni[0];
            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;
            var reply = Query(ch, 0x02, 0x01);
            if (reply == null) continue;
            int percent = reply.Length > offset ? reply[offset] : 0;
            if (percent is < 0 or > 100 || percent == 0)
            {
                percent = reply.Length > 11 ? reply[11] : 0;
                if (percent is < 0 or > 100 || percent == 0) continue;
            }
            results.Add(new PeripheralDevice
            {
                Id = $"rog:{pid:X4}:kb",
                Name = model.Name ?? $"ROG keyboard {pid:X4}",
                Kind = DeviceKind.Keyboard,
                Provider = ProviderName,
                Connected = true,
                Transport = "2.4G",
                BatteryPercent = percent,
                State = reply.Length > 9 && reply[9] == 1 ? BatteryState.Charging : BatteryState.Normal,
                LastUpdated = DateTimeOffset.Now,
            });
        }
    }

    private static void ReadDirectKeyboards(List<PeripheralDevice> results)
    {
        foreach (var (pid, name, offset) in Keyboards)
        {
            if (results.Any(r => r.Id.Contains(pid.ToString("X4")))) continue;
            var iface = HidSafe.EnumerateDevices(0x0B05, pid)
                .Where(d => HidSafe.MaxOutputLen(d) >= 12)
                .OrderByDescending(HidSafe.MaxOutputLen)
                .FirstOrDefault();
            if (iface == null) continue;
            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;
            var reply = Query(ch, 0x00, 0x01);
            if (reply == null) continue;
            int percent = reply.Length > offset ? reply[offset] : 0;
            if (percent is < 0 or > 100 || percent == 0) continue;
            results.Add(new PeripheralDevice
            {
                Id = $"rog:{pid:X4}:kb",
                Name = name,
                Kind = DeviceKind.Keyboard,
                Provider = ProviderName,
                Connected = true,
                Transport = "USB",
                BatteryPercent = percent,
                State = reply.Length > 9 && reply[9] == 1 ? BatteryState.Charging : BatteryState.Normal,
                LastUpdated = DateTimeOffset.Now,
            });
        }
    }

    private static byte[]? Query(HidChannel ch, byte reportId, byte sub, byte? arg = null, int timeoutMs = 1200)
    {
        for (int i = 0; i < 4; i++)
            if (ch.Read(10) == null) break;

        int len = Math.Max(ch.OutputLength, 8);
        var req = new byte[len];
        req[0] = reportId;
        req[1] = 0x12;
        req[2] = sub;
        if (arg is { } a) req[3] = a;
        if (!ch.WriteOutput(req)) return null;

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var r = ch.Read(120);
            if (r == null) continue;
            if (r.Length >= 3 && r[0] == reportId && r[1] == 0x12 && r[2] == sub)
                return r;
        }
        return null;
    }
}

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
                string cleanName = string.IsNullOrWhiteSpace(devName)
                    ? (pid == 0x0703 ? "AM Infinity .97" : $"Angry Miao {vid:X4}:{pid:X4}")
                    : devName.Trim();

                (int Battery, bool Charging, int? DockBattery)? hit = ProbeGroup(g, vid, pid);
                string? note = hit is { DockBattery: { } dock }
                    ? $"底座电池 {dock}%"
                    : hit == null
                        ? "已连接（有线供电无电池或无线休眠中）"
                        : null;

                results.Add(new PeripheralDevice
                {
                    Id = $"am:{vid:X4}-{pid:X4}",
                    Name = cleanName,
                    Kind = DeviceKind.Mouse,
                    Provider = ProviderName,
                    Connected = true,
                    IsSleeping = hit == null,
                    Transport = "2.4G / USB",
                    BatteryPercent = hit?.Battery,
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
        if (d.VendorID == 0x3151 || (d.VendorID == 0x0E8D && d.ProductID == 0x0703))
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
