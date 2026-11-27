using System.Globalization;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;
using GearDeck.Core.Services;

namespace GearDeck.Core.Providers;

/// <summary>
/// VIA / QMK / Vial Raw HID 通用键盘与磁轴键盘识别提供器：
/// 1) 主动探测 UsagePage 0xFF60 / Usage 0x0061 的 QMK/VIA 协议版本与动态按键层数；
/// 2) 识别 Wooting / Keychron / MonsGeek / DrunkDeer / NuPhy 等主流磁轴与客制化键盘；
/// 3) 支持 VIA Custom/Lighting (0x08/0x07/0x09) 键盘背光亮度读取与保存。
/// </summary>
public sealed class ViaKeyboardProvider : IPeripheralProvider, ITunablePeripheralProvider
{
    public const string ProviderName = "VIA/QMK";
    public string Name => ProviderName;

    private const ushort ViaUsagePage = 0xFF60;
    private const ushort ViaUsage = 0x0061;
    private const byte CmdGetProtocolVersion = 0x01;
    private const byte CmdCustomSetValue = 0x07;
    private const byte CmdCustomGetValue = 0x08;
    private const byte CmdCustomSave = 0x09;
    private const byte CmdMacroGetCount = 0x0C;
    private const byte CmdMacroGetBufferSize = 0x0D;
    private const byte CmdMacroGetBuffer = 0x0E;
    private const byte CmdMacroSetBuffer = 0x0F;
    private const byte CmdGetLayerCount = 0x11;
    private const int ViaMacroChunkMax = 28;

    private static readonly byte[] LightingChannelPriority = [0x03, 0x02, 0x01]; // RGB Matrix -> RGBLight -> Backlight

    private static readonly HashSet<ushort> DedicatedKeyboardVids =
    [
        0x0B05, // ROG
        0x1532, // Razer
        0x046D, // Logitech
        0x373B, // ATK / VGN
        0x34D7, // ATK / VXE
        0x38A6, // AULA
        0x384C, // AULA
        0x1B1C, // Corsair
        0x1038, // SteelSeries
        0x258A, // LEOBOG Wired
        0x3554, // LEOBOG Wireless / Telink
    ];

    private static readonly Dictionary<ushort, (string Brand, string IdPrefix)> KnownKeyboardBrands = new()
    {
        [0x31E3] = ("Wooting", "wooting"),
        [0x03EB] = ("Wooting", "wooting"),
        [0x3434] = ("Keychron", "keychron"),
        [0x3151] = ("MonsGeek", "monsgeek"),
        [0x352D] = ("DrunkDeer", "drunkdeer"),
        [0x19F5] = ("NuPhy", "nuphy"),
    };

    // 0x3151 上属于怒喵鼠标的 PID，交由 AngryMiao/GearHubMu 处理，不重复当作键盘。
    private static readonly HashSet<ushort> Excluded3151MousePids = [0x5007, 0x402A, 0x3010];

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("via:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("wooting:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("keychron:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("monsgeek:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("drunkdeer:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("nuphy:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var groups = HidSafe.EnumerateDevices()
            .Where(d => d.VendorID > 0 && d.ProductID > 0)
            .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID));

        foreach (var g in groups)
        {
            var (vid, pid) = g.Key;
            if (DedicatedKeyboardVids.Contains(vid))
                continue;
            if (vid == 0x3151 && Excluded3151MousePids.Contains(pid))
                continue;

            var ifaces = g.ToList();
            string? rawName = ifaces.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            var viaIface = ifaces.FirstOrDefault(d => HidSafe.PrimaryUsage(d) == (ViaUsagePage, ViaUsage));
            bool hasCatalog = DeviceCatalogService.TryLookup(vid, pid, out var cat);
            bool isViaMouse = viaIface != null && (
                vid == 0x5043 // Ploopy open-source QMK/VIA mice & trackballs
                || (rawName?.Contains("Ploopy", StringComparison.OrdinalIgnoreCase) ?? false)
                || (hasCatalog && cat?.ParsedKind == DeviceKind.Mouse && !IsKeychronMouseName(rawName)));
            if (hasCatalog && cat != null && cat.ParsedKind != DeviceKind.Keyboard && !isViaMouse)
                continue;
            if (vid == 0x3434 && IsKeychronMouseName(rawName))
                continue;
            bool catalogHit = hasCatalog && (cat?.ParsedKind == DeviceKind.Keyboard || isViaMouse);
            bool knownBrand = KnownKeyboardBrands.TryGetValue(vid, out var brandMeta);

            if (viaIface == null && !catalogHit && !knownBrand)
                continue;

            ushort? viaVersion = null;
            int? layerCount = null;
            if (viaIface != null)
                (viaVersion, layerCount) = ProbeViaInfo(viaIface);

            string brand = cat?.Brand is { Length: > 0 } cb ? cb : (knownBrand ? brandMeta.Brand : (isViaMouse ? "Ploopy / VIA" : "VIA/QMK"));
            string prefix = knownBrand ? brandMeta.IdPrefix : "via";
            string modelName = cat?.Model is { Length: > 0 } cm
                ? cm
                : (!string.IsNullOrWhiteSpace(rawName) ? rawName.Trim() : $"{brand} {(isViaMouse ? "鼠标" : "键盘")} {vid:X4}:{pid:X4}");

            string transport = InferTransport(cat, rawName, modelName);
            string? note = BuildNote(viaVersion, layerCount, viaIface != null);

            results.Add(new PeripheralDevice
            {
                Id = $"{prefix}:{vid:X4}-{pid:X4}",
                VendorId = vid,
                ProductId = pid,
                Name = modelName,
                Kind = isViaMouse ? DeviceKind.Mouse : DeviceKind.Keyboard,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = false,
                Transport = transport,
                Note = note,
                LastUpdated = DateTimeOffset.Now,
            });
        }

        return results;
    }

