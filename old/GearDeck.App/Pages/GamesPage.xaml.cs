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

    private sealed class UnifiedLibraryGame
    {
        public required string Name { get; set; }
        public required string Platform { get; set; }
        public required List<string> ProcessNames { get; set; }
        public string? IconPath { get; set; }
        public ScannedGameEntry? Scanned { get; init; }
        public GameAutoRule? Rule { get; set; }
        public bool HasConfiguredTargets => Rule != null && Rule.PerMouseTargets.Values.Any(v => !GameProfileRuleMatcher.ParseTarget(v).IsEmpty);
        public int ConfiguredTargetCount => Rule?.PerMouseTargets.Values.Count(v => !GameProfileRuleMatcher.ParseTarget(v).IsEmpty) ?? 0;
        public bool IsRuleEnabled => Rule is { Enabled: true };
        public string PrimaryExe => ProcessNames.FirstOrDefault() ?? "";
    }

    private static List<ScannedGameEntry>? _cachedGames;
    private List<RunningWindowProcess> _runningProcs = [];
    private string? _browsedExePath;
    private bool _isCardMode;
    private bool _binding;
    private string _activeTab = "games";

    private UnifiedLibraryGame? _activeGame;
    private GameAutoRule? _activeRule;

    public GamesPage()
    {
        InitializeComponent();
        _isCardMode = App.Settings.Current.UseCardLayout;
        ApplyHostLayoutModes();

        ViewSwitchSlot.Children.Add(UiBuilders.CreateViewModeSwitcher(_isCardMode, isCard =>
        {
            _isCardMode = isCard;
            ApplyHostLayoutModes();
            RenderUnifiedGameLibrary();
            RebuildStrategyCards();
            RebuildDeviceQuickCards();
            RebuildPresetsUi();
        }));

        RootShellGrid.SizeChanged += (_, e) =>
        {
            LeftPaneColDef.Width = new GridLength(e.NewSize.Width < 860 ? 228 : 284);
        };

        TabGamesBtn.Click += (_, _) =>
        {
            CloseExclusiveGameDetail();
            SelectTab("games");
        };
        TabFullscreenBtn.Click += (_, _) => SelectTab("fullscreen");
        TabPresetsBtn.Click += (_, _) => SelectTab("presets");

        BackToLibraryBtn.Click += (_, _) => CloseExclusiveGameDetail();
        ExclusiveRuleEnableToggle.Toggled += OnExclusiveEnableToggled;
        ExclusiveProcessBox.LostFocus += OnExclusiveProcessLostFocus;
        ExclusiveRequireFsCheck.Checked += OnExclusiveRequireFsChanged;
        ExclusiveRequireFsCheck.Unchecked += OnExclusiveRequireFsChanged;
        ExclusiveApplyNowBtn.Click += OnExclusiveApplyNow;
        ExclusiveResetBtn.Click += OnExclusiveReset;
        ExclusiveRemoveManualBtn.Click += OnExclusiveRemoveManual;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void ApplyHostLayoutModes()
    {
        ScannedGamesHost.IsCardMode = _isCardMode;
        StrategyCardsHost.IsCardMode = _isCardMode;
        DevicePresetsQuickHost.IsCardMode = _isCardMode;
        PresetsHost.IsCardMode = _isCardMode;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _activeGame = null;
        _activeRule = null;
        SelectTab("games");
        RefreshAllUi();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isCardMode = App.Settings.Current.UseCardLayout;
        ApplyHostLayoutModes();
        App.Devices.DevicesUpdated += OnDevicesUpdated;
        if (App.GameAuto != null)
            App.GameAuto.StatusChanged += OnAutoStatusChanged;

        RefreshRunningProcList();
        RefreshAllUi();

        if (_cachedGames == null)
            _ = ScanGamesInternalAsync(quiet: true);
        else
            RenderUnifiedGameLibrary();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Devices.DevicesUpdated -= OnDevicesUpdated;
        if (App.GameAuto != null)
            App.GameAuto.StatusChanged -= OnAutoStatusChanged;
    }

    private void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> _) => App.UiQueue?.TryEnqueue(RefreshAllUi);
    private void OnAutoStatusChanged() => App.UiQueue?.TryEnqueue(UpdateStatusBanner);

    private void SelectTab(string tabKey)
    {
        _activeTab = tabKey is "fullscreen" or "presets" ? tabKey : "games";

        PanelGames.Visibility = _activeTab == "games" ? Visibility.Visible : Visibility.Collapsed;
        PanelFullscreen.Visibility = _activeTab == "fullscreen" ? Visibility.Visible : Visibility.Collapsed;
        PanelPresets.Visibility = _activeTab == "presets" ? Visibility.Visible : Visibility.Collapsed;

        var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
        var normal = (Style)Application.Current.Resources["DefaultButtonStyle"];
        TabGamesBtn.Style = _activeTab == "games" ? accent : normal;
        TabFullscreenBtn.Style = _activeTab == "fullscreen" ? accent : normal;
        TabPresetsBtn.Style = _activeTab == "presets" ? accent : normal;
    }

    private void RefreshAllUi()
    {
        _binding = true;
        try
        {
            var s = App.Settings.Current;
            AutoSwitchToggle.IsOn = s.AutoSwitchEnabled;
            NotifySwitchToggle.IsOn = s.NotifyOnProfileSwitch;
            FullscreenMasterToggle.IsOn = s.EnableExclusiveFullscreenFallback;

            UpdateStatusBanner();
            RenderUnifiedGameLibrary();
            if (_activeGame != null)
                RenderExclusiveGameDetail();
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

    private async void OnScanInstalledGames(object sender, RoutedEventArgs e)
        => await ScanGamesInternalAsync(quiet: false);

    private async Task ScanGamesInternalAsync(bool quiet)
    {
        ScanGamesBtn.IsEnabled = false;
        ScannedGamesHint.Text = "正在后台扫描 Steam / Epic / Riot / WeGame / HoYoPlay / Battle.net / Xbox 游戏...";
        try
        {
            _cachedGames = await InstalledGameScanner.ScanAsync();
            RenderUnifiedGameLibrary();
            if (!quiet)
                ShowStatus($"扫描完成，共在本机游戏库汇总 {_cachedGames.Count} 款已安装游戏。", InfoBarSeverity.Success);
        }
        finally
        {
            ScanGamesBtn.IsEnabled = true;
        }
    }

    private void OnGameFilterChanged(object sender, TextChangedEventArgs e) => RenderUnifiedGameLibrary();

    private List<UnifiedLibraryGame> BuildUnifiedGameLibrary()
    {
        var list = new List<UnifiedLibraryGame>();
        var rules = App.Settings.Current.GameRules;
        var matchedRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_cachedGames != null)
        {
            foreach (var g in _cachedGames)
            {
                var gameProcSet = g.CandidateExes
                    .Select(GameProfileRuleMatcher.NormalizeProcessName)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var matchingRule = rules.FirstOrDefault(r =>
                    !matchedRuleIds.Contains(r.Id) &&
                    r.ProcessNames.Any(p => gameProcSet.Contains(GameProfileRuleMatcher.NormalizeProcessName(p))));

                if (matchingRule != null)
                    matchedRuleIds.Add(matchingRule.Id);

                var procs = matchingRule != null && matchingRule.ProcessNames.Count > 0
                    ? matchingRule.ProcessNames.ToList()
                    : g.CandidateExes.ToList();

                list.Add(new UnifiedLibraryGame
                {
                    Name = matchingRule != null && !string.IsNullOrWhiteSpace(matchingRule.Name) ? matchingRule.Name : g.Name,
                    Platform = g.Platform,
                    ProcessNames = procs,
                    IconPath = !string.IsNullOrWhiteSpace(g.IconPath) ? g.IconPath : matchingRule?.IconPath,
                    Scanned = g,
                    Rule = matchingRule,
                });
            }
        }

        // 手动添加或暂未扫到安装目录的自定义游戏，置顶放入本机游戏库
        var manualGames = new List<UnifiedLibraryGame>();
        foreach (var r in rules)
        {
            if (matchedRuleIds.Contains(r.Id)) continue;
            manualGames.Add(new UnifiedLibraryGame
            {
                Name = r.Name,
                Platform = string.IsNullOrWhiteSpace(r.Platform) ? "手动添加" : r.Platform,
                ProcessNames = r.ProcessNames.ToList(),
                IconPath = r.IconPath,
                Scanned = null,
                Rule = r,
            });
        }

        var orderedScanned = list
            .OrderByDescending(g => g.IsRuleEnabled && g.HasConfiguredTargets)
            .ThenByDescending(g => g.HasConfiguredTargets || g.IsRuleEnabled)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase);

        var combined = new List<UnifiedLibraryGame>(manualGames.Count + list.Count);
        combined.AddRange(manualGames);
        combined.AddRange(orderedScanned);
        return combined;
    }

    private static void ApplyGameBadge(TextBlock badgeBlock, UnifiedLibraryGame item)
    {
        if (item.IsRuleEnabled && item.HasConfiguredTargets)
        {
            badgeBlock.Text = $"● 已开启 · 优先覆盖全屏策略 ({item.ConfiguredTargetCount} 台设备)";
            badgeBlock.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            badgeBlock.FontWeight = FontWeights.SemiBold;
        }
        else if (item.IsRuleEnabled)
        {
            badgeBlock.Text = "◐ 已开启 · 待点「游戏配置」选预设";
            badgeBlock.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            badgeBlock.FontWeight = FontWeights.SemiBold;
        }
        else if (item.HasConfiguredTargets)
        {
            badgeBlock.Text = $"○ 已关闭（已存 {item.ConfiguredTargetCount} 台设备方案）";
            badgeBlock.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            badgeBlock.FontWeight = FontWeights.Normal;
        }
        else
        {
            badgeBlock.Text = "○ 默认关闭";
            badgeBlock.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            badgeBlock.FontWeight = FontWeights.Normal;
        }
    }

    private void UpdateLibraryHint(IReadOnlyList<UnifiedLibraryGame> all)
    {
        int enabledCount = all.Count(g => g.IsRuleEnabled);
        int configuredCount = all.Count(g => g.HasConfiguredTargets);
        int scannedCount = _cachedGames?.Count ?? 0;
        ScannedGamesHint.Text = $"共收录 {all.Count} 款游戏（扫描 {scannedCount} 款 · 已开启 {enabledCount} 款 · 已配方案 {configuredCount} 款）；开启后将优先于「全屏策略」生效";
    }

    private void RenderUnifiedGameLibrary()
    {
        var all = BuildUnifiedGameLibrary();
        string q = GameFilterBox.Text?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(q)
            ? all
            : all.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                             g.Platform.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                             g.ProcessNames.Any(p => p.Contains(q, StringComparison.OrdinalIgnoreCase)))
                 .ToList();

        UpdateLibraryHint(all);

        var elements = new List<UIElement>(filtered.Count);
        if (filtered.Count == 0)
        {
            elements.Add(new TextBlock
            {
                Text = "未找到匹配的游戏；可直接在上方「手动新增到本机游戏库」添加 .exe 或点击「重新扫描」。",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            ScannedGamesHost.SetItems(elements);
            return;
        }

        bool wasBinding = _binding;
        _binding = true;
        try
        {
            foreach (var item in filtered)
            {
                var iconElem = CreateGameIconElement(item.IconPath, _isCardMode ? 42 : 32);
                var card = new Border
                {
                    Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                };

                var badgeBlock = new TextBlock
                {
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                ApplyGameBadge(badgeBlock, item);

                var enableSwitch = new ToggleSwitch
                {
                    IsOn = item.IsRuleEnabled,
                    OnContent = "已开启",
                    OffContent = "已关闭",
                    MinWidth = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                enableSwitch.Toggled += (_, _) =>
                {
                    if (_binding) return;
                    bool enabled = enableSwitch.IsOn;
                    if (item.Rule == null)
                    {
                        item.Rule = new GameAutoRule
                        {
                            Name = item.Name,
                            Enabled = enabled,
                            Platform = item.Platform,
                            IconPath = item.IconPath,
                            ProcessNames = item.ProcessNames.ToList(),
                            RequireFullscreen = false,
                        };
                        App.Settings.Current.GameRules.Add(item.Rule);
                    }
                    else
                    {
                        item.Rule.Enabled = enabled;
                    }

                    App.Settings.Save();
                    App.GameAuto?.ClearAppliedCache();
                    _ = App.GameAuto?.ForceReevaluateAsync();
                    ApplyGameBadge(badgeBlock, item);
                    UpdateLibraryHint(all);

                    if (enabled)
                    {
                        ShowStatus(
                            item.HasConfiguredTargets
                                ? $"已开启「{item.Name}」单独配置：运行该游戏时将优先覆盖全屏策略。"
                                : $"已开启「{item.Name}」配置开关；请点击「游戏配置」为外设选择预设，设置后将优先覆盖全屏策略。",
                            item.HasConfiguredTargets ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
                    }
                    else
                    {
                        ShowStatus(
                            $"已关闭「{item.Name}」单独配置（若已开启全屏策略，全屏运行时将按全屏策略生效）。",
                            InfoBarSeverity.Informational);
                    }
                };

                var configBtn = new Button
                {
                    Content = "游戏配置",
                    Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                    Padding = new Thickness(10, 5, 10, 5),
                    FontSize = 12,
                };
                configBtn.Click += (_, _) => OpenExclusiveGameDetail(item);

                Button? removeBtn = null;
                if (item.Scanned == null && item.Rule != null)
                {
                    removeBtn = new Button
                    {
                        Content = "移除",
                        Padding = new Thickness(8, 5, 8, 5),
                        FontSize = 12,
                    };
                    var ruleToRemove = item.Rule;
                    removeBtn.Click += (_, _) =>
                    {
                        App.Settings.Current.GameRules.Remove(ruleToRemove);
                        App.Settings.Save();
                        App.GameAuto?.ClearAppliedCache();
                        RefreshAllUi();
                        ShowStatus($"已将「{item.Name}」从本机游戏库移除。", InfoBarSeverity.Informational);
                    };
                }

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
                        Text = item.Name,
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    meta.Children.Add(new TextBlock
                    {
                        Text = $"{item.Platform} · {string.Join(", ", item.ProcessNames)}",
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    });
                    meta.Children.Add(badgeBlock);
                    Grid.SetColumn(meta, 1);
                    top.Children.Add(meta);
                    stack.Children.Add(top);

                    var bottomRow = new Grid { ColumnSpacing = 8 };
                    bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    bottomRow.Children.Add(enableSwitch);

                    var btnRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    btnRow.Children.Add(configBtn);
                    if (removeBtn != null)
                        btnRow.Children.Add(removeBtn);
                    Grid.SetColumn(btnRow, 1);
                    bottomRow.Children.Add(btnRow);
                    stack.Children.Add(bottomRow);
                    card.Child = stack;
                }
                else
                {
                    var row = new Grid { ColumnSpacing = 10 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(iconElem);

                    var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                    meta.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold });
                    meta.Children.Add(new TextBlock
                    {
                        Text = $"{item.Platform} · {string.Join(", ", item.ProcessNames)}",
                        FontSize = 11,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    });
                    Grid.SetColumn(meta, 1);
                    row.Children.Add(meta);

                    Grid.SetColumn(badgeBlock, 2);
                    row.Children.Add(badgeBlock);

                    Grid.SetColumn(enableSwitch, 3);
                    row.Children.Add(enableSwitch);

                    var rightBtns = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    rightBtns.Children.Add(configBtn);
                    if (removeBtn != null)
                        rightBtns.Children.Add(removeBtn);
                    Grid.SetColumn(rightBtns, 4);
                    row.Children.Add(rightBtns);
                    card.Child = row;
                }

                elements.Add(card);
            }

            ScannedGamesHost.SetItems(elements);
        }
        finally
        {
            _binding = wasBinding;
        }
    }

    private void OpenExclusiveGameDetail(UnifiedLibraryGame item)
    {
        _activeGame = item;
        _activeRule = item.Rule ?? new GameAutoRule
        {
            Name = item.Name,
            Enabled = false,
            ProcessNames = item.ProcessNames.ToList(),
            RequireFullscreen = false,
            Platform = item.Platform,
            IconPath = item.IconPath,
        };
        SelectTab("games");
        GamesListView.Visibility = Visibility.Collapsed;
        GameExclusiveDetailView.Visibility = Visibility.Visible;
        RenderExclusiveGameDetail();
    }

    private void CloseExclusiveGameDetail()
    {
        _activeGame = null;
        _activeRule = null;
        GameExclusiveDetailView.Visibility = Visibility.Collapsed;
        GamesListView.Visibility = Visibility.Visible;
        RenderUnifiedGameLibrary();
    }

    private void EnsureActiveRulePersisted()
    {
        if (_activeRule == null) return;
        if (!App.Settings.Current.GameRules.Contains(_activeRule))
        {
            App.Settings.Current.GameRules.Add(_activeRule);
            if (_activeGame != null)
                _activeGame.Rule = _activeRule;
        }
    }

    private void RenderExclusiveGameDetail()
    {
        if (_activeGame == null || _activeRule == null) return;

        bool wasBinding = _binding;
        _binding = true;
        try
        {
            ExclusiveIconHost.Children.Clear();
            ExclusiveIconHost.Children.Add(CreateGameIconElement(_activeGame.IconPath, 40));
            ExclusiveGameTitleText.Text = _activeGame.Name;
            UpdateExclusiveSubText();

            ExclusiveRuleEnableToggle.IsOn = _activeRule.Enabled;
            ExclusiveProcessBox.Text = string.Join(", ", _activeRule.ProcessNames);
            ExclusiveRequireFsCheck.IsChecked = _activeRule.RequireFullscreen;
            ExclusiveRemoveManualBtn.Visibility = (_activeGame.Scanned == null && App.Settings.Current.GameRules.Contains(_activeRule))
                ? Visibility.Visible
                : Visibility.Collapsed;

            ExclusiveTargetsPanel.Children.Clear();
            var devs = CollectTunableEntries(_activeRule.PerMouseTargets);
            if (devs.Count == 0)
            {
                ExclusiveTargetsPanel.Children.Add(new TextBlock
                {
                    Text = "当前未检测到鼠标或手柄设备；接入外设后可在此按设备分配专属配置。",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
            }
            else
            {
                foreach (var (deviceId, dev, displayName, _) in devs)
                {
                    var options = BuildTargetOptionsForDevice(deviceId, dev, isGameExclusive: true);
                    ExclusiveTargetsPanel.Children.Add(CreateDeviceTargetRow(
                        deviceId,
                        displayName,
                        options,
                        _activeRule.PerMouseTargets,
                        isEnabled: true,
                        onBeforeSave: () =>
                        {
                            EnsureActiveRulePersisted();
                            if (_activeGame != null)
                                _activeGame.ProcessNames = _activeRule.ProcessNames.ToList();
                        },
                        onAfterSave: () =>
                        {
                            UpdateExclusiveSubText();
                            if (_activeGame != null && _activeRule != null &&
                                !_activeRule.Enabled &&
                                _activeRule.PerMouseTargets.Values.Any(v => !GameProfileRuleMatcher.ParseTarget(v).IsEmpty))
                            {
                                ShowStatus($"已保存「{_activeGame.Name}」的外设配置；当前该游戏开关为「已关闭」，开启右上角或总览卡片开关后即可优先覆盖全屏策略。", InfoBarSeverity.Informational);
                            }
                        }));
                }
            }
        }
        finally
        {
            _binding = wasBinding;
        }
    }

    private void UpdateExclusiveSubText()
    {
        if (_activeGame == null || _activeRule == null) return;
        int count = _activeRule.PerMouseTargets.Values.Count(v => !GameProfileRuleMatcher.ParseTarget(v).IsEmpty);
        string toggleState = _activeRule.Enabled
            ? "规则已开启（优先覆盖全屏策略）"
            : "规则已关闭（开启右上角或总览卡片开关后生效）";
        string state = count > 0
            ? $"已配置 {count} 台设备 · {toggleState}"
            : $"尚未指派专属预设 · {toggleState}";
        ExclusiveGameSubText.Text = $"{_activeGame.Platform} · {state}";
    }

    private void OnExclusiveEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_binding || _activeRule == null) return;
        EnsureActiveRulePersisted();
        _activeRule.Enabled = ExclusiveRuleEnableToggle.IsOn;
        App.Settings.Save();
        App.GameAuto?.ClearAppliedCache();
        _ = App.GameAuto?.ForceReevaluateAsync();
        UpdateExclusiveSubText();
        if (_activeGame != null)
        {
            if (_activeRule.Enabled)
            {
                ShowStatus(
                    _activeGame.HasConfiguredTargets
                        ? $"已开启「{_activeGame.Name}」单独配置：运行该游戏时将优先覆盖全屏策略。"
                        : $"已开启「{_activeGame.Name}」单独配置开关；请为下方外设选择目标预设，设置后将优先覆盖全屏策略。",
                    _activeGame.HasConfiguredTargets ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
            }
            else
            {
                ShowStatus($"已关闭「{_activeGame.Name}」单独配置（全屏运行时将回退由全屏策略控制）。", InfoBarSeverity.Informational);
            }
        }
    }

    private void OnExclusiveProcessLostFocus(object sender, RoutedEventArgs e)
    {
        if (_binding || _activeRule == null) return;
        var parsed = ExclusiveProcessBox.Text
            .Split([',', ';', '，', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (parsed.Count == 0)
        {
            ExclusiveProcessBox.Text = string.Join(", ", _activeRule.ProcessNames);
            return;
        }
        EnsureActiveRulePersisted();
        _activeRule.ProcessNames = parsed;
        if (_activeGame != null)
            _activeGame.ProcessNames = parsed;
        App.Settings.Save();
        App.GameAuto?.ClearAppliedCache();
    }

    private void OnExclusiveRequireFsChanged(object sender, RoutedEventArgs e)
    {
        if (_binding || _activeRule == null) return;
        EnsureActiveRulePersisted();
        _activeRule.RequireFullscreen = ExclusiveRequireFsCheck.IsChecked == true;
        App.Settings.Save();
        App.GameAuto?.ClearAppliedCache();
    }

    private async void OnExclusiveApplyNow(object sender, RoutedEventArgs e)
    {
        if (_activeRule == null || App.GameAuto == null) return;
        if (!_activeRule.PerMouseTargets.Values.Any(v => !GameProfileRuleMatcher.ParseTarget(v).IsEmpty))
        {
            ShowStatus("请先在上方至少为一台外设选择目标板载槽位、预设或保持当前不变。", InfoBarSeverity.Warning);
            return;
        }
        var btn = sender as Button;
        if (btn != null) btn.IsEnabled = false;
        try
        {
            EnsureActiveRulePersisted();
            App.Settings.Save();
            await App.GameAuto.ApplyRuleManuallyAsync(_activeRule);
            UpdateStatusBanner();
            ShowStatus($"已向在线设备套用「{_activeRule.Name}」专属配置。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"套用游戏配置失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
    }

    private void OnExclusiveReset(object sender, RoutedEventArgs e)
    {
        if (_activeGame == null || _activeRule == null) return;
        _activeRule.PerMouseTargets.Clear();
        _activeRule.RequireFullscreen = false;
        _activeRule.Enabled = false;
        if (_activeGame.Scanned != null)
        {
            App.Settings.Current.GameRules.Remove(_activeRule);
            _activeGame.Rule = null;
            _activeRule = new GameAutoRule
            {
                Name = _activeGame.Name,
                Enabled = false,
                ProcessNames = _activeGame.ProcessNames.ToList(),
                Platform = _activeGame.Platform,
                IconPath = _activeGame.IconPath,
            };
        }
        App.Settings.Save();
        App.GameAuto?.ClearAppliedCache();
        RenderExclusiveGameDetail();
        ShowStatus($"已清空「{_activeGame.Name}」的专属外设配置。", InfoBarSeverity.Informational);
    }

    private void OnExclusiveRemoveManual(object sender, RoutedEventArgs e)
    {
        if (_activeGame == null || _activeRule == null || _activeGame.Scanned != null) return;
        string removedName = _activeGame.Name;
        App.Settings.Current.GameRules.Remove(_activeRule);
        App.Settings.Save();
        App.GameAuto?.ClearAppliedCache();
        CloseExclusiveGameDetail();
        ShowStatus($"已将「{removedName}」从本机游戏库移除。", InfoBarSeverity.Informational);
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
        ForegroundMonitorText.Text = $"前台进程：{proc} [{fs}]{title}";
        ActiveRuleStatusText.Text = $"生效策略：{g.ActiveRuleLabel}";

        if (g.LastOutcomeByDevice.Count == 0)
        {
            DeviceOutcomeText.Text = "暂无切换记录（命中规则/全屏且外设唤醒时执行）。";
        }
        else
        {
            var devNames = App.LastDevices.ToDictionary(d => d.Id, d => d.Name, StringComparer.OrdinalIgnoreCase);
            DeviceOutcomeText.Text = string.Join(" · ",
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
        _browsedExePath = null;
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
                _browsedExePath = file.Path;
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

    private void OnAddManualGame(object sender, RoutedEventArgs e)
    {
        var procs = NewRuleProcessBox.Text
            .Split([',', ';', '，', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (procs.Count == 0)
        {
            ShowStatus("请先填写至少一个进程名（如 cs2.exe），或从运行列表 / 浏览 .exe 选择。", InfoBarSeverity.Warning);
            return;
        }

        string name = string.IsNullOrWhiteSpace(NewRuleNameBox.Text)
            ? Path.GetFileNameWithoutExtension(procs[0])
            : NewRuleNameBox.Text.Trim();
        string? iconPath = InstalledGameScanner.ExtractExeIcon(_browsedExePath);

        var normNew = procs.Select(GameProfileRuleMatcher.NormalizeProcessName)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existing = App.Settings.Current.GameRules.FirstOrDefault(r =>
            r.ProcessNames.Any(p => normNew.Contains(GameProfileRuleMatcher.NormalizeProcessName(p))));

        if (existing != null)
        {
            foreach (string p in procs)
            {
                if (!existing.ProcessNames.Any(ep => string.Equals(ep, p, StringComparison.OrdinalIgnoreCase)))
                    existing.ProcessNames.Add(p);
            }
            if (!string.IsNullOrWhiteSpace(iconPath))
                existing.IconPath = iconPath;
            App.Settings.Save();
            ShowStatus($"游戏「{existing.Name}」已在游戏库中，已同步补充进程列表；点击「游戏配置」即可修改专属方案。", InfoBarSeverity.Informational);
        }
        else
        {
            App.Settings.Current.GameRules.Add(new GameAutoRule
            {
                Name = name,
                Enabled = false,
                Platform = "手动添加",
                IconPath = iconPath,
                ProcessNames = procs,
                RequireFullscreen = false,
            });
            App.Settings.Save();
            ShowStatus($"已将「{name}」加入本机游戏库（默认关闭）；点击「游戏配置」指派预设并在卡片开启开关后将优先覆盖全屏策略。", InfoBarSeverity.Success);
        }

        NewRuleNameBox.Text = "";
        NewRuleProcessBox.Text = "";
        _browsedExePath = null;
        RenderUnifiedGameLibrary();
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
        var btn = sender as Button;
        if (btn != null) btn.IsEnabled = false;
        try
        {
            await App.GameAuto.ForceReevaluateAsync();
            UpdateStatusBanner();
            ShowStatus("已完成前台窗口检测并按策略向已唤醒外设执行对应配置。", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowStatus($"重新检测前台窗口失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
    }

    private List<TargetOption> BuildTargetOptionsForDevice(
        string deviceId,
        PeripheralDevice? dev,
        bool isExitRestore = false,
        bool isGameExclusive = false)
    {
        var list = isExitRestore
            ? new List<TargetOption>
            {
                new("", "回退开启前的模式（默认）"),
                new(GameProfileRuleMatcher.KeepCurrentTargetId, "保持切换后不变（不回退）"),
            }
            : isGameExclusive
                ? new List<TargetOption>
                {
                    new("", "未设置"),
                    new(GameProfileRuleMatcher.KeepCurrentTargetId, "保持当前不变（覆盖全屏策略，不切换）"),
                }
                : new List<TargetOption>
                {
                    new("", "保持当前不变"),
                };

        var onboardSlots = App.Settings.GetDeviceOnboardSlots(dev, deviceId);
        foreach (var s in onboardSlots)
        {
            string label = string.IsNullOrWhiteSpace(s.Summary)
                ? $"[厂家板载] {s.Name}"
                : $"[厂家板载] {s.Name} ({s.Summary})";
            list.Add(new TargetOption($"slot:{s.SlotIndex}", label));
        }

        var ownPresets = App.Settings.Current.Presets
            .Where(p => string.Equals(p.SourceDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var preset in ownPresets)
            list.Add(new TargetOption($"preset:{preset.Id}", $"[本设备预设] {preset.Name} ({FormatPresetSummary(preset)})"));

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

    private StackPanel BuildPerMouseTargetPanel(Dictionary<string, string> targetMap, bool isEnabled = true, bool isExitRestore = false)
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
            var options = BuildTargetOptionsForDevice(deviceId, dev, isExitRestore);
            panel.Children.Add(CreateDeviceTargetRow(deviceId, displayName, options, targetMap, isEnabled));
        }
        return panel;
    }

    private Grid CreateDeviceTargetRow(
        string deviceId,
        string displayName,
        List<TargetOption> options,
        Dictionary<string, string> targetMap,
        bool isEnabled = true,
        Action? onBeforeSave = null,
        Action? onAfterSave = null)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });

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
                onBeforeSave?.Invoke();
                if (string.IsNullOrEmpty(opt.Value))
                    targetMap.Remove(deviceId);
                else
                    targetMap[deviceId] = opt.Value;
                App.Settings.Save();
                App.GameAuto?.ClearAppliedCache();
                onAfterSave?.Invoke();
            }
        };

        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        return row;
    }

    private void RebuildStrategyCards()
    {
        var s = App.Settings.Current;
        bool wasBinding = _binding;
        _binding = true;
        try
        {
            bool fsEnabled = s.EnableExclusiveFullscreenFallback;

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
                    ? "当未命中游戏专属规则的全屏应用处于独占全屏时触发；支持选择检测方式与各外设目标预设。"
                    : "请先开启上方「全局独占策略总开关」，开启后才可编辑下方规则并在进入全屏时自动切换。",
                fsCombo,
                BuildPerMouseTargetPanel(s.FullscreenFallbackTargets, isEnabled: fsEnabled, isExitRestore: false));
            card1.Opacity = fsEnabled ? 1.0 : 0.48;
            card1.IsHitTestVisible = fsEnabled;

            var exitCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = fsEnabled,
            };
            exitCombo.Items.Add(new ComboBoxItem { Content = "等待全屏窗口 / 游戏进程完全退出后再恢复默认（Alt+Tab 不切回）", Tag = ExitRestoreMode.WhenWindowExits });
            exitCombo.Items.Add(new ComboBoxItem { Content = "离开游戏 / 全屏窗口焦点 1.5 秒后立即恢复默认", Tag = ExitRestoreMode.WhenFocusLeaves });
            exitCombo.Items.Add(new ComboBoxItem { Content = "退出后不自动恢复（始终保留切换后的配置）", Tag = ExitRestoreMode.NeverRestore });
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
                "2. 退出全屏 / 游戏恢复策略" + (fsEnabled ? "" : "（未开启总开关）"),
                fsEnabled
                    ? "默认将在退出全屏或游戏窗口后自动「回退开启前的模式」，也可按设备单独指定固定的桌面槽位或预设。"
                    : "需先开启上方「全局独占策略总开关」方可编辑并在退出全屏后生效。",
                exitCombo,
                BuildPerMouseTargetPanel(s.DesktopDefaultTargets, isEnabled: fsEnabled, isExitRestore: true));
            card2.Opacity = fsEnabled ? 1.0 : 0.48;
            card2.IsHitTestVisible = fsEnabled;

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
                        ? $"已读取 {d.Name} 的 {manifest.Slots.Count} 个厂家板载配置并更新到游戏配置选项。"
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
            ShowStatus($"已将预设「{preset.Name}」保存到 {dev.Name}，并已加入游戏专属配置下拉选单。", InfoBarSeverity.Success);
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
                Text = "暂无已保存的硬件预设（可在各设备页面顶部“保存预设”，也可在此处抓取）。",
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
