using System.Globalization;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Services;

namespace GearDeck.Core.Providers;

public sealed record Hi98InterfaceDiagnostic(
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    int MaxIn,
    int MaxOut,
    int MaxFeat,
    string Transport,
    string Product,
    string OpenStatus);

public sealed record Hi98WriteOutcome(
    bool Success,
    string Message,
    byte[]? Data = null,
    Hi98LedConfigBlock? LedConfig = null);

public sealed partial class LeobogProvider
{
    internal const int WiredPageLength = 512;
    internal const int CustomRgbLength = 378; // 126 * 3 Planar R[126] G[126] B[126]
    private const byte CmdWiredKeymapRead = 0x83;
    private const byte CmdWiredKeymapWrite = 0x03;
    private const byte CmdWiredCustomRgbRead = 0x86;
    private const byte CmdWiredCustomRgbWrite = 0x06;
    private const byte CmdWiredLiveRgb = 0x08;
    private const byte WirelessCmdKeymap = 0x01;
    private const byte WirelessCmdLiveRgb = 0x88;
    private const byte WirelessLiveSubKeys = 0x01;
    private const int WirelessLiveMaxFrameBytes = 200;

    /// <summary>根据设备 ID 解析物理连接通道，非有线严格只读。</summary>
    public static Hi98ConnectionTransport ResolveTransport(string deviceId)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return Hi98ConnectionTransport.Disconnected;
        if (IsWired(vid, pid))
            return Hi98ConnectionTransport.UsbWired;
        if (pid == 0xFA07)
            return Hi98ConnectionTransport.Bluetooth;
        return Hi98ConnectionTransport.Wireless24G;
    }

    public static int? ReadBatteryOnce(string deviceId, out bool isCharging, out bool permissionDenied)
    {
        isCharging = false;
        permissionDenied = false;
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return null;

        if (IsWired(vid, pid))
        {
            isCharging = true;
            return null; // 有线仅报告充电中，绝不伪造百分比
        }

        if (OperatingSystem.IsMacOS())
        {
            var group = MacIOKitHid.Enumerate(vid, pid);
            return ProbeWirelessBatteryMac(group, out permissionDenied);
        }

        var winGroup = HidSafe.EnumerateDevices(vid, pid);
        return ProbeWirelessBattery(winGroup);
    }

    public static string? ReadWiredModelName(string deviceId)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid) || !IsWired(vid, pid))
            return null;

        if (OperatingSystem.IsMacOS())
        {
            var dev = FindWiredMacInterface(MacIOKitHid.Enumerate(vid, pid));
            if (dev == null) return null;
            using var conn = MacIOKitHid.MacHidConnection.TryOpen(dev, out _);
            return conn == null ? null : ReadWiredModelMac(conn);
        }

        var winDev = FindWiredInterface(HidSafe.EnumerateDevices(vid, pid));
        if (winDev == null) return null;
        using var raw = RawHidFeature.TryOpen(winDev.DevicePath);
        return raw == null ? null : ReadWiredModel(raw);
    }

    public static Hi98LedConfigBlock? ReadLedConfigBlock(string deviceId, out bool permissionDenied, out string? error)
    {
        permissionDenied = false;
        error = null;
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
        {
            error = "无效的 LEOBOG 设备 ID。";
            return null;
        }

        byte[]? rawCfg;
        if (IsWired(vid, pid))
        {
            rawCfg = ReadWiredBlock(vid, pid, CmdWiredConfig, 0, 0, ConfigLength, verifyMagicTail: true,
                out permissionDenied, out error);
        }
        else
        {
            var req = BuildWirelessReadPacket(0x04, 0);
            int retries = pid == 0xFA07 ? 6 : 3;
            rawCfg = QueryWirelessBlock(vid, pid, req, CmdWirelessConfig, ConfigLength, ConfigLength,
                verifyMagicTail: true, timeoutMs: 1400, retries, out permissionDenied, out error);
        }

        if (rawCfg == null)
        {
            error ??= permissionDenied
                ? "缺少 macOS「输入监控」权限，请在系统设置中授权并重启 GearDeck。"
                : "未能从键盘读取 128B 板载配置（请确认键盘未休眠）。";
            return null;
        }

        try
        {
            return new Hi98LedConfigBlock(rawCfg);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static byte[]? ReadKeymapPage(string deviceId, Hi98KeyLayer layer, out bool permissionDenied, out string? error)
    {
        permissionDenied = false;
        error = null;
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
        {
            error = "无效的 LEOBOG 设备 ID。";
            return null;
        }

        if (IsWired(vid, pid))
        {
            var page = ReadWiredBlock(vid, pid, CmdWiredKeymapRead, layer.WiredSub, layer.WiredPacketIndex,
                WiredPageLength, verifyMagicTail: true, out permissionDenied, out error);
            if (page == null)
                error ??= $"有线读取「{layer.Title}」失败。";
            return page;
        }

        if (!layer.IsWritable)
        {
            error = $"无线模式仅支持读取「默认层 / Fn1 / Fn2」，不支持读取「{layer.Title}」。";
            return null;
        }

        var req = BuildWirelessReadPacket(WirelessCmdKeymap, layer.WirelessSub);
        int retries = pid == 0xFA07 ? 6 : 3;
        var wirelessPage = QueryWirelessBlock(vid, pid, req, (byte)(WirelessCmdKeymap | 0x40), WiredPageLength,
            Hi98CLayout.LayerByteLength, verifyMagicTail: false, timeoutMs: 2600, retries,
            out permissionDenied, out error);
        if (wirelessPage == null)
            error ??= permissionDenied
                ? "缺少 macOS「输入监控」权限。"
                : $"无线读取「{layer.Title}」超时（键盘可能休眠，请轻按任意键唤醒后重试）。";
        return wirelessPage;
    }

    public static Dictionary<int, Hi98RgbColor>? ReadPerKeyRgbWired(string deviceId, out bool permissionDenied, out string? error)
    {
        permissionDenied = false;
        error = null;
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid) || !IsWired(vid, pid))
        {
            error = "单键静态配色读取仅支持 USB-C 有线模式。";
            return null;
        }

        var raw = ReadWiredBlock(vid, pid, CmdWiredCustomRgbRead, 0, 0, CustomRgbLength,
            verifyMagicTail: false, out permissionDenied, out error);
        if (raw == null || raw.Length < CustomRgbLength)
        {
            error ??= "未能读取 378B 单键自定义配色。";
            return null;
        }
        return DecodePlanarCustomRgb(raw);
    }

    public static Hi98WriteOutcome WriteKeymapLayerWired(
        string deviceId,
        Hi98KeyLayer layer,
        IReadOnlyDictionary<int, Hi98KeyBindingRecord> updatesByMatrix,
        string reason,
        LeobogHi98BackupService? backup = null)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new(false, "无效的 LEOBOG 设备 ID。");
        if (!IsWired(vid, pid))
            return new(false, "安全保护：LEOBOG Hi98C Pro 仅允许在 USB-C 有线模式下写入板载改键，2.4G / 蓝牙模式严格只读。");
        if (!layer.IsWritable || layer.WiredPacketIndex != 0)
            return new(false, $"「{layer.Title}」为固件只读预设区（Fn+E），不支持写入；请编辑「默认层 / Fn1 / Fn2」并切到 Fn+W 模式生效。");

        var currentPage = ReadWiredBlock(vid, pid, CmdWiredKeymapRead, layer.WiredSub, 0, WiredPageLength,
            verifyMagicTail: true, out bool permDenied, out var readErr);
        if (currentPage == null)
            return new(false, permDenied ? "打开有线接口需要 macOS 输入监控权限。" : $"读取「{layer.Title}」当前页失败：{readErr}");

        if (backup != null)
        {
            var ledRaw = ReadWiredBlock(vid, pid, CmdWiredConfig, 0, 0, ConfigLength, true, out _, out _);
            backup.SaveBackup(new Hi98EepromBackupSnapshot
            {
                Reason = reason,
                Transport = Hi98ConnectionTransport.UsbWired,
                Layers = new Dictionary<int, byte[]> { [layer.RawValue] = currentPage.ToArray() },
                LedConfig = ledRaw,
            });
        }

        var working = new byte[WiredPageLength];
        Array.Copy(currentPage, working, Math.Min(currentPage.Length, WiredPageLength));

        foreach (var (slot, rec) in updatesByMatrix)
        {
            if (slot < 0 || slot >= Hi98CLayout.MatrixSlotCount) continue;
            Array.Copy(rec.Bytes, 0, working, slot * 4, 4);
        }

        if (layer.IsBaseLayer)
        {
            bool hasFn = false;
            for (int slot = 0; slot < Hi98CLayout.MatrixSlotCount; slot++)
            {
                if (working[slot * 4] == 0x0D) { hasFn = true; break; }
            }
            if (!hasFn)
                return new(false, $"「{layer.Title}」至少必须保留一个物理 Fn 键，已阻止可能锁死快捷键层的写入。");
        }

        if (working.AsSpan().SequenceEqual(currentPage))
            return new(true, $"「{layer.Title}」无字节变更，已跳过写入。", currentPage);

        var req = BuildWiredFeatureRequest(CmdWiredKeymapWrite, layer.WiredSub, 1, 0, WiredPageLength, working);
        if (!SendWiredReport(vid, pid, req, out permDenied, out var writeErr))
            return new(false, permDenied ? "写入需要 macOS 输入监控权限。" : $"发送有线改键指令失败：{writeErr}");

        Thread.Sleep(400);

        var readBack = ReadWiredBlock(vid, pid, CmdWiredKeymapRead, layer.WiredSub, 0, WiredPageLength,
            verifyMagicTail: true, out _, out var rbErr);
        if (readBack == null)
            return new(false, $"「{layer.Title}」回读校验失败：{rbErr}");
        if (!readBack.AsSpan(0, Hi98CLayout.LayerByteLength).SequenceEqual(working.AsSpan(0, Hi98CLayout.LayerByteLength)))
            return new(false, $"「{layer.Title}」回读校验不一致，请立即从备份恢复。");

        return new(true, $"已写入「{layer.Title}」并通过 400ms 回读校验。", readBack);
    }

    public static Hi98WriteOutcome SurgicalRestoreFnWired(string deviceId, int fnMatrixSlot, LeobogHi98BackupService? backup = null)
        => WriteKeymapLayerWired(
            deviceId,
            Hi98KeyLayer.Base,
            new Dictionary<int, Hi98KeyBindingRecord> { [fnMatrixSlot] = Hi98KeyBindingRecord.FnKey },
            $"紧急恢复物理 Fn 键 (#{fnMatrixSlot})",
            backup);

    public static Hi98WriteOutcome WriteLedConfigWired(
        string deviceId,
        Hi98LedConfigBlock nextConfig,
        string reason,
        LeobogHi98BackupService? backup = null)
    {
        ArgumentNullException.ThrowIfNull(nextConfig);
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new(false, "无效的 LEOBOG 设备 ID。");
        if (!IsWired(vid, pid))
            return new(false, "安全保护：仅允许在 USB-C 有线模式下写入板载灯光与硬件配置。");
        if (nextConfig.SleepTimerSeconds < 30)
            return new(false, "安全保护：禁止写入永不休眠 (0)，休眠时间不得低于 30 秒。");

        var currentBytes = ReadWiredBlock(vid, pid, CmdWiredConfig, 0, 0, ConfigLength,
            verifyMagicTail: true, out bool permDenied, out var readErr);
        if (currentBytes == null)
            return new(false, permDenied ? "打开有线接口需要 macOS 输入监控权限。" : $"读取板载配置失败：{readErr}");

        var current = new Hi98LedConfigBlock(currentBytes);
        backup?.SaveBackup(new Hi98EepromBackupSnapshot
        {
            Reason = reason,
            Transport = Hi98ConnectionTransport.UsbWired,
            LedConfig = current.RawBytes.ToArray(),
        });

        var allowed = new HashSet<int>
        {
            Hi98LedConfigBlock.PerKeyFlagOffset,
            Hi98LedConfigBlock.EffectOffset,
            Hi98LedConfigBlock.WinLockOffset,
            Hi98LedConfigBlock.SideModeOffset,
            Hi98LedConfigBlock.SleepOffset,
            Hi98LedConfigBlock.OsModeOffset,
        };
        for (int effId = 1; effId <= Hi98LedConfigBlock.MaxSlotEffectID; effId++)
        {
            int off = Hi98LedConfigBlock.SlotsBaseOffset + 2 * (effId - 1);
            if (off + 1 < Hi98LedConfigBlock.ByteLength - 2)
            {
                allowed.Add(off);
                allowed.Add(off + 1);
            }
        }

        var changed = new List<int>();
        for (int i = 0; i < Hi98LedConfigBlock.ByteLength; i++)
        {
            if (nextConfig.RawBytes[i] == current.RawBytes[i]) continue;
            if (!allowed.Contains(i))
                return new(false, $"安全保护：拒绝修改未知偏移 0x{i:X2}。");
            changed.Add(i);
        }

        if (changed.Count == 0)
            return new(true, "灯光与硬件参数未发生变化，跳过写入。", LedConfig: current);

        var req = BuildWiredFeatureRequest(CmdWriteConfig, 0, 1, 0, ConfigLength, nextConfig.RawBytes);
        if (!SendWiredReport(vid, pid, req, out permDenied, out var writeErr))
            return new(false, permDenied ? "写入需要 macOS 输入监控权限。" : $"写入板载配置失败：{writeErr}");

        Thread.Sleep(400);

        var verifyBytes = ReadWiredBlock(vid, pid, CmdWiredConfig, 0, 0, ConfigLength,
            verifyMagicTail: true, out _, out var rbErr);
        if (verifyBytes == null)
            return new(false, $"回读板载配置失败：{rbErr}");

        foreach (int off in changed)
        {
            if (verifyBytes[off] != nextConfig.RawBytes[off])
                return new(false, $"回读校验失败：偏移 0x{off:X2} 写入 {nextConfig.RawBytes[off]:X2}，读回 {verifyBytes[off]:X2}。");
        }

        return new(true, $"已写入硬件/灯效设置并完成 400ms 回读校验（变更 {changed.Count} 字节）。",
            LedConfig: new Hi98LedConfigBlock(verifyBytes));
    }

    public static Hi98WriteOutcome WritePerKeyRgbWired(
        string deviceId,
        IReadOnlyDictionary<int, Hi98RgbColor> colors,
        string reason,
        LeobogHi98BackupService? backup = null)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new(false, "无效的 LEOBOG 设备 ID。");
        if (!IsWired(vid, pid))
            return new(false, "安全保护：单键静态 RGB 烧录仅支持 USB-C 有线模式；无线模式可使用「实时预览」。");

        if (backup != null)
        {
            var curLed = ReadWiredBlock(vid, pid, CmdWiredConfig, 0, 0, ConfigLength, true, out _, out _);
            var curRgb = ReadWiredBlock(vid, pid, CmdWiredCustomRgbRead, 0, 0, CustomRgbLength, false, out _, out _);
            backup.SaveBackup(new Hi98EepromBackupSnapshot
            {
                Reason = reason,
                Transport = Hi98ConnectionTransport.UsbWired,
                LedConfig = curLed,
                PerKeyRgb = curRgb,
            });
        }

        var planar = EncodePlanarCustomRgb(colors);
        var req = BuildWiredFeatureRequest(CmdWiredCustomRgbWrite, 0, 1, 0, CustomRgbLength, planar);
        if (!SendWiredReport(vid, pid, req, out bool permDenied, out var err))
            return new(false, permDenied ? "写入需要 macOS 输入监控权限。" : $"烧录单键配色失败：{err}");

        Thread.Sleep(400);

        var cfgBytes = ReadWiredBlock(vid, pid, CmdWiredConfig, 0, 0, ConfigLength, true, out _, out _);
        if (cfgBytes != null)
        {
            var nextLed = new Hi98LedConfigBlock(cfgBytes)
            {
                EffectHardwareID = Hi98OnboardLightingEffect.CustomPerKeyHardwareID,
                IsPerKeyCustomMode = true,
            };
            return WriteLedConfigWired(deviceId, nextLed, "激活单键自定义灯效", backup: null);
        }

        return new(true, "已烧录 378B 单键静态配色到键盘闪存。");
    }

    public static Hi98WriteOutcome RestoreSnapshotWired(
        string deviceId,
        Hi98EepromBackupSnapshot snapshot,
        LeobogHi98BackupService? backup = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid) || !IsWired(vid, pid))
            return new(false, "安全保护：仅允许在 USB-C 有线模式下恢复 EEPROM 备份。");

        foreach (var (layerRaw, pageBytes) in snapshot.Layers.OrderBy(k => k.Key))
        {
            var layer = Hi98KeyLayer.FromRawValue(layerRaw);
            if (!layer.IsWritable || layer.WiredPacketIndex != 0) continue;

            var padded = new byte[WiredPageLength];
            Array.Copy(pageBytes, padded, Math.Min(pageBytes.Length, WiredPageLength));
            if (layer.IsBaseLayer)
            {
                bool hasFn = false;
                for (int slot = 0; slot < Hi98CLayout.MatrixSlotCount; slot++)
                    if (padded[slot * 4] == 0x0D) { hasFn = true; break; }
                if (!hasFn)
                    return new(false, $"拒绝恢复不含 Fn 键的「{layer.Title}」快照。");
            }

            if (backup != null)
            {
                var before = ReadWiredBlock(vid, pid, CmdWiredKeymapRead, layer.WiredSub, 0, WiredPageLength, true, out _, out _);
                if (before != null)
                {
                    backup.SaveBackup(new Hi98EepromBackupSnapshot
                    {
                        Reason = $"恢复前自动留档 ({snapshot.Reason})",
                        Transport = Hi98ConnectionTransport.UsbWired,
                        Layers = new Dictionary<int, byte[]> { [layer.RawValue] = before },
                    });
                    backup = null; // 每轮恢复仅存一次恢复前备份即可
                }
            }

            var req = BuildWiredFeatureRequest(CmdWiredKeymapWrite, layer.WiredSub, 1, 0, WiredPageLength, padded);
            if (!SendWiredReport(vid, pid, req, out _, out var err))
                return new(false, $"恢复「{layer.Title}」失败：{err}");
            Thread.Sleep(400);
        }

        if (snapshot.PerKeyRgb is { Length: >= CustomRgbLength } rgb)
        {
            var req = BuildWiredFeatureRequest(CmdWiredCustomRgbWrite, 0, 1, 0, CustomRgbLength, rgb[..CustomRgbLength]);
            SendWiredReport(vid, pid, req, out _, out _);
            Thread.Sleep(400);
        }

        if (snapshot.LedConfig is { Length: ConfigLength } led && Hi98LedConfigBlock.IsValidSignature(led))
        {
            var nextLed = new Hi98LedConfigBlock(led);
            if (nextLed.SleepTimerSeconds < 30)
                nextLed.SleepTimerSeconds = 300;
            var req = BuildWiredFeatureRequest(CmdWriteConfig, 0, 1, 0, ConfigLength, nextLed.RawBytes);
            if (!SendWiredReport(vid, pid, req, out _, out var err))
                return new(false, $"恢复灯光设置失败：{err}");
            Thread.Sleep(400);
        }

        return new(true, $"已恢复备份快照「{snapshot.Reason}」。");
    }

    public static bool SendLiveRgbFrame(string deviceId, IReadOnlyDictionary<int, Hi98RgbColor> colors)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return false;

        if (IsWired(vid, pid))
        {
            var req = BuildWiredLiveFrameReport(colors);
            return SendWiredReport(vid, pid, req, out _, out _);
        }

        if (pid == 0xFA07)
            return false;

        var packets = BuildWirelessLiveColorPackets(colors, maxReports: 9);
        return SendWirelessPackets(vid, pid, packets);
    }

    public static IReadOnlyList<Hi98InterfaceDiagnostic> InspectInterfaces(string deviceId)
    {
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return [];

        var list = new List<Hi98InterfaceDiagnostic>();
        if (OperatingSystem.IsMacOS())
        {
            foreach (var d in MacIOKitHid.Enumerate(vid, pid))
            {
                string status;
                using var conn = MacIOKitHid.MacHidConnection.TryOpen(d, out int ret);
                status = conn != null
                    ? "已打开"
                    : (ret == MacIOKitHid.kIOReturnNotPermitted ? "缺少输入监控权限 (0xE00002E2)" : $"打开失败 (0x{unchecked((uint)ret):X})");
                list.Add(new(d.VendorId, d.ProductId, d.PrimaryUsagePage, d.PrimaryUsage,
                    d.MaxInputReportSize, d.MaxOutputReportSize, d.MaxFeatureReportSize,
                    d.Transport, d.Product, status));
            }
            return list;
        }

        foreach (var d in HidSafe.EnumerateDevices(vid, pid))
        {
            var (up, u) = HidSafe.PrimaryUsage(d);
            using var ch = HidChannel.TryOpen(d);
            list.Add(new((ushort)d.VendorID, (ushort)d.ProductID, up, u,
                HidSafe.MaxInputLen(d), HidSafe.MaxOutputLen(d), HidSafe.MaxFeatureLen(d),
                "USB/HID", HidSafe.ProductName(d), ch != null ? "可用" : "只读/不可打开"));
        }
        return list;
    }

    // ── 编码与无线/有线分包组装 ───────────────────────────────────────────────

    internal static byte[] EncodePlanarCustomRgb(IReadOnlyDictionary<int, Hi98RgbColor> colors)
    {
        var bytes = new byte[CustomRgbLength];
        foreach (var (slot, c) in colors)
        {
            if (slot < 0 || slot >= Hi98CLayout.MatrixSlotCount) continue;
            bytes[slot] = c.R;
            bytes[Hi98CLayout.MatrixSlotCount + slot] = c.G;
            bytes[Hi98CLayout.MatrixSlotCount * 2 + slot] = c.B;
        }
        return bytes;
    }

    internal static Dictionary<int, Hi98RgbColor> DecodePlanarCustomRgb(ReadOnlySpan<byte> data)
    {
        var map = new Dictionary<int, Hi98RgbColor>();
        if (data.Length < CustomRgbLength) return map;
        for (int slot = 0; slot < Hi98CLayout.MatrixSlotCount; slot++)
        {
            byte r = data[slot];
            byte g = data[Hi98CLayout.MatrixSlotCount + slot];
            byte b = data[Hi98CLayout.MatrixSlotCount * 2 + slot];
            if (r != 0 || g != 0 || b != 0)
                map[slot] = new Hi98RgbColor(r, g, b);
        }
        return map;
    }

    internal static byte[] BuildWiredLiveFrameReport(IReadOnlyDictionary<int, Hi98RgbColor> colors)
    {
        var report = new byte[WiredReportLength];
        report[0] = WiredReportId;
        report[1] = CmdWiredLiveRgb;
        report[4] = 1;
        report[6] = (byte)(CustomRgbLength & 0xFF);
        report[7] = (byte)((CustomRgbLength >> 8) & 0xFF);
        foreach (var (slot, c) in colors)
        {
            if (slot < 0 || slot >= Hi98CLayout.MatrixSlotCount) continue;
            int off = 8 + slot * 3;
            report[off] = c.R;
            report[off + 1] = c.G;
            report[off + 2] = c.B;
        }
        return report;
    }

    internal static List<byte[]> BuildWirelessLiveColorPackets(IReadOnlyDictionary<int, Hi98RgbColor> colors, int maxReports = 9)
    {
        int maxBytes = Math.Min(WirelessLiveMaxFrameBytes, maxReports * 14);
        int step = 85;
        List<byte> serialized;

        while (true)
        {
            var groups = new Dictionary<Hi98RgbColor, List<byte>>();
            for (int slot = 0; slot < Hi98CLayout.MatrixSlotCount; slot++)
            {
                if (!colors.TryGetValue(slot, out var c) || c.IsBlack) continue;
                byte Quantize(byte v) => (byte)Math.Min(255, ((v + step / 2) / step) * step);
                var q = new Hi98RgbColor(Quantize(c.R), Quantize(c.G), Quantize(c.B));
                if (q.IsBlack)
                {
                    byte top = Math.Max(c.R, Math.Max(c.G, c.B));
                    byte boost = (byte)Math.Min(255, step);
                    q = new Hi98RgbColor(
                        c.R == top ? boost : (byte)0,
                        c.G == top ? boost : (byte)0,
                        c.B == top ? boost : (byte)0);
                }
                if (!groups.TryGetValue(q, out var list))
                {
                    list = [];
                    groups[q] = list;
                }
                list.Add((byte)slot);
            }

            if (groups.Count == 0)
            {
                serialized = [0, 0, 0, 1, 0];
                break;
            }

            var candidate = new List<byte>();
            foreach (var (color, slots) in groups.OrderByDescending(g => g.Value.Count))
            {
                candidate.Add(color.R);
                candidate.Add(color.G);
                candidate.Add(color.B);
                candidate.Add((byte)slots.Count);
                candidate.AddRange(slots);
            }

            if (candidate.Count <= maxBytes || step >= 255)
            {
                serialized = candidate.Count > maxBytes ? candidate.GetRange(0, maxBytes) : candidate;
                break;
            }
            step *= 2;
        }

        int total = (serialized.Count + 13) / 14;
        var packets = new List<byte[]>(total);
        for (int idx = 0; idx < total; idx++)
        {
            int start = idx * 14;
            int len = Math.Min(14, serialized.Count - start);
            var pkt = new byte[WirelessPacketLength];
            pkt[0] = WirelessReportId;
            pkt[1] = WirelessCmdLiveRgb;
            pkt[2] = (byte)(total & 0xFF);
            pkt[3] = (byte)(idx & 0xFF);
            pkt[4] = (byte)(((WirelessLiveSubKeys & 0x0F) << 4) | (len & 0x0F));
            for (int i = 0; i < len; i++)
                pkt[5 + i] = serialized[start + i];
            pkt[WirelessPacketLength - 1] = ComputeWirelessChecksum(pkt);
            packets.Add(pkt);
        }
        return packets;
    }

    internal static byte[]? AssembleWirelessBlock(
        IEnumerable<byte[]> packets,
        byte expectedCmd,
        int targetLength,
        int minRequiredBytes,
        bool verifyMagicTail)
    {
        var byIndex = new SortedDictionary<int, byte[]>();
        var fallbackSeq = new List<byte[]>();
        int expectedTotal = 0;

        foreach (var pkt in packets)
        {
            if (pkt == null || pkt.Length < WirelessPacketLength || pkt[1] != expectedCmd) continue;
            if (!ParseWirelessPacket(pkt, out var payload) || payload.Length == 0) continue;

            int total = pkt[2] & 0x7F;
            int idx = pkt[3];
            if (total > 0)
            {
                expectedTotal = Math.Max(expectedTotal, total);
                if (idx < total)
                    byIndex[idx] = payload;
            }
            else
            {
                fallbackSeq.Add(payload);
            }
        }

        if (expectedTotal > 0 && byIndex.Count < expectedTotal)
            return null;

        var raw = new List<byte>(targetLength);
        foreach (var chunk in expectedTotal > 0 ? (IEnumerable<byte[]>)byIndex.Values : fallbackSeq)
            raw.AddRange(chunk);

        if (raw.Count < minRequiredBytes)
            return null;

        var result = new byte[targetLength];
        Array.Copy(raw.ToArray(), result, Math.Min(raw.Count, targetLength));
        if (verifyMagicTail && !IsValidConfig(result))
            return null;
        return result;
    }

    // ── 跨平台 HID 传输抽象 ──────────────────────────────────────────────────

    private static byte[]? ReadWiredBlock(
        ushort vid, ushort pid, byte cmd, byte sub, byte packetIndex, int length,
        bool verifyMagicTail, out bool permissionDenied, out string? error)
    {
        permissionDenied = false;
        error = null;
        var req = BuildWiredFeatureRequest(cmd, sub, 1, packetIndex, length);

        if (OperatingSystem.IsMacOS())
        {
            var dev = FindWiredMacInterface(MacIOKitHid.Enumerate(vid, pid));
            if (dev == null)
            {
                error = "未找到 520B Feature 0x06 有线配置接口。";
                return null;
            }
            using var conn = MacIOKitHid.MacHidConnection.TryOpen(dev, out int openRet);
            if (conn == null)
            {
                permissionDenied = openRet == MacIOKitHid.kIOReturnNotPermitted;
                error = permissionDenied ? "缺少输入监控权限" : $"打开失败 (0x{unchecked((uint)openRet):X})";
                return null;
            }

            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0) Thread.Sleep(70);
                if (!conn.SetFeatureReport(WiredReportId, req)) continue;
                Thread.Sleep(25);
                var reply = conn.GetFeatureReport(WiredReportId, WiredReportLength);
                if (reply == null) continue;
                if (reply.Length >= 8 + length && reply[0] == WiredReportId && reply[1] == cmd
                    && reply[2] == sub && reply[5] == packetIndex)
                {
                    var slice = reply.AsSpan(8, length).ToArray();
                    if (!verifyMagicTail || (slice[^2] == ConfigTail0 && slice[^1] == ConfigTail1))
                        return slice;
                }
            }
            error = verifyMagicTail ? "回包缺少 0x5A 0xA5 签名" : "有线读取超时";
            return null;
        }

        var winDev = FindWiredInterface(HidSafe.EnumerateDevices(vid, pid));
        if (winDev == null)
        {
            error = "未找到 520B Feature 0x06 有线配置接口。";
            return null;
        }
        using var raw = RawHidFeature.TryOpen(winDev.DevicePath);
        if (raw == null)
        {
            error = "无法打开有线配置接口。";
            return null;
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) Thread.Sleep(70);
            if (!raw.SetFeature(req)) continue;
            Thread.Sleep(25);
            var reply = raw.GetFeature(WiredReportLength, WiredReportId);
            if (reply == null) continue;
            if (reply.Length >= 8 + length && reply[0] == WiredReportId && reply[1] == cmd
                && reply[2] == sub && reply[5] == packetIndex)
            {
                var slice = reply.AsSpan(8, length).ToArray();
                if (!verifyMagicTail || (slice[^2] == ConfigTail0 && slice[^1] == ConfigTail1))
                    return slice;
            }
        }
        error = verifyMagicTail ? "回包缺少 0x5A 0xA5 签名" : "有线读取超时";
        return null;
    }

    private static bool SendWiredReport(ushort vid, ushort pid, byte[] report, out bool permissionDenied, out string? error)
    {
        permissionDenied = false;
        error = null;

        if (OperatingSystem.IsMacOS())
        {
            var dev = FindWiredMacInterface(MacIOKitHid.Enumerate(vid, pid));
            if (dev == null) { error = "未找到有线配置接口"; return false; }
            using var conn = MacIOKitHid.MacHidConnection.TryOpen(dev, out int openRet);
            if (conn == null)
            {
                permissionDenied = openRet == MacIOKitHid.kIOReturnNotPermitted;
                error = permissionDenied ? "缺少输入监控权限" : "打开接口失败";
                return false;
            }
            bool ok = conn.SetFeatureReport(WiredReportId, report);
            if (!ok) error = "SetFeatureReport 返回失败";
            return ok;
        }

        var winDev = FindWiredInterface(HidSafe.EnumerateDevices(vid, pid));
        if (winDev == null) { error = "未找到有线配置接口"; return false; }
        using var raw = RawHidFeature.TryOpen(winDev.DevicePath);
        if (raw == null) { error = "打开接口失败"; return false; }
        bool winOk = raw.SetFeature(report);
        if (!winOk) error = "SetFeature 返回失败";
        return winOk;
    }

    private static byte[]? QueryWirelessBlock(
        ushort vid, ushort pid, byte[] request, byte expectedReplyCmd,
        int targetLength, int minRequiredBytes, bool verifyMagicTail,
        int timeoutMs, int retries, out bool permissionDenied, out string? error)
    {
        permissionDenied = false;
        error = null;

        if (OperatingSystem.IsMacOS())
        {
            foreach (var dev in SelectWirelessMacInterfaces(MacIOKitHid.Enumerate(vid, pid)))
            {
                using var conn = MacIOKitHid.MacHidConnection.TryOpen(dev, out int openRet);
                if (conn == null)
                {
                    if (openRet == MacIOKitHid.kIOReturnNotPermitted)
                        permissionDenied = true;
                    continue;
                }

                for (int attempt = 0; attempt < retries; attempt++)
                {
                    conn.DrainInputReports();
                    if (!conn.WriteOutputReport(WirelessReportId, request))
                        continue;

                    var byIndex = new Dictionary<int, byte[]>();
                    var collected = conn.CollectInputReports(
                        timeoutMs,
                        pkt => pkt.Length >= WirelessPacketLength && pkt[0] == WirelessReportId
                            && pkt[1] == expectedReplyCmd && ParseWirelessPacket(pkt),
                        pkts =>
                        {
                            var last = pkts[^1];
                            int total = last[2] & 0x7F;
                            if (total > 0)
                            {
                                byIndex[last[3]] = last;
                                return byIndex.Count >= total;
                            }
                            return false;
                        });

                    if (AssembleWirelessBlock(collected, expectedReplyCmd, targetLength, minRequiredBytes, verifyMagicTail) is { } block)
                        return block;
                    Thread.Sleep(35);
                }
            }
            return null;
        }

        var candidates = HidSafe.EnumerateDevices(vid, pid)
            .Where(d => HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00 && HidSafe.MaxOutputLen(d) >= 19)
            .OrderByDescending(d => HidSafe.PrimaryUsage(d) == (0xFF02, 0x0002) ? 1 : 0)
            .ToList();
        if (candidates.Count == 0)
            candidates = HidSafe.EnumerateDevices(vid, pid).ToList();

        foreach (var dev in candidates)
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null || ch.OutputLength < WirelessPacketLength) continue;

            for (int attempt = 0; attempt < retries; attempt++)
            {
                for (int i = 0; i < 4; i++)
                    if (ch.Read(5) == null) break;

                if (!ch.WriteOutput(request)) continue;

                var byIndex = new Dictionary<int, byte[]>();
                int expectedTotal = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    var reply = ch.Read(120);
                    if (reply == null) continue;
                    if (reply.Length < WirelessPacketLength || reply[0] != WirelessReportId
                        || reply[1] != expectedReplyCmd || !ParseWirelessPacket(reply))
                        continue;

                    int total = reply[2] & 0x7F;
                    if (total > 0)
                    {
                        expectedTotal = Math.Max(expectedTotal, total);
                        byIndex[reply[3]] = reply;
                        if (byIndex.Count >= expectedTotal) break;
                    }
                }

                if (AssembleWirelessBlock(byIndex.Values, expectedReplyCmd, targetLength, minRequiredBytes, verifyMagicTail) is { } block)
                    return block;
                Thread.Sleep(35);
            }
        }
        return null;
    }

    private static bool SendWirelessPackets(ushort vid, ushort pid, IReadOnlyList<byte[]> packets)
    {
        if (packets.Count == 0) return false;

        if (OperatingSystem.IsMacOS())
        {
            foreach (var dev in SelectWirelessMacInterfaces(MacIOKitHid.Enumerate(vid, pid)))
            {
                using var conn = MacIOKitHid.MacHidConnection.TryOpen(dev, out _);
                if (conn == null) continue;
                bool ok = true;
                foreach (var pkt in packets)
                {
                    if (!conn.WriteOutputReport(WirelessReportId, pkt)) { ok = false; break; }
                    Thread.Sleep(4);
                }
                if (ok) return true;
            }
            return false;
        }

        foreach (var dev in HidSafe.EnumerateDevices(vid, pid)
                     .Where(d => HidSafe.PrimaryUsage(d).UsagePage >= 0xFF00 && HidSafe.MaxOutputLen(d) >= 19))
        {
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;
            bool ok = true;
            foreach (var pkt in packets)
            {
                if (!ch.WriteOutput(pkt)) { ok = false; break; }
                Thread.Sleep(4);
            }
            if (ok) return true;
        }
        return false;
    }
}
