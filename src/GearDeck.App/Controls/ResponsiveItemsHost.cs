using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GearDeck.App.Controls;

/// <summary>
/// 自适应卡片 / 列表布局容器：
/// - 卡片模式 (IsCardMode = true)：按容器实际宽度与 MinCardWidth 动态计算列数（1..N 列等宽铺满窗口）；
/// - 列表模式 (IsCardMode = false)：单列全宽紧凑行排列；
/// 窗口缩放时自动流畅重排，无需固定 MaxWidth。
/// </summary>
public sealed class ResponsiveItemsHost : Grid
{
    private readonly List<UIElement> _items = [];
    private bool _isCardMode = true;
    private double _minCardWidth = 290;
    private double _gap = 12;
    private int _lastCols = -1;

    public ResponsiveItemsHost()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        ColumnSpacing = _gap;
        RowSpacing = _gap;
        SizeChanged += (_, _) => Relayout(force: false);
        Loaded += (_, _) => Relayout(force: true);
    }

    public bool IsCardMode
    {
        get => _isCardMode;
        set
        {
            if (_isCardMode == value) return;
            _isCardMode = value;
            Relayout(force: true);
        }
    }

    public double MinCardWidth
    {
        get => _minCardWidth;
        set
        {
            _minCardWidth = Math.Max(160, value);
            Relayout(force: true);
        }
    }

    public double Gap
    {
        get => _gap;
        set
        {
            _gap = Math.Max(0, value);
            ColumnSpacing = _gap;
            RowSpacing = _gap;
            Relayout(force: true);
        }
    }

    public void SetItems(IEnumerable<UIElement> elements)
    {
        _items.Clear();
        Children.Clear();
        foreach (var el in elements)
        {
            if (el is FrameworkElement fe)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Stretch;
                fe.VerticalAlignment = VerticalAlignment.Stretch;
            }
            _items.Add(el);
            Children.Add(el);
        }
        Relayout(force: true);
    }

    private int ComputeColumnCount()
    {
        if (!_isCardMode) return 1;
        double width = ActualWidth;
        if (width <= 0 || double.IsNaN(width)) return 1;
        return Math.Clamp((int)Math.Floor((width + _gap) / (_minCardWidth + _gap)), 1, 6);
    }

    private void Relayout(bool force)
    {
        int cols = ComputeColumnCount();
        if (!force && cols == _lastCols) return;
        _lastCols = cols;

        ColumnDefinitions.Clear();
        RowDefinitions.Clear();

        for (int c = 0; c < cols; c++)
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int rows = Math.Max(1, (_items.Count + cols - 1) / cols);
        for (int r = 0; r < rows; r++)
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i] is FrameworkElement feItem)
            {
                SetRow(feItem, i / cols);
                SetColumn(feItem, i % cols);
            }
        }
    }
}
