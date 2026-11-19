using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using GearDeck.App.Controls;
using GearDeck.App.Services;
using GearDeck.Core.Models;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GearDeck.App.Pages;

public sealed partial class GamesPage : Page
{
    private sealed record TargetOption(string Value, string Label);

    private static List<ScannedGameEntry>? _cachedGames;
    private List<RunningWindowProcess> _runningProcs = [];
    private bool _isCardMode;
    private bool _isGameLibraryExpanded;
    private bool _binding;

    public GamesPage()
    {
        InitializeComponent();
        _isCardMode = App.Settings.Current.UseCardLayout;
        ApplyHostLayoutModes();

        ViewSwitchSlot.Children.Add(UiBuilders.CreateViewModeSwitcher(_isCardMode, isCard =>
        {
            _isCardMode = isCard;
            ApplyHostLayoutModes();
            RenderScannedGames();
            RebuildRulesUi();
            RebuildStrategyCards();
            RebuildDeviceQuickCards();
            RebuildPresetsUi();
        }));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void ApplyHostLayoutModes()
    {
        ScannedGamesHost.IsCardMode = _isCardMode;
        RulesHost.IsCardMode = _isCardMode;
        StrategyCardsHost.IsCardMode = _isCardMode;
        DevicePresetsQuickHost.IsCardMode = _isCardMode;
        PresetsHost.IsCardMode = _isCardMode;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _isGameLibraryExpanded = false;
        SyncGameLibraryExpandUi();
        RefreshAllUi();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isCardMode = App.Settings.Current.UseCardLayout;
        _isGameLibraryExpanded = false;
        SyncGameLibraryExpandUi();
        ApplyHostLayoutModes();
        App.Devices.DevicesUpdated += OnDevicesUpdated;
        if (App.GameAuto != null)
            App.GameAuto.StatusChanged += OnAutoStatusChanged;

        RefreshRunningProcList();
        RefreshAllUi();

        if (_cachedGames == null)
            _ = ScanGamesInternalAsync(quiet: true, expandAfterScan: false);
        else
            RenderScannedGames();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Devices.DevicesUpdated -= OnDevicesUpdated;
        if (App.GameAuto != null)
            App.GameAuto.StatusChanged -= OnAutoStatusChanged;
    }

    private void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> _) => App.UiQueue?.TryEnqueue(RefreshAllUi);
    private void OnAutoStatusChanged() => App.UiQueue?.TryEnqueue(UpdateStatusBanner);

    private void RefreshAllUi()
    {
        _binding = true;
        try
        {
            var s = App.Settings.Current;
            AutoSwitchToggle.IsOn = s.AutoSwitchEnabled;
            NotifySwitchToggle.IsOn = s.NotifyOnProfileSwitch;
            FullscreenMasterToggle.IsOn = s.EnableExclusiveFullscreenFallback;

            SyncGameLibraryExpandUi();
            UpdateStatusBanner();
            RenderScannedGames();
            RebuildRulesUi();
            RebuildStrategyCards();
            RebuildDeviceQuickCards();
            RebuildCaptureMouseCombo();
            RebuildPresetsUi();
        }
        finally
        {
            _binding = false;
        }
    }

    private void OnToggleGameLibrary(object sender, RoutedEventArgs e)
    {
        _isGameLibraryExpanded = !_isGameLibraryExpanded;
        SyncGameLibraryExpandUi();
    }

    private void SyncGameLibraryExpandUi()
    {
        if (GameLibraryBody == null || ToggleGameLibraryBtn == null) return;
        GameLibraryBody.Visibility = _isGameLibraryExpanded ? Visibility.Visible : Visibility.Collapsed;
        ToggleGameLibraryBtn.Content = _isGameLibraryExpanded ? "收起 ▴" : "展开游戏库 ▾";
        if (_cachedGames != null)
        {
            ScannedGamesHint.Text = _isGameLibraryExpanded
                ? $"已识别 {_cachedGames.Count} 款本机游戏（点击“+ 设为切换规则”即可添加到规则列表）"
                : $"已发现 {_cachedGames.Count} 款本机游戏（默认收起，点击右侧“展开游戏库 ▾”查看）";
        }
    }

