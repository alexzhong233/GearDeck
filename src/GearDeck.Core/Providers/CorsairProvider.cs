using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 海盗船 Corsair 无线外设电量（VID 0x1B1C）：
/// 支持 Bragi / SlipStream (0xFF42 Dark Core / Virtuoso)、Void v2 / HS80 Max 与经典 Void 族。
/// </summary>
public sealed class CorsairProvider : IPeripheralProvider
{
    public const string ProviderName = "Corsair";
    private const ushort CorsairVid = 0x1B1C;

    private static readonly HashSet<ushort> LegacyVoidPids =
    [
        0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A, 0x1B23, 0x1B29,
        0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B, 0x0A75, 0x0A56,
    ];

    public string Name => ProviderName;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("corsair:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevicesByVendor(CorsairVid)
            .GroupBy(d => (ushort)d.ProductID);

        foreach (var g in groups)
        {
            ushort pid = g.Key;
            var list = g.ToList();
            string rawName = list.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                             ?? $"Corsair {pid:X4}";

            (int Level, bool Charging)? hit = pid switch
            {
                0x2A08 or 0x2A02 or 0x0A97 => ProbeV2(list),
                0x1B7F or 0x0A40 or 0x0A3E or 0x0A64 => ProbeBragi(list, pid is 0x0A40 or 0x0A3E or 0x0A64),
                _ when LegacyVoidPids.Contains(pid) => ProbeLegacyVoid(list),
                _ => null,
            };

            if (hit == null && pid is not (0x2A08 or 0x2A02 or 0x0A97 or 0x1B7F or 0x0A40 or 0x0A3E or 0x0A64) && !LegacyVoidPids.Contains(pid))
                continue;

            var (displayName, kind) = ResolveMeta(pid, rawName);
            results.Add(new PeripheralDevice
            {
                Id = $"corsair:{CorsairVid:X4}-{pid:X4}",
                VendorId = CorsairVid,
                ProductId = pid,
                Name = displayName,
                Kind = kind,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = hit == null,
                Transport = "2.4G",
                BatteryPercent = hit?.Level,
                State = hit is { Charging: true } ? BatteryState.Charging : BatteryState.Normal,
                Note = hit == null ? "Corsair 接收器已连接（设备休眠或待唤醒）" : null,
                LastUpdated = DateTimeOffset.Now,
            });
        }

        return results;
    }

    private static (string Name, DeviceKind Kind) ResolveMeta(ushort pid, string fallback) => pid switch
    {
        0x2A08 => ("Corsair Void v2 Wireless", DeviceKind.Headset),
        0x2A02 => ("Corsair Virtuoso Max Wireless", DeviceKind.Headset),
        0x0A97 => ("Corsair HS80 Max Wireless", DeviceKind.Headset),
        0x1B7F => ("Corsair Dark Core RGB Pro SE", DeviceKind.Mouse),
        0x0A40 or 0x0A3E => ("Corsair Virtuoso RGB Wireless SE", DeviceKind.Headset),
        0x0A64 => ("Corsair Virtuoso RGB Wireless XT", DeviceKind.Headset),
        _ => (fallback, DeviceKind.Headset),
    };

    private static (int Level, bool Charging)? ProbeLegacyVoid(List<HidDevice> ifaces)
    {
        var dev = ifaces.FirstOrDefault(d => HidSafe.PrimaryUsage(d) == (0xFFC5, 0x0001) && HidSafe.MaxOutputLen(d) >= 2);
        if (dev == null) return null;
        using var ch = HidChannel.TryOpen(dev);
        if (ch == null) return null;

        var req = new byte[Math.Max(ch.OutputLength, 2)];
        req[0] = 0xC9;
        req[1] = 0x64;
        if (!ch.WriteOutput(req)) return null;

        var dl = Environment.TickCount64 + 600;
        while (Environment.TickCount64 < dl)
        {
            var r = ch.Read(100);
            if (r != null && r.Length >= 5 && r[4] != 0)
            {
                int lvl = r[2] & 0x7F;
                if (lvl <= 100)
                    return (lvl, r[4] is 4 or 5);
            }
        }
        return null;
    }

