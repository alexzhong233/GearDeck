using System.Diagnostics;
using GearDeck.Core;
using GearDeck.Core.Models;

namespace GearDeck.Desktop.Services;

/// <summary>
/// 跨平台原生通知服务：Windows 走托盘气球 (App.Tray.ShowBalloon)，
/// macOS 走 osascript display notification；不依赖 Windows App SDK。
/// </summary>
public static class NativeNotificationService
{
    private static readonly object LockObj = new();
    private static readonly HashSet<string> LowBatteryAlerted = new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;

    public static void Initialize()
    {
        lock (LockObj)
        {
            _initialized = true;
        }
    }

    public static void Unregister()
    {
        lock (LockObj)
        {
            _initialized = false;
        }
    }

    public static void ShowToast(string title, string message)
    {
        if (!_initialized) Initialize();

        if (OperatingSystem.IsWindows())
        {
            App.Tray?.ShowBalloon(title, message);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var psi = new ProcessStartInfo("osascript")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(
                    $"display notification \"{EscapeAppleScript(message)}\" with title \"{EscapeAppleScript(title)}\"");
                using var process = Process.Start(psi);
                process?.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                Diag.Log($"macOS 通知发送失败: {ex.Message}");
            }
        }
    }

    private static string EscapeAppleScript(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    public static void NotifyProfileSwitch(string ruleLabel, IReadOnlyList<(string DeviceName, string TargetLabel)> switched)
    {
        if (!App.Settings.Current.NotifyOnProfileSwitch || switched.Count == 0)
            return;

        string title = $"配置已切换 · {ruleLabel}";
        string body = switched.Count == 1
            ? $"{switched[0].DeviceName} → {switched[0].TargetLabel}"
            : string.Join("\n", switched.Select(x => $"• {x.DeviceName} → {x.TargetLabel}"));
        ShowToast(title, body);
    }

    /// <summary>使用 App.LastDevices 的便捷入口。</summary>
    public static void CheckAndNotifyLowBattery() => CheckAndNotifyLowBattery(App.LastDevices);

    public static void CheckAndNotifyLowBattery(IReadOnlyList<PeripheralDevice> devices)
    {
        foreach (var d in devices)
        {
            if (!d.Connected || d.IsSleeping || d.BatteryPercent is not { } pct)
                continue;

            if (d.State is BatteryState.Charging or BatteryState.FullyCharged)
            {
                lock (LockObj) LowBatteryAlerted.Remove(d.Id);
                continue;
            }

            int threshold = App.Settings.GetEffectiveLowBatteryThreshold(d.Id);
            if (pct >= threshold + 4)
            {
                lock (LockObj) LowBatteryAlerted.Remove(d.Id);
                continue;
            }

            if (App.Settings.ShouldNotifyLowBattery(d.Id, pct))
            {
                bool shouldFire;
                lock (LockObj) shouldFire = LowBatteryAlerted.Add(d.Id);
                if (shouldFire)
                {
                    ShowToast($"低电量提醒 · {d.Name}", $"当前电量仅剩 {pct}%（低于阈值 {threshold}%），请及时充电。");
                }
            }
        }
    }
}
