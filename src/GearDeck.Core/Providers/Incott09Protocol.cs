using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// Incott / 原相 0x09 协议（9 字节 Feature Report 0x09 + 320 字节原生硬件宏 10 分块写入）：
/// 支持独立 6 档 X/Y DPI、回报率 125~8000 Hz、LP/Corded/HP 三档模式、0.7/1/2mm LOD、
/// 移动同步/直线修正/波纹修正、防抖/休眠，以及鼠标原生板载 320B 多步硬件宏录制写入。
/// </summary>
internal static class Incott09Protocol
{
    public const byte ReportId = 0x09;
    public const int MacroBufferBytes = 320;
    public const int MacroChunkBytes = 32;
    public const int MacroChunkCount = 10;
    public const int MacroMaxSteps = 71;

    private static readonly (string Label, uint Code)[] ButtonActions =
    [
        ("左键点击", 0x00F00001u),
        ("右键点击", 0x00F10001u),
        ("中键点击", 0x00F20001u),
        ("前进", 0x00F40001u),
        ("后退", 0x00F30001u),
        ("DPI 循环", 0x00030007u),
        ("DPI +", 0x00010007u),
        ("DPI -", 0x00020007u),
        ("连发开火", 0x0218F00Au),
        ("切换配置", 0x0000F10Au),
        ("播放/暂停", 0x00CD0003u),
        ("音量 +", 0x00E90003u),
        ("音量 -", 0x00EA0003u),
        ("静音", 0x00E20003u),
        ("禁用按键", 0x00000000u),
    ];

    /// <summary>按 `juji_to_hw()` 构建 320 字节鼠标原生板载硬件宏缓冲区。</summary>
    public static byte[] EncodeMacroBuffer(int bufferSlot, OnboardMacroDefinition macro, uint uid = 1)
    {
        int slot = Math.Clamp(bufferSlot, 0, 9);
        int steps = Math.Min(macro.Steps.Count, MacroMaxSteps);
        var buf = new byte[MacroBufferBytes];
        buf[0] = (byte)slot;
        buf[1] = macro.LoopMode switch
        {
            MacroLoopMode.WhileHeld => 0,
            MacroLoopMode.ToggleUntilKey => 1,
            _ => 2,
        };
        ushort cycles = (ushort)Math.Clamp(macro.RepeatCount, 1, 65535);
        buf[2] = (byte)(cycles & 0xFF);
        buf[3] = (byte)(cycles >> 8);

        for (int i = 0; i < steps; i++)
        {
            var s = macro.Steps[i];
            int at = 4 + i * 4;
            buf[at] = (byte)((s.IsPress ? 0x00 : 0x80) | 0x01);
            buf[at + 1] = s.KeyCode;
            ushort ms = (ushort)Math.Clamp(s.DelayMs, 0, 65535);
            buf[at + 2] = (byte)(ms & 0xFF);
            buf[at + 3] = (byte)(ms >> 8);
        }

        // ASCII "Macro" + (slot+1) at offset 288..293
        buf[288] = (byte)'M';
        buf[289] = (byte)'a';
        buf[290] = (byte)'c';
        buf[291] = (byte)'r';
        buf[292] = (byte)'o';
        buf[293] = (byte)('1' + slot);

        int size = (steps + 1) * 4 + 128;
        buf[304] = (byte)(size & 0xFF);
        buf[305] = (byte)((size >> 8) & 0xFF);
        buf[306] = (byte)((size >> 16) & 0xFF);
        buf[307] = (byte)((size >> 24) & 0xFF);
        buf[308] = 16;
        buf[310] = 232;
        buf[311] = 232;
        buf[312] = (byte)(uid & 0xFF);
        buf[313] = (byte)((uid >> 8) & 0xFF);
        buf[314] = (byte)((uid >> 16) & 0xFF);
        buf[315] = (byte)((uid >> 24) & 0xFF);
        int len2 = steps * 2;
        buf[316] = (byte)(len2 & 0xFF);
        buf[317] = (byte)(len2 >> 8);
        buf[318] = (byte)steps;
        return buf;
    }

