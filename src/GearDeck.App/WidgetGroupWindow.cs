using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using GearDeck.Core.Models;
using Windows.Graphics;

namespace GearDeck.App;

/// <summary>点击任务栏组后弹出：显示组图标、标题、贴片设置入口与设备列表。</summary>
public sealed class WidgetGroupWindow : Window
{
    private readonly DispatcherQueueTimer? _dismissTimer;
    private readonly long _openedAt;
    private readonly IntPtr _hwnd;
    private readonly double _scale;
    private readonly RectInt32? _anchor;
    private bool _entered;

    public string GroupId { get; }
    public bool IsClosed { get; private set; }

    public WidgetGroupWindow(string groupId, string title, IReadOnlyList<PeripheralDevice> devices, RectInt32? anchor = null, string? groupGlyph = null, string? groupIconPath = null)
    {
        GroupId = groupId;
        _anchor = anchor;
        Title = "GearDeck Flyout";
        _openedAt = Environment.TickCount64;
        Closed += (_, _) =>
        {
            IsClosed = true;
            _dismissTimer?.Stop();
        };

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

        bool dark = App.Current.RequestedTheme == ApplicationTheme.Dark;
        var panel = new Border
        {
            Background = new SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(245, 32, 32, 32)
                : Windows.UI.Color.FromArgb(250, 249, 249, 249)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(36, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12),
        };
        panel.PointerEntered += (_, _) => _entered = true;

        var root = new StackPanel { Spacing = 8 };
        var headerGrid = new Grid { Margin = new Thickness(4, 0, 0, 2) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrWhiteSpace(groupIconPath) && File.Exists(groupIconPath))
        {
            headerLeft.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(groupIconPath)),
                Width = 18,
                Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else if (!string.IsNullOrWhiteSpace(groupGlyph))
        {
            headerLeft.Children.Add(new FontIcon
            {
                Glyph = groupGlyph,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        headerLeft.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        headerGrid.Children.Add(headerLeft);

        var settingsBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE713", FontSize = 13 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 6, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(settingsBtn, "跳转到任务栏贴片设置");
        settingsBtn.Click += (_, _) =>
        {
            MainWindow.Instance?.OpenWidgetPage();
            Close();
        };
        Grid.SetColumn(settingsBtn, 1);
        headerGrid.Children.Add(settingsBtn);

        root.Children.Add(headerGrid);

        if (devices.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "无设备",
                FontSize = 13,
                Margin = new Thickness(8, 12, 8, 12),
                Foreground = new SolidColorBrush(dark ? Microsoft.UI.Colors.LightGray : Microsoft.UI.Colors.DimGray),
            });
        }

        foreach (var d in devices)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(28, 255, 255, 255) : Windows.UI.Color.FromArgb(16, 0, 0, 0)),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 8, 10, 8),
            };
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(Controls.UiBuilders.CreateDeviceAvatar(d, 28));

            var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = d.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            info.Children.Add(new TextBlock
            {
                Text = d.IsSleeping ? $"{d.Provider} · 休眠" : $"{d.Provider} · {d.Transport}",
                FontSize = 11,
                Foreground = new SolidColorBrush(dark ? Microsoft.UI.Colors.LightGray : Microsoft.UI.Colors.DimGray),
            });
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            var cached = App.Settings.GetLastKnownBattery(d.Id);
            string batDisplay;
            bool isDual = d.LeftEarPercent.HasValue || d.RightEarPercent.HasValue
                          || (cached != null && (cached.LeftEarPercent.HasValue || cached.RightEarPercent.HasValue));
            if (d.FormatDualEarSummary(compact: true) is { } dualText)
            {
                batDisplay = dualText;
            }
            else if (isDual && cached != null)
            {
                string l = cached.LeftEarPercent is { } lv ? $"{lv}%" : "--";
                string r = cached.RightEarPercent is { } rv ? $"{rv}%" : "--";
                batDisplay = $"L{l} R{r} (历史)";
            }
            else if (d.BatteryPercent is { } p)
            {
                batDisplay = d.State == BatteryState.Charging ? $"{p}%⚡" : $"{p}%";
            }
            else if (cached?.BatteryPercent is { } hp)
            {
                batDisplay = d.IsSleeping ? $"{hp}% (休眠)" : $"{hp}% (历史)";
            }
            else
            {
                batDisplay = "--";
            }

            var battery = new TextBlock
            {
                Text = batDisplay,
                FontSize = isDual ? 12 : 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(battery, 2);
            row.Children.Add(battery);

            card.Child = row;
            card.Tapped += (_, _) =>
            {
                MainWindow.Instance?.OpenDevice(d.Id);
                Close();
            };
            root.Children.Add(card);
        }

        panel.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root,
        };
        Content = panel;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint dpi = GetDpiForWindow(_hwnd);
        _scale = (dpi == 0 ? 96u : dpi) / 96.0;
        int flyoutW = (int)Math.Round(328 * _scale);
        int flyoutH = Math.Clamp(
            (int)Math.Round((56 + Math.Max(1, devices.Count) * 56) * _scale),
            (int)Math.Round(108 * _scale),
            (int)Math.Round(480 * _scale));
        AppWindow.Resize(new SizeInt32(flyoutW, flyoutH));

        int gap = (int)Math.Round(8 * _scale);
        if (anchor is { Width: > 0 } a)
        {
            var display = DisplayArea.GetFromPoint(new PointInt32(a.X + a.Width / 2, a.Y), DisplayAreaFallback.Primary);
            var bounds = display.OuterBounds;
            int x = Math.Clamp(a.X + (a.Width - flyoutW) / 2, bounds.X + gap, Math.Max(bounds.X + gap, bounds.X + bounds.Width - flyoutW - gap));
            bool taskbarAtTop = a.Y < bounds.Y + bounds.Height / 3;
            int y = taskbarAtTop
                ? a.Y + a.Height + gap
                : a.Y - flyoutH - gap;
            y = Math.Clamp(y, bounds.Y + gap, Math.Max(bounds.Y + gap, bounds.Y + bounds.Height - flyoutH - gap));
            AppWindow.Move(new PointInt32(x, y));
        }
        else
        {
            var area = DisplayArea.Primary.WorkArea;
            AppWindow.Move(new PointInt32(
                area.X + area.Width - flyoutW - (int)Math.Round(20 * _scale),
                area.Y + area.Height - flyoutH - (int)Math.Round(12 * _scale)));
        }

        try { AppWindow.Show(false); } catch { }
        SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

        var dq = DispatcherQueue.GetForCurrentThread();
        if (dq != null)
        {
            _dismissTimer = dq.CreateTimer();
            _dismissTimer.Interval = TimeSpan.FromMilliseconds(100);
            _dismissTimer.IsRepeating = true;
            _dismissTimer.Tick += OnDismissTick;
            _dismissTimer.Start();
        }
    }

    private void OnDismissTick(DispatcherQueueTimer sender, object args)
    {
        if (IsClosed) return;
        if (!GetCursorPos(out var pt) || !GetWindowRect(_hwnd, out var rc)) return;

        bool inside = pt.X >= rc.Left && pt.X <= rc.Right && pt.Y >= rc.Top && pt.Y <= rc.Bottom;
        if (inside)
        {
            _entered = true;
            return;
        }

        long elapsed = Environment.TickCount64 - _openedAt;
        if (elapsed >= 250)
        {
            bool lDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            bool rDown = (GetAsyncKeyState(0x02) & 0x8000) != 0;
            bool onAnchor = _anchor is { Width: > 0 } a
                            && pt.X >= a.X && pt.X <= a.X + a.Width
                            && pt.Y >= a.Y && pt.Y <= a.Y + a.Height;
            if ((lDown || rDown) && !onAnchor)
            {
                Close();
                return;
            }
        }

        if (_entered && elapsed >= 650)
        {
            int dx = pt.X < rc.Left ? rc.Left - pt.X : (pt.X > rc.Right ? pt.X - rc.Right : 0);
            int dy = pt.Y < rc.Top ? rc.Top - pt.Y : (pt.Y > rc.Bottom ? pt.Y - rc.Bottom : 0);
            int margin = (int)Math.Round(48 * _scale);
            if (dx > margin || dy > margin)
                Close();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
