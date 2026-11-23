namespace GearDeck.Core.Models;

/// <summary>
/// 单个 Win32 顶层窗口的可见性、样式及显示器覆盖快照，用于精准识别全屏游戏/应用。
/// </summary>
public sealed record WindowFullscreenProbe(
    string ProcessName,
    string ClassName,
    bool IsVisible,
    bool IsCloaked,
    bool IsMinimized,
    bool IsMaximized,
    int Style,
    int ExStyle,
    int WindowLeft,
    int WindowTop,
    int WindowRight,
    int WindowBottom,
    int MonitorLeft,
    int MonitorTop,
    int MonitorRight,
    int MonitorBottom,
    bool IsQunsD3dFullscreen);

/// <summary>
/// 纯逻辑窗口全屏分类器与锁存态判定器：排除桌面、Shell、壁纸、无边框最大化普通软件等误触发，并在退出全屏后及时解除锁存。
/// </summary>
public static class FullscreenWindowClassifier
{
    public const int WsCaption = 0x00C00000;
    public const int WsThickFrame = 0x00040000;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExTransparent = 0x00000020;
    public const int WsExNoActivate = 0x08000000;

    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows 系统桌面、壳层、搜索、锁屏、系统设置、截图、游戏栏
        "explorer.exe", "SearchHost.exe", "SearchApp.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "ShellHost.exe", "LockApp.exe", "LogonUI.exe",
        "TextInputHost.exe", "ApplicationFrameHost.exe", "Widgets.exe", "WidgetService.exe",
        "Taskmgr.exe", "dwm.exe", "csrss.exe", "ScreenClippingHost.exe", "SnippingTool.exe",
        "GameBar.exe", "GameBarFTServer.exe", "XboxGameBarWidgets.exe",
        "SystemSettings.exe", "PickerHost.exe", "CredentialUIBroker.exe", "Consent.exe",

        // 桌面整理 / 动态壁纸 / 截图工具 / 显卡及性能浮层
        "Coodesker.exe", "wallpaper32.exe", "wallpaper64.exe", "ui32.exe",
        "LiveWallpaper.exe", "Lively.exe",
        "Snipaste.exe", "PixPin.exe", "ShareX.exe", "flameshot.exe",
        "NVIDIA Overlay.exe", "nvcontainer.exe", "NVDisplay.Container.exe",
        "AMDRSServ.exe", "RadeonSoftware.exe", "RTSS.exe",
        "PowerToys.exe", "Flow.Launcher.exe", "uTools.exe", "Listary.exe", "Everything.exe",

        // 本软件与各外设官方驱动面板
        "GearDeck.App.exe", "GearDeck.Widget.exe",
        "RazerAppEngine.exe", "Razer Synapse 3.exe", "ArmouryCrate.exe", "lghub.exe", "lghub_agent.exe",
    };

    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland", "ForegroundStaging", "NotifyIconOverflowWindow",
        "MultitaskingViewFrame", "TaskListThumbnailWnd", "Shell_InputSwitchTopLevelWindow",
        "ApplicationFrameWindow",
    };

    public static bool IsExcludedProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        return ExcludedProcesses.Contains(Path.GetFileName(processName.Trim()));
    }

    public static bool IsExcludedClass(string? className)
        => !string.IsNullOrWhiteSpace(className) && ExcludedClasses.Contains(className.Trim());

    public static bool IsFullscreen(WindowFullscreenProbe p, bool onlyTrueExclusiveFullscreen)
    {
        if (!p.IsVisible || p.IsCloaked || p.IsMinimized)
            return false;
        if (IsExcludedProcess(p.ProcessName) || IsExcludedClass(p.ClassName))
            return false;
        if ((p.ExStyle & (WsExToolWindow | WsExTransparent | WsExNoActivate)) != 0)
            return false;

        int monW = p.MonitorRight - p.MonitorLeft;
        int monH = p.MonitorBottom - p.MonitorTop;
        if (monW <= 0 || monH <= 0)
            return false;

        bool coversMonitor = p.WindowLeft <= p.MonitorLeft + 2
            && p.WindowTop <= p.MonitorTop + 2
            && p.WindowRight >= p.MonitorRight - 2
            && p.WindowBottom >= p.MonitorBottom - 2;
        if (!coversMonitor)
            return false;

        if (p.IsQunsD3dFullscreen)
            return true;
        if (onlyTrueExclusiveFullscreen)
            return false;

        if ((p.Style & WsCaption) == WsCaption)
            return false;
        if ((p.Style & WsThickFrame) != 0)
            return false;
        if (p.IsMaximized)
            return false;

        return true;
    }

    public static bool IsExclusiveFullscreenLatchAlive(
        bool windowExists,
        bool processAlive,
        WindowFullscreenProbe? currentProbe,
        bool onlyTrueExclusiveFullscreen)
    {
        if (!windowExists || !processAlive || currentProbe == null)
            return false;
        if (!currentProbe.IsVisible || currentProbe.IsCloaked)
            return false;
        if (currentProbe.IsMinimized)
            return true;
        return IsFullscreen(currentProbe, onlyTrueExclusiveFullscreen);
    }
}
