using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// Audeze Maxwell (PS 0x4B18 / Xbox 0x4B19，VID 0x3329) 无线耳机协议提供器（基于 HeadsetControl）：
/// Report Out 0x06 / Report In 0x07 (62B 分片流)，支持电量、侧音与自动关机时间控制。
/// </summary>
public sealed class AudezeProvider : IPeripheralProvider, ITunablePeripheralProvider
{
    public const string ProviderName = "Audeze";
    public const ushort AudezeVid = 0x3329;
    public const ushort MaxwellPsPid = 0x4B18;
    public const ushort MaxwellXboxPid = 0x4B19;

    public const int MsgSize = 62;
    public const byte ReportIdOut = 0x06;
    public const byte ReportIdIn = 0x07;
    public const int FragmentOffset = 3;
    public const int MaxFragmentSize = MsgSize - FragmentOffset;
    public const byte RouteHeadset = 0x80;
    public const byte MessageMarker = 0x05;
    public const byte TypeRequest = 0x5A;
    public const byte TypeResponse = 0x5B;
    public const byte TypeResult = 0x5D;

    private const byte KeySidetoneLevel = 0x2C;
    private const byte KeySidetoneMaxLevel = 0x2D;
    private const byte ParamAutoPowerOff = 0x01;
    private const byte ParamSidetoneEnabled = 0x07;

    public string Name => ProviderName;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("audeze:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevicesByVendor(AudezeVid)
            .Where(d => (ushort)d.ProductID is MaxwellPsPid or MaxwellXboxPid)
            .GroupBy(d => (ushort)d.ProductID);

        foreach (var g in groups)
        {
            ushort pid = g.Key;
            string model = pid == MaxwellXboxPid ? "Audeze Maxwell (Xbox)" : "Audeze Maxwell (PS)";
            int? bat = ProbeBattery(g.ToList());

            results.Add(new PeripheralDevice
            {
                Id = $"audeze:{AudezeVid:X4}-{pid:X4}",
                VendorId = AudezeVid,
                ProductId = pid,
                Name = model,
                Kind = DeviceKind.Headset,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = bat == null,
                Transport = "2.4G",
                BatteryPercent = bat,
                State = bat.HasValue ? BatteryState.Normal : BatteryState.Unknown,
                Note = bat == null ? "Audeze 接收器已连接（耳机未开机或休眠）" : null,
                LastUpdated = DateTimeOffset.Now,
            });
        }
        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!ProviderIdHelper.TryParsePid(deviceId, out ushort pid) || pid is not (MaxwellPsPid or MaxwellXboxPid))
        {
            caps.StatusMessage = "当前 Audeze 设备仅支持电量监测。";
            return caps;
        }

        using var ch = OpenControlChannel(pid);
        if (ch == null)
        {
            caps.StatusMessage = "未检测到可用的 Audeze Maxwell 控制通道。";
            return caps;
        }

        byte? maxSt = ReadKey(ch, KeySidetoneMaxLevel);
        byte? curSt = ReadKey(ch, KeySidetoneLevel);
        byte? stOn = ReadParam(ch, ParamSidetoneEnabled);
        if (curSt == null && stOn == null)
        {
            caps.StatusMessage = "Audeze Maxwell 当前离线或未响应，请开机后重试。";
            return caps;
        }

        int deviceMax = Math.Max((int)(maxSt ?? 31), (int)(curSt ?? 0));
        if (deviceMax <= 0) deviceMax = 31;

