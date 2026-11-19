using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using GearDeck.Metrics;
using Windows.Graphics;

namespace GearDeck.App;

/// <summary>鼠标悬停任务栏系统监控区域后弹出：展示用户勾选的实时系统指标与进度条，移出自动关闭。</summary>
public sealed class WidgetMonitorWindow : Window
{
    private readonly List<(MetricDefinition Def, TextBlock ValueText, ProgressBar? Bar)> _rows = [];
    private readonly DispatcherTimer _sampleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly IntPtr _hwnd;
    private RectInt32? _anchor;
    private int _outsideTicks;

    public bool IsClosed { get; private set; }

    public WidgetMonitorWindow(RectInt32? anchor = null)
    {
        _anchor = anchor;
        Title = "GearDeck Monitor";
        try
        {
            var appIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(appIcon))
                AppWindow.SetIcon(appIcon);
        }
        catch { }

        SystemBackdrop = new MicaBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;

        bool dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        var mutedBrush = new SolidColorBrush(dark ? Microsoft.UI.Colors.LightGray : Microsoft.UI.Colors.DimGray);

        var panel = new Border
        {
            Background = new SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(245, 32, 32, 32)
                : Windows.UI.Color.FromArgb(250, 249, 249, 249)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14),
        };

        var root = new StackPanel { Spacing = 8 };

        // 标题栏 + 设置入口
        var header = new Grid { ColumnSpacing = 8, Margin = new Thickness(2, 0, 2, 2) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new FontIcon { Glyph = "\uE950", FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        var titleTb = new TextBlock
        {
            Text = "系统实时监控",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titleTb, 1);
        header.Children.Add(titleTb);

        var configBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE713", FontSize = 13 },
            Padding = new Thickness(6, 4, 6, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(configBtn, "选择监控项目");
        configBtn.Click += (_, _) =>
        {
            MainWindow.Instance?.Show();
            MainWindow.Instance?.OpenMonitorPage();
            Close();
        };
        Grid.SetColumn(configBtn, 2);
        header.Children.Add(configBtn);
        root.Children.Add(header);

        var selectedIds = App.Settings.Current.MonitorFlyoutMetrics ?? [.. SystemMetricsEngine.DefaultFlyoutMetricIds];
        var metrics = SystemMetricsEngine.AllMetrics
            .Where(m => selectedIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var snap = SystemMetricsEngine.Sample();
        if (metrics.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "未勾选悬浮显示项，点击右上角齿轮挑选指标",
                FontSize = 12,
                Foreground = mutedBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 8, 2, 8),
            });
        }
        else
        {
            foreach (var m in metrics)
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(24, 255, 255, 255) : Windows.UI.Color.FromArgb(14, 0, 0, 0)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 7, 10, 7),
                };
                var rowStack = new StackPanel { Spacing = 4 };
                var lineGrid = new Grid { ColumnSpacing = 8 };
                lineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                lineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                lineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                lineGrid.Children.Add(new FontIcon { Glyph = m.Glyph, FontSize = 14, Foreground = mutedBrush, VerticalAlignment = VerticalAlignment.Center });

                var nameTb = new TextBlock { Text = m.FullName, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(nameTb, 1);
                lineGrid.Children.Add(nameTb);

                var valTb = new TextBlock
                {
                    Text = SystemMetricsEngine.FormatDetailed(m.Id, snap),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(valTb, 2);
                lineGrid.Children.Add(valTb);
                rowStack.Children.Add(lineGrid);

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
                    rowStack.Children.Add(bar);
                }

                card.Child = rowStack;
                root.Children.Add(card);
                _rows.Add((m, valTb, bar));
            }
        }

        panel.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root,
        };
        Content = panel;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint dpi = GetDpiForWindow(_hwnd);
        double scale = (dpi == 0 ? 96u : dpi) / 96.0;
        int flyoutW = (int)Math.Round(336 * scale);
        int estH = 60;
        foreach (var m in metrics)
            estH += m.HasPercentageBar ? 46 : 38;
        int flyoutH = Math.Clamp((int)Math.Round(estH * scale), (int)Math.Round(108 * scale), (int)Math.Round(560 * scale));
        AppWindow.Resize(new SizeInt32(flyoutW, flyoutH));

        PositionAtAnchor(anchor, flyoutW, flyoutH, scale);

        Closed += (_, _) =>
        {
            IsClosed = true;
            _sampleTimer.Stop();
            _hoverTimer.Stop();
        };

        _sampleTimer.Tick += (_, _) => RefreshValues();
        _hoverTimer.Tick += (_, _) => CheckHoverExit();
        _sampleTimer.Start();
        _hoverTimer.Start();

        AppWindow.Show(false);
        SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
    }

    public void UpdateAnchor(RectInt32? anchor)
    {
        if (IsClosed) return;
        _anchor = anchor;
        _outsideTicks = 0;
        uint dpi = GetDpiForWindow(_hwnd);
        double scale = (dpi == 0 ? 96u : dpi) / 96.0;
        var sz = AppWindow.Size;
        PositionAtAnchor(anchor, sz.Width, sz.Height, scale);
        SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
    }

    private void PositionAtAnchor(RectInt32? anchor, int flyoutW, int flyoutH, double scale)
    {
        int gap = (int)Math.Round(8 * scale);
        if (anchor is { Width: > 0 } a)
        {
            var display = DisplayArea.GetFromPoint(new PointInt32(a.X + a.Width / 2, a.Y), DisplayAreaFallback.Primary);
            var bounds = display.OuterBounds;
            int x = Math.Clamp(a.X + (a.Width - flyoutW) / 2, bounds.X + gap, Math.Max(bounds.X + gap, bounds.X + bounds.Width - flyoutW - gap));
            bool taskbarAtTop = a.Y < bounds.Y + bounds.Height / 3;
            int y = taskbarAtTop ? a.Y + a.Height + gap : a.Y - flyoutH - gap;
            y = Math.Clamp(y, bounds.Y + gap, Math.Max(bounds.Y + gap, bounds.Y + bounds.Height - flyoutH - gap));
            AppWindow.Move(new PointInt32(x, y));
        }
        else
        {
            var area = DisplayArea.Primary.WorkArea;
            AppWindow.Move(new PointInt32(
                area.X + area.Width - flyoutW - (int)Math.Round(20 * scale),
                area.Y + area.Height - flyoutH - (int)Math.Round(12 * scale)));
        }
    }

    private void RefreshValues()
    {
        if (IsClosed) return;
        var snap = SystemMetricsEngine.Sample();
        foreach (var (def, tb, bar) in _rows)
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

    private void CheckHoverExit()
    {
        if (IsClosed) return;
        if (!GetCursorPos(out var pt)) return;

        bool insideWindow = GetWindowRect(_hwnd, out var wr)
            && pt.X >= wr.Left - 14 && pt.X <= wr.Right + 14
            && pt.Y >= wr.Top - 14 && pt.Y <= wr.Bottom + 18;

        bool insideAnchor = _anchor is { Width: > 0 } a
            && pt.X >= a.X - 10 && pt.X <= a.X + a.Width + 10
            && pt.Y >= a.Y - 18 && pt.Y <= a.Y + a.Height + 12;

        if (insideWindow || insideAnchor)
        {
            _outsideTicks = 0;
            return;
        }

        _outsideTicks++;
        if (_outsideTicks >= 2)
            Close();
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
