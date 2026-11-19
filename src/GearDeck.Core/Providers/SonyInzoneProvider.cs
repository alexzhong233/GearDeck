using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 索尼 Sony INZONE Buds (WF-G700N, VID 0x054C PID 0x0EC2) 主动 HID 查询协议：
/// 使用 0xFF04/0x0001 64 字节接口发送 Report ID 0x02 / Cmd 0x41 / Sub 0x04 查询包，
/// 解析左耳 (offset 0x0E)、右耳 (offset 0x10) 与充电盒 (offset 0x12) 真实百分比。
/// </summary>
public sealed class SonyInzoneProvider : IPeripheralProvider
{
    public const string ProviderName = "Sony INZONE";
    public const ushort SonyVid = 0x054C;
    public const ushort InzoneBudsPid = 0x0EC2;

    private static byte _seq = 1;

    public string Name => ProviderName;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("sony:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var devs = HidSafe.EnumerateDevices(SonyVid, InzoneBudsPid);
        if (devs.Count == 0)
            return results;

        var candidates = devs
            .OrderByDescending(d => HidSafe.PrimaryUsage(d) == (0xFF04, 0x0001) ? 2 : 0)
            .ThenByDescending(d => HidSafe.MaxOutputLen(d) >= 64 && HidSafe.MaxInputLen(d) >= 64 ? 1 : 0)
            .ToList();

        (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? hit = null;
        foreach (var iface in candidates)
        {
            if (HidSafe.MaxOutputLen(iface) < 64 || HidSafe.MaxInputLen(iface) < 64) continue;
            using var ch = HidChannel.TryOpen(iface);
            if (ch == null) continue;
            hit = QueryBattery(ch);
            if (hit != null) break;
        }

        int? mainPct = null;
        BatteryState batState = BatteryState.Unknown;
        bool sleeping = true;
        string note;

        if (hit is { } b)
        {
            var activeBuds = new List<int>(2);
            if (b.Left is { } l) activeBuds.Add(l);
            if (b.Right is { } r) activeBuds.Add(r);

            if (activeBuds.Count > 0)
            {
                mainPct = activeBuds.Min();
                sleeping = false;
                batState = b.LeftChg || b.RightChg ? BatteryState.Charging : BatteryState.Normal;
            }
            else if (b.Case is { } c)
            {
                mainPct = c;
                sleeping = true;
                batState = b.CaseChg ? BatteryState.Charging : BatteryState.Normal;
            }

            string lStr = b.Left is { } lv ? $"{lv}%{(b.LeftChg ? "⚡" : "")}" : "在盒";
            string rStr = b.Right is { } rv ? $"{rv}%{(b.RightChg ? "⚡" : "")}" : "在盒";
            string cStr = b.Case is { } cv ? $"{cv}%{(b.CaseChg ? "⚡" : "")}" : "--";
            note = $"左耳 {lStr} · 右耳 {rStr} · 充电盒 {cStr}";
        }
        else
        {
            note = "2.4G 接收器已连接（耳机均在盒内待机中，取出任一只即可读取左/右耳与盒电量）";
        }

        results.Add(new PeripheralDevice
        {
            Id = $"sony:{SonyVid:X4}-{InzoneBudsPid:X4}",
            Name = "Sony INZONE Buds",
            Kind = DeviceKind.Headset,
            Provider = ProviderName,
            Connected = true,
            IsSleeping = sleeping,
            Transport = "2.4G USB-C",
            BatteryPercent = mainPct,
            State = batState,
            Note = note,
            LastUpdated = DateTimeOffset.Now,
        });

        return results;
    }

    private static (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? QueryBattery(HidChannel ch)
    {
        for (int i = 0; i < 6; i++)
            if (ch.Read(5) == null) break;

        byte seq = ++_seq;
        if (seq == 0) seq = ++_seq;
        byte[] req = BuildHubRequest(0x04, seq);
        if (!ch.WriteOutput(req))
            return null;

        var deadline = Environment.TickCount64 + 500;
        (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? fallback = null;
        while (Environment.TickCount64 < deadline)
        {
            var r = ch.Read(90);
            if (r == null) continue;
            var parsed = TryParseHubBatteryReport(r);
            if (parsed == null) continue;
            if (r.Length > 11 && r[10] == 0x10 && r[11] == seq)
                return parsed;
            fallback ??= parsed;
        }
        return fallback;
    }

    internal static byte[] BuildHubRequest(byte subcommand, byte sequence)
    {
        var req = new byte[64];
        req[0] = 0x02;
        req[1] = 0x0C;
        req[2] = 0x01;
        req[3] = 0x00;
        req[4] = 0xFC;
        req[5] = 0x08;
        req[6] = 0x96;
        req[7] = 0xC3;
        req[8] = 0x41;
        req[9] = subcommand;
        req[10] = 0x01;
        req[11] = sequence;
        req[12] = 0x00;
        req[13] = (byte)(0x9B + subcommand + sequence);
        return req;
    }

    internal static (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? TryParseHubBatteryReport(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 0x12) return null;
        if (data[0] != 0x02 || data[8] != 0x14 || data[9] != 0x04)
            return null;

        int? left = data[0x0E] <= 100 ? data[0x0E] : null;
        int? right = data[0x10] <= 100 ? data[0x10] : null;
        int? box = data[0x12] <= 100 ? data[0x12] : null;
        bool lChg = data[0x0D] == 0x01;
        bool rChg = data[0x0F] == 0x01;
        bool cChg = data[0x11] == 0x01;

        if (left == null && right == null && box == null)
            return null;
        return (left, lChg, right, rChg, box, cChg);
    }
}
