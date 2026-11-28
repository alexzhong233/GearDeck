using System.Diagnostics;
using System.Text.Json;
using GearDeck.Core;
using GearDeck.Core.Models;
using GearDeck.Core.Services;

namespace GearDeck.Desktop.Services;

/// <summary>主程序写状态文件并管理独立的任务栏贴片进程；仅 Windows 启动，macOS 为 no-op。</summary>
public static class WidgetProcess
{
    private static Process? _process;
    private static string? _lastWrittenJson;
    private static readonly object SyncLock = new();
    private static long _stateSeq;

    private static string ResolveExePath()
    {
        string adjacent = Path.Combine(AppContext.BaseDirectory, "GearDeck.Widget.exe");
        if (File.Exists(adjacent))
            return adjacent;

        string nested = Path.Combine(AppContext.BaseDirectory, "widget", "GearDeck.Widget.exe");
        return File.Exists(nested) ? nested : adjacent;
    }

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.LocalApplicationData
            : Environment.SpecialFolder.ApplicationData),
        "GearDeck", "widget-state.json");

    public static void Refresh()
    {
        if (!OperatingSystem.IsWindows())
            return;

        lock (SyncLock)
        {
            try
            {
                WriteState();
                string exePath = ResolveExePath();
                if (!File.Exists(exePath))
                {
                    Diag.Log($"未找到任务栏贴片程序: {exePath}");
                    return;
                }

                if (_process == null || _process.HasExited)
                {
                    KillStrayProcesses();
                    // 严禁 --preview：贴片仅接受 --widget-bg 后台模式
                    _process = Process.Start(new ProcessStartInfo(exePath)
                    {
                        Arguments = "--widget-bg",
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(exePath)!,
                    });
                }
            }
            catch (Exception ex)
            {
                Diag.Log($"贴片进程启动失败: {ex.Message}");
            }
        }
    }

    public static void Stop()
    {
        if (!OperatingSystem.IsWindows())
            return;

        lock (SyncLock)
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
        lock (SyncLock)
        {
            try
            {
                var settings = App.Settings.Current;
                var devices = App.LastDevices ?? [];
                App.Settings.EnsureDefaults();

                var groups = new List<WidgetChipOut>();
                int monitorIndex = -1;
                bool showMonitor = settings.MonitorCollectionEnabled && settings.MonitorEnabled;
                foreach (var cfg in settings.TaskbarGroups)
                {
                    if (string.Equals(cfg.GroupId, "builtin:monitor", StringComparison.OrdinalIgnoreCase))
                    {
                        if (showMonitor)
                            monitorIndex = groups.Count;
                        continue;
                    }
                    if (!cfg.Visible) continue;
                    var members = TaskbarGroupHelper.GetMembers(cfg.GroupId, devices, connectedOnly: true);
                    var def = BatteryHistoryHelper.SelectGroupDisplayDevice(members, cfg.DefaultDeviceId);
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

                    string text = "--";
                    string textTop = "";
                    string textBottom = "";
                    int? effPct = null;
                    bool usedHist = false;
                    bool isLow = false;
                    if (def != null)
                    {
                        var cached = App.Settings.GetLastKnownBattery(def.Id);
                        var chipRes = BatteryHistoryHelper.BuildChipText(def, label: "", showPct: true, cached);
                        text = chipRes.Text;
                        textTop = chipRes.TextTop;
                        textBottom = chipRes.TextBottom;
                        effPct = chipRes.EffectivePercent;
                        usedHist = chipRes.UsedHistoricalFallback;

                        int? livePct = def.BatteryPercent
                                       ?? (def.LeftEarPercent.HasValue || def.RightEarPercent.HasValue ? chipRes.EffectivePercent : null);
                        isLow = livePct is { } bp && App.Settings.IsDeviceLowBattery(def.Id, bp);
                    }

                    string tip = label + "\n" + (members.Count == 0
                        ? "（无设备）"
                        : string.Join("\n", members.Select(m =>
                        {
                            var mc = App.Settings.GetLastKnownBattery(m.Id);
                            if (m.FormatDualEarSummary(compact: false) is { } dualTip)
                                return $"{m.Name}: {dualTip}";
                            if (mc != null && (mc.LeftEarPercent.HasValue || mc.RightEarPercent.HasValue))
                            {
                                string lt = mc.LeftEarPercent is { } lv ? $"{lv}%" : "--";
                                string rt = mc.RightEarPercent is { } rv ? $"{rv}%" : "--";
                                return $"{m.Name}: {(m.IsSleeping ? "休眠 · 上次 " : "上次 ")}L {lt} / R {rt}";
                            }
                            if (m.BatteryPercent is { } mp)
                                return $"{m.Name}: {mp}%{(m.State == BatteryState.Charging ? " ⚡" : "")}";
                            if (mc?.BatteryPercent is { } histP)
                                return $"{m.Name}: {(m.IsSleeping ? "休眠 · 上次电量" : "上次电量")} {histP}%";
                            return $"{m.Name}: 未读到";
                        })));

                    groups.Add(new WidgetChipOut
                    {
                        Glyph = glyph,
                        IconPath = iconPath,
                        Text = text,
                        TextTop = textTop,
                        TextBottom = textBottom,
                        Tip = tip,
                        Low = isLow,
                        Dim = def == null || def.IsSleeping || usedHist || effPct == null,
                        GroupId = cfg.GroupId,
                    });
                }

                var state = new WidgetStateOut
                {
                    MainExe = Environment.ProcessPath ?? "",
                    Debug = settings.DebugModeEnabled,
                    Embedded = settings.WidgetEmbedded,
                    FlyoutOnHover = settings.WidgetFlyoutOnHover,
                    Align = settings.WidgetAlignment,
                    Offset = settings.WidgetOffsetX,
                    FloatX = settings.WidgetFloatX,
                    FloatY = settings.WidgetFloatY,
                    Monitor = showMonitor,
                    MonitorIndex = monitorIndex,
                    HwInfoBridge = settings.HwInfoBridgeEnabled,
                    MonitorTop = settings.MonitorTopMetric,
                    MonitorBottom = settings.MonitorBottomMetric,
                    Height = settings.WidgetHeight,
                    FontSize = settings.WidgetFontSize,
                    BlockWidth = settings.WidgetBlockWidth,
                    Groups = groups,
                };

                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                // 先以固定 Seq=0 序列化出比较串；内容未变化且状态文件仍在时跳过写盘
                state.Seq = 0;
                string compareJson = JsonSerializer.Serialize(state, jsonOptions);
                if (string.Equals(compareJson, _lastWrittenJson, StringComparison.Ordinal) && File.Exists(StatePath))
                {
                    _lastWrittenJson = compareJson;
                    return;
                }

                // 内容发生变化：自增序号后重新序列化，并用线程唯一临时文件原子替换目标文件
                state.Seq = Interlocked.Increment(ref _stateSeq);
                string json = JsonSerializer.Serialize(state, jsonOptions);

                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                var tmpPath = $"{StatePath}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, StatePath, overwrite: true);
                _lastWrittenJson = compareJson;
            }
            catch (Exception ex)
            {
                Diag.Log($"写入贴片状态失败: {ex.Message}");
            }
        }
    }

    private sealed class WidgetStateOut
    {
        public string MainExe { get; set; } = "";
        public bool Debug { get; set; }
        public bool Embedded { get; set; } = true;
        public bool FlyoutOnHover { get; set; }
        public string Align { get; set; } = "right";
        public int Offset { get; set; }
        public int FloatX { get; set; } = -1;
        public int FloatY { get; set; } = -1;
        public bool Monitor { get; set; }
        public int MonitorIndex { get; set; } = -1;
        public bool HwInfoBridge { get; set; } = true;
        public string MonitorTop { get; set; } = "CPU";
        public string MonitorBottom { get; set; } = "RAM";
        public int Height { get; set; } = 40;
        public int FontSize { get; set; } = 13;
        public int BlockWidth { get; set; } = 92;
        public long Seq { get; set; }
        public List<WidgetChipOut> Groups { get; set; } = [];
    }

    private sealed class WidgetChipOut
    {
        public string Glyph { get; set; } = "";
        public string IconPath { get; set; } = "";
        public string Text { get; set; } = "";
        public string TextTop { get; set; } = "";
        public string TextBottom { get; set; } = "";
        public string Tip { get; set; } = "";
        public bool Low { get; set; }
        public bool Dim { get; set; }
        public string GroupId { get; set; } = "";
    }
}
