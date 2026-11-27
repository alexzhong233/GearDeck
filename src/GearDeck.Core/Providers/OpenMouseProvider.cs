using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;
using GearDeck.Core.Services;

namespace GearDeck.Core.Providers;

/// <summary>
/// OpenMouse 多品牌开源鼠标/鼠标接收器识别与参数控制提供器：
/// 覆盖 Pulsar (0x3710/0x25A7)、Endgame Gear (0x3367)、Rapoo (0x24AE 非迈从)、
/// Ninjutso (0x093A/0x1915/0x3554) 与 Finalmouse (0x361D)，结合外置 VID:PID 目录与多品牌协议路由。
/// </summary>
public sealed class OpenMouseProvider : IPeripheralProvider, ITunablePeripheralProvider
{
    public const string ProviderName = "OpenMouse";
    public string Name => ProviderName;

    private static readonly Dictionary<ushort, (string Brand, string IdPrefix, bool RequireBrandHint)> MouseBrands = new()
    {
        [0x3710] = ("Pulsar", "pulsar", false),
        [0x25A7] = ("Pulsar", "pulsar", true),
        [0x3367] = ("Endgame Gear", "egg", false),
        [0x24AE] = ("Rapoo", "rapoo", true),
        [0x093A] = ("Ninjutso", "ninjutso", true),
        [0x1915] = ("Ninjutso", "ninjutso", true),
        [0x3554] = ("Ninjutso", "ninjutso", true),
        [0x361D] = ("Finalmouse", "finalmouse", false),
    };

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("pulsar:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("egg:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("rapoo:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("ninjutso:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("finalmouse:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("openmouse:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevices()
            .Where(d => d.VendorID > 0 && d.ProductID > 0 && MouseBrands.ContainsKey((ushort)d.VendorID))
            .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID));

        foreach (var g in groups)
        {
            var (vid, pid) = g.Key;
            var meta = MouseBrands[vid];
            var ifaces = g.ToList();
            string? rawName = ifaces.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            string? rawMfr = ifaces.Select(HidSafe.Manufacturer).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            bool catalogHit = DeviceCatalogService.TryLookup(vid, pid, out var cat)
                              && string.Equals(cat?.Protocol, "openmouse", StringComparison.OrdinalIgnoreCase);

            if (meta.RequireBrandHint && !catalogHit && !MatchesBrandHint(meta.Brand, rawName, rawMfr))
                continue;

            string brand = !string.IsNullOrWhiteSpace(cat?.Brand) ? cat.Brand : meta.Brand;
            string modelName = !string.IsNullOrWhiteSpace(cat?.Model)
                ? cat.Model
                : (!string.IsNullOrWhiteSpace(rawName) ? rawName.Trim() : $"{brand} {vid:X4}:{pid:X4}");

            string transport = InferTransport(cat, rawName, modelName, vid, pid);
            bool isWired = string.Equals(transport, "USB", StringComparison.OrdinalIgnoreCase);

            (int Level, bool Charging)? hit = ProbeBattery(vid, pid, ifaces);
            bool sleeping = hit == null && !isWired;

            results.Add(new PeripheralDevice
            {
                Id = $"{meta.IdPrefix}:{vid:X4}-{pid:X4}",
                VendorId = vid,
                ProductId = pid,
                Name = modelName,
                Kind = cat?.ParsedKind ?? DeviceKind.Mouse,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = sleeping,
                Transport = transport,
                BatteryPercent = hit?.Level,
                State = hit is { Charging: true }
                    ? BatteryState.Charging
                    : (hit.HasValue ? BatteryState.Normal : BatteryState.Unknown),
                Note = sleeping ? $"{brand} 接收器已连接（鼠标休眠或待唤醒）" : null,
                LastUpdated = DateTimeOffset.Now,
            });
        }

        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!ProviderIdHelper.TryParseVidPid(deviceId, out ushort vid, out ushort pid))
        {
            caps.StatusMessage = "无效的 OpenMouse 设备标识。";
            return caps;
        }

        var ifaces = HidSafe.EnumerateDevices(vid, pid);
        if (ifaces.Count == 0)
        {
            caps.StatusMessage = "未检测到设备 HID 接口，请确认接收器或数据线已连接。";
            return caps;
        }

        string? rawName = ifaces.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        DeviceCatalogService.TryLookup(vid, pid, out var cat);
        string? modelName = cat?.Model ?? rawName;
        bool isWireless = !string.Equals(InferTransport(cat, rawName, modelName ?? "", vid, pid), "USB", StringComparison.OrdinalIgnoreCase);