    public DeviceTuningCapabilities QueryCapabilities(string deviceId)
    {
        var caps = new DeviceTuningCapabilities { DeviceId = deviceId };
        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
        {
            caps.StatusMessage = "无效的 VIA 设备标识。";
            return caps;
        }

        var viaIface = HidSafe.EnumerateDevices()
            .FirstOrDefault(d => (ushort)d.VendorID == vid && (ushort)d.ProductID == pid
                && HidSafe.PrimaryUsage(d) == (ViaUsagePage, ViaUsage));
        if (viaIface == null)
        {
            caps.StatusMessage = "未检测到 0xFF60:0x0061 VIA Raw HID 通道。";
            return caps;
        }

        using var ch = HidChannel.TryOpen(viaIface);
        if (ch == null)
        {
            caps.StatusMessage = "无法打开 VIA Raw HID 通道。";
            return caps;
        }

        ushort? viaVer = null;
        byte[]? verResp = SendViaCommand(ch, CmdGetProtocolVersion);
        if (verResp != null)
            viaVer = ParseViaProtocolVersion(verResp);

        var (macroCount, macroBufferSize) = QueryMacroInfo(ch);

        int brightness = 0;
        byte? activeChannel = null;
        foreach (byte channel in LightingChannelPriority)
        {
            var resp = SendViaCommand(ch, CmdCustomGetValue, [channel, 0x01]);
            if (resp == null) continue;
            int o = (resp.Length >= 33 && resp[0] == 0x00) ? 1 : 0;
            if (resp.Length >= o + 4 && resp[o] == CmdCustomGetValue && resp[o + 1] == channel)
            {
                activeChannel = channel;
                brightness = resp[o + 3];
                break;
            }
        }

        if (activeChannel == null && viaVer == null && macroCount <= 0)
        {
            caps.StatusMessage = "设备未响应 VIA Raw HID 协议查询。";
            return caps;
        }

        caps.IsOnline = true;
        if (activeChannel != null)
        {
            caps.SupportsLighting = true;
            caps.CurrentLightingBrightness = brightness;
            caps.MaxLightingBrightness = 255;
        }

        if (macroCount > 0 && macroBufferSize > 0)
        {
            caps.SupportsOnboardMacroUpload = true;
            caps.OnboardMacroSlotCount = macroCount;
            caps.MacroCapabilityNote = $"VIA/QMK 原生支持 {macroCount} 组板载动态宏槽位（M0–M{macroCount - 1}，EEPROM 缓冲 {macroBufferSize} B；仅支持键盘按键与延迟步骤）。";
        }

        caps.StatusMessage = activeChannel != null
            ? $"VIA Raw HID 通道 (v0x{viaVer ?? 0:X4} · 灯效频道 0x{activeChannel.Value:X2})"
            : $"VIA Raw HID 通道 (v0x{viaVer ?? 0:X4} · {(macroCount > 0 ? $"{macroCount} 组板载动态宏" : "已连接")})";
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (update.Lighting == null && update.UploadMacro == null)
            return new DeviceTuningResult(true, "未变更 VIA 设备配置");

        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new DeviceTuningResult(false, "无效的 VIA 设备标识");

        var viaIface = HidSafe.EnumerateDevices()
            .FirstOrDefault(d => (ushort)d.VendorID == vid && (ushort)d.ProductID == pid
                && HidSafe.PrimaryUsage(d) == (ViaUsagePage, ViaUsage));
        if (viaIface == null)
            return new DeviceTuningResult(false, "未找到 0xFF60:0x0061 VIA Raw HID 通道");

        using var ch = HidChannel.TryOpen(viaIface);
        if (ch == null)
            return new DeviceTuningResult(false, "无法打开 VIA Raw HID 通道");

        var notes = new List<string>();
        var errors = new List<string>();

        if (update.UploadMacro is { } macroUp)
        {
            var (macroCount, bufferSize) = QueryMacroInfo(ch);
            if (macroCount <= 0 || bufferSize <= 0)
            {
                errors.Add("当前设备固件未启用 VIA 动态宏缓冲区");
            }
            else if ((uint)macroUp.MacroSlot >= (uint)macroCount)
            {
                errors.Add($"宏槽位 M{macroUp.MacroSlot} 超出设备支持范围 (M0–M{macroCount - 1})");
            }
            else
            {
                byte[] encoded = EncodeViaMacroBytes(macroUp.Macro);
                if (encoded.Length == 0)
                {
                    errors.Add("当前宏不包含 VIA 支持的键盘按键或延迟步骤（QMK/VIA 动态宏暂不支持纯鼠标点击动作）");
                }
                else
                {
                    byte[]? currentBuf = ReadViaMacroBuffer(ch, bufferSize);
                    if (currentBuf == null)
                    {
                        errors.Add("读取设备现有 VIA 动态宏缓冲区失败");
                    }
                    else
                    {
                        byte[]? nextBuf = BuildUpdatedViaMacroBuffer(currentBuf, macroUp.MacroSlot, macroCount, encoded, bufferSize, out string? bufErr);
                        if (nextBuf == null)
                        {
                            errors.Add(bufErr ?? "VIA 宏缓冲区超出容量");
                        }
                        else if (!WriteViaMacroBuffer(ch, nextBuf))
                        {
                            errors.Add("写入 VIA 动态宏缓冲区到 EEPROM 失败");
                        }
                        else
                        {
                            notes.Add($"已将宏「{macroUp.Macro.Name}」写入 VIA/QMK 板载槽位 M{macroUp.MacroSlot}");
                        }
                    }
                }
            }
        }

        if (update.Lighting is var (_, brightness))
        {
            byte val = (byte)Math.Clamp(brightness, 0, 255);
            byte targetChannel = 0x03;
            foreach (byte channel in LightingChannelPriority)
            {
                var probe = SendViaCommand(ch, CmdCustomGetValue, [channel, 0x01]);
                if (probe != null)
                {
                    targetChannel = channel;
                    break;
                }
            }

            bool setOk = SendViaCommand(ch, CmdCustomSetValue, [targetChannel, 0x01, val]) != null;
            bool saveOk = SendViaCommand(ch, CmdCustomSave, [targetChannel]) != null;
            if (setOk)
                notes.Add($"已设置键盘亮度 {val}/255{(saveOk ? " 并写入板载" : "")}");
            else
                errors.Add("发送 VIA 亮度指令失败");
        }

        var refreshed = QueryCapabilities(deviceId);
        return errors.Count == 0
            ? new DeviceTuningResult(true, notes.Count > 0 ? string.Join("；", notes) : "已更新 VIA 配置", refreshed)
            : new DeviceTuningResult(false, string.Join("；", errors), refreshed);
    }