    private async void OnScanInstalledGames(object sender, RoutedEventArgs e)
        => await ScanGamesInternalAsync(quiet: false, expandAfterScan: true);

    private async Task ScanGamesInternalAsync(bool quiet, bool expandAfterScan)
    {
        ScanGamesBtn.IsEnabled = false;
        ScannedGamesHint.Text = "正在后台扫描 Steam / Epic / Riot / WeGame / HoYoPlay / Battle.net / Xbox...";
        try
        {
            _cachedGames = await InstalledGameScanner.ScanAsync();
            if (expandAfterScan)
                _isGameLibraryExpanded = true;
            SyncGameLibraryExpandUi();
            RenderScannedGames();
            if (!quiet)
                ShowStatus($"扫描完成，共发现 {_cachedGames.Count} 款有效游戏。", InfoBarSeverity.Success);
        }
        finally
        {
            ScanGamesBtn.IsEnabled = true;
        }
    }

    private void OnGameFilterChanged(object sender, TextChangedEventArgs e) => RenderScannedGames();

    private void RenderScannedGames()
    {
        var list = _cachedGames;
        if (list == null)
        {
            ScannedGamesHost.SetItems([]);
            return;
        }

        string q = GameFilterBox.Text?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(q)
            ? list
            : list.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                              g.PrimaryExe.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                              g.Platform.Contains(q, StringComparison.OrdinalIgnoreCase))
                  .ToList();

