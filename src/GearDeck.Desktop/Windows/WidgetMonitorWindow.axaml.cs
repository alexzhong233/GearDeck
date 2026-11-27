using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Windows;

/// <summary>
/// 任务栏监控区域悬停弹出的轻量无边框系统指标浮窗，使用 DesktopMetricsEngine 每秒采样。
/// </summary>
public sealed partial class WidgetMonitorWindow : Window
{
    private static readonly FontFamily IconFont = Controls.UiBuilders.IconFont;
    private readonly List<(string Id, TextBlock Value, ProgressBar? Bar)> _rows = [];
    private readonly DispatcherTimer _sampleTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public bool IsClosed { get; private set; }

    public WidgetMonitorWindow()
    {
        InitializeComponent();
        Title = "GearDeck Monitor";

        BuildRows();
        CloseBtn.Click += (_, _) => Close();
        Closed += (_, _) =>
        {
            IsClosed = true;
            _sampleTimer.Stop();
        };
        _sampleTimer.Tick += (_, _) => RefreshValues();
        _sampleTimer.Start();
        RefreshValues();
    }

    /// <summary>供既有按 Rect 锚点反射创建路径兼容。</summary>
    public WidgetMonitorWindow(Rect anchor) : this()
    {
        if (anchor.Width > 0 && anchor.Height > 0)
            UpdateAnchor((int)anchor.X, (int)anchor.Y, (int)anchor.Width, (int)anchor.Height);
    }

    /// <summary>更新浮窗的物理像素位置与尺寸。</summary>
    public void UpdateAnchor(int x, int y, int w, int h)
    {
        if (IsClosed) return;
        Position = new PixelPoint(x, y);
        if (w > 0) Width = Math.Clamp(w, 240, 520);
        if (h > 0) Height = Math.Clamp(h, 110, 640);
        Topmost = true;
    }

    private void BuildRows()
    {
        var selected = App.Settings.Current.MonitorFlyoutMetrics;
        var ids = selected != null && selected.Count > 0
            ? selected.ToList()
            : DesktopMetricsEngine.DefaultFlyoutMetricIds.ToList();
        var snap = DesktopMetricsEngine.Sample();

        MetricHost.Children.Clear();
        _rows.Clear();
        foreach (string id in ids)
        {
            var def = DesktopMetricsEngine.AllMetrics.FirstOrDefault(m => m.Id == id);
            if (def == null) continue;

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
            };
            var rowStack = new StackPanel { Spacing = 4 };
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            line.Children.Add(new TextBlock
            {
                Text = def.Glyph,
                FontFamily = IconFont,
                FontSize = 14,
                Foreground = new SolidColorBrush(Colors.LightGray),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            var name = new TextBlock
            {
                Text = def.FullName,
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 1);
            line.Children.Add(name);

            var value = new TextBlock
            {
                Text = DesktopMetricsEngine.FormatDetailed(def.Id, snap),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 2);
            line.Children.Add(value);
            rowStack.Children.Add(line);

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
                rowStack.Children.Add(bar);
            }

            card.Child = rowStack;
            MetricHost.Children.Add(card);
            _rows.Add((def.Id, value, bar));
        }

        if (_rows.Count == 0)
        {
            MetricHost.Children.Add(new TextBlock
            {
                Text = "未勾选悬浮显示项，请先在「系统监控」页面挑选指标。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Colors.LightGray),
                Margin = new Thickness(2, 8, 2, 8),
            });
        }
    }

    private void RefreshValues()
    {
        if (IsClosed) return;
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
}