    internal static byte[] EncodeViaMacroBytes(OnboardMacroDefinition macro)
    {
        var bytes = new List<byte>(macro.Steps.Count * 4);
        foreach (var step in macro.Steps)
        {
            if (step.KeyCode is >= 0x04 and < 0xF0)
            {
                // QMK send_string byte sequence: SS_QMK_PREFIX(0x01) + SS_DOWN_CODE(0x02)/SS_UP_CODE(0x03) + HID usage
                bytes.Add(0x01);
                bytes.Add(step.IsPress ? (byte)0x02 : (byte)0x03);
                bytes.Add(step.KeyCode);
            }
            else if (step.KeyCode != 0)
            {
                continue;
            }
            if (step.DelayMs > 0)
            {
                // QMK SS_DELAY_CODE(0x04) expects ASCII decimal digits terminated by non-digit delimiter '|'
                bytes.Add(0x01);
                bytes.Add(0x04);
                string msText = Math.Clamp(step.DelayMs, 1, 9999).ToString(CultureInfo.InvariantCulture);
                foreach (char c in msText)
                    bytes.Add((byte)c);
                bytes.Add((byte)'|');
            }
        }
        return bytes.ToArray();
    }

    internal static byte[]? BuildUpdatedViaMacroBuffer(
        byte[] existingBuffer,
        int slotIndex,
        int slotCount,
        byte[] encodedMacro,
        int bufferSize,
        out string? error)
    {
        error = null;
        if (slotCount <= 0 || bufferSize <= slotCount || (uint)slotIndex >= (uint)slotCount)
        {
            error = "VIA 宏槽位或缓冲区参数无效";
            return null;
        }

        var slots = new List<byte[]>(slotCount);
        int cursor = 0;
        for (int i = 0; i < slotCount; i++)
        {
            int end = cursor;
            while (end < existingBuffer.Length && existingBuffer[end] != 0)
                end++;
            int len = Math.Max(0, end - cursor);
            var slice = new byte[len];
            if (len > 0)
                Array.Copy(existingBuffer, cursor, slice, 0, len);
            slots.Add(slice);
            cursor = end < existingBuffer.Length ? end + 1 : existingBuffer.Length;
        }

        slots[slotIndex] = encodedMacro;

        int required = slotCount + slots.Sum(s => s.Length);
        if (required > bufferSize)
        {
            error = $"所有 VIA 宏总长度 ({required} B) 超出硬件 EEPROM 容量 ({bufferSize} B)";
            return null;
        }

        var result = new byte[bufferSize];
        int pos = 0;
        for (int i = 0; i < slotCount; i++)
        {
            if (slots[i].Length > 0)
            {
                Array.Copy(slots[i], 0, result, pos, slots[i].Length);
                pos += slots[i].Length;
            }
            result[pos++] = 0x00;
        }
        result[bufferSize - 1] = 0x00;
        return result;
    }

