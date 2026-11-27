using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 索尼 Sony INZONE 系列无线耳机 / Buds 协议提供器（VID 0x054C）：
/// 支持 INZONE Buds (0x0EC2) 双耳+盒电量，与 INZONE H5 (0x0EBF) / H7 (0x0DF4) / H9 (0x0DF5) / H9 II (0x0FA8)
/// 电量、Sidetone、ANC 模式、自动休眠、语音提示与 ChatMix 平衡读取。
/// </summary>
public sealed class SonyInzoneProvider : IPeripheralProvider, ITunablePeripheralProvider
{
    public const string ProviderName = "Sony INZONE";
    public const ushort SonyVid = 0x054C;
    public const ushort InzoneBudsPid = 0x0EC2;
    public const ushort InzoneH5Pid = 0x0EBF;
    public const ushort InzoneH7Pid = 0x0DF4;
    public const ushort InzoneH9Pid = 0x0DF5;
    public const ushort InzoneH9IiPid = 0x0FA8;

    private const byte AddrPcToRx = 0x41;
    private const byte ETypeGet = 0x01;
    private const byte ETypeSet = 0x02;
    private const byte ETypeRet = 0x10;
    private const byte ETypeNtfy = 0x20;
    private const byte ETypeNtfyActive = 0xA0;

    private const byte EidBatteryInfo = 0x04;
    private const byte EidChatMixBalance = 0x22;
    private const byte EidSidetoneVolume = 0x23;
    private const byte EidAmbSetting = 0x41;
    private const byte EidAutoPowerOff = 0x81;
    private const byte EidGuidanceSetting = 0x84;

    private static readonly Dictionary<ushort, string> InzoneHeadsets = new()
    {
        [InzoneBudsPid] = "Sony INZONE Buds",
        [InzoneH9Pid] = "Sony INZONE H9",
        [InzoneH7Pid] = "Sony INZONE H7",
        [InzoneH5Pid] = "Sony INZONE H5",
        [InzoneH9IiPid] = "Sony INZONE H9 II",
    };

    private static byte _seq = 2;

