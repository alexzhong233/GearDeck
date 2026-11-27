using System.Text.RegularExpressions;
using GearDeck.Core.Models;

namespace GearDeck.Core.Services;

/// <summary>
/// 设备归一与路由表结果：合并同一物理设备的有线充电端与 2.4G 无线端，保留稳定主 ID 并将调参路由至活跃通道。
/// </summary>
public sealed record DeviceUnificationResult(
    List<PeripheralDevice> Devices,
    Dictionary<string, string> ActiveTransportRouting);

/// <summary>
/// 跨品牌双模/三模外设归一器：当外设通过 USB 有线连接（或充电）且其 2.4G 接收器同时插在电脑上时，
/// 按显式双 PID 映射表及严格型号键将其合并为单台设备，防止任务栏与设备页出现重复卡片。
/// </summary>
public static partial class DeviceUnifier
{
    private static readonly (ushort VendorId, ushort WiredPid, ushort WirelessPid)[] KnownTransportPairs =
    [
        // Logitech
        (0x046D, 0xC0A9, 0xC54F), // PRO X3 SUPERSTRIKE (USB vs LIGHTSPEED 2.4G)

        // Razer
        (0x1532, 0x00C0, 0x00C1), // Viper V3 Pro
        (0x1532, 0x00B6, 0x00B7), // DeathAdder V3 Pro
        (0x1532, 0x00A5, 0x00A6), // Viper V2 Pro
        (0x1532, 0x00AA, 0x00AB), // Basilisk V3 Pro
        (0x1532, 0x00B2, 0x00B3), // DeathAdder V3 HyperSpeed
        (0x1532, 0x007A, 0x007B), // Viper Ultimate
        (0x1532, 0x007C, 0x007D), // DeathAdder V2 Pro
        (0x1532, 0x0086, 0x0088), // Basilisk Ultimate

        // Angry Miao
        (0x0E8D, 0x0880, 0x0703), // AM Infinity .97
        (0x0E8D, 0x0881, 0x0704), // AM Infinity .100R2
        (0x3151, 0x402A, 0x5007), // AM Infinity Mouse (AM30)
        (0x3151, 0x3010, 0x5007), // AM Infinity Wired vs Dongle

        // AULA
        (0x38A6, 0x2F0C, 0x2F1A), // AULA 昆仑 L5 Ultra
        (0x258A, 0x0136, 0x0138), // AULA SC380 Pro

        // ROG
        (0x0B05, 0x1B78, 0x1B7A), // ROG 游侠 2 RX 98 无线
        (0x0B05, 0x1AAE, 0x1AB0), // ROG 游侠 2 98 无线
        (0x0B05, 0x1B04, 0x1B06), // ROG 魔导士 RX Low Profile
        (0x0B05, 0x193C, 0x193E), // ROG Falchion
        (0x0B05, 0x1A83, 0x1A85), // ROG Azoth
        (0x0B05, 0x1B3F, 0x1B42), // ROG Azoth Extreme
        (0x0B05, 0x1CEF, 0x1CF1), // ROG Azoth Extreme SE
        (0x0B05, 0x1C24, 0x1C25), // ROG Azoth X
        (0x0B05, 0x1B63, 0x1B65), // ROG Harpe Ace Mini
        (0x0B05, 0x1A92, 0x1A94), // ROG Harpe Ace Aim Lab
        (0x0B05, 0x1B16, 0x1B1A), // ROG Keris II Ace
        (0x0B05, 0x1B18, 0x1B1A), // ROG Keris II Ace
        (0x0B05, 0x1A66, 0x1A68), // ROG Keris Wireless Aimpoint
        (0x0B05, 0x1A66, 0x1A6A), // ROG Keris Wireless Aimpoint
        (0x0B05, 0x1C0C, 0x1C0E), // ROG Keris II Origin
        (0x0B05, 0x1A70, 0x1A72), // ROG Gladius III Aimpoint
        (0x0B05, 0x197D, 0x197F), // ROG Gladius III Wireless
        (0x0B05, 0x1A18, 0x1A1A), // ROG Chakram X

        // Lamzu
        (0x37B0, 0x0007, 0x0008),
        (0x37B0, 0x0009, 0x000A),
    ];

