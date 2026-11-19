using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GearDeck.Core.Services;

/// <summary>
/// 外部官方软件与 WebHID 网页驱动共存守卫：
/// 当用户在 GearDeck 中启动官方软件/网页驱动，或前台焦点正停留在官方驱动窗口/WebHID 标签页时，
/// 自动暂停后台 HID 轮询与游戏自动切换，避免与官方软件争抢 USB HID 接口句柄或覆写配置。
/// </summary>
public static class ExternalConfiguratorGuard
{
    private static readonly string[] OfficialProcessNames =
    [
        "lghub",
        "lghub_agent",
        "lghub_ui",
        "RazerAppEngine",
        "Razer Synapse 3",
        "Razer Central",
        "ArmouryCrate",
        "ArmouryCrate.UserSessionHelper",
        "INZONEHub",
        "FlydigiSpaceStation",
        "SpaceStation",
        "Aula Hub",
        "AulaHub",
        "AM Master",
        "AMasterDriver",
        "ATK HUB",
        "ATK V HUB",
    ];

    private static readonly string[] BrowserProcessNames =
    [
        "chrome",
        "msedge",
        "brave",
        "opera",
        "vivaldi",
        "arc",
        "360chrome",
        "qqbrowser",
        "centbrowser",
    ];

    private static readonly string[] WebDriverTitleHints =
    [
        "Synapse",
        "Razer",
        "ATK",
        "HUB",
        "WLmouse",
        "AULA",
        "狼蛛",
        "Angry Miao",
        "AM Master",
        "AMaster",
        "LAMZU",
        "Gear Link",
        "MCHOSE",
        "Keychron",
        "Pulsar",
        "Bibimbap",
        "Scyrox",
    ];

    private static long _manualPauseUntilTicks;
    private static string? _manualPauseLabel;

    public static bool AutoYieldEnabled { get; set; } = true;

    /// <summary>按设备 ID / VID:PID 缓存已命中的子协议标签，避免后台重复盲探不兼容端点。</summary>
    public static ConcurrentDictionary<string, string> CachedSubProtocolByDevice { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static event Action? StateChanged;

    public static void PauseForExternalDriver(string driverLabel, TimeSpan? duration = null)
    {
        long ms = (long)(duration ?? TimeSpan.FromMinutes(3)).TotalMilliseconds;
        Interlocked.Exchange(ref _manualPauseUntilTicks, Environment.TickCount64 + Math.Max(5000, ms));
        _manualPauseLabel = driverLabel;
        Diag.Log($"[Coexistence] 已暂停后台 HID 轮询与自动切换 {ms / 1000}s，让位给 {driverLabel}");
        StateChanged?.Invoke();
    }

    public static void ResumeNow()
    {
        Interlocked.Exchange(ref _manualPauseUntilTicks, 0);
        _manualPauseLabel = null;
        Diag.Log("[Coexistence] 用户主动恢复 GearDeck HID 控制权");
        StateChanged?.Invoke();
    }

    public static bool IsBackgroundYieldActive(out string? reason)
    {
        long until = Interlocked.Read(ref _manualPauseUntilTicks);
        long now = Environment.TickCount64;
        if (until > now)
        {
            long remSec = Math.Max(1, (until - now) / 1000);
            reason = $"已暂时让位给 {_manualPauseLabel ?? "官方驱动"}（剩余约 {remSec} 秒，点击恢复可立即接管）";
            return true;
        }

        if (AutoYieldEnabled && TryDetectForegroundConfigurator(out string? fgLabel))
        {
            reason = $"检测到官方驱动「{fgLabel}」正在前台操作，已自动暂停后台轮询避免冲突";
            return true;
        }

        reason = null;
        return false;
    }

    private static bool TryDetectForegroundConfigurator(out string? detectedLabel)
    {
        detectedLabel = null;
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

            using var proc = Process.GetProcessById((int)pid);
            string pname = proc.ProcessName;

            foreach (string official in OfficialProcessNames)
            {
                if (pname.Contains(official, StringComparison.OrdinalIgnoreCase))
                {
                    detectedLabel = pname;
                    return true;
                }
            }

            if (BrowserProcessNames.Any(b => pname.Equals(b, StringComparison.OrdinalIgnoreCase)))
            {
                string title = ReadWindowTitle(hwnd);
                foreach (string hint in WebDriverTitleHints)
                {
                    if (title.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    {
                        detectedLabel = title;
                        return true;
                    }
                }
            }
        }
        catch
        {
        }
        return false;
    }

    private static string ReadWindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        int len = GetWindowTextW(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