    public string Name => ProviderName;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("sony:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevicesByVendor(SonyVid)
            .Where(d => InzoneHeadsets.ContainsKey((ushort)d.ProductID))
            .GroupBy(d => (ushort)d.ProductID);

        foreach (var g in groups)
        {
            ushort pid = g.Key;
            string modelName = InzoneHeadsets[pid];
            var devs = g.ToList();

            (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? budsHit = null;
            (int Percent, bool Charging)? headsetHit = null;

            foreach (var iface in OrderCandidates(devs))
            {
                if (HidSafe.MaxOutputLen(iface) < 64 || HidSafe.MaxInputLen(iface) < 64) continue;
                using var ch = HidChannel.TryOpen(iface);
                if (ch == null) continue;

                if (pid == InzoneBudsPid)
                {
                    budsHit = QueryBudsBattery(ch);
                    if (budsHit != null) break;
                }
                else
                {
                    headsetHit = QueryHeadsetBattery(ch);
                    if (headsetHit != null) break;
                }
            }

            if (pid == InzoneBudsPid)
            {
                results.Add(BuildBudsDevice(pid, modelName, budsHit));
                continue;
            }

            results.Add(new PeripheralDevice
            {
                Id = $"sony:{SonyVid:X4}-{pid:X4}",
                VendorId = SonyVid,
                ProductId = pid,
                Name = modelName,
                Kind = DeviceKind.Headset,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = headsetHit == null,
                Transport = "2.4G",
                BatteryPercent = headsetHit?.Percent,
                State = headsetHit is { Charging: true } ? BatteryState.Charging
                    : (headsetHit != null ? BatteryState.Normal : BatteryState.Unknown),
                Note = headsetHit == null ? "2.4G 接收器已连接（耳机休眠或待唤醒）" : null,
                LastUpdated = DateTimeOffset.Now,
            });
        }

        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParsePid(deviceId, out ushort pid) || pid == InzoneBudsPid || !InzoneHeadsets.TryGetValue(pid, out string? model))
        {
            caps.StatusMessage = "当前 Sony 设备仅支持电量监测。";
            return caps;
        }

        using var ch = OpenControlChannel(pid);
        if (ch == null)
        {
            caps.StatusMessage = "2.4G 接收器未连接或控制通道被占用。";
            return caps;
        }

        caps.IsOnline = true;
        bool hasAnc = pid is InzoneH9Pid or InzoneH9IiPid;
        bool hasVoicePrompt = pid is InzoneH9Pid or InzoneH9IiPid or InzoneH5Pid;

        int currentAnc = 0;
        if (hasAnc)
        {
            var amb = Exchange(ch, EidAmbSetting, ETypeGet, []);
            if (amb is { Length: >= 1 } && amb[0] <= 2)
                currentAnc = amb[0];
        }

        int currentSidetone = 0;
        var st = Exchange(ch, EidSidetoneVolume, ETypeGet, []);
        if (st is { Length: >= 1 } && st[0] <= 50)
            currentSidetone = st[0];

        string? chatMix = null;
        var cm = Exchange(ch, EidChatMixBalance, ETypeGet, []);
        if (cm is { Length: >= 1 } && cm[0] <= 90)
        {
            int chatPct = cm[0] * 100 / 90;
            int gamePct = 100 - chatPct;
            chatMix = $"游戏 {gamePct}% / 语音 {chatPct}%";
        }

        caps.StatusMessage = $"{model} · INZONE HCI-over-HID 已就绪";
        caps.SupportsSidetone = true;
        caps.SidetoneEnabled = currentSidetone > 0;
        caps.MaxSidetoneLevel = 50;
        caps.CurrentSidetoneLevel = currentSidetone;
        caps.SupportsAncMode = hasAnc;
        caps.SupportedAncModes = hasAnc
            ? [new LodOption(0, "关闭"), new LodOption(1, "主动降噪"), new LodOption(2, "环境音")]
            : [];
        caps.CurrentAncMode = currentAnc;
        caps.SupportsVoicePrompt = hasVoicePrompt;
        caps.SupportsSleepTimeout = true;
        caps.MinSleepSeconds = 300;
        caps.MaxSleepSeconds = 10800;
        caps.SupportedSleepSeconds = [300, 900, 1800, 3600, 10800];
        caps.ChatMixSummary = chatMix;
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParsePid(deviceId, out ushort pid) || pid == InzoneBudsPid || !InzoneHeadsets.ContainsKey(pid))
            return new DeviceTuningResult(false, "当前设备不支持 Sony INZONE 调参");

        var applied = new List<string>();
        var errors = new List<string>();

        using (var ch = OpenControlChannel(pid))
        {
            if (ch == null)
                return new DeviceTuningResult(false, "无法打开 Sony INZONE 控制通道（请确认 2.4G 接收器与耳机已唤醒）");

            if (update.Sidetone is var (stOn, stLevel))
            {
                byte devLevel = stOn ? (byte)Math.Clamp(stLevel, 0, 50) : (byte)0;
                if (Exchange(ch, EidSidetoneVolume, ETypeSet, [devLevel, 0xFF]) != null)
                    applied.Add(stOn ? $"侧音 {devLevel}/50" : "侧音关闭");
                else
                    errors.Add("侧音");
            }

            if (update.AncMode is { } ancCode && pid is InzoneH9Pid or InzoneH9IiPid)
            {
                byte mode = (byte)Math.Clamp(ancCode, 0, 2);
                var cur = Exchange(ch, EidAmbSetting, ETypeGet, []);
                if (cur is { Length: >= 4 })
                {
                    byte[] payload = [mode, cur[1], cur[2], cur[3]];
                    if (Exchange(ch, EidAmbSetting, ETypeSet, payload) != null)
                        applied.Add($"降噪模式 {(mode == 1 ? "主动降噪" : mode == 2 ? "环境音" : "关闭")}");
                    else
                        errors.Add("降噪模式");
                }
                else
                {
                    errors.Add("降噪模式读取");
                }
            }

            if (update.VoicePromptEnabled is { } voiceOn && pid is InzoneH9Pid or InzoneH9IiPid or InzoneH5Pid)
            {
                if (Exchange(ch, EidGuidanceSetting, ETypeSet, [(byte)(voiceOn ? 1 : 0)]) != null)
                    applied.Add($"语音提示: {(voiceOn ? "开启" : "关闭")}");
                else
                    errors.Add("语音提示");
            }

            if (update.SleepSeconds is { } sleepSec && sleepSec > 0)
            {
                int mins = Math.Clamp((int)Math.Round(sleepSec / 60.0), 1, 180);
                byte snapped = mins switch
                {
                    <= 10 => 5,
                    <= 22 => 15,
                    <= 45 => 30,
                    <= 120 => 60,
                    _ => 180,
                };
                byte[] payload = pid == InzoneH9IiPid ? [snapped, snapped] : [snapped];
                if (Exchange(ch, EidAutoPowerOff, ETypeSet, payload) != null)
                    applied.Add($"休眠 {snapped} 分钟");
                else
                    errors.Add("自动休眠");
            }
        }

