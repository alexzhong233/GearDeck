using GearDeck.Core.Models;
using GearDeck.Core.Services;

namespace GearDeck.Desktop.Services;

public sealed record MacStatusBarDeviceItem(
    string Symbol,
    string ValueText,
    DeviceKind Kind,
    string? CustomGlyph);

public sealed record MacStatusBarSlot(
    MacStatusBarDeviceItem? Device,
    string? MonitorTopText,
    string? MonitorBottomText)
{
    public bool IsMonitor => MonitorTopText != null || MonitorBottomText != null;
}

public sealed record MacStatusBarLayout(
    IReadOnlyList<MacStatusBarDeviceItem> Devices,
    string? MonitorTopText,
    string? MonitorBottomText,
    string FallbackText,
    IReadOnlyList<MacStatusBarSlot> Slots)
{
    public MacStatusBarLayout(
        IReadOnlyList<MacStatusBarDeviceItem> devices,
        string? monitorTopText,
        string? monitorBottomText,
        string fallbackText)
        : this(devices, monitorTopText, monitorBottomText, fallbackText, BuildDefaultSlots(devices, monitorTopText, monitorBottomText))
    {
    }

    private static IReadOnlyList<MacStatusBarSlot> BuildDefaultSlots(
        IReadOnlyList<MacStatusBarDeviceItem> devices,
        string? monitorTopText,
        string? monitorBottomText)
    {
        var list = new List<MacStatusBarSlot>(devices.Count + 1);
        foreach (var d in devices)
            list.Add(new MacStatusBarSlot(d, null, null));
        if (monitorTopText != null || monitorBottomText != null)
            list.Add(new MacStatusBarSlot(null, monitorTopText, monitorBottomText));
        return list;
    }
}

/// <summary>
/// macOS 顶部菜单栏状态文本与双行布局格式化器：
/// - 默认平铺模式（MacStatusBarTiled=true）：按启用分组顺序（含系统监控分组）平铺展示；
/// - 精简模式（MacStatusBarTiled=false）：展示单条摘要；
/// - 绝不伪造未观测过的电量百分比，历史回退仅限 LastKnownBatteries。
/// </summary>
public static class MacStatusBarFormatter
{
    public static bool HasStackedMonitor(MacStatusBarLayout layout)
        => layout.Slots.Any(s =>
            s.IsMonitor &&
            !string.IsNullOrWhiteSpace(s.MonitorTopText) &&
            !string.IsNullOrWhiteSpace(s.MonitorBottomText));

    public static MacStatusBarLayout BuildLayout(
        IReadOnlyList<PeripheralDevice> devices,
        AppSettings settings,
        SystemMetricsSnapshot? metrics = null,
        bool permissionDenied = false)
    {
        devices ??= [];
        settings ??= AppSettings.Default;

        var (topMetric, bottomMetric) = FormatMetricLines(settings, metrics);
        bool hasMonitor = topMetric != null || bottomMetric != null;
        var groups = GetEffectiveGroups(settings);

        if (!settings.MacStatusBarTiled)
        {
            string summary = BuildCompactMainText(devices, settings);
            if (permissionDenied && !devices.Any(d => d.BatteryPercent.HasValue))
                return new MacStatusBarLayout([], topMetric, bottomMetric, "GD · 需授权",
                    hasMonitor ? [new MacStatusBarSlot(null, topMetric, bottomMetric)] : []);

            var compactDev = new MacStatusBarDeviceItem("", summary, DeviceKind.Mouse, null);
            var compactSlots = new List<MacStatusBarSlot> { new(compactDev, null, null) };
            if (hasMonitor)
                compactSlots.Add(new MacStatusBarSlot(null, topMetric, bottomMetric));

            return new MacStatusBarLayout(
                [compactDev],
                topMetric,
                bottomMetric,
                summary,
                compactSlots);
        }

        var items = new List<MacStatusBarDeviceItem>();
        var slots = new List<MacStatusBarSlot>();
        bool monitorInserted = false;
        foreach (var cfg in groups)
        {
            if (string.Equals(cfg.GroupId, "builtin:monitor", StringComparison.OrdinalIgnoreCase))
            {
                if (hasMonitor && !monitorInserted)
                {
                    slots.Add(new MacStatusBarSlot(null, topMetric, bottomMetric));
                    monitorInserted = true;
                }
                continue;
            }
            if (!cfg.Visible) continue;
            var primary = PickPrimaryMember(cfg, devices, settings);
            if (primary == null) continue;

            var devItem = BuildDeviceItem(primary, cfg, settings, permissionDenied);
            items.Add(devItem);
            slots.Add(new MacStatusBarSlot(devItem, null, null));
        }

        if (items.Count == 0
            && (settings.TaskbarGroups == null || settings.TaskbarGroups.Count == 0)
            && devices.Count > 0)
        {
            var devItem = BuildDeviceItem(devices[0], null, settings, permissionDenied);
            items.Add(devItem);
            slots.Add(new MacStatusBarSlot(devItem, null, null));
        }

        if (hasMonitor && !monitorInserted)
            slots.Add(new MacStatusBarSlot(null, topMetric, bottomMetric));

        return new MacStatusBarLayout(items, topMetric, bottomMetric, "GearDeck", slots);
    }

