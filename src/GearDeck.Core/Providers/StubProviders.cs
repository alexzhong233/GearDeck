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

    private static readonly (ushort Pid, string Name, int Offset, int Scale)[] Keyboards =
    [
        (0x1C7D, "ROG 魔导士 Ace HFX 75", 6, 1),
        (0x1B7A, "ROG 游侠 2 RX 98 无线", 6, 1),
        (0x1B78, "ROG 游侠 2 RX 98 无线", 6, 1),
        (0x1AB0, "ROG 游侠 2 98 无线", 6, 1),
        (0x1AAE, "ROG 游侠 2 98", 6, 1),
        (0x1B06, "ROG 魔导士 RX Low Profile", 11, 1),
        (0x1B04, "ROG 魔导士 RX Low Profile", 11, 1),
        (0x193E, "ROG Falchion", 11, 1),
        (0x193C, "ROG Falchion", 11, 1),
        (0x1A85, "ROG Azoth", 6, 1),
        (0x1A83, "ROG Azoth", 6, 1),
        (0x1B42, "ROG Azoth Extreme", 6, 1),
        (0x1B3F, "ROG Azoth Extreme", 6, 1),
        (0x1CF1, "ROG Azoth Extreme SE", 6, 1),
        (0x1CEF, "ROG Azoth Extreme SE", 6, 1),
        (0x1C25, "ROG Azoth X", 6, 1),
        (0x1C24, "ROG Azoth X", 6, 1),
        (0x1A07, "ROG Strix Scope RX TKL", 6, 1),
    ];

    private static readonly (ushort Pid, string Name, int Offset, int Scale)[] Mice =
    [
        (0x18E5, "ROG Chakram", 5, 25),
        (0x18E3, "ROG Chakram", 5, 25),
        (0x1A1A, "ROG Chakram X", 5, 1),
        (0x1A18, "ROG Chakram X", 5, 1),
        (0x18A0, "ROG Gladius II Wireless", 5, 25),
        (0x197F, "ROG Gladius III Wireless", 5, 1),
        (0x197D, "ROG Gladius III Wireless", 5, 1),
        (0x1A72, "ROG Gladius III Aimpoint", 5, 1),
        (0x1A70, "ROG Gladius III Aimpoint", 5, 1),
        (0x1B0C, "ROG Gladius III Aimpoint EVA-02", 5, 1),
        (0x1B0A, "ROG Gladius III Aimpoint EVA-02", 5, 1),
        (0x1A94, "ROG Harpe Ace Aim Lab", 5, 1),
        (0x1A92, "ROG Harpe Ace Aim Lab", 5, 1),
        (0x1B68, "ROG Harpe Ace Extreme", 5, 1),
        (0x1B69, "ROG Harpe Ace Extreme", 5, 1),
        (0x1B67, "ROG Harpe Ace Extreme", 5, 1),
        (0x1B65, "ROG Harpe Ace Mini", 5, 1),
        (0x1B63, "ROG Harpe Ace Mini", 5, 1),
        (0x1AD0, "ROG Harpe II Ace", 5, 1),
        (0x1C69, "ROG Harpe II Ace", 5, 1),
        (0x1960, "ROG Keris Wireless", 5, 25),
        (0x195E, "ROG Keris Wireless", 5, 25),
        (0x1A59, "ROG Keris Wireless EVA", 5, 25),
        (0x1A57, "ROG Keris Wireless EVA", 5, 25),
        (0x1A68, "ROG Keris Wireless Aimpoint", 5, 1),
        (0x1A6A, "ROG Keris Wireless Aimpoint", 5, 1),
        (0x1A66, "ROG Keris Wireless Aimpoint", 5, 1),
        (0x1B1A, "ROG Keris II Ace", 5, 1),
        (0x1B18, "ROG Keris II Ace", 5, 1),
        (0x1B16, "ROG Keris II Ace", 5, 1),
        (0x1C0E, "ROG Keris II Origin", 5, 1),
        (0x1C0C, "ROG Keris II Origin", 5, 1),
        (0x1D4E, "ROG Keris II Origin KJP", 5, 1),
        (0x1D4C, "ROG Keris II Origin KJP", 5, 1),
        (0x1908, "ROG Pugio II", 5, 25),
        (0x1906, "ROG Pugio II", 5, 25),
        (0x1979, "ROG Spatha X", 5, 1),
        (0x1977, "ROG Spatha X", 5, 1),
        (0x18B4, "ROG Strix Carry", 7, 25),
        (0x1949, "ROG Strix Impact II Wireless", 5, 25),
        (0x1947, "ROG Strix Impact II Wireless", 5, 25),
        (0x1AD7, "ROG Strix Impact III Wireless", 5, 1),
    ];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (ushort Pid, string Name, DeviceKind Kind)> OmniSlotCache = new();

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
                VendorId = 0x0B05,
                ProductId = (ushort)pid,
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

        var paired = new List<ushort>();
        var listIface = omni.FirstOrDefault(d => d.DevicePath.Contains("col01", StringComparison.OrdinalIgnoreCase)) ?? omni[0];
        using (var listCh = HidChannel.TryOpen(listIface))
        {
            if (listCh != null)
            {
                var listReply = Query(listCh, 0x01, 0xA0, 0x00, 380);
                if (listReply != null && !IsAsleepFrame(listReply))
                {
                    for (int at = 5; at + 1 < listReply.Length; at += 4)
                    {
                        ushort p = (ushort)(listReply[at] | (listReply[at + 1] << 8));
                        if (p == 0) break;
                        paired.Add(p);
                    }
                }
            }
        }
        if (paired.Count > 0)
            Diag.Log($"ROG Omni 配对设备: {string.Join(",", paired.Select(p => p.ToString("X4")))}");

        bool addedAny = false;
        // 尝试键盘接口 col02 (rid=0x02, sub=0x01) 与 鼠标接口 col03 (rid=0x03, sub=0x07)
        var kbIface = omni.FirstOrDefault(d => d.DevicePath.Contains("col02", StringComparison.OrdinalIgnoreCase)) ?? omni[0];
        if (kbIface != null)
        {
            using var kbCh = HidChannel.TryOpen(kbIface);
            if (kbCh != null)
            {
                var reply = Query(kbCh, 0x02, 0x01, null, 380);
                ushort matchedKbPid = paired.FirstOrDefault(p => Keyboards.Any(k => k.Pid == p));
                var kbModel = matchedKbPid != 0 ? Keyboards.FirstOrDefault(k => k.Pid == matchedKbPid) : default;
                int offset = kbModel.Pid != 0 ? kbModel.Offset : 6;

                if (reply != null && !IsAsleepFrame(reply))
                {
                    int pct = reply.Length > offset ? reply[offset] : 0;
                    if (pct is <= 0 or > 100 && reply.Length > 11) pct = reply[11];
                    if (pct is > 0 and <= 100)
                    {
                        string name = !string.IsNullOrEmpty(kbModel.Name)
                            ? kbModel.Name
                            : OmniSlotCache.TryGetValue(1, out var cm) && cm.Kind == DeviceKind.Keyboard
                                ? cm.Name
                                : "ROG 游侠 2 RX 98 无线";
                        ushort idPid = matchedKbPid != 0 ? matchedKbPid : (ushort)0x1B7A;
                        OmniSlotCache[1] = (idPid, name, DeviceKind.Keyboard);
                        results.Add(new PeripheralDevice
                        {
                            Id = $"rog:{idPid:X4}:kb",
                            VendorId = 0x0B05,
                            ProductId = idPid,
                            Name = name,
                            Kind = DeviceKind.Keyboard,
                            Provider = ProviderName,
                            Connected = true,
                            Transport = "Omni 2.4G",
                            BatteryPercent = pct,
                            State = reply.Length > 9 && reply[9] == 1 ? BatteryState.Charging : BatteryState.Normal,
                            LastUpdated = DateTimeOffset.Now,
                        });
                        addedAny = true;
                    }
                }
            }
        }

        var msIface = omni.FirstOrDefault(d => d.DevicePath.Contains("col03", StringComparison.OrdinalIgnoreCase));
        if (msIface != null)
        {
            using var msCh = HidChannel.TryOpen(msIface);
            if (msCh != null)
            {
                var reply = Query(msCh, 0x03, 0x07, null, 380);
                ushort matchedMsPid = paired.FirstOrDefault(p => Mice.Any(m => m.Pid == p));
                if (reply != null && !IsAsleepFrame(reply) && reply.Length > 5)
                {
                    var mModel = matchedMsPid != 0 ? Mice.FirstOrDefault(m => m.Pid == matchedMsPid) : default;
                    int raw = reply[mModel.Pid != 0 ? mModel.Offset : 5];
                    int pct = Math.Clamp(raw * (mModel.Pid != 0 ? mModel.Scale : 1), 0, 100);
                    if (pct is > 0 and <= 100)
                    {
                        string mName = !string.IsNullOrEmpty(mModel.Name) ? mModel.Name : $"ROG 无线鼠标 {matchedMsPid:X4}";
                        ushort idPid = matchedMsPid != 0 ? matchedMsPid : (ushort)0x1ACE;
                        OmniSlotCache[2] = (idPid, mName, DeviceKind.Mouse);
                        results.Add(new PeripheralDevice
                        {
                            Id = $"rog:{idPid:X4}:ms",
                            VendorId = 0x0B05,
                            ProductId = idPid,
                            Name = mName,
                            Kind = DeviceKind.Mouse,
                            Provider = ProviderName,
                            Connected = true,
                            Transport = "Omni 2.4G",
                            BatteryPercent = pct,
                            State = reply.Length > 10 && reply[10] == 1 ? BatteryState.Charging : BatteryState.Normal,
                            LastUpdated = DateTimeOffset.Now,
                        });
                        addedAny = true;
                    }
                }
            }
        }

        // 若 Omni 接收器在线但配对键盘休眠 (FF AA)，仍必须展示已配对的键盘而不是消失
        if (!addedAny)
        {
            ushort sleepPid = paired.FirstOrDefault(p => Keyboards.Any(k => k.Pid == p));
            var sleepModel = sleepPid != 0 ? Keyboards.FirstOrDefault(k => k.Pid == sleepPid) : default;
            string sleepName = !string.IsNullOrEmpty(sleepModel.Name)
                ? sleepModel.Name
                : OmniSlotCache.TryGetValue(1, out var cached) ? cached.Name : "ROG 游侠 2 RX 98 无线";
            ushort idPid = sleepPid != 0 ? sleepPid : (ushort)0x1B7A;

            results.Add(new PeripheralDevice
            {
                Id = $"rog:{idPid:X4}:kb",
                VendorId = 0x0B05,
                ProductId = idPid,
                Name = sleepName,
                Kind = DeviceKind.Keyboard,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = true,
                Transport = "Omni 2.4G",
                BatteryPercent = null,
                State = BatteryState.Unknown,
                Note = "Omni 接收器在线（键盘休眠中，任意按键唤醒后可读取电量）",
                LastUpdated = DateTimeOffset.Now,
            });
        }
    }

    private static bool IsAsleepFrame(byte[] reply)
        => reply.Length >= 3 && reply[1] == 0xFF && reply[2] == 0xAA;

    private static void ReadDirectKeyboards(List<PeripheralDevice> results)
    {
        foreach (var (pid, name, offset, scale) in Keyboards)
        {
            if (results.Any(r => r.ProductId == pid || r.Id.Contains(pid.ToString("X4"), StringComparison.OrdinalIgnoreCase)))
                continue;
            var matching = HidSafe.EnumerateDevices(0x0B05, pid);
            if (matching.Count == 0) continue;

            int? percent = null;
            bool charging = false;
            var iface = matching
                .Where(d => HidSafe.MaxOutputLen(d) >= 12)
                .OrderByDescending(HidSafe.MaxOutputLen)
                .FirstOrDefault();
            if (iface != null)
            {
                using var ch = HidChannel.TryOpen(iface);
                if (ch != null)
                {
                    var reply = Query(ch, 0x00, 0x01, null, 350);
                    if (reply != null && !IsAsleepFrame(reply) && reply.Length > offset)
                    {
                        int candidate = reply[offset] * scale;
                        if (candidate is > 0 and <= 100)
                        {
                            percent = candidate;
                            charging = reply.Length > 9 && reply[9] == 1;
                        }
                    }
                }
            }

            // 有线磁轴键盘如 0B05:1C7D 魔导士 Ace HFX 75 无电池，也必须识别显示以提供奥创/Gear Link 入口
            bool isWiredOnly = pid == 0x1C7D;
            if (percent == null && !isWiredOnly && matching.All(d => HidSafe.MaxOutputLen(d) < 12))
                continue;

            results.Add(new PeripheralDevice
            {
                Id = $"rog:{pid:X4}:kb",
                VendorId = 0x0B05,
                ProductId = pid,
                Name = name,
                Kind = DeviceKind.Keyboard,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = !isWiredOnly && percent == null,
                Transport = isWiredOnly ? "USB 有线磁轴" : "USB / 2.4G",
                BatteryPercent = percent,
                State = charging ? BatteryState.Charging : BatteryState.Unknown,
                Note = isWiredOnly
                    ? "有线磁轴键盘（无内置电池）· 可打开奥创中心或 Gear Link 网页驱动调校"
                    : percent == null
                        ? "已连接（有线模式或暂未上报电量）"
                        : null,
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
