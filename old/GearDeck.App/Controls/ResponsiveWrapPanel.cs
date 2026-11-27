using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace GearDeck.App.Controls;

/// <summary>自动换行流式面板：窗口变窄或处于 Grid Auto 列时自动感知约束宽度并重测高度，防止换行后与下方内容重叠。</summary>
public sealed class ResponsiveWrapPanel : Panel
{
    public double Spacing { get; set; } = 8;
    public double LineSpacing { get; set; } = 8;

    private double _lastConstrainedWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxWidth;
        if (!double.IsInfinity(availableSize.Width) && availableSize.Width > 0)
        {
            maxWidth = availableSize.Width;
        }
        else if (_lastConstrainedWidth > 0)
        {
            maxWidth = _lastConstrainedWidth;
        }
        else
        {
            maxWidth = double.MaxValue;
        }

        double lineWidth = 0;
        double lineHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(maxWidth, double.PositiveInfinity));
            Size desired = child.DesiredSize;

            double nextWidth = lineWidth == 0 ? desired.Width : lineWidth + Spacing + desired.Width;
            if (maxWidth < double.MaxValue && lineWidth > 0 && nextWidth > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += (totalHeight > 0 ? LineSpacing : 0) + lineHeight;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
            }
            else
            {
                lineWidth = nextWidth;
                lineHeight = Math.Max(lineHeight, desired.Height);
            }
        }

        if (lineWidth > 0 || lineHeight > 0)
        {
            totalWidth = Math.Max(totalWidth, lineWidth);
            totalHeight += (totalHeight > 0 ? LineSpacing : 0) + lineHeight;
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? totalWidth : Math.Min(availableSize.Width, Math.Max(totalWidth, 0)),
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double y = 0;
        double lineHeight = 0;
        double maxWidth = finalSize.Width > 0 ? finalSize.Width : double.MaxValue;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            Size desired = child.DesiredSize;
            if (x > 0 && x + desired.Width > maxWidth + 0.5)
            {
                y += lineHeight + LineSpacing;
                x = 0;
                lineHeight = 0;
            }

            child.Arrange(new Rect(x, y, Math.Min(desired.Width, maxWidth), desired.Height));
            x += desired.Width + Spacing;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        double usedHeight = y + lineHeight;
        if (maxWidth < double.MaxValue &&
            usedHeight > finalSize.Height + 1.5 &&
            Math.Abs(_lastConstrainedWidth - maxWidth) > 1.5)
        {
            _lastConstrainedWidth = maxWidth;
            _ = DispatcherQueue?.TryEnqueue(InvalidateMeasure);
        }

        return finalSize;
    }
}
