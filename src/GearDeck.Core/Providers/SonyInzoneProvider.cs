using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 索尼 Sony INZONE 系列与 PULSE 3D 无线耳机/Buds 协议提供器（VID 0x054C）：
/// 支持 INZONE Buds (0x0EC2)、INZONE H5/H7/H9/H9 II 电量与降噪/侧音/休眠/语音/ChatMix，及 PULSE 3D (0x0D5E) 被动状态帧电量。
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
    public const ushort Pulse3DPid = 0x0D5E;

    private static readonly object Pulse3DLock = new();
    private static (int? Level, bool Charging, bool Connected)? _lastPulse3D;
    private static DateTimeOffset _lastPulse3DUtc = DateTimeOffset.MinValue;

    private const byte AddrPcToTx = 0x21;
    private const byte AddrPcToRx = 0x41;
    private const byte ETypeGet = 0x01;
    private const byte ETypeSet = 0x02;
    private const byte ETypeRet = 0x10;
    private const byte ETypeNtfy = 0x20;
    private const byte ETypeNtfyActive = 0xA0;

    private const byte Eid2GhzConnectStatus = 0x01;
    private const byte EidBatteryInfo = 0x04;
    private const byte EidAllFunctionSettingsPart1 = 0x06;
    private const byte EidAllFunctionSettingsPart2 = 0x07;
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
        [Pulse3DPid] = "Sony PULSE 3D",
    };

    private static readonly object BudsStateLock = new();
    private static (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? _lastBudsHit;
    private static DateTimeOffset _lastBudsHitUtc = DateTimeOffset.MinValue;
    private static int _consecutiveBudsMisses;

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
            bool? budsLinkConnected = null;
            (int Percent, bool Charging)? headsetHit = null;

            if (pid == Pulse3DPid)
            {
                var p3d = ProbePulse3D(devs);
                results.Add(new PeripheralDevice
                {
                    Id = $"sony:{SonyVid:X4}-{pid:X4}",
                    VendorId = SonyVid,
                    ProductId = pid,
                    Name = modelName,
                    Kind = DeviceKind.Headset,
                    Provider = ProviderName,
                    Connected = true,
                    IsSleeping = p3d == null || !p3d.Value.Connected,
                    Transport = "2.4G",
                    BatteryPercent = p3d?.Connected == true ? p3d.Value.Level : null,
                    State = p3d is { Connected: true, Charging: true }
                        ? BatteryState.Charging
                        : (p3d is { Connected: true, Level: not null } ? BatteryState.Normal : BatteryState.Unknown),
                    Note = p3d == null || !p3d.Value.Connected ? "2.4G 接收器已连接（PULSE 3D 休眠或待按键唤醒）" : null,
                    LastUpdated = DateTimeOffset.Now,
                });
                continue;
            }

            foreach (var iface in OrderCandidates(devs))
            {
                if (HidSafe.MaxOutputLen(iface) < 64 || HidSafe.MaxInputLen(iface) < 64) continue;
                using var ch = HidChannel.TryOpen(iface);
                if (ch == null) continue;

                if (pid == InzoneBudsPid)
                {
                    budsHit = QueryBudsBattery(ch);
                    if (budsHit == null)
                        budsLinkConnected ??= QueryTransmitterLinkStatus(ch);
                    if (budsHit != null || budsLinkConnected != null)
                        break;
                }
                else
                {
                    headsetHit = QueryHeadsetBattery(ch);
                    if (headsetHit != null) break;
                }
            }

            if (pid == InzoneBudsPid)
            {
                lock (BudsStateLock)
                {
                    if (budsHit is { } hit && (hit.Left != null || hit.Right != null))
                    {
                        _lastBudsHit = hit;
                        _lastBudsHitUtc = DateTimeOffset.UtcNow;
                        _consecutiveBudsMisses = 0;
                    }
                    else if (budsHit == null)
                    {
                        _consecutiveBudsMisses++;
                        if (budsLinkConnected != false &&
                            _consecutiveBudsMisses < 2 &&
                            _lastBudsHit is { } cached &&
                            (DateTimeOffset.UtcNow - _lastBudsHitUtc) < TimeSpan.FromMinutes(3))
                        {
                            budsHit = cached;
                        }
                    }
                }

                results.Add(BuildBudsDevice(pid, modelName, budsHit, budsLinkConnected));
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
        if (!TryParsePid(deviceId, out ushort pid) || !InzoneHeadsets.TryGetValue(pid, out string? model) || pid == Pulse3DPid)
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

        bool isBuds = pid == InzoneBudsPid;
        if (isBuds && QueryTransmitterLinkStatus(ch) == false)
        {
            caps.StatusMessage = "2.4G 链路当前未连接（请先将至少一只耳机佩戴入耳并切换到 2.4G 模式）。";
            return caps;
        }

        caps.IsOnline = true;
        bool hasAnc = isBuds || pid is InzoneH9Pid or InzoneH9IiPid;
        bool hasVoicePrompt = isBuds || pid is InzoneH9Pid or InzoneH9IiPid or InzoneH5Pid;
        int maxSidetone = isBuds ? 10 : 50;

        byte[]? part1 = isBuds ? Exchange(ch, EidAllFunctionSettingsPart1, ETypeGet, []) : null;
        byte[]? part2 = isBuds ? Exchange(ch, EidAllFunctionSettingsPart2, ETypeGet, []) : null;

        int currentAnc = 0;
        if (hasAnc)
        {
            if (part2 is { Length: >= 7 } && part2[3] <= 2)
            {
                currentAnc = part2[3];
            }
            else
            {
                var amb = Exchange(ch, EidAmbSetting, ETypeGet, []);
                if (amb is { Length: >= 1 } && amb[0] <= 2)
                    currentAnc = amb[0];
            }
        }

        int currentSidetone = 0;
        if (part1 is { Length: >= 13 } && part1[11] <= maxSidetone)
        {
            currentSidetone = part1[11];
        }
        else
        {
            var st = Exchange(ch, EidSidetoneVolume, ETypeGet, []);
            if (st is { Length: >= 1 } && st[0] <= maxSidetone)
                currentSidetone = st[0];
        }

        string? chatMix = null;
        int cmMax = isBuds ? 100 : 90;
        int cmVal = part1 is { Length: >= 11 } && part1[10] <= cmMax
            ? part1[10]
            : (Exchange(ch, EidChatMixBalance, ETypeGet, []) is { Length: >= 1 } cm && cm[0] <= cmMax ? cm[0] : -1);
        if (cmVal >= 0)
        {
            int chatPct = cmMax == 100 ? cmVal : cmVal * 100 / 90;
            int gamePct = 100 - chatPct;
            chatMix = $"游戏 {gamePct}% / 语音 {chatPct}%";
        }

        caps.StatusMessage = $"{model} · INZONE HCI-over-HID 已就绪";
        caps.SupportsSidetone = true;
        caps.SidetoneEnabled = currentSidetone > 0;
        caps.MaxSidetoneLevel = maxSidetone;
        caps.CurrentSidetoneLevel = currentSidetone;
        caps.SupportsAncMode = hasAnc;
        caps.SupportedAncModes = hasAnc
            ? [new LodOption(0, "关闭"), new LodOption(1, "主动降噪"), new LodOption(2, "环境音")]
            : [];
        caps.CurrentAncMode = currentAnc;
        caps.SupportsVoicePrompt = hasVoicePrompt;
        caps.SupportsSleepTimeout = true;
        if (isBuds)
        {
            caps.MinSleepSeconds = 900;
            caps.MaxSleepSeconds = 900;
            caps.SupportedSleepSeconds = [900];
        }
        else
        {
            caps.MinSleepSeconds = 300;
            caps.MaxSleepSeconds = 10800;
            caps.SupportedSleepSeconds = [300, 900, 1800, 3600, 10800];
        }
        caps.ChatMixSummary = chatMix;
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!TryParsePid(deviceId, out ushort pid) || !InzoneHeadsets.ContainsKey(pid))
            return new DeviceTuningResult(false, "当前设备不支持 Sony INZONE 调参");

        bool isBuds = pid == InzoneBudsPid;
        int maxSidetone = isBuds ? 10 : 50;
        var applied = new List<string>();
        var errors = new List<string>();

        using (var ch = OpenControlChannel(pid))
        {
            if (ch == null)
                return new DeviceTuningResult(false, "无法打开 Sony INZONE 控制通道（请确认 2.4G 接收器与耳机已唤醒）");

            if (isBuds && QueryTransmitterLinkStatus(ch) == false)
                return new DeviceTuningResult(false, "INZONE Buds 当前未通过 2.4G 链路连接（请确认已佩戴在耳并处于 2.4G 模式）");

            if (update.Sidetone is var (stOn, stLevel))
            {
                byte devLevel = stOn ? (byte)Math.Clamp(stLevel, 0, maxSidetone) : (byte)0;
                if (Exchange(ch, EidSidetoneVolume, ETypeSet, [devLevel, 0xFF]) != null)
                    applied.Add(devLevel > 0 ? $"侧音 {devLevel}/{maxSidetone}" : "侧音关闭");
                else
                    errors.Add("侧音");
            }

            if (update.AncMode is { } ancCode && (isBuds || pid is InzoneH9Pid or InzoneH9IiPid))
            {
                byte mode = (byte)Math.Clamp(ancCode, 0, 2);
                var cur = Exchange(ch, EidAmbSetting, ETypeGet, []);
                byte[] payload = cur is { Length: >= 4 }
                    ? [mode, cur[1], cur[2], cur[3]]
                    : [mode, 0x14, 0xFF, 0x00];
                if (Exchange(ch, EidAmbSetting, ETypeSet, payload) != null)
                    applied.Add($"降噪模式 {(mode == 1 ? "主动降噪" : mode == 2 ? "环境音" : "关闭")}");
                else
                    errors.Add("降噪模式");
            }

            if (update.VoicePromptEnabled is { } voiceOn && (isBuds || pid is InzoneH9Pid or InzoneH9IiPid or InzoneH5Pid))
            {
                if (Exchange(ch, EidGuidanceSetting, ETypeSet, [(byte)(voiceOn ? 1 : 0)]) != null)
                    applied.Add($"语音提示: {(voiceOn ? "开启" : "关闭")}");
                else
                    errors.Add("语音提示");
            }

            if (update.SleepSeconds is { } sleepSec)
            {
                if (isBuds)
                {
                    byte val = sleepSec <= 0 ? (byte)0x00 : (byte)0x0F;
                    if (Exchange(ch, EidAutoPowerOff, ETypeSet, [val]) != null)
                        applied.Add(val == 0 ? "自动休眠: 关闭" : "自动休眠: 15 分钟");
                    else
                        errors.Add("自动休眠");
                }
                else if (sleepSec > 0)
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
        => ProviderIdHelper.TryParsePid(deviceId, out pid);

    private static PeripheralDevice BuildBudsDevice(
        ushort pid,
        string modelName,
        (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? hit,
        bool? linkConnected = null)
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
            Diag.Verbose($"Sony INZONE Buds: L={b.Left?.ToString() ?? "--"} R={b.Right?.ToString() ?? "--"} Case={b.Case?.ToString() ?? "--"}");
        }
        else
        {
            note = linkConnected == false
                ? "2.4G 链路当前未连接（耳机在盒、已切蓝牙或已自动待机）"
                : "2.4G 接收器已连接（耳机休眠或暂未响应）";
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

    internal static bool? QueryTransmitterLinkStatus(HidChannel ch)
    {
        var payload = ExchangeOnce(ch, AddrPcToTx, Eid2GhzConnectStatus, ETypeGet, [], timeoutMs: 250);
        if (payload == null || payload.Length == 0)
            return null;
        return payload.Any(b => b != 0);
    }

    private static (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? QueryBudsBattery(HidChannel ch)
    {
        for (int i = 0; i < 6; i++)
            if (ch.Read(5) == null) break;

        (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? fallback = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            byte seq = NextSeq();
            byte[] req = BuildHubRequest(EidBatteryInfo, seq);
            if (!ch.WriteOutput(req))
                break;

            var deadline = Environment.TickCount64 + 1200;
            while (Environment.TickCount64 < deadline)
            {
                var r = ch.Read(100);
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
            if (fallback != null)
                return fallback;
        }

        // 回退尝试单包读取 AllFunctionSettingsPart1 (EID 0x06)：[modelId, 6B battery, ...]
        var part1 = ExchangeOnce(ch, AddrPcToRx, EidAllFunctionSettingsPart1, ETypeGet, [], timeoutMs: 900);
        if (part1 != null)
        {
            var fromPart1 = TryParsePart1BudsBattery(part1);
            if (fromPart1 != null)
                return fromPart1;
        }

        return null;
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

    private static byte[]? Exchange(HidChannel ch, byte eventId, byte eventType, ReadOnlySpan<byte> payload, byte address = AddrPcToRx)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var res = ExchangeOnce(ch, address, eventId, eventType, payload, timeoutMs: 1500);
            if (res != null)
                return res;
        }
        return null;
    }

    private static byte[]? ExchangeOnce(HidChannel ch, byte address, byte eventId, byte eventType, ReadOnlySpan<byte> payload, int timeoutMs)
    {
        for (int i = 0; i < 6; i++)
            if (ch.Read(5) == null) break;

        byte seq = NextSeq();
        byte[] req = BuildHciPacket(eventId, eventType, seq, payload, address);
        if (!ch.WriteOutput(req))
            return null;

        byte wantType = eventType == ETypeSet ? ETypeNtfy : ETypeRet;
        var dl = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < dl)
        {
            var r = ch.Read(100);
            if (r == null) continue;
            if (TryParseHciEventPayload(r, eventId, seq, wantType) is { } parsed)
                return parsed;
        }
        return null;
    }

    internal static byte[]? TryParseHciEventPayload(ReadOnlySpan<byte> r, byte expectedEventId, byte expectedSeq, byte expectedType)
    {
        if (r.Length < 14) return null;
        if (r[0] != 0x02 || r[2] != 0x04 || r[3] != 0xFF || r[6] != 0x96 || r[7] != 0xC3)
            return null;
        if (r[9] != expectedEventId || r[11] != expectedSeq)
            return null;
        if (r[10] != expectedType && r[10] != ETypeNtfyActive)
            return null;

        // 事件包头: r[2]=0x04, r[3]=0xFF, r[4]=(9 + payloadLen), r[5]=0x00, [6..12]=vendor header, r[13..]=payload
        if (r[4] < 9) return null;
        int payloadLen = Math.Clamp(r[4] - 9, 0, Math.Max(0, r.Length - 14));
        return r.Slice(13, payloadLen).ToArray();
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

    /// <summary>解析 INZONE Buds 主动推送（byte[1]=0x12, byte[2]=0x04）电池帧（openinzone/kinako19 验证：14=左, 16=右, 18=盒）。</summary>
    internal static (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? TryParsePassiveBudsReport(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 18) return null;
        if (data[0] != 0x02 || data[1] != 0x12 || data[2] != 0x04)
            return null;

        int? left = data[14] <= 100 ? data[14] : null;
        int? right = data[16] <= 100 ? data[16] : null;
        int? box = data[18] <= 100 ? data[18] : null;
        bool lChg = data[13] == 0x01;
        bool rChg = data[15] == 0x01;
        bool cChg = data[17] == 0x01;

        if (left == null && right == null && box == null)
            return null;
        return (left, lChg, right, rChg, box, cChg);
    }

    /// <summary>解析 AllFunctionSettingsPart1 (EID 0x06) 载荷中的入耳 6 字节电量槽：[modelId, stL, pctL, stR, pctR, stCase, pctCase, ...]。</summary>
    internal static (int? Left, bool LeftChg, int? Right, bool RightChg, int? Case, bool CaseChg)? TryParsePart1BudsBattery(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 13)
            return null;

        int? left = payload[2] <= 100 ? payload[2] : null;
        int? right = payload[4] <= 100 ? payload[4] : null;
        int? box = payload[6] <= 100 ? payload[6] : null;
        bool lChg = payload[1] == 0x01;
        bool rChg = payload[3] == 0x01;
        bool cChg = payload[5] == 0x01;

        if (left == null && right == null && box == null)
            return null;
        return (left, lChg, right, rChg, box, cChg);
    }

    private static (int? Level, bool Charging, bool Connected)? ProbePulse3D(List<HidDevice> devs)
    {
        var iface = devs.FirstOrDefault(d => HidSafe.PrimaryUsage(d) == (0xFF01, 0x0020))
            ?? devs.FirstOrDefault(d => HidSafe.PrimaryUsage(d).UsagePage == 0xFF01 && HidSafe.MaxInputLen(d) >= 8);
        if (iface == null) return null;

        using var ch = HidChannel.TryOpen(iface);
        if (ch == null) return null;

        (int? Level, bool Charging, bool Connected)? latest = null;
        for (int i = 0; i < 32; i++)
        {
            var r = ch.Read(35);
            if (r == null) break;
            if (ParsePulse3DReport(r) is { } parsed)
                latest = parsed;
        }

        lock (Pulse3DLock)
        {
            if (latest is { } hit)
            {
                if (!hit.Connected)
                    _lastPulse3D = hit;
                else
                    _lastPulse3D = (hit.Level ?? _lastPulse3D?.Level, hit.Charging, true);
                _lastPulse3DUtc = DateTimeOffset.UtcNow;
                return _lastPulse3D;
            }

            if (_lastPulse3D is { Connected: true } cached && (DateTimeOffset.UtcNow - _lastPulse3DUtc) < TimeSpan.FromHours(2))
                return cached;
        }
        return null;
    }

    /// <summary>解析 Sony PULSE 3D 被动 8 字节状态报告 (Report ID 0xB0, UsagePage 0xFF01 / Usage 0x0020)。</summary>
    internal static (int? Level, bool Charging, bool Connected)? ParsePulse3DReport(ReadOnlySpan<byte> report)
    {
        if (report.Length < 8 || report[0] != 0xB0)
            return null;
        bool connected = (report[4] & 0x08) != 0;
        byte rawLevel = report[3];
        if (!connected)
            return (null, false, false);
        if (rawLevel == 0x80)
            return (null, true, true);
        if (rawLevel <= 100)
            return (rawLevel, false, true);
        return (null, false, true);
    }
}
