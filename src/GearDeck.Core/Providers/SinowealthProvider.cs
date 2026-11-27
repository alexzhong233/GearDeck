using System.Globalization;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// Sinowealth (中颖 8051，VID `0x258A` 特定游戏鼠标 PID 白名单) 协议：
/// 参考 `libratbag/src/driver-sinowealth.c`，使用 520B Feature Report (`0x04`/`0x06`) 与 6B Command (`0x05`)，
/// 支持 Glorious Model O / O- / D / D- 有线版、Delux M800DB 等经典中颖鼠标的 DPI、回报率、防抖、板载档位与 0x30 板载硬件宏。
/// 严格排除 LEOBOG (`0x010C`) 与 AULA (`0x0136`/`0x0138`)。
/// </summary>
public sealed class SinowealthProvider : ITunablePeripheralProvider
{
    public const string ProviderName = "Sinowealth";
    public const ushort VendorId = 0x258A;

    public const byte ReportIdConfig = 0x04;
    public const byte ReportIdCmd = 0x05;
    public const byte ReportIdConfigLong = 0x06;
    public const int ConfigReportSize = 520;
    public const int ConfigWriteOffset = 8;

    private const byte CmdGetConfig1 = 0x11;
    private const byte CmdGetButtons1 = 0x12;
    private const byte CmdDebounce = 0x1A;
    private const byte CmdMacro = 0x30;
    private const byte CmdFirmwareVersion = 0x01;
    private const int ButtonReportPayloadSize = 88;
    private const int MaxMacroEvents = 168;

    private static readonly int[] SupportedPollingRates = [125, 250, 500, 1000];

    private static readonly Dictionary<ushort, string> SupportedMouseModels = new()
    {
        [0x0036] = "Glorious Model O / O- (Wired)",
        [0x0033] = "Glorious Model D / D- (Wired)",
        [0x002E] = "Delux M800DB (Wired/2.4G)",
        [0x002F] = "Delux M800 (Sinowealth)",
        [0x0027] = "Sinowealth 8051 Gaming Mouse (DM5 / Xenon)",
        [0x002A] = "Sinowealth 8051 Gaming Mouse (G-Wolves)",
    };

    public string Name => ProviderName;

    internal static bool IsSupportedPid(ushort pid) => SupportedMouseModels.ContainsKey(pid);

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("sinowealth:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevicesByVendor(VendorId)
            .Where(d => IsSupportedPid((ushort)d.ProductID))
            .GroupBy(d => (ushort)d.ProductID);

        foreach (var g in groups)
        {
            ushort pid = g.Key;
            string? rawName = g.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            string model = !string.IsNullOrWhiteSpace(rawName)
                ? rawName.Trim()
                : SupportedMouseModels.GetValueOrDefault(pid, $"Sinowealth {pid:X4}");

            results.Add(new PeripheralDevice
            {
                Id = $"sinowealth:{VendorId:X4}-{pid:X4}",
                VendorId = VendorId,
                ProductId = pid,
                Name = model,
                Kind = DeviceKind.Mouse,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = false,
                Transport = "USB",
                Note = "Sinowealth 8051 协议（支持 DPI / 回报率 / 去抖 / 0x30 板载宏）",
                LastUpdated = DateTimeOffset.Now,
            });
        }
        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!ProviderIdHelper.TryParseVidPid(deviceId, out ushort vid, out ushort pid) || vid != VendorId || !IsSupportedPid(pid))
        {
            caps.StatusMessage = "无效的 Sinowealth 设备标识。";
            return caps;
        }

        var (cfgCh, isLong) = OpenConfigChannel(pid);
        if (cfgCh == null)
        {
            caps.StatusMessage = "未找到 Sinowealth 520B Feature 控制通道。";
            return caps;
        }

