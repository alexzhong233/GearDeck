using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 赛睿 SteelSeries 无线外设协议提供器（VID 0x1038）：
/// 支持 Aerox 3/5/9、Rival 3 鼠标与 Arctis Nova 7/5/3/GameBuds/Classic 耳机电量与 Nova 7 调参。
/// </summary>
public sealed class SteelSeriesProvider : IPeripheralProvider, ITunablePeripheralProvider
{
    public const string ProviderName = "SteelSeries";
    private const ushort SteelSeriesVid = 0x1038;

    private static readonly HashSet<ushort> Nova7Pids =
    [
        0x2202, 0x22A1, 0x227E, 0x223A, 0x22A9, 0x227A,
        0x2206, 0x2258, 0x229E, 0x22AD, 0x22A4, 0x22A5,
        0x220A, 0x22A7, 0x2298,
    ];

    public string Name => ProviderName;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("steelseries:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevicesByVendor(SteelSeriesVid)
            .GroupBy(d => (ushort)d.ProductID);

        foreach (var g in groups)
        {
            ushort pid = g.Key;
            var list = g.ToList();
            string? model = ResolveModel(pid, out DeviceKind kind);
            if (model == null) continue;

            var hit = pid switch
            {
                0x1830 or 0x1872 => ToResult(ProbeMouse(list, aerox: false)),
                0x1838 or 0x1878 or 0x1852 or 0x185C or 0x1860 or 0x1858 or 0x1874 => ToResult(ProbeMouse(list, aerox: true)),
                0x12B3 or 0x12B6 or 0x12D7 or 0x12D5 => ToResult(ProbeClassic(list, 0xFF43, [0x06, 0x12], r => r.Length >= 4 && r[0] == 0x06 && r[1] == 0x12 && r[2] != 0x01 ? (Math.Clamp((int)r[3], 0, 100), false) : null)),
                0x1260 or 0x1252 or 0x12AD => ToResult(ProbeClassic(list, 0, [0x06, 0x18], r => r.Length >= 3 && r[0] == 0x06 && r[1] == 0x18 && r[2] > 0 ? (Math.Clamp((int)r[2], 0, 100), false) : null)),
                0x12C2 or 0x12C4 => ToResult(ProbeClassic(list, 0, [0x00, 0x20], r => r.Length >= 5 && r[0] == 0xAA && r[1] == 0x01 ? (Math.Clamp((Math.Clamp((int)r[3], 0x64, 0x9A) - 0x64) * 100 / (0x9A - 0x64), 0, 100), r[4] == 1) : null)),
                0x12E0 or 0x12E5 => ToResult(ProbeClassic(list, 0, [0x06, 0xB0], r => r.Length >= 16 && r[15] == 0x08 && r[6] <= 8 ? (r[6] * 100 / 8, false) : null)),
                _ => ProbeNova(list, pid),
            };

            string? dualEarNote = hit is { Left: not null } || hit is { Right: not null }
                ? $"左耳 {(hit.Value.Left is { } lv ? $"{lv}%" : "在盒")} · 右耳 {(hit.Value.Right is { } rv ? $"{rv}%" : "在盒")}"
                : null;

            results.Add(new PeripheralDevice
            {
                Id = $"steelseries:{SteelSeriesVid:X4}-{pid:X4}",
                VendorId = SteelSeriesVid,
                ProductId = pid,
                Name = model,
                Kind = kind,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = hit == null,
                Transport = pid is 0x230A or 0x2317 ? "2.4G USB-C" : "2.4G",
                BatteryPercent = hit?.Level,
                LeftEarPercent = hit?.Left,
                RightEarPercent = hit?.Right,
                State = hit is { Charging: true } ? BatteryState.Charging : (hit != null ? BatteryState.Normal : BatteryState.Unknown),
                Note = hit == null ? "SteelSeries 接收器已连接（设备休眠或待唤醒）" : dualEarNote,
                LastUpdated = DateTimeOffset.Now,
            });
        }

        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParsePid(deviceId, out ushort pid) || !Nova7Pids.Contains(pid))
        {
            caps.StatusMessage = "当前 SteelSeries 设备仅支持电量监测。";
            return caps;
        }

        using var ch = OpenNovaChannel(pid);
        if (ch == null)
        {
            caps.StatusMessage = "未检测到可用的 Arctis Nova 7 (0xFFC0) 控制通道。";
            return caps;
        }

        string? chatMix = null;
        var req = new byte[Math.Max(ch.OutputLength, 2)];
        req[0] = 0x00; req[1] = 0xB0;
        if (ch.WriteOutput(req))
        {
            var r = ch.Read(300);
            if (r != null && r.Length >= 6 && r[0] == 0xB0)
                chatMix = $"游戏 {r[4]}% / 语音 {r[5]}%";
        }

