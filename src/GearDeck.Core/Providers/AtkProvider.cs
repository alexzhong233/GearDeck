using System.Globalization;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// ATK / VXE / VGN 全系（373B / 34D7 及部分 3554）：
/// 使用 HidSafe 隔离异常接口；完整支持 0x08 (0x55校验) 全套调参（4 组配置切换 0x0E/0x0F、Ultra DPI、8K 回报率、0x00B5 竞技模式、0x00A9 高级选项），
/// 同时支持 BITMOUSE 0x72 全套调参与 ATK VIA 无线键盘电量。
/// </summary>
public sealed class AtkProvider : ITunablePeripheralProvider
{
    public const string ProviderName = "ATK";
    public string Name => ProviderName;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("atk:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevices()
            .Where(IsAtkDevice)
            .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID));

        foreach (var g in groups)
        {
            try
            {
                var (vid, pid) = g.Key;
                string? rawName = g.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                string cleanName = CleanName(rawName) ?? $"ATK {vid:X4}:{pid:X4}";
                var kind = InferKind(cleanName, g);
                bool wireless = !cleanName.Contains("Wired", StringComparison.OrdinalIgnoreCase);

                (byte Level, bool Charging)? hit = null;
                foreach (var iface in g.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17)
                                       .OrderBy(HidSafe.MaxOutputLen))
                {
                    using var ch = HidChannel.TryOpen(iface);
                    if (ch == null) continue;
                    hit = Compx08Protocol.QueryBattery(ch, 0x08, 700);
                    if (hit != null) break;
                }

                if (hit == null)
                {
                    foreach (var iface in g.Where(d => HidSafe.MaxOutputLen(d) >= 64 && HidSafe.MaxInputLen(d) >= 8)
                                           .OrderByDescending(HidSafe.MaxOutputLen))
                    {
                        using var ch = HidChannel.TryOpen(iface);
                        if (ch == null) continue;
                        hit = BitmouseProtocol.QueryBattery(ch, wireless);
                        if (hit != null) break;
                    }
                }

                hit ??= QueryViaKeyboard(g);

                results.Add(new PeripheralDevice
                {
                    Id = $"atk:{vid:X4}-{pid:X4}",
                    Name = cleanName,
                    Kind = kind,
                    Provider = ProviderName,
                    Connected = true,
                    IsSleeping = hit == null && wireless,
                    Transport = wireless ? "2.4G" : "USB",
                    BatteryPercent = hit?.Level,
                    State = hit is { Charging: true } ? BatteryState.Charging : BatteryState.Normal,
                    Note = hit == null ? "ATK/VXE 接收器已连接（休眠待唤醒）" : null,
                    LastUpdated = DateTimeOffset.Now,
                });
            }
            catch (Exception ex)
            {
                Diag.Log($"ATK {g.Key.Item1:X4}:{g.Key.Item2:X4}: {ex.Message}");
            }
        }
        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
        {
            caps.StatusMessage = "无效的 ATK 设备 ID。";
            return caps;
        }

        var list = HidSafe.EnumerateDevices(vid, pid);
        if (list.Count == 0)
        {
            caps.StatusMessage = "未找到 ATK 设备接口。";
            return caps;
        }

        string? rawName = list.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        bool wireless = rawName?.Contains("Wired", StringComparison.OrdinalIgnoreCase) != true;

        foreach (var iface in list.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17)
                                  .OrderBy(HidSafe.MaxOutputLen))
        {
            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;
            var res = Compx08Protocol.QueryCapabilities(deviceId, ch, isWireless: wireless, productName: rawName);
            if (res.SupportsDpi || res.SupportedPollingRates.Count > 0)
                return res;
        }

        foreach (var iface in list.Where(d => HidSafe.MaxOutputLen(d) >= 64 && HidSafe.MaxInputLen(d) >= 8)
                                  .OrderByDescending(HidSafe.MaxOutputLen))
        {
            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;
            var res = BitmouseProtocol.QueryCapabilities(deviceId, ch, wireless, rawName);
            if (res.IsOnline && (res.SupportsDpi || res.SupportedPollingRates.Count > 0))
                return res;
        }

        caps.IsOnline = false;
        caps.StatusMessage = "ATK/VXE 鼠标当前休眠中，晃动唤醒后点击「读取当前硬件参数」。";
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new DeviceTuningResult(false, "无效的 ATK 设备 ID。");

        var list = HidSafe.EnumerateDevices(vid, pid);
        string? rawName = list.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        bool wireless = rawName?.Contains("Wired", StringComparison.OrdinalIgnoreCase) != true;

        foreach (var iface in list.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17)
                                  .OrderBy(HidSafe.MaxOutputLen))
        {
            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;
            var res = Compx08Protocol.ApplySettings(deviceId, ch, update, isWireless: wireless, productName: rawName);
            if (res.Success) return res;
        }

        foreach (var iface in list.Where(d => HidSafe.MaxOutputLen(d) >= 64 && HidSafe.MaxInputLen(d) >= 8)
                                  .OrderByDescending(HidSafe.MaxOutputLen))
        {
            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;
            if (BitmouseProtocol.QueryBattery(ch, wireless) != null)
                return BitmouseProtocol.ApplySettings(deviceId, ch, wireless, rawName, update);
        }

        return new DeviceTuningResult(false, "ATK/VXE 鼠标未响应控制指令（可能处于深度休眠状态）。");
    }

    private static bool IsAtkDevice(HidDevice d)
    {
        if (d.VendorID is 0x373B or 0x34D7) return true;
        string name = HidSafe.ProductName(d).ToUpperInvariant();
        if (d.VendorID == 0x3554 && (name.Contains("ATK") || name.Contains("VXE") || name.Contains("VGN") || name.Contains("MAD")))
            return true;
        return name.Contains("ATK ") || name.Contains("VXE ") || name.Contains("VGN ");
    }

    private static (byte Level, bool Charging)? QueryViaKeyboard(IEnumerable<HidDevice> group)
    {
        var iface = group.FirstOrDefault(d => HidSafe.MaxOutputLen(d) is 32 or 33 && HidSafe.MaxInputLen(d) >= 32);
        if (iface == null) return null;
        using var ch = HidChannel.TryOpen(iface);
        if (ch == null) return null;

        var req = new byte[ch.OutputLength];
        if (req.Length < 6) return null;
        req[0] = 0x00;
        req[1] = 0x02;
        req[2] = 0x96;
        req[3] = 0x24;
        if (!ch.WriteOutput(req)) return null;
        var r = ch.Read(250);
        if (r != null && r.Length >= 8 && r[0] == 0x02 && r[1] == 0x96 && r[2] == 0x24 && r[4] <= 100)
            return (r[4], r.Length > 5 && r[5] == 1);
        return null;
    }

    private static bool TryParseVidPid(string deviceId, out ushort vid, out ushort pid)
    {
        vid = 0x373B; pid = 0;
        var parts = deviceId.Split(':');
        if (parts.Length < 2) return false;
        if (parts[1].Contains('-'))
        {
            var vp = parts[1].Split('-');
            return vp.Length == 2
                && ushort.TryParse(vp[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid)
                && ushort.TryParse(vp[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
        }
        return ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
    }

    private static DeviceKind InferKind(string name, IEnumerable<HidDevice> devs)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("a98") || lower.Contains("z87") || lower.Contains("v75") || lower.Contains("rs7") || lower.Contains("keyboard") ||
            devs.Any(d => HidSafe.MaxOutputLen(d) is 32 or 33 && !lower.Contains("mouse")))
            return DeviceKind.Keyboard;
        return DeviceKind.Mouse;
    }

    private static string? CleanName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (var suffix in new[] { " Dongle", " Receiver", " RECEIVER", " 8K RECEIVER" })
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                s = s[..^suffix.Length];
        return s.Trim();
    }
}
