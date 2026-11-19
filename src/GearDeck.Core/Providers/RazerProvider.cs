using System.Globalization;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 雷蛇 91 字节 Feature 协议（Report ID 0 + 90 字节帧）：
/// 支持电量/充电查询、DPI（XY 独立 & 板载多档位 0x04/0x85,0x05,0x86,0x06）、
/// 回报率（HyperPolling 125..8000 Hz 0x00/0xC0,0x40 与传统 0x00/0x85,0x05）、
/// 休眠超时（0x07/0x83,0x03）以及硬件低电量提醒阈值（0x07/0x81,0x01）。
/// </summary>
public sealed class RazerProvider : ITunablePeripheralProvider
{
    public const string ProviderName = "Razer";
    public string Name => ProviderName;

    private static readonly byte[] Tids = [0x1F, 0x3F, 0xFF, 0x08, 0x9F];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ExplicitSymLodCache =
        new(StringComparer.OrdinalIgnoreCase);

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("razer:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevicesByVendor(0x1532)
            .Where(d => HidSafe.MaxFeatureLen(d) >= 91)
            .GroupBy(d => (d.VendorID, d.ProductID));

        foreach (var g in groups)
        {
            try
            {
                var control = g.OrderByDescending(HidSafe.MaxFeatureLen).First();
                using var raw = RawHidFeature.TryOpen(control.DevicePath);
                if (raw == null)
                {
                    Diag.Log($"Razer {(ushort)g.Key.ProductID:X4}: raw open fail");
                    continue;
                }

                byte? battery = null;
                bool offline = false;
                byte usedTid = 0;
                foreach (byte tid in Tids)
                {
                    var (status, args) = Command(raw, tid, 0x07, 0x80, 0x02);
                    if (status == 0x02 && args.Length > 1)
                    {
                        battery = args[1];
                        usedTid = tid;
                        break;
                    }
                    if (status == 0x04)
                        offline = true;
                }

                if (battery == null && !offline)
                {
                    Diag.Log($"Razer {(ushort)g.Key.ProductID:X4}: no battery reply");
                    continue;
                }

                BatteryState batState = BatteryState.Unknown;
                if (battery != null)
                {
                    var (cStatus, cArgs) = Command(raw, usedTid, 0x07, 0x84, 0x02);
                    if (cStatus == 0x02 && cArgs.Length > 1)
                        batState = cArgs[1] == 1 ? BatteryState.Charging : BatteryState.Normal;
                }

                bool dormantZero = battery == 0 && batState != BatteryState.Charging;
                int? pct = battery is { } b && !dormantZero ? (b * 100 + 127) / 255 : null;
                string? note = offline
                    ? "鼠标休眠或未开启电源"
                    : dormantZero
                        ? "休眠中（晃动鼠标唤醒后可读取电量和调节参数）"
                        : null;

                string rawName = HidSafe.ProductName(control);
                string cleanName = CleanName(rawName) ?? $"Razer {(ushort)g.Key.ProductID:X4}";
                var kind = InferDeviceKind(cleanName);

                results.Add(new PeripheralDevice
                {
                    Id = $"razer:{(ushort)g.Key.ProductID:X4}",
                    VendorId = 0x1532,
                    ProductId = (ushort)g.Key.ProductID,
                    Name = cleanName,
                    Kind = kind,
                    Provider = ProviderName,
                    Connected = true,
                    IsSleeping = offline || dormantZero,
                    Transport = rawName.Contains("Wired", StringComparison.OrdinalIgnoreCase) ? "USB" : "2.4G",
                    BatteryPercent = pct,
                    State = batState,
                    Note = note,
                    LastUpdated = DateTimeOffset.Now,
                });
            }
            catch (Exception ex)
            {
                Diag.Log($"Razer {(ushort)g.Key.ProductID:X4}: {ex.Message}");
            }
        }
        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParsePid(deviceId, out ushort pid))
        {
            caps.StatusMessage = "无效的 Razer 设备 ID。";
            return caps;
        }

        var control = FindControlDevice(pid);
        if (control == null)
        {
            caps.StatusMessage = "未找到 Razer 控制接口（请确认接收器或数据线已连接）。";
            return caps;
        }

