using System.Globalization;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 狼蛛 AULA 全系（38A6 / 25A7 / 258A SC380 Pro / 3554 FA09/FA0A 等）：
/// 使用 HidSafe 隔离异常接口；依次自适应 0x13/0x4A、0x08 (0x55校验)、GearHub/MU 0xD4/0xD3、CompX 65B 与 SC380 Pro 520B 协议。
/// </summary>
public sealed class AulaProvider : ITunablePeripheralProvider
{
    public const string ProviderName = "AULA";
    public string Name => ProviderName;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("aula:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevices()
            .Where(IsAulaDevice)
            .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID));

        foreach (var g in groups)
        {
            try
            {
                var (vid, pid) = g.Key;
                string? rawName = g.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                string cleanName = CleanName(rawName) ?? (vid == 0x258A && pid is 0x0138 or 0x0136 ? "AULA SC380 Pro" : $"AULA {vid:X4}:{pid:X4}");
                var kind = InferKind(cleanName);

                var hit = ProbeBattery(g, vid, pid);
                results.Add(new PeripheralDevice
                {
                    Id = $"aula:{vid:X4}-{pid:X4}",
                    Name = cleanName,
                    Kind = kind,
                    Provider = ProviderName,
                    Connected = true,
                    IsSleeping = hit == null && !(rawName?.Contains("Wired", StringComparison.OrdinalIgnoreCase) == true || (vid == 0x258A && pid == 0x0136)),
                    Transport = rawName?.Contains("Wired", StringComparison.OrdinalIgnoreCase) == true || (vid == 0x258A && pid == 0x0136) ? "USB" : "2.4G",
                    BatteryPercent = hit?.Battery,
                    State = hit is { Charging: true } ? BatteryState.Charging : BatteryState.Normal,
                    Note = hit == null ? "狼蛛接收器已连接（设备休眠或待唤醒）" : null,
                    LastUpdated = DateTimeOffset.Now,
                });
            }
            catch (Exception ex)
            {
                Diag.Log($"AULA {g.Key.Item1:X4}:{g.Key.Item2:X4}: {ex.Message}");
            }
        }
        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
        {
            caps.StatusMessage = "无效的狼蛛设备 ID。";
            return caps;
        }

        var list = HidSafe.EnumerateDevices(vid, pid);
        if (list.Count == 0)
        {
            caps.StatusMessage = "未找到狼蛛设备接口。";
            return caps;
        }

        string? rawName = list.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        bool wireless = !(vid == 0x258A && pid == 0x0136) && rawName?.Contains("Wired", StringComparison.OrdinalIgnoreCase) != true;

        // 1. AULA SC380 Pro 520B Feature Report 0x09
        var scCaps = QuerySc380Capabilities(deviceId, list);
        if (scCaps != null && scCaps.IsOnline)
            return scCaps;

        // 2. CompX 65B Page/Cmd
        foreach (var dev in list.Where(d => HidSafe.MaxFeatureLen(d) >= 64)
                                .OrderBy(d => HidSafe.MaxFeatureLen(d) == 65 ? 0 : 1))
        {
            using var raw = RawHidFeature.TryOpen(dev.DevicePath);
            if (raw == null) continue;
            var res = CompXPageProtocol.QueryCapabilities(deviceId, raw, HidSafe.MaxFeatureLen(dev), wireless, rawName);
            if (res.IsOnline && (res.SupportsDpi || res.SupportedPollingRates.Count > 0))
                return res;
        }

        // 3. Compx08 (0x08 / 0x55)
        foreach (var iface in list.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17))
        {
            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;
            var res = Compx08Protocol.QueryCapabilities(deviceId, ch, isWireless: wireless, productName: rawName);
            if (res.SupportsDpi || res.SupportedPollingRates.Count > 0)
                return res;
        }

        // 4. GearHub / MU 0xD4/0xD3 Bit7
        var ghCaps = GearHubMuProtocol.QueryCapabilities(deviceId, list);
        if (ghCaps.IsOnline)
            return ghCaps;

        caps.IsOnline = false;
        caps.StatusMessage = "狼蛛设备当前休眠或未唤醒，请晃动鼠标后点击「读取当前硬件参数」。";
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new DeviceTuningResult(false, "无效的狼蛛设备 ID。");

        var list = HidSafe.EnumerateDevices(vid, pid);
        string? rawName = list.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        bool wireless = !(vid == 0x258A && pid == 0x0136) && rawName?.Contains("Wired", StringComparison.OrdinalIgnoreCase) != true;

        if (ApplySc380Settings(deviceId, list, update) is { } scRes)
            return scRes;

        foreach (var dev in list.Where(d => HidSafe.MaxFeatureLen(d) >= 64)
                                .OrderBy(d => HidSafe.MaxFeatureLen(d) == 65 ? 0 : 1))
        {
            int fl = HidSafe.MaxFeatureLen(dev);
            using var raw = RawHidFeature.TryOpen(dev.DevicePath);
            if (raw == null) continue;
            if (CompXPageProtocol.QueryBattery(raw, fl) != null)
                return CompXPageProtocol.ApplySettings(deviceId, raw, fl, wireless, rawName, update);
        }

        foreach (var iface in list.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 17))
        {
            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;
            var res = Compx08Protocol.ApplySettings(deviceId, ch, update, isWireless: wireless, productName: rawName);
            if (res.Success) return res;
        }

        var ghRes = GearHubMuProtocol.ApplySettings(deviceId, list, update);
        if (ghRes.Success) return ghRes;

        return new DeviceTuningResult(false, "狼蛛设备未响应当前参数写入指令（请确保鼠标已开机唤醒）。");
    }

    // ── AULA SC380 Pro (520B Feature Report 0x09) ───────────────────────────

    private static byte[] EncodeSc380Report(byte cmd, byte arg, int length, byte[]? data = null)
    {
        var report = new byte[520];
        report[0] = 0x09;
        report[2] = cmd;
        report[3] = arg;
        report[4] = 0x01;
        report[5] = 0x00;
        report[6] = (byte)(length & 0xFF);
        report[7] = (byte)(length >> 8);
        if (data != null && length > 0)
            Array.Copy(data, 0, report, 8, Math.Min(length, data.Length));
        int sum = 0;
        for (int i = 2; i < 520; i++) sum += report[i];
        report[1] = (byte)(sum & 0xFF);
        return report;
    }

    private static byte[]? ExchangeSc380(RawHidFeature raw, byte cmd, byte arg, int length, byte[]? data = null)
    {
        var req = EncodeSc380Report(cmd, arg, length, data);
        if (!raw.SetFeature(req)) return null;
        Thread.Sleep(cmd == 0x44 || cmd == 0x04 ? 45 : 25);
        var reply = raw.GetFeature(520, 0x09);
        if (reply == null || reply.Length < 8 + length) return null;
        if (reply[2] != cmd || reply[3] != arg || reply[4] != 1) return null;
        if (cmd == 0x44)
        {
            int sum = 0;
            for (int i = 2; i < 8 + length; i++) sum += reply[i];
            if (reply[1] != (byte)(sum & 0xFF)) return null;
        }
        return reply.AsSpan(8, length).ToArray();
    }

    private static DeviceTuningCapabilities? QuerySc380Capabilities(string deviceId, IEnumerable<HidDevice> devs)
    {
        var dev = devs.FirstOrDefault(d => HidSafe.MaxFeatureLen(d) >= 520 && HidReportInspector.GetReportIds(d).FeatureIds.Contains((byte)0x09));
        if (dev == null) return null;

        using var raw = RawHidFeature.TryOpen(dev.DevicePath);
        if (raw == null) return null;

        var block = ExchangeSc380(raw, 0x44, 0x00, 400);
        if (block == null || block.Length < 400 || block[0] != 0x64) return null;

        var caps = new DeviceTuningCapabilities { DeviceId = deviceId, IsOnline = true };
        int count = Math.Clamp(block[3] & 0x0F, 1, 6);
        int active = Math.Clamp((block[3] >> 4) - 1, 0, count - 1);
        int[] rateMap = [125, 250, 500, 1000];
        int rateIdx = (block[2] & 0x0F) - 1;

        caps.SupportsDpi = true;
        caps.SupportsIndependentXY = true;
        caps.SupportsDpiStagesWrite = true;
        caps.MinDpi = 100;
        caps.MaxDpi = 12000;
        caps.DpiStep = 100;
        caps.ActiveDpiStage = active;
        for (int i = 0; i < count; i++)
        {
            int x = ((block[7 + 2 * i] | (block[8 + 2 * i] << 8)) + 1) * 100;
            int y = ((block[27 + 2 * i] | (block[28 + 2 * i] << 8)) + 1) * 100;
            caps.DpiStages.Add((Math.Clamp(x, 100, 12000), Math.Clamp(y, 100, 12000)));
        }
        if (caps.DpiStages.Count > 0)
        {
            caps.CurrentDpiX = caps.DpiStages[active].X;
            caps.CurrentDpiY = caps.DpiStages[active].Y;
        }
        caps.SupportedPollingRates = [125, 250, 500, 1000];
        caps.CurrentPollingRate = (uint)rateIdx < (uint)rateMap.Length ? rateMap[rateIdx] : 1000;
        caps.StatusMessage = "已读取 AULA SC380 Pro 硬件参数。";
        return caps;
    }

    private static DeviceTuningResult? ApplySc380Settings(string deviceId, IEnumerable<HidDevice> devs, DeviceTuningUpdate update)
    {
        var dev = devs.FirstOrDefault(d => HidSafe.MaxFeatureLen(d) >= 520 && HidReportInspector.GetReportIds(d).FeatureIds.Contains((byte)0x09));
        if (dev == null) return null;

        using var raw = RawHidFeature.TryOpen(dev.DevicePath);
        if (raw == null) return null;

        var block = ExchangeSc380(raw, 0x44, 0x00, 400);
        if (block == null || block.Length < 400 || block[0] != 0x64) return null;

        int count = Math.Clamp(block[3] & 0x0F, 1, 6);
        int active = Math.Clamp(update.ActiveDpiStage ?? ((block[3] >> 4) - 1), 0, count - 1);

        if (update.PollingRateHz is { } hz)
        {
            int code = hz switch { 125 => 1, 250 => 2, 500 => 3, 1000 => 4, _ => 0 };
            if (code > 0) block[2] = (byte)((block[2] & 0xF0) | code);
        }

        if (update.DpiStages is { Count: > 0 } stages)
        {
            count = Math.Clamp(stages.Count, 1, 6);
            active = Math.Clamp(active, 0, count - 1);
            for (int i = 0; i < count; i++)
            {
                ushort vx = (ushort)Math.Clamp((int)Math.Round(stages[i].X / 100.0) - 1, 0, 119);
                ushort vy = (ushort)Math.Clamp((int)Math.Round(stages[i].Y / 100.0) - 1, 0, 119);
                block[7 + 2 * i] = (byte)(vx & 0xFF);
                block[8 + 2 * i] = (byte)(vx >> 8);
                block[27 + 2 * i] = (byte)(vy & 0xFF);
                block[28 + 2 * i] = (byte)(vy >> 8);
            }
        }
        else if (update.Dpi is { } dpi)
        {
            ushort vx = (ushort)Math.Clamp((int)Math.Round(dpi.X / 100.0) - 1, 0, 119);
            ushort vy = (ushort)Math.Clamp((int)Math.Round(dpi.Y / 100.0) - 1, 0, 119);
            block[7 + 2 * active] = (byte)(vx & 0xFF);
            block[8 + 2 * active] = (byte)(vx >> 8);
            block[27 + 2 * active] = (byte)(vy & 0xFF);
            block[28 + 2 * active] = (byte)(vy >> 8);
        }

        block[3] = (byte)(((active + 1) << 4) | count);
        raw.SetFeature(EncodeSc380Report(0x04, 0x00, 400, block));
        Thread.Sleep(45);
        return new DeviceTuningResult(true, "已写入 AULA SC380 Pro 硬件配置。", QuerySc380Capabilities(deviceId, devs));
    }

    private static bool IsAulaDevice(HidDevice d)
    {
        if (d.VendorID == 0x38A6) return true;
        if (d.VendorID == 0x258A && d.ProductID is 0x0138 or 0x0136) return true;
        string upper = HidSafe.ProductName(d).ToUpperInvariant();
        if (d.VendorID == 0x3554 && (d.ProductID is 0xFA09 or 0xFA0A || upper.Contains("AULA")))
            return true;
        if (d.VendorID == 0x25A7 && upper.Contains("AULA"))
            return true;
        return upper.Contains("AULA");
    }

    private static (int Battery, bool Charging)? ProbeBattery(IEnumerable<HidDevice> group, ushort vid, ushort pid)
    {
        foreach (var dev in group.Where(d => HidSafe.MaxOutputLen(d) >= 17 || HidSafe.MaxFeatureLen(d) >= 20)
                                 .OrderByDescending(d => Math.Max(HidSafe.MaxFeatureLen(d), HidSafe.MaxOutputLen(d))))
        {
            int featLen = HidSafe.MaxFeatureLen(dev);
            if (featLen >= 520)
            {
                using var scRaw = RawHidFeature.TryOpen(dev.DevicePath);
                if (scRaw != null)
                {
                    var bRep = ExchangeSc380(scRaw, 0x09, 0x01, 1);
                    if (bRep is { Length: >= 1 } && bRep[0] is >= 1 and <= 100)
                        return (bRep[0], false);
                }
            }

            var ids = HidReportInspector.GetReportIds(dev);
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;

            if (ch.OutputLength >= 20)
            {
                var pkt13 = BuildAula13Frame(0x13, ch.OutputLength);
                if (ch.WriteOutput(pkt13))
                {
                    var r = ch.Read(180);
                    if (r != null && ParseAula13Reply(r) is { } a13) return a13;
                }
            }
            if (ch.FeatureLength >= 20)
            {
                var pkt13 = BuildAula13Frame(0x13, ch.FeatureLength);
                if (ch.WriteFeature(pkt13))
                {
                    Thread.Sleep(45);
                    var r = ch.ReadFeature(0x13);
                    if (r != null && ParseAula13Reply(r) is { } a13) return a13;
                }
            }

            foreach (byte repId in ids.OutputIds.Concat(new byte[] { 0x08 }).Distinct())
            {
                if (repId == 0) continue;
                if (Compx08Protocol.QueryBattery(ch, repId, 350) is { } c08)
                    return (c08.Level, c08.Charging);
            }

            if (featLen >= 64)
            {
                using var raw = RawHidFeature.TryOpen(dev.DevicePath);
                if (raw != null)
                {
                    if (CompXPageProtocol.QueryBattery(raw, featLen) is { } pageHit)
                        return pageHit;
                }
            }
        }
        return null;
    }

    internal static byte[] BuildAula13Frame(byte reportId = 0x13, int length = 20)
    {
        int n = Math.Max(length, 20);
        var buf = new byte[n];
        buf[0] = reportId;
        buf[1] = 0x4A;
        int sum = 0;
        for (int i = 0; i < n - 1; i++) sum += buf[i];
        buf[n - 1] = (byte)(sum & 0xFF);
        return buf;
    }

    internal static (int Battery, bool Charging)? ParseAula13Reply(byte[] reply)
    {
        if (reply.Length < 8) return null;
        foreach (int o in new[] { 0, 1 })
        {
            if (reply.Length < o + 7) continue;
            if (reply[o] == 0x13 && (reply[o + 1] & 0x7F) == 0x4A)
            {
                int pct = reply[o + 5];
                bool chg = (reply[o + 6] & 0x10) != 0 || reply[o + 6] == 0x01;
                if (pct is >= 1 and <= 100)
                    return (pct, chg);
            }
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

    private static DeviceKind InferKind(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("f75") || lower.Contains("f87") || lower.Contains("f99") ||
            lower.Contains("f108") || lower.Contains("s99") || lower.Contains("hero") ||
            lower.Contains("win60") || lower.Contains("win68") || lower.Contains("keyboard"))
            return DeviceKind.Keyboard;
        if (lower.Contains("headset"))
            return DeviceKind.Headset;
        return DeviceKind.Mouse;
    }

    private static string? CleanName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (var suffix in new[] { " Dongle", " Receiver", " RECEIVER", " 8K RECEIVER" })
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                s = s[..^suffix.Length];
        string trimmed = s.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Equals("AULA", StringComparison.OrdinalIgnoreCase))
            return "AULA SC 无线鼠标";
        return trimmed;
    }
}
