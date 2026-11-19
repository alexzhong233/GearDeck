using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using GearDeck.Core.Models;
using Windows.Graphics;

namespace GearDeck.App;

/// <summary>点击任务栏组后弹出：显示组图标、标题与设备列表，鼠标移出自动关闭。</summary>
public sealed class WidgetGroupWindow : Window
{
    private bool _entered;

    public WidgetGroupWindow(string title, IReadOnlyList<PeripheralDevice> devices, RectInt32? anchor = null, string? groupGlyph = null, string? groupIconPath = null)
    {
        Title = "GearDeck";
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
        panel.PointerExited += (_, _) => { if (_entered) Close(); };

        var root = new StackPanel { Spacing = 8 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 0, 4, 2) };
        if (!string.IsNullOrWhiteSpace(groupIconPath) && File.Exists(groupIconPath))
        {
            header.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(groupIconPath)),
                Width = 18,
                Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else if (!string.IsNullOrWhiteSpace(groupGlyph))
        {
            header.Children.Add(new FontIcon
            {
                Glyph = groupGlyph,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(header);

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
                Text = $"{d.Provider} · {d.Transport}",
                FontSize = 11,
                Foreground = new SolidColorBrush(dark ? Microsoft.UI.Colors.LightGray : Microsoft.UI.Colors.DimGray),
            });
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            var battery = new TextBlock
            {
                Text = d.FormatDualEarSummary(compact: true) ?? (d.BatteryPercent is { } p ? $"{p}%" : "--"),
                FontSize = d.LeftEarPercent.HasValue || d.RightEarPercent.HasValue ? 12.5 : 15,
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

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint dpi = GetDpiForWindow(hwnd);
        double scale = (dpi == 0 ? 96u : dpi) / 96.0;
        int flyoutW = (int)Math.Round(320 * scale);
        int flyoutH = Math.Clamp(
            (int)Math.Round((52 + Math.Max(1, devices.Count) * 56) * scale),
            (int)Math.Round(104 * scale),
            (int)Math.Round(480 * scale));
        AppWindow.Resize(new SizeInt32(flyoutW, flyoutH));

        int gap = (int)Math.Round(8 * scale);
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
                area.X + area.Width - flyoutW - (int)Math.Round(20 * scale),
                area.Y + area.Height - flyoutH - (int)Math.Round(12 * scale)));
        }

        Activate();
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
