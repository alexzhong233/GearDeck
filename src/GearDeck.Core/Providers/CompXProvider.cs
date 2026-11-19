using System.Globalization;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// CompX / PixArt / Lamzu (含兰族冥神 Inca 8K 0x37B0 / 0x373E / Atlantis 0x3554) / WLmouse (万灵 Beast X / Beast G 全系 0x36A7)：
/// 支持 65B CompX Page/Command 全套硬件控制（多板载配置切换、DPI 多档位、8K 回报率、高速/20K 竞技模式、LOD、移动同步、直线/波纹修正、角度微调、消抖、休眠、Page3 按键重映射）以及 0x08 协议。
/// </summary>
public sealed class CompXProvider : ITunablePeripheralProvider
{
    public const string ProviderName = "CompX";
    public string Name => ProviderName;

    private static readonly HashSet<ushort> ClaimedVids =
    [
        0x1532, // Razer
        0x046D, // Logitech
        0x0B05, // ROG
        0x3837, // MCHOSE
        0x5253, // MCHOSE RealTek
        0x373B, // ATK / VGN / VXE
        0x34D7, // ATK
        0x38A6, // AULA
        0x3151, // Angry Miao / GearHub
    ];

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("compx:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("lamzu:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("wlmouse:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevices()
            .Where(d => !ShouldSkipForOtherProvider(d))
            .Where(d => HidSafe.MaxFeatureLen(d) >= 64 || HidSafe.MaxOutputLen(d) >= 17)
            .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID));

