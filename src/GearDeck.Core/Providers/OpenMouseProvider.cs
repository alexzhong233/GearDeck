using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;
using GearDeck.Core.Services;

namespace GearDeck.Core.Providers;

/// <summary>
/// OpenMouse 多品牌开源小鼠/鼠标接收器识别与只读电量探测提供器：
/// 覆盖 Pulsar (0x3710/0x25A7)、Endgame Gear (0x3367)、Rapoo (0x24AE 非迈从)、
/// Ninjutso (0x093A/0x3554) 与 Finalmouse (0x361D)，结合外置 VID:PID 目录与通用无线休眠规则。
/// </summary>
public sealed class OpenMouseProvider : IPeripheralProvider
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

            string transport = InferTransport(cat, rawName, modelName);
            bool isWired = string.Equals(transport, "USB", StringComparison.OrdinalIgnoreCase);

            (int Level, bool Charging)? hit = ProbeBattery(ifaces);
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
            "Ninjutso" => text.Contains("ninjutso") || text.Contains("sora"),
            "Pulsar" => text.Contains("pulsar") || text.Contains("x2") || text.Contains("xlite") || text.Contains("feinmann"),
            _ => text.Contains(brand, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static (int Level, bool Charging)? ProbeBattery(List<HidDevice> ifaces)
    {
        // 1. 尝试 65B CompX Feature QueryBattery
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

    private static string InferTransport(DeviceCatalogEntry? cat, string? rawName, string modelName)
    {
        if (cat != null && !string.IsNullOrWhiteSpace(cat.Transport))
            return cat.Transport;
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
