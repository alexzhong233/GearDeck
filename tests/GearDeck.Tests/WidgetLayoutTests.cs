using GearDeck.Widget;
using Xunit;

public class WidgetLayoutTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void DragInverseRoundTrip_LeftAndRight_WithinOnePixel(double scale)
    {
        int trayW = (int)Math.Round(2560 * scale);
        int width = (int)Math.Round(300 * scale);
        int leftAnchor = (int)Math.Round(168 * scale);
        int rightBoundary = trayW - (int)Math.Round(200 * scale);

        foreach (int draggedRelX in new[] { 120, 420, 760, trayW / 2 - width - 20, trayW / 2 + 40, rightBoundary - width - 40 })
        {
            int clampedX = Math.Clamp(draggedRelX, 0, trayW - width);
            string align = (clampedX + width / 2) < (trayW / 2) ? "left" : "right";
            int offsetDip = WidgetLayoutRules.ComputeHorizontalOffsetDip(align, clampedX, leftAnchor, rightBoundary, width, scale);
            int recomputedX = WidgetLayoutRules.ComputeHorizontalRelX(align, offsetDip, leftAnchor, rightBoundary, width, trayW, scale);
            Assert.InRange(Math.Abs(recomputedX - clampedX), 0, 1);
        }
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    public void OffsetDirection_PositiveAlwaysMovesRight_NegativeAlwaysMovesLeft(string align)
    {
        const double scale = 1.5;
        int trayW = 3840;
        int width = 360;
        int leftAnchor = 252;
        int rightBoundary = 3400;

        int xNeg = WidgetLayoutRules.ComputeHorizontalRelX(align, -200, leftAnchor, rightBoundary, width, trayW, scale);
        int xZero = WidgetLayoutRules.ComputeHorizontalRelX(align, 0, leftAnchor, rightBoundary, width, trayW, scale);
        int xPos = WidgetLayoutRules.ComputeHorizontalRelX(align, +100, leftAnchor, rightBoundary, width, trayW, scale);

        Assert.True(xNeg < xZero, $"Expected negative offset ({xNeg}) < zero ({xZero}) for align={align}");
        if (align == "left")
            Assert.True(xZero < xPos, $"Expected zero ({xZero}) < positive ({xPos}) for align={align}");
    }

    [Theory]
    [InlineData("Progman", false, 0, 0, true, false, false)]
    [InlineData("WorkerW", false, 0, 0, true, false, false)]
    [InlineData("Shell_TrayWnd", false, 0, 0, true, false, false)]
    [InlineData("CustomWnd", true, 0, 0, true, false, false)]          // explorer.exe same PID
    [InlineData("CustomWnd", false, 0, 0, true, true, false)]          // DWM cloaked
    [InlineData("CustomWnd", false, 0, 0x00000080, true, false, false)] // WS_EX_TOOLWINDOW
    [InlineData("CustomWnd", false, 0, 0x00000020, true, false, false)] // WS_EX_TRANSPARENT (desktop pet / overlay)
    [InlineData("CustomWnd", false, 0, 0x08000000, true, false, false)] // WS_EX_NOACTIVATE
    [InlineData("CustomWnd", false, 0x00C00000, 0, true, false, false)] // WS_CAPTION (regular maximized window)
    [InlineData("UnityWndClass", false, unchecked((int)0x90000000), 0, true, false, true)] // genuine borderless fullscreen game
    public void ShouldTreatAsFullscreen_ExcludesDesktopAndOverlays(
        string className, bool sameProc, int style, int exStyle, bool visible, bool cloaked, bool expected)
    {
        bool actual = WidgetLayoutRules.ShouldTreatAsFullscreen(
            className, sameProc, style, exStyle, visible, cloaked,
            0, 0, 2560, 1440,
            0, 0, 2560, 1440);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsTrayAutoHidden_DetectsVisibleVsSlidOffScreen()
    {
        Assert.False(WidgetLayoutRules.IsTrayAutoHidden(0, 1368, 2560, 1440, 0, 0, 2560, 1440));
        Assert.True(WidgetLayoutRules.IsTrayAutoHidden(0, 1438, 2560, 1510, 0, 0, 2560, 1440));
    }

    [Theory]
    [InlineData(0x00040000, false)] // WS_THICKFRAME：普通可调整大小窗口
    [InlineData(0, true)]           // isMaximized：最大化窗口
    public void ShouldTreatAsFullscreen_ExcludesThickFramedAndMaximizedWindows(int style, bool isMaximized)
    {
        // 前台矩形完全覆盖显示器，仅靠 style / isMaximized 将其排除
        bool actual = WidgetLayoutRules.ShouldTreatAsFullscreen(
            "UnityWndClass", false, style, 0, true, false,
            0, 0, 2560, 1440,
            0, 0, 2560, 1440,
            isMaximized);
        Assert.False(actual);
    }
}
