using System.Runtime.InteropServices;
using HidSharp;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;
using GearDeck.Core.Services;

namespace GearDeck.Core.Providers;

/// <summary>
/// 手柄通用电量提供器：
/// 1. Sony DualShock 4 / DualSense / DualSense Edge (054C:05C4/09CC/0BA0/0CE6/0DF2) 被动 Input Report 解析；
/// 2. Nintendo Switch Pro / Joy-Con (057E:2009/2006/2007) 原生 HID 电量解析；
/// 3. 8BitDo (2DC8) 手柄识别 + Windows XInput 无线 Xbox / 第三方手柄电量查询（自动去重 Flydigi 0x37D7）。
/// </summary>
public sealed class GamepadBatteryProvider : IPeripheralProvider
{
    public const string ProviderName = "Gamepad";
    public string Name => ProviderName;

    private static readonly HashSet<ushort> Ds4Pids = [0x05C4, 0x09CC, 0x0BA0];
    private static readonly HashSet<ushort> DualSensePids = [0x0CE6, 0x0DF2];
    private static readonly HashSet<ushort> NintendoSwitchPids = [0x2009, 0x2006, 0x2007];

    public bool CanHandleDevice(string deviceId)
        => deviceId.StartsWith("ps:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("nintendo:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("8bitdo:", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("xinput:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PeripheralDevice> Enumerate()
    {
        var results = new List<PeripheralDevice>();
        bool hasFlydigi = HidSafe.EnumerateDevicesByVendor(0x37D7).Any(d => (d.ProductID >> 12) == 2);

        try { PollPlayStation(results); } catch (Exception ex) { Diag.Log($"DualSense/DS4: {ex.Message}"); }
        try { PollNintendo(results); } catch (Exception ex) { Diag.Log($"SwitchPro: {ex.Message}"); }
        try { Poll8BitDo(results); } catch (Exception ex) { Diag.Log($"8BitDo: {ex.Message}"); }
        if (OperatingSystem.IsWindows())
        {
            try { PollXInput(results, skipGenericIfFlydigiAwake: hasFlydigi); } catch (Exception ex) { Diag.Log($"XInput: {ex.Message}"); }
        }

        return results;
    }

    private static void PollPlayStation(List<PeripheralDevice> results)
    {
        var pads = HidSafe.EnumerateDevicesByVendor(0x054C)
            .Where(d => (Ds4Pids.Contains((ushort)d.ProductID) || DualSensePids.Contains((ushort)d.ProductID))
                        && HidSafe.PrimaryUsage(d) == (0x0001, 0x0005))
            .GroupBy(d => (ushort)d.ProductID);

        foreach (var g in pads)
        {
            ushort pid = g.Key;
            var dev = g.First();
            using var ch = HidChannel.TryOpen(dev);
            if (ch == null) continue;

            bool bt = HidSafe.MaxInputLen(dev) > 64;
            if (bt && HidSafe.MaxFeatureLen(dev) >= 16)
                _ = ch.ReadFeature(0x05);

            var dl = Environment.TickCount64 + 350;
            while (Environment.TickCount64 < dl)
            {
                var r = ch.Read(90);
                if (r == null || r.Length < 32) continue;

                int? pct = null;
                bool charging = false;

                if (Ds4Pids.Contains(pid))
                {
                    int at = r[0] == 0x01 ? 30 : r[0] == 0x11 ? 32 : 0;
                    if (at == 0 || r.Length <= at || (r[0] == 0x01 && bt)) continue;
                    int lvl = r[at] & 0x0F;
                    bool cable = (r[at] & 0x10) != 0;
                    if (pid == 0x0BA0 && r[at] == 0) break;
                    pct = cable ? (lvl >= 11 ? 100 : Math.Clamp(lvl * 10, 0, 100)) : Math.Clamp(lvl * 10 + 5, 0, 100);
                    charging = cable && lvl < 11;
                }
                else
                {
                    int at = r[0] == 0x01 ? 53 : r[0] == 0x31 ? 54 : 0;
                    if (at == 0 || r.Length <= at || (r[0] == 0x01 && bt)) continue;
                    int lvl = r[at] & 0x0F;
                    int st = r[at] >> 4;
                    pct = st == 0x02 ? 100 : Math.Clamp(lvl * 10 + 5, 0, 100);
                    charging = st is 0x01 or 0x02;
                }

                string name = Ds4Pids.Contains(pid) ? "DualShock 4"
                    : pid == 0x0DF2 ? "DualSense Edge" : "DualSense";

                results.Add(new PeripheralDevice
                {
                    Id = $"ps:054C-{pid:X4}",
                    VendorId = 0x054C,
                    ProductId = pid,
                    Name = name,
                    Kind = DeviceKind.Controller,
                    Provider = ProviderName,
                    Connected = true,
                    IsSleeping = false,
                    Transport = bt ? "Bluetooth" : "USB",
                    BatteryPercent = pct,
                    State = charging ? BatteryState.Charging : BatteryState.Normal,
                    LastUpdated = DateTimeOffset.Now,
                });
                break;
            }
        }
    }

    private static void PollNintendo(List<PeripheralDevice> results)
    {
        var pads = HidSafe.EnumerateDevicesByVendor(0x057E)
            .Where(d => NintendoSwitchPids.Contains((ushort)d.ProductID))
            .GroupBy(d => (ushort)d.ProductID);

        foreach (var g in pads)
        {
            ushort pid = g.Key;
            var dev = g.FirstOrDefault(d => HidSafe.PrimaryUsage(d) == (0x0001, 0x0005)) ?? g.First();
            bool bt = HidSafe.MaxInputLen(dev) > 64 || !dev.DevicePath.Contains("vid_057e", StringComparison.OrdinalIgnoreCase);

            int? pct = null;
            bool charging = false;

            using var ch = HidChannel.TryOpen(dev);
            if (ch != null)
            {
                var dl = Environment.TickCount64 + 350;
                while (Environment.TickCount64 < dl)
                {
                    var r = ch.Read(90);
                    if (r == null) continue;
                    if (ParseSwitchBatteryReport(r) is { } parsed)
                    {
                        pct = parsed.Percent;
                        charging = parsed.Charging;
                        break;
                    }
                }
            }

            string name = DeviceCatalogService.TryLookup(0x057E, pid, out var cat) && cat != null
                ? cat.Model
                : pid switch
                {
                    0x2009 => "Nintendo Switch Pro Controller",
                    0x2006 => "Nintendo Joy-Con (L)",
                    0x2007 => "Nintendo Joy-Con (R)",
                    _ => $"Nintendo Controller {pid:X4}",
                };

            results.Add(new PeripheralDevice
            {
                Id = $"nintendo:057E-{pid:X4}",
                VendorId = 0x057E,
                ProductId = pid,
                Name = name,
                Kind = DeviceKind.Controller,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = pct == null && bt,
                Transport = bt ? "Bluetooth" : "USB",
                BatteryPercent = pct,
                State = charging ? BatteryState.Charging : (pct.HasValue ? BatteryState.Normal : BatteryState.Unknown),
                Note = pct == null ? "已连接 Switch 控制器（按任意键激活输入帧后可读电量）" : null,
                LastUpdated = DateTimeOffset.Now,
            });
        }
    }

    internal static (int Percent, bool Charging)? ParseSwitchBatteryReport(ReadOnlySpan<byte> report)
    {
        if (report.Length < 4 || report[0] is not (0x30 or 0x21 or 0x31))
            return null;

        int nibble = (report[2] >> 4) & 0x0F;
        bool charging = (nibble & 0x01) != 0;
        int level = (nibble >> 1) & 0x07;
        int pct = level switch
        {
            0 => 5,
            1 => 20,
            2 => 45,
            3 => 75,
            _ => 100,
        };
        return (pct, charging);
    }

    private static void Poll8BitDo(List<PeripheralDevice> results)
    {
        var pads = HidSafe.EnumerateDevicesByVendor(0x2DC8)
            .GroupBy(d => (ushort)d.ProductID);

        foreach (var g in pads)
        {
            ushort pid = g.Key;
            string? rawName = g.Select(HidSafe.ProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            bool catalogHit = DeviceCatalogService.TryLookup(0x2DC8, pid, out var cat);
            string name = catalogHit && cat != null
                ? cat.Model
                : (!string.IsNullOrWhiteSpace(rawName) ? rawName.Trim() : $"8BitDo Controller {pid:X4}");
            string transport = catalogHit && cat != null && !string.IsNullOrWhiteSpace(cat.Transport)
                ? cat.Transport
                : "2.4G";

            results.Add(new PeripheralDevice
            {
                Id = $"8bitdo:2DC8-{pid:X4}",
                VendorId = 0x2DC8,
                ProductId = pid,
                Name = name,
                Kind = DeviceKind.Controller,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = false,
                Transport = transport,
                Note = "已识别 八位堂 8BitDo 控制器（若处于 XInput 模式电量将关联显示）",
                LastUpdated = DateTimeOffset.Now,
            });
        }
    }

    private static void PollXInput(List<PeripheralDevice> results, bool skipGenericIfFlydigiAwake)
    {
        for (uint slot = 0; slot < 4; slot++)
        {
            var info = new XINPUT_BATTERY_INFORMATION();
            uint err = XInputGetBatteryInformation(slot, 0x00, ref info);
            if (err != 0 || info.BatteryType == 0x00) // Disconnected
                continue;

            if (skipGenericIfFlydigiAwake && slot == 0 && info.BatteryType == 0x01)
                continue; // 飞智直连 XInput 已由 FlydigiProvider 精确读取电量与型号

            bool isWired = info.BatteryType == 0x01;
            int? pct = info.BatteryType switch
            {
                0x02 or 0x03 or 0xFF => info.BatteryLevel switch
                {
                    0 => 5,
                    1 => 35,
                    2 => 70,
                    _ => 100,
                },
                _ => null,
            };

            results.Add(new PeripheralDevice
            {
                Id = $"xinput:slot-{slot}",
                Name = isWired ? $"Xbox 手柄 #{slot + 1} (有线)" : $"Xbox 无线手柄 #{slot + 1}",
                Kind = DeviceKind.Controller,
                Provider = ProviderName,
                Connected = true,
                IsSleeping = false,
                Transport = isWired ? "USB / XInput" : "Xbox Wireless / BT",
                BatteryPercent = pct,
                State = isWired ? BatteryState.Charging : BatteryState.Normal,
                Note = isWired ? "有线供电中" : "XInput 4 挡估算电量（满/中/低/空）",
                LastUpdated = DateTimeOffset.Now,
            });
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern uint XInputGetBatteryInformation(uint dwUserIndex, byte devType, ref XINPUT_BATTERY_INFORMATION pBatteryInformation);
}
