using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using GearDeck.Core.Models;
using GearDeck.Desktop.Controls;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Views;

/// <summary>
/// 任务栏贴片设置页：紧凑配置、分组/监控卡片与列表视图切换、自定义组创建。
/// </summary>
public partial class WidgetView : UserControl
{
    private static readonly FontFamily IconFont = Controls.UiBuilders.IconFont;

    private static IBrush ResolveBrush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var r) == true && r is IBrush b
            ? b
            : fallback;

    private bool _binding = true;
    private bool _pickingIcon;
    private bool _isCardMode;
    private UniformGrid? _groupGrid;

    public WidgetView()
    {
        InitializeComponent();
        _isCardMode = App.Settings.Current.UseCardLayout;
        ViewSwitchSlot.Children.Add(UiBuilders.CreateViewModeSwitcher(_isCardMode, OnViewModeChanged));
        GroupHost.SizeChanged += (_, _) => UpdateGroupColumns();

        WireEvents();
        LoadSettings();

        bool isWindows = OperatingSystem.IsWindows();
        WindowsWidgetPanel.IsVisible = isWindows;
        MacStatusPanel.IsVisible = !isWindows;
        if (!isWindows)
        {
            PageTitleText.Text = "菜单栏状态";
            PageSubtitleText.Text = "配置 macOS 菜单栏显示、分组与系统监控";
        }
        UpdateMacPreview();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _binding = false;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        App.WidgetPositionChanged += OnExternalPositionChanged;
        if (App.Devices != null)
            App.Devices.DevicesUpdated += OnDevicesUpdated;
        LoadSettings();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        App.WidgetPositionChanged -= OnExternalPositionChanged;
        if (App.Devices != null)
            App.Devices.DevicesUpdated -= OnDevicesUpdated;
    }

    private void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> _)
        => App.UiPost(() =>
        {
            RebuildDeviceChecks();
            UpdateMacPreview();
        });

    private void OnViewModeChanged(bool isCard)
    {
        _isCardMode = isCard;
        RebuildGroups();
    }

    private void OnExternalPositionChanged()
        => App.UiPost(() =>
        {
            bool previous = _binding;
            _binding = true;
            try { SyncPositionControls(); }
            finally { _binding = previous; }
        });

    private void WireEvents()
    {
        VisibleSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            App.Settings.Current.WidgetVisible = VisibleSwitch.IsChecked == true;
            SaveAndRefresh();
        };
        EmbedSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            App.Settings.Current.WidgetEmbedded = EmbedSwitch.IsChecked == true;
            SaveAndRefresh();
        };
        FlyoutHoverSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            App.Settings.Current.WidgetFlyoutOnHover = FlyoutHoverSwitch.IsChecked == true;
            SaveAndRefresh();
        };
        OffsetSlider.ValueChanged += OnOffsetSliderChanged;
        OffsetBox.ValueChanged += (_, _) =>
        {
            if (_binding || double.IsNaN(OffsetBox.Value)) return;
            int val = (int)Math.Round(OffsetBox.Value);
            bool previous = _binding;
            _binding = true;
            try { OffsetSlider.Value = Math.Clamp(val, -1200, 1200); }
            finally { _binding = previous; }
            App.Settings.Current.WidgetOffsetX = val;
            SaveAndRefresh();
        };
        HeightBox.ValueChanged += (_, _) => OnSizeChanged();
        FontBox.ValueChanged += (_, _) => OnSizeChanged();
        BlockBox.ValueChanged += (_, _) => OnSizeChanged();

        PresetLeftWeatherBtn.Click += (_, _) => ApplyPreset("left", 0);
        PresetMidLeftBtn.Click += (_, _) => ApplyPreset("left", 240);
        PresetMidRightBtn.Click += (_, _) => ApplyPreset("right", -240);
        PresetRightTrayBtn.Click += (_, _) => ApplyPreset("right", 0);
        ResetOffsetBtn.Click += (_, _) =>
        {
            bool previous = _binding;
            _binding = true;
            try
            {
                App.Settings.Current.WidgetOffsetX = 0;
                SyncPositionControls();
            }
            finally { _binding = previous; }
            SaveAndRefresh();
        };

        CreateGroupBtn.Click += OnCreateGroup;

        MacTiledSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            App.Settings.Current.MacStatusBarTiled = MacTiledSwitch.IsChecked == true;
            SaveAndRefresh();
            UpdateMacPreview();
        };
        MacMetricsSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            App.Settings.Current.MacStatusBarShowMetrics = MacMetricsSwitch.IsChecked == true;
            SaveAndRefresh();
            UpdateMacPreview();
        };
        MacOpenMonitorBtn.Click += (_, _) => MainWindow.Instance?.ShowMonitorFlyout();
        MacGoMonitorPageBtn.Click += (_, _) => MainWindow.Instance?.OpenMonitorPage();
    }

    private void LoadSettings()
    {
        bool previous = _binding;
        _binding = true;
        try
        {
            App.Settings.EnsureDefaults();
            var s = App.Settings.Current;
            VisibleSwitch.IsChecked = s.WidgetVisible;
            EmbedSwitch.IsChecked = s.WidgetEmbedded;
            FlyoutHoverSwitch.IsChecked = s.WidgetFlyoutOnHover;
            MacTiledSwitch.IsChecked = s.MacStatusBarTiled;
            MacMetricsSwitch.IsChecked = s.MacStatusBarShowMetrics;
            OffsetSlider.Value = Math.Clamp(s.WidgetOffsetX, -1200, 1200);
            OffsetBox.Value = s.WidgetOffsetX;
            HeightBox.Value = s.WidgetHeight;
            FontBox.Value = s.WidgetFontSize;
            BlockBox.Value = s.WidgetBlockWidth;
            RebuildGroups();
        }
        finally { _binding = previous; }
    }

    private void SyncPositionControls()
    {
        int offset = App.Settings.Current.WidgetOffsetX;
        OffsetSlider.Value = Math.Clamp(offset, -1200, 1200);
        OffsetBox.Value = offset;
    }

    private void OnOffsetSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding) return;
        int val = (int)Math.Round(e.NewValue);
        bool previous = _binding;
        _binding = true;
        try { OffsetBox.Value = val; }
        finally { _binding = previous; }
        App.Settings.Current.WidgetOffsetX = val;
        SaveAndRefresh();
    }

    private void OnSizeChanged()
    {
        if (_binding) return;
        if (double.IsNaN(HeightBox.Value) || double.IsNaN(FontBox.Value) || double.IsNaN(BlockBox.Value)) return;
        App.Settings.Current.WidgetHeight = (int)Math.Round(HeightBox.Value);
        App.Settings.Current.WidgetFontSize = (int)Math.Round(FontBox.Value);
        App.Settings.Current.WidgetBlockWidth = (int)Math.Round(BlockBox.Value);
        SaveAndRefresh();
    }

    private void ApplyPreset(string align, int offset)
    {
        bool previous = _binding;
        _binding = true;
        try
        {
            App.Settings.Current.WidgetEmbedded = true;
            EmbedSwitch.IsChecked = true;
            App.Settings.Current.WidgetAlignment = align;
            App.Settings.Current.WidgetOffsetX = offset;
            SyncPositionControls();
        }
        finally { _binding = previous; }
        SaveAndRefresh();
        MainWindow.ShowToast($"已应用位置预设：{(align == "left" ? "靠左" : "靠右")}（偏移 {offset}）", InfoBarSeverity.Success);
    }

    private int ComputeGroupColumns()
    {
        double w = GroupHost.Bounds.Width;
        if (w <= 1) return 3;
        return w >= 920 ? 3 : (w >= 580 ? 2 : 1);
    }

    private void UpdateGroupColumns()
    {
        if (_groupGrid == null) return;
        int cols = ComputeGroupColumns();
        if (_groupGrid.Columns != cols)
            _groupGrid.Columns = cols;
    }

    private void RebuildGroups()
    {
        bool previous = _binding;
        _binding = true;
        try
        {
            App.Settings.EnsureDefaults();
            Panel container;
            if (_isCardMode)
            {
                _groupGrid = new UniformGrid
                {
                    Columns = ComputeGroupColumns(),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                container = _groupGrid;
            }
            else
            {
                _groupGrid = null;
                container = new StackPanel { Spacing = 8 };
            }

            var groups = App.Settings.Current.TaskbarGroups;
            for (int i = 0; i < groups.Count; i++)
                container.Children.Add(_isCardMode
                    ? BuildGroupCard(groups[i], i, groups.Count)
                    : BuildGroupRow(groups[i], i, groups.Count));

            if (OperatingSystem.IsWindows())
                container.Children.Add(_isCardMode ? BuildMonitorCard() : BuildMonitorRow());

            GroupHost.Children.Clear();
            GroupHost.Children.Add(container);

            RebuildDeviceChecks();
            UpdateMacPreview();
        }
        finally { _binding = previous; }
    }

    private void RebuildDeviceChecks()
    {
        var checkedIds = DeviceCheckHost.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => c.Tag as string)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        DeviceCheckHost.Children.Clear();
        if (App.LastDevices.Count == 0)
        {
            DeviceCheckHost.Children.Add(new TextBlock
            {
                Text = "暂未检测到可用外设",
                FontSize = 12,
                Foreground = ResolveBrush("TextFillColorSecondaryBrush", Brushes.Gray),
            });
            return;
        }

        foreach (var d in App.LastDevices)
        {
            DeviceCheckHost.Children.Add(new CheckBox
            {
                Content = d.Name,
                Tag = d.Id,
                IsChecked = checkedIds.Contains(d.Id),
                MinWidth = 0,
                Margin = new Thickness(0, 0, 14, 4),
            });
        }
    }

    private Button CreateBadge(TaskbarGroupConfig cfg, List<PeripheralDevice> members)
    {
        object badgeContent = GroupIconCatalog.ResolveValidIconPath(cfg) is { } iconPath
            && DeviceImageService.CreateSourceFromFile(iconPath, 44) is { } src
            ? new Image
            {
                Source = src,
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
            }
            : new TextBlock
            {
                Text = GroupIconCatalog.ResolveEffectiveGlyph(cfg, members),
                FontFamily = IconFont,
                FontSize = 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

        var badgeBtn = new Button
        {
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(26, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(44, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = badgeContent,
        };
        ToolTip.SetTip(badgeBtn, "点击更换图标");

        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };

        var grid = new UniformGrid { Columns = 2 };
        foreach (var preset in GroupIconCatalog.Presets)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(new TextBlock
            {
                Text = preset.Glyph,
                FontFamily = IconFont,
                FontSize = 14,
                Width = 18,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = preset.Label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var itemBtn = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 5),
                Margin = new Thickness(0, 0, 4, 4),
            };
            itemBtn.Click += (_, _) =>
            {
                cfg.CustomGlyph = preset.Glyph;
                cfg.CustomIconPath = null;
                flyout.Hide();
                SaveAndRefresh();
                RebuildGroups();
            };
            grid.Children.Add(itemBtn);
        }

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        var uploadBtn = new Button
        {
            Content = "上传图片",
            Tag = cfg,
            Height = 28,
            FontSize = 12,
            Padding = new Thickness(10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        uploadBtn.Click += (s, args) =>
        {
            flyout.Hide();
            OnPickIcon(s, args);
        };
        footer.Children.Add(uploadBtn);

        if (!string.IsNullOrWhiteSpace(cfg.CustomGlyph) || !string.IsNullOrWhiteSpace(cfg.CustomIconPath))
        {
            var resetBtn = new Button
            {
                Content = "恢复默认",
                Height = 28,
                FontSize = 12,
                Padding = new Thickness(10, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            resetBtn.Click += (_, _) =>
            {
                cfg.CustomGlyph = null;
                cfg.CustomIconPath = null;
                flyout.Hide();
                SaveAndRefresh();
                RebuildGroups();
            };
            footer.Children.Add(resetBtn);
        }

        flyout.Content = new StackPanel
        {
            Width = 248,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "选择分组图标", FontSize = 12, FontWeight = FontWeight.SemiBold },
                grid,
                footer,
            },
        };
        FlyoutBase.SetAttachedFlyout(badgeBtn, flyout);
        badgeBtn.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(badgeBtn);
        return badgeBtn;
    }

    private ComboBox CreateGroupDeviceCombo(TaskbarGroupConfig cfg, List<PeripheralDevice> members)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 30,
        };
        ToolTip.SetTip(combo, "选择该组在任务栏显示的设备");

        combo.Items.Add(new ComboBoxItem { Content = "自动（优先在线设备）", Tag = null });

        bool matchedDefault = false;
        foreach (var m in members)
        {
            string label = m.BatteryPercent is { } bp
                ? $"{m.Name} ({bp}%)"
                : (m.IsSleeping ? $"{m.Name} (休眠)" : m.Name);
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = m.Id });
            if (!string.IsNullOrWhiteSpace(cfg.DefaultDeviceId) &&
                string.Equals(cfg.DefaultDeviceId, m.Id, StringComparison.OrdinalIgnoreCase))
            {
                matchedDefault = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(cfg.DefaultDeviceId) && !matchedDefault)
            combo.Items.Add(new ComboBoxItem { Content = "已固定设备（当前离线）", Tag = cfg.DefaultDeviceId });

        int selected = 0;
        if (!string.IsNullOrWhiteSpace(cfg.DefaultDeviceId))
        {
            for (int i = 1; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem ci &&
                    string.Equals(ci.Tag as string, cfg.DefaultDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    selected = i;
                    break;
                }
            }
        }

        combo.SelectedIndex = selected;
        combo.SelectionChanged += (_, _) =>
        {
            if (_binding || combo.SelectedItem is not ComboBoxItem item) return;
            cfg.DefaultDeviceId = item.Tag as string;
            SaveAndRefresh();
            UpdateMacPreview();
        };
        return combo;
    }

    private StackPanel CreateOrderButtons(int index, int total)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var up = new Button
        {
            Content = "↑",
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = index > 0,
        };
        ToolTip.SetTip(up, "左移");
        up.Click += (_, _) => MoveGroup(index, -1);
        var down = new Button
        {
            Content = "↓",
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = index < total - 1,
        };
        ToolTip.SetTip(down, "右移");
        down.Click += (_, _) => MoveGroup(index, 1);
        panel.Children.Add(up);
        panel.Children.Add(down);
        return panel;
    }

    private StackPanel CreateGroupActionButtons(TaskbarGroupConfig cfg)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var delete = new Button
        {
            Content = "删除",
            Height = 28,
            FontSize = 12,
            Padding = new Thickness(9, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        delete.Click += (_, _) =>
        {
            if (cfg.GroupId.StartsWith("builtin:", StringComparison.Ordinal))
            {
                App.Settings.Current.RemovedBuiltinGroups ??= [];
                if (!App.Settings.Current.RemovedBuiltinGroups.Any(x => string.Equals(x, cfg.GroupId, StringComparison.OrdinalIgnoreCase)))
                    App.Settings.Current.RemovedBuiltinGroups.Add(cfg.GroupId);
            }
            else if (cfg.GroupId.StartsWith("custom:", StringComparison.Ordinal))
            {
                App.Settings.Current.CustomGroups.RemoveAll(g => g.Id == cfg.GroupId);
            }
            App.Settings.Current.TaskbarGroups.RemoveAll(g => g.GroupId == cfg.GroupId);
            App.Settings.Save();
            App.RefreshWidget();
            RebuildGroups();
        };
        panel.Children.Add(delete);

        return panel;
    }

    private Control BuildGroupCard(TaskbarGroupConfig cfg, int index, int total)
    {
        var members = TaskbarGroupHelper.GetMembers(cfg.GroupId, App.LastDevices);
        var border = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 12, 12),
            Background = ResolveBrush("CardBackgroundFillColorDefaultBrush", Brushes.Transparent),
            BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
        };
        var stack = new StackPanel { Spacing = 10 };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        top.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        top.Children.Add(CreateBadge(cfg, members));

        var visible = new CheckBox
        {
            Content = LabelOf(cfg),
            IsChecked = cfg.Visible,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 0, 0, 0),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        visible.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            cfg.Visible = visible.IsChecked == true;
            SaveAndRefresh();
            UpdateMacPreview();
        };
        Grid.SetColumn(visible, 1);
        top.Children.Add(visible);

        var orderPanel = CreateOrderButtons(index, total);
        Grid.SetColumn(orderPanel, 2);
        top.Children.Add(orderPanel);
        stack.Children.Add(top);

        stack.Children.Add(CreateGroupDeviceCombo(cfg, members));
        stack.Children.Add(CreateGroupActionButtons(cfg));

        border.Child = stack;
        return border;
    }

    private Control BuildGroupRow(TaskbarGroupConfig cfg, int index, int total)
    {
        var members = TaskbarGroupHelper.GetMembers(cfg.GroupId, App.LastDevices);
        var border = new Border
        {
            Background = ResolveBrush("CardBackgroundFillColorDefaultBrush", Brushes.Transparent),
            BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10),
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        row.Children.Add(CreateBadge(cfg, members));

        var visible = new CheckBox
        {
            Content = LabelOf(cfg),
            IsChecked = cfg.Visible,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(12, 0, 12, 0),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        visible.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            cfg.Visible = visible.IsChecked == true;
            SaveAndRefresh();
            UpdateMacPreview();
        };
        Grid.SetColumn(visible, 1);
        row.Children.Add(visible);

        var deviceCombo = CreateGroupDeviceCombo(cfg, members);
        deviceCombo.Width = 176;
        deviceCombo.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(deviceCombo, 2);
        row.Children.Add(deviceCombo);

        var actions = CreateGroupActionButtons(cfg);
        actions.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(actions, 3);
        row.Children.Add(actions);

        var orderPanel = CreateOrderButtons(index, total);
        Grid.SetColumn(orderPanel, 4);
        row.Children.Add(orderPanel);

        border.Child = row;
        return border;
    }

    private Control BuildMonitorCard()
    {
        var border = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 12, 12),
            Background = ResolveBrush("CardBackgroundFillColorDefaultBrush", Brushes.Transparent),
            BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
        };
        var stack = new StackPanel { Spacing = 10 };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        top.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        top.Children.Add(CreateMonitorBadge());

        var toggle = CreateMonitorToggle();
        toggle.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(toggle, 1);
        top.Children.Add(toggle);

        var openBtn = CreateOpenMonitorButton();
        Grid.SetColumn(openBtn, 2);
        top.Children.Add(openBtn);
        stack.Children.Add(top);

        var metricStack = new StackPanel { Spacing = 6 };
        var topCombo = CreateMetricCombo(App.Settings.Current.MonitorTopMetric, allowNone: false, isTop: true);
        topCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        metricStack.Children.Add(topCombo);

        var botCombo = CreateMetricCombo(App.Settings.Current.MonitorBottomMetric, allowNone: true, isTop: false);
        botCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        metricStack.Children.Add(botCombo);

        stack.Children.Add(metricStack);

        border.Child = stack;
        return border;
    }

    private Control BuildMonitorRow()
    {
        var border = new Border
        {
            Background = ResolveBrush("CardBackgroundFillColorDefaultBrush", Brushes.Transparent),
            BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10),
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        row.Children.Add(CreateMonitorBadge());

        var toggle = CreateMonitorToggle();
        toggle.Margin = new Thickness(12, 0, 12, 0);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);

        var topCombo = CreateMetricCombo(App.Settings.Current.MonitorTopMetric, allowNone: false, isTop: true);
        topCombo.Width = 170;
        topCombo.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(topCombo, 2);
        row.Children.Add(topCombo);

        var botCombo = CreateMetricCombo(App.Settings.Current.MonitorBottomMetric, allowNone: true, isTop: false);
        botCombo.Width = 170;
        botCombo.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(botCombo, 3);
        row.Children.Add(botCombo);

        var openBtn = CreateOpenMonitorButton();
        Grid.SetColumn(openBtn, 4);
        row.Children.Add(openBtn);

        border.Child = row;
        return border;
    }

    private static Border CreateMonitorBadge() => new()
    {
        Width = 34,
        Height = 34,
        CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Color.FromArgb(26, 128, 128, 128)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(44, 128, 128, 128)),
        BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = "\uE950",
            FontFamily = IconFont,
            FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private CheckBox CreateMonitorToggle()
    {
        var chk = new CheckBox
        {
            Content = "系统监控",
            IsChecked = App.Settings.Current.MonitorEnabled,
            FontWeight = FontWeight.SemiBold,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chk.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            if ((chk.IsChecked == true) != App.Settings.Current.MonitorEnabled)
                App.ToggleMonitor();
        };
        return chk;
    }

    private static Button CreateOpenMonitorButton()
    {
        var btn = new Button
        {
            Content = "系统监控 →",
            Height = 28,
            FontSize = 12,
            Padding = new Thickness(9, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        btn.Click += (_, _) => MainWindow.Instance?.OpenMonitorPage();
        return btn;
    }

    private ComboBox CreateMetricCombo(string selectedId, bool allowNone, bool isTop)
    {
        var box = new ComboBox { Height = 30 };
        if (allowNone)
            box.Items.Add(new ComboBoxItem { Content = "不显示（单行）", Tag = "NONE" });
        foreach (var m in DesktopMetricsEngine.AllMetrics)
            box.Items.Add(new ComboBoxItem { Content = $"{m.FullName}（{m.ShortLabel}）", Tag = m.Id });

        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                break;
            }
        }
        if (box.SelectedIndex < 0 && box.Items.Count > 0)
            box.SelectedIndex = 0;

        box.SelectionChanged += (_, _) =>
        {
            if (_binding || box.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
            if (isTop)
                App.Settings.Current.MonitorTopMetric = id;
            else
                App.Settings.Current.MonitorBottomMetric = id;
            App.Settings.Save();
            App.RefreshWidget();
        };
        return box;
    }

    private async void OnPickIcon(object? sender, RoutedEventArgs e)
    {
        if (_pickingIcon || sender is not Button button || button.Tag is not TaskbarGroupConfig cfg) return;
        _pickingIcon = true;
        button.IsEnabled = false;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider == null)
            {
                MainWindow.ShowToast("文件选择器不可用。", InfoBarSeverity.Warning);
                return;
            }
            var picked = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择分组图标",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片") { Patterns = ["*.png", "*.ico", "*.jpg", "*.jpeg", "*.bmp"] },
                ],
            });
            string? path = picked.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            string iconsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GearDeck", "icons");
            Directory.CreateDirectory(iconsDir);
            string safeId = string.Concat(cfg.GroupId.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
            string extension = Path.GetExtension(path);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
            string dest = Path.Combine(iconsDir, $"{safeId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{extension}");
            File.Copy(path, dest, overwrite: true);

            cfg.CustomIconPath = dest;
            cfg.CustomGlyph = null;
            App.Settings.Save();
            App.RefreshWidget();
            RebuildGroups();
            MainWindow.ShowToast($"已为「{LabelOf(cfg)}」更新图标。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            MainWindow.ShowToast($"选择自定义图标失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            button.IsEnabled = true;
            _pickingIcon = false;
        }
    }

    private static string LabelOf(TaskbarGroupConfig cfg) => cfg.GroupId switch
    {
        "builtin:mouse" => "鼠标组",
        "builtin:keyboard" => "键盘组",
        "builtin:headset" => "耳机组",
        "builtin:controller" => "手柄组",
        _ => App.Settings.Current.CustomGroups.FirstOrDefault(c => c.Id == cfg.GroupId)?.DisplayName ?? "自定义组",
    };

    private void MoveGroup(int index, int delta)
    {
        var groups = App.Settings.Current.TaskbarGroups;
        int next = index + delta;
        if (next < 0 || next >= groups.Count) return;
        (groups[index], groups[next]) = (groups[next], groups[index]);
        SaveAndRefresh();
        RebuildGroups();
    }

    private void OnCreateGroup(object? sender, RoutedEventArgs e)
    {
        string name = GroupNameBox.Text?.Trim() ?? "";
        var members = DeviceCheckHost.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => c.Tag as string)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();
        if (name.Length == 0 || members.Count == 0)
        {
            MainWindow.ShowToast("请填写组名并至少勾选一台设备。", InfoBarSeverity.Warning);
            return;
        }

        var group = new DeviceGroup
        {
            Id = "custom:" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = name,
            MemberIds = members,
        };
        App.Settings.Current.CustomGroups.Add(group);
        App.Settings.Current.TaskbarGroups.Add(new TaskbarGroupConfig { GroupId = group.Id, Visible = true });
        GroupNameBox.Text = "";
        foreach (var chk in DeviceCheckHost.Children.OfType<CheckBox>())
            chk.IsChecked = false;
        App.Settings.Save();
        App.RefreshWidget();
        RebuildGroups();
        MainWindow.ShowToast($"已创建自定义组「{name}」。", InfoBarSeverity.Success);
    }

    private void UpdateMacPreview()
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var snap = App.Settings.Current.MacStatusBarShowMetrics ? DesktopMetricsEngine.Sample() : null;
            MacPreviewText.Text = MacStatusBarFormatter.BuildTitle(App.LastDevices ?? [], App.Settings.Current, snap);
        }
        catch { }
    }

    private static void SaveAndRefresh()
    {
        App.Settings.Save();
        App.RefreshWidget();
    }
}