    public static bool UploadMacroAndBindButton(RawHidFeature feat, HidChannel outCh, int macroSlot, int targetButtonWireIdx, OnboardMacroDefinition macro)
    {
        byte[] buffer320 = EncodeMacroBuffer(macroSlot, macro, (uint)(macroSlot + 1));
        for (int chunk = 0; chunk < MacroChunkCount; chunk++)
        {
            byte[] header = [ReportId, 0x07, MacroChunkCount, (byte)chunk, MacroChunkBytes, (byte)macroSlot, 0, 0, 0];
            if (!feat.SetFeature(header)) return false;
            Thread.Sleep(80);
            var pkt = new byte[Math.Max(outCh.OutputLength, MacroChunkBytes + 1)];
            pkt[0] = ReportId;
            Array.Copy(buffer320, chunk * MacroChunkBytes, pkt, 1, MacroChunkBytes);
            if (!outCh.WriteOutput(pkt)) return false;
            Thread.Sleep(25);
        }

        uint bindCode = ((uint)Math.Clamp(macroSlot, 0, 9) << 16) | 0x0009u;
        byte[] bindFrame =
        [
            ReportId, 0x06, (byte)Math.Clamp(targetButtonWireIdx, 0, 5),
            (byte)(bindCode & 0xFF), (byte)((bindCode >> 8) & 0xFF),
            (byte)((bindCode >> 16) & 0xFF), (byte)((bindCode >> 24) & 0xFF),
            0, 0
        ];
        return feat.SetFeature(bindFrame);
    }