    private static readonly (ushort WiredVid, ushort WiredPid, ushort WirelessVid, ushort WirelessPid)[] CrossVidTransportPairs =
    [
        // MLOONG
        (0xA8A4, 0x2255, 0xA8A5, 0x2255), // MX301 PRO / V2
        (0xA8A4, 0xFB36, 0xA8A5, 0xFB35), // MX301 PRO X

        // LEOBOG
        (0x258A, 0x010C, 0x3554, 0xFA09), // Hi98C Pro USB-C <-> 2.4G
        (0x258A, 0x010C, 0x3554, 0xFA07), // Hi98C Pro USB-C <-> Bluetooth
    ];

    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "logitech", "razer", "wlmouse", "lamzu", "atk", "vxe", "vgn", "mchose",
        "angry", "miao", "angrymiao", "aula", "rog", "asus", "flydigi",
        "corsair", "steelseries", "hyperx", "sony",
        "wired", "wireless", "dongle", "receiver", "omni", "lightspeed", "hyperpolling",
        "8k", "4k", "2.4g", "2.4ghz", "usb", "bt", "bluetooth",
        "gaming", "mouse", "keyboard", "headset", "controller",
    };

    private static readonly string[] CjkNoiseSubstrings =
    [
        "有线", "无线", "接收器", "磁轴", "鼠标", "键盘", "耳机", "手柄"
    ];

    public static bool IsKnownTransportPair(ushort vid, ushort wiredPid, ushort wirelessPid)
        => IsKnownTransportPair(vid, wiredPid, vid, wirelessPid);

    public static bool IsKnownTransportPair(ushort wiredVid, ushort wiredPid, ushort wirelessVid, ushort wirelessPid)
    {
        if (wiredVid == wirelessVid)
        {
            foreach (var pair in KnownTransportPairs)
            {
                if (pair.VendorId == wiredVid && pair.WiredPid == wiredPid && pair.WirelessPid == wirelessPid)
                    return true;
            }
        }
        foreach (var pair in CrossVidTransportPairs)
        {
            if (pair.WiredVid == wiredVid && pair.WiredPid == wiredPid &&
                pair.WirelessVid == wirelessVid && pair.WirelessPid == wirelessPid)
                return true;
        }
        return false;
    }

    public static bool IsKnownWiredPid(ushort? vid, ushort? pid)
    {
        if (vid is not { } v || pid is not { } p) return false;
        foreach (var pair in KnownTransportPairs)
        {
            if (pair.VendorId == v && pair.WiredPid == p)
                return true;
        }
        foreach (var pair in CrossVidTransportPairs)
        {
            if (pair.WiredVid == v && pair.WiredPid == p)
                return true;
        }
        return false;
    }

    public static bool IsKnownWirelessPid(ushort? vid, ushort? pid)
    {
        if (vid is not { } v || pid is not { } p) return false;
        foreach (var pair in KnownTransportPairs)
        {
            if (pair.VendorId == v && pair.WirelessPid == p)
                return true;
        }
        foreach (var pair in CrossVidTransportPairs)
        {
            if (pair.WirelessVid == v && pair.WirelessPid == p)
                return true;
        }
        return false;
    }

    public static string NormalizeModelKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        string trimmed = name.Trim().ToLowerInvariant();
        if (FallbackVidPidRegex().IsMatch(trimmed) || FallbackShortHexRegex().IsMatch(trimmed))
            return "";

        trimmed = trimmed
            .Replace("2.4ghz", " ", StringComparison.Ordinal)
            .Replace("2.4g", " ", StringComparison.Ordinal);

        foreach (string cjk in CjkNoiseSubstrings)
            trimmed = trimmed.Replace(cjk, " ", StringComparison.Ordinal);

        var cleaned = new System.Text.StringBuilder(trimmed.Length);
        foreach (char c in trimmed)
        {
            if (c is '(' or ')' or '（' or '）' or '[' or ']' or '/' or '\\' or '-' or '_' or ':' or '·')
                cleaned.Append(' ');
            else
                cleaned.Append(c);
        }

        var parts = cleaned.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var kept = new System.Text.StringBuilder();
        foreach (string token in parts)
        {
            if (NoiseTokens.Contains(token))
                continue;
            foreach (char c in token)
            {
                if (char.IsLetterOrDigit(c) || c == '+' || (c >= '\u4e00' && c <= '\u9fff'))
                    kept.Append(c);
            }
        }

        string key = kept.ToString();
        if (key.Length < 3 || PureHexKeyRegex().IsMatch(key))
            return "";

        return key;
    }

    public static DeviceUnificationResult Unify(IReadOnlyList<PeripheralDevice> rawDevices)
    {
        var output = new List<PeripheralDevice>(rawDevices.Count);
        var routing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (rawDevices.Count <= 1)
        {
            output.AddRange(rawDevices);
            return new DeviceUnificationResult(output, routing);
        }

        var wiredIndices = new List<int>();
        var wirelessIndices = new List<int>();
        for (int i = 0; i < rawDevices.Count; i++)
        {
            var d = rawDevices[i];
            if (IsWiredEndpoint(d))
                wiredIndices.Add(i);
            else if (IsWirelessDongleEndpoint(d))
                wirelessIndices.Add(i);
        }

        var pairedWireless = new HashSet<int>();
        var mergedByWirelessIdx = new Dictionary<int, PeripheralDevice>();
        var consumedWiredIdx = new HashSet<int>();

        // 第 1 轮：按显式 VID + (WiredPID, WirelessPID) 双模映射精确配对
        foreach (int wIdx in wiredIndices)
        {
            var wired = rawDevices[wIdx];
            if (wired.VendorId is not { } wVid || wired.ProductId is not { } wPid)
                continue;

            foreach (int wlIdx in wirelessIndices)
            {
                if (pairedWireless.Contains(wlIdx) || wlIdx == wIdx) continue;
                var wireless = rawDevices[wlIdx];
                if (wired.Kind != wireless.Kind) continue;
                if (wireless.VendorId is not { } wlVid || wireless.ProductId is not { } wlPid) continue;

                if (IsKnownTransportPair(wVid, wPid, wlVid, wlPid))
                {
                    pairedWireless.Add(wlIdx);
                    consumedWiredIdx.Add(wIdx);
                    mergedByWirelessIdx[wlIdx] = MergePair(wired, wireless, routing);
                    break;
                }
            }
        }

        // 第 2 轮：对未在显式表中的设备，按兼容厂商 + 精确归一化型号键配对
        foreach (int wIdx in wiredIndices)
        {
            if (consumedWiredIdx.Contains(wIdx)) continue;
            var wired = rawDevices[wIdx];
            string wiredKey = NormalizeModelKey(wired.Name);
            if (string.IsNullOrEmpty(wiredKey)) continue;

            foreach (int wlIdx in wirelessIndices)
            {
                if (pairedWireless.Contains(wlIdx) || wlIdx == wIdx) continue;
                var wireless = rawDevices[wlIdx];
                if (wired.Kind != wireless.Kind) continue;
                if (!AreBrandsCompatible(wired, wireless)) continue;

                string wirelessKey = NormalizeModelKey(wireless.Name);
                if (!string.IsNullOrEmpty(wirelessKey) && string.Equals(wiredKey, wirelessKey, StringComparison.Ordinal))
                {
                    pairedWireless.Add(wlIdx);
                    consumedWiredIdx.Add(wIdx);
                    mergedByWirelessIdx[wlIdx] = MergePair(wired, wireless, routing);
                    break;
                }
            }
        }

        for (int i = 0; i < rawDevices.Count; i++)
        {
            if (consumedWiredIdx.Contains(i))
                continue;

            output.Add(mergedByWirelessIdx.TryGetValue(i, out var merged) ? merged : rawDevices[i]);
        }

        return new DeviceUnificationResult(output, routing);
    }

    private static bool AreBrandsCompatible(PeripheralDevice a, PeripheralDevice b)
    {
        if (string.Equals(a.Provider, b.Provider, StringComparison.OrdinalIgnoreCase))
            return true;
        return a.VendorId.HasValue && b.VendorId.HasValue && a.VendorId == b.VendorId;
    }

    private static bool IsBluetooth(PeripheralDevice d)
    {
        string t = d.Transport ?? "";
        return t.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Trim(), "BT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWiredEndpoint(PeripheralDevice d)
    {
        if (IsBluetooth(d))
            return false;
        if (IsKnownWiredPid(d.VendorId, d.ProductId))
            return true;
        if (IsKnownWirelessPid(d.VendorId, d.ProductId))
            return false;

        string t = d.Transport ?? "";
        if (t.Contains("2.4G", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Omni", StringComparison.OrdinalIgnoreCase))
            return false;

        return t.Contains("USB", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Wired", StringComparison.OrdinalIgnoreCase)
            || t.Contains("有线", StringComparison.Ordinal)
            || d.Name.Contains("Wired", StringComparison.OrdinalIgnoreCase)
            || d.Name.Contains("有线", StringComparison.Ordinal);
    }

    private static bool IsWirelessDongleEndpoint(PeripheralDevice d)
    {
        if (IsBluetooth(d) || IsKnownWiredPid(d.VendorId, d.ProductId))
            return false;
        if (IsKnownWirelessPid(d.VendorId, d.ProductId))
            return true;

        string t = d.Transport ?? "";
        return t.Contains("2.4G", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Omni", StringComparison.OrdinalIgnoreCase);
    }

    private static PeripheralDevice MergePair(
        PeripheralDevice wired,
        PeripheralDevice wireless,
        Dictionary<string, string> routing)
    {
        bool wiredActive = !wired.IsSleeping
            || wired.BatteryPercent.HasValue
            || wired.State is BatteryState.Charging or BatteryState.FullyCharged;

        var activeState = wiredActive && wired.State != BatteryState.Unknown
            ? wired.State
            : wireless.State;

        string effectiveTransport = wiredActive
            ? activeState switch
            {
                BatteryState.Charging => "USB 充电中",
                BatteryState.FullyCharged => "USB 已充满",
                _ => "USB 有线",
            }
            : wireless.Transport;

        string? mergedNote = wiredActive ? wired.Note : wireless.Note;
        if (wiredActive && !string.IsNullOrWhiteSpace(wireless.Note) &&
            wireless.Note.Contains("备用电池仓", StringComparison.Ordinal))
        {
            mergedNote = string.IsNullOrWhiteSpace(wired.Note)
                ? wireless.Note
                : $"{wired.Note} · {wireless.Note}";
        }

        if (wiredActive && !wired.IsSleeping &&
            !string.Equals(wired.Id, wireless.Id, StringComparison.OrdinalIgnoreCase))
        {
            routing[wireless.Id] = wired.Id;
        }

        return new PeripheralDevice
        {
            Id = wireless.Id,
            Name = PickPreferredName(wired.Name, wireless.Name),
            Kind = wireless.Kind,
            Provider = !string.IsNullOrWhiteSpace(wireless.Provider) ? wireless.Provider : wired.Provider,
            BatteryPercent = wiredActive ? (wired.BatteryPercent ?? wireless.BatteryPercent) : wireless.BatteryPercent,
            State = activeState,
            Connected = wired.Connected || wireless.Connected,
            IsSleeping = wiredActive ? wired.IsSleeping : wireless.IsSleeping,
            Transport = effectiveTransport,
            Note = mergedNote,
            VendorId = wireless.VendorId ?? wired.VendorId,
            ProductId = wireless.ProductId ?? wired.ProductId,
            LeftEarPercent = wired.LeftEarPercent ?? wireless.LeftEarPercent,
            LeftEarCharging = wired.LeftEarCharging || wireless.LeftEarCharging,
            RightEarPercent = wired.RightEarPercent ?? wireless.RightEarPercent,
            RightEarCharging = wired.RightEarCharging || wireless.RightEarCharging,
            CaseBatteryPercent = wired.CaseBatteryPercent ?? wireless.CaseBatteryPercent,
            LastUpdated = wired.LastUpdated >= wireless.LastUpdated ? wired.LastUpdated : wireless.LastUpdated,
        };
    }

    private static string PickPreferredName(string wiredName, string wirelessName)
    {
        static int Score(string s)
        {
            int score = s.Length;
            if (!s.Contains("Wired", StringComparison.OrdinalIgnoreCase) &&
                !s.Contains("有线", StringComparison.Ordinal) &&
                !s.Contains("Receiver", StringComparison.OrdinalIgnoreCase) &&
                !s.Contains("Dongle", StringComparison.OrdinalIgnoreCase) &&
                !s.Contains("接收器", StringComparison.Ordinal))
            {
                score += 100;
            }
            return score;
        }

        return Score(wirelessName) >= Score(wiredName) ? wirelessName : wiredName;
    }

    [GeneratedRegex(@"^[a-z]*\s*[0-9a-f]{4}[:\-][0-9a-f]{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex FallbackVidPidRegex();

    [GeneratedRegex(@"^(razer|logitech|atk|aul[a]?|compx|mchose|rog)\s+[0-9a-f]{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex FallbackShortHexRegex();

    [GeneratedRegex(@"^[0-9a-f]{4,8}$", RegexOptions.IgnoreCase)]
    private static partial Regex PureHexKeyRegex();
}