        caps.IsOnline = true;
        caps.StatusMessage = "SteelSeries Arctis Nova 7 · 0xFFC0 控制通道";
        caps.SupportsSidetone = true;
        caps.SidetoneEnabled = true;
        caps.MaxSidetoneLevel = 3;
        caps.SupportsSleepTimeout = true;
        caps.MinSleepSeconds = 300;
        caps.MaxSleepSeconds = 3600;
        caps.SupportedSleepSeconds = [300, 600, 900, 1800, 3600];
        caps.ChatMixSummary = chatMix;
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParsePid(deviceId, out ushort pid) || !Nova7Pids.Contains(pid))
            return new DeviceTuningResult(false, "当前 SteelSeries 设备不支持调参");

        var applied = new List<string>();
        var errors = new List<string>();

        using (var ch = OpenNovaChannel(pid))
        {
            if (ch == null) return new DeviceTuningResult(false, "无法打开 SteelSeries Nova 7 控制通道");

            if (update.Sidetone is var (stOn, stLevel))
            {
                byte level = stOn ? (byte)Math.Clamp(stLevel, 0, 3) : (byte)0;
                if (WriteNovaCmd(ch, [0x00, 0x39, level]))
                {
                    if (pid == 0x227E)
                        WriteNovaCmd(ch, [0x00, 0x09]);
                    applied.Add(level == 0 ? "侧音关闭" : $"侧音档位 {level}/3");
                }
                else
                {
                    errors.Add("侧音");
                }
            }

            if (update.SleepSeconds is { } sec && sec > 0)
            {
                byte mins = (byte)Math.Clamp((int)Math.Round(sec / 60.0), 1, 255);
                if (WriteNovaCmd(ch, [0x00, 0xA3, mins]))
                    applied.Add($"休眠 {mins} 分钟");
                else
                    errors.Add("自动休眠");
            }
        }

        if (applied.Count == 0 && errors.Count == 0)
            return new DeviceTuningResult(true, "未变更 SteelSeries 参数", QueryCapabilities(deviceId));
        return errors.Count > 0
            ? new DeviceTuningResult(applied.Count > 0, $"部分设置失败 ({string.Join("、", errors)})", QueryCapabilities(deviceId))
            : new DeviceTuningResult(true, $"已写入 {string.Join(" / ", applied)}", QueryCapabilities(deviceId));
    }

    private static bool WriteNovaCmd(HidChannel ch, ReadOnlySpan<byte> cmd)
    {
        var req = new byte[Math.Max(ch.OutputLength, 64)];
        cmd.CopyTo(req);
        return ch.WriteOutput(req);
    }

    private static HidChannel? OpenNovaChannel(ushort pid)
    {
        var dev = HidSafe.EnumerateDevicesByVendor(SteelSeriesVid)
            .FirstOrDefault(d => (ushort)d.ProductID == pid
                && HidSafe.PrimaryUsage(d) == (0xFFC0, 0x0001)
                && HidSafe.MaxOutputLen(d) >= 3);
        return dev != null ? HidChannel.TryOpen(dev) : null;
    }

    private static bool TryParsePid(string deviceId, out ushort pid)
    {
        pid = 0;
        var parts = deviceId.Split(':');
        if (parts.Length < 2) return false;
        var vp = parts[1].Split('-');
        return vp.Length == 2 && ushort.TryParse(vp[1], System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    private static string? ResolveModel(ushort pid, out DeviceKind kind)
    {
        kind = DeviceKind.Mouse;
        switch (pid)
        {
            case 0x1830 or 0x1872: return "SteelSeries Rival 3 Wireless";
            case 0x1838: return "SteelSeries Aerox 3 Wireless";
            case 0x1878: return "SteelSeries Aerox 3 CS2 Dragon Lore";
            case 0x1852 or 0x185C or 0x1860: return "SteelSeries Aerox 5 Wireless";
            case 0x1858 or 0x1874: return "SteelSeries Aerox 9 Wireless";
        }

        kind = DeviceKind.Headset;
        return pid switch
        {
            0x2202 or 0x22A1 or 0x227E => "SteelSeries Arctis Nova 7",
            0x223A or 0x22A9 => "SteelSeries Arctis Nova 7 Diablo IV",
            0x227A => "SteelSeries Arctis Nova 7 WoW",
            0x2206 or 0x2258 or 0x229E or 0x22AD or 0x22A4 or 0x22A5 => "SteelSeries Arctis Nova 7X",
            0x220A or 0x22A7 or 0x2298 => "SteelSeries Arctis Nova 7P",
            0x2232 => "SteelSeries Arctis Nova 5",
            0x2253 => "SteelSeries Arctis Nova 5X",
            0x2269 or 0x226D => "SteelSeries Arctis Nova 3 Wireless",
            0x220E or 0x2212 or 0x2216 or 0x2236 => "SteelSeries Arctis 7+",
            0x230A or 0x2317 => "SteelSeries Arctis GameBuds",
            0x12B3 or 0x12B6 => "SteelSeries Arctis 1 Wireless",
            0x12D7 or 0x12D5 or 0x12AD or 0x1260 => "SteelSeries Arctis 7 Wireless",
            0x1252 => "SteelSeries Arctis Pro Wireless",
            0x12C2 or 0x12C4 => "SteelSeries Arctis 9",
            0x12E0 or 0x12E5 => "SteelSeries Arctis Nova Pro Wireless",
            _ => null,
        };
    }

    private static (int Level, bool Charging, int? Left, int? Right)? ToResult((int Level, bool Charging)? raw)
        => raw is { } v ? (v.Level, v.Charging, null, null) : null;

    private static (int Level, bool Charging, int? Left, int? Right)? ProbeNova(List<HidDevice> ifaces, ushort pid)
    {
        var dev = ifaces.FirstOrDefault(d => HidSafe.PrimaryUsage(d) == (0xFFC0, 0x0001) && HidSafe.MaxOutputLen(d) >= 2 && HidSafe.MaxInputLen(d) >= 4);
        if (dev == null) return null;
        using var ch = HidChannel.TryOpen(dev);
        if (ch == null) return null;

        var req = new byte[Math.Max(ch.OutputLength, 2)];
        req[0] = 0x00;
        req[1] = 0xB0;
        if (!ch.WriteOutput(req)) return null;

        var dl = Environment.TickCount64 + 800;
        while (Environment.TickCount64 < dl)
        {
            var r = ch.Read(100);
            if (r == null) continue;
            if (ParseNovaReport(pid, r) is { } parsed)
                return parsed;
        }
        return null;
    }

    internal static (int Level, bool Charging, int? Left, int? Right)? ParseNovaReport(ushort pid, ReadOnlySpan<byte> r)
    {
        if (r.Length < 4 || r[0] != 0xB0)
            return null;

        if (pid is 0x230A or 0x2317 && r.Length >= 7)
        {
            int? l = r[3] == 0x03 ? Math.Clamp((int)r[5], 0, 100) : null;
            int? rr = r[4] == 0x03 ? Math.Clamp((int)r[6], 0, 100) : null;
            if (l != null && rr != null) return (Math.Min(l.Value, rr.Value), false, l, rr);
            if (l != null || rr != null) return ((l ?? rr)!.Value, false, l, rr);
            return null;
        }
        if (pid is 0x2232 or 0x2253 or 0x2269 or 0x226D && r.Length >= 5)
        {
            if (r[1] == 0x02) return null;
            return (Math.Clamp((int)r[3], 0, 100), r[4] == 0x01, null, null);
        }
        if (pid is 0x220E or 0x2212 or 0x2216 or 0x2236)
        {
            if (r[1] == 0x01) return null;
            return (Math.Clamp((int)r[2], 0, 4) * 25, r[3] == 0x01, null, null);
        }
        if (r[1] == 0x03 && r[3] != 0x00)
        {
            bool discrete = pid is 0x2202 or 0x2206 or 0x220A or 0x223A or 0x227A or 0x22A4;
            int pct = discrete ? Math.Clamp((int)r[2], 0, 4) * 25 : Math.Clamp((int)r[2], 0, 100);
            return (pct, r[3] is 0x01 or 0x02, null, null);
        }
        return null;
    }

    private static (int Level, bool Charging)? ProbeMouse(List<HidDevice> ifaces, bool aerox)
    {
        var dev = ifaces.FirstOrDefault(d => HidSafe.PrimaryUsage(d) == (0xFFC0, 0x0001) && HidSafe.MaxOutputLen(d) >= 3 && HidSafe.MaxInputLen(d) >= 3);
        if (dev == null) return null;
        using var ch = HidChannel.TryOpen(dev);
        if (ch == null) return null;

        var req = new byte[Math.Max(ch.OutputLength, 64)];
        if (aerox) req[1] = 0xD2;
        else { req[1] = 0xAA; req[2] = 0x01; }
        byte echo = req[1];

        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!ch.WriteOutput(req)) return null;
            for (int read = 0; read < 5; read++)
            {
                var r = ch.Read(90);
                if (r == null || r.Length < 3) continue;
                int o = r[0] == 0x00 && r.Length > 2 ? 1 : 0;
                if (r[o] != echo) continue;

                if (aerox && r.Length > o + 1)
                {
                    byte b = r[o + 1];
                    int v = b & 0x7F;
                    if (v == 0) return null;
                    int lvl = v > 21 ? Math.Min(v, 100) : (v - 1) * 5;
                    return (lvl, (b & 0x80) != 0);
                }
                if (!aerox && r.Length > o + 3 && r[o + 1] <= 100)
                {
                    return (r[o + 1], r[o + 3] != 0);
                }
            }
        }
        return null;
    }

    private static (int Level, bool Charging)? ProbeClassic(
        List<HidDevice> ifaces, ushort page, byte[] cmd, Func<byte[], (int Level, bool Charging)?> parse)
    {
        var candidates = ifaces
            .Where(d => HidSafe.MaxOutputLen(d) >= cmd.Length && (page == 0 ? HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00 : HidSafe.PrimaryUsage(d).UsagePage == page))
            .Take(2);
        foreach (var dev in candidates)
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;
            var req = new byte[Math.Max(ch.OutputLength, cmd.Length)];
            Array.Copy(cmd, req, cmd.Length);
            if (!ch.WriteOutput(req)) continue;
            var r = ch.Read(450);
            if (r != null && parse(r) is { } hit)
                return hit;
        }
        return null;
    }
}
