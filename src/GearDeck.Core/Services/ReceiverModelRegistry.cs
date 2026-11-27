using System.Text.RegularExpressions;
using GearDeck.Core.Models;

namespace GearDeck.Core.Services;

/// <summary>
/// 跨品牌通用「接收器 ID → 真实外设型号」KV 记忆与自定义重命名注册表：
/// 1) 协议查到真实型号（如 HID++ / ATK CID / CompX 固件或双模有线归一）时自动写入映射并持久化；
/// 2) 当无线鼠标休眠、USB 口仅暴露接收器名称（如 LAMZU Crystal 8K / 2.4G Wireless Receiver）时自动回填已记住的真实型号；
/// 3) 支持用户在详情页手动重命名/绑定型号，自定义名称优先级最高。
/// </summary>
public static partial class ReceiverModelRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Custom = new(StringComparer.OrdinalIgnoreCase);

    public static void Seed(
        IDictionary<string, string>? known,
        IDictionary<string, string>? custom = null)
    {
        lock (Gate)
        {
            if (known != null)
            {
                foreach (var kv in known)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) &&
                        !string.IsNullOrWhiteSpace(kv.Value) &&
                        !IsGenericReceiverName(kv.Value))
                    {
                        Known[kv.Key] = kv.Value.Trim();
                    }
                }
            }

            if (custom != null)
            {
                foreach (var kv in custom)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        Custom[kv.Key] = kv.Value.Trim();
                }
            }
        }
    }

    public static void ClearForTest()
    {
        lock (Gate)
        {
            Known.Clear();
            Custom.Clear();
        }
    }

    public static bool IsGenericReceiverName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        string trimmed = name.Trim();
        if (HexVidPidRegex().IsMatch(trimmed) || ShortHexModelRegex().IsMatch(trimmed))
            return true;

        if (trimmed.Contains("Receiver", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Dongle", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Crystal", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Adapter", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("接收器", StringComparison.Ordinal))
        {
            return true;
        }

        if (BrandOnlyRateRegex().IsMatch(trimmed))
            return true;

        return false;
    }

    public static void ApplyAndLearn(IEnumerable<PeripheralDevice> devices)
    {
        lock (Gate)
        {
            foreach (var d in devices)
            {
                if (string.IsNullOrWhiteSpace(d.Id))
                    continue;

                if (Custom.TryGetValue(d.Id, out string? customName) && !string.IsNullOrWhiteSpace(customName))
                {
                    d.Name = customName;
                    continue;
                }

                if (!IsGenericReceiverName(d.Name))
                {
                    Known[d.Id] = d.Name.Trim();
                    continue;
                }

                if (Known.TryGetValue(d.Id, out string? learned) && !string.IsNullOrWhiteSpace(learned))
                {
                    d.Name = learned;
                }
            }
        }
    }

    public static void SetCustomName(string deviceId, string? customName)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(customName))
                Custom.Remove(deviceId);
            else
                Custom[deviceId] = customName.Trim();
        }
    }

    public static Dictionary<string, string> SnapshotKnown()
    {
        lock (Gate)
            return new Dictionary<string, string>(Known, StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^.*\b[0-9a-f]{4}[:\-][0-9a-f]{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex HexVidPidRegex();

    [GeneratedRegex(@"^[a-z0-9\-]+\s+[0-9a-f]{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex ShortHexModelRegex();

    [GeneratedRegex(@"^(atk|vxe|vgn|lamzu|wlmouse|mchose|pulsar|razer|logitech|rog)\s*(2\.4g|wireless|4k|8k)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BrandOnlyRateRegex();
}