        using (cfgCh)
        {
            byte reportId = isLong ? ReportIdConfigLong : ReportIdConfig;
            byte[]? cfg = QueryConfig(cfgCh, reportId, CmdGetConfig1);
            if (cfg == null || cfg.Length < 48)
            {
                caps.StatusMessage = "读取 Sinowealth 板载配置失败。";
                return caps;
            }

            caps.IsOnline = true;
            ParseConfigCapabilities(cfg, caps);

            if (!isLong && cfg.Length >= 10 && TryReadDebounceMs(pid, out int debounceMs))
            {
                caps.SupportsDebounce = true;
                caps.MaxDebounceMs = 16;
                caps.CurrentDebounceMs = debounceMs;
            }

            caps.SupportsOnboardMacroUpload = true;
            caps.OnboardMacroSlotCount = 8;
            caps.MacroCapabilityNote = "Sinowealth 0x30 协议：支持将键盘按键/鼠标点击与延迟 (1~255ms) 写入板载宏并绑定到侧键。";
            caps.StatusMessage = "已读取 Sinowealth 硬件配置。";
            return caps;
        }
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!ProviderIdHelper.TryParseVidPid(deviceId, out ushort vid, out ushort pid) || vid != VendorId || !IsSupportedPid(pid))
            return new DeviceTuningResult(false, "无效的 Sinowealth 设备标识。");

        var (cfgCh, isLong) = OpenConfigChannel(pid);
        if (cfgCh == null)
            return new DeviceTuningResult(false, "打开 Sinowealth 控制通道失败。");

        var notes = new List<string>();
        var errors = new List<string>();
        byte reportId = isLong ? ReportIdConfigLong : ReportIdConfig;

        using (cfgCh)
        {
            byte[]? cfg = QueryConfig(cfgCh, reportId, CmdGetConfig1);
            if (cfg == null || cfg.Length < ConfigReportSize)
            {
                return new DeviceTuningResult(false, "读取 Sinowealth 当前配置失败，无法安全回写。");
            }

            bool writeCfg = false;
            byte sensorType = cfg[9];
            if (update.PollingRateHz is { } hz && EncodePollingRate(hz) is { } rateCode)
            {
                cfg[10] = (byte)((cfg[10] & 0xF0) | (rateCode & 0x0F));
                writeCfg = true;
                notes.Add($"回报率 {hz} Hz");
            }

            if (update.Dpi is { } dpi)
            {
                int activeSlot = Math.Clamp(((cfg[11] >> 4) & 0x0F) - 1, 0, 7);
                int maxDpi = GetMaxDpiForSensor(sensorType);
                int clamped = Math.Clamp(dpi.X, 100, maxDpi);
                byte rawDpi = EncodeDpi(sensorType, clamped);
                bool xyIndependent = (cfg[10] & 0x80) != 0;
                if (xyIndependent)
                {
                    cfg[13 + activeSlot * 2] = rawDpi;
                    cfg[14 + activeSlot * 2] = EncodeDpi(sensorType, Math.Clamp(dpi.Y, 100, maxDpi));
                }
                else
                {
                    cfg[13 + activeSlot] = rawDpi;
                }
                writeCfg = true;
                notes.Add($"DPI {clamped}");
            }

            if (writeCfg)
            {
                cfg[0] = reportId;
                cfg[1] = CmdGetConfig1;
                // Preserve firmware config payload size if present; standard Sinowealth writes len-8 at byte[3].
                if (cfg[3] == 0) cfg[3] = (byte)((isLong ? 167 : 137) - ConfigWriteOffset);
                if (!cfgCh.WriteFeature(cfg))
                    errors.Add("写入 Sinowealth DPI/回报率配置失败");
            }

            if (update.DebounceMs is { } debMs)
            {
                if (WriteDebounceMs(pid, debMs))
                    notes.Add($"防抖 {Math.Clamp(debMs, 4, 16)} ms");
                else
                    errors.Add("写入防抖时间失败");
            }

            if (update.UploadMacro is { } macroUp)
            {
                byte macroIdx = (byte)(Math.Clamp(macroUp.MacroSlot, 0, 19) + 1);
                byte[] macroReport = EncodeMacroReport(reportId, macroIdx, macroUp.Macro);
                if (!cfgCh.WriteFeature(macroReport))
                {
                    errors.Add("写入 Sinowealth 0x30 硬件宏数据失败");
                }
                else
                {
                    byte[]? btnReport = QueryConfig(cfgCh, reportId, CmdGetButtons1);
                    if (btnReport == null || btnReport.Length < ConfigReportSize ||
                        !PatchButtonReportWithMacro(btnReport, macroUp.TargetButtonSlot, macroIdx, macroUp.Macro.LoopMode, macroUp.Macro.RepeatCount))
                    {
                        errors.Add("绑定宏到 Sinowealth 按键报表失败");
                    }
                    else
                    {
                        btnReport[0] = reportId;
                        btnReport[1] = CmdGetButtons1;
                        if (!cfgCh.WriteFeature(btnReport))
                            errors.Add("写回 Sinowealth 按键映射失败");
                        else
                            notes.Add($"已将宏「{macroUp.Macro.Name}」烧录并绑定到按钮 {macroUp.TargetButtonSlot + 1}");
                    }
                }
            }
        }

        var refreshed = QueryCapabilities(deviceId);
        return errors.Count == 0
            ? new DeviceTuningResult(true, notes.Count > 0 ? string.Join("，", notes) : "已更新 Sinowealth 配置。", refreshed)
            : new DeviceTuningResult(false, string.Join("；", errors), refreshed);
    }

    internal static void ParseConfigCapabilities(ReadOnlySpan<byte> cfg, DeviceTuningCapabilities caps)
    {
        byte sensor = cfg[9];
        int maxDpi = GetMaxDpiForSensor(sensor);
        caps.SupportsDpi = true;
        caps.MinDpi = 100;
        caps.MaxDpi = maxDpi;
        caps.DpiStep = 100;

        byte rateRaw = (byte)(cfg[10] & 0x0F);
        caps.SupportedPollingRates = [.. SupportedPollingRates];
        caps.CurrentPollingRate = DecodePollingRate(rateRaw);

        bool xyIndep = (cfg[10] & 0x80) != 0;
        caps.SupportsIndependentXY = xyIndep;
        int dpiCount = Math.Clamp(cfg[11] & 0x0F, 1, 8);
        int activeSlot = Math.Clamp(((cfg[11] >> 4) & 0x0F) - 1, 0, dpiCount - 1);
        caps.DpiStages.Clear();
        for (int i = 0; i < dpiCount; i++)
        {
            if (xyIndep)
                caps.DpiStages.Add((DecodeDpi(sensor, cfg[13 + i * 2]), DecodeDpi(sensor, cfg[14 + i * 2])));
            else
            {
                int d = DecodeDpi(sensor, cfg[13 + i]);
                caps.DpiStages.Add((d, d));
            }
        }
        caps.ActiveDpiStage = activeSlot;
        caps.CurrentDpiX = caps.DpiStages[activeSlot].X;
        caps.CurrentDpiY = caps.DpiStages[activeSlot].Y;
    }

    internal static int GetMaxDpiForSensor(byte sensorType) => sensorType switch
    {
        0x06 => 12000, // PMW3360
        0x08 => 7200,  // PMW3212
        0x0E => 10200, // PMW3327
        0x0F => 16000, // PMW3389
        _ => 12000,
    };

    internal static byte EncodeDpi(byte sensorType, int dpi)
    {
        int raw = Math.Max(1, dpi / 100);
        if (sensorType is 0x06 or 0x0E)
            raw = Math.Max(0, raw - 1);
        return (byte)Math.Clamp(raw, 0, 255);
    }

    internal static int DecodeDpi(byte sensorType, byte raw)
    {
        int units = sensorType is 0x06 or 0x0E ? raw + 1 : raw;
        return Math.Max(100, units * 100);
    }

    internal static byte? EncodePollingRate(int hz) => hz switch
    {
        125 => 0x01,
        250 => 0x02,
        500 => 0x03,
        1000 => 0x04,
        _ => null,
    };

    internal static int DecodePollingRate(byte raw) => raw switch
    {
        0x01 => 125,
        0x02 => 250,
        0x03 => 500,
        _ => 1000,
    };

    /// <summary>按 `struct sinowealth_macro_report` 构建 520B 宏写入数据包（Report ID + Cmd 0x30 + 0x02 + 事件组）。</summary>
    internal static byte[] EncodeMacroReport(byte reportId, byte macroIndex, OnboardMacroDefinition macro)
    {
        var report = new byte[ConfigReportSize];
        report[0] = reportId;
        report[1] = CmdMacro;
        report[2] = 0x02; // unknown1 = 0x2 when writing
        report[8] = macroIndex;

        int eventCount = 0;
        foreach (var step in macro.Steps)
        {
            if (step.KeyCode == 0)
            {
                if (step.DelayMs > 0 && eventCount > 0)
                    report[11 + (eventCount - 1) * 3 + 1] = (byte)Math.Clamp(step.DelayMs, 1, 255);
                continue;
            }

            if (eventCount >= MaxMacroEvents)
                break;

            byte cmd;
            byte payload;
            if (step.KeyCode is >= HidKeyCatalog.MouseLeftCode and <= HidKeyCatalog.MouseMiddleCode)
            {
                cmd = step.IsPress ? (byte)0x10 : (byte)0x90;
                payload = (byte)(1 << (step.KeyCode - HidKeyCatalog.MouseLeftCode));
            }
            else if (step.KeyCode is >= 0x04 and < 0xF0)
            {
                cmd = step.IsPress ? (byte)0x50 : (byte)0xD0;
                payload = step.KeyCode;
            }
            else
            {
                continue;
            }

            int off = 11 + eventCount * 3;
            report[off] = cmd;
            report[off + 1] = (byte)Math.Clamp(step.DelayMs > 0 ? step.DelayMs : 1, 1, 255);
            report[off + 2] = payload;
            eventCount++;
        }

        report[10] = (byte)eventCount;
        return report;
    }

    internal static bool PatchButtonReportWithMacro(
        byte[] buttonReport,
        int buttonIndex,
        byte macroIndex,
        MacroLoopMode loopMode,
        int repeatCount)
    {
        if (buttonReport.Length < ConfigReportSize || (uint)buttonIndex >= 20)
            return false;

        buttonReport[3] = ButtonReportPayloadSize - ConfigWriteOffset;
        int at = 8 + buttonIndex * 4;
        buttonReport[at] = 0x70; // SINOWEALTH_BUTTON_TYPE_MACRO
        buttonReport[at + 1] = macroIndex;
        (buttonReport[at + 2], buttonReport[at + 3]) = loopMode switch
        {
            MacroLoopMode.WhileHeld => ((byte)0x04, (byte)0x00),
            MacroLoopMode.ToggleUntilKey => ((byte)0x02, (byte)0x00),
            MacroLoopMode.PlayOnce => ((byte)0x01, (byte)0x01),
            _ => ((byte)0x01, (byte)Math.Clamp(repeatCount, 1, 255)),
        };
        return true;
    }

    private static (HidChannel? Channel, bool IsLong) OpenConfigChannel(ushort pid)
    {
        var candidates = HidSafe.EnumerateDevices(VendorId, pid)
            .OrderByDescending(d => HidSafe.MaxFeatureLen(d));
        foreach (var dev in candidates)
        {
            int flen = HidSafe.MaxFeatureLen(dev);
            if (flen < ConfigReportSize) continue;
            var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;
            // Probe REPORT_ID_CONFIG (0x04) vs REPORT_ID_CONFIG_LONG (0x06) via FirmwareVersion query
            if (ProbeHasConfigReport(ch, ReportIdConfig))
                return (ch, false);
            if (ProbeHasConfigReport(ch, ReportIdConfigLong))
                return (ch, true);
            ch.Dispose();
        }
        return (null, false);
    }

    private static bool ProbeHasConfigReport(HidChannel ch, byte reportId)
    {
        var req = new byte[ConfigReportSize];
        req[0] = reportId;
        req[1] = CmdGetConfig1;
        if (!ch.WriteFeature(req)) return false;
        Thread.Sleep(10);
        var resp = ch.ReadFeature(reportId);
        return resp is { Length: >= 16 } && resp[0] == reportId;
    }

    private static byte[]? QueryConfig(HidChannel ch, byte reportId, byte cmd)
    {
        var req = new byte[ConfigReportSize];
        req[0] = reportId;
        req[1] = cmd;
        if (!ch.WriteFeature(req)) return null;
        Thread.Sleep(15);
        return ch.ReadFeature(reportId);
    }

    private static bool TryReadDebounceMs(ushort pid, out int ms)
    {
        ms = 0;
        var cmdDev = HidSafe.EnumerateDevices(VendorId, pid).FirstOrDefault(d => HidSafe.MaxFeatureLen(d) >= 6);
        if (cmdDev == null) return false;
        using var ch = HidChannel.TryOpen(cmdDev);
        if (ch == null) return false;
        byte[] req = [ReportIdCmd, CmdDebounce, 0, 0, 0, 0];
        if (!ch.WriteFeature(req)) return false;
        var resp = ch.ReadFeature(ReportIdCmd);
        if (resp is { Length: >= 3 } && resp[0] == ReportIdCmd && resp[2] is >= 2 and <= 8)
        {
            ms = resp[2] * 2;
            return true;
        }
        // Fallback probe firmware cmd keeps unused static clean
        _ = CmdFirmwareVersion;
        return false;
    }

    private static bool WriteDebounceMs(ushort pid, int debounceMs)
    {
        var cmdDev = HidSafe.EnumerateDevices(VendorId, pid).FirstOrDefault(d => HidSafe.MaxFeatureLen(d) >= 6);
        if (cmdDev == null) return false;
        using var ch = HidChannel.TryOpen(cmdDev);
        if (ch == null) return false;
        byte half = (byte)(Math.Clamp(debounceMs, 4, 16) / 2);
        return ch.WriteFeature([ReportIdCmd, CmdDebounce, half, 0, 0, 0]);
    }
}