        if (vid == EggWeProtocol.EggVid && EggWeProtocol.IsLikelyWeDevice(pid, ifaces))
        {
            var eggCaps = EggWeProtocol.QueryCapabilities(deviceId, ifaces, modelName);
            if (eggCaps.IsOnline) return eggCaps;
        }

        if (vid is NinjutsoProtocol.CurrentVid or NinjutsoProtocol.LegacyVid)
        {
            var ninjCaps = NinjutsoProtocol.QueryCapabilities(deviceId, vid, pid, ifaces, modelName);
            if (ninjCaps.IsOnline) return ninjCaps;
        }

        if (vid == FinalmouseProtocol.VendorId)
        {
            var fmCaps = FinalmouseProtocol.QueryCapabilities(deviceId, pid, ifaces, modelName);
            if (fmCaps.IsOnline) return fmCaps;
        }

        if (vid == RapooProtocol.RapooVid)
        {
            var rapooCaps = RapooProtocol.QueryCapabilities(deviceId, pid, ifaces, modelName);
            if (rapooCaps.IsOnline) return rapooCaps;
        }

        if (TryQueryCompXCapabilities(deviceId, ifaces, isWireless, modelName ?? rawName) is { } compxCaps)
            return compxCaps;

        caps.IsOnline = false;
        caps.StatusMessage = "设备当前休眠中或该固件尚未开放调参通道，请晃动唤醒后重试。";
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!ProviderIdHelper.TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new DeviceTuningResult(false, "无效的 OpenMouse 设备标识。");

        var ifaces = HidSafe.EnumerateDevices(vid, pid);
        if (ifaces.Count == 0)
            return new DeviceTuningResult(false, "未找到设备 HID 接口。");

        string? rawName = ifaces.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        DeviceCatalogService.TryLookup(vid, pid, out var cat);
        string? modelName = cat?.Model ?? rawName;
        bool isWireless = !string.Equals(InferTransport(cat, rawName, modelName ?? "", vid, pid), "USB", StringComparison.OrdinalIgnoreCase);

        if (vid == EggWeProtocol.EggVid && EggWeProtocol.IsLikelyWeDevice(pid, ifaces))
            return EggWeProtocol.ApplySettings(deviceId, ifaces, modelName, update);

        if (vid is NinjutsoProtocol.CurrentVid or NinjutsoProtocol.LegacyVid)
            return NinjutsoProtocol.ApplySettings(deviceId, vid, pid, ifaces, modelName, update);

        if (vid == FinalmouseProtocol.VendorId)
            return FinalmouseProtocol.ApplySettings(deviceId, pid, ifaces, modelName, update);

        if (vid == RapooProtocol.RapooVid)
            return RapooProtocol.ApplySettings(deviceId, pid, ifaces, modelName, update);

        if (TryApplyCompXSettings(deviceId, ifaces, isWireless, modelName ?? rawName, update) is { } compxRes)
            return compxRes;

