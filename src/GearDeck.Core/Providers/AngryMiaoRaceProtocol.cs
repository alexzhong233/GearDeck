using System.Diagnostics;
using System.Text;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 怒喵 Angry Miao 协议实现：
/// 1) AM35 / AM30R2 家族（AM Infinity .97 0E8D:0703/0880、35A1:0035 与 AM Infinity .100R2 0E8D:0704/0881）：
///    基于 Airoha/MediaTek RACE 帧协议，通过 HID Feature Report 0x14（62B）或 Output 0x06 / Input 0x07 通讯，
///    2.4G 接收器通过本地桥接初始化（deviceId=0x00）后以 deviceId=0x80 访问远端无线鼠标，
///    支持电量/备用电池仓读取、3 组板载配置、50~30000 独立 X/Y 多档 DPI、125~8000 Hz 回报率、LOD、
///    按回报率缩放防抖、Motion Sync / 直线修正 / 波纹控制、FPS 竞技模式、传感器角度偏移、无线休眠与按键重映射。
/// 2) AM30 初代（0x3151:0x5007 / 0x402A）状态识别补充。
/// </summary>
public static class AngryMiaoRaceProtocol
{
    public const ushort VidMediaTek = 0x0E8D;
    public const ushort VidAm35Alt = 0x35A1;
    public const ushort VidSinowealth = 0x3151;

    public const ushort PidAm35Dongle = 0x0703;
    public const ushort PidAm35Wired = 0x0880;
    public const ushort PidAm35Alt = 0x0035;
    public const ushort PidAm30R2Dongle = 0x0704;
    public const ushort PidAm30R2Wired = 0x0881;

    public const ushort PidAm30Wired = 0x402A;
    public const ushort PidAm30Dongle = 0x5007;

    private const byte FeatureReportId14 = 0x14; // 20
    private const byte OutputReportId06 = 0x06;
    private const byte InputReportId07 = 0x07;
    private const int MinReportDataBytes = 61;
    private const int MaxRacePayloadPerPacket = 59;

    private const byte LocalDeviceId = 0x00;
    private const byte RemoteMouseDeviceId = 0x80;

    // ── RACE Command IDs (from ammaster.angrymiao.com) ──────────────────────
    private const ushort RaceGetFirmwareVersion = 7175;
    private const ushort RaceReadNvData = 2572;
    private const ushort RaceWriteNvData = 2561; // AM30R2 wired sleep NV
    private const ushort NvAm30R2PowerSaveId = 24576;

    private const ushort RaceGetDongleMode = 11395;
    private const ushort RaceGetDongleDeviceList = 11412;
    private const ushort RaceGetDonglePairedList = 11414;
    private const ushort RaceSetDongleLinkType = 11442;

    private const ushort RaceGetDongleBattery = 12303;
    private const ushort RaceSetReportRate = 12304;
    private const ushort RaceGetReportRate = 12305;
    private const ushort RaceSetSleepTime = 12306;
    private const ushort RaceGetSleepTime = 12307;

    private const ushort RaceSetKeyRemap = 12482;
    private const ushort RaceGetKeyRemap = 12483;
    private const ushort RaceSetDpi = 12484;
    private const ushort RaceGetDpi = 12485;
    private const ushort RaceSetDpiLoopRange = 12486;
    private const ushort RaceSetCurrentDpiStage = 12487;
    private const ushort RaceChangeProfile = 12488;
    private const ushort RaceGetProfile = 12489;
    private const ushort RaceSetLod = 12492;
    private const ushort RaceGetLod = 12493;
    private const ushort RaceGetBattery = 12495;
    private const ushort RaceSetMotionSync = 12498;
    private const ushort RaceGetMotionSync = 12499;
    private const ushort RaceSetAngleSnapping = 12500;
    private const ushort RaceGetAngleSnapping = 12501;
    private const ushort RaceSetRippleControl = 12502;
    private const ushort RaceGetRippleControl = 12503;
    private const ushort RaceSetRotateAngle = 12504;
    private const ushort RaceGetRotateAngle = 12505;
    private const ushort RaceSetFpsMode = 12510;
    private const ushort RaceGetFpsMode = 12511;
    private const ushort RaceSetSwDebounce = 12520;
    private const ushort RaceGetSwDebounce = 12521;

    private static readonly int[] DebounceDisplayMs = [30, 25, 20, 15, 10, 5, 0];
    private static readonly byte[] BtDebounceByGear = [10, 8, 6, 4, 3, 2, 0];

    private static readonly Dictionary<int, byte[]> WireAndRfDebounceByRate = new()
    {
        [125] = [10, 8, 6, 4, 3, 2, 0],
        [250] = [10, 8, 6, 4, 3, 2, 0],
        [500] = [10, 8, 6, 4, 3, 2, 0],
        [1000] = [10, 8, 6, 4, 3, 2, 0],
        [2000] = [20, 16, 12, 8, 4, 2, 0],
        [4000] = [39, 31, 23, 15, 7, 3, 0],
        [8000] = [79, 63, 47, 31, 15, 7, 0],
    };

    private static readonly int[] StandardRates = [125, 250, 500, 1000, 2000, 4000, 8000];
    private static readonly int[] StandardSleepSeconds = [15, 30, 60, 600, 1800, 3600, 0];

    private static readonly (string Action, byte Type, byte[] Params)[] SelectableButtonActions =
    [
        ("左键点击", 1, []),
        ("右键点击", 2, []),
        ("中键点击", 3, []),
        ("前进", 4, []),
        ("后退", 5, []),
        ("滚轮向上", 12, []),
        ("滚轮向下", 13, []),
        ("DPI 循环", 11, [3, 0, 0]),
        ("DPI +", 11, [0, 0, 0]),
        ("DPI -", 11, [1, 0, 0]),
        ("回报率切换", 7, []),
        ("禁用按键", 0, []),
    ];

    public sealed record RaceFrame(byte Header, byte Type, ushort RaceId, byte[] Payload);

    public sealed record DongleDeviceEntry(byte[] Address, byte DeviceId, byte GroupId, byte Role);

    public sealed record RaceBatteryInfo(bool Charging, bool Full, int Percent, bool Exists);

    public sealed record RaceDpiInfo(int CurrentIndex, bool SyncXY, List<(int X, int Y)> Stages);

    public sealed record ProbeStatus(
        bool IsAwake,
        int? MouseBatteryPercent,
        bool MouseCharging,
        int? DockBatteryPercent,
        bool DockCharging,
        bool IsDongle,
        string? Diagnostics);

    public static bool IsRaceVidPid(ushort vid, ushort pid)
        => (vid == VidMediaTek && pid is PidAm35Dongle or PidAm35Wired or PidAm30R2Dongle or PidAm30R2Wired)
        || (vid == VidAm35Alt && pid == PidAm35Alt);

