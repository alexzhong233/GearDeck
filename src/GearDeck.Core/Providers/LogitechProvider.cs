using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 罗技 HID++ 2.0 协议：
/// 支持电量 (0x1004/0x1000)、设备真实名称 (0x0005)、板载/主机模式切换 (0x8100)、
/// DPI / XY 独立 DPI / 静默高度 LOD (0x2202 优先, 回落 0x2201) 以及回报率 125..8000 Hz (0x8061 优先, 回落 0x8060)。
/// </summary>
public sealed class LogitechProvider : ITunablePeripheralProvider
{
    public const string ProviderName = "Logitech";
    private const byte SwId = 0x0D;
    private static readonly int[] ExtendedRatesHz = [125, 250, 500, 1000, 2000, 4000, 8000];
    private static readonly ConcurrentDictionary<(ushort Pid, byte Idx), (string Name, DeviceKind Kind)> MetaCache = new();

    public string Name => ProviderName;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("logi:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevicesByVendor(0x046D)
            .Where(d => HidSafe.MaxOutputLen(d) is 7 or 20 or 21 or 64)
            .GroupBy(d => (d.VendorID, d.ProductID));

        foreach (var g in groups)
        {
            ushort pid = (ushort)g.Key.ProductID;
            var (shorts, longs) = OpenChannels(g);
            try
            {
                if (shorts.Count == 0 && longs.Count == 0)
                {
                    Diag.Log($"Logitech {pid:X4}: no channels");
                    continue;
                }
                var h = new Hidpp(shorts, longs);
                var anyDevice = g.First();
                string product = HidSafe.ProductName(anyDevice);
                bool passthrough = pid == 0xC54F;
                bool receiver = passthrough || product.Contains("Receiver", StringComparison.OrdinalIgnoreCase) || product.Contains("Lightspeed", StringComparison.OrdinalIgnoreCase);
                Diag.Log($"Logitech {pid:X4}: shorts={shorts.Count} longs={longs.Count} product={product}");

                var list = (passthrough ? new byte[] { 1 }
                    : receiver ? new byte[] { 1, 2, 3, 4, 5, 6 }
                    : new byte[] { 0xFF }).ToList();
                if (!receiver) list.AddRange(new byte[] { 1, 2, 3, 4, 5, 6 });

                foreach (byte idx in list.Distinct())
                {
                    var battery = ReadBattery(h, idx, out byte chargeStatus);
                    Diag.Log($"Logitech {pid:X4}: idx={idx:X2} battery={battery}");
                    if (battery == null)
                    {
                        if (passthrough && idx == 1)
                        {
                            var sleepMeta = MetaCache.TryGetValue((pid, idx), out var cachedMeta)
                                ? cachedMeta
                                : (product.Contains("SUPERSTRIKE", StringComparison.OrdinalIgnoreCase)
                                    ? product.Trim()
                                    : "Logitech PRO X3 SUPERSTRIKE", DeviceKind.Mouse);
                            results.Add(new PeripheralDevice
                            {
                                Id = $"logi:{pid:X4}:{idx:X2}",
                                VendorId = 0x046D,
                                ProductId = pid,
                                Name = sleepMeta.Item1,
                                Kind = sleepMeta.Item2,
                                Provider = ProviderName,
                                Connected = true,
                                IsSleeping = true,
                                Transport = "2.4G",
                                BatteryPercent = null,
                                State = BatteryState.Unknown,
                                Note = "罗技接收器在线（鼠标休眠中，晃动唤醒后读取电量与 HITS 参数）",
                                LastUpdated = DateTimeOffset.Now,
                            });
                            break;
                        }
                        continue;
                    }

                    var meta = ResolveMeta(h, pid, idx, product);
                    results.Add(new PeripheralDevice
                    {
                        Id = $"logi:{pid:X4}:{idx:X2}",
                        VendorId = 0x046D,
                        ProductId = pid,
                        Name = meta.Name,
                        Kind = meta.Kind,
                        Provider = ProviderName,
                        Connected = true,
                        Transport = idx == 0xFF ? "USB/BT" : "2.4G",
                        BatteryPercent = battery,
                        State = chargeStatus switch
                        {
                            1 => BatteryState.Charging,
                            2 => BatteryState.FullyCharged,
                            _ => BatteryState.Normal,
                        },
                        LastUpdated = DateTimeOffset.Now,
                    });
                    if (!receiver || passthrough) break;
                }
            }
            catch (Exception ex)
            {
                Diag.Log($"Logitech {pid:X4}: {ex.Message}");
            }
            finally
            {
                foreach (var c in shorts.Concat(longs)) c.Dispose();
            }
        }
        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParseId(deviceId, out ushort pid, out byte idx))
        {
            caps.StatusMessage = "无效的罗技设备 ID。";
            return caps;
        }

