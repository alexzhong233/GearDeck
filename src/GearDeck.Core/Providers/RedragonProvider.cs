using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// Redragon / Holtek (`0x04D9`: M908 Impact `0xFC4D`, M711 Cobra `0xFC30`) 原生硬件协议：
/// 参考 `dokutan/mouse_m908`，支持 5 组板载配置切换与 15 组 256B 板载硬件宏直写（Report ID `0x02`/`0x04`）。
/// </summary>
public sealed class RedragonProvider : ITunablePeripheralProvider
{
    public const string ProviderName = "Redragon";
    public const ushort VendorId = 0x04D9;
    public const ushort PidM908 = 0xFC4D;
    public const ushort PidM711 = 0xFC30;

    public const int ProfileCount = 5;
    public const int MacroSlotCount = 15;
    public const int MacroPacketSize = 256;

    private static readonly byte[][] SlotAddressTable =
    [
        [0x78, 0x04], [0x40, 0x05], [0x08, 0x06], [0xD0, 0x06], [0x98, 0x07],
        [0x60, 0x08], [0x28, 0x09], [0xF0, 0x09], [0xB8, 0x0A], [0x80, 0x0B],
        [0x48, 0x0C], [0x10, 0x0D], [0xD8, 0x0D], [0xA0, 0x0E], [0x68, 0x0F],
    ];

    public string Name => ProviderName;

    internal static bool IsSupportedPid(ushort pid) => pid is PidM908 or PidM711;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("redragon:", StringComparison.OrdinalIgnoreCase);

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
            string name = !string.IsNullOrWhiteSpace(rawName)
                ? rawName.Trim()
                : pid == PidM908 ? "Redragon M908 Impact" : "Redragon M711 Cobra";