        var existingProcs = App.Settings.Current.GameRules
            .SelectMany(r => r.ProcessNames)
            .Select(GameProfileRuleMatcher.NormalizeProcessName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var elements = new List<UIElement>(filtered.Count);
        foreach (var g in filtered)
        {
            bool alreadyAdded = existingProcs.Contains(GameProfileRuleMatcher.NormalizeProcessName(g.PrimaryExe));

            var iconElem = CreateGameIconElement(g.IconPath, _isCardMode ? 42 : 32);
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
            };

            var addBtn = new Button
            {
                Content = alreadyAdded ? "已存在规则" : "+ 设为切换规则",
                IsEnabled = !alreadyAdded,
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
            };
            if (!alreadyAdded) addBtn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            addBtn.Click += (_, _) => AddRuleFromScannedGame(g);

            if (_isCardMode)
            {
                var stack = new StackPanel { Spacing = 8 };
                var top = new Grid { ColumnSpacing = 10 };
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                top.Children.Add(iconElem);

                var meta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                meta.Children.Add(new TextBlock
                {
                    Text = g.Name,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                meta.Children.Add(new TextBlock
                {
                    Text = $"{g.Platform} · {g.PrimaryExe}",
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
                Grid.SetColumn(meta, 1);
                top.Children.Add(meta);
                stack.Children.Add(top);

                var btnRow = new ResponsiveWrapPanel { Spacing = 6, LineSpacing = 4 };
                btnRow.Children.Add(addBtn);
                stack.Children.Add(btnRow);
                card.Child = stack;
            }
            else
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(iconElem);

                var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                meta.Children.Add(new TextBlock { Text = g.Name, FontWeight = FontWeights.SemiBold });
                meta.Children.Add(new TextBlock
                {
                    Text = $"{g.Platform} · {string.Join(", ", g.CandidateExes)}",
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
                Grid.SetColumn(meta, 1);
                row.Children.Add(meta);
                Grid.SetColumn(addBtn, 2);
                row.Children.Add(addBtn);
                card.Child = row;
            }
            elements.Add(card);
        }

        ScannedGamesHost.SetItems(elements);
    }

    private static UIElement CreateGameIconElement(string? iconPath, double size)
    {
        var border = new Border
        {
            Width = size + 6,
            Height = size + 6,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 128, 128, 128)),
        };
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            border.Child = new Image
            {
                Source = new BitmapImage(new Uri(iconPath)),
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
            };
        }
        else
        {
            border.Child = new FontIcon { Glyph = "\uE7FC", FontSize = Math.Max(16, size * 0.5) };
        }
        return border;
    }

    private void AddRuleFromScannedGame(ScannedGameEntry g)
    {
        App.Settings.Current.GameRules.Add(new GameAutoRule
        {
            Name = g.Name,
            Enabled = true,
            ProcessNames = g.CandidateExes.ToList(),
            RequireFullscreen = false,
        });
        App.Settings.Save();
        RefreshAllUi();
        ShowStatus($"已添加游戏规则「{g.Name}」({string.Join(", ", g.CandidateExes)})，请在下方为每台设备选择板载或软件预设。", InfoBarSeverity.Success);
    }

    private void UpdateStatusBanner()
    {
        var g = App.GameAuto;
        if (g == null)
        {
            ForegroundMonitorText.Text = "自动监测服务尚未启动。";
            ActiveRuleStatusText.Text = "当前生效策略：--";
            DeviceOutcomeText.Text = "";
            return;
        }

        string proc = string.IsNullOrWhiteSpace(g.LastForegroundProcess) ? "（无焦点窗口）" : g.LastForegroundProcess;
        string fs = g.LastIsFullscreen ? "独占 / 无边框全屏" : "普通窗口";
        string title = string.IsNullOrWhiteSpace(g.LastForegroundTitle) ? "" : $" · 「{g.LastForegroundTitle}」";
        ForegroundMonitorText.Text = $"当前前台进程：{proc} [{fs}]{title}";
        ActiveRuleStatusText.Text = $"当前生效策略：{g.ActiveRuleLabel}";

        if (g.LastOutcomeByDevice.Count == 0)
        {
            DeviceOutcomeText.Text = "尚无切换写入记录（仅当目标外设未休眠且进入匹配规则/全屏时执行写入）。";
        }
        else
        {
            var devNames = App.LastDevices.ToDictionary(d => d.Id, d => d.Name, StringComparer.OrdinalIgnoreCase);
            DeviceOutcomeText.Text = string.Join(" | ",
                g.LastOutcomeByDevice.Select(kv => $"{(devNames.TryGetValue(kv.Key, out var n) ? n : kv.Key)}: {kv.Value}"));
        }
    }

    private void RefreshRunningProcList()
    {
        _runningProcs = GameProfileAutoService.GetRunningForegroundCandidates();
        RunningProcCombo.Items.Clear();
        foreach (var p in _runningProcs)
            RunningProcCombo.Items.Add($"{p.ProcessName}  ({p.WindowTitle})");
    }

    private void OnRefreshRunningProcs(object sender, RoutedEventArgs e) => RefreshRunningProcList();

    private void OnRunningProcSelected(object sender, SelectionChangedEventArgs e)
    {
        int idx = RunningProcCombo.SelectedIndex;
        if (idx < 0 || idx >= _runningProcs.Count) return;
        var chosen = _runningProcs[idx];
        NewRuleProcessBox.Text = chosen.ProcessName;
        if (string.IsNullOrWhiteSpace(NewRuleNameBox.Text))
            NewRuleNameBox.Text = Path.GetFileNameWithoutExtension(chosen.ProcessName);
    }

    private async void OnBrowseExe(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            if (MainWindow.Instance != null)
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(MainWindow.Instance));
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                NewRuleProcessBox.Text = file.Name;
                if (string.IsNullOrWhiteSpace(NewRuleNameBox.Text))
                    NewRuleNameBox.Text = Path.GetFileNameWithoutExtension(file.Name);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"文件选择器打开失败: {ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var procs = NewRuleProcessBox.Text
            .Split([',', ';', '，', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (procs.Count == 0)
        {
            ShowStatus("请先填写至少一个进程名（如 cs2.exe），或从游戏库/运行列表中选择。", InfoBarSeverity.Warning);
            return;
        }

        string name = string.IsNullOrWhiteSpace(NewRuleNameBox.Text)
            ? Path.GetFileNameWithoutExtension(procs[0])
            : NewRuleNameBox.Text.Trim();

        App.Settings.Current.GameRules.Add(new GameAutoRule
        {
            Name = name,
            Enabled = true,
            ProcessNames = procs,
            RequireFullscreen = NewRuleRequireFsCheck.IsChecked == true,
        });
        App.Settings.Save();
        NewRuleNameBox.Text = "";
        NewRuleProcessBox.Text = "";
        NewRuleRequireFsCheck.IsChecked = false;
        RefreshAllUi();
        ShowStatus($"已添加游戏规则「{name}」，请在下方为每台设备指派对应配置文件或预设。", InfoBarSeverity.Success);
    }

    private void OnGlobalSettingsToggled(object sender, RoutedEventArgs e)
    {
        if (_binding) return;
        var s = App.Settings.Current;
        s.AutoSwitchEnabled = AutoSwitchToggle.IsOn;
        s.NotifyOnProfileSwitch = NotifySwitchToggle.IsOn;
        App.Settings.Save();
        App.GameAuto?.ClearAppliedCache();
        _ = App.GameAuto?.ForceReevaluateAsync();
    }

    private void OnFullscreenMasterToggled(object sender, RoutedEventArgs e)
    {
        if (_binding) return;
        App.Settings.Current.EnableExclusiveFullscreenFallback = FullscreenMasterToggle.IsOn;
        App.Settings.Save();
        RebuildStrategyCards();
        App.GameAuto?.ClearAppliedCache();
        _ = App.GameAuto?.ForceReevaluateAsync();
    }

    private async void OnForceReevaluate(object sender, RoutedEventArgs e)
    {
        if (App.GameAuto == null) return;
        await App.GameAuto.ForceReevaluateAsync();
        UpdateStatusBanner();
        ShowStatus("已完成前台窗口检测并按策略向已唤醒外设执行对应配置。", InfoBarSeverity.Informational);
    }

    private List<TargetOption> BuildTargetOptionsForDevice(string deviceId, PeripheralDevice? dev)
    {
        var list = new List<TargetOption>
        {
            new("", "保持当前不变"),
        };

        // 1. 该设备真实厂家板载预设槽位（已读取的带 DPI/Hz 详情）
        var onboardSlots = App.Settings.GetDeviceOnboardSlots(dev, deviceId);
        foreach (var s in onboardSlots)
        {
            string label = string.IsNullOrWhiteSpace(s.Summary)
                ? $"[厂家板载] {s.Name}"
                : $"[厂家板载] {s.Name} ({s.Summary})";
            list.Add(new TargetOption($"slot:{s.SlotIndex}", label));
        }

        // 2. 保存在本台硬件下的专属软件预设
        var ownPresets = App.Settings.Current.Presets
            .Where(p => string.Equals(p.SourceDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var preset in ownPresets)
            list.Add(new TargetOption($"preset:{preset.Id}", $"[本设备预设] {preset.Name} ({FormatPresetSummary(preset)})"));

        // 3. 兼容的其他设备预设
        var otherPresets = App.Settings.Current.Presets
            .Where(p => !string.Equals(p.SourceDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var preset in otherPresets)
            list.Add(new TargetOption($"preset:{preset.Id}", $"[通用预设] {preset.Name} ({FormatPresetSummary(preset)})"));

        return list;
    }

    private static string FormatPresetSummary(MouseHardwarePreset preset)
    {
        string summary = preset.DpiX is { } d ? $"{d} DPI" : "参数预设";
        if (preset.PollingRateHz is { } r) summary += $" · {r}Hz";
        return summary;
    }

    private List<(string DeviceId, PeripheralDevice? Device, string DisplayName, bool IsSleeping)> CollectTunableEntries(
        IReadOnlyDictionary<string, string>? extraKeys = null)
    {
        var result = new Dictionary<string, (PeripheralDevice? Dev, string Label, bool Sleeping)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in App.LastDevices.Where(x => x.Kind is DeviceKind.Mouse or DeviceKind.Controller))
            result[d.Id] = (d, $"{d.Name} ({d.Provider})" + (d.IsSleeping ? " [休眠]" : ""), d.IsSleeping);

        if (extraKeys != null)
        {
            foreach (var k in extraKeys.Keys)
            {
                if (!result.ContainsKey(k))
                    result[k] = (null, $"{k} (离线)", true);
            }
        }
        return result.Select(kv => (kv.Key, kv.Value.Dev, kv.Value.Label, kv.Value.Sleeping))
            .OrderBy(x => x.Sleeping).ThenBy(x => x.Label).ToList();
    }

    private StackPanel BuildPerMouseTargetPanel(Dictionary<string, string> targetMap, bool isEnabled = true)
    {
        var panel = new StackPanel { Spacing = 8 };
        var devs = CollectTunableEntries(targetMap);
        if (devs.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "当前未检测到可调节的鼠标或手柄设备；连接后此处将自动列出每台设备供单独指派。",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            return panel;
        }

        foreach (var (deviceId, dev, displayName, _) in devs)
        {
            var options = BuildTargetOptionsForDevice(deviceId, dev);
            panel.Children.Add(CreateDeviceTargetRow(deviceId, displayName, options, targetMap, isEnabled));
        }
        return panel;
    }

    private Grid CreateDeviceTargetRow(
        string deviceId,
        string displayName,
        List<TargetOption> options,
        Dictionary<string, string> targetMap,
        bool isEnabled = true)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

        row.Children.Add(new TextBlock
        {
            Text = displayName,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = options,
            DisplayMemberPath = nameof(TargetOption.Label),
            IsEnabled = isEnabled,
        };
        targetMap.TryGetValue(deviceId, out string? currentTarget);
        int selectedIdx = options.FindIndex(o => string.Equals(o.Value, currentTarget ?? "", StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;

        combo.SelectionChanged += (_, _) =>
        {
            if (_binding) return;
            if (combo.SelectedItem is TargetOption opt)
            {
                if (string.IsNullOrEmpty(opt.Value))
                    targetMap.Remove(deviceId);
                else
                    targetMap[deviceId] = opt.Value;
                App.Settings.Save();
                App.GameAuto?.ClearAppliedCache();
            }
        };

        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        return row;
    }

    /// <summary>
    /// 统一构建“全屏独占策略”与“退出恢复策略”两张合并策略卡片：
    /// 卡片 1 受上方「全盘独占策略总开关」控制，仅在开启后可编辑并生效。
    /// </summary>
    private void RebuildStrategyCards()
    {
        var s = App.Settings.Current;
        bool wasBinding = _binding;
        _binding = true;
        try
        {
            bool fsEnabled = s.EnableExclusiveFullscreenFallback;

            // 卡片 1：全屏独占策略（仅总开关开启后允许更改且生效）
            var fsCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = fsEnabled,
            };
            fsCombo.Items.Add(new ComboBoxItem { Content = "独占全屏 + 无边框全屏窗口均触发（推荐）", Tag = false });
            fsCombo.Items.Add(new ComboBoxItem { Content = "仅原生 Direct3D 独占全屏时触发（忽略无边框/全屏浏览器）", Tag = true });
            fsCombo.SelectedIndex = s.OnlyTrueExclusiveFullscreen ? 1 : 0;
            fsCombo.SelectionChanged += (_, _) =>
            {
                if (_binding) return;
                if (fsCombo.SelectedItem is ComboBoxItem ci && ci.Tag is bool onlyTrue)
                {
                    s.OnlyTrueExclusiveFullscreen = onlyTrue;
                    App.Settings.Save();
                    App.GameAuto?.ClearAppliedCache();
                    _ = App.GameAuto?.ForceReevaluateAsync();
                }
            };

            var card1 = BuildStrategyCard(
                "1. 全屏独占自动切换策略" + (fsEnabled ? "（已启用）" : "（未开启）"),
                fsEnabled
                    ? "当未命中自定义进程规则的游戏或应用进入全屏独占时触发；支持选择检测方式与各外设目标预设。"
                    : "请先开启上方「全盘独占策略总开关」，开启后才可编辑下方规则并在进入全屏时自动切换。",
                fsCombo,
                BuildPerMouseTargetPanel(s.FullscreenFallbackTargets, isEnabled: fsEnabled));
            card1.Opacity = fsEnabled ? 1.0 : 0.55;

            // 卡片 2：退出恢复策略
            var exitCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            exitCombo.Items.Add(new ComboBoxItem { Content = "等待全屏窗口 / 游戏进程完全退出后再恢复桌面默认（Alt+Tab 不切回）", Tag = ExitRestoreMode.WhenWindowExits });
            exitCombo.Items.Add(new ComboBoxItem { Content = "离开游戏 / 全屏窗口焦点 1.5 秒后立即恢复桌面默认", Tag = ExitRestoreMode.WhenFocusLeaves });
            exitCombo.Items.Add(new ComboBoxItem { Content = "退出后不自动恢复（始终保留上一次的配置）", Tag = ExitRestoreMode.NeverRestore });
            exitCombo.SelectedIndex = (int)s.GetExitRestoreMode();
            exitCombo.SelectionChanged += (_, _) =>
            {
                if (_binding) return;
                if (exitCombo.SelectedItem is ComboBoxItem ci && ci.Tag is ExitRestoreMode mode)
                {
                    s.SetExitRestoreMode(mode);
                    App.Settings.Save();
                    App.GameAuto?.ClearAppliedCache();
                    _ = App.GameAuto?.ForceReevaluateAsync();
                }
            };

            var card2 = BuildStrategyCard(
                "2. 退出全屏 / 游戏默认恢复策略",
                "只要检测到全屏独占中的窗口或游戏尚未退出（如 Alt+Tab 切出），推荐保持不切回默认；窗口退出后再恢复以下默认配置。",
                exitCombo,
                BuildPerMouseTargetPanel(s.DesktopDefaultTargets, isEnabled: true));

            StrategyCardsHost.SetItems([card1, card2]);
        }
        finally
        {
            _binding = wasBinding;
        }
    }

    private static UIElement BuildStrategyCard(
        string title,
        string description,
        ComboBox modeDropdown,
        UIElement targetsContent)
    {
        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
        };
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        stack.Children.Add(modeDropdown);
        stack.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] });
        stack.Children.Add(targetsContent);
        card.Child = stack;
        return card;
    }

