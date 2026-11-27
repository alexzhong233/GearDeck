using System.Collections.Concurrent;
using System.Globalization;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// ASUS ROG 全系（VID 0x0B05，参考 seerge/g-helper HID 协议）：
/// 1. 耳机（ROG Delta II 0x1AFA/0x1D41, ROG Pelta 0x1B84，Report ID 0xCC）：电量/充电、侧音、降噪语音提示、10段 EQ 预设、休眠/低电阈值、灯效；
/// 2. 键盘（魔导士 Ace HFX / Ace 75 HE / 游侠 2 RX 98 / Azoth / Scope II / Omni col02）：6 组板载配置、灯效亮度与模式、休眠/低电阈值；
///    （安全红线：绝不对 0x1C7D / 0x1B7E 发送未公开的磁轴 RT 写指令，防止固件锁死）
/// 3. 鼠标（42 款直连 USB/2.4G + Omni col03）：DPI、回报率、消抖、LOD、Motion Sync、角度校准、板载配置、休眠与低电阈值。
/// </summary>
public sealed class RogProvider : ITunablePeripheralProvider
{
    public const string ProviderName = "ROG";
    public string Name => ProviderName;

    private static readonly (ushort Pid, string Name, int Offset, int Scale)[] Keyboards =
    [
        (0x1C7D, "ROG 魔导士 Ace HFX 75", 6, 1),
        (0x1B7E, "ROG Falchion Ace HFX", 6, 1),
        (0x1A64, "ROG Falchion Ace", 6, 1),
        (0x1B7A, "ROG 游侠 2 RX 98 无线", 6, 1),
        (0x1B78, "ROG 游侠 2 RX 98 无线", 6, 1),
        (0x1AB0, "ROG 游侠 2 98 无线", 6, 1),
        (0x1AAE, "ROG 游侠 2 98", 6, 1),
        (0x1AB3, "ROG Strix Scope II", 6, 1),
        (0x1AB5, "ROG Strix Scope II RX", 6, 1),
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

    private static readonly HashSet<ushort> WiredOnlyKeyboardPids =
    [
        0x1C7D, 0x1B7E, 0x1A64, 0x1AB3, 0x1AB5, 0x1AAE, 0x1A07,
    ];

    private static readonly (ushort Pid, string Name)[] Headsets =
    [
        (0x1AFA, "ROG Delta II"),
        (0x1D41, "ROG Delta II KJP"),
        (0x1B84, "ROG Pelta"),
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

    private static readonly (string Name, byte[] Gains)[] HeadsetEqPresets =
    [
        ("默认平衡", [50, 50, 50, 50, 50, 50, 50, 50, 50, 50]),
        ("古典", [50, 50, 100, 100, 50, 50, 50, 50, 66, 66]),
        ("嘻哈", [50, 50, 90, 58, 40, 40, 50, 50, 82, 82]),
        ("爵士", [50, 50, 50, 74, 74, 74, 50, 66, 82, 82]),
        ("金属", [50, 50, 50, 50, 50, 50, 74, 50, 74, 58]),
        ("摇滚", [50, 50, 66, 74, 40, 40, 50, 50, 82, 82]),
        ("电音", [50, 50, 82, 40, 40, 32, 50, 50, 90, 90]),
        ("人声", [50, 50, 66, 58, 50, 50, 50, 50, 32, 8]),
    ];

    private static readonly int[] DebounceMsTable = [0, 8, 12, 16, 20, 24, 28, 32];
    private static readonly int[] MousePollingHzTable = [125, 250, 500, 1000, 2000, 4000, 8000];
    private static readonly int[] SleepSecTable = [60, 120, 180, 300, 600];

    private static readonly ConcurrentDictionary<int, (ushort Pid, string Name, DeviceKind Kind)> OmniSlotCache = new();
    private static readonly ConcurrentDictionary<ushort, int> SelectedEqPresetCache = new();

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("rog:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        try { ReadHeadsets(results); } catch (Exception ex) { Diag.Log($"ROG headset: {ex.Message}"); }
        try { ReadOmni(results); } catch (Exception ex) { Diag.Log($"ROG omni: {ex.Message}"); }
        try { ReadDirectKeyboards(results); } catch (Exception ex) { Diag.Log($"ROG kb: {ex.Message}"); }
        try { ReadDirectMice(results); } catch (Exception ex) { Diag.Log($"ROG mouse: {ex.Message}"); }
        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParseDeviceId(deviceId, out ushort pid, out string kindTag))
        {
            caps.StatusMessage = "无效的 ROG 设备 ID。";
            return caps;
        }

        if (kindTag == "hs")
            return QueryHeadsetCapabilities(deviceId, pid);
        if (kindTag == "kb")
            return QueryKeyboardCapabilities(deviceId, pid);
        return QueryMouseCapabilities(deviceId, pid);
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParseDeviceId(deviceId, out ushort pid, out string kindTag))
            return new DeviceTuningResult(false, "无效的 ROG 设备 ID。");

        if (kindTag == "hs")
            return ApplyHeadsetSettings(deviceId, pid, update);
        if (kindTag == "kb")
            return ApplyKeyboardSettings(deviceId, pid, update);
        return ApplyMouseSettings(deviceId, pid, update);
    }

