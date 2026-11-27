using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;
using GearDeck.Core.Services;
using GearDeck.Desktop.Controls;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Views;

/// <summary>设备总览：卡片/列表切换、按品类过滤、历史电量胶囊、官方驱动入口。</summary>
public partial class DevicesView : UserControl
{
    private bool _isCardMode;
    private DeviceKind? _kindFilter;
    private bool _refreshing;
    private bool _imageBusy;

    public DevicesView()
    {
        InitializeComponent();
        _isCardMode = App.Settings.Current.UseCardLayout;
        ViewSwitchSlot.Children.Add(UiBuilders.CreateViewModeSwitcher(_isCardMode, OnViewModeChanged));
    }

    /// <summary>总览传 null；鼠标/键盘/耳机/手柄传对应 DeviceKind。</summary>
    public void SetKindFilter(DeviceKind? kind)
    {
        _kindFilter = kind;
        Apply(App.LastDevices);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        App.Devices.DevicesUpdated += OnDevicesUpdated;
        DeviceImageService.ImagesUpdated += OnImagesUpdated;
        Apply(App.LastDevices);
        App.RequestRefresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        App.Devices.DevicesUpdated -= OnDevicesUpdated;
        DeviceImageService.ImagesUpdated -= OnImagesUpdated;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModeChanged(bool isCard)
    {
        _isCardMode = isCard;
        Apply(App.LastDevices);
    }

    private void OnImagesUpdated() => App.UiPost(() => Apply(App.LastDevices));

    private void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> devices)
        => App.UiPost(() => Apply(devices));

    private void Apply(IReadOnlyList<PeripheralDevice> all)
    {
        var devices = _kindFilter is { } kind
            ? all.Where(d => d.Kind == kind).ToList()
            : all.ToList();

        Panel host = _isCardMode ? new WrapPanel() : new StackPanel { Spacing = 10 };
        if (devices.Count == 0)
        {
            host.Children.Add(BuildEmptyCard());
        }
        else
        {
            foreach (var device in devices)
                host.Children.Add(_isCardMode ? BuildDeviceCard(device) : BuildDeviceRow(device));
        }

        DeviceHost.Children.Clear();
        DeviceHost.Children.Add(host);

        if (ExternalConfiguratorGuard.IsBackgroundYieldActive(out string? yieldReason))
            StatusText.Text = $"共 {devices.Count} 台 · {yieldReason}（点击上方按钮可立即刷新收回控制）";
        else
            StatusText.Text = $"共检测到 {devices.Count} 台设备 · 最近更新 {DateTime.Now:HH:mm:ss}";

        UpdateMacPermissionBanner();
    }

    private void UpdateMacPermissionBanner()
    {
        if (!OperatingSystem.IsMacOS())
        {
            MacPermissionBanner.IsVisible = false;
            return;
        }
        var access = MacIOKitHid.CheckInputMonitoringAccess();
        bool show = access != MacHidAccessState.Granted || MacIOKitHid.HasPermissionDeniedDevices;
        MacPermissionBanner.IsVisible = show;
        if (!show) return;

        var blocked = MacIOKitHid.BlockedDevices;
        MacPermissionText.Text = blocked.Count > 0
            ? $"当前设备受限：{string.Join("、", blocked)}。授权 GearDeck 访问 HID 后请重新打开应用以读取电量与配置。"
            : "授权 macOS 输入监控后，才能读取与配置无线键盘（如 LEOBOG HI98）等 HID 外设。授权后建议重启 GearDeck。";
    }

    private void OnRequestMacPermissionClick(object? sender, RoutedEventArgs e)
    {
        MacIOKitHid.RequestInputMonitoringAccess();
        UpdateMacPermissionBanner();
        App.RequestRefresh();
    }

    private void OnOpenMacPermissionSettingsClick(object? sender, RoutedEventArgs e)
        => MacIOKitHid.OpenInputMonitoringSettings();

