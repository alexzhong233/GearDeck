using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GearDeck.Core.Models;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Controls;

/// <summary>
/// Avalonia 版通用 UI 构造器：卡片/列表切换器、设备头像、Hero 设备外观与图标回退字形。
/// </summary>
public static class UiBuilders
{
    // 优先使用跨平台内嵌 FluentAvalonia Symbols，其次 Windows Segoe Fluent Icons。
    public static readonly FontFamily IconFont = new("avares://FluentAvalonia/Fonts#Symbols, Segoe Fluent Icons, $default");

    public static StackPanel CreateViewModeSwitcher(bool isCard, Action<bool> onChanged)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var cardBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = "\uF0E2", FontFamily = IconFont, FontSize = 13 },
                    new TextBlock { Text = "卡片", FontSize = 12 },
                },
            },
            Padding = new Thickness(10, 5, 10, 5),
        };

        var listBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = "\uE8FD", FontFamily = IconFont, FontSize = 13 },
                    new TextBlock { Text = "列表", FontSize = 12 },
                },
            },
            Padding = new Thickness(10, 5, 10, 5),
        };

        void SyncStyles(bool cardSelected)
        {
            var accent = ResolveBrush("AccentFillColorDefaultBrush",
                new SolidColorBrush(Color.FromArgb(255, 0, 120, 215)));
            var normal = ResolveBrush("SubtleFillColorSecondaryBrush",
                new SolidColorBrush(Color.FromArgb(20, 128, 128, 128)));
            cardBtn.Background = cardSelected ? accent : normal;
            listBtn.Background = cardSelected ? normal : accent;
        }

        SyncStyles(isCard);
        cardBtn.Click += (_, _) =>
        {
            App.Settings.Current.UseCardLayout = true;
            App.Settings.Save();
            SyncStyles(true);
            onChanged(true);
        };
        listBtn.Click += (_, _) =>
        {
            App.Settings.Current.UseCardLayout = false;
            App.Settings.Save();
            SyncStyles(false);
            onChanged(false);
        };

        panel.Children.Add(cardBtn);
        panel.Children.Add(listBtn);
        return panel;
    }

    public static Border CreateDeviceAvatar(PeripheralDevice device, double size = 56)
    {
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(Math.Max(8, size * 0.16)),
            Background = new SolidColorBrush(Color.FromArgb(26, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(44, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(Math.Max(4, size * 0.08)),
        };

        var src = DeviceImageService.LoadImageSource(device, size);
        if (src != null)
        {
            border.Child = new Image
            {
                Source = src,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            border.Child = new TextBlock
            {
                Text = FallbackGlyph(device.Kind),
                FontFamily = IconFont,
                FontSize = Math.Max(16, size * 0.42),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        return border;
    }

    /// <summary>无嵌套灰框的 Hero 设备外观：优先产品位图，否则字形回退。</summary>
    public static Control CreateHeroDeviceVisual(PeripheralDevice device, double maxVisualSize = 168)
    {
        var src = DeviceImageService.LoadImageSource(device, maxVisualSize);
        if (src != null)
        {
            return new Image
            {
                Source = src,
                Stretch = Stretch.Uniform,
                MaxHeight = maxVisualSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        return new TextBlock
        {
            Text = FallbackGlyph(device.Kind),
            FontFamily = IconFont,
            FontSize = Math.Max(28, maxVisualSize * 0.34),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static string FallbackGlyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Keyboard => "\uE765",
        DeviceKind.Headset => "\uE95B",
        DeviceKind.Controller => "\uE7FC",
        _ => "\uE962",
    };

    private static IBrush ResolveBrush(string key, IBrush fallback)
    {
        try
        {
            if (Application.Current != null &&
                Application.Current.TryGetResource(key, null, out var value) &&
                value is IBrush brush)
                return brush;
        }
        catch { }
        return fallback;
    }
}