    // ── Enumeration ──────────────────────────────────────────────────────────────

    private static void ReadHeadsets(List<PeripheralDevice> results)
    {
        foreach (var (pid, name) in Headsets)
        {
            var iface = FindHeadsetInterface(pid);
            if (iface == null) continue;
            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;
            var reply = Query(ch, 0xCC, 0x07);
            if (reply == null) continue;
            Diag.Verbose($"ROG {pid:X4} headset raw={BitConverter.ToString(reply, 0, Math.Min(16, reply.Length))}");
            int percent = reply.Length > 6 ? reply[6] : 0;
            bool charging = false;
            var charge = Query(ch, 0xCC, 0x08);
            if (charge is { Length: > 5 }) charging = charge[5] == 1;
            bool valid = !IsAsleepFrame(reply) && (percent is > 0 and <= 100 || (percent == 0 && charging));
            results.Add(new PeripheralDevice
            {
                Id = $"rog:{pid:X4}:hs",
                VendorId = 0x0B05,
                ProductId = pid,
                Name = name,
                Kind = DeviceKind.Headset,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = !valid,
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

        var paired = QueryOmniPairedPids(omni);
        if (paired.Count > 0)
            Diag.Verbose($"ROG Omni 配对设备: {string.Join(",", paired.Select(p => p.ToString("X4")))}");

        bool addedAny = false;
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

    private static List<ushort> QueryOmniPairedPids(List<HidDevice> omni)
    {
        var paired = new List<ushort>();
        var listIface = omni.FirstOrDefault(d => d.DevicePath.Contains("col01", StringComparison.OrdinalIgnoreCase)) ?? omni[0];
        using var listCh = HidChannel.TryOpen(listIface);
        if (listCh == null) return paired;
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
        return paired;
    }

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
            var iface = PickVendorInterface(matching);
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

            bool isWiredOnly = WiredOnlyKeyboardPids.Contains(pid);
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
                Transport = isWiredOnly ? (pid is 0x1C7D or 0x1B7E ? "USB 有线磁轴" : "USB") : "USB / 2.4G",
                BatteryPercent = percent,
                State = charging ? BatteryState.Charging : BatteryState.Unknown,
                Note = isWiredOnly
                    ? "有线键盘（无内置电池）· 支持板载配置、灯效与 Gear Link"
                    : percent == null
                        ? "已连接（有线模式或暂未上报电量）"
                        : null,
                LastUpdated = DateTimeOffset.Now,
            });
        }
    }