        foreach (var g in groups)
        {
            try
            {
                var (vid, pid) = g.Key;
                string? rawName = g.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                var (brand, idPrefix) = ClassifyBrand(vid, rawName);
                bool knownBrandReceiver = brand is "Lamzu" or "WLmouse";

                (int Battery, bool Charging)? hit = null;

                // 1. 先查 65B (优先精确 65 字节，再查 513) Page/Cmd 0x83 电池帧 (WLmouse / Lamzu Inca / Maya X)
                foreach (var dev in g.Where(d => HidSafe.MaxFeatureLen(d) >= 64)
                                     .OrderBy(d => HidSafe.MaxFeatureLen(d) == 65 ? 0 : 1)
                                     .ThenBy(HidSafe.MaxFeatureLen))
                {
                    hit = Query0x83Feature(dev);
                    if (hit != null) break;
                }

                // 2. 再查 0x08 17/64 字节 (Lamzu Atlantis 等 CompX Flash 固件)
                if (hit == null)
                {
                    foreach (var dev in g.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17))
                    {
                        using var ch = HidChannel.TryOpen(dev);
                        if (ch == null) continue;
                        if (Compx08Protocol.QueryBattery(ch, 0x08, 450) is { } b08)
                        {
                            hit = (b08.Level, b08.Charging);
                            break;
                        }
                    }
                }

                if (hit == null && !knownBrandReceiver)
                    continue;

                string cleanName = CleanName(rawName) ?? KnownModelName(vid, pid) ?? $"{brand} {vid:X4}:{pid:X4}";
                bool wired = rawName?.Contains("Wired", StringComparison.OrdinalIgnoreCase) == true
                          || (vid == 0x37B0 && pid is 0x0007 or 0x0009);
                results.Add(new PeripheralDevice
                {
                    Id = $"{idPrefix}:{vid:X4}-{pid:X4}",
                    Name = cleanName,
                    Kind = DeviceKind.Mouse,
                    Provider = brand,
                    Connected = true,
                    IsSleeping = hit == null && !wired,
                    Transport = wired ? "USB" : "2.4G",
                    BatteryPercent = hit?.Battery,
                    State = hit is { Charging: true } ? BatteryState.Charging : BatteryState.Normal,
                    Note = hit == null ? "接收器已连接（鼠标休眠中，晃动唤醒）" : null,
                    LastUpdated = DateTimeOffset.Now,
                });
            }
            catch (Exception ex)
            {
                Diag.Log($"CompX {g.Key.Item1:X4}:{g.Key.Item2:X4}: {ex.Message}");
            }
        }
        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
        {
            caps.StatusMessage = "无效的设备 ID。";
            return caps;
        }

        var devs = HidSafe.EnumerateDevices(vid, pid);
        if (devs.Count == 0)
        {
            caps.StatusMessage = "未找到设备接口（请确认接收器或数据线已插好）。";
            return caps;
        }

        string? rawName = devs.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        bool isWireless = IsWirelessDevice(vid, pid, rawName);

        // 1. 优先尝试 CompX 65B Page/Cmd 协议 (WLmouse / Lamzu Inca / Maya X 等)
        foreach (var dev in devs.Where(d => HidSafe.MaxFeatureLen(d) >= 64)
                                .OrderBy(d => HidSafe.MaxFeatureLen(d) == 65 ? 0 : 1)
                                .ThenBy(HidSafe.MaxFeatureLen))
        {
            int featLen = HidSafe.MaxFeatureLen(dev);
            using var raw = RawHidFeature.TryOpen(dev.DevicePath);
            if (raw == null) continue;
            var res = CompXPageProtocol.QueryCapabilities(deviceId, raw, featLen, isWireless, rawName);
            if (res.IsOnline && (res.SupportsDpi || res.SupportedPollingRates.Count > 0))
                return res;
        }

        // 2. 尝试 0x08 EEPROM/Flash 协议 (Lamzu Atlantis / Aurora 旧代 / 通用 CompX)
        foreach (var dev in devs.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17))
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;
            var res = Compx08Protocol.QueryCapabilities(deviceId, ch, isWireless: isWireless, productName: rawName);
            if (res.SupportsDpi || res.SupportedPollingRates.Count > 0)
                return res;
        }

        caps.IsOnline = false;
        caps.StatusMessage = "无线鼠标当前休眠中，请晃动唤醒后点击「读取当前硬件参数」重试。";
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new DeviceTuningResult(false, "无效的设备 ID。");

        var devs = HidSafe.EnumerateDevices(vid, pid);
        string? rawName = devs.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        bool isWireless = IsWirelessDevice(vid, pid, rawName);

        foreach (var dev in devs.Where(d => HidSafe.MaxFeatureLen(d) >= 64)
                                .OrderBy(d => HidSafe.MaxFeatureLen(d) == 65 ? 0 : 1)
                                .ThenBy(HidSafe.MaxFeatureLen))
        {
            int featLen = HidSafe.MaxFeatureLen(dev);
            using var raw = RawHidFeature.TryOpen(dev.DevicePath);
            if (raw == null) continue;
            if (CompXPageProtocol.QueryBattery(raw, featLen) != null ||
                CompXPageProtocol.QueryCapabilities(deviceId, raw, featLen, isWireless, rawName).SupportsDpi)
            {
                return CompXPageProtocol.ApplySettings(deviceId, raw, featLen, isWireless, rawName, update);
            }
        }

        foreach (var dev in devs.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17))
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;
            var res = Compx08Protocol.ApplySettings(deviceId, ch, update, isWireless: isWireless, productName: rawName);
            if (res.Success) return res;
        }

        return new DeviceTuningResult(false, "鼠标当前处于休眠状态或未回应，请先晃动唤醒鼠标后再应用设置。");
    }

    private static (int Battery, bool Charging)? Query0x83Feature(HidDevice dev)
    {
        int featLen = HidSafe.MaxFeatureLen(dev);
        if (featLen < 64) return null;

        using (var raw = RawHidFeature.TryOpen(dev.DevicePath))
        {
            if (raw != null)
            {
                var hit = CompXPageProtocol.QueryBattery(raw, featLen);
                if (hit != null) return hit;

                var reportIds = HidReportInspector.GetReportIds(dev);
                byte[] request = CompXPageProtocol.BuildRequest(0x02, 0x00, 0x83, default, 0x02);
                foreach (byte repId in reportIds.FeatureIds)
                {
                    if (repId == 0) continue;
                    request[0] = repId;
                    var frame = featLen == request.Length ? request : PadFeature(request, featLen);
                    if (!raw.SetFeature(frame)) continue;
                    Thread.Sleep(45);
                    var reply = raw.GetFeature(featLen, repId);
                    if (reply != null && ParseFrame(reply) is { } altHit)
                        return altHit;
                }
            }
        }

        using var ch = HidChannel.TryOpen(dev);
        if (ch == null) return null;
        var ids = HidReportInspector.GetReportIds(dev);
        byte[] req = CompXPageProtocol.BuildRequest(0x02, 0x00, 0x83, default, 0x02);
        req[0] = ids.FeatureIds[0];
        if (!ch.WriteFeature(PadFeature(req, featLen))) return null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Thread.Sleep(50);
            var reply = ch.ReadFeature(req[0]);
            if (reply != null && ParseFrame(reply) is { } hit)
                return hit;
        }
        return null;
    }

    private static bool ShouldSkipForOtherProvider(HidDevice d)
    {
        ushort vid = (ushort)d.VendorID;
        if (ClaimedVids.Contains(vid)) return true;
        if (vid == 0x0E8D && d.ProductID == 0x0703) return true; // Angry Miao AM Infinity .97
        string upper = HidSafe.ProductName(d).ToUpperInvariant();
        if (upper.Contains("AULA") || upper.Contains("MCHOSE") || upper.Contains("ATK ") || upper.Contains("VXE "))
            return true;
        if (vid == 0x3554 && !upper.Contains("LAMZU") && !upper.Contains("ATLANTIS") && !upper.Contains("INCA") && !upper.Contains("THORN"))
        {
            // Keep 0x3554 Lamzu variants in CompX; skip known ATK/AULA 0x3554 IDs claimed by their own providers
            if (d.ProductID is 0xFA09 or 0xFA0A or 0xF58A or 0xF58C or 0xF58E or 0xF58F)
                return true;
        }
        return false;
    }

    private static (string Brand, string IdPrefix) ClassifyBrand(ushort vid, string? productName)
    {
        string upper = (productName ?? "").ToUpperInvariant();
        if (vid is 0x373E or 0x37B0 or 0x3367 || upper.Contains("LAMZU") || upper.Contains("INCA") ||
            upper.Contains("MAYA") || upper.Contains("ATLANTIS") || upper.Contains("THORN") ||
            upper.Contains("PARO") || upper.Contains("TACHI"))
            return ("Lamzu", "lamzu");
        if (vid == 0x36A7 || upper.Contains("WLMOUSE") || upper.Contains("BEAST") ||
            upper.Contains("SWORD") || upper.Contains("YING"))
            return ("WLmouse", "wlmouse");
        return (ProviderName, "compx");
    }

    private static string? KnownModelName(ushort vid, ushort pid) => (vid, pid) switch
    {
        (0x37B0, 0x0009 or 0x000F or 0x0010) => "Lamzu Inca 8K",
        (0x37B0, 0x0007 or 0x000D or 0x000E) => "Lamzu Paro Aurora",
        (0x37B0, 0x0001) => "Lamzu Aurora 8K",
        (0x36A7, 0xA856) => "WLmouse Beast X V2 8K",
        (0x36A7, 0xA860) => "WLmouse Beast G 8K",
        _ => null,
    };

    private static bool IsWirelessDevice(ushort vid, ushort pid, string? name)
    {
        if ((vid, pid) is (0x37B0, 0x0009 or 0x0007)) return false;
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (name.Contains("Receiver", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Dongle", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("8K", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
                return true;
            if (name.Contains("Wired", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    internal static (int Battery, bool Charging)? ParseFrame(byte[] reply)
    {
        foreach (int o in new[] { 0, 1 })
        {
            if (reply.Length < o + 9) continue;
            bool layoutA = reply[o + 3] == 0x02 && reply[o + 4] == 0x02 && reply[o + 6] == 0x83;
            bool layoutB = reply[o + 2] == 0x02 && reply[o + 3] == 0x02 && reply[o + 5] == 0x83;
            if (!layoutA && !layoutB) continue;
            byte status = reply[o + 1];
            if (status is not (0xA1 or 0xA2)) continue;
            int charge = layoutA ? reply[o + 7] : reply[o + 6];
            int battery = layoutA ? reply[o + 8] : reply[o + 7];
            if (battery is >= 0 and <= 100) return (battery, charge != 0);
        }
        return null;
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

    private static byte[] PadFeature(byte[] request, int featureLength)
    {
        if (featureLength <= 0 || featureLength == request.Length) return request;
        var b = new byte[featureLength];
        Array.Copy(request, b, Math.Min(request.Length, featureLength));
        return b;
    }

    private static string? CleanName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (var suffix in new[] { " 8K RECEIVER", " 4K RECEIVER", " RECEIVER", " DONGLE", " Wireless" })
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                s = s[..^suffix.Length];
        return s.Trim();
    }
}
