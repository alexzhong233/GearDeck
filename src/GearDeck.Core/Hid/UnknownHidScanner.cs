using System.Text;
using System.Text.Json;
using HidSharp;
using GearDeck.Core.Models;
using GearDeck.Core.Services;

namespace GearDeck.Core.Hid;

public sealed record UnknownHidFingerprint(
    string VidHex,
    string PidHex,
    string Manufacturer,
    string ProductName,
    string[] UsagePages,
    int MaxInput,
    int MaxOutput,
    int MaxFeature,
    int InterfaceCount);

/// <summary>
/// 扫描未被当前任何 Provider 认领的 USB/蓝牙 HID 外设接口指纹（重点捕获 UsagePage >= 0xFF00 厂商控制口），
/// 方便用户在「诊断」页一键复制 JSON 反馈给开发者快速补充新设备协议。
/// </summary>
public static class UnknownHidScanner
{
    private static readonly HashSet<ushort> IgnoredSystemVids =
    [
        0x0000,
        0x04F3, // ELAN Touchpad
        0x06CB, // Synaptics Touchpad
        0x8087, // Intel Bluetooth / Sensor Hub
    ];

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static IReadOnlyList<UnknownHidFingerprint> Scan(IReadOnlyList<PeripheralDevice> recognizedDevices)
    {
        var claimed = new HashSet<(ushort Vid, ushort Pid)>();
        foreach (var d in recognizedDevices)
        {
            if (d.VendorId is { } v && v > 0 && d.ProductId is { } p && p > 0)
            {
                claimed.Add((v, p));
            }
            else if (DeviceImageRuleMatcher.TryExtractVidPid(d, out string vh, out string ph) &&
                     ushort.TryParse(vh, System.Globalization.NumberStyles.HexNumber, null, out ushort pv) &&
                     ushort.TryParse(ph, System.Globalization.NumberStyles.HexNumber, null, out ushort pp))
            {
                claimed.Add((pv, pp));
            }
        }

        var list = new List<UnknownHidFingerprint>();
        var groups = HidSafe.EnumerateDevices()
            .Where(d => d.VendorID > 0 && d.ProductID > 0)
            .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID));

        foreach (var g in groups)
        {
            var (vid, pid) = g.Key;
            if (IgnoredSystemVids.Contains(vid) || claimed.Contains((vid, pid)))
                continue;

            var ifaces = g.ToList();
            var pages = new SortedSet<ushort>();
            int maxIn = 0;
            int maxOut = 0;
            int maxFeat = 0;

            foreach (var iface in ifaces)
            {
                var usage = HidSafe.PrimaryUsage(iface);
                if (usage.UsagePage > 0)
                    pages.Add(usage.UsagePage);
                maxIn = Math.Max(maxIn, HidSafe.MaxInputLen(iface));
                maxOut = Math.Max(maxOut, HidSafe.MaxOutputLen(iface));
                maxFeat = Math.Max(maxFeat, HidSafe.MaxFeatureLen(iface));
            }

            bool hasVendorPage = pages.Any(p => p >= 0xFF00);
            string productName = ifaces.Select(HidSafe.ProductName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
            string manufacturer = ifaces.Select(HidSafe.Manufacturer).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";

            // 过滤纯系统标准键鼠：仅保留包含厂商定制接口 (0xFFxx) 或带明确名称且具备输出/特征帧的未识别设备
            if (!hasVendorPage && (string.IsNullOrWhiteSpace(productName) || (maxOut < 8 && maxFeat < 8)))
                continue;

            if (vid == 0x045E && !hasVendorPage)
                continue;

            list.Add(new UnknownHidFingerprint(
                $"{vid:X4}",
                $"{pid:X4}",
                manufacturer.Trim(),
                productName.Trim(),
                pages.Select(p => $"0x{p:X4}").ToArray(),
                maxIn,
                maxOut,
                maxFeat,
                ifaces.Count));
        }

        return list.OrderBy(x => x.VidHex, StringComparer.Ordinal).ThenBy(x => x.PidHex, StringComparer.Ordinal).ToList();
    }

    public static string FormatSummary(IReadOnlyList<UnknownHidFingerprint> items)
    {
        if (items.Count == 0)
            return "（当前所有可定制 HID 外设均已识别或无未知厂商通道）";

        var sb = new StringBuilder();
        foreach (var item in items)
        {
            string label = !string.IsNullOrWhiteSpace(item.ProductName)
                ? item.ProductName
                : (!string.IsNullOrWhiteSpace(item.Manufacturer) ? item.Manufacturer : "未命名外设");
            string pages = item.UsagePages.Length > 0 ? string.Join(",", item.UsagePages) : "-";
            sb.AppendLine($"{item.VidHex}:{item.PidHex} | {label} | UP=[{pages}] | in={item.MaxInput} out={item.MaxOutput} feat={item.MaxFeature}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string SerializeJson(IReadOnlyList<UnknownHidFingerprint> items)
        => JsonSerializer.Serialize(items, JsonOpts);
}
