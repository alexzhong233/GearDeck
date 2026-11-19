using System.Diagnostics;

namespace GearDeck.App.Services;

/// <summary>系统监控独立进程的启动/停止。</summary>
public static class MonitorLauncher
{
    private const string ProcessName = "GearDeck.Monitor";

    public static void Launch()
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "monitor", "GearDeck.Monitor.exe");
            if (!File.Exists(exe))
                return;
            // 单实例：已在跑就不重复拉起
            if (Process.GetProcessesByName(ProcessName).Length > 0)
                return;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 监控进程拉起失败不影响主功能
        }
    }

    public static void Kill()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(ProcessName))
                p.Kill(entireProcessTree: true);
        }
        catch
        {
            // 忽略
        }
    }
}

