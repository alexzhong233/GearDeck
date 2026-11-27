using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// HyperX 无线耳机协议提供器（HP 0x03F0 / Kingston 0x0951）：
/// 支持 Cloud II/III/III S/Flight/Alpha/Alpha 2 电量，以及 Alpha Wireless (0x098D) 与 Alpha 2 (0x08BE) 控制。
/// </summary>
public sealed class HyperXProvider : IPeripheralProvider, ITunablePeripheralProvider
{
    public const string ProviderName = "HyperX";
    private const ushort HpVid = 0x03F0;
    private const ushort KingstonVid = 0x0951;
    private const ushort CloudAlphaPid = 0x098D;
    private const ushort CloudAlpha2Pid = 0x08BE;

    public string Name => ProviderName;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("hyperx:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var devs = HidSafe.EnumerateDevicesByVendor(HpVid)
            .Concat(HidSafe.EnumerateDevicesByVendor(KingstonVid))
            .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID));

        foreach (var g in devs)
        {
            var (vid, pid) = g.Key;
            string? name = ResolveModel(vid, pid);
            if (name == null) continue;

            var list = g.ToList();
            (int? Level, bool Charging)? hit = (vid, pid) switch
            {
                (KingstonVid, 0x16C4 or 0x1723) => ProbeCloudFlight(list),
                (HpVid, 0x0696 or 0x018B) => ProbeCloud2(list),
                (HpVid, 0x05B7 or 0x0C9D or 0x0795 or 0x0995) => ProbeCloud3(list),
                (HpVid, 0x02CC or 0x06BE) => ProbeCloud3S(list),
                (HpVid, CloudAlphaPid) => ProbeCloudAlpha(list),
                (HpVid, CloudAlpha2Pid) => ProbeCloudAlpha2(list),
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
                State = hit is { Charging: true } ? BatteryState.Charging : (hit != null ? BatteryState.Normal : BatteryState.Unknown),
                Note = hit == null ? "HyperX 接收器已连接（耳机未开机或休眠）" : (hit is { Level: null, Charging: true } ? "充电中" : null),
                LastUpdated = DateTimeOffset.Now,
            });
        }

        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid) || vid != HpVid)
        {
            caps.StatusMessage = "当前 HyperX 设备仅支持电量监测。";
            return caps;
        }

        if (pid == CloudAlphaPid)
        {
            if (OpenAlphaChannel() is not { } ch)
            {
                caps.StatusMessage = "未检测到可用的 Cloud Alpha 控制通道。";
                return caps;
            }
            using (ch)
            {
                caps.IsOnline = true;
                caps.StatusMessage = "HyperX Cloud Alpha Wireless · 2.4G 控制通道";
                caps.SupportsSidetone = true;
                caps.MaxSidetoneLevel = 128;
                caps.SupportsSleepTimeout = true;
                caps.MinSleepSeconds = 300;
                caps.MaxSleepSeconds = 3600;
                caps.SupportedSleepSeconds = [300, 600, 900, 1800, 3600];
                caps.SupportsVoicePrompt = true;
                return caps;
            }
        }

        if (pid == CloudAlpha2Pid)
        {
            using var ch = OpenAlpha2Channel();
            if (ch == null)
            {
                caps.StatusMessage = "未检测到 Cloud Alpha 2 控制底座 MCU。";
                return caps;
            }
            var block = ReadAlpha2AudioBlock(ch);
            bool stOn = block != null && block[4] != 0;
            int sidetone = block != null ? Math.Clamp((int)block[5], 0, 20) : 0;
            int eqCode = block != null && block[11] is >= 1 and <= 3 ? block[11] : 1;
            string? chatMix = null;
            if (block != null && block[8] is >= 0x7B and <= 0x85)
            {
                int step = block[8] - 0x80;
                int gamePct = step > 0 ? 100 - step * 20 : 100;
                int chatPct = step < 0 ? 100 + step * 20 : 100;
                chatMix = $"游戏 {gamePct}% / 语音 {chatPct}%";
            }

            caps.IsOnline = true;
            caps.StatusMessage = "HyperX Cloud Alpha 2 · 控制底座 MCU";
            caps.SupportsSidetone = true;
            caps.SidetoneEnabled = stOn;
            caps.CurrentSidetoneLevel = sidetone;
            caps.MaxSidetoneLevel = 20;
            caps.SupportsEqualizer = true;
            caps.EqualizerEnabled = true;
            caps.SupportedEqPresets = [new LodOption(1, "预设 1"), new LodOption(2, "预设 2"), new LodOption(3, "预设 3")];
            caps.CurrentEqPreset = eqCode;
            caps.ChatMixSummary = chatMix;
            return caps;
        }

        caps.StatusMessage = "当前 HyperX 型号仅支持电量监测。";
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid) || vid != HpVid)
            return new DeviceTuningResult(false, "当前 HyperX 设备不支持调参");

        var (ok, msg) = pid switch
        {
            CloudAlphaPid => ApplyCloudAlpha(update),
            CloudAlpha2Pid => ApplyCloudAlpha2(update),
            _ => (false, "当前 HyperX 型号仅支持电量监测"),
        };
        return new DeviceTuningResult(ok, msg, QueryCapabilities(deviceId));
    }

    private static (bool Success, string Message) ApplyCloudAlpha(DeviceTuningUpdate update)
    {
        using var ch = OpenAlphaChannel();
        if (ch == null) return (false, "无法打开 HyperX Cloud Alpha 控制通道");

        var applied = new List<string>();
        var errors = new List<string>();

        if (update.Sidetone is var (stOn, stLevel))
        {
            if (!stOn || stLevel <= 0)
            {
                if (WriteAlphaCmd(ch, 0x10, 0x00)) applied.Add("侧音关闭");
                else errors.Add("侧音");
            }
            else
            {
                byte devVal = (byte)Math.Clamp(stLevel, 1, 128);
                if (WriteAlphaCmd(ch, 0x10, 0x01) && WriteAlphaCmd(ch, 0x11, devVal))
                    applied.Add($"侧音 {devVal}/128");
                else
                    errors.Add("侧音");
            }
        }

        if (update.SleepSeconds is { } sec && sec > 0)
        {
            byte mins = (byte)Math.Clamp((int)Math.Round(sec / 60.0), 1, 255);
            if (WriteAlphaCmd(ch, 0x12, mins)) applied.Add($"休眠 {mins} 分钟");
            else errors.Add("自动休眠");
        }

        if (update.VoicePromptEnabled is { } vp)
        {
            if (WriteAlphaCmd(ch, 0x13, (byte)(vp ? 1 : 0)))
                applied.Add($"语音提示: {(vp ? "开启" : "关闭")}");
            else
                errors.Add("语音提示");
        }

        if (applied.Count == 0 && errors.Count == 0) return (true, "未变更 HyperX 参数");
        return errors.Count > 0
            ? (applied.Count > 0, $"部分设置失败 ({string.Join("、", errors)})")
            : (true, $"已写入 {string.Join(" / ", applied)}");
    }

    private static (bool Success, string Message) ApplyCloudAlpha2(DeviceTuningUpdate update)
    {
        using var ch = OpenAlpha2Channel();
        if (ch == null) return (false, "无法打开 HyperX Cloud Alpha 2 控制底座通道");

        var block = ReadAlpha2AudioBlock(ch);
        if (block == null) return (false, "读取 Alpha 2 音频配置块失败");

        var applied = new List<string>();
        if (update.Sidetone is var (stOn, stLevel))
        {
            bool enable = stOn && stLevel > 0;
            block[4] = (byte)(enable ? 1 : 0);
            if (enable)
                block[5] = (byte)Math.Clamp(stLevel, 1, 20);
            applied.Add(enable ? $"侧音 {block[5]}/20" : "侧音关闭");
        }

        if (update.Equalizer is { PresetCode: { } eqCode })
        {
            byte preset = (byte)Math.Clamp(eqCode, 1, 3);
            block[11] = preset;
            applied.Add($"EQ 预设 {preset}");
        }

        if (applied.Count == 0) return (true, "未变更 Alpha 2 参数");

        var req = new byte[Math.Max(ch.OutputLength, 64)];
        req[0] = 0x60;
        req[1] = 0x01;
        Array.Copy(block, 0, req, 2, 12);
        return ch.WriteOutput(req)
            ? (true, $"已写入 {string.Join(" / ", applied)}")
            : (false, "写入 Alpha 2 控制指令失败");
    }

    private static bool WriteAlphaCmd(HidChannel ch, byte cmd, byte value)
    {
        var req = new byte[Math.Max(ch.OutputLength, 31)];
        req[0] = 0x21; req[1] = 0xBB; req[2] = cmd; req[3] = value;
        return ch.WriteOutput(req);
    }

    private static HidChannel? OpenAlphaChannel()
    {
        var dev = HidSafe.EnumerateDevicesByVendor(HpVid)
            .FirstOrDefault(d => (ushort)d.ProductID == CloudAlphaPid && HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00 && HidSafe.MaxOutputLen(d) >= 31);
        return dev != null ? HidChannel.TryOpen(dev) : null;
    }

    private static HidChannel? OpenAlpha2Channel()
    {
        var dev = HidSafe.EnumerateDevicesByVendor(HpVid)
            .Where(d => (ushort)d.ProductID == CloudAlpha2Pid && HidSafe.MaxOutputLen(d) >= 64)
            .OrderByDescending(d => HidSafe.PrimaryUsage(d) == (0xFF13, 0xFF00) ? 2 : 0)
            .FirstOrDefault();
        return dev != null ? HidChannel.TryOpen(dev) : null;
    }

    private static bool TryParseVidPid(string deviceId, out ushort vid, out ushort pid)
    {
        vid = 0; pid = 0;
        var parts = deviceId.Split(':');
        if (parts.Length < 2) return false;
        var vp = parts[1].Split('-');
        return vp.Length == 2
            && ushort.TryParse(vp[0], System.Globalization.NumberStyles.HexNumber, null, out vid)
            && ushort.TryParse(vp[1], System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    private static string? ResolveModel(ushort vid, ushort pid)
    {
        if (vid == KingstonVid)
            return pid is 0x16C4 or 0x1723 ? "HyperX Cloud Flight Wireless" : null;

        return pid switch
        {
            0x0696 or 0x018B => "HyperX Cloud II Wireless",
            0x05B7 or 0x0C9D => "HyperX Cloud III Wireless",
            0x0795 or 0x0995 => "HyperX Cloud II Core Wireless",
            0x02CC or 0x06BE => "HyperX Cloud III S Wireless",
            CloudAlphaPid => "HyperX Cloud Alpha Wireless",
            CloudAlpha2Pid => "HyperX Cloud Alpha 2",
            _ => null,
        };
    }

    private static (int? Level, bool Charging)? ProbeCloudFlight(List<HidDevice> list)
    {
        var dev = list.FirstOrDefault(d => HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00 && HidSafe.MaxOutputLen(d) >= 20)
            ?? list.FirstOrDefault(d => HidSafe.MaxOutputLen(d) >= 20);
        if (dev == null) return null;
        using var ch = HidChannel.TryOpen(dev);
        if (ch == null) return null;

        var req = new byte[Math.Max(ch.OutputLength, 20)];
        req[0] = 0x21; req[1] = 0xFF; req[2] = 0x05;
        if (!ch.WriteOutput(req)) return null;

        var r = ch.Read(600);
        if (r == null) return null;
        return ParseCloudFlightReport(r);
    }

    internal static (int? Level, bool Charging)? ParseCloudFlightReport(ReadOnlySpan<byte> r)
    {
        if (r.Length != 15 && r.Length != 20)
            return null;
        ushort voltage = (ushort)((r[3] << 8) | r[4]);
        if (voltage == 0) return null;
        if (voltage > 0x100B)
            return (null, true);
        return (EstimateCloudFlightPercent(voltage), false);
    }

    internal static int EstimateCloudFlightPercent(ushort voltage)
    {
        if (voltage <= 3648)
            return Math.Clamp((int)Math.Round(0.00125f * voltage), 0, 100);
        if (voltage > 3975)
            return 100;
        double v = voltage;
        double est = 0.00000002547505 * Math.Pow(v, 4) - 0.0003900299 * Math.Pow(v, 3)
            + 2.238321 * Math.Pow(v, 2) - 5706.256 * v + 5452299.0;
        return Math.Clamp((int)Math.Round(est), 0, 100);
    }

    private static (int? Level, bool Charging)? ProbeCloud2(List<HidDevice> list)
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

    private static (int? Level, bool Charging)? ProbeCloud3(List<HidDevice> list)
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

    private static (int? Level, bool Charging)? ProbeCloud3S(List<HidDevice> list)
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

    private static (int? Level, bool Charging)? ProbeCloudAlpha(List<HidDevice> list)
    {
        var dev = list.FirstOrDefault(d => HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00 && HidSafe.MaxOutputLen(d) >= 31);
        if (dev == null) return null;
        using var ch = HidChannel.TryOpen(dev);
        if (ch == null) return null;

        byte[]? SendAlpha(byte cmd)
        {
            var req = new byte[Math.Max(ch.OutputLength, 31)];
            req[0] = 0x21; req[1] = 0xBB; req[2] = cmd;
            if (!ch.WriteOutput(req)) return null;
            return ch.Read(600);
        }

        // 0x03 连接状态：r[3] == 0x01 表示未连接
        var conn = SendAlpha(0x03);
        if (conn == null || conn.Length < 4 || conn[3] == 0x01)
            return null;

        // 0x0C 充电状态：r[3] == 0x01 表示充电中
        var chgResp = SendAlpha(0x0C);
        bool charging = chgResp != null && chgResp.Length >= 4 && chgResp[3] == 0x01;
        if (charging)
            return (null, true);

        // 0x0B 电量百分比
        var bat = SendAlpha(0x0B);
        if (bat != null && bat.Length >= 4 && bat[3] <= 100)
            return (bat[3], false);
        return null;
    }

    private static (int? Level, bool Charging)? ProbeCloudAlpha2(List<HidDevice> list)
    {
        var dev = list
            .Where(d => HidSafe.MaxOutputLen(d) >= 64)
            .OrderByDescending(d => HidSafe.PrimaryUsage(d) == (0xFF13, 0xFF00) ? 2 : 0)
            .FirstOrDefault();
        if (dev == null) return null;
        using var ch = HidChannel.TryOpen(dev);
        if (ch == null) return null;

        var conn = QueryAlpha2(ch, [0x52, 0x01], 0x53, 0x01);
        if (conn == null || conn.Length < 3 || conn[2] == 0x00)
            return null;

        var bat = QueryAlpha2(ch, [0x50, 0x02], 0x51, 0x02);
        return ParseAlpha2BatteryReport(bat);
    }

    internal static (int? Level, bool Charging)? ParseAlpha2BatteryReport(ReadOnlySpan<byte> r)
    {
        if (r.Length < 4 || r[0] != 0x51 || r[1] != 0x02 || r[2] > 100)
            return null;
        return (r[2], r[3] == 0x01);
    }

    private static byte[]? ReadAlpha2AudioBlock(HidChannel ch)
    {
        var r = QueryAlpha2(ch, [0x60, 0x02], 0x61, 0x02);
        if (r == null || r.Length < 16) return null;
        var block = r.AsSpan(4, 12).ToArray();
        if (block[4] > 1 || block[5] > 20 || block[11] is < 1 or > 3)
            return null;
        return block;
    }

    private static byte[]? QueryAlpha2(HidChannel ch, ReadOnlySpan<byte> cmd, byte replyCmd, byte replySub)
    {
        for (int i = 0; i < 6; i++)
            if (ch.Read(5) == null) break;

        var req = new byte[Math.Max(ch.OutputLength, 64)];
        cmd.CopyTo(req);
        if (!ch.WriteOutput(req)) return null;

        var dl = Environment.TickCount64 + 800;
        while (Environment.TickCount64 < dl)
        {
            var r = ch.Read(100);
            if (r == null || r.Length < 4) continue;
            if (r[0] == replyCmd && r[1] == replySub)
                return r;
        }
        return null;
    }
}