    private static (int Level, bool Charging)? ProbeV2(List<HidDevice> ifaces)
    {
        var candidates = ifaces
            .Where(d => HidSafe.MaxOutputLen(d) >= 64 && HidSafe.MaxInputLen(d) >= 6)
            .OrderByDescending(d => d.DevicePath.Contains("mi_04", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Take(2);

        foreach (var dev in candidates)
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;
            WriteV2(ch, 0x08, 0x02, 0x13);
            WriteV2(ch, 0x08, 0x02, 0x12);
            while (ch.Read(25) != null) { }

            WriteV2(ch, 0x09, 0x02, 0x12);
            if (ch.Read(500) == null) continue;
            while (ch.Read(20) != null) { }

            WriteV2(ch, 0x09, 0x02, 0x0F);
            var r = ch.Read(500);
            if (r != null && r.Length > 5)
            {
                int tenths = r[4] | (r[5] << 8);
                if (tenths is >= 1 and <= 1000)
                    return (Math.Clamp(tenths / 10, 0, 100), false);
            }
        }
        return null;
    }

    private static bool WriteV2(HidChannel ch, byte endpoint, byte sub, byte cmd)
    {
        var frame = new byte[Math.Max(ch.OutputLength, 65)];
        frame[1] = 0x02;
        frame[2] = endpoint;
        frame[3] = sub;
        frame[4] = cmd;
        return ch.WriteOutput(frame);
    }

    private static (int Level, bool Charging)? ProbeBragi(List<HidDevice> ifaces, bool bare)
    {
        var dev = ifaces
            .Where(d => HidSafe.MaxOutputLen(d) >= 64 && HidSafe.MaxInputLen(d) >= 6)
            .OrderByDescending(d => HidSafe.PrimaryUsage(d) == (0xFF42, 0x0001) ? 2 : 0)
            .FirstOrDefault();
        if (dev == null) return null;

        using var ch = HidChannel.TryOpen(dev);
        if (ch == null) return null;
        while (ch.Read(20) != null) { }

        if (bare)
        {
            SendBragi(ch, 0x13, 0x08, true);
            _ = ch.Read(200);
            SendBragi(ch, 0x12, 0x08, true);
            _ = ch.Read(200);
            SendBragi(ch, 0x12, 0x09, true);
            _ = ch.Read(200);
        }

        if (!SendBragi(ch, 0x0F, 0x09, bare)) return null;
        int? val = ReadBragiValue(ch, 1600, v => v is >= 1 and <= 1000);
        if (val == null) return null;

        int lvl = Math.Clamp(val.Value / 10, 0, 100);
        bool chg = false;
        if (SendBragi(ch, 0x10, 0x09, bare))
            chg = ReadBragiValue(ch, 450, v => v is 1 or 2) == 1;

        return (lvl, chg);
    }

    private static bool SendBragi(HidChannel ch, byte prop, byte route, bool bare)
    {
        var frame = new byte[Math.Max(ch.OutputLength, bare ? 64 : 65)];
        if (bare)
        {
            frame[0] = 0x02; frame[1] = route; frame[2] = 0x02; frame[3] = prop;
        }
        else
        {
            frame[1] = route; frame[2] = 0x02; frame[3] = prop;
        }
        return ch.WriteOutput(frame);
    }

    private static int? ReadBragiValue(HidChannel ch, int windowMs, Func<int, bool> accept)
    {
        var dl = Environment.TickCount64 + windowMs;
        while (Environment.TickCount64 < dl)
        {
            var r = ch.Read(120);
            if (r == null) continue;
            ReadOnlySpan<byte> data = r.Length > 64 ? r.AsSpan(1) : r;
            if (data.Length < 5 || data[0] != 0x01) continue;
            int? parsed = data[1] == 0x02 && data[2] == 0x00
                ? data[3] | (data[4] << 8)
                : data.Length >= 6 && data[1] == 0x01 && data[2] == 0x02 && data[3] == 0x00
                    ? data[4] | (data[5] << 8)
                    : null;
            if (parsed is { } v && accept(v))
                return v;
        }
        return null;
    }
}
