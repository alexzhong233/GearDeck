using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
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
    private UniformGrid? _cardGrid;

    public DevicesView()
    {
        InitializeComponent();
        _isCardMode = App.Settings.Current.UseCardLayout;
        ViewSwitchSlot.Children.Add(UiBuilders.CreateViewModeSwitcher(_isCardMode, OnViewModeChanged));
        DeviceHost.SizeChanged += (_, _) => UpdateCardColumns();
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

        Panel host;
        if (_isCardMode)
        {
            _cardGrid = new UniformGrid
            {
                Columns = ComputeCardColumns(),
                VerticalAlignment = VerticalAlignment.Top,
            };
            host = _cardGrid;
        }
        else
        {
            _cardGrid = null;
            host = new StackPanel { Spacing = 10 };
        }
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

    private int ComputeCardColumns()
    {
        double w = DeviceHost.Bounds.Width;
        if (w <= 1) return 3;
        return w >= 920 ? 3 : (w >= 580 ? 2 : 1);
    }

    private void UpdateCardColumns()
    {
        if (_cardGrid == null) return;
        int cols = ComputeCardColumns();
        if (_cardGrid.Columns != cols)
            _cardGrid.Columns = cols;
    }

    private Control BuildDeviceCard(PeripheralDevice d)
    {
        var card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 12, 12),
            Background = ResolveBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        var root = new StackPanel { Spacing = 12 };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        top.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        top.Children.Add(UiBuilders.CreateDeviceAvatar(d, 58));

        var meta = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(12, 0, 10, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        meta.Children.Add(new TextBlock
        {
            Text = d.Name,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        meta.Children.Add(new TextBlock
        {
            Text = FormatDeviceStatus(d),
            FontSize = 12,
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
        });
        Grid.SetColumn(meta, 1);
        top.Children.Add(meta);

        var capsule = BuildBatteryCapsule(d);
        Grid.SetColumn(capsule, 2);
        top.Children.Add(capsule);
        root.Children.Add(top);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var primaryActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        AddDriverButtons(primaryActions, d, compact: false);
        Grid.SetColumn(primaryActions, 0);
        footer.Children.Add(primaryActions);

        var utilityTools = CreateUtilityToolPair(d);
        utilityTools.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        utilityTools.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(utilityTools, 1);
        footer.Children.Add(utilityTools);

        root.Children.Add(footer);
        card.Child = root;
        card.Tapped += (_, e) =>
        {
            if (!IsClickInsideButton(e.Source))
                MainWindow.Instance?.OpenDevice(d.Id);
        };
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
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var avatar = UiBuilders.CreateDeviceAvatar(d, 42);
        row.Children.Add(avatar);

        var meta = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        meta.Children.Add(new TextBlock { Text = d.Name, FontWeight = FontWeight.SemiBold });
        meta.Children.Add(new TextBlock
        {
            Text = FormatDeviceStatus(d),
            FontSize = 12,
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
        });
        Grid.SetColumn(meta, 1);
        row.Children.Add(meta);

        var capsule = BuildBatteryCapsule(d);
        if (capsule is Control capCtrl)
            capCtrl.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(capsule, 2);
        row.Children.Add(capsule);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var drivers = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        AddDriverButtons(drivers, d, compact: true);
        actions.Children.Add(drivers);
        actions.Children.Add(CreateUtilityToolPair(d));

        Grid.SetColumn(actions, 3);
        row.Children.Add(actions);

        card.Child = row;
        card.Tapped += (_, e) =>
        {
            if (!IsClickInsideButton(e.Source))
                MainWindow.Instance?.OpenDevice(d.Id);
        };
        return card;
    }

    private StackPanel CreateUtilityToolPair(PeripheralDevice d)
    {
        var pair = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var imageBtn = CreateUtilityIconButton("\uE8B9", "更换设备图");
        imageBtn.Tag = d;
        imageBtn.Click += OnImageClick;
        var pinBtn = CreateUtilityIconButton("\uE718", OperatingSystem.IsMacOS() ? "固定到菜单栏" : "固定到任务栏");
        pinBtn.Click += (_, _) => PinToTaskbar(d);
        pair.Children.Add(imageBtn);
        pair.Children.Add(pinBtn);
        return pair;
    }

    private static Button CreateUtilityIconButton(string glyph, string tooltip)
    {
        var btn = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Background = ResolveBrush("SubtleFillColorSecondaryBrush"),
            BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = UiBuilders.IconFont,
                FontSize = 13,
                Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
            },
        };
        ToolTip.SetTip(btn, tooltip);
        return btn;
    }

    private static string FormatDeviceStatus(PeripheralDevice d)
        => d.IsSleeping ? "接收器已连接 · 休眠" : (d.Connected ? $"{d.Provider} · 在线" : $"{d.Provider} · 离线");

    private static bool IsClickInsideButton(object? source)
    {
        for (Visual? cur = source as Visual; cur != null; cur = cur.GetVisualParent())
        {
            if (cur is Button)
                return true;
        }
        return false;
    }

    private static string ShortLocalDriverLabel(string displayName)
    {
        if (displayName.Contains("雷云", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("Synapse", StringComparison.OrdinalIgnoreCase))
            return "雷云";
        if (displayName.Contains("奥创", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("Armoury", StringComparison.OrdinalIgnoreCase))
            return "奥创";
        if (displayName.Contains("G HUB", StringComparison.OrdinalIgnoreCase))
            return "G HUB";
        if (displayName.Contains("INZONE", StringComparison.OrdinalIgnoreCase))
            return "INZONE";
        if (displayName.Contains("飞智", StringComparison.OrdinalIgnoreCase))
            return "空间站";
        return displayName.Replace("打开 ", "");
    }

    private static void AddDriverButtons(Panel panel, PeripheralDevice d, bool compact)
    {
        var links = VendorDriverCatalog.Resolve(d);
        if (links.Local is { } local)
        {
            var btn = new Button
            {
                Content = ShortLocalDriverLabel(local.DisplayName),
                Height = 28,
                FontSize = 12,
                Padding = new Thickness(9, 0),
                Margin = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
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
                Height = 28,
                FontSize = 12,
                Padding = new Thickness(9, 0),
                Margin = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(btn, $"{web.DisplayName}: {web.Url}（启动后自动暂停后台轮询避免 WebHID 抢占）");
            btn.Click += (_, _) => { try { _ = VendorDriverLauncher.LaunchWeb(web); } catch { } };
            panel.Children.Add(btn);
        }
    }

    /// <summary>电量胶囊：双耳分 L/R 两行展示，不追加多余标注。</summary>
    private static Control BuildBatteryCapsule(PeripheralDevice d)
    {
        var chip = BatteryHistoryHelper.BuildChipText(d, string.Empty, true, App.Settings.GetLastKnownBattery(d.Id));
        bool isLow = chip.EffectivePercent is { } p && !d.IsSleeping && App.Settings.IsDeviceLowBattery(d.Id, p);
        IBrush fg = isLow ? new SolidColorBrush(Colors.OrangeRed) : ResolveBrush("TextFillColorPrimaryBrush");

        Control content;
        if (!string.IsNullOrEmpty(chip.TextTop) && !string.IsNullOrEmpty(chip.TextBottom))
        {
            var stack = new StackPanel { Spacing = 1 };
            stack.Children.Add(new TextBlock
            {
                Text = chip.TextTop,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Right,
                Foreground = fg,
            });
            stack.Children.Add(new TextBlock
            {
                Text = chip.TextBottom,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Right,
                Foreground = fg,
            });
            int? casePct = d.CaseBatteryPercent ?? App.Settings.GetLastKnownBattery(d.Id)?.CaseBatteryPercent;
            if (casePct.HasValue)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"盒 {casePct.Value}%{(d.CaseCharging ? "⚡" : "")}",
                    FontSize = 11,
                    TextAlignment = TextAlignment.Right,
                    Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
                });
            }
            content = stack;
        }
        else
        {
            int? casePct = d.CaseBatteryPercent ?? App.Settings.GetLastKnownBattery(d.Id)?.CaseBatteryPercent;
            if (casePct.HasValue)
            {
                var stack = new StackPanel { Spacing = 1 };
                stack.Children.Add(new TextBlock
                {
                    Text = chip.Text,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    TextAlignment = TextAlignment.Right,
                    Foreground = fg,
                });
                stack.Children.Add(new TextBlock
                {
                    Text = $"备 {casePct.Value}%{(d.CaseCharging ? "⚡" : "")}",
                    FontSize = 11,
                    TextAlignment = TextAlignment.Right,
                    Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
                });
                content = stack;
            }
            else
            {
                content = new TextBlock
                {
                    Text = chip.Text,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = fg,
                };
            }
        }

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = ResolveBrush("SubtleFillColorSecondaryBrush"),
            Child = content,
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
        App.Settings.Current.RemovedBuiltinGroups?.RemoveAll(x => string.Equals(x, groupId, StringComparison.OrdinalIgnoreCase));
        App.Settings.EnsureDefaults();
        var cfg = App.Settings.Current.TaskbarGroups.FirstOrDefault(g => g.GroupId == groupId);
        if (cfg == null)
        {
            cfg = new TaskbarGroupConfig { GroupId = groupId, Visible = true };
            App.Settings.Current.TaskbarGroups.Add(cfg);
        }
        cfg.Visible = true;
        cfg.DefaultDeviceId = d.Id;
        App.Settings.Current.WidgetVisible = true;
        App.Settings.Save();
        App.RefreshWidget();
        string toast = OperatingSystem.IsMacOS()
            ? $"已将「{d.Name}」固定到菜单栏对应分组"
            : $"已将「{d.Name}」固定到任务栏对应分组";
        StatusText.Text = toast;
        MainWindow.ShowToast(toast, FluentAvalonia.UI.Controls.InfoBarSeverity.Success);
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