    private void RebuildRulesUi()
    {
        var rules = App.Settings.Current.GameRules;
        NoRulesHint.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var cards = new List<UIElement>(rules.Count);

        foreach (var rule in rules.ToList())
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
            };
            var stack = new StackPanel { Spacing = 10 };

            stack.Children.Add(new TextBlock
            {
                Text = rule.Name,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"匹配进程：{string.Join(", ", rule.ProcessNames)}",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });

            var devs = CollectTunableEntries(rule.PerMouseTargets);
            if (devs.Count == 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "未检测到鼠标/手柄设备；连接后可在此指派每台设备的目标配置。",
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
            }
            else
            {
                foreach (var (deviceId, dev, displayName, _) in devs)
                {
                    var options = BuildTargetOptionsForDevice(deviceId, dev);
                    stack.Children.Add(CreateDeviceTargetRow(deviceId, displayName, options, rule.PerMouseTargets));
                }
            }

            var footer = new ResponsiveWrapPanel { Spacing = 10, LineSpacing = 6 };
            var fsCheck = new CheckBox { Content = "仅全屏触发", IsChecked = rule.RequireFullscreen };
            fsCheck.Checked += (_, _) => { if (!_binding) { rule.RequireFullscreen = true; App.Settings.Save(); } };
            fsCheck.Unchecked += (_, _) => { if (!_binding) { rule.RequireFullscreen = false; App.Settings.Save(); } };
            footer.Children.Add(fsCheck);

