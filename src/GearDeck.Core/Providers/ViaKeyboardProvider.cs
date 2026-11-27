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
    private const byte CmdGetLayerCount = 0x11;

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
            var viaIface = ifaces.FirstOrDefault(d => HidSafe.PrimaryUsage(d) == (ViaUsagePage, ViaUsage));
            bool catalogHit = DeviceCatalogService.TryLookup(vid, pid, out var cat) && cat?.ParsedKind == DeviceKind.Keyboard;
            bool knownBrand = KnownKeyboardBrands.TryGetValue(vid, out var brandMeta);

            if (viaIface == null && !catalogHit && !knownBrand)
                continue;

            ushort? viaVersion = null;
            int? layerCount = null;
            if (viaIface != null)
                (viaVersion, layerCount) = ProbeViaInfo(viaIface);

            string? rawName = ifaces.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            string brand = cat?.Brand is { Length: > 0 } cb ? cb : (knownBrand ? brandMeta.Brand : "VIA/QMK");
            string prefix = knownBrand ? brandMeta.IdPrefix : "via";
            string modelName = cat?.Model is { Length: > 0 } cm
                ? cm
                : (!string.IsNullOrWhiteSpace(rawName) ? rawName.Trim() : $"{brand} 键盘 {vid:X4}:{pid:X4}");

            string transport = InferTransport(cat, rawName, modelName);
            string? note = BuildNote(viaVersion, layerCount, viaIface != null);

            results.Add(new PeripheralDevice
            {
                Id = $"{prefix}:{vid:X4}-{pid:X4}",
                VendorId = vid,
                ProductId = pid,
                Name = modelName,
                Kind = DeviceKind.Keyboard,
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

        if (activeChannel == null)
        {
            caps.StatusMessage = "设备未响应 VIA 灯效频道查询。";
            return caps;
        }

        caps.IsOnline = true;
        caps.StatusMessage = $"VIA Raw HID 通道 (灯效频道 0x{activeChannel.Value:X2})";
        caps.SupportsLighting = true;
        caps.CurrentLightingBrightness = brightness;
        caps.MaxLightingBrightness = 255;
        return caps;
    }

    public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
    {
        if (update.Lighting is not var (_, brightness))
            return new DeviceTuningResult(true, "未变更 VIA 键盘灯效");

        if (!TryParseVidPid(deviceId, out ushort vid, out ushort pid))
            return new DeviceTuningResult(false, "无效的 VIA 设备标识");

        var viaIface = HidSafe.EnumerateDevices()
            .FirstOrDefault(d => (ushort)d.VendorID == vid && (ushort)d.ProductID == pid
                && HidSafe.PrimaryUsage(d) == (ViaUsagePage, ViaUsage));
        if (viaIface == null)
            return new DeviceTuningResult(false, "未找到 0xFF60:0x0061 VIA Raw HID 通道");

        bool setOk;
        bool saveOk;
        byte val = (byte)Math.Clamp(brightness, 0, 255);
        using (var ch = HidChannel.TryOpen(viaIface))
        {
            if (ch == null)
                return new DeviceTuningResult(false, "无法打开 VIA Raw HID 通道");

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

            setOk = SendViaCommand(ch, CmdCustomSetValue, [targetChannel, 0x01, val]) != null;
            saveOk = SendViaCommand(ch, CmdCustomSave, [targetChannel]) != null;
        }

        return setOk
            ? new DeviceTuningResult(true, $"已设置键盘亮度 {val}/255{(saveOk ? " 并写入板载" : "")}", QueryCapabilities(deviceId))
            : new DeviceTuningResult(false, "发送 VIA 亮度指令失败");
    }

    private static bool TryParseVidPid(string deviceId, out ushort vid, out ushort pid)
    {
        vid = 0; pid = 0;
        var parts = deviceId.Split(':');
        if (parts.Length < 2) return false;
        var vp = parts[1].Split('-');
        return vp.Length == 2
            && ushort.TryParse(vp[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid)
            && ushort.TryParse(vp[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
    }

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

    private static string? BuildNote(ushort? ver, int? layers, bool hasVia)
    {
        if (ver.HasValue && layers.HasValue)
            return $"VIA 协议 v0x{ver.Value:X4} · {layers.Value} 层动态按键";
        if (ver.HasValue)
            return $"VIA 协议 v0x{ver.Value:X4}";
        return hasVia ? "已接入 QMK/VIA 通道，可通过网页驱动自定义改键/灯效" : null;
    }
}