        return new DeviceTuningResult(false, "鼠标当前休眠中或暂不支持此固件调参，请唤醒后重试。");
    }

    private static bool MatchesBrandHint(string brand, string? rawName, string? rawMfr)
    {
        string text = $"{rawName} {rawMfr}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // 0x24AE 与 0x3554 在国产鼠标中复用率高，严格避免抢占 MCHOSE / LEOBOG / AULA / ATK / LAMZU。
        if (text.Contains("mchose") || text.Contains("迈从") || text.Contains("leobog") ||
            text.Contains("hi98") || text.Contains("aula") || text.Contains("atk") ||
            text.Contains("lamzu") || text.Contains("vxe") || text.Contains("vgn"))
        {
            return false;
        }

        return brand switch
        {
            "Rapoo" => text.Contains("rapoo") || text.Contains("雷柏") || text.Contains("vt9") || text.Contains("vt1") || text.Contains("vt3"),
            "Ninjutso" => text.Contains("ninjutso") || text.Contains("sora") || text.Contains("ten "),
            "Pulsar" => text.Contains("pulsar") || text.Contains("x2") || text.Contains("xlite") || text.Contains("feinmann"),
            _ => text.Contains(brand, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static (int Level, bool Charging)? ProbeBattery(ushort vid, ushort pid, List<HidDevice> ifaces)
    {
        if (vid == EggWeProtocol.EggVid && EggWeProtocol.IsLikelyWeDevice(pid, ifaces))
        {
            if (EggWeProtocol.QueryBattery(ifaces) is { } eggHit)
                return eggHit;
        }
        else if (vid is NinjutsoProtocol.CurrentVid or NinjutsoProtocol.LegacyVid)
        {
            if (NinjutsoProtocol.QueryBattery(vid, pid, ifaces) is { } ninjHit)
                return ninjHit;
        }
        else if (vid == FinalmouseProtocol.VendorId)
        {
            if (FinalmouseProtocol.QueryBattery(pid, ifaces) is { } fmHit)
                return fmHit;
        }
        else if (vid == RapooProtocol.RapooVid)
        {
            if (RapooProtocol.QueryBattery(ifaces) is { } rapooHit)
                return rapooHit;
        }

        // 1. 尝试 65B CompX Feature QueryBattery (Pulsar / Endgame 4K / 其他)
        foreach (var dev in ifaces.Where(d => HidSafe.MaxFeatureLen(d) >= 64)
                                  .OrderBy(d => HidSafe.MaxFeatureLen(d) == 65 ? 0 : 1)
                                  .ThenBy(HidSafe.MaxFeatureLen))
        {
            int featLen = HidSafe.MaxFeatureLen(dev);
            using var raw = RawHidFeature.TryOpen(dev.DevicePath);
            if (raw != null && CompXPageProtocol.QueryBattery(raw, featLen) is { } pageHit)
                return pageHit;
        }

        // 2. 尝试 0x08 通用电量协议 (部分 CompX / Telink / Nordic 鼠标)
        foreach (var dev in ifaces.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17)
                                  .OrderByDescending(d => HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00 ? 1 : 0))
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;
            if (Compx08Protocol.QueryBattery(ch, 0x08, 320) is { } hit08)
                return (hit08.Level, hit08.Charging);
        }

        return null;
    }

    private static DeviceTuningCapabilities? TryQueryCompXCapabilities(string deviceId, List<HidDevice> ifaces, bool isWireless, string? productName)
    {
        foreach (var dev in ifaces.Where(d => HidSafe.MaxFeatureLen(d) >= 64)
                                  .OrderBy(d => HidSafe.MaxFeatureLen(d) == 65 ? 0 : 1)
                                  .ThenBy(HidSafe.MaxFeatureLen))
        {
            int featLen = HidSafe.MaxFeatureLen(dev);
            using var raw = RawHidFeature.TryOpen(dev.DevicePath);
            if (raw == null) continue;
            var res = CompXPageProtocol.QueryCapabilities(deviceId, raw, featLen, isWireless, productName);
            if (res.IsOnline && (res.SupportsDpi || res.SupportedPollingRates.Count > 0))
                return res;
        }

        foreach (var dev in ifaces.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17))
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;
            var res = Compx08Protocol.QueryCapabilities(deviceId, ch, isWireless: isWireless, productName: productName);
            if (res.SupportsDpi || res.SupportedPollingRates.Count > 0)
                return res;
        }
        return null;
    }

    private static DeviceTuningResult? TryApplyCompXSettings(string deviceId, List<HidDevice> ifaces, bool isWireless, string? productName, DeviceTuningUpdate update)
    {
        foreach (var dev in ifaces.Where(d => HidSafe.MaxFeatureLen(d) >= 64)
                                  .OrderBy(d => HidSafe.MaxFeatureLen(d) == 65 ? 0 : 1)
                                  .ThenBy(HidSafe.MaxFeatureLen))
        {
            int featLen = HidSafe.MaxFeatureLen(dev);
            using var raw = RawHidFeature.TryOpen(dev.DevicePath);
            if (raw == null) continue;
            if (CompXPageProtocol.QueryBattery(raw, featLen) != null ||
                CompXPageProtocol.QueryCapabilities(deviceId, raw, featLen, isWireless, productName).SupportsDpi)
            {
                return CompXPageProtocol.ApplySettings(deviceId, raw, featLen, isWireless, productName, update);
            }
        }

        foreach (var dev in ifaces.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17))
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;
            var res = Compx08Protocol.ApplySettings(deviceId, ch, update, isWireless: isWireless, productName: productName);
            if (res.Success) return res;
        }
        return null;
    }

    private static string InferTransport(DeviceCatalogEntry? cat, string? rawName, string modelName, ushort vid, ushort pid)
    {
        if (cat != null && !string.IsNullOrWhiteSpace(cat.Transport))
            return cat.Transport;
        if (vid == RapooProtocol.RapooVid && pid == 0x4405) return "USB";
        string combined = $"{rawName} {modelName}";
        if (combined.Contains("Wired", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("有线", StringComparison.Ordinal) ||
            combined.Contains("8k", StringComparison.OrdinalIgnoreCase) && !combined.Contains("dongle", StringComparison.OrdinalIgnoreCase))
        {
            return "USB";
        }
        return "2.4G";
    }
}
