using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// CompX / PixArt 65 字节 Page/Command Feature 协议：
/// 覆盖 万灵 WLmouse (0x36A7, Beast X V2/Beast G 8K)、兰族 Lamzu (0x37B0 冥神 Inca 8K / Paro Aurora、0x373E Maya X) 及兼容 CompX 设备。
/// </summary>
internal static class CompXPageProtocol
{
    public const byte ReportId = 0x00;
    public const int PacketLength = 64;
    public const byte StatusOk = 0xA1;
    public const byte StatusUnsupported = 0xA2;

    public const byte TargetDongle = 0x00;
    public const byte TargetPairing = 0x01;
    public const byte TargetMouse = 0x02;

    public const byte PageDevice = 0x00;
    public const byte PageProfile = 0x01;
    public const byte PageButtons = 0x03;

    private static readonly (byte Code, int Hz)[] PollingRateTable =
    [
        (0x08, 125),
        (0x04, 250),
        (0x02, 500),
        (0x01, 1000),
        (0x10, 1000),
        (0x20, 2000),
        (0x40, 4000),
        (0x80, 8000),
    ];

    private static readonly (int Code, string Label)[] LodTable =
    [
        (0x87, "0.7 mm (低)"),
        (0x01, "1.0 mm (中)"),
        (0x02, "2.0 mm (高)"),
    ];

    private static readonly string[] PhysicalButtonNames = ["左键 (Left)", "右键 (Right)", "中键 (Middle)", "后退键 (Back)", "前进键 (Forward)"];

    private static readonly (string Label, byte FnId, byte[] Data)[] StandardButtonActions =
    [
        ("左键点击", 1, [1]),
        ("右键点击", 1, [2]),
        ("中键点击", 1, [3]),
        ("后退", 1, [4]),
        ("前进", 1, [5]),
        ("滚轮向上", 1, [0x10]),
        ("滚轮向下", 1, [0x11]),
        ("DPI 循环", 7, [6]),
        ("DPI +", 7, [1]),
        ("DPI -", 7, [2]),
        ("禁用按键", 0, []),
        ("播放/暂停", 5, [0x00, 0xCD]),
        ("音量 +", 5, [0x00, 0xE9]),
        ("音量 -", 5, [0x00, 0xEA]),
        ("静音", 5, [0x00, 0xE2]),
    ];

    public sealed record ExchangeResult(bool Ok, bool Unsupported, byte[] Payload);

    public static byte[] BuildRequest(byte target, byte page, byte command, ReadOnlySpan<byte> args, int lengthOverride = -1)
    {
        var pkt = new byte[1 + PacketLength]; // Report ID 0 + 64-byte CompX packet
        pkt[0] = ReportId;
        pkt[1] = 0x00; // status=request
        pkt[2] = 0x00;
        pkt[3] = target;
        pkt[4] = (byte)(lengthOverride >= 0 ? lengthOverride : Math.Min(args.Length, PacketLength - 6));
        pkt[5] = page;
        pkt[6] = command;
        if (!args.IsEmpty)
            args[..Math.Min(args.Length, PacketLength - 6)].CopyTo(pkt.AsSpan(7));
        return pkt;
    }

    public static ExchangeResult TryDecodeReply(byte[] reply, byte expectedPage, byte expectedCommand)
    {
        foreach (int o in new[] { 1, 2, 0 })
        {
            if (o + 6 > reply.Length) continue;
            if (reply[o + 4] != expectedPage || reply[o + 5] != expectedCommand) continue;
            byte status = reply[o];
            if (status == StatusUnsupported)
                return new ExchangeResult(false, true, []);
            if (status != StatusOk) continue;
            int declaredLen = reply[o + 3];
            int avail = reply.Length - (o + 6);
            int len = Math.Clamp(declaredLen, 0, Math.Max(0, avail));
            return new ExchangeResult(true, false, reply.AsSpan(o + 6, len).ToArray());
        }
        return new ExchangeResult(false, false, []);
    }

    public static List<(int X, int Y)> DecodeDpiStages(ReadOnlySpan<byte> payload)
    {
        var list = new List<(int X, int Y)>();
        if (payload.Length < 2) return list;
        int count = Math.Clamp((int)payload[1], 0, 8);
        for (int i = 0; i < count; i++)
        {
            int off = 2 + i * 4;
            if (off + 3 >= payload.Length) break;
            int x = (payload[off] << 8) | payload[off + 1];
            int y = (payload[off + 2] << 8) | payload[off + 3];
            if (y == 0) y = x;
            if (x is >= 50 and <= 50000)
                list.Add((x, Math.Clamp(y, 50, 50000)));
        }
        return list;
    }