        var devices = HidSafe.EnumerateDevices(0x046D, pid)
            .Where(d => HidSafe.MaxOutputLen(d) is 7 or 20 or 21 or 64)
            .ToList();
        if (devices.Count == 0)
        {
            caps.StatusMessage = "未找到罗技 HID++ 设备（接收器或数据线可能已拔出）。";
            return caps;
        }

        var (shorts, longs) = OpenChannels(devices);
        try
        {
            if (shorts.Count == 0 && longs.Count == 0)
            {
                caps.StatusMessage = "无法打开罗技 HID++ 控制通道。";
                return caps;
            }

            var h = new Hidpp(shorts, longs);
            // 先查电量/ping，确定无线鼠标当前在线且未休眠
            var battery = ReadBattery(h, idx, out _);
            byte f2202 = h.FeatureIndex(idx, 0x2202);
            byte f2201 = h.FeatureIndex(idx, 0x2201);
            byte f8061 = h.FeatureIndex(idx, 0x8061);
            byte f8060 = h.FeatureIndex(idx, 0x8060);
            byte f8100 = h.FeatureIndex(idx, 0x8100);
            byte f1B0C = h.FeatureIndex(idx, 0x1B0C);

            if (battery == null && f2202 == 0 && f2201 == 0 && f8061 == 0 && f8060 == 0 && f1B0C == 0)
            {
                caps.IsOnline = false;
                caps.StatusMessage = "无线鼠标当前休眠或未唤醒，请晃动鼠标后点击「读取当前硬件参数」。";
                return caps;
            }

            caps.IsOnline = true;
            ReadCapabilities(h, idx, f8100, f2202, f2201, f8061, f8060, f1B0C, caps);
            caps.StatusMessage = caps.SupportsDpi || caps.SupportedPollingRates.Count > 0
                ? (caps.SupportsHostModeSwitch && !caps.IsHostMode
                    ? "当前鼠标处于板载内存模式；直接调节参数时将自动切换为主机软件模式以实时生效。"
                    : "已读取罗技 HID++ 实时硬件参数。")
                : "设备已响应，但未开放可调 DPI / 回报率特性。";
            return caps;
        }
        finally
        {
            foreach (var c in shorts.Concat(longs)) c.Dispose();
        }
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParseId(deviceId, out ushort pid, out byte idx))
            return new DeviceTuningResult(false, "无效的罗技设备 ID。");

        var devices = HidSafe.EnumerateDevices(0x046D, pid)
            .Where(d => HidSafe.MaxOutputLen(d) is 7 or 20 or 21 or 64)
            .ToList();
        var (shorts, longs) = OpenChannels(devices);
        try
        {
            if (shorts.Count == 0 && longs.Count == 0)
                return new DeviceTuningResult(false, "打开罗技 HID++ 控制通道失败。");

            var h = new Hidpp(shorts, longs);
            byte f8100 = h.FeatureIndex(idx, 0x8100);
            byte f2202 = h.FeatureIndex(idx, 0x2202);
            byte f2201 = h.FeatureIndex(idx, 0x2201);
            byte f8061 = h.FeatureIndex(idx, 0x8061);
            byte f8060 = h.FeatureIndex(idx, 0x8060);
            byte f1B0C = h.FeatureIndex(idx, 0x1B0C);

            var notes = new List<string>();
            var errors = new List<string>();

            // 1. 显式或自动切换主机模式 (Host Mode = 2, Onboard Mode = 1) 与板载配置槽位 (fn 3)
            bool writingLiveParams = update.Dpi != null || update.PollingRateHz != null || update.LodCode != null;
            if (f8100 != 0)
            {
                if (update.ActiveOnboardProfile is { } slot && update.SetHostMode != true)
                {
                    var mReply = h.Call(idx, f8100, 2, null, out _);
                    if (mReply is { Length: >= 1 } && mReply[0] != 1)
                    {
                        h.Call(idx, f8100, 1, [1], out _);
                        Thread.Sleep(35);
                    }
                    ushort sector = (ushort)(Math.Clamp(slot, 0, 4) + 1);
                    h.Call(idx, f8100, 3, [(byte)(sector >> 8), (byte)(sector & 0xFF)], out byte profErr);
                    if (profErr != 0)
                        errors.Add($"板载配置切换失败 (err=0x{profErr:X2})");
                    else
                    {
                        Thread.Sleep(45);
                        notes.Add($"已切换为板载配置 {sector}");
                    }
                }
                else if (update.SetHostMode is { } explicitHost)
                {
                    byte targetMode = explicitHost ? (byte)2 : (byte)1;
                    h.Call(idx, f8100, 1, [targetMode], out byte err);
                    if (err != 0)
                        errors.Add($"模式切换失败 (err=0x{err:X2})");
                    else
                        notes.Add(explicitHost ? "已切换为主机软件控制模式" : "已切换为板载内存模式");
                }
                else if (writingLiveParams)
                {
                    var mReply = h.Call(idx, f8100, 2, null, out _);
                    if (mReply is { Length: >= 1 } && mReply[0] == 1)
                    {
                        h.Call(idx, f8100, 1, [2], out byte modeErr);
                        if (modeErr == 0)
                        {
                            Thread.Sleep(30);
                            notes.Add("已自动切换为主机软件控制模式");
                        }
                    }
                }
            }

            // 2. 写入 DPI / XY / LOD
            if (update.Dpi != null || update.LodCode != null)
            {
                bool dpiOk = false;
                if (f2202 != 0)
                {
                    var cur = ReadExtendedDpi(h, idx, f2202);
                    int targetX = Math.Clamp(update.Dpi?.X ?? cur?.X ?? 800, 100, 44000);
                    int targetY = Math.Clamp(update.Dpi?.Y ?? cur?.Y ?? targetX, 100, 44000);
                    int targetLod = Math.Clamp(update.LodCode ?? cur?.Lod ?? 2, 1, 3);

                    byte[] payload6 =
                    [
                        0x00,
                        (byte)(targetX >> 8), (byte)(targetX & 0xFF),
                        (byte)(targetY >> 8), (byte)(targetY & 0xFF),
                        (byte)targetLod,
                    ];
                    h.Call(idx, f2202, 6, payload6, out byte err6);
                    if (err6 == 0)
                    {
                        dpiOk = true;
                    }
                    else
                    {
                        byte[] payloadAlt =
                        [
                            (byte)(targetX >> 8), (byte)(targetX & 0xFF),
                            (byte)(targetY >> 8), (byte)(targetY & 0xFF),
                            (byte)targetLod,
                        ];
                        h.Call(idx, f2202, 2, payloadAlt, out byte err2);
                        dpiOk = err2 == 0;
                    }
                }

                if (!dpiOk && f2201 != 0 && update.Dpi is { } dpiStd)
                {
                    int target = Math.Clamp(dpiStd.X, 100, 44000);
                    h.Call(idx, f2201, 3, [0x00, (byte)(target >> 8), (byte)(target & 0xFF)], out byte errStd);
                    dpiOk = errStd == 0;
                }

                if (!dpiOk)
                    errors.Add("DPI / LOD 写入被设备拒绝");
            }

            // 3. 写入回报率 (8K 0x8061 优先，传统 0x8060 回落)
            if (update.PollingRateHz is { } hz)
            {
                bool rateOk = false;
                byte connType = idx == 0xFF ? (byte)0 : (byte)1;
                if (f8061 != 0 && MapExtendedRateCode(hz) is { } code)
                {
                    h.Call(idx, f8061, 3, [code], out byte err3);
                    if (err3 == 0)
                        rateOk = true;
                    else
                    {
                        h.Call(idx, f8061, 3, [connType, code], out byte err3Conn);
                        rateOk = err3Conn == 0;
                    }
                }
                if (!rateOk && f8060 != 0 && hz <= 1000 && hz >= 125)
                {
                    byte ms = (byte)Math.Clamp(1000 / hz, 1, 8);
                    h.Call(idx, f8060, 2, [ms], out byte err60);
                    rateOk = err60 == 0;
                }
                if (!rateOk)
                    errors.Add($"回报率 {hz} Hz 写入未生效");
            }

            // 4. 写入 HITS 电磁微动触发点 / Rapid Trigger / 触感 (0x1B0C)
            if (update.AnalogSwitches is { } analog)
            {
                if (f1B0C == 0)
                {
                    errors.Add("当前罗技设备未开放 0x1B0C 电磁微动特性");
                }
                else
                {
                    var info0 = h.Call(idx, f1B0C, 0, null, out _);
                    var (_, maxAct, maxRt, maxHap) = ParseAnalogCapabilitiesReply(info0);

                    bool okLeft = WriteAnalogButton(h, idx, f1B0C, 0, analog.Left, maxAct, maxRt, maxHap, out byte errL);
                    var rightTarget = analog.IndependentButtons
                        ? analog.Right with { ButtonIndex = 1 }
                        : analog.Left with { ButtonIndex = 1 };
                    bool okRight = WriteAnalogButton(h, idx, f1B0C, 1, rightTarget, maxAct, maxRt, maxHap, out byte errR);

                    if (!okLeft || !okRight)
                        errors.Add($"HITS 电磁微动写入失败 (L=0x{errL:X2}, R=0x{errR:X2})");
                    else
                        notes.Add(analog.IndependentButtons ? "已写入左右独立 HITS 参数" : "已同步写入双键 HITS 参数");
                }
            }

            Thread.Sleep(45);
            var refreshed = new DeviceTuningCapabilities { DeviceId = deviceId, IsOnline = true };
            ReadCapabilities(h, idx, f8100, f2202, f2201, f8061, f8060, f1B0C, refreshed);
            refreshed.StatusMessage = errors.Count == 0
                ? (notes.Count > 0 ? $"{string.Join("，", notes)}，并已写入硬件。" : "已写入罗技硬件并完成回读校验。")
                : string.Join("；", errors);

            return new DeviceTuningResult(errors.Count == 0, refreshed.StatusMessage!, refreshed);
        }
        finally
        {
            foreach (var c in shorts.Concat(longs)) c.Dispose();
        }
    }

    private static void ReadCapabilities(
        Hidpp h, byte idx,
        byte f8100, byte f2202, byte f2201, byte f8061, byte f8060, byte f1B0C,
        DeviceTuningCapabilities caps)
    {
        if (f8100 != 0)
        {
            var m = h.Call(idx, f8100, 2, null, out _);
            if (m is { Length: >= 1 })
            {
                caps.SupportsHostModeSwitch = true;
                // 1 = Onboard Mode, 2 = Host Mode (部分固件用 0 表示 Host/Disabled)
                caps.IsHostMode = m[0] != 1;
            }

            // fn 0 (getInfo) -> payload[3] = profileCount
            var info = h.Call(idx, f8100, 0, null, out _);
            int pCount = info is { Length: >= 4 } && info[3] is > 0 and <= 5 ? info[3] : 0;
            if (pCount > 0)
            {
                caps.OnboardProfileCount = pCount;
                caps.OnboardProfileNames = Enumerable.Range(1, pCount).Select(i => $"板载配置 {i}").ToList();

                // fn 4 (getCurrentProfile) -> payload[0..1] = current 16-bit sector (低字节为 1..N)
                var cur = h.Call(idx, f8100, 4, null, out _);
                if (cur is { Length: >= 2 })
                {
                    int sector = (cur[0] << 8) | cur[1];
                    int oneBased = sector & 0xFF;
                    if (oneBased >= 1 && oneBased <= pCount)
                        caps.ActiveOnboardProfile = oneBased - 1;
                }
            }
        }

        if (f2202 != 0)
        {
            caps.SupportsDpi = true;
            caps.SupportsIndependentXY = true;
            caps.MinDpi = 100;
            caps.MaxDpi = 44000;
            caps.DpiStep = 50;

            var listReply = h.Call(idx, f2202, 2, [0x00, 0x00], out _)
                            ?? h.Call(idx, f2202, 1, [0x00], out _);
            if (listReply is { Length: >= 6 })
            {
                var range = ParseDpiRangeList(listReply, startOffset: listReply[0] == 0 ? 2 : 1);
                if (range != null)
                {
                    caps.MinDpi = range.Value.Min;
                    caps.MaxDpi = range.Value.Max;
                    caps.DpiStep = Math.Max(1, range.Value.Step);
                }
            }

            var ext = ReadExtendedDpi(h, idx, f2202);
            if (ext != null)
            {
                caps.CurrentDpiX = ext.Value.X;
                caps.CurrentDpiY = ext.Value.Y;
                caps.CurrentLod = ext.Value.Lod is >= 1 and <= 3 ? ext.Value.Lod : 2;
            }
            caps.SupportedLodOptions =
            [
                new(1, "低 (Low)"),
                new(2, "中 (Medium)"),
                new(3, "高 (High)"),
            ];
            if (caps.CurrentLod == 0) caps.CurrentLod = 2;
        }
        else if (f2201 != 0)
        {
            caps.SupportsDpi = true;
            caps.SupportsIndependentXY = false;
            caps.MinDpi = 100;
            caps.MaxDpi = 25600;
            caps.DpiStep = 50;

            var listReply = h.Call(idx, f2201, 1, [0x00], out _);
            if (listReply != null)
            {
                var range = ParseDpiRangeList(listReply, startOffset: 1);
                if (range != null)
                {
                    caps.MinDpi = range.Value.Min;
                    caps.MaxDpi = range.Value.Max;
                    caps.DpiStep = Math.Max(1, range.Value.Step);
                }
            }

            var dpiReply = h.Call(idx, f2201, 2, [0x00], out _);
            if (dpiReply is { Length: >= 3 })
            {
                int d = (dpiReply[1] << 8) | dpiReply[2];
                if (d is >= 50 and <= 50000)
                {
                    caps.CurrentDpiX = d;
                    caps.CurrentDpiY = d;
                }
            }
        }

        if (f8061 != 0)
        {
            byte connType = idx == 0xFF ? (byte)0 : (byte)1;
            var maskReply = h.Call(idx, f8061, 1, null, out _)
                            ?? h.Call(idx, f8061, 0, [connType], out _);
            var parsedRates = maskReply is { Length: >= 1 } ? ParseExtendedRateBitmask(maskReply[0]) : [];
            if (parsedRates.Count == 0 && maskReply is { Length: >= 2 })
                parsedRates = ParseExtendedRateBitmask(maskReply[1]);
            caps.SupportedPollingRates = parsedRates.Count > 0
                ? parsedRates
                : [125, 250, 500, 1000, 2000, 4000, 8000];

            var curRateReply = h.Call(idx, f8061, 2, [connType], out _)
                               ?? h.Call(idx, f8061, 2, null, out _);
            if (curRateReply is { Length: >= 1 })
            {
                int code = curRateReply[0];
                if ((uint)code < (uint)ExtendedRatesHz.Length)
                    caps.CurrentPollingRate = ExtendedRatesHz[code];
                else if (curRateReply.Length >= 2 && (uint)curRateReply[1] < (uint)ExtendedRatesHz.Length)
                    caps.CurrentPollingRate = ExtendedRatesHz[curRateReply[1]];
            }
        }
        else if (f8060 != 0)
        {
            var listReply = h.Call(idx, f8060, 0, null, out _);
            if (listReply is { Length: >= 1 })
                caps.SupportedPollingRates = ParseClassicRateBitmask(listReply[0]);
            if (caps.SupportedPollingRates.Count == 0)
                caps.SupportedPollingRates = [125, 250, 500, 1000];

            var curReply = h.Call(idx, f8060, 1, null, out _);
            if (curReply is { Length: >= 1 } && curReply[0] is >= 1 and <= 8)
                caps.CurrentPollingRate = 1000 / curReply[0];
        }

        if (f1B0C != 0)
        {
            var info0 = h.Call(idx, f1B0C, 0, null, out _);
            var (btnMask, maxAct, maxRt, maxHap) = ParseAnalogCapabilitiesReply(info0);
            if ((btnMask & 0x03) != 0)
            {
                var rLeft = h.Call(idx, f1B0C, 2, [0x00], out _);
                var rRight = h.Call(idx, f1B0C, 2, [0x01], out _);
                var leftCfg = ParseAnalogButtonReply(0, rLeft, maxAct, maxRt, maxHap);
                var rightCfg = ParseAnalogButtonReply(1, rRight, maxAct, maxRt, maxHap);
                if (leftCfg != null || rightCfg != null)
                {
                    caps.SupportsAnalogSwitchTuning = true;
                    caps.AnalogSwitchTechName = "HITS Tuning (电磁微动 · HID++ 0x1B0C)";
                    caps.AnalogMaxActuation = maxAct;
                    caps.AnalogMaxRapidTrigger = maxRt;
                    caps.AnalogMaxHaptics = maxHap;
                    caps.SupportsAnalogHapticsSwitch = false;
                    caps.LeftAnalogButton = leftCfg ?? rightCfg! with { ButtonIndex = 0 };
                    caps.RightAnalogButton = rightCfg ?? leftCfg! with { ButtonIndex = 1 };
                }
            }
        }
    }

    internal static (int ButtonMask, int MaxActuation, int MaxRapidTrigger, int MaxHaptics) ParseAnalogCapabilitiesReply(byte[]? p)
    {
        if (p == null || p.Length < 5)
            return (0x03, 10, 5, 5);
        int mask = (p[0] << 8) | p[1];
        int maxAct = Math.Clamp(p[2] >> 2, 1, 20);
        int maxRt = Math.Clamp(p[3] >> 2, 1, 10);
        int maxHap = Math.Clamp(p[4] >> 2, 1, 10);
        return (mask == 0 ? 0x03 : mask, maxAct, maxRt, maxHap);
    }

    internal static AnalogButtonConfig? ParseAnalogButtonReply(int btnIdx, byte[]? p, int maxAct, int maxRt, int maxHap)
    {
        if (p == null || p.Length < 4)
            return null;
        int act = Math.Clamp(p[1] >> 2, 1, maxAct);
        bool rtEnabled = (p[2] & 0x01) != 0;
        int rt = Math.Clamp(p[2] >> 2, 1, maxRt);
        int hap = Math.Clamp(p[3] >> 2, 0, maxHap);
        return new AnalogButtonConfig(btnIdx, act, rtEnabled, rt, hap > 0, hap);
    }

    internal static byte[] BuildAnalogWritePayload(int btnIdx, AnalogButtonConfig cfg, int maxAct, int maxRt, int maxHap)
    {
        byte wireAct = (byte)((Math.Clamp(cfg.Actuation, 1, maxAct) & 0x3F) << 2);
        byte wireRt = (byte)(((Math.Clamp(cfg.RapidTrigger, 1, maxRt) & 0x3F) << 2) | (cfg.RapidTriggerEnabled ? 0x01 : 0x00));
        byte wireHap = (byte)((Math.Clamp(cfg.Haptics, 0, maxHap) & 0x3F) << 2);
        return [(byte)btnIdx, wireAct, wireRt, wireHap];
    }

    private static bool WriteAnalogButton(
        Hidpp h, byte idx, byte f1B0C, int btnIdx,
        AnalogButtonConfig cfg, int maxAct, int maxRt, int maxHap, out byte err)
    {
        byte[] payload = BuildAnalogWritePayload(btnIdx, cfg, maxAct, maxRt, maxHap);
        h.Call(idx, f1B0C, 1, payload, out err);
        return err == 0;
    }

    private static (int X, int Y, int Lod)? ReadExtendedDpi(Hidpp h, byte idx, byte f2202)
    {
        foreach (byte fn in new byte[] { 5, 1 })
        {
            var reply = h.Call(idx, f2202, fn, [0x00], out _);
            if (reply == null) continue;
            Diag.Log($"Logitech 0x2202 fn{fn} raw={BitConverter.ToString(reply, 0, Math.Min(12, reply.Length))}");
            var parsed = ParseExtendedDpiParams(reply);
            if (parsed != null) return parsed;
        }
        return null;
    }

    private static (string Name, DeviceKind Kind) ResolveMeta(Hidpp h, ushort pid, byte idx, string usbProductName)
    {
        if (MetaCache.TryGetValue((pid, idx), out var cached))
            return cached;

        string? resolvedName = null;
        var kind = DeviceKind.Mouse;
        byte fName = h.FeatureIndex(idx, 0x0005);
        if (fName != 0)
        {
            var cnt = h.Call(idx, fName, 0, null, out _);
            if (cnt is { Length: >= 1 } && cnt[0] is > 0 and <= 64)
            {
                int totalLen = cnt[0];
                var bytes = new List<byte>(totalLen);
                int offset = 0;
                while (bytes.Count < totalLen && offset < 64)
                {
                    var chunk = h.Call(idx, fName, 1, [(byte)offset], out _);
                    if (chunk == null || chunk.Length == 0) break;
                    int take = Math.Min(chunk.Length, totalLen - bytes.Count);
                    for (int i = 0; i < take; i++)
                    {
                        if (chunk[i] == 0) break;
                        bytes.Add(chunk[i]);
                    }
                    offset += take;
                    if (take == 0) break;
                }
                string candidate = Encoding.UTF8.GetString(bytes.ToArray()).Trim('\0', ' ');
                if (!string.IsNullOrWhiteSpace(candidate))
                    resolvedName = candidate;
            }

            var typeReply = h.Call(idx, fName, 2, null, out _);
            if (typeReply is { Length: >= 1 })
            {
                kind = typeReply[0] switch
                {
                    0 or 2 => DeviceKind.Keyboard,
                    8 => DeviceKind.Headset,
                    _ => DeviceKind.Mouse,
                };
            }
        }

        string finalName = resolvedName
                           ?? (string.IsNullOrWhiteSpace(usbProductName) ? $"Logitech {pid:X4}" : usbProductName.Trim());
        var result = (finalName, kind);
        MetaCache[(pid, idx)] = result;
        return result;
    }

    private static (List<HidChannel> Shorts, List<HidChannel> Longs) OpenChannels(IEnumerable<HidDevice> devices)
    {
        var list = devices.ToList();
        var shorts = list.Where(d => HidSafe.MaxOutputLen(d) == 7)
            .Select(HidChannel.TryOpen).Where(c => c != null).Cast<HidChannel>().ToList();
        var longs = list.Where(d => HidSafe.MaxOutputLen(d) is 20 or 21)
            .Concat(list.Where(d => HidSafe.MaxOutputLen(d) == 64))
            .Select(HidChannel.TryOpen).Where(c => c != null).Cast<HidChannel>().ToList();
        return (shorts, longs);
    }

    private static bool TryParseId(string deviceId, out ushort pid, out byte idx)
    {
        pid = 0;
        idx = 0;
        var parts = deviceId.Split(':');
        if (parts.Length < 3) return false;
        return ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid)
            && byte.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out idx);
    }

    private static int? ReadBattery(Hidpp h, byte idx, out byte chargeStatus)
    {
        chargeStatus = 0;
        byte f = h.FeatureIndex(idx, 0x1004);
        if (f != 0)
        {
            var p = h.Call(idx, f, 1, null, out _);
            if (p is { Length: >= 3 } && p[0] is > 0 and <= 100)
            {
                chargeStatus = p[2];
                return p[0];
            }
        }
        f = h.FeatureIndex(idx, 0x1000);
        if (f != 0)
        {
            var p = h.Call(idx, f, 0, null, out _);
            if (p is { Length: >= 3 } && p[0] <= 100)
            {
                chargeStatus = p[2];
                return p[0];
            }
        }
        return null;
    }

    internal static (int Min, int Max, int Step)? ParseDpiRangeList(byte[] data, int startOffset = 1)
    {
        int min = int.MaxValue;
        int max = 0;
        int step = 50;
        int lastVal = 0;
        for (int i = startOffset; i + 1 < data.Length; i += 2)
        {
            ushort tok = (ushort)((data[i] << 8) | data[i + 1]);
            if (tok == 0) break;
            if ((tok & 0xE000) == 0xE000)
            {
                int decodedStep = tok & 0x1FFF;
                if (decodedStep > 0) step = decodedStep;
            }
            else if (tok is >= 50 and <= 50000)
            {
                min = Math.Min(min, tok);
                max = Math.Max(max, tok);
                lastVal = tok;
            }
        }
        if (max == 0 || min > max)
        {
            if (lastVal >= 50) return (lastVal, lastVal, 1);
            return null;
        }
        return (min, max, step);
    }

    internal static (int X, int Y, int Lod)? ParseExtendedDpiParams(byte[] p)
    {
        // 格式 A (Solaar fn 5 / 0x2202): [sensorIdx, curX_be16, defX_be16, curY_be16, defY_be16, lod, ...] (>=10 bytes)
        if (p.Length >= 10)
        {
            int curX = (p[1] << 8) | p[2];
            int defX = (p[3] << 8) | p[4];
            int curY = (p[5] << 8) | p[6];
            int defY = (p[7] << 8) | p[8];
            int lod = p[9];
            if (curX is >= 100 and <= 50000 && defX is >= 100 and <= 50000 &&
                curY is >= 100 and <= 50000 && defY is >= 100 and <= 50000)
                return (curX, curY, lod is >= 1 and <= 3 ? lod : 2);
        }
        // 格式 B: [sensorIdx, curX_be16, curY_be16, lod, ...] (>=6 bytes)
        if (p.Length >= 6)
        {
            int curX = (p[1] << 8) | p[2];
            int curY = (p[3] << 8) | p[4];
            int lod = p[5];
            if (curX is >= 100 and <= 50000 && curY is >= 100 and <= 50000)
                return (curX, curY, lod is >= 1 and <= 3 ? lod : 2);
        }
        // 格式 C: [curX_be16, curY_be16, lod] (>=5 bytes)
        if (p.Length >= 5)
        {
            int curX = (p[0] << 8) | p[1];
            int curY = (p[2] << 8) | p[3];
            int lod = p[4];
            if (curX is >= 100 and <= 50000 && curY is >= 100 and <= 50000)
                return (curX, curY, lod is >= 1 and <= 3 ? lod : 2);
        }
        return null;
    }

    internal static List<int> ParseExtendedRateBitmask(byte mask)
    {
        var list = new List<int>();
        for (int bit = 0; bit < ExtendedRatesHz.Length; bit++)
        {
            if ((mask & (1 << bit)) != 0)
                list.Add(ExtendedRatesHz[bit]);
        }
        return list;
    }

    internal static List<int> ParseClassicRateBitmask(byte mask)
    {
        var list = new List<int>();
        foreach (int ms in new[] { 8, 4, 2, 1 })
        {
            if ((mask & (1 << (ms - 1))) != 0)
                list.Add(1000 / ms);
        }
        return list;
    }

    internal static byte? MapExtendedRateCode(int hz)
    {
        int idx = Array.IndexOf(ExtendedRatesHz, hz);
        return idx >= 0 ? (byte)idx : null;
    }

    private sealed class Hidpp
    {
        private readonly List<HidChannel> _shorts;
        private readonly List<HidChannel> _longs;

        public Hidpp(List<HidChannel> shorts, List<HidChannel> longs)
        {
            _shorts = shorts;
            _longs = longs;
        }

        public byte FeatureIndex(byte idx, ushort featureId)
        {
            var p = Call(idx, 0x00, 0, [(byte)(featureId >> 8), (byte)(featureId & 0xFF)], out _);
            return p is { Length: > 0 } ? p[0] : (byte)0;
        }

        public byte[]? Call(byte idx, byte feature, byte fn, byte[]? extra, out byte errorCode)
        {
            errorCode = 0;
            bool useLong = _longs.Count > 0;
            var channel = useLong ? _longs[0] : _shorts[0];
            int len = useLong ? (channel.OutputLength >= 64 ? 64 : 20) : 7;
            var msg = new byte[len];
            msg[0] = len switch
            {
                64 => (byte)0x12,
                20 => (byte)0x11,
                _ => (byte)0x10,
            };
            msg[1] = idx;
            msg[2] = feature;
            msg[3] = (byte)((fn << 4) | SwId);
            if (extra != null)
                Array.Copy(extra, 0, msg, 4, Math.Min(extra.Length, len - 4));

            if (!channel.WriteOutput(msg))
            {
                errorCode = 0xFE;
                return null;
            }

            var deadline = Environment.TickCount64 + 420;
            while (Environment.TickCount64 < deadline)
            {
                foreach (var c in useLong ? _longs.Concat(_shorts) : _shorts)
                {
                    var r = c.Read(80);
                    if (r == null || r.Length < 7) continue;
                    if (r[0] is not (0x10 or 0x11 or 0x12)) continue;
                    if (r[1] != idx && !(idx == 0xFF && r[1] == 0x00)) continue;
                    if (r[2] == feature && r[3] == msg[3])
                        return r[4..];
                    if (r[2] == 0xFF && r[3] == feature && r[4] == msg[3])
                    {
                        errorCode = r.Length > 5 ? r[5] : (byte)0xFF;
                        return null;
                    }
                }
            }
            errorCode = 0xFD;
            return null;
        }
    }
}