        using var raw = RawHidFeature.TryOpen(control.DevicePath);
        if (raw == null)
        {
            caps.StatusMessage = "打开 Razer HID 特征接口失败。";
            return caps;
        }

        byte? tid = ProbeWorkingTid(raw);
        if (tid == null)
        {
            caps.IsOnline = false;
            caps.StatusMessage = "鼠标当前处于无线休眠或离线状态，请晃动鼠标唤醒后再点击「读取当前硬件参数」。";
            return caps;
        }

        caps.IsOnline = true;
        ReadCapabilities(raw, tid.Value, caps);
        caps.StatusMessage = caps.SupportsDpi || caps.SupportedPollingRates.Count > 0
            ? "已读取 Razer 实时硬件参数。"
            : "鼠标已响应，但未返回可识别的参数寄存器（请确认鼠标已唤醒）。";
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParsePid(deviceId, out ushort pid))
            return new DeviceTuningResult(false, "无效的 Razer 设备 ID。");

        var control = FindControlDevice(pid);
        if (control == null)
            return new DeviceTuningResult(false, "未找到 Razer 控制接口。");

        using var raw = RawHidFeature.TryOpen(control.DevicePath);
        if (raw == null)
            return new DeviceTuningResult(false, "打开 Razer HID 特征接口失败。");

        byte? tidOpt = ProbeWorkingTid(raw);
        if (tidOpt == null)
            return new DeviceTuningResult(false, "鼠标当前休眠或离线，请先晃动鼠标唤醒后再写入设置。");

        byte tid = tidOpt.Value;
        var errors = new List<string>();

        if (update.Dpi is { } dpi)
        {
            int x = Math.Clamp(dpi.X, 100, 35000);
            int y = Math.Clamp(dpi.Y, 100, 35000);
            bool dpiOk = TryWriteDpi(raw, tid, x, y);
            if (!dpiOk)
                errors.Add("DPI 写入被拒绝");
        }

        if (update.DpiStages != null || update.ActiveDpiStage != null)
        {
            var cur = new DeviceTuningCapabilities { DeviceId = deviceId };
            ReadDpiStages(raw, tid, cur);
            var stages = update.DpiStages is { Count: > 0 } ? update.DpiStages : cur.DpiStages;
            if (stages.Count > 0)
            {
                int active = Math.Clamp(update.ActiveDpiStage ?? cur.ActiveDpiStage, 0, stages.Count - 1);
                var stageArgs = BuildDpiStagesArgs(active, stages);
                var (st, _) = Command(raw, tid, 0x04, 0x06, 0x26, stageArgs);
                if (st != 0x02)
                    errors.Add("多档位 DPI 写入未成功");
            }
        }
        else if (update.Dpi is { } activeDpi)
        {
            // 如果只改了当前 DPI 且设备启用了板载档位，同步更新当前激活档位以防按键切档跳回旧值
            var cur = new DeviceTuningCapabilities { DeviceId = deviceId };
            ReadDpiStages(raw, tid, cur);
            if (cur.SupportsDpiStagesWrite && cur.DpiStages.Count > 0)
            {
                int idx = Math.Clamp(cur.ActiveDpiStage, 0, cur.DpiStages.Count - 1);
                cur.DpiStages[idx] = (Math.Clamp(activeDpi.X, 100, 35000), Math.Clamp(activeDpi.Y, 100, 35000));
                var stageArgs = BuildDpiStagesArgs(idx, cur.DpiStages);
                Command(raw, tid, 0x04, 0x06, 0x26, stageArgs);
            }
        }

        if (update.PollingRateHz is { } hz)
        {
            if (!TryWritePollingRate(raw, tid, hz))
                errors.Add($"回报率 {hz} Hz 写入失败");
        }

        if (update.SleepSeconds is { } sleepSec)
        {
            int clamped = Math.Clamp(sleepSec, 60, 900);
            var (st, _) = Command(raw, tid, 0x07, 0x03, 0x02, [(byte)(clamped >> 8), (byte)(clamped & 0xFF)]);
            if (st != 0x02)
                errors.Add("休眠超时写入失败");
        }

        if (update.LowBatteryWarningPercent is { } warnPct)
        {
            byte threshold = (byte)Math.Clamp((warnPct * 255 + 50) / 100, 0x0C, 0xFF);
            var (st, _) = Command(raw, tid, 0x07, 0x01, 0x02, [threshold, 0x00]);
            if (st != 0x02)
                (st, _) = Command(raw, tid, 0x07, 0x01, 0x01, [threshold]);
            if (st != 0x02)
                errors.Add("低电量警告阈值写入失败");
        }

        if (update.AsymmetricLod is { } asym)
        {
            int lift = Math.Clamp(asym.LiftOff, 2, 26);
            int land = Math.Clamp(asym.Landing, 1, lift - 1);
            if (asym.Enabled)
            {
                var (s1, _) = Command(raw, tid, 0x0B, 0x0B, 0x04, [0x00, 0x04, 0x04, 0x01]);
                var (s2, _) = Command(raw, tid, 0x0B, 0x05, 0x0A,
                    [0x00, 0x04, (byte)(lift - 1), (byte)(land - 1), 0, 0, 0, 0, 0, 0]);
                if (s1 != 0x02 && s2 != 0x02)
                    errors.Add("非对称 LOD 写入失败");
            }
            else
            {
                byte symLevel = (byte)Math.Clamp(update.LodCode ?? 1, 0, 2);
                ExplicitSymLodCache[deviceId] = symLevel;
                var (s1, _) = Command(raw, tid, 0x0B, 0x0B, 0x04, [0x00, 0x04, 0x01, symLevel]);
                if (s1 != 0x02)
                    errors.Add("关闭非对称 LOD 失败");
            }
        }
        else if (update.LodCode is { } lodCode)
        {
            byte symLevel = (byte)Math.Clamp(lodCode, 0, 2);
            ExplicitSymLodCache[deviceId] = symLevel;
            var (st, _) = Command(raw, tid, 0x0B, 0x0B, 0x04, [0x00, 0x04, 0x01, symLevel]);
            if (st != 0x02)
                errors.Add("静默高度 LOD 写入失败");
        }

        if (update.ButtonBinding is { } bind && bind.SlotIndex > 0)
        {
            if (!TryWriteButtonBinding(raw, tid, (byte)bind.SlotIndex, bind.Action))
                errors.Add($"按键 0x{bind.SlotIndex:X2} 重映射写入失败");
        }

        Thread.Sleep(35);
        var updated = QueryCapabilities(deviceId);
        return errors.Count == 0
            ? new DeviceTuningResult(true, "已写入 Razer 硬件并完成回读校验。", updated)
            : new DeviceTuningResult(false, string.Join("；", errors), updated);
    }

    private static void ReadCapabilities(RawHidFeature raw, byte tid, DeviceTuningCapabilities caps)
    {
        // 1. 当前 DPI (varstore=0x01 -> nostore=0x00)
        foreach (byte store in new byte[] { 0x01, 0x00 })
        {
            var (st, args) = Command(raw, tid, 0x04, 0x85, 0x07, [store]);
            if (st == 0x02 && ParseDpiReply(args) is { } dpi)
            {
                caps.SupportsDpi = true;
                caps.SupportsIndependentXY = true;
                caps.MinDpi = 100;
                caps.MaxDpi = 35000;
                caps.DpiStep = 50;
                caps.CurrentDpiX = dpi.X;
                caps.CurrentDpiY = dpi.Y;
                break;
            }
        }

        // 2. 板载 DPI 档位表
        ReadDpiStages(raw, tid, caps);

        // 3. 回报率：先试 HyperPolling 0xC0 (最高 8K)，失败回落经典 0x85 (最高 1K)
        var (extSt, extArgs) = Command(raw, tid, 0x00, 0xC0, 0x02, [0x00, 0x00]);
        int? extHz = extSt == 0x02 ? ParseExtendedPollingReply(extArgs) : null;
        if (extHz != null)
        {
            caps.SupportedPollingRates = [125, 250, 500, 1000, 2000, 4000, 8000];
            caps.CurrentPollingRate = extHz.Value;
        }
        else
        {
            var (clSt, clArgs) = Command(raw, tid, 0x00, 0x85, 0x01);
            int? clHz = clSt == 0x02 ? ParseClassicPollingReply(clArgs) : null;
            if (clHz != null)
            {
                caps.SupportedPollingRates = [125, 500, 1000];
                caps.CurrentPollingRate = clHz.Value;
            }
        }

        // 4. 闲置休眠超时 (秒)
        var (slSt, slArgs) = Command(raw, tid, 0x07, 0x83, 0x02);
        if (slSt == 0x02 && slArgs.Length >= 2)
        {
            int sec = (slArgs[0] << 8) | slArgs[1];
            if (sec is >= 30 and <= 3600)
            {
                caps.SupportsSleepTimeout = true;
                caps.MinSleepSeconds = 60;
                caps.MaxSleepSeconds = 900;
                caps.CurrentSleepSeconds = Math.Clamp(sec, 60, 900);
            }
        }

        // 5. 低电量硬件警告阈值 (0x07/0x81 args[0])
        var (lbSt, lbArgs) = Command(raw, tid, 0x07, 0x81, 0x02, [0x00, 0x00]);
        if (lbSt != 0x02)
            (lbSt, lbArgs) = Command(raw, tid, 0x07, 0x81, 0x01);
        if (lbSt == 0x02 && lbArgs.Length >= 1 && lbArgs[0] > 0)
        {
            caps.SupportsLowBatteryWarning = true;
            caps.CurrentLowBatteryWarningPercent = Math.Clamp((lbArgs[0] * 100 + 127) / 255, 5, 100);
        }

        // 6. 静默高度 LOD & 非对称抬起/着陆高度 (0x0B/0x85 & 0x0B/0x8B)
        ReadLodCapabilities(raw, tid, caps);

        // 7. 原生按键重映射 (0x02/0x8C 读取，0x02/0x0C tid=0x10 写入)
        ReadButtonBindings(raw, tid, caps);
    }

    private static void ReadDpiStages(RawHidFeature raw, byte tid, DeviceTuningCapabilities caps)
    {
        var (st, args) = Command(raw, tid, 0x04, 0x86, 0x26, [0x01]);
        if (st != 0x02) return;
        var parsed = ParseDpiStagesReply(args);
        if (parsed == null) return;
        caps.SupportsDpiStagesWrite = true;
        caps.DpiStages = parsed.Value.Stages;
        caps.ActiveDpiStage = parsed.Value.ActiveIndex;
    }

    private static bool TryWriteDpi(RawHidFeature raw, byte tid, int x, int y)
    {
        foreach (byte store in new byte[] { 0x01, 0x00 })
        {
            byte[] args = [store, (byte)(x >> 8), (byte)(x & 0xFF), (byte)(y >> 8), (byte)(y & 0xFF), 0x00, 0x00];
            var (st, _) = Command(raw, tid, 0x04, 0x05, 0x07, args);
            if (st == 0x02) return true;
        }
        return false;
    }

    private static bool TryWritePollingRate(RawHidFeature raw, byte tid, int hz)
    {
        if (MapExtendedPollingCode(hz) is { } extCode)
        {
            var (s0, _) = Command(raw, tid, 0x00, 0x40, 0x02, [0x00, extCode]);
            var (s1, _) = Command(raw, tid, 0x00, 0x40, 0x02, [0x01, extCode]);
            if (s0 == 0x02 || s1 == 0x02) return true;
        }
        if (MapClassicPollingCode(hz) is { } clCode)
        {
            var (st, _) = Command(raw, tid, 0x00, 0x05, 0x01, [clCode]);
            if (st == 0x02) return true;
        }
        return false;
    }

    private static byte? ProbeWorkingTid(RawHidFeature raw)
    {
        foreach (byte tid in Tids)
        {
            var (status, args) = Command(raw, tid, 0x07, 0x80, 0x02);
            if (status == 0x02 && args.Length > 1)
                return tid;
        }
        return null;
    }

    private static HidDevice? FindControlDevice(ushort pid)
    {
        return HidSafe.EnumerateDevices(0x1532, pid)
            .Where(d => HidSafe.MaxFeatureLen(d) >= 91)
            .OrderByDescending(HidSafe.MaxFeatureLen)
            .FirstOrDefault();
    }

    private static readonly (byte CtrlId, string Label, bool ReadOnly)[] RazerButtons =
    [
        (0x01, "左键 (Left)", true),
        (0x02, "右键 (Right)", false),
        (0x03, "中键 (Middle)", false),
        (0x04, "侧键后退 (M4)", false),
        (0x05, "侧键前进 (M5)", false),
        (0x09, "滚轮上 (Scroll Up)", false),
        (0x0A, "滚轮下 (Scroll Down)", false),
        (0x60, "DPI 切换键", false),
    ];

    private static readonly (string Label, byte FnType, byte Len, byte Val)[] RazerStandardActions =
    [
        ("左键", 0x01, 0x01, 0x01),
        ("右键", 0x01, 0x01, 0x02),
        ("中键", 0x01, 0x01, 0x03),
        ("后退", 0x01, 0x01, 0x04),
        ("前进", 0x01, 0x01, 0x05),
        ("滚轮上", 0x01, 0x01, 0x09),
        ("滚轮下", 0x01, 0x01, 0x0A),
        ("DPI 循环", 0x06, 0x01, 0x01),
        ("DPI +", 0x06, 0x01, 0x02),
        ("DPI -", 0x06, 0x01, 0x03),
        ("禁用", 0x00, 0x00, 0x00),
    ];

    private static void ReadLodCapabilities(RawHidFeature raw, byte tid, DeviceTuningCapabilities caps)
    {
        var (st85, args85) = Command(raw, tid, 0x0B, 0x85, 0x05, []);
        if (st85 != 0x02)
            (st85, args85) = Command(raw, tid, 0x0B, 0x85, 0x0A, [0x00, 0x04]);
        var (st8B, args8B) = Command(raw, tid, 0x0B, 0x8B, 0x04, [0x00, 0x04]);
        if (st85 != 0x02 && st8B != 0x02) return;

        caps.SupportedLodOptions =
        [
            new(0, "低 (Low)"),
            new(1, "中 (Medium)"),
            new(2, "高 (High)"),
        ];

        int? explicitCached = ExplicitSymLodCache.TryGetValue(caps.DeviceId, out int cl) ? cl : null;
        var decoded = DecodeLodState(st8B == 0x02 ? args8B : null, st85 == 0x02 ? args85 : null, explicitCached);
        caps.CurrentLod = decoded.SymLod;
        if (decoded.SupportsAsymmetric)
        {
            caps.SupportsAsymmetricLod = true;
            caps.AsymmetricLodEnabled = decoded.AsymEnabled;
            caps.AsymmetricLiftOff = decoded.LiftOff;
            caps.AsymmetricLanding = decoded.Landing;
        }
    }

    internal static (int SymLod, bool SupportsAsymmetric, bool AsymEnabled, int LiftOff, int Landing) DecodeLodState(
        byte[]? args8B, byte[]? args85, int? explicitSymLod = null)
    {
        bool asymOn = args8B is { Length: >= 3 } && args8B[2] == 0x04;
        int symLod = explicitSymLod ?? 1;
        bool supportsAsym = args85 != null || args8B != null;
        int lift = 10;
        int land = 5;

        if (args85 is { Length: >= 5 })
        {
            int rawSym = Math.Clamp((int)args85[2], 0, 2);
            bool allZeroStore = args85[2] == 0 && args85[3] == 0 && args85[4] == 0;
            if (!allZeroStore)
                symLod = rawSym;
            else if (explicitSymLod is { } exp)
                symLod = Math.Clamp(exp, 0, 2);

            if (args85[3] >= 1)
            {
                lift = Math.Clamp(args85[3] + 1, 2, 26);
                land = Math.Clamp(args85[4] + 1, 1, lift - 1);
            }
        }
        return (symLod, supportsAsym, asymOn, lift, land);
    }

    private static void ReadButtonBindings(RawHidFeature raw, byte tid, DeviceTuningCapabilities caps)
    {
        var avail = RazerStandardActions.Select(a => a.Label).ToList();
        foreach (var (ctrlId, label, readOnly) in RazerButtons)
        {
            var (st, args) = Command(raw, tid, 0x02, 0x8C, 0x0A, [0x01, ctrlId, 0x00]);
            if (st != 0x02 || args.Length < 6) continue;
            string current = DecodeButtonAction(args);
            caps.ButtonBindings.Add(new ButtonBindingSlot(ctrlId, label, current, avail, readOnly));
        }

        if (caps.ButtonBindings.Count > 0)
            caps.MacroCapabilityNote = "已启用 Razer 硬件原生按键重映射与组合键写入 (tid=0x10)；多步复杂宏可通过「本地宏/软件回放」配合配置文件使用。";
    }

    private static string DecodeButtonAction(byte[] args)
    {
        byte fnType = args[3];
        byte len = args[4];
        byte val = args[5];
        foreach (var (label, stdType, _, stdVal) in RazerStandardActions)
        {
            if (stdType == fnType && (fnType == 0x00 || stdVal == val))
                return label;
        }
        if (fnType == 0x02)
        {
            if (len >= 2 && args.Length > 6 && args[6] != 0)
                return HidKeyCatalog.FormatKeyboardBinding(args[5], args[6]);
            if (val != 0)
                return HidKeyCatalog.FormatKeyboardBinding(0, val);
        }
        return $"自定义 ({fnType:X2}:{val:X2})";
    }

    private static bool TryWriteButtonBinding(RawHidFeature raw, byte probedTid, byte ctrlId, string action)
    {
        if (ctrlId == 0x01) return false;

        byte[] reqArgs;
        if (HidKeyCatalog.TryParseKeyboardBinding(action, out byte mod, out byte usage))
        {
            reqArgs = [0x01, ctrlId, 0x00, 0x02, 0x02, mod, usage, 0x00, 0x00, 0x00];
        }
        else
        {
            byte fnType = 0, len = 0, val = 0;
            bool matched = false;
            foreach (var (label, stdType, stdLen, stdVal) in RazerStandardActions)
            {
                if (label == action)
                {
                    fnType = stdType;
                    len = stdLen;
                    val = stdVal;
                    matched = true;
                    break;
                }
            }
            if (!matched) return false;
            reqArgs = [0x01, ctrlId, 0x00, fnType, len, val, 0x00, 0x00, 0x00, 0x00];
        }

        // OpenMouse 实测 Viper V3 Pro 等 Razer 鼠必须使用 tid=0x10 写入按键绑定，切勿用 0x1F
        var (st, _) = Command(raw, 0x10, 0x02, 0x0C, 0x0A, reqArgs);
        if (st == 0x02) return true;
        if (probedTid != 0x10 && probedTid != 0x1F)
        {
            (st, _) = Command(raw, probedTid, 0x02, 0x0C, 0x0A, reqArgs);
            return st == 0x02;
        }
        return false;
    }

    private static bool TryParsePid(string deviceId, out ushort pid)
    {
        pid = 0;
        var parts = deviceId.Split(':');
        return parts.Length >= 2 && ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
    }

    private static DeviceKind InferDeviceKind(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("blackwidow") || lower.Contains("huntsman") || lower.Contains("deathstalker") ||
            lower.Contains("ornata") || lower.Contains("keyboard"))
            return DeviceKind.Keyboard;
        if (lower.Contains("blackshark") || lower.Contains("kraken") || lower.Contains("barracuda") ||
            lower.Contains("nari") || lower.Contains("headset"))
            return DeviceKind.Headset;
        return DeviceKind.Mouse;
    }

    private static string? CleanName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (var suffix in new[] { " Dongle", " (Wired)", " (Wireless)", " Wireless Receiver", " Receiver", " HyperSpeed Dongle" })
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                s = s[..^suffix.Length];
        return s.Trim();
    }

    internal static byte[] BuildPacket(byte tid, byte cls, byte id, byte size, ReadOnlySpan<byte> reqArgs = default)
    {
        var report = new byte[91];
        report[2] = tid;
        report[6] = size;
        report[7] = cls;
        report[8] = id;
        if (!reqArgs.IsEmpty)
            reqArgs[..Math.Min(reqArgs.Length, 80)].CopyTo(report.AsSpan(9));
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= report[i];
        report[89] = crc;
        return report;
    }

    internal static (int X, int Y)? ParseDpiReply(byte[] args)
    {
        if (args.Length < 5) return null;
        int x = (args[1] << 8) | args[2];
        int y = (args[3] << 8) | args[4];
        if (y == 0) y = x;
        if (x is < 50 or > 45000) return null;
        return (x, Math.Clamp(y, 50, 45000));
    }

    internal static (int ActiveIndex, List<(int X, int Y)> Stages)? ParseDpiStagesReply(byte[] args)
    {
        if (args.Length < 10) return null;
        int count = args[2];
        if (count is < 1 or > 5) return null;
        var list = new List<(int X, int Y)>(count);
        for (int i = 0; i < count; i++)
        {
            int off = 3 + i * 7;
            if (off + 4 >= args.Length) return null;
            int x = (args[off + 1] << 8) | args[off + 2];
            int y = (args[off + 3] << 8) | args[off + 4];
            if (y == 0) y = x;
            if (x is < 50 or > 45000) return null;
            list.Add((x, Math.Clamp(y, 50, 45000)));
        }
        int active = Math.Clamp(args[1] - 1, 0, list.Count - 1);
        return (active, list);
    }

    internal static byte[] BuildDpiStagesArgs(int activeZeroBased, IReadOnlyList<(int X, int Y)> stages)
    {
        int count = Math.Clamp(stages.Count, 1, 5);
        var args = new byte[0x26];
        args[0] = 0x01;
        args[1] = (byte)(Math.Clamp(activeZeroBased, 0, count - 1) + 1);
        args[2] = (byte)count;
        for (int i = 0; i < count; i++)
        {
            int off = 3 + i * 7;
            int x = Math.Clamp(stages[i].X, 100, 35000);
            int y = Math.Clamp(stages[i].Y, 100, 35000);
            args[off] = (byte)(i + 1);
            args[off + 1] = (byte)(x >> 8);
            args[off + 2] = (byte)(x & 0xFF);
            args[off + 3] = (byte)(y >> 8);
            args[off + 4] = (byte)(y & 0xFF);
        }
        return args;
    }

    internal static int? ParseExtendedPollingReply(byte[] args)
    {
        if (args.Length < 2) return null;
        byte code = args[1] != 0 ? args[1] : args[0];
        return code switch
        {
            0x01 => 8000,
            0x02 => 4000,
            0x04 => 2000,
            0x08 => 1000,
            0x10 => 500,
            0x20 => 250,
            0x40 => 125,
            _ => null,
        };
    }

    internal static byte? MapExtendedPollingCode(int hz) => hz switch
    {
        8000 => 0x01,
        4000 => 0x02,
        2000 => 0x04,
        1000 => 0x08,
        500 => 0x10,
        250 => 0x20,
        125 => 0x40,
        _ => null,
    };

    internal static int? ParseClassicPollingReply(byte[] args)
    {
        if (args.Length < 1) return null;
        return args[0] switch
        {
            0x01 => 1000,
            0x02 => 500,
            0x04 => 250,
            0x08 => 125,
            _ => null,
        };
    }

    internal static byte? MapClassicPollingCode(int hz) => hz switch
    {
        1000 => 0x01,
        500 => 0x02,
        250 => 0x04,
        125 => 0x08,
        _ => null,
    };

    /// <summary>发送 91 字节 Feature 命令并返回 (status, args[0..79])。</summary>
    private static (byte Status, byte[] Args) Command(RawHidFeature raw, byte tid, byte cls, byte id, byte size, ReadOnlySpan<byte> reqArgs = default)
    {
        var report = BuildPacket(tid, cls, id, size, reqArgs);

        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (!raw.SetFeature(report))
                return (0xFF, []);
            Thread.Sleep(attempt == 0 ? 20 : 40);
            var resp = raw.GetFeature(91);
            if (resp == null || resp.Length < 91) continue;
            byte rCrc = 0;
            for (int i = 3; i <= 88; i++) rCrc ^= resp[i];
            if (resp[2] != tid || resp[3] != 0 || resp[4] != 0 || resp[6] != size ||
                resp[7] != cls || resp[8] != id || resp[89] != rCrc)
                continue;
            byte status = resp[1];
            if (status == 0x01) continue;       // busy
            if (status == 0x02) return (status, resp[9..89]);
            return (status, []);
        }
        return (0xFF, []);
    }
}
