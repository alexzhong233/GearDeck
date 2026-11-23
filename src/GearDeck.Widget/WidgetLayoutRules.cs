namespace GearDeck.Widget;

internal static class WidgetLayoutRules
{
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private static readonly HashSet<string> ExcludedFullscreenClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "ForegroundStaging",
        "NotifyIconOverflowWindow",
    };

    /// <summary>
    /// 统一水平方向约定：无论左锚定还是右锚定，Offset 正数始终向右，负数始终向左。
    /// </summary>
    public static int ComputeHorizontalRelX(string align, int offsetDip, int leftAnchor, int rightBoundary, int width, int trayW, double scale)
    {
        int raw = string.Equals(align, "left", StringComparison.OrdinalIgnoreCase)
            ? leftAnchor + (int)Math.Round(offsetDip * scale)
            : rightBoundary - width - (int)Math.Round(12 * scale) + (int)Math.Round(offsetDip * scale);
        return Math.Clamp(raw, 0, Math.Max(0, trayW - width));
    }

    public static int ComputeHorizontalOffsetDip(string align, int relX, int leftAnchor, int rightBoundary, int width, double scale)
    {
        return string.Equals(align, "left", StringComparison.OrdinalIgnoreCase)
            ? (int)Math.Round((relX - leftAnchor) / scale)
            : (int)Math.Round((relX - (rightBoundary - width - (int)Math.Round(12 * scale))) / scale);
    }

    /// <summary>
    /// 竖直任务栏方向约定：Offset 正数始终向下，负数始终向上。
    /// </summary>
    public static int ComputeVerticalRelY(string align, int offsetDip, int height, int trayH, double scale)
    {
        int raw = string.Equals(align, "left", StringComparison.OrdinalIgnoreCase)
            ? (int)Math.Round((8 + offsetDip) * scale)
            : trayH - height - (int)Math.Round(120 * scale) + (int)Math.Round(offsetDip * scale);
        return Math.Clamp(raw, 0, Math.Max(0, trayH - height));
    }

    public static int ComputeVerticalOffsetDip(string align, int relY, int height, int trayH, double scale)
    {
        return string.Equals(align, "left", StringComparison.OrdinalIgnoreCase)
            ? (int)Math.Round(relY / scale) - 8
            : (int)Math.Round((relY - (trayH - height - (int)Math.Round(120 * scale))) / scale);
    }

    public static bool IsTrayAutoHidden(
        int trayLeft, int trayTop, int trayRight, int trayBottom,
        int monLeft, int monTop, int monRight, int monBottom)
    {
        int tw = Math.Max(0, trayRight - trayLeft);
        int th = Math.Max(0, trayBottom - trayTop);
        if (tw == 0 || th == 0) return false;
        int visW = Math.Max(0, Math.Min(trayRight, monRight) - Math.Max(trayLeft, monLeft));
        int visH = Math.Max(0, Math.Min(trayBottom, monBottom) - Math.Max(trayTop, monTop));
        return tw >= th ? visH < th / 2 : visW < tw / 2;
    }

    /// <summary>
    /// 判断前台窗口是否为真正的全屏应用（排除桌面 Progman/WorkerW、Explorer 壳层、透明桌宠与普通带标题栏窗口）。
    /// </summary>
    public static bool ShouldTreatAsFullscreen(
        string? className,
        bool sameProcessAsTray,
        int style,
        int exStyle,
        bool isVisible,
        bool isCloaked,
        int fgLeft, int fgTop, int fgRight, int fgBottom,
        int monLeft, int monTop, int monRight, int monBottom,
        bool isMaximized = false)
    {
        if (!isVisible || isCloaked || sameProcessAsTray)
            return false;

        if (!string.IsNullOrEmpty(className) && ExcludedFullscreenClasses.Contains(className))
            return false;

        if ((exStyle & (WsExToolWindow | WsExTransparent | WsExNoActivate)) != 0)
            return false;

        if ((style & WsCaption) == WsCaption)
            return false;

        if ((style & WsThickFrame) != 0 || isMaximized)
            return false;

        return fgLeft <= monLeft && fgTop <= monTop && fgRight >= monRight && fgBottom >= monBottom;
    }
}