    public static string BuildTitle(
        IReadOnlyList<PeripheralDevice> devices,
        AppSettings settings,
        SystemMetricsSnapshot? metrics = null,
        bool permissionDenied = false)
    {
        var layout = BuildLayout(devices, settings, metrics, permissionDenied);
        if (!settings.MacStatusBarTiled)
        {
            string main = layout.Devices.Count > 0 ? layout.Devices[0].ValueText : layout.FallbackText;
            string? mText = JoinMetricLines(layout.MonitorTopText, layout.MonitorBottomText);
            return !string.IsNullOrWhiteSpace(mText) ? $"{main} · {mText}" : main;
        }

        var segments = new List<string>();
        foreach (var slot in layout.Slots)
        {
            if (slot.IsMonitor)
            {
                if (JoinMetricLines(slot.MonitorTopText, slot.MonitorBottomText) is { } metricsText)
                    segments.Add(metricsText);
            }
            else if (slot.Device is { } d)
            {
                string seg = string.IsNullOrEmpty(d.Symbol) ? d.ValueText : $"{d.Symbol} {d.ValueText}";
                if (!string.IsNullOrWhiteSpace(seg))
                    segments.Add(seg);
            }
        }

        return segments.Count > 0 ? string.Join("  ", segments) : layout.FallbackText;
    }

    public static string? FormatMetricsSegment(AppSettings settings, SystemMetricsSnapshot? metrics)
    {
        var (top, bottom) = FormatMetricLines(settings, metrics);
        return JoinMetricLines(top, bottom);
    }

    private static (string? Top, string? Bottom) FormatMetricLines(AppSettings settings, SystemMetricsSnapshot? metrics)
    {
        if (metrics == null || !settings.MonitorCollectionEnabled || !settings.MonitorEnabled)
            return (null, null);

        string topId = string.IsNullOrWhiteSpace(settings.MonitorTopMetric) ? "CPU" : settings.MonitorTopMetric;
        string top = DesktopMetricsEngine.FormatTaskbar(topId, metrics);

        string bottomId = string.IsNullOrWhiteSpace(settings.MonitorBottomMetric) ? "NONE" : settings.MonitorBottomMetric;
        string bottom = string.Equals(bottomId, "NONE", StringComparison.OrdinalIgnoreCase)
            ? ""
            : DesktopMetricsEngine.FormatTaskbar(bottomId, metrics);

        string? topOrNull = string.IsNullOrWhiteSpace(top) ? null : top;
        string? bottomOrNull = string.IsNullOrWhiteSpace(bottom) ? null : bottom;
        return (topOrNull, bottomOrNull);
    }

    private static string? JoinMetricLines(string? top, string? bottom)
    {
        if (!string.IsNullOrWhiteSpace(top) && !string.IsNullOrWhiteSpace(bottom))
            return $"{top} · {bottom}";
        if (!string.IsNullOrWhiteSpace(top))
            return top;
        return !string.IsNullOrWhiteSpace(bottom) ? bottom : null;
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

    private static string BuildCompactMainText(
        IReadOnlyList<PeripheralDevice> devices,
        AppSettings settings)
    {
        var pcts = new List<int>();
        foreach (var d in devices)
        {
            if (d.BatteryPercent is >= 0 and <= 100)
                pcts.Add(d.BatteryPercent.Value);
            else if (settings.LastKnownBatteries.TryGetValue(d.Id, out var cached) && cached.BatteryPercent is int hp)
                pcts.Add(hp);
        }

        return pcts.Count > 0 ? $"GD {pcts.Min()}%" : (devices.Count > 0 ? $"GD ({devices.Count})" : "GearDeck");
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

    private static MacStatusBarDeviceItem BuildDeviceItem(
        PeripheralDevice d,
        TaskbarGroupConfig? cfg,
        AppSettings settings,
        bool permissionDenied)
    {
        string symbol = ResolveGroupSymbol(cfg, d.Kind);
        string? customGlyph = string.IsNullOrWhiteSpace(cfg?.CustomGlyph) ? null : cfg.CustomGlyph;
        string valueText;

        if (d.FormatDualEarSummary(compact: true) is { } dual)
        {
            valueText = dual;
        }
        else
        {
            settings.LastKnownBatteries.TryGetValue(d.Id, out var cached);
            var chip = BatteryHistoryHelper.BuildChipText(d, string.Empty, true, cached);
            if (chip.EffectivePercent is { } pct)
            {
                string bolt = d.State == BatteryState.Charging && !d.IsSleeping ? "⚡" : "";
                valueText = $"{pct}%{bolt}";
            }
            else if (d.State == BatteryState.Charging)
            {
                valueText = "⚡";
            }
            else if (permissionDenied && !string.IsNullOrWhiteSpace(d.Note) && d.Note.Contains("输入监控"))
            {
                valueText = "需授权";
            }
            else
            {
                valueText = "—";
            }
        }

        return new MacStatusBarDeviceItem(symbol, valueText, d.Kind, customGlyph);
    }

    private static string ResolveGroupSymbol(TaskbarGroupConfig? cfg, DeviceKind kind)
    {
        if (!string.IsNullOrWhiteSpace(cfg?.CustomGlyph))
        {
            string? mapped = cfg.CustomGlyph switch
            {
                "\uE962" => "🖱",
                "\uE765" => "⌨",
                "\uE95B" => "🎧",
                "\uEC4F" => "🎙",
                "\uE7FC" => "🎮",
                "\uE7C8" => "⌖",
                "\uE945" => "⚡",
                "\uE701" => "📶",
                "\uE702" => "ᛒ",
                "\uE9F5" => "⚙",
                "\uE83F" => "🔋",
                "\uE734" => "★",
                "\uE720" => "🎤",
                "\uE8D6" => "🔊",
                "\uE975" => "🖥",
                "\uEC4A" => "◔",
                _ => null,
            };
            if (mapped != null)
                return mapped;
        }
        return KindSymbol(kind);
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
