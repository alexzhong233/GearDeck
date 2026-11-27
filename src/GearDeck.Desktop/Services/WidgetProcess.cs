using System.Diagnostics;
using System.Text.Json;
using GearDeck.Core;
using GearDeck.Core.Models;

namespace GearDeck.Desktop.Services;

/// <summary>主程序写状态文件并管理独立的任务栏贴片进程；仅 Windows 启动，macOS 为 no-op。</summary>
public static class WidgetProcess
{
    private static Process? _process;
    private static string? _lastWrittenJson;
    private static readonly object SyncLock = new();
    private static long _stateSeq;

    private static string ExePath => Path.Combine(AppContext.BaseDirectory, "widget", "GearDeck.Widget.exe");

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.LocalApplicationData
            : Environment.SpecialFolder.ApplicationData),
        "GearDeck", "widget-state.json");

    public static void Refresh()
    {
        lock (SyncLock)
        {
            try
            {
                WriteState();
                if (!OperatingSystem.IsWindows()) return;
                if (!File.Exists(ExePath)) return;
                if (_process == null || _process.HasExited)
                {
                    KillStrayProcesses();
                    // 严禁 --preview：贴片仅接受 --widget-bg 后台模式
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
                Diag.Log($"贴片进程启动失败: {ex.Message}");
            }
        }
    }

    public static void Stop()
    {
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
            if (OperatingSystem.IsWindows()) KillStrayProcesses();
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
                var devices = App.LastDevices ?? [];
                var settings = App.Settings;

                // 桌面版无任务栏分组配置：按已连接设备生成贴片条目。
                var groups = new List<WidgetChipOut>();
                foreach (var dev in devices.Where(d => d.Connected))
                {
                    string label = dev.Kind switch
                    {
                        DeviceKind.Mouse => "鼠标",
                        DeviceKind.Keyboard => "键盘",
                        DeviceKind.Headset => "耳机",
                        _ => "手柄",
                    };
                    string glyph = dev.Kind switch
                    {
                        DeviceKind.Mouse => "\uE962",
                        DeviceKind.Keyboard => "\uE765",
                        DeviceKind.Headset => "\uE95B",
                        _ => "\uE7FC",
                    };

                    var cached = settings?.GetLastKnownBattery(dev.Id);
                    int? livePct = dev.BatteryPercent;
                    int? effPct = livePct ?? cached?.BatteryPercent;
                    bool usedHist = livePct == null && effPct != null;
                    string text = effPct is { } pct ? $"{pct}%" : "--";
                    bool isLow = effPct is { } bp && bp <= 20;

                    string tip = label + "\n" + (
                        dev.FormatDualEarSummary(compact: false) is { } dual
                            ? $"{dev.Name}: {(dev.IsSleeping ? "休眠 · 上次 " : "")}{dual}"
                            : livePct is { } lp
                                ? $"{dev.Name}: {lp}%{(dev.State == BatteryState.Charging ? " ⚡" : "")}"
                                : effPct is { } hp
                                    ? $"{dev.Name}: {(dev.IsSleeping ? "休眠 · 上次电量" : "上次电量")} {hp}%"
                                    : $"{dev.Name}: 未读到");

                    groups.Add(new WidgetChipOut
                    {
                        Glyph = glyph,
                        IconPath = "",
                        Text = text,
                        TextTop = "",
                        TextBottom = "",
                        Tip = tip,
                        Low = isLow,
                        Dim = dev.IsSleeping || usedHist || effPct == null,
                        GroupId = $"device:{dev.Id}",
                    });
                }

                var state = new WidgetStateOut
                {
                    MainExe = Environment.ProcessPath ?? "",
                    Embedded = true,
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
