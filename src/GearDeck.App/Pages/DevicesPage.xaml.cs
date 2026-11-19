using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using GearDeck.App.Controls;
using GearDeck.App.Services;
using GearDeck.Core.Models;

namespace GearDeck.App.Pages;

public sealed partial class DevicesPage : Page
{
    private bool _isCardMode;

    public DevicesPage()
    {
        InitializeComponent();
        _isCardMode = App.Settings.Current.UseCardLayout;
        DeviceHost.IsCardMode = _isCardMode;

        ViewSwitchSlot.Children.Add(UiBuilders.CreateViewModeSwitcher(_isCardMode, isCard =>
        {
            _isCardMode = isCard;
            DeviceHost.IsCardMode = isCard;
            Apply(App.Devices.LastSnapshot());
        }));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isCardMode = App.Settings.Current.UseCardLayout;
        DeviceHost.IsCardMode = _isCardMode;
        App.Devices.DevicesUpdated += OnDevicesUpdated;
        DeviceImageService.ImagesUpdated += OnImagesUpdated;
        Apply(App.Devices.LastSnapshot());
        _ = App.Devices.RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Devices.DevicesUpdated -= OnDevicesUpdated;
        DeviceImageService.ImagesUpdated -= OnImagesUpdated;
    }

    private void OnImagesUpdated()
        => App.UiQueue?.TryEnqueue(() => Apply(App.LastDevices));

