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
using GearDeck.Core.Models;
using GearDeck.Core.Services;
using GearDeck.Desktop.Controls;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Windows;

/// <summary>
/// 任务栏分组点击后弹出的无边框置顶浮窗；支持浅/深主题、自适应高度、DPI 对齐、实时刷新与自动关闭。
/// </summary>
public sealed partial class WidgetGroupWindow : Window
{
    private const double FlyoutWidthDip = 328;
    private const double MinFlyoutHeightDip = 116;
    private const double MaxFlyoutHeightDip = 480;

    private static readonly FontFamily IconFont = Controls.UiBuilders.IconFont;
    private static WidgetGroupWindow? _open;

    private PixelRect? _anchor;
    private DispatcherTimer? _dismissTimer;
    private long _openedTick;
    private long _lastAnchorTick;
    private int _outsideTicks;
    private bool _isHoverMode;
    private bool _enteredWindow;
    private bool _isClosed;
    private bool _dark;
    private IBrush _secondaryBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4));
    private IBrush _cardBrush = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
    private IBrush _cardHoverBrush = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255));

    public string GroupId { get; }

    public static bool IsOpen => _open is { _isClosed: false };

    public WidgetGroupWindow() : this("builtin:mouse")
    {
    }

    public WidgetGroupWindow(string groupId)
    {
        InitializeComponent();
        GroupId = string.IsNullOrWhiteSpace(groupId) ? "builtin:mouse" : groupId;
        Title = "GearDeck Flyout";
        Width = FlyoutWidthDip;
        _open = this;
        _openedTick = Environment.TickCount64;
        _lastAnchorTick = _openedTick;

        SettingsBtn.FontFamily = IconFont;
        SettingsBtn.Click += (_, _) =>
        {
            Close();
            App.ShowMainWindow();
            MainWindow.Instance?.OpenWidgetPage();
        };

        ApplyTheme();
        RebuildDevices();

        App.Devices.DevicesUpdated += OnDevicesUpdated;
        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged += OnThemeChanged;

        Opened += (_, _) =>
        {
            _openedTick = Environment.TickCount64;
            _lastAnchorTick = _openedTick;
            FlushClickState();
            AdjustFlyoutHeight();
            StartDismissMonitor();
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
                if (!_isClosed && _isHoverMode && !IsPointerOver && (_anchor != null || _enteredWindow))
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
            if (!_isClosed && !_isHoverMode && Environment.TickCount64 - _openedTick > 220)
                Close();
        };

        Closed += (_, _) =>
        {
            _isClosed = true;
            _dismissTimer?.Stop();
            App.Devices.DevicesUpdated -= OnDevicesUpdated;
            if (Application.Current != null)
                Application.Current.ActualThemeVariantChanged -= OnThemeChanged;
            if (ReferenceEquals(_open, this))
                _open = null;
        };
    }

    /// <summary>供按 Rect 锚点构造兼容。</summary>
    public WidgetGroupWindow(string groupId, Rect anchor) : this(groupId)
    {
        if (anchor.Width > 0 && anchor.Height > 0)
            UpdateAnchor((int)anchor.X, (int)anchor.Y, (int)anchor.Width, (int)anchor.Height);
    }

    /// <summary>在给定任务栏锚点附近显示或切换分组浮窗；点击模式重复点击同组切换关闭，悬浮模式复用更新。</summary>
    public static WidgetGroupWindow? ShowNear(int x, int y, int w, int h, string groupId, bool isHover = false)
    {
        WidgetGroupWindow? result = null;
        void Run()
        {
            WidgetMonitorWindow.CloseIfOpen();
            if (_open is { _isClosed: false, IsVisible: true } existing
                && string.Equals(existing.GroupId, groupId, StringComparison.OrdinalIgnoreCase))
            {
                if (!isHover)
                {
                    if (!existing._isHoverMode && Environment.TickCount64 - existing._openedTick > 250)
                    {
                        CloseIfOpenCore();
                        return;
                    }

                    existing._isHoverMode = false;
                    existing._outsideTicks = 0;
                    existing._openedTick = Environment.TickCount64;
                    existing._lastAnchorTick = existing._openedTick;
                    if (w > 0 && h > 0)
                        existing.UpdateAnchor(x, y, w, h);
                    existing.Activate();
                    result = existing;
                    return;
                }

                existing._outsideTicks = 0;
                existing._lastAnchorTick = Environment.TickCount64;
                if (w > 0 && h > 0)
                    existing.UpdateAnchor(x, y, w, h);
                result = existing;
                return;
            }

            CloseIfOpenCore();
            var created = new WidgetGroupWindow(groupId)
            {
                ShowActivated = !isHover,
                _isHoverMode = isHover,
            };
            if (w > 0 && h > 0)
                created.UpdateAnchor(x, y, w, h);
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

    public static WidgetGroupWindow? ShowStandalone(string groupId, Rect? anchor = null, bool isHover = false)
    {
        if (anchor is { Width: > 0, Height: > 0 } r)
            return ShowNear((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, groupId, isHover);
        return ShowNear(0, 0, 0, 0, groupId, isHover);
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

    /// <summary>记录任务栏按钮的物理像素锚点，并重新按弹窗自身尺寸与 DPI 定位到任务栏上方。</summary>
    public void UpdateAnchor(int x, int y, int w, int h)
    {
        if (_isClosed) return;
        _anchor = w > 0 && h > 0 ? new PixelRect(x, y, w, h) : null;
        _outsideTicks = 0;
        _lastAnchorTick = Environment.TickCount64;
        PositionAtAnchor();
        Topmost = true;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_isClosed) return;
        ApplyTheme();
        RebuildDevices();
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
        _cardBrush = new SolidColorBrush(_dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0));
        _cardHoverBrush = new SolidColorBrush(_dark ? Color.FromArgb(42, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));

        TitleText.Foreground = primary;
        IconText.Foreground = primary;
        SettingsBtn.Foreground = _secondaryBrush;
    }

    private void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> _)
    {
        if (_isClosed) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isClosed) RebuildDevices();
        });
    }

    private void RebuildDevices()
    {
        var members = TaskbarGroupHelper.GetMembers(GroupId, App.LastDevices);
        string label = GroupId switch
        {
            "builtin:mouse" => "鼠标组",
            "builtin:keyboard" => "键盘组",
            "builtin:headset" => "耳机组",
            "builtin:controller" => "手柄组",
            _ => App.Settings.Current.CustomGroups.FirstOrDefault(g => g.Id == GroupId)?.DisplayName ?? "设备组",
        };
        TitleText.Text = label;

        var groupCfg = App.Settings.Current.TaskbarGroups
            .FirstOrDefault(g => string.Equals(g.GroupId, GroupId, StringComparison.OrdinalIgnoreCase))
            ?? new TaskbarGroupConfig { GroupId = GroupId, Visible = true };

        if (GroupIconCatalog.ResolveValidIconPath(groupCfg) is { } iconPath
            && DeviceImageService.CreateSourceFromFile(iconPath, 44) is { } iconSource)
        {
            IconSlot.Child = new Image
            {
                Source = iconSource,
                Width = 18,
                Height = 18,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            IconText.Text = GroupIconCatalog.ResolveEffectiveGlyph(groupCfg, members);
            IconText.FontFamily = IconFont;
            IconSlot.Child = IconText;
        }

        DeviceHost.Children.Clear();
        if (members.Count == 0)
        {
            DeviceHost.Children.Add(new TextBlock
            {
                Text = "当前分组暂无已连接设备",
                FontSize = 13,
                Margin = new Thickness(6, 12, 6, 12),
                Foreground = _secondaryBrush,
            });
        }
        else
        {
            foreach (var device in members)
            {
                var card = new Border
                {
                    Background = _cardBrush,
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(10, 8, 10, 8),
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                row.Children.Add(UiBuilders.CreateDeviceAvatar(device, 30));

                var info = new StackPanel
                {
                    Spacing = 1,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                info.Children.Add(new TextBlock
                {
                    Text = device.Name,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = TitleText.Foreground,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                info.Children.Add(new TextBlock
                {
                    Text = device.IsSleeping ? "接收器已连接 · 休眠" : (device.Connected ? $"{device.Provider} · 在线" : $"{device.Provider} · 离线"),
                    FontSize = 11,
                    Foreground = _secondaryBrush,
                });
                Grid.SetColumn(info, 1);
                row.Children.Add(info);

                var battery = BuildBatteryControl(device);
                Grid.SetColumn(battery, 2);
                row.Children.Add(battery);

                card.Child = row;
                card.PointerEntered += (_, _) => card.Background = _cardHoverBrush;
                card.PointerExited += (_, _) => card.Background = _cardBrush;
                card.Tapped += (_, _) =>
                {
                    Close();
                    App.ShowMainWindow();
                    MainWindow.Instance?.OpenDevice(device.Id);
                };
                DeviceHost.Children.Add(card);
            }
        }

        double estHeight = Math.Clamp(68 + Math.Max(1, members.Count) * 56, MinFlyoutHeightDip, MaxFlyoutHeightDip);
        if (Math.Abs(Height - estHeight) > 1 && !IsLoaded)
            Height = estHeight;

        PositionAtAnchor();
        Dispatcher.UIThread.Post(AdjustFlyoutHeight, DispatcherPriority.Loaded);
    }

    private double ComputeMaxFlyoutHeightDip()
    {
        var screen = _anchor is { Width: > 0 } a
            ? (Screens.ScreenFromPoint(new PixelPoint(a.X + a.Width / 2, a.Y)) ?? Screens.Primary)
            : Screens.Primary;
        double scale = screen?.Scaling ?? RenderScaling;
        if (scale <= 0.1) scale = 1.0;
        double workAreaDip = (screen?.WorkingArea.Height ?? (int)Math.Round(1000 * scale)) / scale;
        return Math.Clamp(workAreaDip - 80, MaxFlyoutHeightDip, 680);
    }

    private void AdjustFlyoutHeight()
    {
        if (_isClosed) return;
        double maxH = ComputeMaxFlyoutHeightDip();
        DeviceHost.Margin = new Thickness(0);
        HeaderGrid.Measure(new Size(FlyoutWidthDip - 26, double.PositiveInfinity));
        DeviceHost.Measure(new Size(FlyoutWidthDip - 26, double.PositiveInfinity));
        double headerH = Math.Max(HeaderGrid.DesiredSize.Height, HeaderGrid.Bounds.Height);
        double contentH = DeviceHost.DesiredSize.Height;
        double rawDip = headerH + contentH + 44;
        bool needScroll = rawDip > maxH;
        DeviceScroll.VerticalScrollBarVisibility = needScroll
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        if (needScroll)
        {
            DeviceHost.Margin = new Thickness(0, 0, 10, 0);
            DeviceHost.Measure(new Size(FlyoutWidthDip - 36, double.PositiveInfinity));
            contentH = DeviceHost.DesiredSize.Height;
            rawDip = headerH + contentH + 44;
        }
        double targetDip = Math.Clamp(Math.Ceiling(rawDip), MinFlyoutHeightDip, maxH);
        if (Math.Abs(Height - targetDip) > 1)
            Height = targetDip;
        PositionAtAnchor();
    }

    private void PositionAtAnchor()
    {
        if (_isClosed) return;
        var screen = _anchor is { Width: > 0 } a
            ? (Screens.ScreenFromPoint(new PixelPoint(a.X + a.Width / 2, a.Y)) ?? Screens.Primary)
            : Screens.Primary;

        double scale = screen?.Scaling ?? RenderScaling;
        if (scale <= 0.1) scale = 1.0;

        double maxH = ComputeMaxFlyoutHeightDip();
        int flyoutW = (int)Math.Round(FlyoutWidthDip * scale);
        int flyoutH = Math.Clamp(
            (int)Math.Round(Height * scale),
            (int)Math.Round(MinFlyoutHeightDip * scale),
            (int)Math.Round(maxH * scale));
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

    private void StartDismissMonitor()
    {
        if (!OperatingSystem.IsWindows() || _dismissTimer != null) return;
        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _dismissTimer.Tick += (_, _) => CheckOutsideClick();
        _dismissTimer.Start();
    }

    private void CheckOutsideClick()
    {
        if (_isClosed || !OperatingSystem.IsWindows()) return;
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

        if (!_isHoverMode) return;
        if (insideWindow || insideAnchor)
        {
            _outsideTicks = 0;
            return;
        }
        if (_anchor == null && !_enteredWindow) return;
        if (Environment.TickCount64 - _lastAnchorTick < 550) return;

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

    private Control BuildBatteryControl(PeripheralDevice device)
    {
        var cached = App.Settings.GetLastKnownBattery(device.Id);
        var chip = BatteryHistoryHelper.BuildChipText(device, string.Empty, true, cached);
        if (!string.IsNullOrEmpty(chip.TextTop) && !string.IsNullOrEmpty(chip.TextBottom))
        {
            var stack = new StackPanel
            {
                Spacing = 1,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(new TextBlock
            {
                Text = chip.TextTop,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = TitleText.Foreground,
                TextAlignment = TextAlignment.Right,
            });
            stack.Children.Add(new TextBlock
            {
                Text = chip.TextBottom,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = TitleText.Foreground,
                TextAlignment = TextAlignment.Right,
            });
            return stack;
        }

        return new TextBlock
        {
            Text = chip.Text,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = TitleText.Foreground,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
}
