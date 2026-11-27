using System.Diagnostics.CodeAnalysis;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// Endgame Gear WE 系列（OP1we / XM2we，VID 0x3367）协议编解码与读写实现（基于 OpenMouse mouse-protocol）：
/// Feature Report 0x08 (16B 载荷, Sum==0x55) 发指令，Input Report 0x09 收应答；支持电量、DPI 档位、回报率、LOD、防抖与休眠。
/// </summary>
public static class EggWeProtocol
{
    public const ushort EggVid = 0x3367;
    public const byte ReportIdCmd = 0x08;
    public const byte ReportIdNotify = 0x09;
    public const int PayloadLength = 16;
    public const byte ChecksumTarget = 0x55;
    public const byte CmdGetPower = 0x04;
    public const byte CmdWriteEeprom = 0x07;
    public const byte CmdReadEeprom = 0x08;
    public const int MaxEepromChunk = 8;
    public const int ProfileEndOffset = 0xB5;

    public const int OffReportRate = 0x00;
    public const int OffDpiStageCount = 0x02;
    public const int OffActiveDpiLevel = 0x04;
    public const int OffLod = 0x0A;
    public const int OffDpiStages = 0x0C;
    public const int DpiStageBytes = 4;
    public const int MaxDpiStages = 8;
    public const int OffDebounce = 0xA9;
    public const int OffSleep = 0xAD;

    public static readonly int[] SupportedPollingRates = [125, 250, 500, 1000];

    private static readonly HashSet<ushort> WePids =
    [
        0x1902, 0x1903, 0x1904, 0x1905,
        0x1960, 0x1961, 0x1962, 0x1968, 0x1970, 0x1972, 0x1982,
    ];

    public sealed record WeProfile(
        int ActiveDpiLevel,
        int DpiStageCount,
        int PollingRateHz,
        int LodCode,
        int? DebounceMs,
        int? SleepSeconds,
        List<(int X, int Y)> Stages);

    public static bool IsLikelyWeDevice(ushort pid, List<HidDevice> ifaces)
    {
        if (!WePids.Contains(pid))
            return false;
        // OP1 8K / v2 复用部分 PID 时暴露 0xA1 Feature Report，避免误判
        foreach (var d in ifaces)
        {
            var ids = HidReportInspector.GetReportIds(d);
            if (System.Linq.Enumerable.Contains(ids.FeatureIds, (byte)0xA1))
                return false;
        }
        return true;
    }

    public static byte ComputeChecksum(byte reportId, ReadOnlySpan<byte> first15Bytes)
    {
        int sum = reportId;
        int len = Math.Min(15, first15Bytes.Length);
        for (int i = 0; i < len; i++)
            sum += first15Bytes[i];
        return (byte)((ChecksumTarget - (sum & 0xFF)) & 0xFF);
    }

    public static byte[] BuildCommandFrame(byte cmd, ReadOnlySpan<byte> dataAfterCmd = default)
    {
        var frame = new byte[1 + PayloadLength];
        frame[0] = ReportIdCmd;
        frame[1] = cmd;
        int copy = Math.Min(14, dataAfterCmd.Length);
        if (copy > 0)
            dataAfterCmd[..copy].CopyTo(frame.AsSpan(2));
        frame[16] = ComputeChecksum(ReportIdCmd, frame.AsSpan(1, 15));
        return frame;
    }

    public static byte[] BuildReadEepromFrame(ushort addr, int length)
    {
        int clamped = Math.Clamp(length, 1, MaxEepromChunk);
        return BuildCommandFrame(CmdReadEeprom, [0x00, (byte)(addr >> 8), (byte)(addr & 0xFF), (byte)clamped]);
    }

    public static byte[] BuildWriteEepromFrame(ushort addr, ReadOnlySpan<byte> data)
    {
        int len = Math.Clamp(data.Length, 1, MaxEepromChunk);
        Span<byte> args = stackalloc byte[4 + len];
        args[0] = 0x00;
        args[1] = (byte)(addr >> 8);
        args[2] = (byte)(addr & 0xFF);
        args[3] = (byte)len;
        data[..len].CopyTo(args[4..]);
        return BuildCommandFrame(CmdWriteEeprom, args);
    }

    public static (byte Value, byte Crc) PackScalarPair(byte value)
        => (value, (byte)((ChecksumTarget - value) & 0xFF));

    public static byte? UnpackScalarPair(byte value, byte crc)
        => (byte)((ChecksumTarget - value) & 0xFF) == crc ? value : null;

    public static byte[] PackDpiStage(int xCpi, int yCpi, byte flags = 0)
    {
        byte x = (byte)(Math.Clamp((int)Math.Round(xCpi / 50.0), 1, 256) - 1);
        byte y = (byte)(Math.Clamp((int)Math.Round(yCpi / 50.0), 1, 256) - 1);
        byte crc = (byte)((ChecksumTarget - ((x + y + flags) & 0xFF)) & 0xFF);
        return [x, y, flags, crc];
    }

