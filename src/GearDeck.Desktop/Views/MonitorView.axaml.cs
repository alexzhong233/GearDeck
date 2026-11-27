using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GearDeck.Desktop.Services;
using GearDeck.Desktop.Windows;

namespace GearDeck.Desktop.Views;

/// <summary>
/// Avalonia 版系统监控页：12 项指标实时面板 + 任务栏双指标配置 + 桌面悬浮条开关。
/// </summary>
public partial class MonitorView : UserControl
{
    private static readonly FontFamily IconFont = Controls.UiBuilders.IconFont;
    private static IBrush ResolveBrush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var r) == true && r is IBrush b
            ? b
            : fallback;
    private readonly List<(string Id, TextBlock Value, ProgressBar? Bar)> _rows = [];
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _binding = true;
    private bool _updating;

    public MonitorView()
    {
        InitializeComponent();
        WireEvents();
        LoadSettings();
        BuildMetricCards();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _binding = false;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        FloatingMonitorWindow.StateChanged += OnFloatingStateChanged;
        OnFloatingStateChanged();
        _liveTimer.Start();
        UpdateLiveValues();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        FloatingMonitorWindow.StateChanged -= OnFloatingStateChanged;
        _liveTimer.Stop();
    }

    private void WireEvents()
    {
        MonitorSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            if ((MonitorSwitch.IsChecked == true) != App.Settings.Current.MonitorEnabled)
                App.ToggleMonitor();
            UpdateStatusText();
        };
        TopMetricBox.SelectionChanged += OnTaskbarMetricChanged;
        BottomMetricBox.SelectionChanged += OnTaskbarMetricChanged;
        FloatingToggleBtn.Click += (_, _) => FloatingMonitorWindow.ShowOrToggle();
        GoWidgetBtn.Click += (_, _) => MainWindow.Instance?.OpenWidgetPage();
        _liveTimer.Tick += (_, _) => UpdateLiveValues();
    }

    private void LoadSettings()
    {
        bool previous = _binding;
        _binding = true;
        try
        {
            App.Settings.EnsureDefaults();
            var s = App.Settings.Current;
            MonitorSwitch.IsChecked = s.MonitorEnabled;
            PopulateMetricCombo(TopMetricBox, s.MonitorTopMetric, allowNone: false);
            PopulateMetricCombo(BottomMetricBox, s.MonitorBottomMetric, allowNone: true);
            UpdateStatusText();
        }
        finally { _binding = previous; }
    }

    private void BuildMetricCards()
    {
        var snap = DesktopMetricsEngine.Sample();
        MetricsHost.Children.Clear();
        _rows.Clear();

        foreach (var def in DesktopMetricsEngine.AllMetrics)
        {
            var card = new Border
            {
                Width = 306,
                Margin = new Thickness(0, 0, 10, 10),
                Background = ResolveBrush("CardBackgroundFillColorDefaultBrush", Brushes.Transparent),
                BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush", Brushes.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
            };
            var stack = new StackPanel { Spacing = 8 };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.Children.Add(new TextBlock
            {
                Text = def.Glyph,
                FontFamily = IconFont,
                FontSize = 18,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            var title = new StackPanel { Spacing = 1, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = def.FullName, FontWeight = FontWeight.SemiBold });
            Grid.SetColumn(title, 1);
            head.Children.Add(title);
            stack.Children.Add(head);

            var value = new TextBlock
            {
                Text = DesktopMetricsEngine.FormatDetailed(def.Id, snap),
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
            };
            stack.Children.Add(value);

            ProgressBar? bar = null;
            if (def.HasPercentageBar)
            {
                bar = new ProgressBar
                {
                    Maximum = 100,
                    Minimum = 0,
                    Value = DesktopMetricsEngine.GetPercentage(def.Id, snap) ?? 0,
                    Height = 3,
                    MinHeight = 3,
                };
                stack.Children.Add(bar);
            }

            card.Child = stack;
            MetricsHost.Children.Add(card);
            _rows.Add((def.Id, value, bar));
        }
    }

    private void UpdateLiveValues()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var snap = DesktopMetricsEngine.Sample();
            foreach (var (id, text, bar) in _rows)
            {
                text.Text = DesktopMetricsEngine.FormatDetailed(id, snap);
                if (bar == null) continue;
                if (DesktopMetricsEngine.GetPercentage(id, snap) is { } pct)
                {
                    bar.IsVisible = true;
                    bar.Value = Math.Clamp(pct, 0, 100);
                }
                else
                {
                    bar.IsVisible = false;
                }
            }
        }
        finally { _updating = false; }
    }

    private static void PopulateMetricCombo(ComboBox box, string selectedId, bool allowNone)
    {
        box.Items.Clear();
        if (allowNone)
            box.Items.Add(new ComboBoxItem { Content = "不显示（仅单行）", Tag = "NONE" });
        foreach (var m in DesktopMetricsEngine.AllMetrics)
            box.Items.Add(new ComboBoxItem { Content = $"{m.FullName}（{m.ShortLabel}）", Tag = m.Id });

        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private void OnTaskbarMetricChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_binding) return;
        if (TopMetricBox.SelectedItem is ComboBoxItem top && top.Tag is string topId)
            App.Settings.Current.MonitorTopMetric = topId;
        if (BottomMetricBox.SelectedItem is ComboBoxItem bottom && bottom.Tag is string bottomId)
            App.Settings.Current.MonitorBottomMetric = bottomId;
        App.Settings.Save();
        App.RefreshWidget();
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        if (!App.Settings.Current.MonitorEnabled)
        {
            StatusText.Text = "未启用：任务栏贴片只显示外设电量。";
            return;
        }
        string top = DesktopMetricsEngine.AllMetrics
            .FirstOrDefault(m => m.Id == App.Settings.Current.MonitorTopMetric)?.ShortLabel
            ?? App.Settings.Current.MonitorTopMetric;
        string bottom = App.Settings.Current.MonitorBottomMetric == "NONE"
            ? "隐藏"
            : (DesktopMetricsEngine.AllMetrics
                .FirstOrDefault(m => m.Id == App.Settings.Current.MonitorBottomMetric)?.ShortLabel
               ?? App.Settings.Current.MonitorBottomMetric);
        StatusText.Text = $"已启用：任务栏显示「{top} / {bottom}」，鼠标移至监控区域自动弹出详细悬浮窗。";
    }

    private void OnFloatingStateChanged()
        => FloatingToggleBtn.Content = FloatingMonitorWindow.IsOpen
            ? "关闭桌面悬浮监控条"
            : "显示桌面悬浮监控条";
}
