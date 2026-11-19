using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using GearDeck.App.Controls;
using GearDeck.Metrics;

namespace GearDeck.App.Pages;

public sealed partial class MonitorPage : Page
{
    private readonly List<(MetricDefinition Def, TextBlock ValueTb, ProgressBar? Bar)> _liveItems = [];
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading;
    private bool _isCardMode;

    public MonitorPage()
    {
        InitializeComponent();
        _isCardMode = App.Settings.Current.UseCardLayout;
        FlyoutMetricsHost.IsCardMode = _isCardMode;
        ViewSwitchSlot.Children.Add(UiBuilders.CreateViewModeSwitcher(_isCardMode, isCard =>
        {
            _isCardMode = isCard;
            FlyoutMetricsHost.IsCardMode = isCard;
            BuildFlyoutMetricRows();
        }));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _liveTimer.Tick += (_, _) => UpdateLiveValues();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        _isCardMode = App.Settings.Current.UseCardLayout;
        FlyoutMetricsHost.IsCardMode = _isCardMode;
        App.Settings.EnsureDefaults();
        MonitorSwitch.IsOn = App.Settings.Current.MonitorEnabled;
        PopulateMetricBox(TopMetricBox, App.Settings.Current.MonitorTopMetric, allowNone: false);
        PopulateMetricBox(BottomMetricBox, App.Settings.Current.MonitorBottomMetric, allowNone: true);
        UpdateStatus();
        BuildFlyoutMetricRows();
        _loading = false;
        _liveTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _liveTimer.Stop();

    private static void PopulateMetricBox(ComboBox box, string selectedId, bool allowNone)
    {
        box.Items.Clear();
        if (allowNone)
            box.Items.Add(new ComboBoxItem { Content = "不显示（仅单行）", Tag = "NONE" });
        foreach (var m in SystemMetricsEngine.AllMetrics)
            box.Items.Add(new ComboBoxItem { Content = $"{m.FullName}（{m.ShortLabel}）", Tag = m.Id });

        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem it && string.Equals(it.Tag as string, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private void BuildFlyoutMetricRows()
    {
        _liveItems.Clear();
        var selected = new HashSet<string>(
            App.Settings.Current.MonitorFlyoutMetrics ?? [],
            StringComparer.OrdinalIgnoreCase);

        var snap = SystemMetricsEngine.Sample();
        var elements = new List<UIElement>();
        foreach (var m in SystemMetricsEngine.AllMetrics)
        {
            bool isChecked = selected.Contains(m.Id);
            var border = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
            };

            var valTb = new TextBlock
            {
                Text = SystemMetricsEngine.FormatDetailed(m.Id, snap),
                FontSize = _isCardMode ? 18 : 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };

            ProgressBar? bar = null;
            if (m.HasPercentageBar)
            {
                bar = new ProgressBar
                {
                    Maximum = 100,
                    Minimum = 0,
                    Value = SystemMetricsEngine.GetPercentage(m.Id, snap) ?? 0,
                    Height = 3,
                    MinHeight = 3,
                };
            }

            if (_isCardMode)
            {
                var stack = new StackPanel { Spacing = 8 };
                var top = new Grid { ColumnSpacing = 8 };
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var icon = new FontIcon { Glyph = m.Glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
                top.Children.Add(icon);
                var title = new StackPanel { Spacing = 1 };
                title.Children.Add(new TextBlock { Text = m.FullName, FontWeight = FontWeights.SemiBold });
                title.Children.Add(new TextBlock
                {
                    Text = $"简写 {m.ShortLabel}",
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
                Grid.SetColumn(title, 1);
                top.Children.Add(title);
                stack.Children.Add(top);
                stack.Children.Add(valTb);
                if (bar != null) stack.Children.Add(bar);

                // 卡片内嵌开关按钮
                var toggleBtn = new Button
                {
                    Content = isChecked ? "✓ 已显示在悬浮窗" : "+ 加入悬浮窗",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 5, 10, 5),
                };
                if (isChecked) toggleBtn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                toggleBtn.Click += (_, _) =>
                {
                    ToggleMetricSelection(m.Id);
                    BuildFlyoutMetricRows();
                };
                stack.Children.Add(toggleBtn);
                border.Child = stack;
            }
            else
            {
                var stack = new StackPanel { Spacing = 6 };
                var grid = new Grid { ColumnSpacing = 12 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var cb = new CheckBox
                {
                    IsChecked = isChecked,
                    MinWidth = 28,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                cb.Checked += (_, _) => SetMetricSelected(m.Id, true);
                cb.Unchecked += (_, _) => SetMetricSelected(m.Id, false);
                grid.Children.Add(cb);

                var icon = new FontIcon { Glyph = m.Glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(icon, 1);
                grid.Children.Add(icon);

                var labelPanel = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                labelPanel.Children.Add(new TextBlock { Text = m.FullName, FontWeight = FontWeights.SemiBold });
                labelPanel.Children.Add(new TextBlock
                {
                    Text = $"任务栏简写：{m.ShortLabel}",
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
                Grid.SetColumn(labelPanel, 2);
                grid.Children.Add(labelPanel);

                Grid.SetColumn(valTb, 3);
                grid.Children.Add(valTb);
                stack.Children.Add(grid);
                if (bar != null) stack.Children.Add(bar);
                border.Child = stack;
            }

            elements.Add(border);
            _liveItems.Add((m, valTb, bar));
        }

        FlyoutMetricsHost.SetItems(elements);
    }

    private void ToggleMetricSelection(string id)
    {
        var list = App.Settings.Current.MonitorFlyoutMetrics ?? [];
        if (list.Contains(id, StringComparer.OrdinalIgnoreCase))
            list.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        else
            list.Add(id);
        App.Settings.Current.MonitorFlyoutMetrics = list;
        App.Settings.Save();
    }

    private void SetMetricSelected(string id, bool selected)
    {
        if (_loading) return;
        var list = App.Settings.Current.MonitorFlyoutMetrics ?? [];
        list.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        if (selected) list.Add(id);
        App.Settings.Current.MonitorFlyoutMetrics = list;
        App.Settings.Save();
    }

    private void UpdateLiveValues()
    {
        var snap = SystemMetricsEngine.Sample();
        foreach (var (def, tb, bar) in _liveItems)
        {
            tb.Text = SystemMetricsEngine.FormatDetailed(def.Id, snap);
            if (bar != null)
            {
                if (SystemMetricsEngine.GetPercentage(def.Id, snap) is { } pct)
                {
                    bar.Visibility = Visibility.Visible;
                    bar.Value = Math.Clamp(pct, 0, 100);
                }
                else
                {
                    bar.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    private void SetSelectionSet(IEnumerable<string> ids)
    {
        App.Settings.Current.MonitorFlyoutMetrics = ids.ToList();
        App.Settings.Save();
        BuildFlyoutMetricRows();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
        => SetSelectionSet(SystemMetricsEngine.AllMetrics.Select(m => m.Id));

    private void OnSelectCore(object sender, RoutedEventArgs e)
        => SetSelectionSet(["CPU", "RAM", "GPU", "GPU_TEMP", "NET_DOWN", "NET_UP"]);

    private void OnResetDefaults(object sender, RoutedEventArgs e)
        => SetSelectionSet(SystemMetricsEngine.DefaultFlyoutMetricIds);

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (MonitorSwitch.IsOn != App.Settings.Current.MonitorEnabled)
            App.ToggleMonitor();
        UpdateStatus();
    }

    private void OnTaskbarMetricChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (TopMetricBox.SelectedItem is ComboBoxItem topIt && topIt.Tag is string top)
            App.Settings.Current.MonitorTopMetric = top;
        if (BottomMetricBox.SelectedItem is ComboBoxItem botIt && botIt.Tag is string bot)
            App.Settings.Current.MonitorBottomMetric = bot;
        App.Settings.Save();
        App.RefreshWidget();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (!App.Settings.Current.MonitorEnabled)
        {
            Status.Text = "未启用：贴片只显示外设电量";
            return;
        }
        string top = SystemMetricsEngine.AllMetrics.FirstOrDefault(m => m.Id == App.Settings.Current.MonitorTopMetric)?.ShortLabel ?? App.Settings.Current.MonitorTopMetric;
        string bot = App.Settings.Current.MonitorBottomMetric == "NONE"
            ? "隐藏"
            : (SystemMetricsEngine.AllMetrics.FirstOrDefault(m => m.Id == App.Settings.Current.MonitorBottomMetric)?.ShortLabel ?? App.Settings.Current.MonitorBottomMetric);
        Status.Text = $"已启用：任务栏显示「{top} / {bot}」，鼠标移至任务栏监控区域自动弹出详细悬浮窗";
    }
}
