using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using GearDeck.App.Controls;
using GearDeck.App.Services;
using GearDeck.Core.Models;
using Windows.Storage.Pickers;

namespace GearDeck.App.Pages;

public sealed partial class WidgetPage : Page
{
    private bool _loading;
    private bool _isCardMode;

    public WidgetPage()
    {
        InitializeComponent();
        _isCardMode = App.Settings.Current.UseCardLayout;
        GroupHost.IsCardMode = _isCardMode;
        GroupViewSwitchSlot.Children.Add(UiBuilders.CreateViewModeSwitcher(_isCardMode, isCard =>
        {
            _isCardMode = isCard;
            GroupHost.IsCardMode = isCard;
            Rebuild();
        }));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isCardMode = App.Settings.Current.UseCardLayout;
        GroupHost.IsCardMode = _isCardMode;
        App.WidgetPositionChanged += OnExternalPositionChanged;
        _loading = true;
        App.Settings.EnsureDefaults();
        VisibleSwitch.IsOn = App.Settings.Current.WidgetVisible;
        EmbedSwitch.IsOn = App.Settings.Current.WidgetEmbedded;
        SyncPositionControls();
        HeightBox.Value = App.Settings.Current.WidgetHeight;
        FontBox.Value = App.Settings.Current.WidgetFontSize;
        BlockBox.Value = App.Settings.Current.WidgetBlockWidth;
        Rebuild();
        _loading = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.WidgetPositionChanged -= OnExternalPositionChanged;
    }

    private void OnGoToMonitor(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.OpenMonitorPage();

    private void OnExternalPositionChanged()
    {
        App.UiQueue?.TryEnqueue(() =>
        {
            _loading = true;
            SyncPositionControls();
            _loading = false;
        });
    }

    private void SyncPositionControls()
    {
        AlignRadios.SelectedIndex = App.Settings.Current.WidgetAlignment == "left" ? 0 : 1;
        int offset = App.Settings.Current.WidgetOffsetX;
        OffsetSlider.Value = Math.Clamp(offset, -1200, 1200);
        OffsetBox.Value = offset;
    }

    private void Rebuild()
    {
        var groups = App.Settings.Current.TaskbarGroups;
        var items = new List<UIElement>(groups.Count);
        for (int i = 0; i < groups.Count; i++)
            items.Add(BuildGroupCard(groups[i], i, groups.Count));
        GroupHost.SetItems(items);

        CustomDeviceList.Children.Clear();
        foreach (var d in App.Devices.LastSnapshot())
            CustomDeviceList.Children.Add(new CheckBox { Content = d.Name, Tag = d.Id });
    }

    private FrameworkElement BuildGroupCard(TaskbarGroupConfig cfg, int index, int totalCount)
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
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
        };
        var panel = new StackPanel { Spacing = 10 };