    public static bool IsDongleVidPid(ushort vid, ushort pid)
        => (vid == VidMediaTek && pid is PidAm35Dongle or PidAm30R2Dongle)
        || (vid == VidSinowealth && pid == PidAm30Dongle);

    public static bool IsAm30R2VidPid(ushort vid, ushort pid)
        => vid == VidMediaTek && pid is PidAm30R2Dongle or PidAm30R2Wired;

    public static bool IsKnownAngryMiaoVidPid(ushort vid, ushort pid)
        => IsRaceVidPid(vid, pid)
        || (vid == VidSinowealth && pid is PidAm30Dongle or PidAm30Wired or 0x3010);

    public static string ResolveModelName(ushort vid, ushort pid, string? productName)
    {
        if (vid == VidMediaTek && pid is PidAm30R2Dongle or PidAm30R2Wired)
            return "Angry Miao AM INFINITY .100R2";
        if (IsRaceVidPid(vid, pid))
            return "Angry Miao AM Infinity .97";
        if (!string.IsNullOrWhiteSpace(productName) &&
            !productName.Contains("Receiver", StringComparison.OrdinalIgnoreCase) &&
            !productName.Contains("Dongle", StringComparison.OrdinalIgnoreCase))
            return productName.Trim();
        return $"Angry Miao {vid:X4}:{pid:X4}";
    }

    // ── Packet Builders & Parsers (Pure & Unit-Testable) ─────────────────────

    public static byte[] BuildRaceCommand(ushort raceId, ReadOnlySpan<byte> args = default)
    {
        int len = args.Length + 2;
        var bytes = new byte[6 + args.Length];
        bytes[0] = 0x05;
        bytes[1] = 0x5A;
        bytes[2] = (byte)(len & 0xFF);
        bytes[3] = (byte)((len >> 8) & 0xFF);
        bytes[4] = (byte)(raceId & 0xFF);
        bytes[5] = (byte)((raceId >> 8) & 0xFF);
        if (!args.IsEmpty)
            args.CopyTo(bytes.AsSpan(6));
        return bytes;
    }

    public static byte[] BuildFeature14Report(byte deviceId, ReadOnlySpan<byte> raceChunk, int reportLen = 62)
    {
        int bufLen = Math.Max(MinReportDataBytes + 1, reportLen);
        int chunkLen = Math.Min(raceChunk.Length, MaxRacePayloadPerPacket);
        var buf = new byte[bufLen];
        buf[0] = FeatureReportId14;
        buf[1] = (byte)chunkLen;
        buf[2] = deviceId;
        if (chunkLen > 0)
            raceChunk[..chunkLen].CopyTo(buf.AsSpan(3));
        return buf;
    }

    public static byte[] BuildOutput06Report(byte deviceId, ReadOnlySpan<byte> raceChunk, int reportLen = 62)
    {
        int bufLen = Math.Max(MinReportDataBytes + 1, reportLen);
        int chunkLen = Math.Min(raceChunk.Length, MaxRacePayloadPerPacket);
        var buf = new byte[bufLen];
        buf[0] = OutputReportId06;
        buf[1] = (byte)chunkLen;
        buf[2] = deviceId;
        if (chunkLen > 0)
            raceChunk[..chunkLen].CopyTo(buf.AsSpan(3));
        return buf;
    }