    public static byte[]? QueryFeature09(RawHidFeature raw, byte cmd, byte sub = 0, byte arg2 = 0)
    {
        byte[] req = [ReportId, cmd, sub, arg2, 0, 0, 0, 0, 0];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!raw.SetFeature(req)) continue;
            Thread.Sleep(25);
            var rep = raw.GetFeature(9, ReportId);
            if (rep != null && rep.Length >= 4 && rep[0] == ReportId && rep[1] == cmd)
                return rep;
        }
        return null;
    }

    public static DeviceTuningCapabilities QueryCapabilities(string deviceId, IEnumerable<HidDevice> interfaces)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        var cfgDev = interfaces.FirstOrDefault(d => HidSafe.MaxFeatureLen(d) >= 9 && HidReportInspector.GetReportIds(d).FeatureIds.Contains(ReportId));
        if (cfgDev == null) return caps;

        using var raw = RawHidFeature.TryOpen(cfgDev.DevicePath);
        if (raw == null) return caps;

        var id = QueryFeature09(raw, 0x8F);
        if (id == null || id[2] != 0x01) return caps;

        caps.IsOnline = true;
        bool isPro = id.Length > 6 && (id[5] == 0xF1 || id[6] == 0xF1);
        int maxDpi = isPro ? 45000 : 32000;

        var cyc = QueryFeature09(raw, 0x83, 0x06);
        if (cyc != null && cyc[2] is >= 1 and <= 6 && cyc[3] < cyc[2])
        {
            caps.SupportsDpi = true;
            caps.SupportsIndependentXY = true;
            caps.SupportsDpiStagesWrite = true;
            caps.MinDpi = 50;
            caps.MaxDpi = maxDpi;
            caps.DpiStep = 50;
            int count = cyc[2];
            caps.ActiveDpiStage = cyc[3];
            for (int stg = 0; stg < count; stg++)
            {
                var rx = QueryFeature09(raw, 0x82, (byte)stg, 1);
                var ry = QueryFeature09(raw, 0x82, (byte)stg, 2);
                int x = rx is { Length: >= 5 } ? ((rx[3] | (rx[4] << 8)) + 1) * 50 : 800;
                int y = ry is { Length: >= 5 } ? ((ry[3] | (ry[4] << 8)) + 1) * 50 : x;
                caps.DpiStages.Add((x, y));
            }
            if (caps.DpiStages.Count > 0)
            {
                caps.CurrentDpiX = caps.DpiStages[caps.ActiveDpiStage].X;
                caps.CurrentDpiY = caps.DpiStages[caps.ActiveDpiStage].Y;
            }
        }

        var poll = QueryFeature09(raw, 0x81);
        if (poll != null)
        {
            int[] map = [1000, 500, 250, 125, 8000, 4000, 2000];
            caps.SupportedPollingRates = [125, 250, 500, 1000, 2000, 4000, 8000];
            caps.CurrentPollingRate = poll[2] < map.Length ? map[poll[2]] : 1000;
        }

        var perf = QueryFeature09(raw, 0x84, 0x04);
        if (perf != null && perf[3] <= 2)
        {
            caps.SupportedPerformanceModes =
            [
                new(0, "低功耗 (LP)", "长续航标准采样"),
                new(1, "有线级响应 (Corded)", "强化追踪与稳定率"),
                new(2, "高性能竞技 (HP)", "满血传感器高频追踪"),
            ];
            caps.CurrentPerformanceMode = perf[3];
        }

        var lod = QueryFeature09(raw, 0x84, 0x01);
        if (lod != null)
        {
            caps.SupportedLodOptions = [new(2, "0.7 mm (低)"), new(0, "1.0 mm (中)"), new(1, "2.0 mm (高)")];
            caps.CurrentLod = lod[3] <= 2 ? lod[3] : 0;
        }

        var ms = QueryFeature09(raw, 0x84, 0x03);
        if (ms != null) { caps.SupportsMotionSync = true; caps.MotionSync = ms[3] == 1; }
        var asnap = QueryFeature09(raw, 0x84, 0x05);
        if (asnap != null) { caps.SupportsAngleSnapping = true; caps.AngleSnapping = asnap[3] == 1; }
        var rip = QueryFeature09(raw, 0x84, 0x02);
        if (rip != null) { caps.SupportsRippleControl = true; caps.RippleControl = rip[3] == 1; }

        var deb = QueryFeature09(raw, 0x85, 0x01);
        if (deb != null) { caps.SupportsDebounce = true; caps.MaxDebounceMs = 30; caps.CurrentDebounceMs = Math.Clamp((int)deb[3], 0, 30); }
        var slp = QueryFeature09(raw, 0x85, 0x03);
        if (slp != null && slp.Length >= 5)
        {
            caps.SupportsSleepTimeout = true;
            caps.MinSleepSeconds = 10;
            caps.MaxSleepSeconds = 900;
            caps.SupportedSleepSeconds = [10, 30, 60, 120, 300, 600, 900];
            caps.CurrentSleepSeconds = slp[3] | (slp[4] << 8);
        }

        string[] dispNames = ["左键 (Left)", "右键 (Right)", "中键 (Middle)", "前进键 (Forward)", "后退键 (Back)", "DPI 键"];
        int[] wireMatrix = [0, 1, 2, 4, 3, 5];
        var avail = ButtonActions.Select(b => b.Label).ToList();
        for (int i = 0; i < 6; i++)
        {
            var bRep = QueryFeature09(raw, 0x86, (byte)wireMatrix[i]);
            string actName = "默认";
            if (bRep is { Length: >= 6 })
            {
                uint code = (uint)(bRep[3] | (bRep[4] << 8) | (bRep[5] << 16) | ((bRep.Length > 6 ? bRep[6] : 0) << 24));
                actName = DecodeButtonCode(code);
            }
            caps.ButtonBindings.Add(new ButtonBindingSlot(wireMatrix[i], dispNames[i], actName, avail, IsReadOnly: i == 0));
        }

        caps.SupportsOnboardMacroUpload = true;
        caps.OnboardMacroSlotCount = 10;
        caps.MacroCapabilityNote = "原生支持 10 组 320B 板载硬件宏直写与侧键绑定！";
        caps.StatusMessage = "已通过 0x09 协议读取全部硬件参数与板载宏能力。";
        return caps;
    }

    public static DeviceTuningResult ApplySettings(string deviceId, IEnumerable<HidDevice> interfaces, DeviceTuningUpdate update)
    {
        var list = interfaces.ToList();
        var cfgDev = list.FirstOrDefault(d => HidSafe.MaxFeatureLen(d) >= 9 && HidReportInspector.GetReportIds(d).FeatureIds.Contains(ReportId));
        if (cfgDev == null)
            return new DeviceTuningResult(false, "未找到 0x09 控制接口。");

        using var raw = RawHidFeature.TryOpen(cfgDev.DevicePath);
        if (raw == null)
            return new DeviceTuningResult(false, "打开 0x09 通道失败。");

        if (update.UploadMacro is { } up)
        {
            var outDev = list.FirstOrDefault(d => HidSafe.MaxOutputLen(d) >= 33) ?? cfgDev;
            using var outCh = HidChannel.TryOpen(outDev);
            if (outCh == null)
                return new DeviceTuningResult(false, "无法打开 32B Output 通道以写入板载宏。");
            bool ok = UploadMacroAndBindButton(raw, outCh, up.MacroSlot, up.TargetButtonSlot, up.Macro);
            var refreshedMacro = QueryCapabilities(deviceId, list);
            return new DeviceTuningResult(ok, ok ? $"已将宏「{up.Macro.Name}」烧录至鼠标板载槽位 {up.MacroSlot + 1}！" : "写入板载宏失败。", refreshedMacro);
        }

        if (update.PollingRateHz is { } hz)
        {
            byte code = hz switch { 1000 => 0, 500 => 1, 250 => 2, 125 => 3, 8000 => 4, 4000 => 5, 2000 => 6, _ => 0 };
            raw.SetFeature([ReportId, 0x01, code, 0, 0, 0, 0, 0, 0]);
        }
        if (update.Dpi is { } dpi)
        {
            int idx = Math.Clamp(update.ActiveDpiStage ?? 0, 0, 5);
            int wx = Math.Clamp((int)Math.Round(dpi.X / 50.0) - 1, 0, 1023);
            int wy = Math.Clamp((int)Math.Round(dpi.Y / 50.0) - 1, 0, 1023);
            raw.SetFeature([ReportId, 0x02, (byte)idx, (byte)(wx & 0xFF), (byte)(wx >> 8), 0, 0, 0, 1]);
            raw.SetFeature([ReportId, 0x02, (byte)idx, (byte)(wy & 0xFF), (byte)(wy >> 8), 0, 0, 0, 2]);
        }
        if (update.ActiveDpiStage is { } act)
        {
            var cyc = QueryFeature09(raw, 0x83, 0x06);
            int cnt = cyc != null && cyc[2] is >= 1 and <= 6 ? cyc[2] : 6;
            raw.SetFeature([ReportId, 0x03, (byte)cnt, (byte)Math.Clamp(act, 0, cnt - 1), 0, 0, 0, 0, 0]);
        }
        if (update.PerformanceMode is { } pm)
            raw.SetFeature([ReportId, 0x04, 0x04, (byte)Math.Clamp(pm, 0, 2), 0, 0, 0, 0, 0]);
        if (update.LodCode is { } lod)
            raw.SetFeature([ReportId, 0x04, 0x01, (byte)Math.Clamp(lod, 0, 2), 0, 0, 0, 0, 0]);
        if (update.MotionSync is { } ms)
            raw.SetFeature([ReportId, 0x04, 0x03, (byte)(ms ? 1 : 0), 0, 0, 0, 0, 0]);
        if (update.AngleSnapping is { } snap)
            raw.SetFeature([ReportId, 0x04, 0x05, (byte)(snap ? 1 : 0), 0, 0, 0, 0, 0]);
        if (update.RippleControl is { } rip)
            raw.SetFeature([ReportId, 0x04, 0x02, (byte)(rip ? 1 : 0), 0, 0, 0, 0, 0]);
        if (update.DebounceMs is { } deb)
            raw.SetFeature([ReportId, 0x05, 0x01, (byte)Math.Clamp(deb, 0, 30), 0, 0, 0, 0, 0]);
        if (update.SleepSeconds is { } slp)
        {
            int s = Math.Clamp(slp, 10, 900);
            raw.SetFeature([ReportId, 0x05, 0x03, (byte)(s & 0xFF), (byte)(s >> 8), 0, 0, 0, 0]);
        }
        if (update.ButtonBinding is { } bind && bind.SlotIndex is >= 1 and <= 5)
        {
            foreach (var (lbl, code) in ButtonActions)
            {
                if (lbl == bind.Action)
                {
                    raw.SetFeature([ReportId, 0x06, (byte)bind.SlotIndex,
                        (byte)(code & 0xFF), (byte)((code >> 8) & 0xFF),
                        (byte)((code >> 16) & 0xFF), (byte)((code >> 24) & 0xFF), 0, 0]);
                    break;
                }
            }
        }

        Thread.Sleep(40);
        var refreshed = QueryCapabilities(deviceId, list);
        return new DeviceTuningResult(true, "已写入 0x09 硬件参数。", refreshed);
    }

    private static string DecodeButtonCode(uint code)
    {
        foreach (var (lbl, val) in ButtonActions)
            if (val == code) return lbl;
        if ((code & 0xFFFF) == 9)
            return $"板载宏 #{(code >> 16) + 1}";
        return $"自定义 (0x{code:X8})";
    }
}