    private static Control BuildEmptyCard() => new Border
    {
        Background = ResolveBrush("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(20),
        Child = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "正在扫描已连接的外设…", FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "请确认接收器或数据线已插稳，无线鼠标可轻晃唤醒后点击上方刷新。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
                },
            },
        },
    };

    private Control BuildDeviceCard(PeripheralDevice d)
    {
        var card = new Border
        {
            Width = 310,
            Margin = new Thickness(0, 0, 12, 12),
            Background = ResolveBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
        };

        var root = new StackPanel { Spacing = 12 };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        top.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        top.Children.Add(UiBuilders.CreateDeviceAvatar(d, 64));

        var meta = new StackPanel { Spacing = 3, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        meta.Children.Add(new TextBlock
        {
            Text = d.Name,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        meta.Children.Add(new TextBlock
        {
            Text = $"{d.Provider} · {d.Transport} · {(d.IsSleeping ? "休眠中" : "在线")}",
            FontSize = 12,
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
        });
        if (!string.IsNullOrWhiteSpace(d.Note))
        {
            meta.Children.Add(new TextBlock
            {
                Text = d.Note,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
            });
        }
        Grid.SetColumn(meta, 1);
        top.Children.Add(meta);

        var capsule = BuildBatteryCapsule(d);
        Grid.SetColumn(capsule, 2);
        top.Children.Add(capsule);
        top.Tapped += (_, _) => MainWindow.Instance?.OpenDevice(d.Id);
        root.Children.Add(top);

        var primary = new WrapPanel();
        var tuneBtn = new Button
        {
            Content = "参数调节",
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 8),
        };
        tuneBtn.Click += (_, _) => MainWindow.Instance?.OpenDevice(d.Id);
        primary.Children.Add(tuneBtn);
        AddDriverButtons(primary, d, compact: false);

        var secondary = new WrapPanel();
        var imageBtn = new Button
        {
            Content = "换图",
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 8),
            Tag = d,
        };
        imageBtn.Click += OnImageClick;
        secondary.Children.Add(imageBtn);

        var pinBtn = new Button
        {
            Content = OperatingSystem.IsMacOS() ? "固定到菜单栏" : "固定贴片",
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 8),
        };
        pinBtn.Click += (_, _) => PinToTaskbar(d);
        secondary.Children.Add(pinBtn);

        root.Children.Add(primary);
        root.Children.Add(secondary);
        card.Child = root;
        return card;
    }

    private Control BuildDeviceRow(PeripheralDevice d)
    {
        var card = new Border
        {
            Margin = new Thickness(0, 0, 0, 0),
            Background = ResolveBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var avatar = UiBuilders.CreateDeviceAvatar(d, 42);
        avatar.Tapped += (_, _) => MainWindow.Instance?.OpenDevice(d.Id);
        row.Children.Add(avatar);

        var meta = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        meta.Children.Add(new TextBlock { Text = d.Name, FontWeight = FontWeight.SemiBold });
        meta.Children.Add(new TextBlock
        {
            Text = $"{d.Provider} · {d.Transport}" + (string.IsNullOrWhiteSpace(d.Note) ? "" : $" · {d.Note}"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
        });
        meta.Tapped += (_, _) => MainWindow.Instance?.OpenDevice(d.Id);
        Grid.SetColumn(meta, 1);
        row.Children.Add(meta);

        var capsule = BuildBatteryCapsule(d);
        Grid.SetColumn(capsule, 2);
        row.Children.Add(capsule);

        var actions = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var primary = new WrapPanel { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var tuneBtn = new Button
        {
            Content = "调节",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 6),
        };
        tuneBtn.Click += (_, _) => MainWindow.Instance?.OpenDevice(d.Id);
        primary.Children.Add(tuneBtn);
        AddDriverButtons(primary, d, compact: true);

        var secondary = new WrapPanel { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var imageBtn = new Button
        {
            Content = "换图",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Tag = d,
        };
        imageBtn.Click += OnImageClick;
        secondary.Children.Add(imageBtn);
        var pinBtn = new Button
        {
            Content = OperatingSystem.IsMacOS() ? "固定到菜单栏" : "固定贴片",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 6, 6),
        };
        pinBtn.Click += (_, _) => PinToTaskbar(d);
        secondary.Children.Add(pinBtn);

        actions.Children.Add(primary);
        actions.Children.Add(secondary);
        Grid.SetColumn(actions, 3);
        row.Children.Add(actions);

        card.Child = row;
        return card;
    }

    private static void AddDriverButtons(Panel panel, PeripheralDevice d, bool compact)
    {
        var links = VendorDriverCatalog.Resolve(d);
        if (links.Local is { } local)
        {
            var btn = new Button
            {
                Content = compact ? "官方软件" : local.DisplayName.Replace("打开 ", ""),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 8),
            };
            ToolTip.SetTip(btn, $"{local.DisplayName}（启动后自动暂停 GearDeck 后台轮询 180 秒）");
            btn.Click += (_, _) => { try { _ = VendorDriverLauncher.LaunchLocal(local); } catch { } };
            panel.Children.Add(btn);
        }
        if (links.Web is { } web)
        {
            var btn = new Button
            {
                Content = "网页驱动",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 8),
            };
            ToolTip.SetTip(btn, $"{web.DisplayName}: {web.Url}（启动后自动暂停后台轮询避免 WebHID 抢占）");
            btn.Click += (_, _) => { try { _ = VendorDriverLauncher.LaunchWeb(web); } catch { } };
            panel.Children.Add(btn);
        }
    }

    /// <summary>历史电量胶囊：仅使用真实观测过的历史值，绝不虚构电量。</summary>
    private static Control BuildBatteryCapsule(PeripheralDevice d)
    {
        var chip = BatteryHistoryHelper.BuildChipText(d, string.Empty, true, App.Settings.GetLastKnownBattery(d.Id));
        string text = chip.UsedHistoricalFallback ? chip.Text + "（历史）" : chip.Text;
        bool isLow = chip.EffectivePercent is { } p && !d.IsSleeping && App.Settings.IsDeviceLowBattery(d.Id, p);

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = ResolveBrush("SubtleFillColorSecondaryBrush"),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = isLow ? new SolidColorBrush(Colors.OrangeRed) : ResolveBrush("TextFillColorPrimaryBrush"),
            },
        };
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
        StatusText.Text = OperatingSystem.IsMacOS()
            ? $"已将「{d.Name}」固定到菜单栏对应分组"
            : $"已将「{d.Name}」固定到任务栏对应分组";
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            ExternalConfiguratorGuard.ResumeNow();
            _ = App.Devices.RefreshAsync(bypassCoexistenceGuard: true).ContinueWith(
                _ => App.UiPost(() =>
                {
                    _refreshing = false;
                    RefreshButton.IsEnabled = true;
                }));
        }
        catch
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private async void OnImageClick(object? sender, RoutedEventArgs e)
    {
        if (_imageBusy || sender is not Button button || button.Tag is not PeripheralDevice device)
            return;
        _imageBusy = true;
        button.IsEnabled = false;
        try
        {
            if (TopLevel.GetTopLevel(this) is not Window owner)
                return;
            if (await DeviceImagePickerDialog.ShowAsync(owner, device))
                Apply(App.LastDevices);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"换图失败：{ex.Message}";
        }
        finally
        {
            _imageBusy = false;
            button.IsEnabled = true;
        }
    }

    private static IBrush ResolveBrush(string key)
    {
        try
        {
            if (Application.Current != null &&
                Application.Current.TryGetResource(key, null, out var value) &&
                value is IBrush brush)
                return brush;
        }
        catch { }
        return new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));
    }
}