            results.Add(new PeripheralDevice
            {
                Id = $"redragon:{VendorId:X4}-{pid:X4}",
                VendorId = VendorId,
                ProductId = pid,
                Name = name,
                Kind = DeviceKind.Mouse,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = false,
                Transport = "USB",
                Note = "Redragon 原生协议（5 组板载配置 · 15 组板载硬件宏直写）",
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
            caps.StatusMessage = "无效的 Redragon 设备标识。";
            return caps;
        }

        using var ch = OpenControlChannel(pid, minFeatureLen: 16);
        if (ch == null)
        {
            caps.StatusMessage = "未找到 Redragon USB Feature 控制通道。";
            return caps;
        }

        caps.IsOnline = true;
        caps.OnboardProfileCount = ProfileCount;
        caps.ActiveOnboardProfile = 0;
        caps.OnboardProfileNames = ["配置 1", "配置 2", "配置 3", "配置 4", "配置 5"];
        caps.SupportsOnboardMacroUpload = true;
        caps.OnboardMacroSlotCount = MacroSlotCount;
        caps.MacroCapabilityNote = "Redragon M908/M711 原生协议：支持 5 组板载配置切换与 15 组板载硬件宏直写（仅写入宏槽位 1~15）。";
        caps.StatusMessage = "已连接 Redragon 板载控制通道。";
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (!ProviderIdHelper.TryParseVidPid(deviceId, out ushort vid, out ushort pid) || vid != VendorId || !IsSupportedPid(pid))
            return new DeviceTuningResult(false, "无效的 Redragon 设备标识。");

        var notes = new List<string>();
        var errors = new List<string>();

        if (update.ActiveOnboardProfile is { } profIdx)
        {
            using var ch = OpenControlChannel(pid, minFeatureLen: 16);
            if (ch == null)
            {
                errors.Add("打开 Redragon 配置通道失败");
            }
            else
            {
                int clamped = Math.Clamp(profIdx, 0, ProfileCount - 1);
                bool ok = true;
                foreach (var pkt in BuildProfileSwitchPackets(clamped))
                {
                    if (!ch.WriteFeature(pkt)) { ok = false; break; }
                }
                if (ok) notes.Add($"已切换为板载配置 {clamped + 1}");
                else errors.Add("切换 Redragon 板载配置失败");
            }
        }

        if (update.UploadMacro is { } macroUp)
        {
            using var ch16 = OpenControlChannel(pid, minFeatureLen: 16);
            using var ch256 = OpenControlChannel(pid, minFeatureLen: MacroPacketSize);
            if (ch16 == null || ch256 == null)
            {
                errors.Add("未找到 Redragon 256B 宏写入通道");
            }
            else
            {
                int slot = Math.Clamp(macroUp.MacroSlot, 0, MacroSlotCount - 1);
                byte[] unlock = [0x02, 0xF5, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
                byte[] macroPkt = EncodeMacroPacket(slot, macroUp.Macro);
                byte[] commit = [0x02, 0xF5, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

                bool ok = ch16.WriteFeature(unlock)
                    && ch256.WriteFeature(macroPkt)
                    && ch16.WriteFeature(commit);
                if (ok) notes.Add($"已将宏「{macroUp.Macro.Name}」写入 Redragon 硬件宏槽位 {slot + 1}");
                else errors.Add($"写入 Redragon 硬件宏槽位 {slot + 1} 失败");
            }
        }

        var refreshed = QueryCapabilities(deviceId);
        return errors.Count == 0
            ? new DeviceTuningResult(true, notes.Count > 0 ? string.Join("，", notes) : "已更新 Redragon 配置。", refreshed)
            : new DeviceTuningResult(false, string.Join("；", errors), refreshed);
    }

    /// <summary>按 `mouse_m908::_c_data_s_profile` 生成 6×16B 切换板载配置报文。</summary>
    internal static byte[][] BuildProfileSwitchPackets(int profileZeroBased)
    {
        byte prof = (byte)Math.Clamp(profileZeroBased, 0, ProfileCount - 1);
        return
        [
            [0x02, 0xF3, 0x2C, 0x00, 0x02, 0x00, 0x00, 0x00, prof, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0x02, 0xF1, 0x02, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0x02, 0xF1, 0x02, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0x02, 0xF1, 0x02, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0x02, 0xF1, 0x02, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0x02, 0xF1, 0x02, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        ];
    }

    /// <summary>按 `mouse_m908::_c_data_macros_2` 与 `rd_mouse::_i_encode_macro` 构建 256B 宏数据包。</summary>
    internal static byte[] EncodeMacroPacket(int slotZeroBased, OnboardMacroDefinition macro)
    {
        int slot = Math.Clamp(slotZeroBased, 0, MacroSlotCount - 1);
        var buf = new byte[MacroPacketSize];
        buf[0] = 0x04;
        buf[1] = 0xF3;
        buf[2] = SlotAddressTable[slot][0];
        buf[3] = SlotAddressTable[slot][1];
        buf[4] = 0xC8;

        int offset = 8;
        const int maxOffset = 212;
        foreach (var step in macro.Steps)
        {
            if (offset > maxOffset) break;

            if (step.KeyCode is >= 0x04 and < 0xF0)
            {
                buf[offset] = step.IsPress ? (byte)0x84 : (byte)0x04;
                buf[offset + 1] = step.KeyCode;
                offset += 3;
            }
            else if (step.KeyCode is >= HidKeyCatalog.MouseLeftCode and <= HidKeyCatalog.MouseXButton2Code)
            {
                buf[offset] = step.IsPress ? (byte)0x81 : (byte)0x01;
                buf[offset + 1] = (byte)(1 << (step.KeyCode - HidKeyCatalog.MouseLeftCode));
                offset += 3;
            }

            int delayRemain = step.DelayMs;
            while (delayRemain > 0 && offset <= maxOffset)
            {
                int chunk = Math.Min(delayRemain, 255);
                buf[offset] = 0x06;
                buf[offset + 1] = (byte)chunk;
                offset += 3;
                delayRemain -= chunk;
            }
        }

        if (offset <= maxOffset)
        {
            ushort repeat = macro.LoopMode == MacroLoopMode.PlayOnce
                ? (ushort)1
                : (ushort)Math.Clamp(macro.RepeatCount, 1, 65535);
            buf[offset] = 0xFF;
            buf[offset + 1] = (byte)(repeat & 0xFF);
            buf[offset + 2] = (byte)(repeat >> 8);
        }
        return buf;
    }

    private static HidChannel? OpenControlChannel(ushort pid, int minFeatureLen)
    {
        foreach (var dev in HidSafe.EnumerateDevices(VendorId, pid).OrderByDescending(d => HidSafe.MaxFeatureLen(d)))
        {
            if (HidSafe.MaxFeatureLen(dev) < minFeatureLen) continue;
            var ch = HidChannel.TryOpen(dev);
            if (ch != null) return ch;
        }
        return null;
    }
}
