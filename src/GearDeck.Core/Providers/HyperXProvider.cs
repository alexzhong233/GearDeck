using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>HyperX 无线耳机电量（HP 0x03F0 / Kingston 0x0951）。</summary>
public sealed class HyperXProvider : IPeripheralProvider
{
    public const string ProviderName = "HyperX";

    public string Name => ProviderName;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("hyperx:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var devs = HidSafe.EnumerateDevicesByVendor(0x03F0)
            .Concat(HidSafe.EnumerateDevicesByVendor(0x0951))
            .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID));

        foreach (var g in devs)
        {
            var (vid, pid) = g.Key;
            string? name = ResolveModel(vid, pid);
            if (name == null) continue;

            var list = g.ToList();
            (int Level, bool Charging)? hit = pid switch
            {
                0x0696 or 0x018B => ProbeCloud2(list),
                0x05B7 or 0x0C9D or 0x0795 or 0x0995 => ProbeCloud3(list),
                0x02CC or 0x06BE => ProbeCloud3S(list),
                0x098D => ProbeCloudAlpha(list),
                _ => null,
            };

            results.Add(new PeripheralDevice
            {
                Id = $"hyperx:{vid:X4}-{pid:X4}",
                VendorId = vid,
                ProductId = pid,
                Name = name,
                Kind = DeviceKind.Headset,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = hit == null,
                Transport = "2.4G",
                BatteryPercent = hit?.Level,
                State = hit is { Charging: true } ? BatteryState.Charging : BatteryState.Normal,
                Note = hit == null ? "HyperX 接收器已连接（耳机未开机或休眠）" : null,
                LastUpdated = DateTimeOffset.Now,
            });
        }

        return results;
    }

    private static string? ResolveModel(ushort vid, ushort pid)
    {
        if (vid == 0x0951)
            return pid is 0x16C4 or 0x1723 ? "HyperX Cloud Flight Wireless" : null;

        return pid switch
        {
            0x0696 or 0x018B => "HyperX Cloud II Wireless",
            0x05B7 or 0x0C9D => "HyperX Cloud III Wireless",
            0x0795 or 0x0995 => "HyperX Cloud II Core Wireless",
            0x02CC or 0x06BE => "HyperX Cloud III S Wireless",
            0x098D => "HyperX Cloud Alpha Wireless",
            0x08BE => "HyperX Cloud Alpha 2",
            _ => null,
        };
    }

    private static (int Level, bool Charging)? ProbeCloud2(List<HidDevice> list)
    {
        var dev = list.FirstOrDefault(d => HidSafe.PrimaryUsage(d) == (0xFF90, 0x0303));
        if (dev == null) return null;
        using var ch = HidChannel.TryOpen(dev);
        if (ch == null) return null;

        var req = new byte[Math.Max(ch.OutputLength, 52)];
        req[0] = 0x06; req[1] = 0xFF; req[2] = 0xBB; req[3] = 0x02;
        if (!ch.WriteOutput(req)) return null;
        var r = ch.Read(500);
        if (r != null && r.Length >= 8 && r[0] == 0x06 && r[3] == 0x02 && r[7] <= 100)
            return (r[7], false);
        return null;
    }

    private static (int Level, bool Charging)? ProbeCloud3(List<HidDevice> list)
    {
        var dev = list.FirstOrDefault(d => HidSafe.PrimaryUsage(d) == (0xFF13, 0x0001));
        if (dev == null) return null;
        using var ch = HidChannel.TryOpen(dev);
        if (ch == null) return null;

        var req = new byte[Math.Max(ch.OutputLength, 62)];
        req[0] = 0x66; req[1] = 0x89;
        bool sent = ch.WriteOutput(req);
        if (!sent && ch.FeatureLength >= 62) sent = ch.WriteFeature(req);
        if (!sent) return null;

        var r = ch.Read(600);
        if (r != null && r.Length >= 5 && r[0] == 0x66 && r[1] is 0x89 or 0x0D && !(r[2] == 0 && r[3] == 0) && r[4] <= 100)
            return (r[4], false);
        return null;
    }

    private static (int Level, bool Charging)? ProbeCloud3S(List<HidDevice> list)
    {
        foreach (var dev in list.Where(d => HidSafe.MaxOutputLen(d) >= 64).OrderBy(d => HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00 ? 0 : 1).Take(2))
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;
            var req = new byte[Math.Max(ch.OutputLength, 64)];
            req[0] = 0x0C; req[1] = 0x02; req[2] = 0x03; req[3] = 0x01; req[5] = 0x06;
            if (!ch.WriteOutput(req)) continue;

            var dl = Environment.TickCount64 + 900;
            while (Environment.TickCount64 < dl)
            {
                var r = ch.Read(120);
                if (r == null || r.Length < 7) continue;
                if (r[0] == 0x0C && r[5] == 0x06 && r[6] is >= 1 and <= 100)
                    return (r[6], false);
                if (r[0] == 0x0D && r[4] == 1 && r[5] is >= 1 and <= 100)
                    return (r[5], false);
            }
        }
        return null;
    }

    private static (int Level, bool Charging)? ProbeCloudAlpha(List<HidDevice> list)
    {
        var dev = list.FirstOrDefault(d => HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00 && HidSafe.MaxOutputLen(d) >= 31);
        if (dev == null) return null;
        using var ch = HidChannel.TryOpen(dev);
        if (ch == null) return null;

        var req = new byte[Math.Max(ch.OutputLength, 31)];
        req[0] = 0x21; req[1] = 0xBB; req[2] = 0x0B;
        if (!ch.WriteOutput(req)) return null;
        var r = ch.Read(600);
        if (r != null && r.Length >= 4 && r[3] <= 100)
            return (r[3], false);
        return null;
    }
}
