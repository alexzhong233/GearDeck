using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Windows;

/// <summary>
/// 任务栏监控区域悬停或点击弹出的无边框系统指标浮窗；支持浅/深主题、自适应高度、DPI 定位与自动关闭。
/// </summary>
public sealed partial class WidgetMonitorWindow : Window
{
    private const double FlyoutWidthDip = 336;
    private const double MinFlyoutHeightDip = 116;
    private const double MaxFlyoutHeightDip = 600;

    private static readonly FontFamily IconFont = Controls.UiBuilders.IconFont;
    private static WidgetMonitorWindow? _open;

    private readonly List<(string Id, TextBlock Value, ProgressBar? Bar)> _rows = [];
    private readonly DispatcherTimer _sampleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private MetricScope _scope = MetricScope.All;
    private DispatcherTimer? _hoverTimer;
    private PixelRect? _anchor;
    private long _openedTick;
    private long _lastAnchorTick;
    private int _outsideTicks;
    private bool _isHoverMode;
    private bool _enteredWindow;
    private bool _dark;
    private IBrush _secondaryBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4));
    private IBrush _cardBrush = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));

    public bool IsClosed { get; private set; }

    public WidgetMonitorWindow()
    {
        InitializeComponent();
        Title = "GearDeck Monitor";
        Width = FlyoutWidthDip;
        _open = this;
        _openedTick = Environment.TickCount64;
        _lastAnchorTick = _openedTick;

        IconText.FontFamily = IconFont;
        SettingsBtn.FontFamily = IconFont;
        SettingsBtn.Click += (_, _) =>
        {
            Close();
            App.ShowMainWindow();
            MainWindow.Instance?.OpenMonitorPage();
        };

        ApplyTheme();
        BuildRows();
        RefreshValues();

        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged += OnThemeChanged;

        Opened += (_, _) =>
        {
            _openedTick = Environment.TickCount64;
            _lastAnchorTick = _openedTick;
            FlushClickState();
            AdjustFlyoutHeight();
            StartHoverMonitor();
        };

        PointerEntered += (_, _) =>
        {
            _enteredWindow = true;
            _outsideTicks = 0;
        };

        PointerExited += (_, _) =>
        {
            if (!_isHoverMode || OperatingSystem.IsWindows()) return;
            Dispatcher.UIThread.Post(async () =>
            {
                await System.Threading.Tasks.Task.Delay(450);
                if (!IsClosed && _isHoverMode && !IsPointerOver && (_anchor != null || _enteredWindow))
                    Close();
            });
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };

        Deactivated += (_, _) =>
        {
            if (!IsClosed && !_isHoverMode && Environment.TickCount64 - _openedTick > 220)
                Close();
        };

        Closed += (_, _) =>
        {
            IsClosed = true;
            _sampleTimer.Stop();
            _hoverTimer?.Stop();
            if (Application.Current != null)
                Application.Current.ActualThemeVariantChanged -= OnThemeChanged;
            if (ReferenceEquals(_open, this))
                _open = null;
        };

        _sampleTimer.Tick += (_, _) => RefreshValues();
        _sampleTimer.Start();
    }

    /// <summary>供按 Rect 锚点构造兼容。</summary>
    public WidgetMonitorWindow(Rect anchor) : this()
    {
        if (anchor.Width > 0 && anchor.Height > 0)
            UpdateAnchor((int)anchor.X, (int)anchor.Y, (int)anchor.Width, (int)anchor.Height);
    }

    /// <summary>单例展示或更新系统监控浮窗；点击模式再次点击切换关闭，悬浮模式复用更新。</summary>
    public static WidgetMonitorWindow? ShowOrUpdate(Rect? anchor, bool isHover = false)
    {
        WidgetMonitorWindow? result = null;
        void Run()
        {
            WidgetGroupWindow.CloseIfOpen();
            if (_open is { IsClosed: false, IsVisible: true } existing)
            {
                if (!isHover)
                {
                    if (!existing._isHoverMode && Environment.TickCount64 - existing._openedTick >= 450)
                    {
                        existing.Close();
                        return;
                    }

                    existing._isHoverMode = false;
                    existing._outsideTicks = 0;
                    existing._openedTick = Environment.TickCount64;
                    existing._lastAnchorTick = existing._openedTick;
                    if (anchor is { Width: > 0, Height: > 0 } ar)
                        existing.UpdateAnchor((int)ar.X, (int)ar.Y, (int)ar.Width, (int)ar.Height);
                    existing.Activate();
                    result = existing;
                    return;
                }

                existing._outsideTicks = 0;
                existing._lastAnchorTick = Environment.TickCount64;
                if (anchor is { Width: > 0, Height: > 0 } ah)
                    existing.UpdateAnchor((int)ah.X, (int)ah.Y, (int)ah.Width, (int)ah.Height);
                result = existing;
                return;
            }

            var created = new WidgetMonitorWindow
            {
                ShowActivated = !isHover,
                _isHoverMode = isHover,
            };
            if (anchor is { Width: > 0, Height: > 0 } r)
                created.UpdateAnchor((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            else
                created.PositionAtAnchor();
            created.Show();
            _open = created;
            result = created;
        }

        if (Dispatcher.UIThread.CheckAccess()) Run();
        else Dispatcher.UIThread.Invoke(Run);
        return result;
    }

    public static void CloseIfOpen()
    {
        if (Dispatcher.UIThread.CheckAccess()) CloseIfOpenCore();
        else Dispatcher.UIThread.Invoke(CloseIfOpenCore);
    }

    private static void CloseIfOpenCore()
    {
        var window = _open;
        _open = null;
        if (window != null)
        {
            try { window.Close(); }
            catch { }
        }
    }

    /// <summary>记录任务栏监控块的物理像素锚点，并重新按弹窗自身尺寸与 DPI 定位。</summary>
    public void UpdateAnchor(int x, int y, int w, int h)
    {
        if (IsClosed) return;
        _anchor = w > 0 && h > 0 ? new PixelRect(x, y, w, h) : null;
        _outsideTicks = 0;
        _lastAnchorTick = Environment.TickCount64;
        PositionAtAnchor();
        Topmost = true;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsClosed) return;
        ApplyTheme();
        BuildRows();
        RefreshValues();
    }

    private void ApplyTheme()
    {
        _dark = Application.Current?.ActualThemeVariant != ThemeVariant.Light;

        RootBorder.Background = new SolidColorBrush(_dark
            ? Color.FromArgb(0xF5, 0x20, 0x20, 0x20)
            : Color.FromArgb(0xFA, 0xF9, 0xF9, 0xF9));
        RootBorder.BorderBrush = new SolidColorBrush(_dark
            ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x22, 0x00, 0x00, 0x00));

        var primary = new SolidColorBrush(_dark ? Color.FromRgb(0xF5, 0xF5, 0xF5) : Color.FromRgb(0x1A, 0x1A, 0x1A));
        _secondaryBrush = new SolidColorBrush(_dark ? Color.FromRgb(0xB4, 0xB4, 0xB4) : Color.FromRgb(0x5E, 0x5E, 0x5E));
        _cardBrush = new SolidColorBrush(_dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0));

        TitleText.Foreground = primary;
        IconText.Foreground = primary;
        SettingsBtn.Foreground = _secondaryBrush;
    }

    public static void RefreshSelectionIfOpen()
    {
        if (_open is not { IsClosed: false } win)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (win.IsClosed) return;
            win.BuildRows();
            win.RefreshValues();
            win.AdjustFlyoutHeight();
        });
    }

    private void BuildRows()
    {
        var selected = App.Settings.Current.MonitorFlyoutMetrics;
        var ids = selected != null && selected.Count > 0
            ? selected.ToList()
            : DesktopMetricsEngine.DefaultFlyoutMetricIds.ToList();
        _scope = SystemMetricsEngine.FromMetricIds(ids.ToArray());
        var snap = DesktopMetricsEngine.Sample(_scope);

        MetricHost.Children.Clear();
        _rows.Clear();
        double estHeight = 60;

        foreach (string id in ids)
        {
            var def = DesktopMetricsEngine.AllMetrics.FirstOrDefault(m => m.Id == id);
            if (def == null) continue;

            var card = new Border
            {
                Background = _cardBrush,
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
                Foreground = _secondaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var name = new TextBlock
            {
                Text = def.FullName,
                FontSize = 12,
                Foreground = TitleText.Foreground,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 1);
            line.Children.Add(name);

            var value = new TextBlock
            {
                Text = DesktopMetricsEngine.FormatDetailed(def.Id, snap),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = TitleText.Foreground,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
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
            estHeight += def.HasPercentageBar ? 48 : 38;
        }

        if (_rows.Count == 0)
        {
            MetricHost.Children.Add(new TextBlock
            {
                Text = "未勾选悬浮显示项，请点击右上角设置图标挑选指标。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _secondaryBrush,
                Margin = new Thickness(2, 8, 2, 8),
            });
            estHeight = 124;
        }

        Height = Math.Clamp(estHeight, MinFlyoutHeightDip, MaxFlyoutHeightDip);
        PositionAtAnchor();
        Dispatcher.UIThread.Post(AdjustFlyoutHeight, DispatcherPriority.Loaded);
    }

    private void AdjustFlyoutHeight()
    {
        if (IsClosed) return;
        MetricHost.Margin = new Thickness(0);
        HeaderGrid.Measure(new Size(FlyoutWidthDip - 26, double.PositiveInfinity));
        MetricHost.Measure(new Size(FlyoutWidthDip - 26, double.PositiveInfinity));
        double headerH = Math.Max(HeaderGrid.DesiredSize.Height, HeaderGrid.Bounds.Height);
        double contentH = MetricHost.DesiredSize.Height;
        double rawDip = headerH + contentH + 34;
        if (rawDip > MaxFlyoutHeightDip)
        {
            MetricHost.Margin = new Thickness(0, 0, 10, 0);
            MetricHost.Measure(new Size(FlyoutWidthDip - 36, double.PositiveInfinity));
            contentH = MetricHost.DesiredSize.Height;
            rawDip = headerH + contentH + 34;
        }
        double targetDip = Math.Clamp(Math.Ceiling(rawDip), MinFlyoutHeightDip, MaxFlyoutHeightDip);
        if (Math.Abs(Height - targetDip) > 1)
            Height = targetDip;
        PositionAtAnchor();
    }

    private void PositionAtAnchor()
    {
        if (IsClosed) return;
        var screen = _anchor is { Width: > 0 } a
            ? (Screens.ScreenFromPoint(new PixelPoint(a.X + a.Width / 2, a.Y)) ?? Screens.Primary)
            : Screens.Primary;

        double scale = screen?.Scaling ?? RenderScaling;
        if (scale <= 0.1) scale = 1.0;

        int flyoutW = (int)Math.Round(FlyoutWidthDip * scale);
        int flyoutH = Math.Clamp(
            (int)Math.Round(Height * scale),
            (int)Math.Round(MinFlyoutHeightDip * scale),
            (int)Math.Round(MaxFlyoutHeightDip * scale));
        int gap = (int)Math.Round(8 * scale);

        if (_anchor is { Width: > 0, Height: > 0 } anc)
        {
            var bounds = screen?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
            int x = Math.Clamp(
                anc.X + (anc.Width - flyoutW) / 2,
                bounds.X + gap,
                Math.Max(bounds.X + gap, bounds.X + bounds.Width - flyoutW - gap));
            bool taskbarAtTop = anc.Y < bounds.Y + bounds.Height / 3;
            int y = taskbarAtTop ? anc.Y + anc.Height + gap : anc.Y - flyoutH - gap;
            y = Math.Clamp(
                y,
                bounds.Y + gap,
                Math.Max(bounds.Y + gap, bounds.Y + bounds.Height - flyoutH - gap));
            Position = new PixelPoint(x, y);
        }
        else
        {
            var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            Position = new PixelPoint(
                Math.Max(area.X, area.X + area.Width - flyoutW - (int)Math.Round(20 * scale)),
                Math.Max(area.Y, area.Y + area.Height - flyoutH - (int)Math.Round(12 * scale)));
        }
    }

    private void StartHoverMonitor()
    {
        if (!OperatingSystem.IsWindows() || _hoverTimer != null) return;
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _hoverTimer.Tick += (_, _) => CheckAutoDismiss();
        _hoverTimer.Start();
    }

    private void CheckAutoDismiss()
    {
        if (IsClosed || !OperatingSystem.IsWindows()) return;
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var pt)) return;

        bool insideWindow = GetWindowRect(hwnd, out var wr)
            && pt.X >= wr.Left - 14 && pt.X <= wr.Right + 14
            && pt.Y >= wr.Top - 14 && pt.Y <= wr.Bottom + 18;
        if (insideWindow)
            _enteredWindow = true;

        bool insideAnchor = _anchor is { Width: > 0 } a
            && pt.X >= a.X - 10 && pt.X <= a.X + a.Width + 10
            && pt.Y >= a.Y - 18 && pt.Y <= a.Y + a.Height + 12;

        if (Environment.TickCount64 - _lastAnchorTick > 220)
        {
            bool clicked = (GetAsyncKeyState(0x01) & 0x8001) != 0 || (GetAsyncKeyState(0x02) & 0x8001) != 0;
            if (clicked && !insideWindow && (!_isHoverMode || !insideAnchor))
            {
                Close();
                return;
            }
        }

        if (!_isHoverMode)
            return;

        if (insideWindow || insideAnchor)
        {
            _outsideTicks = 0;
            return;
        }

        // 无任务栏锚点（例如从设置页按钮弹出）且指针从未进入浮窗时，仅靠点击外部/失焦收起。
        if (_anchor == null && !_enteredWindow)
            return;

        if (Environment.TickCount64 - _lastAnchorTick < 550)
            return;

        _outsideTicks++;
        if (_outsideTicks >= 2)
            Close();
    }

    private static void FlushClickState()
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = GetAsyncKeyState(0x01);
        _ = GetAsyncKeyState(0x02);
    }

    private void RefreshValues()
    {
        if (IsClosed) return;
        var snap = DesktopMetricsEngine.Sample(_scope);
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

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
}