        var topRow = new Grid { ColumnSpacing = 10 };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var previewBadge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(26, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 128, 128, 128)),
            BorderThickness = new Thickness(1),
        };
        string? customIconPath = GroupIconCatalog.ResolveValidIconPath(cfg);
        if (customIconPath != null)
        {
            previewBadge.Child = new Image
            {
                Source = new BitmapImage(new Uri(customIconPath)),
                Width = 22,
                Height = 22,
                Stretch = Stretch.Uniform,
            };
        }
        else
        {
            previewBadge.Child = new FontIcon
            {
                Glyph = GroupIconCatalog.ResolveEffectiveGlyph(cfg, members),
                FontSize = 18,
            };
        }
        topRow.Children.Add(previewBadge);

        var visible = new CheckBox
        {
            Content = label,
            IsChecked = cfg.Visible,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        visible.Checked += (_, _) => { cfg.Visible = true; SaveAndRefresh(); };
        visible.Unchecked += (_, _) => { cfg.Visible = false; SaveAndRefresh(); };
        Grid.SetColumn(visible, 1);
        topRow.Children.Add(visible);

        var orderPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var upBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE74A", FontSize = 12 },
            IsEnabled = index > 0,
            Padding = new Thickness(8, 6, 8, 6),
        };
        ToolTipService.SetToolTip(upBtn, "前移");
        upBtn.Click += (_, _) => MoveGroup(index, -1);

        var downBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE74B", FontSize = 12 },
            IsEnabled = index < totalCount - 1,
            Padding = new Thickness(8, 6, 8, 6),
        };
        ToolTipService.SetToolTip(downBtn, "后移");
        downBtn.Click += (_, _) => MoveGroup(index, 1);

        orderPanel.Children.Add(upBtn);
        orderPanel.Children.Add(downBtn);
        Grid.SetColumn(orderPanel, 2);
        topRow.Children.Add(orderPanel);
        panel.Children.Add(topRow);

        var iconRow = new ResponsiveWrapPanel { Spacing = 8, LineSpacing = 6 };
        var presetBtn = new Button { Content = "选择预设图标" };
        presetBtn.Flyout = BuildPresetIconFlyout(cfg);
        iconRow.Children.Add(presetBtn);

        var uploadBtn = new Button { Content = "上传图标..." };
        uploadBtn.Click += async (_, _) => await PickCustomIconAsync(cfg);
        iconRow.Children.Add(uploadBtn);

        if (!string.IsNullOrWhiteSpace(cfg.CustomGlyph) || !string.IsNullOrWhiteSpace(cfg.CustomIconPath))
        {
            var resetBtn = new Button { Content = "恢复默认" };
            resetBtn.Click += (_, _) =>
            {
                cfg.CustomGlyph = null;
                cfg.CustomIconPath = null;
                SaveAndRefresh();
                Rebuild();
            };
            iconRow.Children.Add(resetBtn);
        }
        if (cfg.GroupId.StartsWith("custom:"))
        {
            var delete = new Button { Content = "删除此组" };
            delete.Click += (_, _) =>
            {
                App.Settings.Current.CustomGroups.RemoveAll(g => g.Id == cfg.GroupId);
                App.Settings.Current.TaskbarGroups.RemoveAll(g => g.GroupId == cfg.GroupId);
                App.Settings.Save();
                App.RefreshWidget();
                Rebuild();
            };
            iconRow.Children.Add(delete);
        }
        panel.Children.Add(iconRow);

        var combo = new ComboBox { Header = "默认设备（显示在任务栏上的电量）", HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.Items.Add(new ComboBoxItem { Content = "自动（第一台在线设备）", Tag = "" });
        foreach (var d in members)
            combo.Items.Add(new ComboBoxItem { Content = $"{d.Name}（{d.BatteryPercent?.ToString() ?? "--"}%）", Tag = d.Id });
        int selected = 0;
        for (int i = 1; i < combo.Items.Count; i++)
            if (combo.Items[i] is ComboBoxItem it && (string?)it.Tag == cfg.DefaultDeviceId) selected = i;
        combo.SelectedIndex = selected;
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            cfg.DefaultDeviceId = (combo.SelectedItem as ComboBoxItem)?.Tag as string is { Length: > 0 } id ? id : null;
            SaveAndRefresh();
        };
        panel.Children.Add(combo);

        border.Child = panel;
        return border;
    }

    private Flyout BuildPresetIconFlyout(TaskbarGroupConfig cfg)
    {
        var flyout = new Flyout();
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (int c = 0; c < 4; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        int rows = (GroupIconCatalog.Presets.Count + 3) / 4;
        for (int r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < GroupIconCatalog.Presets.Count; i++)
        {
            var preset = GroupIconCatalog.Presets[i];
            var stack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new FontIcon { Glyph = preset.Glyph, FontSize = 18 });
            stack.Children.Add(new TextBlock { Text = preset.Label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });

            var btn = new Button
            {
                Content = stack,
                Width = 76,
                Padding = new Thickness(4, 6, 4, 6),
            };
            btn.Click += (_, _) =>
            {
                cfg.CustomGlyph = preset.Glyph;
                cfg.CustomIconPath = null;
                flyout.Hide();
                SaveAndRefresh();
                Rebuild();
            };
            Grid.SetRow(btn, i / 4);
            Grid.SetColumn(btn, i % 4);
            grid.Children.Add(btn);
        }

        flyout.Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "选择任务栏组图标", FontWeight = FontWeights.SemiBold },
                grid,
            },
        };
        return flyout;
    }

    private async Task PickCustomIconAsync(TaskbarGroupConfig cfg)
    {
        try
        {
            var window = MainWindow.Instance;
            if (window == null) return;
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                ViewMode = PickerViewMode.Thumbnail,
            };
            foreach (var ext in new[] { ".png", ".ico", ".jpg", ".jpeg", ".bmp" })
                picker.FileTypeFilter.Add(ext);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            string iconsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GearDeck", "icons");
            Directory.CreateDirectory(iconsDir);

            string safeId = string.Concat(cfg.GroupId.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
            string extSelected = Path.GetExtension(file.Path);
            if (string.IsNullOrWhiteSpace(extSelected)) extSelected = ".png";
            string dest = Path.Combine(iconsDir, $"{safeId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{extSelected}");
            File.Copy(file.Path, dest, overwrite: true);

            cfg.CustomIconPath = dest;
            SaveAndRefresh();
            Rebuild();
        }
        catch (Exception ex)
        {
            GearDeck.Core.Diag.Log($"选择自定义图标失败: {ex.Message}");
        }
    }

    private void MoveGroup(int index, int delta)
    {
        var groups = App.Settings.Current.TaskbarGroups;
        int next = index + delta;
        if (next < 0 || next >= groups.Count) return;
        (groups[index], groups[next]) = (groups[next], groups[index]);
        SaveAndRefresh();
        Rebuild();
    }

    private static List<PeripheralDevice> MembersOf(string groupId)
    {
        var all = App.Devices.LastSnapshot();
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

    private void ApplyPreset(string align, int offset)
    {
        _loading = true;
        App.Settings.Current.WidgetEmbedded = true;
        EmbedSwitch.IsOn = true;
        App.Settings.Current.WidgetAlignment = align;
        App.Settings.Current.WidgetOffsetX = offset;
        SyncPositionControls();
        _loading = false;
        SaveAndRefresh();
    }

    private void OnPresetLeftWeather(object sender, RoutedEventArgs e) => ApplyPreset("left", 0);
    private void OnPresetMidLeft(object sender, RoutedEventArgs e) => ApplyPreset("left", 240);
    private void OnPresetMidRight(object sender, RoutedEventArgs e) => ApplyPreset("right", -240);
    private void OnPresetRightTray(object sender, RoutedEventArgs e) => ApplyPreset("right", 0);

    private void OnResetOffset(object sender, RoutedEventArgs e)
    {
        _loading = true;
        App.Settings.Current.WidgetOffsetX = 0;
        SyncPositionControls();
        _loading = false;
        SaveAndRefresh();
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.WidgetVisible = VisibleSwitch.IsOn;
        App.Settings.Current.WidgetEmbedded = EmbedSwitch.IsOn;
        SaveAndRefresh();
    }

    private void OnAlignChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _loading = true;
        App.Settings.Current.WidgetAlignment = AlignRadios.SelectedIndex == 0 ? "left" : "right";
        App.Settings.Current.WidgetOffsetX = 0;
        SyncPositionControls();
        _loading = false;
        SaveAndRefresh();
    }

    private void OnOffsetSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        int val = (int)Math.Round(e.NewValue);
        _loading = true;
        OffsetBox.Value = val;
        _loading = false;
        App.Settings.Current.WidgetOffsetX = val;
        SaveAndRefresh();
    }

    private void OnOffsetChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        int val = (int)Math.Round(args.NewValue);
        _loading = true;
        OffsetSlider.Value = Math.Clamp(val, -1200, 1200);
        _loading = false;
        App.Settings.Current.WidgetOffsetX = val;
        SaveAndRefresh();
    }

    private void OnSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        App.Settings.Current.WidgetHeight = (int)HeightBox.Value;
        App.Settings.Current.WidgetFontSize = (int)FontBox.Value;
        App.Settings.Current.WidgetBlockWidth = (int)BlockBox.Value;
        SaveAndRefresh();
    }

    private void OnCreateGroup(object sender, RoutedEventArgs e)
    {
        string name = GroupNameBox.Text.Trim();
        var members = CustomDeviceList.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => c.Tag as string)
            .Where(id => id != null)
            .Cast<string>()
            .ToList();
        if (name.Length == 0 || members.Count == 0)
            return;

        var group = new DeviceGroup
        {
            Id = "custom:" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = name,
            MemberIds = members,
        };
        App.Settings.Current.CustomGroups.Add(group);
        App.Settings.Current.TaskbarGroups.Add(new TaskbarGroupConfig { GroupId = group.Id, Visible = true });
        App.Settings.Save();
        GroupNameBox.Text = "";
        App.RefreshWidget();
        Rebuild();
    }

    private void SaveAndRefresh()
    {
        App.Settings.Save();
        App.RefreshWidget();
    }
}
