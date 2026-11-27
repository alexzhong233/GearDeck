using GearDeck.Core.Models;
using GearDeck.Core.Services;

namespace GearDeck.Desktop.Services;

/// <summary>
/// macOS 顶部菜单栏状态文本格式化器：
/// - 默认平铺模式（MacStatusBarTiled=true）：按启用分组平铺展示图标符号与实时电量/状态；
/// - 精简模式（MacStatusBarTiled=false）：展示单条摘要；
/// - 绝不伪造未观测过的电量百分比，历史回退仅限 LastKnownBatteries。
/// </summary>
public static class MacStatusBarFormatter
{
    public static string BuildTitle(
        IReadOnlyList<PeripheralDevice> devices,
        AppSettings settings,
        SystemMetricsSnapshot? metrics = null,
        bool permissionDenied = false)
    {
        devices ??= [];
        settings ??= AppSettings.Default;

        var groups = GetEffectiveGroups(settings);
        if (!settings.MacStatusBarTiled)
        {
            string summary = BuildCompactSummary(devices, settings, metrics);
            return permissionDenied && !devices.Any(d => d.BatteryPercent.HasValue)
                ? "GD · 需授权"
                : summary;
        }

        var segments = new List<string>();
        foreach (var cfg in groups)
        {
            if (!cfg.Visible) continue;
            var primary = PickPrimaryMember(cfg, devices, settings);
            if (primary == null) continue;

            string badge = FormatDeviceSegment(primary, settings, permissionDenied);
            if (!string.IsNullOrWhiteSpace(badge))
                segments.Add(badge);
        }

        if (segments.Count == 0 && devices.Count > 0)
        {
            var first = devices[0];
            segments.Add(FormatDeviceSegment(first, settings, permissionDenied));
        }

        if (settings.MacStatusBarShowMetrics && metrics != null)
        {
            string top = DesktopMetricsEngine.FormatTaskbar(
                string.IsNullOrWhiteSpace(settings.MonitorTopMetric) ? "CPU" : settings.MonitorTopMetric,
                metrics);
            if (!string.IsNullOrWhiteSpace(top))
                segments.Add(top);
        }

        return segments.Count > 0 ? string.Join("  ", segments) : "GearDeck";
    }

    public static List<PeripheralDevice> GetGroupMembers(
        TaskbarGroupConfig cfg,
        IReadOnlyList<PeripheralDevice> devices,
        AppSettings settings)
    {
        return cfg.GroupId switch
        {
            "builtin:mouse" => devices.Where(d => d.Kind == DeviceKind.Mouse).ToList(),
            "builtin:keyboard" => devices.Where(d => d.Kind == DeviceKind.Keyboard).ToList(),
            "builtin:headset" => devices.Where(d => d.Kind == DeviceKind.Headset).ToList(),
            "builtin:controller" => devices.Where(d => d.Kind == DeviceKind.Controller).ToList(),
            _ => devices.Where(d => settings.CustomGroups
                .FirstOrDefault(g => string.Equals(g.Id, cfg.GroupId, StringComparison.OrdinalIgnoreCase))
                ?.MemberIds.Contains(d.Id, StringComparer.OrdinalIgnoreCase) == true).ToList(),
        };
    }

    public static string FormatDeviceMenuLine(PeripheralDevice d, AppSettings settings)
    {
        settings.LastKnownBatteries.TryGetValue(d.Id, out var cached);
        var chip = BatteryHistoryHelper.BuildChipText(d, string.Empty, true, cached);
        string status = d.FormatDualEarSummary(compact: false)
            ?? (chip.UsedHistoricalFallback
                ? $"{chip.Text} (上次)"
                : (chip.Text == "--" ? (d.State == BatteryState.Charging ? "充电中 ⚡" : (d.IsSleeping ? "休眠" : "未读到")) : chip.Text));
        string icon = KindSymbol(d.Kind);
        string transport = string.IsNullOrWhiteSpace(d.Transport) ? "" : $" [{d.Transport}]";
        return $"{icon} {d.Name}{transport}  ·  {status}";
    }

    private static string BuildCompactSummary(
        IReadOnlyList<PeripheralDevice> devices,
        AppSettings settings,
        SystemMetricsSnapshot? metrics)
    {
        var pcts = new List<int>();
        foreach (var d in devices)
        {
            if (d.BatteryPercent is >= 0 and <= 100)
                pcts.Add(d.BatteryPercent.Value);
            else if (settings.LastKnownBatteries.TryGetValue(d.Id, out var cached) && cached.BatteryPercent is int hp)
                pcts.Add(hp);
        }

        string main = pcts.Count > 0 ? $"GD {pcts.Min()}%" : (devices.Count > 0 ? $"GD ({devices.Count})" : "GearDeck");
        if (settings.MacStatusBarShowMetrics && metrics != null)
        {
            string top = DesktopMetricsEngine.FormatTaskbar(
                string.IsNullOrWhiteSpace(settings.MonitorTopMetric) ? "CPU" : settings.MonitorTopMetric,
                metrics);
            if (!string.IsNullOrWhiteSpace(top))
                return $"{main} · {top}";
        }
        return main;
    }

    private static PeripheralDevice? PickPrimaryMember(
        TaskbarGroupConfig cfg,
        IReadOnlyList<PeripheralDevice> devices,
        AppSettings settings)
    {
        var members = GetGroupMembers(cfg, devices, settings);
        if (members.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(cfg.DefaultDeviceId))
        {
            var matched = members.FirstOrDefault(m =>
                string.Equals(m.Id, cfg.DefaultDeviceId, StringComparison.OrdinalIgnoreCase));
            if (matched != null) return matched;
        }

        return members
            .OrderByDescending(m => m.BatteryPercent.HasValue)
            .ThenBy(m => m.IsSleeping)
            .ThenBy(m => m.BatteryPercent ?? int.MaxValue)
            .First();
    }

    private static string FormatDeviceSegment(PeripheralDevice d, AppSettings settings, bool permissionDenied)
    {
        string icon = KindSymbol(d.Kind);
        if (d.FormatDualEarSummary(compact: true) is { } dual)
            return $"{icon} {dual}";

        settings.LastKnownBatteries.TryGetValue(d.Id, out var cached);
        var chip = BatteryHistoryHelper.BuildChipText(d, string.Empty, true, cached);
        if (chip.EffectivePercent is { } pct)
        {
            string bolt = d.State == BatteryState.Charging && !d.IsSleeping ? "⚡" : "";
            return $"{icon} {pct}%{bolt}";
        }

        if (d.State == BatteryState.Charging)
            return $"{icon} ⚡";

        if (permissionDenied && !string.IsNullOrWhiteSpace(d.Note) && d.Note.Contains("输入监控"))
            return $"{icon} 需授权";

        return $"{icon} —";
    }

    private static string KindSymbol(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "🖱",
        DeviceKind.Keyboard => "⌨",
        DeviceKind.Headset => "🎧",
        DeviceKind.Controller => "🎮",
        _ => "◈",
    };

    private static List<TaskbarGroupConfig> GetEffectiveGroups(AppSettings settings)
    {
        if (settings.TaskbarGroups is { Count: > 0 })
            return settings.TaskbarGroups;
        return
        [
            new TaskbarGroupConfig { GroupId = "builtin:mouse", Visible = true },
            new TaskbarGroupConfig { GroupId = "builtin:keyboard", Visible = true },
            new TaskbarGroupConfig { GroupId = "builtin:headset", Visible = true },
            new TaskbarGroupConfig { GroupId = "builtin:controller", Visible = false },
        ];
    }
}
