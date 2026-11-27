using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using GearDeck.Core.Models;
using GearDeck.Desktop.Controls;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Views;

/// <summary>
/// Avalonia 版任务栏贴片设置页：Windows 配置贴片、分组图标与监控双指标，macOS 仅显示菜单栏状态说明。
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

    public WidgetView()
    {
        InitializeComponent();
        WireEvents();
        LoadSettings();

        bool isWindows = OperatingSystem.IsWindows();
        WindowsWidgetPanel.IsVisible = isWindows;
        MacStatusPanel.IsVisible = !isWindows;
        if (!isWindows)
        {
            PageTitleText.Text = "菜单栏状态";
            PageSubtitleText.Text = "配置 macOS 顶部菜单栏平铺展示、外设分组与图标、以及系统监控摘要。";
        }
        UpdateMacPreview();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _binding = false;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
        => App.WidgetPositionChanged += OnExternalPositionChanged;

    private void OnUnloaded(object? sender, RoutedEventArgs e)
        => App.WidgetPositionChanged -= OnExternalPositionChanged;

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
        AlignCombo.SelectionChanged += (_, _) =>
        {
            if (_binding) return;
            bool previous = _binding;
            _binding = true;
            try
            {
                App.Settings.Current.WidgetAlignment = AlignCombo.SelectedIndex == 0 ? "left" : "right";
                App.Settings.Current.WidgetOffsetX = 0;
                SyncPositionControls();
            }
            finally { _binding = previous; }
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

        MonitorSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            if ((MonitorSwitch.IsChecked == true) != App.Settings.Current.MonitorEnabled)
                App.ToggleMonitor();
            UpdateMonitorStatus();
        };
        TopMetricBox.SelectionChanged += OnTaskbarMetricChanged;
        BottomMetricBox.SelectionChanged += OnTaskbarMetricChanged;
        GoMonitorBtn.Click += (_, _) => MainWindow.Instance?.OpenMonitorPage();
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
            MacTiledSwitch.IsChecked = s.MacStatusBarTiled;
            MacMetricsSwitch.IsChecked = s.MacStatusBarShowMetrics;
            AlignCombo.SelectedIndex = s.WidgetAlignment == "left" ? 0 : 1;
            OffsetSlider.Value = Math.Clamp(s.WidgetOffsetX, -1200, 1200);
            OffsetBox.Value = s.WidgetOffsetX;
            HeightBox.Value = s.WidgetHeight;
            FontBox.Value = s.WidgetFontSize;
            BlockBox.Value = s.WidgetBlockWidth;
            MonitorSwitch.IsChecked = s.MonitorEnabled;
            PopulateMetricCombo(TopMetricBox, s.MonitorTopMetric, allowNone: false);
            PopulateMetricCombo(BottomMetricBox, s.MonitorBottomMetric, allowNone: true);
            RebuildGroups();
            UpdateMonitorStatus();
        }
        finally { _binding = previous; }
    }

    private void SyncPositionControls()
    {
        AlignCombo.SelectedIndex = App.Settings.Current.WidgetAlignment == "left" ? 0 : 1;
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
        HintText.Text = $"已应用预设：{(align == "left" ? "靠左" : "靠右")}锚点，偏移 {offset}。";
    }

    private void RebuildGroups()
    {
        bool previous = _binding;
        _binding = true;
        try
        {
            App.Settings.EnsureDefaults();
            GroupHost.Children.Clear();
            var groups = App.Settings.Current.TaskbarGroups;
            for (int i = 0; i < groups.Count; i++)
                GroupHost.Children.Add(BuildGroupCard(groups[i], i, groups.Count));

            DeviceCheckHost.Children.Clear();
            foreach (var d in App.LastDevices)
                DeviceCheckHost.Children.Add(new CheckBox { Content = d.Name, Tag = d.Id });
            UpdateMacPreview();
        }
        finally { _binding = previous; }
    }

    private Control BuildGroupCard(TaskbarGroupConfig cfg, int index, int total)
    {
        string label = cfg.GroupId switch
        {
            "builtin:mouse" => "鼠标组",
            "builtin:keyboard" => "键盘组",
            "builtin:headset" => "耳机组",
            "builtin:controller" => "手柄组",
            _ => App.Settings.Current.CustomGroups.FirstOrDefault(c => c.Id == cfg.GroupId)?.DisplayName ?? "自定义组",
        };
        var members = MembersOf(cfg.GroupId);

        var border = new Border
        {
            Background = ResolveBrush("CardBackgroundFillColorDefaultBrush", Brushes.Transparent),
            BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
        };
        var stack = new StackPanel { Spacing = 8 };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromArgb(26, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(45, 128, 128, 128)),
            BorderThickness = new Thickness(1),
        };
        string? iconPath = GroupIconCatalog.ResolveValidIconPath(cfg);
        if (iconPath != null)
        {
            try
            {
                badge.Child = new Image
                {
                    Source = new Avalonia.Media.Imaging.Bitmap(iconPath),
                    Width = 22,
                    Height = 22,
                    Stretch = Stretch.Uniform,
                };
            }
            catch { badge.Child = null; }
        }
        if (badge.Child == null)
        {
            badge.Child = new TextBlock
            {
                Text = GroupIconCatalog.ResolveEffectiveGlyph(cfg, members),
                FontFamily = IconFont,
                FontSize = 18,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
        }
        top.Children.Add(badge);

        var visible = new CheckBox
        {
            Content = label,
            IsChecked = cfg.Visible,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        visible.IsCheckedChanged += (_, _) => { cfg.Visible = visible.IsChecked == true; SaveAndRefresh(); UpdateMacPreview(); };
        Grid.SetColumn(visible, 1);
        top.Children.Add(visible);

        var orderPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var up = new Button { Content = "↑", Padding = new Thickness(8, 4, 8, 4), IsEnabled = index > 0 };
        up.Click += (_, _) => MoveGroup(index, -1);
        var down = new Button { Content = "↓", Padding = new Thickness(8, 4, 8, 4), IsEnabled = index < total - 1 };
        down.Click += (_, _) => MoveGroup(index, 1);
        orderPanel.Children.Add(up);
        orderPanel.Children.Add(down);
        Grid.SetColumn(orderPanel, 2);
        top.Children.Add(orderPanel);
        stack.Children.Add(top);

        var iconCombo = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        foreach (var preset in GroupIconCatalog.Presets)
            iconCombo.Items.Add(new ComboBoxItem { Content = $"{preset.Glyph}  {preset.Label}", Tag = preset.Glyph });
        int selected = -1;
        for (int i = 0; i < iconCombo.Items.Count; i++)
        {
            if (iconCombo.Items[i] is ComboBoxItem ci &&
                string.Equals(ci.Tag as string, cfg.CustomGlyph, StringComparison.Ordinal))
            {
                selected = i;
                break;
            }
        }
        iconCombo.SelectedIndex = selected;
        iconCombo.SelectionChanged += (_, _) =>
        {
            if (_binding || iconCombo.SelectedItem is not ComboBoxItem item) return;
            cfg.CustomGlyph = item.Tag as string;
            cfg.CustomIconPath = null;
            SaveAndRefresh();
            RebuildGroups();
        };
        stack.Children.Add(iconCombo);

        var actions = new WrapPanel();
        var upload = new Button { Content = "上传图标...", Tag = cfg, Margin = new Thickness(0, 0, 6, 6) };
        upload.Click += OnPickIcon;
        actions.Children.Add(upload);
        if (!string.IsNullOrWhiteSpace(cfg.CustomGlyph) || !string.IsNullOrWhiteSpace(cfg.CustomIconPath))
        {
            var reset = new Button { Content = "恢复默认", Margin = new Thickness(0, 0, 6, 6) };
            reset.Click += (_, _) =>
            {
                cfg.CustomGlyph = null;
                cfg.CustomIconPath = null;
                SaveAndRefresh();
                RebuildGroups();
            };
            actions.Children.Add(reset);
        }
        if (cfg.GroupId.StartsWith("custom:", StringComparison.Ordinal))
        {
            var delete = new Button { Content = "删除此组", Margin = new Thickness(0, 0, 6, 6) };
            delete.Click += (_, _) =>
            {
                App.Settings.Current.CustomGroups.RemoveAll(g => g.Id == cfg.GroupId);
                App.Settings.Current.TaskbarGroups.RemoveAll(g => g.GroupId == cfg.GroupId);
                App.Settings.Save();
                App.RefreshWidget();
                RebuildGroups();
            };
            actions.Children.Add(delete);
        }
        stack.Children.Add(actions);

        border.Child = stack;
        return border;
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
                HintText.Text = "文件选择器不可用。";
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
            {
                HintText.Text = "未选择有效的图标文件。";
                return;
            }

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
            HintText.Text = $"已为「{LabelOf(cfg)}」更新图标。";
        }
        catch (Exception ex)
        {
            HintText.Text = $"选择自定义图标失败：{ex.Message}";
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
        _ => cfg.GroupId,
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

    private static List<PeripheralDevice> MembersOf(string groupId)
    {
        var all = App.LastDevices;
        return groupId switch
        {
            "builtin:mouse" => all.Where(d => d.Kind == DeviceKind.Mouse).ToList(),
            "builtin:keyboard" => all.Where(d => d.Kind == DeviceKind.Keyboard).ToList(),
            "builtin:headset" => all.Where(d => d.Kind == DeviceKind.Headset).ToList(),
            "builtin:controller" => all.Where(d => d.Kind == DeviceKind.Controller).ToList(),
            _ => all.Where(d => App.Settings.Current.CustomGroups
                .FirstOrDefault(g => g.Id == groupId)?.MemberIds.Contains(d.Id) == true).ToList(),
        };
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
            HintText.Text = "请填写组名并至少勾选一台设备。";
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
        App.Settings.Save();
        App.RefreshWidget();
        RebuildGroups();
        HintText.Text = $"已创建自定义组「{name}」。";
    }

    private static void PopulateMetricCombo(ComboBox box, string selectedId, bool allowNone)
    {
        box.Items.Clear();
        if (allowNone)
            box.Items.Add(new ComboBoxItem { Content = "不显示（仅单行）", Tag = "NONE" });
        foreach (var m in DesktopMetricsEngine.AllMetrics)
            box.Items.Add(new ComboBoxItem { Content = $"{m.FullName}（{m.ShortLabel}）", Tag = m.Id });

        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private void OnTaskbarMetricChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_binding) return;
        if (TopMetricBox.SelectedItem is ComboBoxItem top && top.Tag is string topId)
            App.Settings.Current.MonitorTopMetric = topId;
        if (BottomMetricBox.SelectedItem is ComboBoxItem bottom && bottom.Tag is string bottomId)
            App.Settings.Current.MonitorBottomMetric = bottomId;
        App.Settings.Save();
        App.RefreshWidget();
        UpdateMonitorStatus();
    }

    private void UpdateMonitorStatus()
    {
        if (!App.Settings.Current.MonitorEnabled)
        {
            MonitorStatusText.Text = "未启用：任务栏贴片只显示外设电量。";
            return;
        }
        string top = DesktopMetricsEngine.AllMetrics
            .FirstOrDefault(m => m.Id == App.Settings.Current.MonitorTopMetric)?.ShortLabel
            ?? App.Settings.Current.MonitorTopMetric;
        string bottom = App.Settings.Current.MonitorBottomMetric == "NONE"
            ? "隐藏"
            : (DesktopMetricsEngine.AllMetrics
                .FirstOrDefault(m => m.Id == App.Settings.Current.MonitorBottomMetric)?.ShortLabel
               ?? App.Settings.Current.MonitorBottomMetric);
        MonitorStatusText.Text = $"已启用：任务栏显示「{top} / {bottom}」，鼠标移至监控区域自动弹出详细悬浮窗。";
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