    private static (int MacroCount, int BufferSize) QueryMacroInfo(HidChannel ch)
    {
        int count = 0;
        int bufSize = 0;
        byte[]? cResp = SendViaCommand(ch, CmdMacroGetCount);
        if (cResp != null)
        {
            int o = (cResp.Length >= 33 && cResp[0] == 0x00) ? 1 : 0;
            if (cResp.Length >= o + 2 && cResp[o] == CmdMacroGetCount)
                count = cResp[o + 1];
        }
        byte[]? sResp = SendViaCommand(ch, CmdMacroGetBufferSize);
        if (sResp != null)
        {
            int o = (sResp.Length >= 33 && sResp[0] == 0x00) ? 1 : 0;
            if (sResp.Length >= o + 3 && sResp[o] == CmdMacroGetBufferSize)
                bufSize = (sResp[o + 1] << 8) | sResp[o + 2];
        }
        return (count, bufSize);
    }

    private static byte[]? ReadViaMacroBuffer(HidChannel ch, int bufferSize)
    {
        var buf = new byte[bufferSize];
        for (int offset = 0; offset < bufferSize; offset += ViaMacroChunkMax)
        {
            int len = Math.Min(ViaMacroChunkMax, bufferSize - offset);
            byte[]? resp = SendViaCommand(ch, CmdMacroGetBuffer, [(byte)(offset >> 8), (byte)(offset & 0xFF), (byte)len]);
            if (resp == null)
                return null;
            int o = (resp.Length >= 33 && resp[0] == 0x00) ? 1 : 0;
            if (resp.Length < o + 4 + len || resp[o] != CmdMacroGetBuffer)
                return null;
            Array.Copy(resp, o + 4, buf, offset, len);
        }
        return buf;
    }