    private static void ReadDirectMice(List<PeripheralDevice> results)
    {
        foreach (var (pid, name, offset, scale) in Mice)
        {
            if (results.Any(r => r.ProductId == pid || r.Id.Contains(pid.ToString("X4"), StringComparison.OrdinalIgnoreCase)))
                continue;
            var matching = HidSafe.EnumerateDevices(0x0B05, pid);
            if (matching.Count == 0) continue;

            int? percent = null;
            bool charging = false;
            var iface = PickVendorInterface(matching);
            if (iface != null)
            {
                using var ch = HidChannel.TryOpen(iface);
                if (ch != null)
                {
                    var reply = Query(ch, 0x00, 0x07, null, 350);
                    if (reply != null && !IsAsleepFrame(reply) && reply.Length > offset)
                    {
                        int candidate = Math.Clamp(reply[offset] * scale, 0, 100);
                        if (candidate is > 0 and <= 100)
                        {
                            percent = candidate;
                            charging = reply.Length > 10 && reply[10] == 1;
                        }
                    }
                }
            }

            string? rawName = matching.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            bool wired = rawName?.Contains("Wired", StringComparison.OrdinalIgnoreCase) == true;
            results.Add(new PeripheralDevice
            {
                Id = $"rog:{pid:X4}:ms",
                VendorId = 0x0B05,
                ProductId = pid,
                Name = name,
                Kind = DeviceKind.Mouse,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = !wired && percent == null,
                Transport = wired ? "USB" : "2.4G / USB",
                BatteryPercent = percent,
                State = charging ? BatteryState.Charging : BatteryState.Unknown,
                Note = percent == null && !wired ? "ROG 接收器已连接（鼠标休眠中，晃动唤醒）" : null,
                LastUpdated = DateTimeOffset.Now,
            });
        }
    }

    // ── Headset Control (G-Helper AsusHeadset.cs) ────────────────────────────────

    private static DeviceTuningCapabilities QueryHeadsetCapabilities(string deviceId, ushort pid)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        var iface = FindHeadsetInterface(pid);
        if (iface == null)
        {
            caps.StatusMessage = "未找到 ROG 耳机 HID 控制接口。";
            return caps;
        }

        using var ch = HidChannel.TryOpen(iface);
        if (ch == null)
        {
            caps.StatusMessage = "无法打开 ROG 耳机控制通道。";
            return caps;
        }

        var bat = Query(ch, 0xCC, 0x07);
        if (bat == null || IsAsleepFrame(bat) || bat.Length <= 8 || bat[6] > 100)
        {
            caps.IsOnline = false;
            caps.StatusMessage = "ROG 耳机当前休眠或未开机，请开启耳机电源后刷新。";
            return caps;
        }

        caps.IsOnline = true;
        caps.SupportsSleepTimeout = true;
        caps.SupportedSleepSeconds = [180, 300, 600, 900, 1200, 1800];
        caps.CurrentSleepSeconds = bat[5] > 0 ? Math.Clamp(bat[5] * 60, 180, 1800) : 300;
        caps.SupportsLowBatteryWarning = true;
        caps.CurrentLowBatteryWarningPercent = Math.Clamp((int)bat[7], 5, 50);

        caps.SupportsSidetone = true;
        caps.MaxSidetoneLevel = 20;
        var stEn = Query(ch, 0xCC, 0x24);
        var stLvl = Query(ch, 0xCC, 0x19);
        if (stEn is { Length: > 5 }) caps.SidetoneEnabled = stEn[5] == 1;
        if (stLvl is { Length: > 5 }) caps.CurrentSidetoneLevel = Math.Clamp((int)stLvl[5], 0, 20);

        caps.SupportsVoicePrompt = true;
        var vp = Query(ch, 0xCC, 0x28);
        if (vp is { Length: > 5 }) caps.VoicePromptEnabled = vp[5] == 1;

        caps.SupportsEqualizer = true;
        var eq = Query(ch, 0xCC, 0x21);
        if (eq is { Length: > 5 }) caps.EqualizerEnabled = eq[5] == 1;
        caps.SupportedEqPresets = HeadsetEqPresets.Select((p, i) => new LodOption(i, p.Name)).ToList();
        caps.CurrentEqPreset = SelectedEqPresetCache.GetValueOrDefault(pid, 0);