            var enableToggle = new ToggleSwitch
            {
                IsOn = rule.Enabled,
                OnContent = "启用",
                OffContent = "停用",
                MinWidth = 86,
            };
            enableToggle.Toggled += (_, _) =>
            {
                if (_binding) return;
                rule.Enabled = enableToggle.IsOn;
                App.Settings.Save();
                App.GameAuto?.ClearAppliedCache();
            };
            footer.Children.Add(enableToggle);

            var testBtn = new Button
            {
                Content = "立即套用此规则",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                Padding = new Thickness(10, 4, 10, 4),
            };
            testBtn.Click += async (_, _) =>
            {
                if (App.GameAuto == null) return;
                await App.GameAuto.ApplyRuleManuallyAsync(rule);
                UpdateStatusBanner();
            };
            footer.Children.Add(testBtn);

            var delBtn = new Button { Content = "删除规则", Padding = new Thickness(10, 4, 10, 4) };
            delBtn.Click += (_, _) =>
            {
                App.Settings.Current.GameRules.Remove(rule);
                App.Settings.Save();
                RefreshAllUi();
            };
            footer.Children.Add(delBtn);

            stack.Children.Add(footer);
            card.Child = stack;
            cards.Add(card);
        }

        RulesHost.SetItems(cards);
    }

    private void RebuildDeviceQuickCards()
    {
        var devs = App.LastDevices.Where(d => d.Kind is DeviceKind.Mouse or DeviceKind.Controller).ToList();
        var cards = new List<UIElement>(devs.Count);
        foreach (var d in devs)
        {
            var slots = App.Settings.GetDeviceOnboardSlots(d, d.Id);
            int savedCount = App.Settings.Current.Presets.Count(p =>
                string.Equals(p.SourceDeviceId, d.Id, StringComparison.OrdinalIgnoreCase));

            var slotSummaries = slots.Where(s => !string.IsNullOrWhiteSpace(s.Summary))
                .Select(s => $"{s.Name}: {s.Summary}")
                .ToList();
            string statusLine = slots.Count > 0
                ? $"厂家板载 {slots.Count} 槽" + (slotSummaries.Count > 0 ? $" ({string.Join(" | ", slotSummaries.Take(2))})" : " (待唤醒读取)")
                : "无多板载槽位，可使用软件预设";

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
            head.Children.Add(UiBuilders.CreateDeviceAvatar(d, 40));

            var meta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(new TextBlock
            {
                Text = $"{d.Name}" + (d.IsSleeping ? " [休眠]" : ""),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            meta.Children.Add(new TextBlock
            {
                Text = $"{statusLine} · 已存专属预设 {savedCount} 个",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            Grid.SetColumn(meta, 1);
            head.Children.Add(meta);
            stack.Children.Add(head);

            var btns = new ResponsiveWrapPanel { Spacing = 6, LineSpacing = 6 };
            if (slots.Count > 0)
            {
                var probeBtn = new Button
                {
                    Content = "读取厂家板载预设",
                    Padding = new Thickness(10, 4, 10, 4),
                    FontSize = 12,
                    IsEnabled = !d.IsSleeping,
                };
                probeBtn.Click += async (_, _) =>
                {
                    probeBtn.IsEnabled = false;
                    var manifest = await App.Settings.ProbeAllOnboardSlotsAsync(App.Devices, d.Id);
                    RefreshAllUi();
                    ShowStatus(manifest != null
                        ? $"已读取 {d.Name} 的 {manifest.Slots.Count} 个厂家板载配置并更新到规则下拉。"
                        : $"无法读取 {d.Name}：设备处于休眠状态，请先唤醒。",
                        manifest != null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                };
                btns.Children.Add(probeBtn);
            }

            var detailBtn = new Button
            {
                Content = "打开设备预设管理 →",
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
            };
            detailBtn.Click += (_, _) => MainWindow.Instance?.OpenDevice(d.Id);
            btns.Children.Add(detailBtn);

            stack.Children.Add(btns);
            card.Child = stack;
            cards.Add(card);
        }

        DevicePresetsQuickHost.SetItems(cards);
    }

    private void RebuildCaptureMouseCombo()
    {
        CaptureMouseCombo.Items.Clear();
        var devs = App.LastDevices.Where(d => d.Kind is DeviceKind.Mouse or DeviceKind.Controller).ToList();
        foreach (var d in devs)
            CaptureMouseCombo.Items.Add(new ComboBoxItem { Content = $"{d.Name} ({d.Provider})", Tag = d });
        if (devs.Count > 0)
            CaptureMouseCombo.SelectedIndex = 0;
        CapturePresetBtn.IsEnabled = devs.Count > 0;
    }

    private async void OnCapturePreset(object sender, RoutedEventArgs e)
    {
        if (CaptureMouseCombo.SelectedItem is not ComboBoxItem item || item.Tag is not PeripheralDevice dev)
            return;

        CapturePresetBtn.IsEnabled = false;
        try
        {
            var caps = await App.Devices.QueryCapabilitiesAsync(dev.Id);
            if (!caps.IsOnline)
            {
                ShowStatus($"无法读取 {dev.Name}：设备当前处于休眠或离线，请唤醒后再试。", InfoBarSeverity.Warning);
                return;
            }
            App.Settings.RecordCapabilitiesManifest(dev.Id, caps);

            string presetName = string.IsNullOrWhiteSpace(PresetNameBox.Text)
                ? $"{dev.Name} {(caps.CurrentDpiX > 0 ? caps.CurrentDpiX : 800)}DPI"
                : PresetNameBox.Text.Trim();
            var preset = MouseHardwarePreset.CaptureFrom(presetName, dev.Id, dev.Name, caps);
            App.Settings.Current.Presets.Add(preset);
            App.Settings.Save();
            PresetNameBox.Text = "";
            RefreshAllUi();
            ShowStatus($"已将预设「{preset.Name}」保存到 {dev.Name}，并已加入下拉选单。", InfoBarSeverity.Success);
        }
        finally
        {
            CapturePresetBtn.IsEnabled = CaptureMouseCombo.Items.Count > 0;
        }
    }

    private void RebuildPresetsUi()
    {
        var presets = App.Settings.Current.Presets;
        var elements = new List<UIElement>();
        if (presets.Count == 0)
        {
            elements.Add(new TextBlock
            {
                Text = "暂无已保存的硬件预设（可在各设备页面内“保存当前参数到本设备”，也可在此处抓取）。",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            PresetsHost.SetItems(elements);
            return;
        }

        foreach (var preset in presets.ToList())
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
            };

            var parts = new List<string>();
            if (preset.DpiX is { } x)
                parts.Add(preset.DpiY is { } y && y != x ? $"DPI X={x}/Y={y}" : $"DPI {x}");
            if (preset.PollingRateHz is { } rate) parts.Add($"{rate} Hz");
            if (preset.PerformanceMode is { } pm) parts.Add($"模式#{pm}");
            if (preset.LodCode is { } lod) parts.Add($"LOD#{lod}");
            if (preset.MotionSync == true) parts.Add("MotionSync");
            if (preset.DebounceMs is { } db) parts.Add($"防抖 {db}ms");
            if (!string.IsNullOrWhiteSpace(preset.SourceDeviceName)) parts.Add($"属于 {preset.SourceDeviceName}");

            var delBtn = new Button { Content = "删除预设", Padding = new Thickness(10, 4, 10, 4) };
            delBtn.Click += (_, _) =>
            {
                App.Settings.Current.Presets.Remove(preset);
                App.Settings.Save();
                RefreshAllUi();
            };

            if (_isCardMode)
            {
                var stack = new StackPanel { Spacing = 8 };
                stack.Children.Add(new TextBlock { Text = preset.Name, FontWeight = FontWeights.SemiBold });
                stack.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", parts),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
                stack.Children.Add(delBtn);
                card.Child = stack;
            }
            else
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel { Spacing = 2 };
                info.Children.Add(new TextBlock { Text = preset.Name, FontWeight = FontWeights.SemiBold });
                info.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", parts),
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
                row.Children.Add(info);
                delBtn.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(delBtn, 1);
                row.Children.Add(delBtn);
                card.Child = row;
            }
            elements.Add(card);
        }

        PresetsHost.SetItems(elements);
    }

    private void ShowStatus(string msg, InfoBarSeverity severity)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Message = msg;
        StatusInfoBar.IsOpen = !string.IsNullOrWhiteSpace(msg);
    }
}
