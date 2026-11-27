using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 罗技 G600 MMO 游戏鼠标专有板载协议 (`046D:C24A`)：
/// 基于 `libratbag/src/driver-logitech-g600.c`，使用 Feature Report `0xF0`（当前配置/DPI）与 `0xF3..0xF5`（154B 完整板载配置），
/// 支持 3 组板载配置、4 档 200~8200 DPI、125~1000 Hz 回报率与物理按键/快捷键重映射。
/// </summary>
internal static class LogitechG600Protocol
{
    public const ushort PidG600 = 0xC24A;
    public const int ProfileCount = 3;
    public const int DpiSlotCount = 4;
    public const int ProfileReportSize = 154;
    public const byte ReportIdActive = 0xF0;

    private static readonly int[] SupportedRates = [125, 250, 500, 1000];

    private static readonly (int SlotIndex, string Label)[] EditableButtons =
    [
        (0, "左键"),
        (1, "右键"),
        (2, "滚轮中键"),
        (3, "后退侧键"),
        (4, "前进侧键"),
        (5, "G-Shift 辅助键"),
        (6, "滚轮左倾"),
        (7, "滚轮右倾"),
        (8, "G9 拇指键"),
        (9, "G10 拇指键"),
        (10, "G11 拇指键"),
        (11, "G12 拇指键"),
    ];

    private static readonly Dictionary<byte, string> KnownCodeLabels = new()
    {
        [0x01] = "左键点击",
        [0x02] = "右键点击",
        [0x03] = "中键点击",
        [0x04] = "后退",
        [0x05] = "前进",
        [0x11] = "DPI +",
        [0x12] = "DPI -",
        [0x13] = "DPI 循环",
        [0x14] = "切换配置",
        [0x15] = "DPI Shift",
        [0x17] = "G-Shift",
    };

    private static readonly IReadOnlyList<string> StandardButtonOptions =
    [
        "左键点击", "右键点击", "中键点击", "后退", "前进",
        "DPI +", "DPI -", "DPI 循环", "切换配置", "DPI Shift", "G-Shift", "禁用",
    ];

    public static bool IsG600(ushort pid) => pid == PidG600;

    public static PeripheralDevice EnumerateG600(IEnumerable<HidDevice> interfaces)
    {
        var any = interfaces.First();
        string raw = HidSafe.ProductName(any);
        return new PeripheralDevice
        {
            Id = $"logi:{PidG600:X4}:FF",
            VendorId = 0x046D,
            ProductId = PidG600,
            Name = string.IsNullOrWhiteSpace(raw) ? "Logitech G600 MMO" : raw.Trim(),
            Kind = DeviceKind.Mouse,
            Provider = LogitechProvider.ProviderName,
            Connected = true,
            IsSleeping = false,
            Transport = "USB",
            Note = "G600 专有板载协议（3 组配置 · 4 档 DPI · 20 键映射）",
            LastUpdated = DateTimeOffset.Now,
        };
    }

    public static DeviceTuningCapabilities QueryCapabilities(string deviceId, IEnumerable<HidDevice> devices)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        using var ch = OpenFeatureChannel(devices);
        if (ch == null)
        {
            caps.StatusMessage = "未找到 G600 Feature Report 通道。";
            return caps;
        }

        byte[]? activeBuf = ch.ReadFeature(ReportIdActive);
        int activeProfile = 0;
        int activeDpiSlot = 0;
        if (activeBuf is { Length: >= 2 })
        {
            activeProfile = Math.Clamp((activeBuf[1] >> 4) & 0x0F, 0, ProfileCount - 1);
            activeDpiSlot = Math.Clamp((activeBuf[1] >> 1) & 0x03, 0, DpiSlotCount - 1);
        }

        byte reportId = (byte)(0xF3 + activeProfile);
        byte[]? profileBuf = ch.ReadFeature(reportId);
        if (profileBuf == null || profileBuf.Length < ProfileReportSize)
        {
            caps.StatusMessage = "读取 G600 板载配置失败。";
            return caps;
        }