        if (applied.Count == 0 && errors.Count == 0)
            return new DeviceTuningResult(true, "未变更 Sony INZONE 参数", QueryCapabilities(deviceId));
        if (errors.Count > 0)
            return new DeviceTuningResult(applied.Count > 0, $"部分设置失败 ({string.Join("、", errors)})", QueryCapabilities(deviceId));
        return new DeviceTuningResult(true, $"已写入 {string.Join(" / ", applied)}", QueryCapabilities(deviceId));
    }

    private static IEnumerable<HidDevice> OrderCandidates(IEnumerable<HidDevice> devs)
        => devs
            .OrderByDescending(d =>
            {
                var (up, u) = HidSafe.PrimaryUsage(d);
                if (up == 0xFF04 && u is 0x0001 or 0x0002) return 4;
                return up >= 0xFF00 ? 2 : 0;
            })
            .ThenByDescending(d => HidSafe.MaxOutputLen(d) >= 64 && HidSafe.MaxInputLen(d) >= 64 ? 1 : 0);

    private static HidChannel? OpenControlChannel(ushort pid)
    {
        var devs = HidSafe.EnumerateDevicesByVendor(SonyVid)
            .Where(d => (ushort)d.ProductID == pid)
            .ToList();
        foreach (var iface in OrderCandidates(devs))
        {
            if (HidSafe.MaxOutputLen(iface) < 64 || HidSafe.MaxInputLen(iface) < 64) continue;
            var ch = HidChannel.TryOpen(iface);
            if (ch != null) return ch;
        }
        return null;
    }

    private static bool TryParsePid(string deviceId, out ushort pid)
    {
        pid = 0;
        var parts = deviceId.Split(':');
        if (parts.Length < 2) return false;
        var vp = parts[1].Split('-');
        return vp.Length == 2 && ushort.TryParse(vp[1], System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    private static PeripheralDevice BuildBudsDevice(
        ushort pid,
        string modelName,
        (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? hit)
    {
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
            Diag.Log($"Sony INZONE Buds: L={b.Left?.ToString() ?? "--"} R={b.Right?.ToString() ?? "--"} Case={b.Case?.ToString() ?? "--"}");
        }
        else
        {
            note = "2.4G 接收器已连接（耳机均在盒内待机中，取出任一只即可读取左/右耳与盒电量）";
        }

        return new PeripheralDevice
        {
            Id = $"sony:{SonyVid:X4}-{pid:X4}",
            VendorId = SonyVid,
            ProductId = pid,
            Name = modelName,
            Kind = DeviceKind.Headset,
            Provider = ProviderName,
            Connected = true,
            IsSleeping = sleeping,
            Transport = "2.4G USB-C",
            BatteryPercent = mainPct,
            LeftEarPercent = hit?.Left,
            LeftEarCharging = hit?.LeftChg == true,
            RightEarPercent = hit?.Right,
            RightEarCharging = hit?.RightChg == true,
            CaseBatteryPercent = hit?.Case,
            CaseCharging = hit?.CaseChg == true,
            State = batState,
            Note = note,
            LastUpdated = DateTimeOffset.Now,
        };
    }

    private static (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? QueryBudsBattery(HidChannel ch)
    {
        for (int i = 0; i < 6; i++)
            if (ch.Read(5) == null) break;

        byte seq = NextSeq();
        byte[] req = BuildHubRequest(EidBatteryInfo, seq);
        if (!ch.WriteOutput(req))
            return null;

        var deadline = Environment.TickCount64 + 550;
        (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? fallback = null;
        while (Environment.TickCount64 < deadline)
        {
            var r = ch.Read(90);
            if (r == null) continue;
            var parsed = TryParseHubBatteryReport(r);
            if (parsed != null)
            {
                if (r.Length > 11 && r[10] == ETypeRet && r[11] == seq)
                    return parsed;
                fallback ??= parsed;
                continue;
            }
            fallback ??= TryParsePassiveBudsReport(r);
        }
        return fallback;
    }

    private static (int Percent, bool Charging)? QueryHeadsetBattery(HidChannel ch)
    {
        var payload = Exchange(ch, EidBatteryInfo, ETypeGet, []);
        if (payload == null || payload.Length < 2) return null;
        byte charger = payload[0];
        byte pct = payload[1];
        if (pct == 0xFF || pct > 100) return null;
        return (pct, charger != 0);
    }

    private static byte[]? Exchange(HidChannel ch, byte eventId, byte eventType, ReadOnlySpan<byte> payload)
    {
        for (int i = 0; i < 6; i++)
            if (ch.Read(5) == null) break;

        byte seq = NextSeq();
        byte[] req = BuildHciPacket(eventId, eventType, seq, payload);
        if (!ch.WriteOutput(req))
            return null;

        byte wantType = eventType == ETypeSet ? ETypeNtfy : ETypeRet;
        var dl = Environment.TickCount64 + 900;
        while (Environment.TickCount64 < dl)
        {
            var r = ch.Read(100);
            if (r == null || r.Length < 14) continue;
            if (r[0] != 0x02 || r[6] != 0x96 || r[7] != 0xC3) continue;
            if (r[9] != eventId || r[11] != seq) continue;
            if (r[10] != wantType && r[10] != ETypeNtfyActive) continue;

            int hciLen = r[5];
            int payloadLen = Math.Clamp(hciLen - 8, 0, Math.Max(0, r.Length - 14));
            return r.AsSpan(13, payloadLen).ToArray();
        }
        return null;
    }

    private static byte NextSeq()
    {
        byte s = ++_seq;
        if (s <= 1) s = _seq = 2;
        return s;
    }

    internal static byte[] BuildHubRequest(byte subcommand, byte sequence)
        => BuildHciPacket(subcommand, ETypeGet, sequence, []);

    internal static byte[] BuildHciPacket(byte eventId, byte eventType, byte sequence, ReadOnlySpan<byte> payload, byte address = AddrPcToRx)
    {
        var req = new byte[64];
        int pLen = Math.Min(payload.Length, 64 - 14);
        req[0] = 0x02;
        req[1] = (byte)(12 + pLen);
        req[2] = 0x01;
        req[3] = 0x00;
        req[4] = 0xFC;
        req[5] = (byte)(8 + pLen);
        req[6] = 0x96;
        req[7] = 0xC3;
        req[8] = address;
        req[9] = eventId;
        req[10] = eventType;
        req[11] = sequence;
        req[12] = 0x00;
        payload[..pLen].CopyTo(req.AsSpan(13));
        int sum = 0;
        for (int i = 6; i < 13 + pLen; i++)
            sum += req[i];
        req[13 + pLen] = (byte)sum;
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

    /// <summary>解析 INZONE Buds 主动推送（byte[1]=0x12, byte[2]=0x04）电池帧。</summary>
    internal static (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? TryParsePassiveBudsReport(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 18) return null;
        if (data[0] != 0x02 || data[1] != 0x12 || data[2] != 0x04)
            return null;

        int? right = data[14] <= 100 ? data[14] : null;
        int? left = data[16] <= 100 ? data[16] : null;
        int? box = data[18] <= 100 ? data[18] : null;
        bool rChg = data[13] == 0x01;
        bool lChg = data[15] == 0x01;
        bool cChg = data[17] == 0x01;

        if (left == null && right == null && box == null)
            return null;
        return (left, lChg, right, rChg, box, cChg);
    }
}