        caps.IsOnline = true;
        caps.StatusMessage = pid == MaxwellXboxPid ? "Audeze Maxwell (Xbox) · HID 分片通道" : "Audeze Maxwell (PS) · HID 分片通道";
        caps.SupportsSidetone = true;
        caps.SidetoneEnabled = (stOn ?? 0) != 0 && (curSt ?? 0) > 0;
        caps.CurrentSidetoneLevel = Math.Clamp((int)(curSt ?? 0), 0, deviceMax);
        caps.MaxSidetoneLevel = deviceMax;
        caps.SupportsSleepTimeout = true;
        caps.MinSleepSeconds = 300;
        caps.MaxSleepSeconds = 3600;
        caps.SupportedSleepSeconds = [300, 600, 900, 1800, 3600];
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!ProviderIdHelper.TryParsePid(deviceId, out ushort pid) || pid is not (MaxwellPsPid or MaxwellXboxPid))
            return new DeviceTuningResult(false, "当前 Audeze 设备不支持调参");

        using var ch = OpenControlChannel(pid);
        if (ch == null)
            return new DeviceTuningResult(false, "无法打开 Audeze Maxwell 控制通道");

        var applied = new List<string>();
        var errors = new List<string>();

        if (update.Sidetone is var (stOn, stLevel))
        {
            bool enable = stOn && stLevel > 0;
            bool ok = WriteParam(ch, ParamSidetoneEnabled, (byte)(enable ? 1 : 0));
            if (ok && enable)
            {
                byte maxSt = ReadKey(ch, KeySidetoneMaxLevel) ?? 31;
                byte lvl = (byte)Math.Clamp(stLevel, 1, Math.Max((int)maxSt, 1));
                ok = WriteKey(ch, KeySidetoneLevel, lvl);
            }
            if (ok) applied.Add(enable ? $"侧音 {stLevel}" : "侧音关闭");
            else errors.Add("侧音");
        }

        if (update.SleepSeconds is { } sec && sec > 0)
        {
            byte mins = (byte)Math.Clamp((int)Math.Round(sec / 60.0), 1, 255);
            if (WriteParam(ch, ParamAutoPowerOff, mins))
                applied.Add($"自动关机 {mins} 分钟");
            else
                errors.Add("自动关机");
        }

        var refreshed = QueryCapabilities(deviceId);
        if (applied.Count == 0 && errors.Count == 0)
            return new DeviceTuningResult(true, "未变更 Audeze 参数", refreshed);
        return errors.Count > 0
            ? new DeviceTuningResult(applied.Count > 0, $"部分设置失败 ({string.Join("、", errors)})", refreshed)
            : new DeviceTuningResult(true, $"已写入 {string.Join(" / ", applied)}", refreshed);
    }

    public static byte[] BuildMessage(byte type, ReadOnlySpan<byte> body)
    {
        var msg = new byte[4 + body.Length];
        msg[0] = MessageMarker;
        msg[1] = type;
        msg[2] = (byte)(body.Length & 0xFF);
        msg[3] = (byte)(body.Length >> 8);
        body.CopyTo(msg.AsSpan(4));
        return msg;
    }

    public static List<byte[]> BuildReports(ReadOnlySpan<byte> message)
    {
        var list = new List<byte[]>();
        for (int offset = 0; offset < message.Length; offset += MaxFragmentSize)
        {
            int len = Math.Min(MaxFragmentSize, message.Length - offset);
            var report = new byte[MsgSize];
            report[0] = ReportIdOut;
            report[1] = (byte)len;
            report[2] = RouteHeadset;
            message.Slice(offset, len).CopyTo(report.AsSpan(FragmentOffset));
            list.Add(report);
        }
        return list;
    }

    public static byte[]? TryExtractMessage(List<byte> stream)
    {
        while (stream.Count > 0)
        {
            if (stream[0] != MessageMarker)
            {
                stream.RemoveAt(0);
                continue;
            }
            if (stream.Count < 4) return null;
            int totalSize = 4 + (stream[2] | (stream[3] << 8));
            if (totalSize > 512)
            {
                stream.RemoveAt(0);
                continue;
            }
            if (stream.Count < totalSize) return null;
            byte[] msg = stream.GetRange(0, totalSize).ToArray();
            stream.RemoveRange(0, totalSize);
            return msg;
        }
        return null;
    }

    public static int? ParseBatteryMessage(ReadOnlySpan<byte> msg)
    {
        if (msg.Length <= 8 || msg[1] != TypeResult || msg[4] != 0xD6 || msg[5] != 0x0C)
            return null;
        return Math.Clamp((int)msg[8], 0, 100);
    }

    private static HidChannel? OpenControlChannel(ushort pid)
    {
        var dev = HidSafe.EnumerateDevicesByVendor(AudezeVid)
            .Where(d => (ushort)d.ProductID == pid && HidSafe.MaxOutputLen(d) >= MsgSize && HidSafe.MaxInputLen(d) >= MsgSize)
            .OrderByDescending(d => HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00 ? 1 : 0)
            .FirstOrDefault();
        return dev != null ? HidChannel.TryOpen(dev) : null;
    }

    private static int? ProbeBattery(List<HidDevice> ifaces)
    {
        var dev = ifaces
            .Where(d => HidSafe.MaxOutputLen(d) >= MsgSize && HidSafe.MaxInputLen(d) >= MsgSize)
            .OrderByDescending(d => HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00 ? 1 : 0)
            .FirstOrDefault();
        if (dev == null) return null;
        using var ch = HidChannel.TryOpen(dev);
        if (ch == null) return null;

        var msg = RequestMessage(ch, [0xD6, 0x0C, 0x00], m =>
            (m.Length > 8 && m[1] == TypeResult && m[4] == 0xD6 && m[5] == 0x0C) ||
            (m.Length > 6 && m[1] == TypeResponse && m[4] == 0xD6 && m[5] == 0x0C && m[6] != 0));
        return msg != null ? ParseBatteryMessage(msg) : null;
    }

    private static byte? ReadKey(HidChannel ch, byte key)
    {
        var msg = RequestMessage(ch, [0x01, 0x09, key, 0x00], m =>
            m.Length >= 9 && m[1] == TypeResponse && m[4] == 0x01 && m[5] == 0x09 && m[6] == key);
        return msg is { Length: >= 9 } ? msg[8] : null;
    }

    private static bool WriteKey(HidChannel ch, byte key, byte value)
    {
        var msg = RequestMessage(ch, [0x00, 0x09, key, 0x00, value], m =>
            m.Length >= 8 && m[1] == TypeResponse && m[4] == 0x00 && m[5] == 0x09 && m[6] == key);
        return msg != null;
    }

    private static byte? ReadParam(HidChannel ch, byte param)
    {
        var msg = RequestMessage(ch, [0x83, 0x2C, param, 0x00], m =>
            m.Length >= 10 && m[1] == TypeResponse && m[4] == 0x83 && m[5] == 0x2C && m[7] == param);
        return msg is { Length: >= 10 } && msg[6] == 0 ? msg[9] : null;
    }

    private static bool WriteParam(HidChannel ch, byte param, byte value)
    {
        var msg = RequestMessage(ch, [0x82, 0x2C, param, 0x00, value], m =>
            m.Length >= 9 && m[1] == TypeResponse && m[4] == 0x82 && m[5] == 0x2C && m[7] == param);
        return msg is { Length: >= 7 } && msg[6] == 0;
    }

    private static byte[]? RequestMessage(HidChannel ch, ReadOnlySpan<byte> body, Func<byte[], bool> accept)
    {
        while (ch.Read(5) != null) { }
        var stream = new List<byte>(128);
        foreach (var r in BuildReports(BuildMessage(TypeRequest, body)))
        {
            if (!ch.WriteOutput(r))
                return null;
        }

        long dl = Environment.TickCount64 + 1100;
        while (Environment.TickCount64 < dl)
        {
            Thread.Sleep(45);
            var report = ch.Read(40);
            if (report == null || report.Length < FragmentOffset || report[0] != ReportIdIn)
                continue;
            int fragLen = report[1];
            if (fragLen == 0 || fragLen > MaxFragmentSize || FragmentOffset + fragLen > report.Length)
                continue;
            for (int i = 0; i < fragLen; i++)
                stream.Add(report[FragmentOffset + i]);
            while (TryExtractMessage(stream) is { } msg)
            {
                if (accept(msg))
                    return msg;
            }
        }
        return null;
    }
}
