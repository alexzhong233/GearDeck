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
        DevicePoliciesHost.IsCardMode = _isCardMode;

        ViewSwitchSlot.Children.Add(UiBuilders.CreateViewModeSwitcher(_isCardMode, isCard =>
        {
            _isCardMode = isCard;
            DeviceHost.IsCardMode = isCard;
            DevicePoliciesHost.IsCardMode = isCard;
            Apply(App.Devices.LastSnapshot());
        }));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isCardMode = App.Settings.Current.UseCardLayout;
        DeviceHost.IsCardMode = _isCardMode;
        DevicePoliciesHost.IsCardMode = _isCardMode;
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
        RebuildDevicePolicies(devices);
        if (GearDeck.Core.Services.ExternalConfiguratorGuard.IsBackgroundYieldActive(out string? yieldReason))
            StatusText.Text = $"共 {devices.Count} 台 · {yieldReason}（点击左侧按钮可立即刷新收回控制）";
        else
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
        string? dualSummary = d.FormatDualEarSummary(compact: true);
        batPanel.Children.Add(new TextBlock
        {
            Text = dualSummary ?? (d.BatteryPercent is { } p ? $"{p}%" : "--"),
            FontSize = dualSummary != null ? 17 : 24,
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

        // 下半区：内嵌按钮（含官方软件与官方网页驱动直达）
        var actions = new ResponsiveWrapPanel { Spacing = 8, LineSpacing = 6 };
        var tuneBtn = new Button
        {
            Content = "参数调节",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Padding = new Thickness(12, 5, 12, 5),
        };
        tuneBtn.Click += (_, _) => MainWindow.Instance?.OpenDevice(d.Id);
        actions.Children.Add(tuneBtn);

        var driverLinks = GearDeck.Core.Services.VendorDriverCatalog.Resolve(d);
        if (driverLinks.Local is { } localDrv)
        {
            var localBtn = new Button { Content = localDrv.DisplayName.Replace("打开 ", ""), Padding = new Thickness(10, 5, 10, 5) };
            ToolTipService.SetToolTip(localBtn, $"{localDrv.DisplayName}（启动后自动暂停 GearDeck 后台轮询 180s）");
            localBtn.Click += (_, _) => _ = Services.VendorDriverLauncher.LaunchLocal(localDrv);
            actions.Children.Add(localBtn);
        }
        if (driverLinks.Web is { } webDrv)
        {
            var webBtn = new Button { Content = "网页驱动", Padding = new Thickness(10, 5, 10, 5) };
            ToolTipService.SetToolTip(webBtn, $"{webDrv.DisplayName}: {webDrv.Url}（启动后自动暂停后台轮询避免 WebHID 抢占）");
            webBtn.Click += (_, _) => _ = Services.VendorDriverLauncher.LaunchWeb(webDrv);
            actions.Children.Add(webBtn);
        }

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
            Text = d.FormatDualEarSummary(compact: true) ?? (d.BatteryPercent is { } p ? $"{p}%" : "--"),
            FontSize = d.LeftEarPercent.HasValue || d.RightEarPercent.HasValue ? 15 : 20,
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
        var driverLinks = GearDeck.Core.Services.VendorDriverCatalog.Resolve(d);
        btnBar.Children.Add(tuneBtn);
        if (driverLinks.Local is { } localDrv)
        {
            var localBtn = new Button { Content = "官方软件", Padding = new Thickness(8, 4, 8, 4) };
            ToolTipService.SetToolTip(localBtn, localDrv.DisplayName);
            localBtn.Click += (_, _) => _ = Services.VendorDriverLauncher.LaunchLocal(localDrv);
            btnBar.Children.Add(localBtn);
        }
        if (driverLinks.Web is { } webDrv)
        {
            var webBtn = new Button { Content = "网页驱动", Padding = new Thickness(8, 4, 8, 4) };
            ToolTipService.SetToolTip(webBtn, $"{webDrv.DisplayName}: {webDrv.Url}");
            webBtn.Click += (_, _) => _ = Services.VendorDriverLauncher.LaunchWeb(webDrv);
            btnBar.Children.Add(webBtn);
        }
        var imgBtn = new Button { Content = "换图", Padding = new Thickness(8, 4, 8, 4) };
        imgBtn.Click += async (_, _) =>
        {
            if (await DeviceImagePickerDialog.ShowAsync(this.XamlRoot, d))
                Apply(App.Devices.LastSnapshot());
        };
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

    private void RebuildDevicePolicies(IReadOnlyList<PeripheralDevice> snapshot)
    {
        var devices = snapshot.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        var ids = devices.Keys
            .Concat(App.Settings.Current.DeviceLowBatteryPolicies.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => devices.TryGetValue(id, out var d) ? d.Name : id)
            .ToList();

        var elements = new List<UIElement>(ids.Count);
        if (ids.Count == 0)
        {
            elements.Add(new TextBlock
            {
                Text = "尚无已扫描到的外设；等待设备接入后可在此按设备配置独立提醒。",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            DevicePoliciesHost.SetItems(elements);
            return;
        }

        foreach (string id in ids)
        {
            devices.TryGetValue(id, out var dev);
            string devName = dev?.Name ?? id;
            var policy = App.Settings.GetLowBatteryPolicy(id);
            int eff = App.Settings.GetEffectiveLowBatteryThreshold(id);

            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
            };

            var stack = new StackPanel { Spacing = 8 };
            var head = new Grid { ColumnSpacing = 10 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (dev != null)
                head.Children.Add(UiBuilders.CreateDeviceAvatar(dev, 36));
            else
                head.Children.Add(new FontIcon { Glyph = "\uE962", FontSize = 18 });

            var title = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = devName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var thresholdSubText = new TextBlock
            {
                Text = policy.CustomThresholdPercent.HasValue ? $"独立阈值：{eff}%" : $"默认阈值：{SettingsService.DefaultLowBatteryThreshold}%",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            title.Children.Add(thresholdSubText);
            Grid.SetColumn(title, 1);
            head.Children.Add(title);
            stack.Children.Add(head);

            var controls = new ResponsiveWrapPanel { Spacing = 10, LineSpacing = 6 };
            var enableChk = new CheckBox { Content = "提醒", IsChecked = policy.Enabled };
            var notifyChk = new CheckBox { Content = "弹窗通知", IsChecked = policy.WindowsNotification };
            var numBox = new NumberBox
            {
                Header = "独立阈值 (%)",
                Maximum = 80,
                Minimum = 5,
                Width = 130,
                Value = eff,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };

            bool bindingRow = true;

            void CommitPolicy(int? customVal)
            {
                App.Settings.Current.DeviceLowBatteryPolicies[id] = new DeviceLowBatteryPolicy
                {
                    Enabled = enableChk.IsChecked == true,
                    CustomThresholdPercent = customVal,
                    WindowsNotification = notifyChk.IsChecked == true,
                };
                App.Settings.Save();
                App.RefreshWidget();
                int nextEff = App.Settings.GetEffectiveLowBatteryThreshold(id);
                thresholdSubText.Text = customVal.HasValue
                    ? $"独立阈值：{nextEff}%"
                    : $"默认阈值：{SettingsService.DefaultLowBatteryThreshold}%";
            }

            enableChk.Checked += (_, _) => CommitPolicy(App.Settings.GetLowBatteryPolicy(id).CustomThresholdPercent);
            enableChk.Unchecked += (_, _) => CommitPolicy(App.Settings.GetLowBatteryPolicy(id).CustomThresholdPercent);
            notifyChk.Checked += (_, _) => CommitPolicy(App.Settings.GetLowBatteryPolicy(id).CustomThresholdPercent);
            notifyChk.Unchecked += (_, _) => CommitPolicy(App.Settings.GetLowBatteryPolicy(id).CustomThresholdPercent);
            numBox.ValueChanged += (_, args) =>
            {
                if (bindingRow || double.IsNaN(args.NewValue)) return;
                CommitPolicy(Math.Clamp((int)Math.Round(args.NewValue), 5, 80));
            };

            var resetBtn = new Button
            {
                Content = $"恢复默认 ({SettingsService.DefaultLowBatteryThreshold}%)",
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            resetBtn.Click += (_, _) =>
            {
                App.Settings.Current.DeviceLowBatteryPolicies.Remove(id);
                App.Settings.Save();
                App.RefreshWidget();
                RebuildDevicePolicies(App.LastDevices);
            };

            bindingRow = false;

            controls.Children.Add(enableChk);
            controls.Children.Add(notifyChk);
            controls.Children.Add(numBox);
            controls.Children.Add(resetBtn);
            stack.Children.Add(controls);

            card.Child = stack;
            elements.Add(card);
        }

        DevicePoliciesHost.SetItems(elements);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        GearDeck.Core.Services.ExternalConfiguratorGuard.ResumeNow();
        RefreshButton.IsEnabled = false;
        _ = App.Devices.RefreshAsync(bypassCoexistenceGuard: true).ContinueWith(_ =>
            App.UiQueue?.TryEnqueue(() => RefreshButton.IsEnabled = true));
    }
}