        caps.IsOnline = true;
        PopulateCapabilitiesFromProfile(profileBuf, activeProfile, activeDpiSlot, caps);
        caps.StatusMessage = $"已读取 G600 板载配置 {activeProfile + 1}。";
        return caps;
    }

    public static DeviceTuningResult ApplySettings(string deviceId, IEnumerable<HidDevice> devices, DeviceTuningUpdate update)
    {
        using var ch = OpenFeatureChannel(devices);
        if (ch == null)
            return new DeviceTuningResult(false, "打开 G600 HID Feature 通道失败。");

        byte[]? activeBuf = ch.ReadFeature(ReportIdActive);
        int activeProfile = activeBuf is { Length: >= 2 }
            ? Math.Clamp((activeBuf[1] >> 4) & 0x0F, 0, ProfileCount - 1)
            : 0;
        int activeDpiSlot = activeBuf is { Length: >= 2 }
            ? Math.Clamp((activeBuf[1] >> 1) & 0x03, 0, DpiSlotCount - 1)
            : 0;

        if (update.ActiveOnboardProfile is { } nextProf)
        {
            activeProfile = Math.Clamp(nextProf, 0, ProfileCount - 1);
            ch.WriteFeature([ReportIdActive, (byte)(0x80 | (activeProfile << 4)), 0x00, 0x00]);
        }

        byte reportId = (byte)(0xF3 + activeProfile);
        byte[]? profileBuf = ch.ReadFeature(reportId);
        if (profileBuf == null || profileBuf.Length < ProfileReportSize)
            return new DeviceTuningResult(false, $"读取 G600 配置 {activeProfile + 1} 失败。");

        bool needWriteProfile = false;
        if (update.PollingRateHz is { } hz && hz is >= 125 and <= 1000)
        {
            profileBuf[11] = (byte)Math.Clamp((1000 / hz) - 1, 0, 7);
            needWriteProfile = true;
        }

        if (update.Dpi is { } dpi)
        {
            int targetDpi = Math.Clamp(dpi.X, 200, 8200);
            int slotIdx = Math.Clamp(activeDpiSlot, 0, DpiSlotCount - 1);
            profileBuf[14 + slotIdx] = (byte)Math.Clamp(targetDpi / 50, 4, 0xA4);
            needWriteProfile = true;
        }

        if (update.ButtonBinding is { } btnBind)
        {
            if (PatchButtonBinding(profileBuf, btnBind.SlotIndex, btnBind.Action))
                needWriteProfile = true;
        }

        if (needWriteProfile && !ch.WriteFeature(profileBuf[..ProfileReportSize]))
            return new DeviceTuningResult(false, "写入 G600 板载配置失败。");

        var refreshed = QueryCapabilities(deviceId, devices);
        return new DeviceTuningResult(true, "已更新 G600 板载配置。", refreshed);
    }

    internal static void PopulateCapabilitiesFromProfile(
        ReadOnlySpan<byte> profileBuf,
        int activeProfile,
        int activeDpiSlot,
        DeviceTuningCapabilities caps)
    {
        caps.OnboardProfileCount = ProfileCount;
        caps.ActiveOnboardProfile = activeProfile;
        caps.OnboardProfileNames = ["板载配置 1", "板载配置 2", "板载配置 3"];

        caps.SupportsDpi = true;
        caps.MinDpi = 200;
        caps.MaxDpi = 8200;
        caps.DpiStep = 50;

        caps.DpiStages.Clear();
        for (int i = 0; i < DpiSlotCount; i++)
        {
            int val = profileBuf[14 + i] * 50;
            if (val <= 0) val = 800;
            caps.DpiStages.Add((val, val));
        }
        int curIdx = Math.Clamp(activeDpiSlot, 0, DpiSlotCount - 1);
        caps.ActiveDpiStage = curIdx;
        caps.CurrentDpiX = caps.DpiStages[curIdx].X;
        caps.CurrentDpiY = caps.DpiStages[curIdx].Y;

        int freqDiv = profileBuf[11] + 1;
        int rate = freqDiv > 0 ? 1000 / freqDiv : 1000;
        caps.SupportedPollingRates = [.. SupportedRates];
        caps.CurrentPollingRate = SupportedRates.Contains(rate) ? rate : 1000;

        caps.ButtonBindings.Clear();
        foreach (var (slotIdx, label) in EditableButtons)
        {
            int at = 31 + slotIdx * 3;
            byte code = profileBuf[at];
            byte mod = profileBuf[at + 1];
            byte key = profileBuf[at + 2];
            string curAction = FormatButtonAction(code, mod, key);
            caps.ButtonBindings.Add(new ButtonBindingSlot(
                slotIdx,
                label,
                curAction,
                StandardButtonOptions,
                IsReadOnly: slotIdx == 0));
        }
    }

    internal static bool PatchButtonBinding(byte[] profileBuf, int slotIndex, string action)
    {
        if (profileBuf.Length < ProfileReportSize || (uint)slotIndex >= 20)
            return false;

        int at = 31 + slotIndex * 3;
        if (HidKeyCatalog.TryParseKeyboardBinding(action, out byte mod, out byte usage))
        {
            profileBuf[at] = 0x00;
            profileBuf[at + 1] = mod;
            profileBuf[at + 2] = usage;
            return true;
        }

        if (action == "禁用")
        {
            profileBuf[at] = 0x00;
            profileBuf[at + 1] = 0x00;
            profileBuf[at + 2] = 0x00;
            return true;
        }

        foreach (var kv in KnownCodeLabels)
        {
            if (string.Equals(kv.Value, action, StringComparison.OrdinalIgnoreCase))
            {
                profileBuf[at] = kv.Key;
                profileBuf[at + 1] = 0x00;
                profileBuf[at + 2] = 0x00;
                return true;
            }
        }
        return false;
    }

    private static string FormatButtonAction(byte code, byte mod, byte key)
    {
        if (KnownCodeLabels.TryGetValue(code, out var label))
            return label;
        if (code == 0x00 && (mod != 0 || key != 0))
            return HidKeyCatalog.FormatKeyboardBinding(mod, key);
        return code == 0x00 ? "禁用" : $"功能 0x{code:X2}";
    }

    private static HidChannel? OpenFeatureChannel(IEnumerable<HidDevice> devices)
    {
        foreach (var dev in devices.OrderByDescending(d => HidSafe.MaxFeatureLen(d)))
        {
            if (HidSafe.MaxFeatureLen(dev) < ProfileReportSize)
                continue;
            var ch = HidChannel.TryOpen(dev);
            if (ch != null)
                return ch;
        }
        return null;
    }
}
