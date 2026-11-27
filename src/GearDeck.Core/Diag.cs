using GearDeck.Core.Hid;

namespace GearDeck.Core;

/// <summary>全局诊断日志与崩溃追踪：记录扫描过程、HID 设备清单及启动/运行时异常。</summary>
public static class Diag
{
    private static readonly object Gate = new();
    private static readonly List<string> Lines = [];

    private static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GearDeck", "logs");

    public static void Log(string message)
    {
        lock (Gate)
        {
            Lines.Add($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            if (Lines.Count > 600)
                Lines.RemoveRange(0, Lines.Count - 600);
        }
    }

    public static void LogCrash(string context, Exception? ex)
    {
        string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{context}] {ex}\n";
        Log($"{context}: {ex?.Message ?? "unknown error"}");
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(Path.Combine(LogDir, "crash.log"), text);
        }
        catch { }
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate)
            return Lines.ToList();
    }

    /// <summary>枚举本机全部 HID 设备（VID/PID/名称/报告长度），单接口读取异常自动隔离。</summary>
    public static List<string> DumpHidInventory()
    {
        var lines = new List<string> { "== HID 设备清单 ==" };
        try
        {
            foreach (var d in HidSafe.EnumerateDevices()
                         .OrderBy(d => d.VendorID).ThenBy(d => d.ProductID))
            {
                string name = HidSafe.ProductName(d);
                string serial = HidSafe.SerialNumber(d);
                int inLen = HidSafe.MaxInputLen(d);
                int outLen = HidSafe.MaxOutputLen(d);
                int featLen = HidSafe.MaxFeatureLen(d);
                lines.Add($"{d.VendorID:X4}:{d.ProductID:X4} mi={InterfaceNumber(d.DevicePath)} in={inLen} out={outLen} feat={featLen} serial={serial} name={name}");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"枚举失败: {ex.Message}");
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                lines.Add($"macOS InputMonitoring={MacIOKitHid.CheckInputMonitoringAccess()}");
                foreach (var m in MacIOKitHid.Enumerate().OrderBy(x => x.VendorId).ThenBy(x => x.ProductId))
                {
                    lines.Add($"[IOKit] {m.VendorId:X4}:{m.ProductId:X4} up=0x{m.PrimaryUsagePage:X4}:0x{m.PrimaryUsage:X4} in={m.MaxInputReportSize} out={m.MaxOutputReportSize} feat={m.MaxFeatureReportSize} loc=0x{m.LocationId:X} transport={m.Transport} name={m.Product}");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"macOS IOKit 枚举失败: {ex.Message}");
            }
        }
        return lines;
    }

    private static string InterfaceNumber(string path)
    {
        var idx = path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? path.Substring(idx + 1, Math.Min(8, path.Length - idx - 1)) : "?";
    }
}
