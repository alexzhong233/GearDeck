using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using GearDeck.Core;
using GearDeck.Core.Models;

namespace GearDeck.App.Services;

/// <summary>
/// Windows 原生通知服务：优先调用 Windows App SDK AppNotificationManager 发出系统 Toast 横幅通知，
/// 若非打包环境受限时自动回落至 Win32 托盘气球通知 (NIF_INFO)。
/// </summary>
public static class NativeNotificationService
{
    private static readonly object LockObj = new();
    private static readonly HashSet<string> LowBatteryAlerted = new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;
    private static bool _toastSupported;

    public static void Initialize()
    {
        lock (LockObj)
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                if (AppNotificationManager.IsSupported())
                {
                    AppNotificationManager.Default.Register();
                    _toastSupported = true;
                }
            }
            catch (Exception ex)
            {
                _toastSupported = false;
                Diag.Log($"AppNotification 注册失败（回落托盘气球）: {ex.Message}");
            }
        }
    }

    public static void Unregister()
    {
        lock (LockObj)
        {
            if (!_toastSupported) return;
            try { AppNotificationManager.Default.Unregister(); }
            catch { }
            _toastSupported = false;
        }
    }

    public static void ShowToast(string title, string message)
    {
        if (!_initialized) Initialize();

        if (_toastSupported)
        {
            try
            {
                var builder = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message);
                AppNotificationManager.Default.Show(builder.BuildNotification());
                return;
            }
            catch (Exception ex)
            {
                Diag.Log($"Windows Toast 发送失败（回落托盘通知）: {ex.Message}");
            }
        }

        App.TrayIcon?.ShowBalloon(title, message);
    }

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