    public static int? DecodePollingRate(byte code)
    {
        foreach (var (c, hz) in PollingRateTable)
            if (c == code) return hz;
        return null;
    }

    public static int? DecodeSleepSeconds(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3) return null;
        int sec = (payload[1] << 8) | payload[2];
        return sec == 0 || sec >= 0xFF00 ? 0 : sec;
    }

    public static (int Battery, bool Charging)? QueryBattery(RawHidFeature raw, int featLen)
    {
        var res = Exchange(raw, featLen, TargetMouse, PageDevice, 0x83, [], expectedLen: 0x02, attempts: 3);
        if (!res.Ok || res.Payload.Length < 2) return null;
        int pct = res.Payload[1];
        if (pct > 100) return null;
        return (pct, res.Payload[0] != 0);
    }

    public static DeviceTuningCapabilities QueryCapabilities(string deviceId, RawHidFeature raw, int featLen, bool isWireless, string? productName)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };

        // 1. Online check via firmware or battery
        var fwRes = Exchange(raw, featLen, TargetMouse, PageDevice, 0x81, [], expectedLen: 0x10, attempts: 4);
        var batRes = Exchange(raw, featLen, TargetMouse, PageDevice, 0x83, [], expectedLen: 0x02, attempts: 4);
        if (!fwRes.Ok && !batRes.Ok)
        {
            caps.IsOnline = false;
            caps.StatusMessage = "鼠标当前处于无线休眠或离线状态，请晃动唤醒后点击「读取当前硬件参数」。";
            return caps;
        }

        caps.IsOnline = true;

        // 2. Active onboard profile (1-based on wire -> 0-based in model)
        var profRes = Exchange(raw, featLen, TargetMouse, PageDevice, 0x85, [], expectedLen: 0x01, attempts: 3);
        int profile1Based = profRes.Ok && profRes.Payload.Length >= 1 && profRes.Payload[0] is >= 1 and <= 4
            ? profRes.Payload[0]
            : 1;
        byte profileByte = (byte)profile1Based;
        caps.OnboardProfileCount = 4;
        caps.ActiveOnboardProfile = profile1Based - 1;
        caps.OnboardProfileNames = ["板载配置 1", "板载配置 2", "板载配置 3", "板载配置 4"];

        // 3. DPI stages & active stage
        var stagesRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x81, [profileByte, 6], expectedLen: 0x0A, attempts: 3);
        if (stagesRes.Ok)
        {
            var stages = DecodeDpiStages(stagesRes.Payload);
            if (stages.Count > 0)
            {
                caps.SupportsDpi = true;
                caps.SupportsIndependentXY = true;
                caps.SupportsDpiStagesWrite = true;
                caps.MinDpi = 50;
                caps.MaxDpi = 30000;
                caps.DpiStep = 50;
                caps.DpiStages = stages;

                var actStageRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x82, [profileByte], expectedLen: 0x02, attempts: 2);
                int activeIdx = actStageRes.Ok && actStageRes.Payload.Length >= 2
                    ? Math.Clamp(actStageRes.Payload[1] - 1, 0, stages.Count - 1)
                    : 0;
                caps.ActiveDpiStage = activeIdx;
                caps.CurrentDpiX = stages[activeIdx].X;
                caps.CurrentDpiY = stages[activeIdx].Y;
            }
        }

        // 4. Polling rate
        var rateRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x80, [profileByte], expectedLen: 0x02, attempts: 3);
        if (rateRes.Ok && rateRes.Payload.Length >= 2 && DecodePollingRate(rateRes.Payload[1]) is { } curHz)
        {
            bool has8K = isWireless || (productName ?? "").Contains("8K", StringComparison.OrdinalIgnoreCase);
            caps.SupportedPollingRates = has8K
                ? [125, 250, 500, 1000, 2000, 4000, 8000]
                : [125, 250, 500, 1000];
            caps.CurrentPollingRate = curHz;
        }

        // 5. Performance / Competitive / High-Speed modes (0x8B HyperMode & 0x93 Turbo/CompetitiveMode)
        var hyperRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x8B, [profileByte], expectedLen: 0x02, attempts: 2);
        var turboRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x93, [profileByte], expectedLen: 0x02, attempts: 2);
        if (hyperRes.Ok && hyperRes.Payload.Length >= 2)
        {
            bool hyperOn = hyperRes.Payload[1] == 1;
            bool turboSupported = turboRes.Ok && turboRes.Payload.Length >= 2;
            bool turboOn = turboSupported && turboRes.Payload[1] == 1;

            caps.SupportedPerformanceModes.Add(new PerformanceModeOption(0, "标准节能", "常规办公与长续航回报处理"));
            caps.SupportedPerformanceModes.Add(new PerformanceModeOption(1, "高速模式 (High-Speed)", "提升传感器扫描帧率与响应稳定性"));
            if (turboSupported)
                caps.SupportedPerformanceModes.Add(new PerformanceModeOption(2, "竞技狂暴模式 (Turbo 20K FPS)", "传感器满血 20K FPS 竞技追踪"));

            caps.CurrentPerformanceMode = turboOn ? 2 : (hyperOn ? 1 : 0);
        }
        else if (turboRes.Ok && turboRes.Payload.Length >= 2)
        {
            caps.SupportedPerformanceModes.Add(new PerformanceModeOption(0, "标准模式", "均衡性能与无线续航"));
            caps.SupportedPerformanceModes.Add(new PerformanceModeOption(2, "竞技模式 (Competitive)", "全速竞技传感采样"));
            caps.CurrentPerformanceMode = turboRes.Payload[1] == 1 ? 2 : 0;
        }

        // 6. LOD
        var lodRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x88, [profileByte], expectedLen: 0x02, attempts: 2);
        if (lodRes.Ok && lodRes.Payload.Length >= 2 && lodRes.Payload[1] != 0)
        {
            caps.SupportedLodOptions = LodTable.Select(x => new LodOption(x.Code, x.Label)).ToList();
            caps.CurrentLod = LodTable.Any(x => x.Code == lodRes.Payload[1]) ? lodRes.Payload[1] : 0x01;
        }

        // 7. Sensor flags (Motion Sync, Angle Snapping, Ripple Control, Angle Tuning)
        var msRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x89, [profileByte], expectedLen: 0x02, attempts: 2);
        if (msRes.Ok && msRes.Payload.Length >= 2)
        {
            caps.SupportsMotionSync = true;
            caps.MotionSync = msRes.Payload[1] == 1;
        }

        var asRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x84, [profileByte], expectedLen: 0x02, attempts: 2);
        if (asRes.Ok && asRes.Payload.Length >= 2)
        {
            caps.SupportsAngleSnapping = true;
            caps.AngleSnapping = asRes.Payload[1] == 1;
        }

        var rcRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x8A, [profileByte], expectedLen: 0x02, attempts: 2);
        if (rcRes.Ok && rcRes.Payload.Length >= 2)
        {
            caps.SupportsRippleControl = true;
            caps.RippleControl = rcRes.Payload[1] == 1;
        }

        var atRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x94, [profileByte], expectedLen: 0x02, attempts: 2);
        if (atRes.Ok && atRes.Payload.Length >= 2)
        {
            caps.SupportsAngleTuning = true;
            sbyte deg = unchecked((sbyte)atRes.Payload[1]);
            caps.AngleTuningDeg = Math.Clamp((int)deg, -30, 30);
        }

        // 8. Debounce & Sleep
        var debRes = Exchange(raw, featLen, TargetMouse, PageDevice, 0x88, [profileByte], expectedLen: 0x02, attempts: 2);
        if (debRes.Ok && debRes.Payload.Length >= 2)
        {
            caps.SupportsDebounce = true;
            caps.MaxDebounceMs = 15;
            caps.CurrentDebounceMs = Math.Clamp((int)debRes.Payload[1], 0, 15);
        }

        var slpRes = Exchange(raw, featLen, TargetMouse, PageDevice, 0x87, [profileByte], expectedLen: 0x03, attempts: 2);
        if (slpRes.Ok && DecodeSleepSeconds(slpRes.Payload) is { } sleepSec)
        {
            caps.SupportsSleepTimeout = true;
            caps.MinSleepSeconds = 0;
            caps.MaxSleepSeconds = 1800;
            caps.SupportedSleepSeconds = [10, 30, 60, 120, 300, 600, 1800];
            caps.CurrentSleepSeconds = sleepSec;
        }

        // 9. Button bindings (page 3 cmd 0x80) - only populated if device echoes expected format
        var availableActions = StandardButtonActions.Select(a => a.Label).ToList();
        for (int btnId = 1; btnId <= 5; btnId++)
        {
            var btnRes = Exchange(raw, featLen, TargetMouse, PageButtons, 0x80, [profileByte, (byte)btnId, 0x00, 0xFF, 0x0A], expectedLen: 15, attempts: 2);
            if (!btnRes.Ok || btnRes.Payload.Length < 5)
            {
                if (btnId == 1) break;
                continue;
            }
            if (btnRes.Payload[0] != profileByte || btnRes.Payload[1] != btnId) continue;
            byte fn = btnRes.Payload[3];
            byte dataLen = Math.Min(btnRes.Payload[4], (byte)Math.Max(0, btnRes.Payload.Length - 5));
            var fnData = btnRes.Payload.AsSpan(5, dataLen).ToArray();
            string curLabel = DecodeButtonBinding(fn, fnData);
            caps.ButtonBindings.Add(new ButtonBindingSlot(btnId, PhysicalButtonNames[btnId - 1], curLabel, availableActions, IsReadOnly: btnId == 1));
        }

        caps.MacroCapabilityNote = caps.ButtonBindings.Count > 0
            ? "CompX Page3 原生支持鼠标键/DPI/多媒体与键盘组合键板载直写（多步宏由本机高精度宏引擎辅助）。"
            : "该 CompX 固件支持 DPI/配置槽位/竞技模式/传感器开关实时读写。";

        caps.StatusMessage = $"已通过 CompX 65B 协议读取配置 {profile1Based}（{(caps.SupportedPerformanceModes.FirstOrDefault(m => m.Code == caps.CurrentPerformanceMode)?.Label ?? "标准模式")}）。";
        return caps;
    }

    public static DeviceTuningResult ApplySettings(string deviceId, RawHidFeature raw, int featLen, bool isWireless, string? productName, DeviceTuningUpdate update)
    {
        var errors = new List<string>();

        // Switch profile first if requested so subsequent edits apply to the target profile
        if (update.ActiveOnboardProfile is { } targetProfIdx)
        {
            byte target1 = (byte)(Math.Clamp(targetProfIdx, 0, 3) + 1);
            var swRes = Exchange(raw, featLen, TargetMouse, PageDevice, 0x05, [target1], expectedLen: 0x01, attempts: 3);
            if (!swRes.Ok)
                errors.Add("切换板载配置失败");
            else
                Thread.Sleep(40);
        }

        var profRead = Exchange(raw, featLen, TargetMouse, PageDevice, 0x85, [], expectedLen: 0x01, attempts: 3);
        byte profileByte = profRead.Ok && profRead.Payload.Length >= 1 && profRead.Payload[0] is >= 1 and <= 4
            ? profRead.Payload[0]
            : (byte)1;

        if (update.DpiStages is { Count: > 0 } || update.Dpi != null)
        {
            var curStageRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x81, [profileByte, 6], expectedLen: 0x0A, attempts: 3);
            var curStages = curStageRes.Ok ? DecodeDpiStages(curStageRes.Payload) : [(800, 800)];
            if (curStages.Count == 0) curStages = [(800, 800)];

            var actRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x82, [profileByte], expectedLen: 0x02, attempts: 2);
            int curActive = actRes.Ok && actRes.Payload.Length >= 2
                ? Math.Clamp(actRes.Payload[1] - 1, 0, curStages.Count - 1)
                : 0;

            var targetStages = update.DpiStages is { Count: > 0 } ? update.DpiStages.ToList() : curStages;
            int targetActive = Math.Clamp(update.ActiveDpiStage ?? curActive, 0, targetStages.Count - 1);
            if (update.Dpi is { } singleDpi)
                targetStages[targetActive] = (Math.Clamp(singleDpi.X, 50, 42000), Math.Clamp(singleDpi.Y, 50, 42000));

            var args = new List<byte>(2 + targetStages.Count * 4) { profileByte, (byte)targetStages.Count };
            foreach (var (sx, sy) in targetStages)
            {
                int cx = Math.Clamp(sx, 50, 42000);
                int cy = Math.Clamp(sy, 50, 42000);
                args.Add((byte)(cx >> 8));
                args.Add((byte)(cx & 0xFF));
                args.Add((byte)(cy >> 8));
                args.Add((byte)(cy & 0xFF));
            }
            var wr = Exchange(raw, featLen, TargetMouse, PageProfile, 0x01, args.ToArray(), attempts: 3);
            if (!wr.Ok) errors.Add("写入 DPI 档位表失败");

            var wrAct = Exchange(raw, featLen, TargetMouse, PageProfile, 0x02, [profileByte, (byte)(targetActive + 1)], attempts: 3);
            if (!wrAct.Ok) errors.Add("切换激活 DPI 档位失败");
        }
        else if (update.ActiveDpiStage is { } actIdx)
        {
            var wrAct = Exchange(raw, featLen, TargetMouse, PageProfile, 0x02, [profileByte, (byte)(Math.Clamp(actIdx, 0, 5) + 1)], attempts: 3);
            if (!wrAct.Ok) errors.Add("切换激活 DPI 档位失败");
        }

        if (update.PollingRateHz is { } hz)
        {
            var curRateRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x80, [profileByte], expectedLen: 0x02, attempts: 2);
            bool currently8KFamily = curRateRes.Ok && curRateRes.Payload.Length >= 2 && curRateRes.Payload[1] >= 0x10;
            byte code = (hz, isWireless || currently8KFamily) switch
            {
                (125, _) => 0x08,
                (250, _) => 0x04,
                (500, _) => 0x02,
                (1000, true) => 0x10,
                (1000, false) => 0x01,
                (2000, _) => 0x20,
                (4000, _) => 0x40,
                (8000, _) => 0x80,
                _ => 0x00,
            };
            if (code == 0)
            {
                errors.Add($"不支持回报率 {hz} Hz");
            }
            else
            {
                var rRes = Exchange(raw, featLen, TargetMouse, PageProfile, 0x00, [profileByte, code], attempts: 3);
                if (hz == 1000 && rRes.Ok)
                {
                    var verify = Exchange(raw, featLen, TargetMouse, PageProfile, 0x80, [profileByte], expectedLen: 0x02, attempts: 2);
                    if (!verify.Ok || DecodePollingRate(verify.Payload.ElementAtOrDefault(1)) != 1000)
                        Exchange(raw, featLen, TargetMouse, PageProfile, 0x00, [profileByte, (byte)(code == 0x10 ? 0x01 : 0x10)], attempts: 2);
                }
                else if (!rRes.Ok)
                {
                    errors.Add($"写入回报率 {hz} Hz 失败");
                }
            }
        }

        if (update.PerformanceMode is { } perfMode)
        {
            if (perfMode == 0)
            {
                Exchange(raw, featLen, TargetMouse, PageProfile, 0x13, [profileByte, 0], attempts: 2);
                var h = Exchange(raw, featLen, TargetMouse, PageProfile, 0x0B, [profileByte, 0], attempts: 2);
                if (!h.Ok && h.Unsupported) Exchange(raw, featLen, TargetMouse, PageProfile, 0x13, [profileByte, 0], attempts: 2);
            }
            else if (perfMode == 1)
            {
                var h = Exchange(raw, featLen, TargetMouse, PageProfile, 0x0B, [profileByte, 1], attempts: 2);
                Exchange(raw, featLen, TargetMouse, PageProfile, 0x13, [profileByte, 0], attempts: 2);
                if (!h.Ok) errors.Add("写入高速模式失败");
            }
            else if (perfMode >= 2)
            {
                Exchange(raw, featLen, TargetMouse, PageProfile, 0x0B, [profileByte, 1], attempts: 2);
                var t = Exchange(raw, featLen, TargetMouse, PageProfile, 0x13, [profileByte, 1], attempts: 2);
                if (!t.Ok) errors.Add("写入竞技/Turbo 模式失败");
            }
        }

        if (update.LodCode is { } lod)
        {
            var r = Exchange(raw, featLen, TargetMouse, PageProfile, 0x08, [profileByte, (byte)lod], attempts: 2);
            if (!r.Ok) errors.Add("写入静默高度失败");
        }

        if (update.MotionSync is { } ms)
        {
            if (!Exchange(raw, featLen, TargetMouse, PageProfile, 0x09, [profileByte, (byte)(ms ? 1 : 0)], attempts: 2).Ok)
                errors.Add("写入移动同步失败");
        }

        if (update.AngleSnapping is { } snap)
        {
            if (!Exchange(raw, featLen, TargetMouse, PageProfile, 0x04, [profileByte, (byte)(snap ? 1 : 0)], attempts: 2).Ok)
                errors.Add("写入直线修正失败");
        }

        if (update.RippleControl is { } ripple)
        {
            if (!Exchange(raw, featLen, TargetMouse, PageProfile, 0x0A, [profileByte, (byte)(ripple ? 1 : 0)], attempts: 2).Ok)
                errors.Add("写入波纹控制失败");
        }

        if (update.AngleTuningDeg is { } deg)
        {
            byte encoded = unchecked((byte)(sbyte)Math.Clamp(deg, -30, 30));
            if (!Exchange(raw, featLen, TargetMouse, PageProfile, 0x14, [profileByte, encoded], attempts: 2).Ok)
                errors.Add("写入传感器角度微调失败");
        }

        if (update.DebounceMs is { } deb)
        {
            byte msVal = (byte)Math.Clamp(deb, 0, 15);
            if (!Exchange(raw, featLen, TargetMouse, PageDevice, 0x08, [profileByte, msVal], attempts: 2).Ok)
                errors.Add("写入防抖时间失败");
        }

        if (update.SleepSeconds is { } sleep)
        {
            int sec = Math.Clamp(sleep, 10, 0xFEFF);
            if (!Exchange(raw, featLen, TargetMouse, PageDevice, 0x07, [profileByte, (byte)(sec >> 8), (byte)(sec & 0xFF)], attempts: 2).Ok)
                errors.Add("写入休眠时间失败");
        }

        if (update.ButtonBinding is { } bind && bind.SlotIndex is >= 2 and <= 5)
        {
            var encoded = EncodeButtonBinding(bind.Action);
            if (encoded == null)
                errors.Add($"不支持按键动作「{bind.Action}」");
            else
            {
                var (fnId, data) = encoded.Value;
                var args = new byte[5 + data.Length];
                args[0] = profileByte;
                args[1] = (byte)bind.SlotIndex;
                args[2] = 0;
                args[3] = fnId;
                args[4] = (byte)data.Length;
                Array.Copy(data, 0, args, 5, data.Length);
                if (!Exchange(raw, featLen, TargetMouse, PageButtons, 0x00, args, attempts: 3).Ok)
                    errors.Add("写入板载按键映射失败");
            }
        }

        Thread.Sleep(35);
        var updated = QueryCapabilities(deviceId, raw, featLen, isWireless, productName);
        return errors.Count == 0
            ? new DeviceTuningResult(true, "已写入 CompX 硬件并完成回读校验。", updated)
            : new DeviceTuningResult(false, string.Join("；", errors), updated);
    }

    private static string DecodeButtonBinding(byte fnId, byte[] data)
    {
        foreach (var (label, knownFn, knownData) in StandardButtonActions)
        {
            if (knownFn == fnId && data.SequenceEqual(knownData))
                return label;
        }
        if (fnId == 4 && data.Length == 2)
            return HidKeyCatalog.FormatKeyboardBinding(data[0], data[1]);
        if (fnId is 0x10 or 0x11 or 0x12)
            return $"板载宏 #{fnId:X2}";
        return $"自定义 ({fnId:X2})";
    }

    private static (byte FnId, byte[] Data)? EncodeButtonBinding(string action)
    {
        foreach (var (label, fnId, data) in StandardButtonActions)
            if (label == action) return (fnId, data);

        if (HidKeyCatalog.TryParseKeyboardBinding(action, out byte mod, out byte usage))
            return (4, [mod, usage]);

        return null;
    }

    private static ExchangeResult Exchange(RawHidFeature raw, int featLen, byte target, byte page, byte command, byte[] args, int expectedLen = -1, int attempts = 4)
    {
        int bufLen = Math.Max(featLen, 65);
        byte[] packet = BuildRequest(target, page, command, args, expectedLen);
        if (packet.Length != bufLen)
        {
            var resized = new byte[bufLen];
            Array.Copy(packet, resized, Math.Min(packet.Length, bufLen));
            packet = resized;
        }

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (!raw.SetFeature(packet))
                continue;
            Thread.Sleep(attempt == 0 ? 25 : 65);
            var reply = raw.GetFeature(bufLen, ReportId);
            if (reply == null) continue;
            var decoded = TryDecodeReply(reply, page, command);
            if (decoded.Ok || decoded.Unsupported)
                return decoded;
        }
        return new ExchangeResult(false, false, []);
    }
}
