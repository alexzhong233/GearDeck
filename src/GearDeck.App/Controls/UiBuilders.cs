using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using GearDeck.App.Services;
using GearDeck.Core.Models;

namespace GearDeck.App.Controls;

public static class UiBuilders
{
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
                    new FontIcon { Glyph = "\uF0E2", FontSize = 13 },
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
                    new FontIcon { Glyph = "\uE8FD", FontSize = 13 },
                    new TextBlock { Text = "列表", FontSize = 12 },
                },
            },
            Padding = new Thickness(10, 5, 10, 5),
        };

        void SyncStyles(bool cardSelected)
        {
            var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
            var normal = (Style)Application.Current.Resources["DefaultButtonStyle"];
            cardBtn.Style = cardSelected ? accent : normal;
            listBtn.Style = cardSelected ? normal : accent;
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
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(26, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(44, 128, 128, 128)),
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
            border.Child = new FontIcon
            {
                Glyph = FallbackGlyph(device.Kind),
                FontSize = Math.Max(16, size * 0.42),
            };
        }
        return border;
    }

    /// <summary>生成左侧详情 Hero 区的无嵌套纯设备外观视图（仅图标或透明产品图，无额外嵌套灰框）。</summary>
    public static FrameworkElement CreateHeroDeviceVisual(PeripheralDevice device, double maxVisualSize = 168)
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

        return new FontIcon
        {
            Glyph = FallbackGlyph(device.Kind),
            FontSize = Math.Max(28, maxVisualSize * 0.34),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static string FallbackGlyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Keyboard => "\uE765",
        DeviceKind.Headset => "\uE7F5",
        DeviceKind.Controller => "\uE7FC",
        _ => "\uE962",
    };
}