    public static List<RaceFrame> ParseFeature14Frames(ReadOnlySpan<byte> rawReport)
    {
        var frames = new List<RaceFrame>();
        if (rawReport.Length < 4) return frames;

        bool hasReportPrefix = rawReport[0] == FeatureReportId14 &&
            (rawReport.Length >= 62 ||
             (rawReport.Length >= 9 && rawReport[1] is > 0 and <= MaxRacePayloadPerPacket && rawReport[3] is 0x05 or 0x15));
        var data = hasReportPrefix ? rawReport[1..] : rawReport;

        bool allZero = true;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != 0) { allZero = false; break; }
        }
        if (allZero || data.Length < 2) return frames;

        int payloadLen = data[0];
        if (payloadLen < 6) return frames;
        int available = Math.Min(payloadLen, data.Length - 2);
        var payload = data.Slice(2, available);

        int offset = 0;
        while (offset + 6 <= payload.Length)
        {
            byte header = payload[offset];
            if (header != 0x05 && header != 0x15) break;
            byte type = payload[offset + 1];
            int fieldLen = payload[offset + 2] | (payload[offset + 3] << 8);
            int frameLen = fieldLen + 4;
            if (fieldLen < 2 || offset + frameLen > payload.Length) break;

            ushort rId = (ushort)(payload[offset + 4] | (payload[offset + 5] << 8));
            byte[] rPayload = payload.Slice(offset + 6, frameLen - 6).ToArray();

            bool isFwVersionAckOnly = rId == RaceGetFirmwareVersion && rPayload.Length == 1 && rPayload[0] == 0;
            if (!isFwVersionAckOnly)
                frames.Add(new RaceFrame(header, type, rId, rPayload));

            offset += frameLen;
        }
        return frames;
    }

    public static List<DongleDeviceEntry> ParseDongleDeviceList(ReadOnlySpan<byte> payload)
    {
        var list = new List<DongleDeviceEntry>();
        if (payload.Length < 1 || payload[0] != 0) return list;
        for (int i = 1; i + 9 < payload.Length; i += 10)
        {
            list.Add(new DongleDeviceEntry(
                payload.Slice(i, 7).ToArray(),
                payload[i + 7],
                payload[i + 8],
                payload[i + 9]));
        }
        return list;
    }

    public static RaceBatteryInfo? ParseBatteryPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3 || payload[0] != 0) return null;

        int percent;
        if (payload.Length >= 7 && payload[2] <= 100)
            percent = payload[2];
        else
        {
            int raw = payload[1] | (payload[2] << 8);
            percent = raw is >= 0 and <= 100 ? raw : 0;
        }

        byte statusByte = payload[1];
        bool exists = payload.Length < 5 || payload[4] != 0;
        bool full = statusByte == 2 && percent >= 100;
        bool charging = statusByte != 0 && !full;
        return new RaceBatteryInfo(charging, full, Math.Clamp(percent, 0, 100), exists);
    }

    public static RaceDpiInfo? ParseDpiPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 7 || payload[0] != 0) return null;
        int count = payload[1];
        int active = payload[2];
        if (count is < 1 or > 8) return null;

        const int headerOffset = 3;
        const int maxSlots = 8;
        int stride = payload.Length >= headerOffset + maxSlots * 4 ? maxSlots : count;
        int yOffset = headerOffset + stride * 2;
        if (payload.Length < yOffset + count * 2) return null;

        var stages = new List<(int X, int Y)>(count);
        for (int i = 0; i < count; i++)
        {
            int x = payload[headerOffset + i * 2] | (payload[headerOffset + i * 2 + 1] << 8);
            int y = payload[yOffset + i * 2] | (payload[yOffset + i * 2 + 1] << 8);
            if (x is < 50 or > 30000) x = Math.Clamp(x == 0 ? 800 : x, 50, 30000);
            if (y is < 50 or > 30000) y = x;
            stages.Add((x, y));
        }

        bool sync = stages.All(s => s.X == s.Y);
        return new RaceDpiInfo(Math.Clamp(active, 0, stages.Count - 1), sync, stages);
    }

    public static int DecodeDebounceMs(int reportRateHz, byte wireDebounce)
    {
        if (!WireAndRfDebounceByRate.TryGetValue(reportRateHz, out var table))
            table = WireAndRfDebounceByRate[1000];

        int bestIdx = 6; // 0 ms default
        int bestDiff = int.MaxValue;
        for (int i = 0; i < table.Length; i++)
        {
            int diff = Math.Abs(table[i] - wireDebounce);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIdx = i;
            }
        }
        return DebounceDisplayMs[bestIdx];
    }

    public static (byte Wire, byte Rf24g, byte Bt) EncodeDebounceBytes(int reportRateHz, int debounceMs)
    {
        int clamped = Math.Clamp(debounceMs, 0, 30);
        int bestIdx = 6;
        int bestDiff = int.MaxValue;
        for (int i = 0; i < DebounceDisplayMs.Length; i++)
        {
            int diff = Math.Abs(DebounceDisplayMs[i] - clamped);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIdx = i;
            }
        }
        if (!WireAndRfDebounceByRate.TryGetValue(reportRateHz, out var table))
            table = WireAndRfDebounceByRate[1000];
        byte v = table[bestIdx];
        byte bt = BtDebounceByGear[bestIdx];
        return (v, v, bt);
    }

    public static int DecodeSleepSeconds24G(ReadOnlySpan<byte> payload, bool responseHasStatus)
    {
        int offset = responseHasStatus || (payload.Length >= 15 && payload[0] == 0) ? 1 : 0;
        if (payload.Length < offset + 6) return 30;

        int s0 = payload[offset] | (payload[offset + 1] << 8);
        int s1 = payload[offset + 2] | (payload[offset + 3] << 8);
        int s2 = payload[offset + 4] | (payload[offset + 5] << 8);
        int total = s0 + s1 + s2;
        return total >= 65535 ? 0 : Math.Max(0, total);
    }

    public static byte[] BuildSleepTimeArgs(int sleep24GSeconds, int sleepBtSeconds)
    {
        static void WriteThreeWay(Span<byte> dst, int seconds)
        {
            int wireTotal = seconds <= 0 ? 65535 : Math.Clamp(seconds, 15, 65535);
            int third = wireTotal / 3;
            int rem = wireTotal - third * 2;
            WriteLe16(dst, 0, third);
            WriteLe16(dst, 2, third);
            WriteLe16(dst, 4, rem);
            dst[6] = 0;
            dst[7] = 0;
        }

        var args = new byte[24];
        WriteThreeWay(args.AsSpan(0, 8), sleep24GSeconds);
        WriteThreeWay(args.AsSpan(8, 8), sleepBtSeconds);
        args[16] = 60; args[17] = 0;
        args[18] = 30; args[19] = 0;
        args[20] = 60; args[21] = 0;
        args[22] = 30; args[23] = 0;
        return args;
    }

    // ── Transport Session over HID Feature 0x14 (and Out 0x06 fallback) ──────

    private sealed class RaceHidSession : IDisposable
    {
        private readonly HidStream? _stream;
        private readonly RawHidFeature? _rawFeature;
        private readonly int _featureLen;
        private readonly int _outputLen;
        private readonly bool _preferOutputWrite;
        private readonly bool _hasFeature14;
        private bool _firstCmdSent;

        private RaceHidSession(
            HidStream? stream,
            RawHidFeature? rawFeature,
            int featureLen,
            int outputLen,
            bool hasFeature14,
            bool preferOutputWrite)
        {
            _stream = stream;
            _rawFeature = rawFeature;
            _featureLen = featureLen;
            _outputLen = outputLen;
            _hasFeature14 = hasFeature14;
            _preferOutputWrite = preferOutputWrite;
        }

        public static RaceHidSession? Open(IEnumerable<HidDevice> group, ushort vid, ushort pid)
        {
            bool isR2 = IsAm30R2VidPid(vid, pid);
            var candidates = group
                .Select(d => new
                {
                    Device = d,
                    FeatLen = HidSafe.MaxFeatureLen(d),
                    OutLen = HidSafe.MaxOutputLen(d),
                    InLen = HidSafe.MaxInputLen(d),
                    Ids = HidReportInspector.GetReportIds(d),
                })
                .Where(x => x.FeatLen >= 62 || (x.OutLen >= 62 && x.InLen >= 62))
                .OrderByDescending(x => x.Ids.FeatureIds.Contains(FeatureReportId14) ? 1 : 0)
                .ThenBy(x => x.FeatLen == 62 ? 0 : 1)
                .ThenByDescending(x => x.FeatLen)
                .ToList();

            foreach (var c in candidates)
            {
                bool hasFeat14 = c.FeatLen >= 62 &&
                                 (c.Ids.FeatureIds.Contains(FeatureReportId14) || c.FeatLen == 62);
                bool hasOut06 = c.OutLen >= 62 && c.Ids.OutputIds.Contains(OutputReportId06);

                if (c.Device.TryOpen(out var stream) && stream != null)
                {
                    stream.ReadTimeout = 100;
                    stream.WriteTimeout = 300;
                    return new RaceHidSession(
                        stream,
                        null,
                        c.FeatLen,
                        c.OutLen,
                        hasFeat14,
                        isR2 && hasOut06 && hasFeat14);
                }

                if (hasFeat14)
                {
                    var raw = RawHidFeature.TryOpen(c.Device.DevicePath);
                    if (raw != null)
                        return new RaceHidSession(null, raw, c.FeatLen, c.OutLen, true, false);
                }
            }
            return null;
        }

        public RaceFrame? SendCommand(
            byte deviceId,
            ushort raceId,
            ReadOnlySpan<byte> args,
            int timeoutMs = 600,
            bool expectResponse = true,
            bool requireStatusZero = true)
        {
            int attempts = !_firstCmdSent ? 2 : 1;
            _firstCmdSent = true;
            var argsCopy = args.ToArray();

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                var res = SendOnce(deviceId, raceId, argsCopy, timeoutMs, expectResponse);
                if (!expectResponse) return res.Sent ? new RaceFrame(0x05, 0x5B, raceId, []) : null;
                if (res.Frame != null)
                {
                    if (requireStatusZero && (res.Frame.Payload.Length == 0 || res.Frame.Payload[0] != 0))
                        return null;
                    return res.Frame;
                }
                if (attempt + 1 < attempts)
                    Thread.Sleep(25);
            }
            return null;
        }

        private (bool Sent, RaceFrame? Frame) SendOnce(
            byte deviceId,
            ushort raceId,
            byte[] args,
            int timeoutMs,
            bool expectResponse)
        {
            byte[] raceBytes = BuildRaceCommand(raceId, args);
            if (!WriteRacePackets(deviceId, raceBytes))
                return (false, null);

            if (!expectResponse)
                return (true, null);

            if (_hasFeature14)
                return (true, ReadFeature14Response(raceId, timeoutMs));

            return (true, ReadInput07Response(raceId, timeoutMs));
        }

        private bool WriteRacePackets(byte deviceId, byte[] raceBytes)
        {
            for (int off = 0; off < Math.Max(1, raceBytes.Length); off += MaxRacePayloadPerPacket)
            {
                int count = Math.Min(MaxRacePayloadPerPacket, raceBytes.Length - off);
                var chunk = raceBytes.AsSpan(off, count);

                bool written = false;
                if (_preferOutputWrite && _stream != null && _outputLen >= 62)
                {
                    try
                    {
                        _stream.Write(BuildOutput06Report(deviceId, chunk, _outputLen));
                        written = true;
                    }
                    catch { }
                }

                if (!written && _hasFeature14)
                {
                    var rep = BuildFeature14Report(deviceId, chunk, _featureLen);
                    if (_stream != null)
                    {
                        try { _stream.SetFeature(rep); written = true; }
                        catch { }
                    }
                    else if (_rawFeature != null)
                    {
                        written = _rawFeature.SetFeature(rep);
                    }
                }

                if (!written && _stream != null && _outputLen >= 62)
                {
                    try
                    {
                        _stream.Write(BuildOutput06Report(deviceId, chunk, _outputLen));
                        written = true;
                    }
                    catch { }
                }

                if (!written) return false;
                if (off + count < raceBytes.Length) Thread.Sleep(2);
            }
            return true;
        }

        private RaceFrame? ReadFeature14Response(ushort expectedRaceId, int timeoutMs)
        {
            int len = Math.Max(62, _featureLen);
            var rx = new byte[len];
            var accum = new List<byte>();
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(20);
                Array.Clear(rx, 0, rx.Length);
                rx[0] = FeatureReportId14;

                bool ok = false;
                if (_stream != null)
                {
                    try { _stream.GetFeature(rx); ok = true; }
                    catch { }
                }
                else if (_rawFeature != null)
                {
                    var got = _rawFeature.GetFeature(len, FeatureReportId14);
                    if (got != null)
                    {
                        got.CopyTo(rx, 0);
                        ok = true;
                    }
                }
                if (!ok) continue;

                var frames = ParseFeature14Frames(rx);
                foreach (var f in frames)
                {
                    if (f.RaceId == expectedRaceId)
                        return f;
                }

                // Multi-report RACE frame reassembly fallback
                var span = rx.Length >= 62 && rx[0] == FeatureReportId14 ? rx.AsSpan(1) : rx.AsSpan();
                int pLen = span.Length >= 2 ? span[0] : 0;
                if (pLen > 0 && pLen + 2 <= span.Length)
                {
                    var slice = span.Slice(2, pLen);
                    if (accum.Count == 0 && slice[0] != 0x05 && slice[0] != 0x15)
                        accum.Clear();
                    else
                    {
                        accum.AddRange(slice.ToArray());
                        if (accum.Count >= 6)
                        {
                            int expectedLen = (accum[2] | (accum[3] << 8)) + 4;
                            if (accum.Count >= expectedLen && expectedLen >= 6)
                            {
                                ushort rId = (ushort)(accum[4] | (accum[5] << 8));
                                byte[] rPay = accum.GetRange(6, expectedLen - 6).ToArray();
                                byte hdr = accum[0];
                                byte tp = accum[1];
                                accum.Clear();
                                if (rId == expectedRaceId && !(rId == RaceGetFirmwareVersion && rPay.Length == 1 && rPay[0] == 0))
                                    return new RaceFrame(hdr, tp, rId, rPay);
                            }
                        }
                    }
                }
            }
            return null;
        }

        private RaceFrame? ReadInput07Response(ushort expectedRaceId, int timeoutMs)
        {
            if (_stream == null) return null;
            var sw = Stopwatch.StartNew();
            var buf = new byte[Math.Max(64, _featureLen)];
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    _stream.ReadTimeout = Math.Max(40, Math.Min(150, timeoutMs - (int)sw.ElapsedMilliseconds));
                    int n = _stream.Read(buf, 0, buf.Length);
                    if (n < 6) continue;
                    var span = buf.AsSpan(0, n);
                    int start = span[0] == InputReportId07 ? 1 : 0;
                    var inner = span[start..];
                    if (inner.Length >= 4)
                    {
                        // Check 2-byte length header ([lenLo, lenHi, ...]) OR 1-byte length + devId
                        foreach (var f in ParseFeature14Frames(span))
                            if (f.RaceId == expectedRaceId) return f;

                        int pLen16 = inner[0] | (inner[1] << 8);
                        if (pLen16 >= 6 && pLen16 + 2 <= inner.Length && (inner[2] == 0x05 || inner[2] == 0x15))
                        {
                            ushort rId = (ushort)(inner[6] | (inner[7] << 8));
                            if (rId == expectedRaceId)
                                return new RaceFrame(inner[2], inner[3], rId, inner.Slice(8, pLen16 - 6).ToArray());
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _rawFeature?.Dispose();
        }
    }

    // ── Public Entry Points for AngryMiaoProvider ─────────────────────────────

    public static ProbeStatus? ProbeStatusAsyncSafe(IEnumerable<HidDevice> group, ushort vid, ushort pid)
    {
        if (!IsRaceVidPid(vid, pid))
            return null;

        bool isDongle = IsDongleVidPid(vid, pid);
        using var session = RaceHidSession.Open(group, vid, pid);
        if (session == null)
            return null;

        if (!isDongle)
        {
            var batFrame = session.SendCommand(LocalDeviceId, RaceGetBattery, [], timeoutMs: 350, requireStatusZero: false);
            var bat = batFrame != null ? ParseBatteryPayload(batFrame.Payload) : null;
            if (bat != null)
                return new ProbeStatus(true, bat.Percent, bat.Charging, null, false, false, null);

            var prof = session.SendCommand(LocalDeviceId, RaceGetProfile, [], timeoutMs: 280);
            if (prof != null)
                return new ProbeStatus(true, null, false, null, false, false, null);

            return new ProbeStatus(false, null, false, null, false, false, null);
        }

        // 2.4G Dongle: query dock battery + connected devices list quickly
        var dockFrame = session.SendCommand(LocalDeviceId, RaceGetDongleBattery, [], timeoutMs: 240, requireStatusZero: false);
        var dockBat = dockFrame != null ? ParseBatteryPayload(dockFrame.Payload) : null;
        int? dockPct = dockBat is { Exists: true } ? dockBat.Percent : null;
        bool dockChg = dockBat is { Exists: true, Charging: true };

        byte targetRemoteId = InitializeDongleAndPickTarget(session, quick: true, out bool hasActiveLink, out byte? altRemoteId);

        // If dongle reports no active wireless device, try a short 180ms ping on 0x80 anyway in case firmware omits 11412
        int batTimeout = hasActiveLink ? 450 : 180;
        var mouseBatFrame = session.SendCommand(targetRemoteId, RaceGetBattery, [], timeoutMs: batTimeout, requireStatusZero: false);
        var mouseBat = mouseBatFrame != null ? ParseBatteryPayload(mouseBatFrame.Payload) : null;
        if (mouseBat != null)
            return new ProbeStatus(true, mouseBat.Percent, mouseBat.Charging, dockPct, dockChg, true, null);

        if (altRemoteId is { } alt)
        {
            var altFrame = session.SendCommand(alt, RaceGetBattery, [], timeoutMs: 200, requireStatusZero: false);
            var altBat = altFrame != null ? ParseBatteryPayload(altFrame.Payload) : null;
            if (altBat != null)
                return new ProbeStatus(true, altBat.Percent, altBat.Charging, dockPct, dockChg, true, null);
        }

        return new ProbeStatus(false, null, false, dockPct, dockChg, true, null);
    }

    public static DeviceTuningCapabilities QueryCapabilities(string deviceId, IReadOnlyList<HidDevice> devs, ushort vid, ushort pid)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        bool isDongle = IsDongleVidPid(vid, pid);
        bool isR2 = IsAm30R2VidPid(vid, pid);

        using var session = RaceHidSession.Open(devs, vid, pid);
        if (session == null)
        {
            caps.StatusMessage = "未找到 AM35 RACE 通讯接口（Feature 0x14），请确认接收器或数据线已插牢。";
            return caps;
        }

        return ReadCapabilitiesOnSession(session, deviceId, isDongle, isR2);
    }

    public static DeviceTuningResult ApplySettings(
        string deviceId,
        IReadOnlyList<HidDevice> devs,
        ushort vid,
        ushort pid,
        DeviceTuningUpdate update)
    {
        bool isDongle = IsDongleVidPid(vid, pid);
        bool isR2 = IsAm30R2VidPid(vid, pid);

        using var session = RaceHidSession.Open(devs, vid, pid);
        if (session == null)
            return new DeviceTuningResult(false, "未找到 AM35 RACE 通讯接口（Feature 0x14）。");

        byte targetId = ResolveOnlineTargetId(session, isDongle, out _);
        if (targetId == 0xFF)
        {
            var offlineCaps = BuildSleepingCapabilities(deviceId, isDongle, null);
            return new DeviceTuningResult(false, offlineCaps.StatusMessage!, offlineCaps);
        }

        // Read current report rate if needed for debounce scaling or sleep preservation
        int currentRate = 1000;
        if (update.DebounceMs.HasValue || update.PollingRateHz.HasValue)
        {
            var rFrame = session.SendCommand(targetId, RaceGetReportRate, [], timeoutMs: 400);
            if (rFrame != null && rFrame.Payload.Length >= 3)
                currentRate = rFrame.Payload[1] | (rFrame.Payload[2] << 8);
        }

        if (update.ActiveOnboardProfile is { } profSlot)
        {
            byte p = (byte)Math.Clamp(profSlot, 0, 2);
            session.SendCommand(targetId, RaceChangeProfile, [p], timeoutMs: 600, requireStatusZero: false);
            Thread.Sleep(40);
        }

        if (update.DpiStages is { Count: > 0 } newStages)
        {
            int count = Math.Clamp(newStages.Count, 1, 8);
            session.SendCommand(targetId, RaceSetDpiLoopRange, [(byte)count], timeoutMs: 500);
            for (int i = 0; i < count; i++)
            {
                var (sx, sy) = newStages[i];
                int x = Math.Clamp((sx / 50) * 50, 50, 30000);
                int y = Math.Clamp((sy / 50) * 50, 50, 30000);
                byte[] args = [(byte)i, (byte)(x & 0xFF), (byte)(x >> 8), (byte)(y & 0xFF), (byte)(y >> 8)];
                session.SendCommand(targetId, RaceSetDpi, args, timeoutMs: 500);
            }
            if (update.ActiveDpiStage is { } act)
                session.SendCommand(targetId, RaceSetCurrentDpiStage, [(byte)Math.Clamp(act, 0, count - 1)], timeoutMs: 500);
        }
        else if (update.ActiveDpiStage is { } actOnly)
        {
            session.SendCommand(targetId, RaceSetCurrentDpiStage, [(byte)Math.Clamp(actOnly, 0, 7)], timeoutMs: 500);
        }
        else if (update.Dpi is { } singleDpi)
        {
            var cur = session.SendCommand(targetId, RaceGetDpi, [], timeoutMs: 400);
            var parsed = cur != null ? ParseDpiPayload(cur.Payload) : null;
            int idx = parsed?.CurrentIndex ?? 0;
            int x = Math.Clamp((singleDpi.X / 50) * 50, 50, 30000);
            int y = Math.Clamp((singleDpi.Y / 50) * 50, 50, 30000);
            byte[] args = [(byte)idx, (byte)(x & 0xFF), (byte)(x >> 8), (byte)(y & 0xFF), (byte)(y >> 8)];
            session.SendCommand(targetId, RaceSetDpi, args, timeoutMs: 500);
        }

        if (update.PollingRateHz is { } rate)
        {
            int clampedRate = StandardRates.Contains(rate) ? rate : 1000;
            byte[] args = [(byte)(clampedRate & 0xFF), (byte)(clampedRate >> 8)];
            session.SendCommand(targetId, RaceSetReportRate, args, timeoutMs: 500);

            // Preserve current debounce ms across new polling rate clock divider
            if (!update.DebounceMs.HasValue)
            {
                var dbFrame = session.SendCommand(targetId, RaceGetSwDebounce, [], timeoutMs: 400);
                if (dbFrame != null && dbFrame.Payload.Length >= 2)
                {
                    int curDbMs = DecodeDebounceMs(currentRate, dbFrame.Payload[1]);
                    var (w, rf, bt) = EncodeDebounceBytes(clampedRate, curDbMs);
                    session.SendCommand(targetId, RaceSetSwDebounce, [w, rf, bt], timeoutMs: 400);
                }
            }
            currentRate = clampedRate;
        }

        if (update.PerformanceMode is { } perfMode)
        {
            session.SendCommand(targetId, RaceSetFpsMode, [(byte)(perfMode != 0 ? 1 : 0)], timeoutMs: 500, requireStatusZero: false);
        }

        if (update.LodCode is { } lod)
        {
            int maxLod = isR2 ? 4 : 2;
            session.SendCommand(targetId, RaceSetLod, [(byte)Math.Clamp(lod, 0, maxLod)], timeoutMs: 500);
        }

        if (update.MotionSync is { } ms)
            session.SendCommand(targetId, RaceSetMotionSync, [(byte)(ms ? 1 : 0)], timeoutMs: 500);

        if (update.AngleSnapping is { } angleSnap)
            session.SendCommand(targetId, RaceSetAngleSnapping, [(byte)(angleSnap ? 1 : 0)], timeoutMs: 500);

        if (update.RippleControl is { } ripple)
            session.SendCommand(targetId, RaceSetRippleControl, [(byte)(ripple ? 1 : 0)], timeoutMs: 500);

        if (update.DebounceMs is { } dbMs)
        {
            var (w, rf, bt) = EncodeDebounceBytes(currentRate, dbMs);
            session.SendCommand(targetId, RaceSetSwDebounce, [w, rf, bt], timeoutMs: 500);
        }

        if (update.AngleTuningDeg is { } angDeg)
        {
            int clamped = Math.Clamp(angDeg, -90, 90);
            byte en = (byte)(clamped != 0 ? 1 : 0);
            byte wireAng = clamped < 0 ? (byte)(clamped + 256) : (byte)clamped;
            session.SendCommand(targetId, RaceSetRotateAngle, [en, wireAng], timeoutMs: 500, requireStatusZero: false);
        }

        if (update.SleepSeconds is { } sleep24G)
        {
            if (isR2 && !isDongle)
            {
                var nvRead = session.SendCommand(targetId, RaceReadNvData, [(byte)(NvAm30R2PowerSaveId & 0xFF), (byte)(NvAm30R2PowerSaveId >> 8), 0xE8, 0x03], timeoutMs: 400);
                if (nvRead != null && nvRead.Payload.Length >= 21)
                {
                    int nLen = nvRead.Payload[3] | (nvRead.Payload[4] << 8);
                    var nv = nvRead.Payload.AsSpan(5, Math.Min(nLen, nvRead.Payload.Length - 5)).ToArray();
                    if (nv.Length >= 16)
                    {
                        var patched = BuildSleepTimeArgs(sleep24G <= 0 ? -1 : sleep24G, 600);
                        Array.Copy(patched, 0, nv, 0, 8);
                        var writeArgs = new byte[2 + nv.Length];
                        writeArgs[0] = (byte)(NvAm30R2PowerSaveId & 0xFF);
                        writeArgs[1] = (byte)(NvAm30R2PowerSaveId >> 8);
                        nv.CopyTo(writeArgs, 2);
                        session.SendCommand(targetId, RaceWriteNvData, writeArgs, timeoutMs: 500);
                    }
                }
            }
            else
            {
                int btSleep = 600;
                var curSleep = session.SendCommand(targetId, RaceGetSleepTime, [], timeoutMs: 400, requireStatusZero: false);
                if (curSleep != null)
                {
                    int off = curSleep.Payload.Length >= 15 && curSleep.Payload[0] == 0 ? 1 : 0;
                    if (curSleep.Payload.Length >= off + 14)
                    {
                        int b0 = curSleep.Payload[off + 8] | (curSleep.Payload[off + 9] << 8);
                        int b1 = curSleep.Payload[off + 10] | (curSleep.Payload[off + 11] << 8);
                        int b2 = curSleep.Payload[off + 12] | (curSleep.Payload[off + 13] << 8);
                        btSleep = (b0 + b1 + b2) >= 65535 ? -1 : Math.Max(15, b0 + b1 + b2);
                    }
                }
                var args = BuildSleepTimeArgs(sleep24G <= 0 ? -1 : sleep24G, btSleep);
                session.SendCommand(targetId, RaceSetSleepTime, args, timeoutMs: 500, requireStatusZero: false);
            }
        }

        if (update.ButtonBinding is var (slotIdx, actionStr))
        {
            byte keyId = (byte)Math.Clamp(slotIdx, 1, isR2 ? 8 : 7);
            if (!TryEncodeButtonAction(actionStr, isR2, out byte type, out byte[] actionParams))
                return new DeviceTuningResult(false, $"不支持的怒喵按键动作：{actionStr}");

            // Guard: preserve at least one Left Click
            if (type != 1)
            {
                int maxKeys = isR2 ? 8 : 7;
                int leftCount = 0;
                for (byte k = 1; k <= maxKeys; k++)
                {
                    var kf = session.SendCommand(targetId, RaceGetKeyRemap, [k], timeoutMs: 350);
                    if (kf != null && kf.Payload.Length >= 3 && kf.Payload[2] == 1 && k != keyId)
                        leftCount++;
                }
                if (leftCount == 0)
                    return new DeviceTuningResult(false, "为了防止鼠标无法点击，至少需要保留一个按键作为「左键点击」。");
            }

            var args = new byte[2 + actionParams.Length];
            args[0] = keyId;
            args[1] = type;
            actionParams.CopyTo(args, 2);
            session.SendCommand(targetId, RaceSetKeyRemap, args, timeoutMs: 500);
        }

        var refreshed = ReadCapabilitiesOnSession(session, deviceId, isDongle, isR2);
        return new DeviceTuningResult(true, "怒喵硬件参数已实时写入生效。", refreshed);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static byte InitializeDongleAndPickTarget(RaceHidSession session, bool quick, out bool hasActiveDevice, out byte? altDeviceId)
    {
        hasActiveDevice = false;
        altDeviceId = null;
        session.SendCommand(LocalDeviceId, RaceGetDongleMode, [18, 0], timeoutMs: quick ? 220 : 400, requireStatusZero: false);
        session.SendCommand(LocalDeviceId, RaceSetDongleLinkType, [6], timeoutMs: quick ? 220 : 400, requireStatusZero: false);

        var devListFrame = session.SendCommand(LocalDeviceId, RaceGetDongleDeviceList, [], timeoutMs: quick ? 220 : 400, requireStatusZero: false);
        var active = devListFrame != null ? ParseDongleDeviceList(devListFrame.Payload) : [];
        hasActiveDevice = active.Count > 0;
        altDeviceId = active.FirstOrDefault(e => e.DeviceId >= 0x80 && e.DeviceId != RemoteMouseDeviceId)?.DeviceId;

        if (!hasActiveDevice && !quick)
        {
            var pairedFrame = session.SendCommand(LocalDeviceId, RaceGetDonglePairedList, [], timeoutMs: 400, requireStatusZero: false);
            var paired = pairedFrame != null ? ParseDongleDeviceList(pairedFrame.Payload) : [];
            altDeviceId ??= paired.FirstOrDefault(e => e.DeviceId >= 0x80 && e.DeviceId != RemoteMouseDeviceId)?.DeviceId;
        }

        return RemoteMouseDeviceId;
    }

    private static byte ResolveOnlineTargetId(RaceHidSession session, bool isDongle, out int? dockBatteryPercent)
    {
        dockBatteryPercent = null;
        if (!isDongle)
        {
            var prof = session.SendCommand(LocalDeviceId, RaceGetProfile, [], timeoutMs: 450);
            return prof != null ? LocalDeviceId : (byte)0xFF;
        }

        var dockFrame = session.SendCommand(LocalDeviceId, RaceGetDongleBattery, [], timeoutMs: 280, requireStatusZero: false);
        var dockBat = dockFrame != null ? ParseBatteryPayload(dockFrame.Payload) : null;
        if (dockBat is { Exists: true })
            dockBatteryPercent = dockBat.Percent;

        InitializeDongleAndPickTarget(session, quick: false, out bool hasActive, out byte? altId);

        if (session.SendCommand(RemoteMouseDeviceId, RaceGetProfile, [], timeoutMs: 650) != null)
            return RemoteMouseDeviceId;

        if (altId is { } alt && session.SendCommand(alt, RaceGetProfile, [], timeoutMs: 450) != null)
            return alt;

        return hasActive && session.SendCommand(RemoteMouseDeviceId, RaceGetProfile, [], timeoutMs: 450) != null
            ? RemoteMouseDeviceId
            : (byte)0xFF;
    }

    private static DeviceTuningCapabilities BuildSleepingCapabilities(string deviceId, bool isDongle, int? dockPercent)
    {
        string dockHint = dockPercent is { } dp ? $"（接收器备用电池仓 {dp}%）" : "";
        return new DeviceTuningCapabilities
        {
            DeviceId = deviceId,
            IsOnline = false,
            StatusMessage = isDongle
                ? $"怒喵鼠标当前处于无线休眠状态{dockHint}，请晃动或按动鼠标唤醒后点击右上角「重新读取设备参数」。"
                : "怒喵设备未响应读取命令，请确认鼠标已开机连接后重试。",
        };
    }

    private static DeviceTuningCapabilities ReadCapabilitiesOnSession(
        RaceHidSession session,
        string deviceId,
        bool isDongle,
        bool isR2)
    {
        byte targetId = ResolveOnlineTargetId(session, isDongle, out int? dockPct);
        if (targetId == 0xFF)
            return BuildSleepingCapabilities(deviceId, isDongle, dockPct);

        var caps = new DeviceTuningCapabilities
        {
            DeviceId = deviceId,
            IsOnline = true,
            SupportsHostModeSwitch = false,
            IsHostMode = false,
            OnboardProfileCount = 3,
            OnboardProfileNames = ["日常", "游戏", "猛攻"],
            SupportsDpi = true,
            SupportsIndependentXY = true,
            SupportsDpiStagesWrite = true,
            MinDpi = 50,
            MaxDpi = 30000,
            DpiStep = 50,
            SupportedPollingRates = [.. StandardRates],
            SupportedPerformanceModes =
            [
                new(0, "标准模式", "均衡功耗与日常办公响应"),
                new(1, "FPS 竞技模式", "锁定最高传感器跟踪性能"),
            ],
            SupportedLodOptions = isR2
                ? [new(0, "0.7 mm (1档)"), new(1, "1.0 mm (2档)"), new(2, "1.3 mm (3档)"), new(3, "1.7 mm (4档)"), new(4, "2.0 mm (5档)")]
                : [new(0, "0.7 mm (低)"), new(1, "1.0 mm (中)"), new(2, "2.0 mm (高)")],
            SupportsMotionSync = true,
            SupportsAngleSnapping = true,
            SupportsRippleControl = true,
            SupportsDebounce = true,
            MaxDebounceMs = 30,
            SupportsAngleTuning = true,
            SupportsSleepTimeout = true,
            SupportedSleepSeconds = [.. StandardSleepSeconds],
            StatusMessage = dockPct is { } dp
                ? $"AM35 RACE 无线协议已实时同步（接收器备用电池仓 {dp}%）。"
                : "AM35 RACE 协议已实时同步。",
        };

        var profFrame = session.SendCommand(targetId, RaceGetProfile, [], timeoutMs: 450);
        if (profFrame != null && profFrame.Payload.Length >= 2)
            caps.ActiveOnboardProfile = Math.Clamp((int)profFrame.Payload[1], 0, 2);

        var dpiFrame = session.SendCommand(targetId, RaceGetDpi, [], timeoutMs: 500);
        if (dpiFrame != null && ParseDpiPayload(dpiFrame.Payload) is { } dpi)
        {
            caps.DpiStages = dpi.Stages;
            caps.ActiveDpiStage = dpi.CurrentIndex;
            var act = dpi.Stages[dpi.CurrentIndex];
            caps.CurrentDpiX = act.X;
            caps.CurrentDpiY = act.Y;
        }
        else
        {
            caps.DpiStages = [(800, 800)];
            caps.CurrentDpiX = 800;
            caps.CurrentDpiY = 800;
        }

        var rateFrame = session.SendCommand(targetId, RaceGetReportRate, [], timeoutMs: 400);
        int rate = rateFrame != null && rateFrame.Payload.Length >= 3
            ? rateFrame.Payload[1] | (rateFrame.Payload[2] << 8)
            : 1000;
        caps.CurrentPollingRate = StandardRates.Contains(rate) ? rate : 1000;

        var fpsFrame = session.SendCommand(targetId, RaceGetFpsMode, [], timeoutMs: 400, requireStatusZero: false);
        if (fpsFrame != null && fpsFrame.Payload.Length >= 1)
        {
            byte fv = fpsFrame.Payload.Length >= 2 && fpsFrame.Payload[0] == 0 ? fpsFrame.Payload[1] : fpsFrame.Payload[0];
            caps.CurrentPerformanceMode = fv != 0 ? 1 : 0;
        }

        var lodFrame = session.SendCommand(targetId, RaceGetLod, [], timeoutMs: 400);
        if (lodFrame != null && lodFrame.Payload.Length >= 2)
            caps.CurrentLod = Math.Clamp((int)lodFrame.Payload[1], 0, isR2 ? 4 : 2);

        var msFrame = session.SendCommand(targetId, RaceGetMotionSync, [], timeoutMs: 400);
        if (msFrame != null && msFrame.Payload.Length >= 2)
            caps.MotionSync = msFrame.Payload[1] != 0;

        var asFrame = session.SendCommand(targetId, RaceGetAngleSnapping, [], timeoutMs: 400);
        if (asFrame != null && asFrame.Payload.Length >= 2)
            caps.AngleSnapping = asFrame.Payload[1] != 0;

        var rcFrame = session.SendCommand(targetId, RaceGetRippleControl, [], timeoutMs: 350);
        if (rcFrame != null && rcFrame.Payload.Length >= 2)
            caps.RippleControl = rcFrame.Payload[1] != 0;

        var dbFrame = session.SendCommand(targetId, RaceGetSwDebounce, [], timeoutMs: 400);
        if (dbFrame != null && dbFrame.Payload.Length >= 2)
            caps.CurrentDebounceMs = DecodeDebounceMs(caps.CurrentPollingRate, dbFrame.Payload[1]);

        var angFrame = session.SendCommand(targetId, RaceGetRotateAngle, [], timeoutMs: 350, requireStatusZero: false);
        if (angFrame != null && angFrame.Payload.Length >= 3 && angFrame.Payload[0] == 0)
        {
            bool angEn = angFrame.Payload[1] != 0;
            byte rawAng = angFrame.Payload[2];
            int signed = rawAng > 90 ? rawAng - 256 : rawAng;
            caps.AngleTuningDeg = angEn ? Math.Clamp(signed, -30, 30) : 0;
        }

        var sleepFrame = session.SendCommand(targetId, RaceGetSleepTime, [], timeoutMs: 400, requireStatusZero: false);
        if (sleepFrame != null)
        {
            int s24 = DecodeSleepSeconds24G(sleepFrame.Payload, responseHasStatus: false);
            caps.CurrentSleepSeconds = s24;
            if (!caps.SupportedSleepSeconds.Contains(s24))
            {
                caps.SupportedSleepSeconds.Insert(0, s24);
                caps.SupportedSleepSeconds = caps.SupportedSleepSeconds.OrderBy(x => x == 0 ? int.MaxValue : x).ToList();
            }
        }
        else if (isR2 && !isDongle)
        {
            var nvFrame = session.SendCommand(targetId, RaceReadNvData, [(byte)(NvAm30R2PowerSaveId & 0xFF), (byte)(NvAm30R2PowerSaveId >> 8), 0xE8, 0x03], timeoutMs: 400);
            if (nvFrame != null && nvFrame.Payload.Length >= 21)
            {
                int s24 = DecodeSleepSeconds24G(nvFrame.Payload.AsSpan(5), responseHasStatus: false);
                caps.CurrentSleepSeconds = s24;
            }
        }

        int keyCount = isR2 ? 8 : 7;
        var baseOptions = SelectableButtonActions.Select(x => x.Action).ToList();
        if (isR2) baseOptions.Add("切换键层");

        for (byte k = 1; k <= keyCount; k++)
        {
            var kf = session.SendCommand(targetId, RaceGetKeyRemap, [k], timeoutMs: 350);
            if (kf == null || kf.Payload.Length < 3 || kf.Payload[0] != 0) continue;

            string btnName = GetButtonSlotName(k, isR2);
            string current = FormatKeyRemapLabel(kf.Payload, out bool isLeftClickLock);
            caps.ButtonBindings.Add(new ButtonBindingSlot(k, btnName, current, baseOptions, isLeftClickLock));
        }

        return caps;
    }

    private static string GetButtonSlotName(byte keyId, bool isR2) => keyId switch
    {
        1 => "左键 (主键 1)",
        2 => "右键 (主键 2)",
        3 => "中键 (滚轮点击)",
        4 => "侧键 Forward (前进)",
        5 => "侧键 Back (后退)",
        6 => "滚轮向上",
        7 => "滚轮向下",
        8 when isR2 => "底部功能键",
        _ => $"按键 {keyId}",
    };

    private static string FormatKeyRemapLabel(byte[] payload, out bool isPrimaryLeft)
    {
        isPrimaryLeft = false;
        byte type = payload[2];
        var p = payload.Length > 3 ? payload.AsSpan(3) : ReadOnlySpan<byte>.Empty;

        switch (type)
        {
            case 0: return "禁用按键";
            case 1:
                isPrimaryLeft = payload[1] == 1;
                return "左键点击";
            case 2: return "右键点击";
            case 3: return "中键点击";
            case 4: return "前进";
            case 5: return "后退";
            case 6:
                if (p.Length >= 4)
                {
                    ushort consumer = (ushort)(p[0] | (p[1] << 8));
                    byte usage = p[2];
                    byte mod = p[3];
                    if (consumer > 0) return $"多媒体 0x{consumer:X4}";
                    return HidKeyCatalog.FormatKeyboardBinding(mod, usage);
                }
                return "键盘按键";
            case 7: return "回报率切换";
            case 8:
                if (p.Length >= 16)
                {
                    int len = Array.IndexOf<byte>(p[..16].ToArray(), 0);
                    string name = Encoding.ASCII.GetString(p[..(len < 0 ? 16 : len)]).Trim();
                    return string.IsNullOrEmpty(name) ? "板载硬件宏" : $"板载宏: {name}";
                }
                return "板载硬件宏";
            case 9: return "连点键";
            case 11:
                byte sub = p.Length > 0 ? p[0] : (byte)3;
                return sub switch
                {
                    0 => "DPI +",
                    1 => "DPI -",
                    2 => p.Length >= 3 ? $"DPI 锁定 ({p[1] | (p[2] << 8)})" : "DPI 锁定",
                    _ => "DPI 循环",
                };
            case 12: return "滚轮向上";
            case 13: return "滚轮向下";
            case 14: return "切换键层";
            default: return $"自定义 (0x{type:X2})";
        }
    }

    private static bool TryEncodeButtonAction(string action, bool isR2, out byte type, out byte[] actionParams)
    {
        if (HidKeyCatalog.TryParseKeyboardBinding(action, out byte mod, out byte usage))
        {
            type = 6;
            actionParams = [0x00, 0x00, usage, mod];
            return true;
        }

        foreach (var item in SelectableButtonActions)
        {
            if (string.Equals(item.Action, action, StringComparison.Ordinal))
            {
                type = item.Type;
                actionParams = item.Params;
                return true;
            }
        }

        if (isR2 && action == "切换键层")
        {
            type = 14;
            actionParams = [];
            return true;
        }

        type = 0;
        actionParams = [];
        return false;
    }

    private static void WriteLe16(Span<byte> dst, int offset, int value)
    {
        dst[offset] = (byte)(value & 0xFF);
        dst[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