    public static (int X, int Y)? UnpackDpiStage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return null;
        byte x = bytes[0], y = bytes[1], flags = bytes[2], crc = bytes[3];
        if ((byte)((ChecksumTarget - ((x + y + flags) & 0xFF)) & 0xFF) != crc)
            return null;
        return ((x + 1) * 50, (y + 1) * 50);
    }

    public static int? DecodePollingRate(byte divider) => divider switch
    {
        1 => 1000,
        2 => 500,
        4 => 250,
        8 => 125,
        _ => null,
    };

    public static byte? EncodePollingRate(int hz) => hz switch
    {
        1000 => 1,
        500 => 2,
        250 => 4,
        125 => 8,
        _ => null,
    };

    public static (int Level, bool Charging)? ParseGetPowerResponse(ReadOnlySpan<byte> response)
    {
        ReadOnlySpan<byte> data = response;
        if (data.Length > PayloadLength - 1 && data[0] == ReportIdNotify)
            data = data[1..];
        if (data.Length < 7 || data[0] != CmdGetPower || data[1] != 0)
            return null;
        byte power = data[5];
        if (power > 100) return null;
        return (power, data[6] != 0);
    }

    public static byte[]? ParseReadEepromResponse(ReadOnlySpan<byte> response, ushort expectedAddr, int expectedLen)
    {
        ReadOnlySpan<byte> data = response;
        if (data.Length > 0 && data[0] == ReportIdNotify)
            data = data[1..];
        if (data.Length < 5 + expectedLen) return null;
        if (data[0] != CmdReadEeprom || data[1] != 0) return null;
        if (data[3] != (byte)(expectedAddr & 0xFF) || data[4] != expectedLen) return null;
        return data.Slice(5, expectedLen).ToArray();
    }

    public static bool ParseWriteEepromResponse(ReadOnlySpan<byte> response)
    {
        ReadOnlySpan<byte> data = response;
        if (data.Length > 0 && data[0] == ReportIdNotify)
            data = data[1..];
        return data.Length >= 2 && data[0] == CmdWriteEeprom && data[1] == 0;
    }

    public static WeProfile DecodeProfile(ReadOnlySpan<byte> mem)
    {
        static byte? ReadPair(ReadOnlySpan<byte> buf, int off)
            => off + 1 < buf.Length ? UnpackScalarPair(buf[off], buf[off + 1]) : null;

        int stageCount = Math.Clamp((int)(ReadPair(mem, OffDpiStageCount) ?? 1), 1, MaxDpiStages);
        int active = Math.Clamp((int)(ReadPair(mem, OffActiveDpiLevel) ?? 0), 0, stageCount - 1);
        int rateHz = ReadPair(mem, OffReportRate) is { } r ? (DecodePollingRate(r) ?? 1000) : 1000;
        int lod = ReadPair(mem, OffLod) is { } l && l is 1 or 2 ? l : 1;
        int? debounce = ReadPair(mem, OffDebounce) is { } db ? (int)db : null;
        int? sleepSec = ReadPair(mem, OffSleep) is { } sl && sl > 0 ? sl * 10 : null;

        var stages = new List<(int X, int Y)>(stageCount);
        for (int i = 0; i < stageCount; i++)
        {
            int off = OffDpiStages + i * DpiStageBytes;
            if (off + 4 <= mem.Length && UnpackDpiStage(mem.Slice(off, 4)) is { } st)
                stages.Add(st);
            else
                stages.Add((800, 800));
        }

        return new WeProfile(active, stageCount, rateHz, lod, debounce, sleepSec, stages);
    }

    public static (int Level, bool Charging)? QueryBattery(List<HidDevice> ifaces)
    {
        if (!TryOpenPair(ifaces, out var cmd, out var notify))
            return null;
        using (cmd)
        using (notify)
        {
            var reply = Transact(cmd, notify, BuildCommandFrame(CmdGetPower), CmdGetPower, 550);
            return reply != null ? ParseGetPowerResponse(reply) : null;
        }
    }

    public static DeviceTuningCapabilities QueryCapabilities(string deviceId, List<HidDevice> ifaces, string? modelName)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryOpenPair(ifaces, out var cmd, out var notify))
        {
            caps.StatusMessage = "未检测到 Endgame Gear WE (0xFF02/0xFF01) 控制通道。";
            return caps;
        }

        using (cmd)
        using (notify)
        {
            byte[]? mem = ReadProfileMemory(cmd, notify);
            if (mem == null)
            {
                caps.StatusMessage = "Endgame Gear 鼠标当前休眠中，请晃动唤醒后重试。";
                return caps;
            }

            var prof = DecodeProfile(mem);
            var activeStage = prof.Stages[Math.Clamp(prof.ActiveDpiLevel, 0, prof.Stages.Count - 1)];

            caps.IsOnline = true;
            caps.StatusMessage = $"{modelName ?? "Endgame Gear"} · WE EEPROM 协议";
            caps.SupportsDpi = true;
            caps.SupportsIndependentXY = true;
            caps.SupportsDpiStagesWrite = true;
            caps.MinDpi = 50;
            caps.MaxDpi = 12800;
            caps.DpiStep = 50;
            caps.DpiStages = prof.Stages;
            caps.ActiveDpiStage = prof.ActiveDpiLevel;
            caps.CurrentDpiX = activeStage.X;
            caps.CurrentDpiY = activeStage.Y;
            caps.SupportedPollingRates = [.. SupportedPollingRates];
            caps.CurrentPollingRate = prof.PollingRateHz;
            caps.SupportedLodOptions = [new LodOption(1, "中 (Medium)"), new LodOption(2, "高 (High)")];
            caps.CurrentLod = prof.LodCode;
            if (prof.DebounceMs is { } db)
            {
                caps.SupportsDebounce = true;
                caps.MaxDebounceMs = 30;
                caps.CurrentDebounceMs = db;
            }
            if (prof.SleepSeconds is { } sl)
            {
                caps.SupportsSleepTimeout = true;
                caps.MinSleepSeconds = 60;
                caps.MaxSleepSeconds = 1800;
                caps.CurrentSleepSeconds = sl;
                caps.SupportedSleepSeconds = [60, 180, 300, 600, 900, 1800];
            }
            return caps;
        }
    }

    public static DeviceTuningResult ApplySettings(string deviceId, List<HidDevice> ifaces, string? modelName, DeviceTuningUpdate update)
    {
        if (!TryOpenPair(ifaces, out var cmd, out var notify))
            return new DeviceTuningResult(false, "无法打开 Endgame Gear WE 控制通道。");

        var applied = new List<string>();
        var errors = new List<string>();

        using (cmd)
        using (notify)
        {
            byte[]? mem = ReadProfileMemory(cmd, notify);
            if (mem == null)
                return new DeviceTuningResult(false, "读取鼠标配置失败，请先唤醒鼠标。");

            var prof = DecodeProfile(mem);

            if (update.ActiveDpiStage is { } actStage)
            {
                byte clamped = (byte)Math.Clamp(actStage, 0, Math.Max(0, prof.DpiStageCount - 1));
                var (v, c) = PackScalarPair(clamped);
                if (WriteEeprom(cmd, notify, OffActiveDpiLevel, [v, c]))
                    applied.Add($"档位 {clamped + 1}");
                else
                    errors.Add("活动档位");
            }

            if (update.Dpi is var (dx, dy))
            {
                int slot = Math.Clamp(update.ActiveDpiStage ?? prof.ActiveDpiLevel, 0, MaxDpiStages - 1);
                ushort addr = (ushort)(OffDpiStages + slot * DpiStageBytes);
                byte[] packed = PackDpiStage(dx, dy);
                if (WriteEeprom(cmd, notify, addr, packed))
                    applied.Add(dx == dy ? $"DPI {dx}" : $"DPI {dx}/{dy}");
                else
                    errors.Add("DPI");
            }

            if (update.DpiStages is { Count: > 0 } stages)
            {
                byte count = (byte)Math.Clamp(stages.Count, 1, MaxDpiStages);
                var (cv, cc) = PackScalarPair(count);
                bool ok = WriteEeprom(cmd, notify, OffDpiStageCount, [cv, cc]);
                for (int i = 0; i < count && ok; i++)
                {
                    ushort addr = (ushort)(OffDpiStages + i * DpiStageBytes);
                    ok = WriteEeprom(cmd, notify, addr, PackDpiStage(stages[i].X, stages[i].Y));
                }
                if (ok) applied.Add($"{count} 档 DPI");
                else errors.Add("DPI 档位表");
            }

            if (update.PollingRateHz is { } hz && EncodePollingRate(hz) is { } rateCode)
            {
                var (v, c) = PackScalarPair(rateCode);
                if (WriteEeprom(cmd, notify, OffReportRate, [v, c]))
                    applied.Add($"{hz}Hz");
                else
                    errors.Add("回报率");
            }

            if (update.LodCode is { } lod)
            {
                byte code = (byte)(lod >= 2 ? 2 : 1);
                var (v, c) = PackScalarPair(code);
                if (WriteEeprom(cmd, notify, OffLod, [v, c]))
                    applied.Add($"LOD {(code == 2 ? "High" : "Medium")}");
                else
                    errors.Add("LOD");
            }

            if (update.DebounceMs is { } db)
            {
                byte ms = (byte)Math.Clamp(db, 0, 30);
                var (v, c) = PackScalarPair(ms);
                if (WriteEeprom(cmd, notify, OffDebounce, [v, c]))
                    applied.Add($"防抖 {ms}ms");
                else
                    errors.Add("防抖");
            }

            if (update.SleepSeconds is { } sl && sl > 0)
            {
                byte rawSleep = (byte)Math.Clamp((int)Math.Round(sl / 10.0), 1, 255);
                var (v, c) = PackScalarPair(rawSleep);
                if (WriteEeprom(cmd, notify, OffSleep, [v, c]))
                    applied.Add($"休眠 {rawSleep * 10}s");
                else
                    errors.Add("休眠");
            }
        }

        var refreshed = QueryCapabilities(deviceId, ifaces, modelName);
        if (applied.Count == 0 && errors.Count == 0)
            return new DeviceTuningResult(true, "未变更 Endgame Gear 参数", refreshed);
        return errors.Count > 0
            ? new DeviceTuningResult(applied.Count > 0, $"部分设置失败 ({string.Join("、", errors)})", refreshed)
            : new DeviceTuningResult(true, $"已写入 {string.Join(" / ", applied)}", refreshed);
    }

    private static bool TryOpenPair(
        List<HidDevice> ifaces,
        [NotNullWhen(true)] out RawHidFeature? cmd,
        [NotNullWhen(true)] out HidChannel? notify)
    {
        cmd = null;
        notify = null;

        var cmdDev = ifaces
            .Where(d => HidSafe.PrimaryUsage(d).UsagePage == 0xFF02 && HidSafe.MaxFeatureLen(d) >= 16)
            .OrderByDescending(d => HidSafe.MaxFeatureLen(d))
            .FirstOrDefault()
            ?? ifaces.FirstOrDefault(d => HidSafe.MaxFeatureLen(d) >= 16 && HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00);

        var notifyDev = ifaces
            .Where(d => HidSafe.PrimaryUsage(d).UsagePage == 0xFF01 && HidSafe.MaxInputLen(d) >= 8)
            .FirstOrDefault()
            ?? ifaces.FirstOrDefault(d => HidSafe.MaxInputLen(d) >= 8 && HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00);

        if (cmdDev == null || notifyDev == null)
            return false;

        cmd = RawHidFeature.TryOpen(cmdDev.DevicePath);
        notify = HidChannel.TryOpen(notifyDev);
        if (cmd == null || notify == null)
        {
            cmd?.Dispose();
            notify?.Dispose();
            cmd = null;
            notify = null;
            return false;
        }
        return true;
    }

    private static byte[]? Transact(RawHidFeature cmd, HidChannel notify, byte[] frame, byte expectedCmd, int timeoutMs)
    {
        while (notify.Read(10) != null) { }
        if (!cmd.SetFeature(frame))
            return null;

        long dl = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < dl)
        {
            var r = notify.Read(80);
            if (r == null || r.Length < 2) continue;
            int o = (r.Length > PayloadLength - 1 && r[0] == ReportIdNotify) ? 1 : 0;
            if (r.Length > o && r[o] == expectedCmd)
                return r;
        }
        return null;
    }

    private static byte[]? ReadProfileMemory(RawHidFeature cmd, HidChannel notify)
    {
        var mem = new byte[ProfileEndOffset];
        for (int offset = 0; offset < ProfileEndOffset; offset += MaxEepromChunk)
        {
            int len = Math.Min(MaxEepromChunk, ProfileEndOffset - offset);
            byte[]? chunk = null;
            for (int attempt = 0; attempt < 2 && chunk == null; attempt++)
            {
                var reply = Transact(cmd, notify, BuildReadEepromFrame((ushort)offset, len), CmdReadEeprom, 600);
                if (reply != null)
                    chunk = ParseReadEepromResponse(reply, (ushort)offset, len);
            }
            if (chunk == null)
                return null;
            Array.Copy(chunk, 0, mem, offset, len);
        }
        return mem;
    }

    private static bool WriteEeprom(RawHidFeature cmd, HidChannel notify, ushort addr, byte[] data)
    {
        for (int offset = 0; offset < data.Length; offset += MaxEepromChunk)
        {
            int len = Math.Min(MaxEepromChunk, data.Length - offset);
            var reply = Transact(cmd, notify, BuildWriteEepromFrame((ushort)(addr + offset), data.AsSpan(offset, len)), CmdWriteEeprom, 700);
            if (reply == null || !ParseWriteEepromResponse(reply))
                return false;
            Thread.Sleep(20);
        }
        return true;
    }
}
