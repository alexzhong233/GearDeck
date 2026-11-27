using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// 系统蓝牙电量（Windows PnP DEVPKEY_Bluetooth_Battery）与标准 HID Power Device 通用电量补充：
/// 自动去重已由各品牌专属驱动认领的 VID/PID，让未做专属适配的蓝牙鼠标/键盘/耳机也能显示实时电量。
/// </summary>
public sealed class BluetoothBatteryProvider : IPeripheralProvider
{
    public const string ProviderName = "Bluetooth";

    private static readonly DEVPROPKEY DevPKeyBluetoothBattery = new()
    {
        fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
        pid = 2,
    };

    private static readonly DEVPROPKEY DevPKeyFriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 14,
    };

    private static readonly HashSet<ushort> DedicatedVids =
    [
        0x046D, 0x1532, 0x0B05, 0x054C, 0x37D7, 0x373B, 0x34D7, 0x3837,
        0x5253, 0x38A6, 0x384C, 0x3151, 0x36A7, 0x37B0, 0x373E, 0x1B1C, 0x1038, 0x03F0, 0x0951,
        0xA8A4, 0xA8A5, 0x258A, 0x3554,
        0x3710, 0x3367, 0x361D, 0x3434, 0x31E3, 0x03EB, 0x352D, 0x19F5, 0x057E, 0x2DC8, 0x291D,
    ];

    public string Name => ProviderName;

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("bt:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                foreach (string instanceId in GetPresentBluetoothDeviceIds())
                {
                    if (!TryReadDevNodeBattery(instanceId, out int pct))
                        continue;
                    string friendly = ReadDevNodeName(instanceId) ?? ExtractReadableId(instanceId);
                    if (string.IsNullOrWhiteSpace(friendly) || !seenNames.Add(friendly))
                        continue;

                    // 若同名称已被更精确的高级蓝牙服务节点读到，跳过重复项
                    if (friendly.Contains("AVRCP", StringComparison.OrdinalIgnoreCase) ||
                        friendly.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase))
                        continue;

                    results.Add(new PeripheralDevice
                    {
                        Id = $"bt:{SanitizeKey(friendly)}",
                        Name = friendly,
                        Kind = InferKind(friendly),
                        Provider = ProviderName,
                        Connected = true,
                        IsSleeping = false,
                        Transport = "Bluetooth",
                        BatteryPercent = Math.Clamp(pct, 0, 100),
                        State = BatteryState.Normal,
                        Note = "通过 Windows 蓝牙 BAS / HFP 标准服务读取电量",
                        LastUpdated = DateTimeOffset.Now,
                    });
                }
            }
            catch (Exception ex)
            {
                Diag.Log($"BluetoothBattery: {ex.Message}");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            PollMacOsBluetooth(results, seenNames);
        }

        // 同时兜底标准 HID Power / Generic Battery Strength (0x0084 / 0x0085 / 0x0006:0x0020)
        try
        {
            foreach (var g in HidSafe.EnumerateDevices()
                         .Where(d => !DedicatedVids.Contains((ushort)d.VendorID))
                         .Where(d =>
                         {
                             var u = HidSafe.PrimaryUsage(d);
                             return u.UsagePage is 0x0084 or 0x0085 || (u.UsagePage == 0x0006 && u.Usage == 0x0020);
                         })
                         .GroupBy(d => ((ushort)d.VendorID, (ushort)d.ProductID)))
            {
                var (vid, pid) = g.Key;
                var dev = g.First();
                string name = HidSafe.ProductName(dev);
                if (string.IsNullOrWhiteSpace(name) || results.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                int? pct = null;
                foreach (var d in g)
                {
                    if (HidSafe.MaxFeatureLen(d) >= 2)
                    {
                        using var raw = RawHidFeature.TryOpen(d.DevicePath);
                        if (raw != null)
                        {
                            var ids = HidReportInspector.GetReportIds(d);
                            foreach (byte fid in ids.FeatureIds.Take(2))
                            {
                                var r = raw.GetFeature(HidSafe.MaxFeatureLen(d), fid);
                                if (r != null && r.Length >= 2 && r[1] is >= 1 and <= 100)
                                {
                                    pct = r[1];
                                    break;
                                }
                            }
                        }
                    }
                    if (pct != null) break;
                }

                if (pct == null) continue;
                results.Add(new PeripheralDevice
                {
                    Id = $"bt:hid-{vid:X4}-{pid:X4}",
                    VendorId = vid,
                    ProductId = pid,
                    Name = name,
                    Kind = InferKind(name),
                    Provider = ProviderName,
                    Connected = true,
                    IsSleeping = false,
                    Transport = "HID / BT",
                    BatteryPercent = pct,
                    State = BatteryState.Normal,
                    Note = "通过标准 HID Battery Strength 协议读取电量",
                    LastUpdated = DateTimeOffset.Now,
                });
            }
        }
        catch
        {
        }

        return results;
    }

    private static void PollMacOsBluetooth(List<PeripheralDevice> results, HashSet<string> seenNames)
    {
        try
        {
            var psi = new ProcessStartInfo("ioreg", "-rn AppleDeviceManagementHIDEventService")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            string output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(1500))
            {
                try { proc.Kill(); } catch { }
                return;
            }

            string? curProduct = null;
            int? curBattery = null;
            void Flush()
            {
                if (string.IsNullOrWhiteSpace(curProduct) || curBattery is not { } pct) return;
                string name = curProduct.Trim();
                if (!seenNames.Add(name)) return;
                results.Add(new PeripheralDevice
                {
                    Id = $"bt:{SanitizeKey(name)}",
                    Name = name,
                    Kind = InferKind(name),
                    Provider = ProviderName,
                    Connected = true,
                    IsSleeping = false,
                    Transport = "Bluetooth",
                    BatteryPercent = Math.Clamp(pct, 0, 100),
                    State = BatteryState.Normal,
                    Note = "通过 macOS IOKit 蓝牙 HID 服务读取电量",
                    LastUpdated = DateTimeOffset.Now,
                });
            }

            foreach (string rawLine in output.Split('\n'))
            {
                if (rawLine.Contains("+-o "))
                {
                    Flush();
                    curProduct = null;
                    curBattery = null;
                    continue;
                }

                var pm = Regex.Match(rawLine, "\"Product\"\\s*=\\s*\"([^\"]+)\"");
                if (pm.Success) curProduct = pm.Groups[1].Value;

                var bm = Regex.Match(rawLine, "\"BatteryPercent\"\\s*=\\s*(\\d+)");
                if (bm.Success && int.TryParse(bm.Groups[1].Value, out int bp)) curBattery = bp;
            }
            Flush();
        }
        catch (Exception ex)
        {
            Diag.Log($"macOS BluetoothBattery: {ex.Message}");
        }
    }

    private static List<string> GetPresentBluetoothDeviceIds()
    {
        var list = new List<string>();
        foreach (string filter in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE" })
        {
            if (CM_Get_Device_ID_List_SizeW(out uint size, filter, CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT) != 0 || size <= 1)
                continue;

            var buf = new char[size];
            if (CM_Get_Device_ID_ListW(filter, buf, size, CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT) != 0)
                continue;

            int start = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i] == '\0')
                {
                    if (i > start)
                        list.Add(new string(buf, start, i - start));
                    start = i + 1;
                    if (start < buf.Length && buf[start] == '\0')
                        break;
                }
            }
        }
        return list;
    }

    private static bool TryReadDevNodeBattery(string instanceId, out int percent)
    {
        percent = 0;
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0)
            return false;

        var key = DevPKeyBluetoothBattery;
        byte[] val = new byte[4];
        uint size = (uint)val.Length;
        int cr = CM_Get_DevNode_PropertyW(devInst, ref key, out uint propType, val, ref size, 0);
        if (cr != 0 || size == 0) return false;
        percent = val[0];
        return percent is >= 0 and <= 100 && propType is 0x03 or 0x07; // DEVPROP_TYPE_BYTE or UINT32
    }

    private static string? ReadDevNodeName(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0)
            return null;
        var key = DevPKeyFriendlyName;
        uint size = 512;
        byte[] buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out _, buf, ref size, 0) != 0 || size < 2)
            return null;
        return System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0', ' ');
    }

    private static DeviceKind InferKind(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("keyboard") || lower.Contains("键盘") || lower.Contains("keychron") || lower.Contains("nuphy"))
            return DeviceKind.Keyboard;
        if (lower.Contains("bud") || lower.Contains("pod") || lower.Contains("head") || lower.Contains("wh-") ||
            lower.Contains("wf-") || lower.Contains("ear") || lower.Contains("audio") || lower.Contains("耳机"))
            return DeviceKind.Headset;
        if (lower.Contains("controller") || lower.Contains("gamepad") || lower.Contains("xbox") ||
            lower.Contains("dualsense") || lower.Contains("switch pro") || lower.Contains("手柄"))
            return DeviceKind.Controller;
        return DeviceKind.Mouse;
    }

    private static string ExtractReadableId(string instanceId)
        => instanceId.Split('\\').LastOrDefault()?.Split('_').FirstOrDefault() ?? instanceId;

    private static string SanitizeKey(string s)
        => new string(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');

    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? pszFilter, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(
        uint dnDevInst,
        ref DEVPROPKEY propertyKey,
        out uint propertyType,
        [Out] byte[] propertyBuffer,
        ref uint propertyBufferSize,
        uint ulFlags);
}