    private void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> devices)
    {
        App.UiQueue?.TryEnqueue(() => Apply(devices));
    }

    private void Apply(IReadOnlyList<PeripheralDevice> devices)
    {
        var items = new List<UIElement>();
        if (devices.Count == 0)
        {
            items.Add(BuildEmptyCard());
        }
        else
        {
            foreach (var d in devices)
                items.Add(_isCardMode ? BuildDeviceCard(d) : BuildDeviceRow(d));
        }
        DeviceHost.SetItems(items);
        StatusText.Text = $"共检测到 {devices.Count} 台设备 · 最近更新 {DateTime.Now:HH:mm:ss}";
    }

    private static UIElement BuildEmptyCard()
    {
        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "正在扫描已连接的外设…", FontSize = 16, FontWeight = FontWeights.SemiBold },
                    new TextBlock
                    {
                        Text = "请确认接收器或数据线已插稳，无线鼠标可轻晃唤醒后点击上方刷新。",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    },
                },
            },
        };
    }

    private UIElement BuildDeviceCard(PeripheralDevice d)
    {
        bool isLow = d.BatteryPercent is { } bp && !d.IsSleeping && App.Settings.IsDeviceLowBattery(d.Id, bp);
        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
        };

        var root = new StackPanel { Spacing = 12 };

        // 上半区：图片 + 标题/状态 + 电量（点击打开详情并同步跳转左侧栏）
        var topArea = new Grid { ColumnSpacing = 14 };
        topArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = UiBuilders.CreateDeviceAvatar(d, 64);
        topArea.Children.Add(avatar);

        var meta = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        meta.Children.Add(new TextBlock
        {
            Text = d.Name,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        meta.Children.Add(new TextBlock
        {
            Text = $"{d.Provider} · {d.Transport} · {(d.IsSleeping ? "休眠中" : "在线")}",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        if (!string.IsNullOrWhiteSpace(d.Note))
        {
            meta.Children.Add(new TextBlock
            {
                Text = d.Note,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        Grid.SetColumn(meta, 1);
        topArea.Children.Add(meta);

        var batPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        batPanel.Children.Add(new TextBlock
        {
            Text = d.BatteryPercent is { } p ? $"{p}%" : "--",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = isLow
                ? new SolidColorBrush(Colors.OrangeRed)
                : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        });
        batPanel.Children.Add(new TextBlock
        {
            Text = FormatStateLabel(d),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Grid.SetColumn(batPanel, 2);
        topArea.Children.Add(batPanel);

        topArea.Tapped += (_, _) => MainWindow.Instance?.OpenDevice(d.Id);
        root.Children.Add(topArea);

        // 下半区：内嵌按钮
        var actions = new ResponsiveWrapPanel { Spacing = 8, LineSpacing = 6 };
        var tuneBtn = new Button
        {
            Content = "参数调节",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Padding = new Thickness(12, 5, 12, 5),
        };
        tuneBtn.Click += (_, _) => MainWindow.Instance?.OpenDevice(d.Id);
        actions.Children.Add(tuneBtn);

        var imgBtn = new Button { Content = "换图", Padding = new Thickness(10, 5, 10, 5) };
        imgBtn.Click += async (_, _) =>
        {
            if (await DeviceImagePickerDialog.ShowAsync(this.XamlRoot, d))
                Apply(App.Devices.LastSnapshot());
        };
        actions.Children.Add(imgBtn);

        var pinBtn = new Button { Content = "固定贴片", Padding = new Thickness(10, 5, 10, 5) };
        pinBtn.Click += (_, _) => PinToTaskbar(d);
        actions.Children.Add(pinBtn);

        root.Children.Add(actions);
        card.Child = root;
        return card;
    }

    private UIElement BuildDeviceRow(PeripheralDevice d)
    {
        bool isLow = d.BatteryPercent is { } bp && !d.IsSleeping && App.Settings.IsDeviceLowBattery(d.Id, bp);
        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
        };

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = UiBuilders.CreateDeviceAvatar(d, 42);
        avatar.Tapped += (_, _) => MainWindow.Instance?.OpenDevice(d.Id);
        row.Children.Add(avatar);

        var meta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        meta.Children.Add(new TextBlock { Text = d.Name, FontWeight = FontWeights.SemiBold });
        meta.Children.Add(new TextBlock
        {
            Text = $"{d.Provider} · {d.Transport}" + (string.IsNullOrEmpty(d.Note) ? "" : $" · {d.Note}"),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        meta.Tapped += (_, _) => MainWindow.Instance?.OpenDevice(d.Id);
        Grid.SetColumn(meta, 1);
        row.Children.Add(meta);

        var bat = new TextBlock
        {
            Text = d.BatteryPercent is { } p ? $"{p}%" : "--",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = isLow
                ? new SolidColorBrush(Colors.OrangeRed)
                : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        };
        Grid.SetColumn(bat, 2);
        row.Children.Add(bat);

        var btnBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var tuneBtn = new Button
        {
            Content = "调节",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Padding = new Thickness(10, 4, 10, 4),
        };
        tuneBtn.Click += (_, _) => MainWindow.Instance?.OpenDevice(d.Id);
        var imgBtn = new Button { Content = "换图", Padding = new Thickness(8, 4, 8, 4) };
        imgBtn.Click += async (_, _) =>
        {
            if (await DeviceImagePickerDialog.ShowAsync(this.XamlRoot, d))
                Apply(App.Devices.LastSnapshot());
        };
        btnBar.Children.Add(tuneBtn);
        btnBar.Children.Add(imgBtn);
        Grid.SetColumn(btnBar, 3);
        row.Children.Add(btnBar);

        card.Child = row;
        return card;
    }

    private static string FormatStateLabel(PeripheralDevice d)
    {
        if (d.State == BatteryState.Charging) return "充电中 ⚡";
        if (d.State == BatteryState.FullyCharged) return "已充满";
        return d.IsSleeping ? "无线休眠" : (d.Connected ? "点击进入设置" : "离线");
    }

    private void PinToTaskbar(PeripheralDevice d)
    {
        string groupId = d.Kind switch
        {
            DeviceKind.Keyboard => "builtin:keyboard",
            DeviceKind.Headset => "builtin:headset",
            DeviceKind.Controller => "builtin:controller",
            _ => "builtin:mouse",
        };
        App.Settings.EnsureDefaults();
        var cfg = App.Settings.Current.TaskbarGroups.First(g => g.GroupId == groupId);
        cfg.Visible = true;
        cfg.DefaultDeviceId = d.Id;
        App.Settings.Current.WidgetVisible = true;
        App.Settings.Save();
        App.RefreshWidget();
        StatusText.Text = $"已将「{d.Name}」固定到任务栏对应分组";
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        _ = App.Devices.RefreshAsync().ContinueWith(_ =>
            App.UiQueue?.TryEnqueue(() => RefreshButton.IsEnabled = true));
    }
}