        caps.SupportsLighting = true;
        caps.MaxLightingBrightness = 100;
        caps.SupportedLightingModes =
        [
            new(0, "关闭"),
            new(1, "常亮"),
            new(2, "呼吸"),
            new(4, "色彩循环"),
            new(5, "彩虹"),
        ];
        var lcfg = Query(ch, 0xCC, 0x03);
        if (lcfg is { Length: > 6 })
        {
            caps.CurrentLightingMode = lcfg[5];
            caps.CurrentLightingBrightness = Math.Clamp((int)lcfg[6], 0, 100);
        }

        caps.StatusMessage = "已读取 ROG 耳机音频、EQ、灯效与省电参数。";
        return caps;
    }

    private static DeviceTuningResult ApplyHeadsetSettings(string deviceId, ushort pid, DeviceTuningUpdate update)
    {
        var iface = FindHeadsetInterface(pid);
        if (iface == null) return new DeviceTuningResult(false, "未找到 ROG 耳机控制接口。");
        using var ch = HidChannel.TryOpen(iface);
        if (ch == null) return new DeviceTuningResult(false, "打开 ROG 耳机通道失败。");

        if (update.SleepSeconds is not null || update.LowBatteryWarningPercent is not null)
        {
            var cur = Query(ch, 0xCC, 0x07);
            int curMin = cur is { Length: > 5 } && cur[5] > 0 ? cur[5] : 5;
            int curWarn = cur is { Length: > 7 } && cur[7] is >= 5 and <= 50 ? cur[7] : 15;
            byte min = (byte)(update.SleepSeconds is { } s ? Math.Clamp(s / 60, 1, 60) : curMin);
            byte warn = (byte)(update.LowBatteryWarningPercent is { } w ? Math.Clamp(w, 5, 50) : curWarn);
            WriteRaw(ch, [0xCC, 0x51, 0x37, 0x00, 0x00, min, warn, 0x01]);
        }

        if (update.Sidetone is { } st)
        {
            WriteRaw(ch, [0xCC, 0x41, 0x11, 0x00, 0x00, (byte)(st.Enabled ? 1 : 0)]);
            WriteRaw(ch, [0xCC, 0x61, 0x11, 0x00, 0x00, (byte)Math.Clamp(st.Level, 0, 20)]);
        }

        if (update.VoicePromptEnabled is { } vp)
            WriteRaw(ch, [0xCC, 0x41, 0x0A, 0x00, 0x00, (byte)(vp ? 1 : 0)]);

        if (update.Equalizer is { } eq)
        {
            WriteRaw(ch, [0xCC, 0x41, 0x03, 0x00, 0x00, (byte)(eq.Enabled ? 1 : 0)]);
            if (eq.PresetCode is { } code && code >= 0 && code < HeadsetEqPresets.Length)
            {
                SelectedEqPresetCache[pid] = code;
                byte[] gains = HeadsetEqPresets[code].Gains;
                Thread.Sleep(60);
                var pkt = new byte[5 + gains.Length];
                pkt[0] = 0xCC; pkt[1] = 0x41; pkt[2] = 0x04;
                Array.Copy(gains, 0, pkt, 5, gains.Length);
                WriteRaw(ch, pkt);
                for (byte band = 0; band < gains.Length; band++)
                    WriteRaw(ch, [0xCC, 0x41, 0x06, 0x00, 0x00, band, gains[band]]);
            }
        }

        if (update.Lighting is { } light)
        {
            bool on = light.Mode != 0 && light.Brightness > 0;
            WriteRaw(ch, [0xCC, 0x51, 0x10, 0x00, 0x00, (byte)(on ? 1 : 0)]);
            WriteRaw(ch, [0xCC, 0x51, 0x28, 0x00, 0x00, (byte)Math.Clamp(light.Mode, 0, 5), (byte)Math.Clamp(light.Brightness, 0, 100), 0xFF, 0x00, 0x00]);
        }

        Thread.Sleep(35);
        return new DeviceTuningResult(true, "已写入 ROG 耳机硬件参数。", QueryHeadsetCapabilities(deviceId, pid));
    }

    // ── Keyboard Control (G-Helper AsusKeyboard.cs) ──────────────────────────────

    private static DeviceTuningCapabilities QueryKeyboardCapabilities(string deviceId, ushort pid)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        var (iface, rid) = FindKeyboardChannel(pid);
        if (iface == null)
        {
            caps.StatusMessage = "未找到 ROG 键盘控制接口。";
            return caps;
        }
        using var ch = HidChannel.TryOpen(iface);
        if (ch == null)
        {
            caps.StatusMessage = "打开 ROG 键盘控制接口失败。";
            return caps;
        }

        var prof = Query(ch, rid, 0x00);
        if (prof == null || IsAsleepFrame(prof) || (prof.Length > 5 && prof[5] == 0xFA))
        {
            caps.IsOnline = false;
            caps.StatusMessage = "ROG 键盘当前处于无线休眠状态，请按任意键唤醒后重试。";
            return caps;
        }

        caps.IsOnline = true;
        caps.OnboardProfileCount = 6;
        caps.OnboardProfileNames = Enumerable.Range(1, 6).Select(i => $"板载配置 {i}").ToList();
        if (prof.Length > 11 && prof[11] is >= 1 and <= 6)
            caps.ActiveOnboardProfile = prof[11] == 6 ? 0 : prof[11];

        caps.SupportsLighting = true;
        caps.MaxLightingBrightness = 100;
        caps.CurrentLightingBrightness = 100;
        caps.SupportedLightingModes =
        [
            new(0xF0, "关闭"),
            new(0, "常亮"),
            new(1, "呼吸"),
            new(2, "色彩循环"),
            new(3, "彩虹"),
        ];
        if (prof.Length > 10 && prof[10] is 0 or 1 or 2 or 3 or 0xF0)
            caps.CurrentLightingMode = prof[10];

        if (!WiredOnlyKeyboardPids.Contains(pid))
        {
            var bat = Query(ch, rid, 0x01);
            caps.SupportsSleepTimeout = true;
            caps.SupportedSleepSeconds = [60, 120, 180, 300, 600];
            caps.CurrentSleepSeconds = bat is { Length: > 7 } && bat[7] < SleepSecTable.Length
                ? SleepSecTable[bat[7]]
                : 300;
            caps.SupportsLowBatteryWarning = true;
            caps.CurrentLowBatteryWarningPercent = 15;
        }

        caps.StatusMessage = "已读取 ROG 键盘板载配置与灯效参数（磁轴 RT 请使用奥创中心 / Gear Link）。";
        return caps;
    }

    private static DeviceTuningResult ApplyKeyboardSettings(string deviceId, ushort pid, DeviceTuningUpdate update)
    {
        var (iface, rid) = FindKeyboardChannel(pid);
        if (iface == null) return new DeviceTuningResult(false, "未找到 ROG 键盘控制接口。");
        using var ch = HidChannel.TryOpen(iface);
        if (ch == null) return new DeviceTuningResult(false, "打开 ROG 键盘通道失败。");

        if (update.ActiveOnboardProfile is { } slot)
            WriteRaw(ch, BuildKeyboardProfilePacket(rid, slot));

        if (update.Lighting is { } light)
        {
            WriteRaw(ch, BuildKeyboardLightingPacket(rid, light.Mode, light.Brightness));
            WriteRaw(ch, [rid, 0x50, 0x55]);
        }

        if (!WiredOnlyKeyboardPids.Contains(pid))
        {
            if (update.LowBatteryWarningPercent is { } warn)
                WriteRaw(ch, [rid, 0x51, 0x37, 0x00, 0x00, (byte)Math.Clamp(warn, 5, 50)]);
            if (update.SleepSeconds is { } sleepSec)
                WriteRaw(ch, [rid, 0x51, 0x38, 0x00, 0x00, MapPowerOffCode(sleepSec)]);
        }

        Thread.Sleep(40);
        return new DeviceTuningResult(true, "已写入 ROG 键盘硬件。", QueryKeyboardCapabilities(deviceId, pid));
    }

    // ── Mouse Control (G-Helper AsusMouse.cs) ────────────────────────────────────

    private static DeviceTuningCapabilities QueryMouseCapabilities(string deviceId, ushort pid)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        var (iface, rid) = FindMouseChannel(pid);
        if (iface == null)
        {
            caps.StatusMessage = "未找到 ROG 鼠标控制接口。";
            return caps;
        }
        using var ch = HidChannel.TryOpen(iface);
        if (ch == null)
        {
            caps.StatusMessage = "打开 ROG 鼠标通道失败。";
            return caps;
        }

        var prof = Query(ch, rid, 0x00);
        var info = Query(ch, rid, 0x04, 0x00);
        if (info == null || IsAsleepFrame(info))
        {
            caps.IsOnline = false;
            caps.StatusMessage = "ROG 鼠标当前无线休眠中，请晃动鼠标唤醒后重试。";
            return caps;
        }

        caps.IsOnline = true;
        if (prof is { Length: > 10 } && !IsAsleepFrame(prof))
        {
            caps.OnboardProfileCount = 5;
            caps.OnboardProfileNames = Enumerable.Range(1, 5).Select(i => $"板载配置 {i}").ToList();
            if (prof[10] is >= 1 and <= 5)
                caps.ActiveOnboardProfile = prof[10] - 1;
        }

        var dpiResp = Query(ch, rid, 0x04, 0x02) ?? info;
        if (ParseMouseDpi(dpiResp) is { } dpiVal)
        {
            caps.SupportsDpi = true;
            caps.SupportsIndependentXY = false;
            caps.MinDpi = 100;
            caps.MaxDpi = 36000;
            caps.DpiStep = 50;
            caps.CurrentDpiX = dpiVal;
            caps.CurrentDpiY = dpiVal;
        }

        if (info.Length > 14)
        {
            int code = (info[14] & 0xF0) > 0 ? (info[14] >> 4) : (info[14] & 0x07);
            if ((uint)code < (uint)MousePollingHzTable.Length)
            {
                caps.SupportedPollingRates = [125, 250, 500, 1000, 2000, 4000, 8000];
                caps.CurrentPollingRate = MousePollingHzTable[code];
            }
        }

        if (info.Length > 15 && info[15] < DebounceMsTable.Length)
        {
            caps.SupportsDebounce = true;
            caps.MaxDebounceMs = 32;
            caps.CurrentDebounceMs = DebounceMsTable[info[15]];
        }

        var lodResp = Query(ch, rid, 0x06);
        if (lodResp is { Length: > 8 } && !IsAsleepFrame(lodResp))
        {
            caps.SupportedLodOptions = [new(0, "低 (1 mm)"), new(1, "高 (2 mm)")];
            caps.CurrentLod = lodResp[8] == 1 ? 1 : 0;
        }

        var msResp = Query(ch, rid, 0x04, 0x04);
        if (msResp is { Length: > 5 } && !IsAsleepFrame(msResp))
        {
            caps.SupportsMotionSync = true;
            caps.MotionSync = msResp[5] == 1;
        }

        caps.SupportsAngleSnapping = true;
        caps.SupportsAngleTuning = true;
        caps.SupportsSleepTimeout = true;
        caps.SupportedSleepSeconds = SleepSecTable.ToList();
        caps.CurrentSleepSeconds = 300;
        caps.SupportsLowBatteryWarning = true;
        caps.CurrentLowBatteryWarningPercent = 15;
        caps.StatusMessage = "已读取 ROG 鼠标实时硬件参数。";
        return caps;
    }

    private static DeviceTuningResult ApplyMouseSettings(string deviceId, ushort pid, DeviceTuningUpdate update)
    {
        var (iface, rid) = FindMouseChannel(pid);
        if (iface == null) return new DeviceTuningResult(false, "未找到 ROG 鼠标控制接口。");
        using var ch = HidChannel.TryOpen(iface);
        if (ch == null) return new DeviceTuningResult(false, "打开 ROG 鼠标通道失败。");

        if (update.ActiveOnboardProfile is { } slot)
            WriteRaw(ch, [rid, 0x51, 0x00, 0x00, 0x00, (byte)Math.Clamp(slot, 0, 4)]);

        if (update.Dpi is { } dpi)
        {
            int d = Math.Clamp(dpi.X, 100, 36000);
            ushort enc = (ushort)((d - 50) / 50);
            WriteRaw(ch, [rid, 0x51, 0x31, 0x00, 0x00, (byte)(enc & 0xFF), (byte)(enc >> 8)]);
        }

        if (update.PollingRateHz is { } hz)
        {
            int code = Array.IndexOf(MousePollingHzTable, hz);
            if (code >= 0)
                WriteRaw(ch, [rid, 0x51, 0x31, 0x04, 0x00, (byte)code]);
        }

        if (update.DebounceMs is { } ms)
        {
            byte dbIdx = (byte)Math.Clamp(Array.FindIndex(DebounceMsTable, x => x >= ms), 0, DebounceMsTable.Length - 1);
            WriteRaw(ch, [rid, 0x51, 0x31, 0x05, 0x00, dbIdx]);
        }

        if (update.AngleSnapping is { } snap)
            WriteRaw(ch, [rid, 0x51, 0x31, 0x06, 0x00, (byte)(snap ? 1 : 0)]);

        if (update.AngleTuningDeg is { } ang)
        {
            short s = (short)Math.Clamp(ang, -30, 30);
            WriteRaw(ch, [rid, 0x51, 0x31, 0x0B, 0x00, (byte)(s & 0xFF), (byte)((s >> 8) & 0xFF)]);
        }

        if (update.LodCode is { } lod)
            WriteRaw(ch, [rid, 0x51, 0x35, 0xFF, 0x00, 0xFF, (byte)(lod > 0 ? 1 : 0)]);

        if (update.MotionSync is { } motion)
            WriteRaw(ch, [rid, 0x51, 0x31, 0x12, 0x00, (byte)(motion ? 1 : 0)]);

        if (update.SleepSeconds is not null || update.LowBatteryWarningPercent is not null)
        {
            var cur = Query(ch, rid, 0x12, 0x07);
            byte curPo = cur is { Length: > 5 } && cur[5] <= 4 ? cur[5] : (byte)3;
            int curLw = cur is { Length: > 7 } && cur[7] is >= 5 and <= 50 ? cur[7] : 15;
            byte po = update.SleepSeconds is { } ss ? MapPowerOffCode(ss) : curPo;
            byte lw = (byte)(update.LowBatteryWarningPercent is { } ww ? Math.Clamp(ww, 5, 50) : curLw);
            WriteRaw(ch, [rid, 0x51, 0x37, 0x00, 0x00, po, 0x00, lw]);
        }

        WriteRaw(ch, [rid, 0x50, 0x03]); // Flush to onboard memory
        Thread.Sleep(40);
        return new DeviceTuningResult(true, "已写入 ROG 鼠标硬件。", QueryMouseCapabilities(deviceId, pid));
    }

    // ── Channel Pickers & Internal Protocol Helpers ──────────────────────────────

    private static HidDevice? FindHeadsetInterface(ushort pid)
        => HidSafe.EnumerateDevices(0x0B05, pid)
            .Where(d => HidSafe.MaxInputLen(d) >= 8 && HidSafe.MaxOutputLen(d) >= 8)
            .OrderByDescending(HidSafe.MaxOutputLen)
            .FirstOrDefault();

    private static (HidDevice? Device, byte ReportId) FindKeyboardChannel(ushort pid)
    {
        var direct = PickVendorInterface(HidSafe.EnumerateDevices(0x0B05, pid));
        if (direct != null) return (direct, 0x00);

        var omni = HidSafe.EnumerateDevices(0x0B05, 0x1ACE)
            .Where(d => HidSafe.MaxOutputLen(d) >= 12)
            .FirstOrDefault(d => d.DevicePath.Contains("col02", StringComparison.OrdinalIgnoreCase));
        return (omni, 0x02);
    }

    private static (HidDevice? Device, byte ReportId) FindMouseChannel(ushort pid)
    {
        var direct = PickVendorInterface(HidSafe.EnumerateDevices(0x0B05, pid));
        if (direct != null) return (direct, 0x00);

        var omni = HidSafe.EnumerateDevices(0x0B05, 0x1ACE)
            .Where(d => HidSafe.MaxOutputLen(d) >= 12)
            .FirstOrDefault(d => d.DevicePath.Contains("col03", StringComparison.OrdinalIgnoreCase));
        return (omni, 0x03);
    }

    private static HidDevice? PickVendorInterface(IReadOnlyList<HidDevice> list)
        => list.Where(d => HidSafe.MaxOutputLen(d) >= 12)
               .OrderByDescending(d => HidSafe.PrimaryUsage(d).UsagePage == 0xFF00 ? 2 : 0)
               .ThenByDescending(HidSafe.MaxOutputLen)
               .FirstOrDefault();

    internal static byte MapPowerOffCode(int sleepSeconds) => sleepSeconds switch
    {
        <= 60 => 0,
        <= 120 => 1,
        <= 180 => 2,
        <= 300 => 3,
        _ => 4,
    };

    internal static byte[] BuildKeyboardProfilePacket(byte reportId, int zeroBasedSlot)
        => [reportId, 0x51, 0x00, 0x00, 0x00, (byte)Math.Clamp(zeroBasedSlot, 0, 5)];

    internal static byte[] BuildKeyboardLightingPacket(byte reportId, int mode, int brightness)
    {
        bool off = mode == 0xF0 || brightness <= 0;
        var pkt = new byte[19];
        pkt[0] = reportId;
        pkt[1] = 0x51;
        pkt[2] = 0x2C;
        pkt[3] = off ? (byte)0x00 : (byte)Math.Clamp(mode, 0, 3);
        pkt[5] = 0x07;
        pkt[6] = off ? (byte)0x00 : (byte)Math.Clamp(brightness, 0, 100);
        pkt[7] = 0x01;
        pkt[9] = 0x02;
        pkt[10] = 0xFF;
        return pkt;
    }

    internal static int? ParseMouseDpi(byte[] packet)
    {
        if (packet.Length < 7 || packet[1] != 0x12 || packet[2] != 0x04) return null;
        int raw = (packet[6] << 8) | packet[5];
        int dpi = (raw + 1) * 50;
        return dpi is >= 50 and <= 45000 ? dpi : null;
    }

    private static bool IsAsleepFrame(byte[] reply)
        => reply.Length >= 3 && reply[1] == 0xFF && reply[2] == 0xAA;

    private static bool WriteRaw(HidChannel ch, byte[] payload)
    {
        int len = Math.Max(ch.OutputLength, Math.Max(payload.Length, 19));
        var req = new byte[len];
        Array.Copy(payload, req, Math.Min(payload.Length, req.Length));
        return ch.WriteOutput(req);
    }

    private static byte[]? Query(HidChannel ch, byte reportId, byte sub, byte? arg = null, int timeoutMs = 900)
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

    private static bool TryParseDeviceId(string id, out ushort pid, out string kindTag)
    {
        pid = 0;
        kindTag = "ms";
        var parts = id.Split(':');
        if (parts.Length < 2) return false;
        if (parts.Length >= 3) kindTag = parts[2].ToLowerInvariant();
        return ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
    }
}
