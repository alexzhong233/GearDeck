using System.Diagnostics;
using System.Text.Json;
using GearDeck.Core.Models;

namespace GearDeck.App.Services;

/// <summary>主程序写状态文件并管理独立的任务栏贴片进程。</summary>
public static class WidgetProcess
{
    private static Process? _process;

    private static string ExePath => Path.Combine(AppContext.BaseDirectory, "widget", "GearDeck.Widget.exe");
    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GearDeck", "widget-state.json");

    public static void Refresh()
    {
        try
        {
            WriteState();
            if (!File.Exists(ExePath)) return;
            if (_process == null || _process.HasExited)
            {
                KillStrayProcesses();
                _process = Process.Start(new ProcessStartInfo(ExePath)
                {
                    Arguments = "--widget-bg",
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(ExePath)!,
                });
            }
        }
        catch (Exception ex)
        {
            Core.Diag.Log($"贴片进程启动失败: {ex.Message}");
        }
    }

    public static void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(1000);
            }
        }
        catch { }
        finally { _process = null; }
        KillStrayProcesses();
    }

    private static void KillStrayProcesses()
    {
        try
        {
            foreach (string procName in new[] { "GearDeck.Widget", "PeripheralHub.Widget" })
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    using (p)
                    {
                        if (_process != null && p.Id == _process.Id) continue;
                        try
                        {
                            if (!p.HasExited)
                            {
                                p.Kill(entireProcessTree: true);
                                p.WaitForExit(500);
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    private static void WriteState()
    {
        var settings = App.Settings.Current;
        var devices = App.LastDevices;
        App.Settings.EnsureDefaults();

        var groups = new List<WidgetChipOut>();
        foreach (var cfg in settings.TaskbarGroups.Where(g => g.Visible))
        {
            var members = MembersOf(cfg.GroupId, devices);
            var def = members.FirstOrDefault(d => d.Id == cfg.DefaultDeviceId)
                      ?? members.FirstOrDefault(d => d.BatteryPercent != null)
                      ?? members.FirstOrDefault();
            string label = cfg.GroupId switch
            {
                "builtin:mouse" => "鼠标",
                "builtin:keyboard" => "键盘",
                "builtin:headset" => "耳机",
                "builtin:controller" => "手柄",
                _ => settings.CustomGroups.FirstOrDefault(c => c.Id == cfg.GroupId)?.DisplayName ?? "组",
            };
            string glyph = GroupIconCatalog.ResolveEffectiveGlyph(cfg, members);
            string iconPath = GroupIconCatalog.ResolveValidIconPath(cfg) ?? "";
            string text;
            if (def?.FormatDualEarSummary(compact: true) is { } dualText)
            {
                text = dualText;
            }
            else
            {
                text = def?.BatteryPercent is { } p ? $"{p}%" : "--";
                if (def?.State == BatteryState.Charging) text += " \u26A1";
            }
            string tip = label + "\n" + (members.Count == 0
                ? "（无设备）"
                : string.Join("\n", members.Select(m =>
                {
                    if (m.FormatDualEarSummary(compact: false) is { } dualTip)
                        return $"{m.Name}: {dualTip}";
                    return $"{m.Name}: {(m.BatteryPercent is { } mp ? mp + "%" : "未读到")}";
                })));
            groups.Add(new WidgetChipOut
            {
                Glyph = glyph,
                IconPath = iconPath,
                Text = text,
                Tip = tip,
                Low = def?.BatteryPercent is { } bp && App.Settings.IsDeviceLowBattery(def.Id, bp),
                Dim = def == null || def.BatteryPercent == null,
                GroupId = cfg.GroupId,
            });
        }

        var state = new WidgetStateOut
        {
            MainExe = Environment.ProcessPath ?? "",
            Embedded = settings.WidgetEmbedded,
            Align = settings.WidgetAlignment,
            Offset = settings.WidgetOffsetX,
            FloatX = settings.WidgetFloatX,
            FloatY = settings.WidgetFloatY,
            Monitor = settings.MonitorEnabled,
            MonitorTop = settings.MonitorTopMetric,
            MonitorBottom = settings.MonitorBottomMetric,
            Height = settings.WidgetHeight,
            FontSize = settings.WidgetFontSize,
            BlockWidth = settings.WidgetBlockWidth,
            Groups = groups,
        };
        var dir = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(dir);
        var tmpPath = StatePath + ".tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmpPath, StatePath, overwrite: true);
    }

    private static List<PeripheralDevice> MembersOf(string groupId, IReadOnlyList<PeripheralDevice> devices)
    {
        IEnumerable<PeripheralDevice> q = groupId switch
        {
            "builtin:mouse" => devices.Where(d => d.Kind == DeviceKind.Mouse),
            "builtin:keyboard" => devices.Where(d => d.Kind == DeviceKind.Keyboard),
            "builtin:headset" => devices.Where(d => d.Kind == DeviceKind.Headset),
            "builtin:controller" => devices.Where(d => d.Kind == DeviceKind.Controller),
            _ => devices.Where(d => App.Settings.Current.CustomGroups
                .FirstOrDefault(g => g.Id == groupId)?.MemberIds.Contains(d.Id) == true),
        };
        return q.Where(d => d.Connected).ToList();
    }

    private sealed class WidgetStateOut
    {
        public string MainExe { get; set; } = "";
        public bool Embedded { get; set; } = true;
        public string Align { get; set; } = "right";
        public int Offset { get; set; }
        public int FloatX { get; set; } = -1;
        public int FloatY { get; set; } = -1;
        public bool Monitor { get; set; }
        public string MonitorTop { get; set; } = "CPU";
        public string MonitorBottom { get; set; } = "RAM";
        public int Height { get; set; } = 40;
        public int FontSize { get; set; } = 13;
        public int BlockWidth { get; set; } = 92;
        public List<WidgetChipOut> Groups { get; set; } = [];
    }

    private sealed class WidgetChipOut
    {
        public string Glyph { get; set; } = "";
        public string IconPath { get; set; } = "";
        public string Text { get; set; } = "";
        public string Tip { get; set; } = "";
        public bool Low { get; set; }
        public bool Dim { get; set; }
        public string GroupId { get; set; } = "";
    }
}