    private static bool WriteViaMacroBuffer(HidChannel ch, byte[] buffer)
    {
        for (int offset = 0; offset < buffer.Length; offset += ViaMacroChunkMax)
        {
            int len = Math.Min(ViaMacroChunkMax, buffer.Length - offset);
            var payload = new byte[3 + len];
            payload[0] = (byte)(offset >> 8);
            payload[1] = (byte)(offset & 0xFF);
            payload[2] = (byte)len;
            Array.Copy(buffer, offset, payload, 3, len);
            if (SendViaCommand(ch, CmdMacroSetBuffer, payload) == null)
                return false;
        }
        return true;
    }

    private static bool TryParseVidPid(string deviceId, out ushort vid, out ushort pid)
        => ProviderIdHelper.TryParseVidPid(deviceId, out vid, out pid);

    private static (ushort? Version, int? Layers) ProbeViaInfo(HidDevice iface)
    {
        using var ch = HidChannel.TryOpen(iface);
        if (ch == null)
            return (null, null);

        ushort? ver = null;
        int? layers = null;

        byte[]? verResp = SendViaCommand(ch, CmdGetProtocolVersion);
        if (verResp != null)
            ver = ParseViaProtocolVersion(verResp);

        byte[]? layerResp = SendViaCommand(ch, CmdGetLayerCount);
        if (layerResp != null)
            layers = ParseViaLayerCount(layerResp);

        return (ver, layers);
    }

    internal static byte[] BuildLightingPacket(int outLen, byte cmd, ReadOnlySpan<byte> payload)
    {
        int len = outLen >= 33 ? outLen : (outLen == 32 ? 32 : 33);
        var pkt = new byte[len];
        int offset = len >= 33 ? 1 : 0;
        pkt[offset] = cmd;
        if (!payload.IsEmpty && offset + 1 + payload.Length <= len)
            payload.CopyTo(pkt.AsSpan(offset + 1));
        return pkt;
    }

    private static byte[]? SendViaCommand(HidChannel ch, byte cmd, ReadOnlySpan<byte> payload = default)
    {
        var pkt = BuildLightingPacket(ch.OutputLength, cmd, payload);
        if (!ch.WriteOutput(pkt))
            return null;

        var dl = Environment.TickCount64 + 180;
        while (Environment.TickCount64 < dl)
        {
            var r = ch.Read(60);
            if (r == null) continue;
            int offset = (r.Length >= 33 && r[0] == 0x00) ? 1 : 0;
            if (r.Length > offset && r[offset] == cmd)
                return r;
        }
        return null;
    }

    internal static ushort? ParseViaProtocolVersion(ReadOnlySpan<byte> resp)
    {
        int o = (resp.Length >= 33 && resp[0] == 0x00) ? 1 : 0;
        if (resp.Length < o + 3 || resp[o] != CmdGetProtocolVersion)
            return null;
        return (ushort)((resp[o + 1] << 8) | resp[o + 2]);
    }

    internal static int? ParseViaLayerCount(ReadOnlySpan<byte> resp)
    {
        int o = (resp.Length >= 33 && resp[0] == 0x00) ? 1 : 0;
        if (resp.Length < o + 2 || resp[o] != CmdGetLayerCount)
            return null;
        int count = resp[o + 1];
        return count is >= 1 and <= 16 ? count : null;
    }

    private static string InferTransport(DeviceCatalogEntry? cat, string? rawName, string modelName)
    {
        if (cat != null && !string.IsNullOrWhiteSpace(cat.Transport))
            return cat.Transport;
        string combined = $"{rawName} {modelName}";
        if (combined.Contains("Dongle", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("2.4G", StringComparison.OrdinalIgnoreCase))
            return "2.4G";
        if (combined.Contains("BT", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            return "Bluetooth";
        return "USB";
    }

    internal static bool IsKeychronMouseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)) return false;
        return name.Contains("Mouse", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Lemokey", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(name, @"\bM[1-7]\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string? BuildNote(ushort? ver, int? layers, bool hasVia)
    {
        if (ver.HasValue && layers.HasValue)
            return $"VIA 协议 v0x{ver.Value:X4} · {layers.Value} 层动态按键";
        if (ver.HasValue)
            return $"VIA 协议 v0x{ver.Value:X4}";
        return hasVia ? "已接入 QMK/VIA 通道，可通过网页驱动自定义改键/灯效" : null;
    }
}
