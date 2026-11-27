using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using GearDeck.Core;
using GearDeck.Core.Models;
using GearDeck.Desktop.Controls;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Views;

/// <summary>
/// Avalonia 版「游戏与配置切换」页：本机游戏库 / 全屏独占与退出恢复 / 硬件参数预设。
/// 与 App.Settings、App.GameAuto 热联动，规则与设备目标变更后即时落盘并触发重新评估。
/// </summary>
public partial class GamesView : UserControl
{
    private sealed record TargetOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class UnifiedLibraryGame
    {
        public required string Name { get; set; }
        public required string Platform { get; set; }
        public required List<string> ProcessNames { get; set; }
        public string? IconPath { get; set; }
        public ScannedGameEntry? Scanned { get; init; }
        public GameAutoRule? Rule { get; set; }
        public bool HasConfiguredTargets => Rule?.PerMouseTargets != null &&
            Rule.PerMouseTargets.Values.Any(v => !GameProfileRuleMatcher.ParseTarget(v).IsEmpty);
        public int ConfiguredTargetCount => Rule?.PerMouseTargets?.Values.Count(v => !GameProfileRuleMatcher.ParseTarget(v).IsEmpty) ?? 0;
        public bool IsRuleEnabled => Rule is { Enabled: true };
    }

    private static List<ScannedGameEntry>? _cachedGames;
    private readonly Dictionary<string, string?> _fgIconCache = new(StringComparer.OrdinalIgnoreCase);
    private List<RunningWindowProcess> _runningProcs = [];
    private UniformGrid? _gamesGrid;
    private UniformGrid? _quickCardsGrid;
    private UniformGrid? _presetsGrid;
    private string? _browsedExePath;
    private bool _isCardMode;
    private bool _binding;
    private string _activeTab = "games";

    // async void / 后台任务的防重入标记
    private bool _scanning;
    private bool _browsing;
    private bool _applying;
    private bool _forceEvaluating;
    private bool _capturing;
    private bool _quickCapturing;
    private bool _probing;
    private bool _dialogOpen;

    public GamesView()
    {
        _binding = true;
        InitializeComponent();
        _isCardMode = App.Settings.Current.UseCardLayout;

        ViewSwitchSlot.Children.Add(UiBuilders.CreateViewModeSwitcher(_isCardMode, isCard =>
        {
            _isCardMode = isCard;
            RenderUnifiedGameLibrary();
            RebuildStrategyCards();
            RebuildDeviceQuickCards();
            RebuildPresetsUi();
        }));

        WireEvents();
        ScannedGamesHost.SizeChanged += (_, _) => UpdateGameLibraryColumns();
        DevicePresetsQuickHost.SizeChanged += (_, _) => UpdateUniformColumns(_quickCardsGrid, DevicePresetsQuickHost);
        PresetsHost.SizeChanged += (_, _) => UpdateUniformColumns(_presetsGrid, PresetsHost);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _binding = false;
    }

    private static int ComputeRightPaneColumns(Panel host)
    {
        double w = host.Bounds.Width;
        if (w <= 1) return 2;
        return w >= 620 ? 2 : 1;
    }

    private static int ComputeGameLibraryColumns(Panel host)
    {
        double w = host.Bounds.Width;
        if (w <= 1) return 4;
        if (w >= 1060) return 5;
        if (w >= 820) return 4;
        if (w >= 580) return 3;
        if (w >= 380) return 2;
        return 1;
    }

    private void UpdateGameLibraryColumns()
    {
        if (_gamesGrid == null) return;
        int cols = ComputeGameLibraryColumns(ScannedGamesHost);
        if (_gamesGrid.Columns != cols)
            _gamesGrid.Columns = cols;
    }

    private static void UpdateUniformColumns(UniformGrid? grid, Panel host)
    {
        if (grid == null) return;
        int cols = ComputeRightPaneColumns(host);
        if (grid.Columns != cols)
            grid.Columns = cols;
    }

    private void WireEvents()
    {
        TabGamesBtn.Click += (_, _) => SelectTab("games");
        TabFullscreenBtn.Click += (_, _) => SelectTab("fullscreen");
        TabPresetsBtn.Click += (_, _) => SelectTab("presets");
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshRunningProcList();
        RefreshAllUi();
        App.Devices.DevicesUpdated += OnDevicesUpdated;
        if (App.GameAuto != null)
            App.GameAuto.StatusChanged += OnAutoStatusChanged;

        if (_cachedGames == null)
            _ = ScanGamesInternalAsync(quiet: true);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        App.Devices.DevicesUpdated -= OnDevicesUpdated;
        if (App.GameAuto != null)
            App.GameAuto.StatusChanged -= OnAutoStatusChanged;
    }

    private void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> _) => App.UiPost(RefreshAllUi);
    private void OnAutoStatusChanged() => App.UiPost(UpdateStatusBanner);

    private void SelectTab(string tabKey)
    {
        _activeTab = tabKey is "fullscreen" or "presets" ? tabKey : "games";
        PanelGames.IsVisible = _activeTab == "games";
        PanelFullscreen.IsVisible = _activeTab == "fullscreen";
        PanelPresets.IsVisible = _activeTab == "presets";

        TabGamesBtn.Classes.Set("accent", _activeTab == "games");
        TabFullscreenBtn.Classes.Set("accent", _activeTab == "fullscreen");
        TabPresetsBtn.Classes.Set("accent", _activeTab == "presets");
    }

    private void RefreshAllUi()
    {
        _binding = true;
        try
        {
            var s = App.Settings.Current;
            AutoSwitchToggle.IsChecked = s.AutoSwitchEnabled;
            NotifySwitchToggle.IsChecked = s.NotifyOnProfileSwitch;
            FullscreenMasterToggle.IsChecked = s.EnableExclusiveFullscreenFallback;

            UpdateStatusBanner();
            RenderUnifiedGameLibrary();
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

    // ── 游戏库扫描 / 过滤 ────────────────────────────────────────────────────

    private async void OnScanInstalledGames(object? sender, RoutedEventArgs e)
    {
        if (_scanning) return;
        try
        {
            await ScanGamesInternalAsync(quiet: false);
        }
        catch (Exception ex)
        {
            ShowStatus($"扫描游戏失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task ScanGamesInternalAsync(bool quiet)
    {
        if (_scanning) return;
        _scanning = true;
        ScanGamesBtn.IsEnabled = false;
        ScannedGamesHint.Text = "正在后台扫描 Steam / Epic / Riot / WeGame / HoYoPlay / Battle.net / Xbox 游戏...";
        try
        {
            _cachedGames = await InstalledGameScanner.ScanAsync();
            RenderUnifiedGameLibrary();
            if (!quiet)
                ShowStatus($"扫描完成，共在本机游戏库汇总 {_cachedGames.Count} 款已安装游戏。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            if (!quiet)
                ShowStatus($"扫描游戏失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            ScanGamesBtn.IsEnabled = true;
            _scanning = false;
        }
    }

    private void OnGameFilterChanged(object? sender, TextChangedEventArgs e) => RenderUnifiedGameLibrary();

    private List<UnifiedLibraryGame> BuildUnifiedGameLibrary()
    {
        var list = new List<UnifiedLibraryGame>();
        var rules = App.Settings.Current.GameRules;
        var matchedRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool backfilledIcons = false;

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
                {
                    matchedRuleIds.Add(matchingRule.Id);
                    if (string.IsNullOrWhiteSpace(matchingRule.IconPath) && !string.IsNullOrWhiteSpace(g.IconPath))
                    {
                        matchingRule.IconPath = g.IconPath;
                        backfilledIcons = true;
                    }
                }

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

        if (backfilledIcons)
            App.Settings.Save();

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
            badgeBlock.Text = $"● 已开启 · {item.ConfiguredTargetCount} 台设备";
            badgeBlock.Foreground = BrushRes("AccentTextFillColorPrimaryBrush");
            badgeBlock.FontWeight = FontWeight.SemiBold;
        }
        else if (item.IsRuleEnabled)
        {
            badgeBlock.Text = "◐ 已开启 · 点此配预设";
            badgeBlock.Foreground = BrushRes("AccentTextFillColorPrimaryBrush");
            badgeBlock.FontWeight = FontWeight.SemiBold;
        }
        else if (item.HasConfiguredTargets)
        {
            badgeBlock.Text = $"○ 已关闭 · 已存 {item.ConfiguredTargetCount} 台";
            badgeBlock.Foreground = BrushRes("TextFillColorSecondaryBrush");
            badgeBlock.FontWeight = FontWeight.Normal;
        }
        else
        {
            badgeBlock.Text = "○ 未配置";
            badgeBlock.Foreground = BrushRes("TextFillColorSecondaryBrush");
            badgeBlock.FontWeight = FontWeight.Normal;
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
        ScannedGamesHost.Children.Clear();

        if (filtered.Count == 0)
        {
            ScannedGamesHost.Children.Add(new TextBlock
            {
                Text = "未找到匹配的游戏；可直接在上方「手动新增到本机游戏库」添加 .exe 或点击「重新扫描」。",
                FontSize = 12,
                Foreground = BrushRes("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        bool wasBinding = _binding;
        _binding = true;
        try
        {
            Panel host;
            if (_isCardMode)
            {
                _gamesGrid = new UniformGrid
                {
                    Columns = ComputeGameLibraryColumns(ScannedGamesHost),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                host = _gamesGrid;
            }
            else
            {
                _gamesGrid = null;
                host = new StackPanel { Spacing = 8 };
            }

            foreach (var item in filtered)
            {
                var iconElem = CreateGameIconElement(item.IconPath, _isCardMode ? 40 : 32);
                var card = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = _isCardMode ? new Thickness(0, 0, 8, 8) : new Thickness(0),
                    Background = BrushRes("SubtleFillColorSecondaryBrush"),
                    BorderBrush = BrushRes("CardStrokeColorDefaultBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = _isCardMode ? new Thickness(10) : new Thickness(12, 10),
                    Cursor = new Cursor(StandardCursorType.Hand),
                };

                var badgeBlock = new TextBlock
                {
                    FontSize = 11,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                ApplyGameBadge(badgeBlock, item);

                var enableSwitch = new ToggleSwitch
                {
                    IsChecked = item.IsRuleEnabled,
                    OnContent = _isCardMode ? "开" : "已开启",
                    OffContent = _isCardMode ? "关" : "已关闭",
                    MinWidth = 0,
                };
                enableSwitch.IsCheckedChanged += (_, _) =>
                {
                    if (_binding) return;
                    try
                    {
                        bool enabled = enableSwitch.IsChecked == true;
                        App.Settings.Current.GameRules ??= [];
                        item.ProcessNames ??= [];
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
                                PerMouseTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                            };
                            App.Settings.Current.GameRules.Add(item.Rule);
                        }
                        else
                        {
                            item.Rule.Enabled = enabled;
                            item.Rule.ProcessNames ??= item.ProcessNames.ToList();
                            item.Rule.PerMouseTargets ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        }

                        App.Settings.Save();
                        App.GameAuto?.ClearAppliedCache();
                        _ = App.GameAuto?.ForceReevaluateAsync();
                        ApplyGameBadge(badgeBlock, item);
                        UpdateLibraryHint(all);

                        ShowStatus(
                            enabled
                                ? item.HasConfiguredTargets
                                    ? $"已开启「{item.Name}」单独配置：运行该游戏时将优先覆盖全屏策略。"
                                    : $"已开启「{item.Name}」开关；点击卡片可为外设指派预设。"
                                : $"已关闭「{item.Name}」单独配置。",
                            enabled && item.HasConfiguredTargets ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
                    }
                    catch (Exception ex)
                    {
                        Diag.Log($"Toggle game rule error ({item.Name}): {ex.Message}");
                        ShowStatus($"切换游戏规则失败：{ex.Message}", InfoBarSeverity.Error);
                    }
                };

                var configBtn = new Button
                {
                    Content = "配置",
                    Classes = { "accent" },
                    Padding = new Thickness(9, 4, 9, 4),
                    FontSize = 12,
                };
                configBtn.Click += async (_, _) => await OpenGameConfigDialogAsync(item);

                Button? removeBtn = null;
                if (item.Scanned == null && item.Rule != null)
                {
                    removeBtn = new Button
                    {
                        Content = "移除",
                        Padding = new Thickness(8, 4, 8, 4),
                        FontSize = 12,
                    };
                    var ruleToRemove = item.Rule;
                    removeBtn.Click += (_, _) =>
                    {
                        App.Settings.Current.GameRules?.Remove(ruleToRemove);
                        App.Settings.Save();
                        App.GameAuto?.ClearAppliedCache();
                        RefreshAllUi();
                        ShowStatus($"已将「{item.Name}」从本机游戏库移除。", InfoBarSeverity.Informational);
                    };
                }

                var actionRow = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 5,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                actionRow.Children.Add(configBtn);
                if (removeBtn != null)
                    actionRow.Children.Add(removeBtn);

                if (_isCardMode)
                {
                    var stack = new StackPanel { Spacing = 8 };
                    var top = new Grid();
                    top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    top.Children.Add(iconElem);

                    var meta = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                    meta.Children.Add(new TextBlock
                    {
                        Text = item.Name,
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    meta.Children.Add(new TextBlock
                    {
                        Text = item.Platform,
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = BrushRes("TextFillColorSecondaryBrush"),
                    });
                    Grid.SetColumn(meta, 1);
                    top.Children.Add(meta);
                    stack.Children.Add(top);

                    stack.Children.Add(badgeBlock);

                    var bottomRow = new Grid();
                    bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    bottomRow.Children.Add(enableSwitch);
                    Grid.SetColumn(actionRow, 1);
                    bottomRow.Children.Add(actionRow);
                    stack.Children.Add(bottomRow);
                    card.Child = stack;
                }
                else
                {
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(iconElem);

                    var meta = new StackPanel { Spacing = 1, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                    meta.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    meta.Children.Add(new TextBlock
                    {
                        Text = item.Platform,
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = BrushRes("TextFillColorSecondaryBrush"),
                    });
                    Grid.SetColumn(meta, 1);
                    row.Children.Add(meta);

                    badgeBlock.Margin = new Thickness(10, 0, 0, 0);
                    Grid.SetColumn(badgeBlock, 2);
                    row.Children.Add(badgeBlock);

                    enableSwitch.Margin = new Thickness(10, 0, 0, 0);
                    Grid.SetColumn(enableSwitch, 3);
                    row.Children.Add(enableSwitch);

                    Grid.SetColumn(actionRow, 4);
                    row.Children.Add(actionRow);
                    card.Child = row;
                }

                card.Tapped += async (_, e) =>
                {
                    if (!IsClickInsideInteractiveControl(e.Source))
                        await OpenGameConfigDialogAsync(item);
                };

                host.Children.Add(card);
            }

            ScannedGamesHost.Children.Add(host);
        }
        finally
        {
            _binding = wasBinding;
        }
    }

    private static bool IsClickInsideInteractiveControl(object? source)
    {
        for (Visual? cur = source as Visual; cur != null; cur = cur.GetVisualParent())
        {
            if (cur is Button or ToggleSwitch)
                return true;
        }
        return false;
    }

    // ── 单游戏专属配置弹窗 ───────────────────────────────────────────────────

    private async Task OpenGameConfigDialogAsync(UnifiedLibraryGame item)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            App.Settings.Current.GameRules ??= [];
            item.ProcessNames ??= [];

            var rule = item.Rule ?? new GameAutoRule
            {
                Name = item.Name,
                Enabled = false,
                ProcessNames = item.ProcessNames.ToList(),
                RequireFullscreen = false,
                Platform = item.Platform,
                IconPath = item.IconPath,
                PerMouseTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            };
            rule.ProcessNames ??= item.ProcessNames.ToList();
            rule.PerMouseTargets ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void EnsurePersisted()
            {
                App.Settings.Current.GameRules ??= [];
                if (!App.Settings.Current.GameRules.Contains(rule))
                {
                    App.Settings.Current.GameRules.Add(rule);
                    item.Rule = rule;
                }
            }

            var subTextBlock = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = BrushRes("TextFillColorSecondaryBrush"),
            };

            void RefreshSubText()
            {
                int count = rule.PerMouseTargets.Values.Count(v => !GameProfileRuleMatcher.ParseTarget(v).IsEmpty);
                string toggleState = rule.Enabled
                    ? "规则已开启（优先覆盖全屏策略）"
                    : "规则已关闭（开启开关后生效）";
                string state = count > 0 ? $"已配置 {count} 台设备 · {toggleState}" : $"尚未指派专属预设 · {toggleState}";
                subTextBlock.Text = $"{item.Platform} · {state}";
            }
            RefreshSubText();

            var enableToggle = new ToggleSwitch
            {
                IsChecked = rule.Enabled,
                OnContent = "已开启",
                OffContent = "已关闭",
                VerticalAlignment = VerticalAlignment.Center,
            };

            var processBox = new TextBox
            {
                Text = string.Join(", ", rule.ProcessNames),
                Watermark = "匹配进程名（多个可用逗号分隔，如 cs2.exe）",
            };

            var requireFsCheck = new CheckBox
            {
                Content = "仅当该游戏处于全屏 / 独占全屏状态时触发",
                IsChecked = rule.RequireFullscreen,
            };

            var targetsPanel = new StackPanel { Spacing = 10 };

            void RebuildTargetRows()
            {
                targetsPanel.Children.Clear();
                var devs = CollectTunableEntries(rule.PerMouseTargets);
                if (devs.Count == 0)
                {
                    targetsPanel.Children.Add(new TextBlock
                    {
                        Text = "当前未检测到鼠标或手柄设备；接入外设后可在此按设备分配专属配置。",
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = BrushRes("TextFillColorSecondaryBrush"),
                    });
                    return;
                }

                foreach (var (deviceId, dev, displayName, _) in devs)
                {
                    var options = BuildTargetOptionsForDevice(deviceId, dev, isGameExclusive: true);
                    targetsPanel.Children.Add(CreateDeviceTargetRow(
                        deviceId,
                        displayName,
                        options,
                        rule.PerMouseTargets,
                        isEnabled: true,
                        onBeforeSave: () =>
                        {
                            EnsurePersisted();
                            item.ProcessNames = rule.ProcessNames.ToList();
                        },
                        onAfterSave: RefreshSubText,
                        stacked: true));
                }
            }
            RebuildTargetRows();

            var dialog = new ContentDialog
            {
                Title = item.Name,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 720.0;

            void CommitProcesses()
            {
                var parsed = SplitProcessNames(processBox.Text);
                if (parsed.Count == 0)
                {
                    processBox.Text = string.Join(", ", rule.ProcessNames);
                    return;
                }
                EnsurePersisted();
                rule.ProcessNames = parsed;
                item.ProcessNames = parsed;
                App.Settings.Save();
                App.GameAuto?.ClearAppliedCache();
            }

            bool suppressing = false;
            enableToggle.IsCheckedChanged += (_, _) =>
            {
                if (suppressing) return;
                try
                {
                    EnsurePersisted();
                    rule.Enabled = enableToggle.IsChecked == true;
                    App.Settings.Save();
                    App.GameAuto?.ClearAppliedCache();
                    _ = App.GameAuto?.ForceReevaluateAsync();
                    RefreshSubText();
                }
                catch (Exception ex)
                {
                    Diag.Log($"Dialog enable toggle error: {ex.Message}");
                    ShowStatus($"切换规则状态失败：{ex.Message}", InfoBarSeverity.Error);
                }
            };

            processBox.LostFocus += (_, _) => CommitProcesses();

            requireFsCheck.IsCheckedChanged += (_, _) =>
            {
                if (suppressing) return;
                EnsurePersisted();
                rule.RequireFullscreen = requireFsCheck.IsChecked == true;
                App.Settings.Save();
                App.GameAuto?.ClearAppliedCache();
            };

            var applyBtn = new Button
            {
                Content = "立即套用此游戏配置",
                Classes = { "accent" },
                Margin = new Thickness(0, 0, 8, 4),
            };
            applyBtn.Click += async (_, _) =>
            {
                if (_applying || App.GameAuto == null) return;
                if (!rule.PerMouseTargets.Values.Any(v => !GameProfileRuleMatcher.ParseTarget(v).IsEmpty))
                {
                    ShowStatus("请先至少为一台外设选择目标板载槽位或预设。", InfoBarSeverity.Warning);
                    return;
                }
                _applying = true;
                applyBtn.IsEnabled = false;
                try
                {
                    EnsurePersisted();
                    App.Settings.Save();
                    await App.GameAuto.ApplyRuleManuallyAsync(rule);
                    UpdateStatusBanner();
                    ShowStatus($"已向在线设备套用「{rule.Name}」专属配置。", InfoBarSeverity.Success);
                }
                catch (Exception ex)
                {
                    ShowStatus($"套用游戏配置失败：{ex.Message}", InfoBarSeverity.Error);
                }
                finally
                {
                    applyBtn.IsEnabled = true;
                    _applying = false;
                }
            };

            var resetBtn = new Button
            {
                Content = "清空专属配置",
                Margin = new Thickness(0, 0, 8, 4),
            };
            resetBtn.Click += (_, _) =>
            {
                suppressing = true;
                try
                {
                    rule.PerMouseTargets.Clear();
                    rule.RequireFullscreen = false;
                    rule.Enabled = false;
                    enableToggle.IsChecked = false;
                    requireFsCheck.IsChecked = false;
                    if (item.Scanned != null)
                    {
                        App.Settings.Current.GameRules.Remove(rule);
                        item.Rule = null;
                    }
                    App.Settings.Save();
                    App.GameAuto?.ClearAppliedCache();
                    RefreshSubText();
                    RebuildTargetRows();
                    ShowStatus($"已清空「{item.Name}」的专属外设配置。", InfoBarSeverity.Informational);
                }
                finally { suppressing = false; }
            };

            var removeManualBtn = new Button
            {
                Content = "从游戏库移除",
                IsVisible = item.Scanned == null && App.Settings.Current.GameRules.Contains(rule),
                Margin = new Thickness(0, 0, 8, 4),
            };
            removeManualBtn.Click += (_, _) =>
            {
                App.Settings.Current.GameRules.Remove(rule);
                item.Rule = null;
                App.Settings.Save();
                App.GameAuto?.ClearAppliedCache();
                dialog.Hide();
                ShowStatus($"已将「{item.Name}」从本机游戏库移除。", InfoBarSeverity.Informational);
            };

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            header.Children.Add(CreateGameIconElement(item.IconPath, 40));
            var titleStack = new StackPanel
            {
                Spacing = 2,
                Margin = new Thickness(12, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            titleStack.Children.Add(subTextBlock);
            Grid.SetColumn(titleStack, 1);
            header.Children.Add(titleStack);
            Grid.SetColumn(enableToggle, 2);
            header.Children.Add(enableToggle);

            var body = new StackPanel
            {
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 12, 0),
                Children =
                {
                    header,
                    new Border { Height = 1, Background = BrushRes("CardStrokeColorDefaultBrush") },
                    new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock { Text = "匹配进程名（多个可用逗号分隔）", FontSize = 12, FontWeight = FontWeight.SemiBold },
                            processBox,
                        },
                    },
                    requireFsCheck,
                    new Border { Height = 1, Background = BrushRes("CardStrokeColorDefaultBrush") },
                    new TextBlock { Text = "各外设专属配置", FontSize = 13, FontWeight = FontWeight.SemiBold },
                    targetsPanel,
                    new WrapPanel
                    {
                        Children = { applyBtn, resetBtn, removeManualBtn },
                    },
                },
            };

            double maxH = Math.Clamp((TopLevel.GetTopLevel(this)?.Bounds.Height ?? 760) - 220, 320, 560);
            dialog.Content = new ScrollViewer
            {
                MaxHeight = maxH,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body,
            };

            await dialog.ShowAsync();
            CommitProcesses();
            RenderUnifiedGameLibrary();
            UpdateStatusBanner();
        }
        catch (Exception ex)
        {
            Diag.Log($"OpenGameConfigDialogAsync 异常: {ex.Message}");
            ShowStatus($"打开游戏配置弹窗失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ── 前台进程 & 生效策略卡片 ───────────────────────────────────────────────

    private void UpdateStatusBanner()
    {
        var g = App.GameAuto;
        if (g == null)
        {
            ForegroundProcNameText.Text = "自动监测服务未启动";
            ForegroundWindowModeText.Text = "【未运行】";
            SetForegroundIcon(null);
            ActiveRuleStatusText.Text = "--";
            ActiveDeviceStrategiesPanel.Children.Clear();
            return;
        }

        string proc = string.IsNullOrWhiteSpace(g.LastForegroundProcess) ? "无焦点窗口" : g.LastForegroundProcess;
        ForegroundProcNameText.Text = proc;
        ForegroundWindowModeText.Text = g.LastIsFullscreen ? "【全屏窗口】" : "【普通窗口】";
        SetForegroundIcon(g.LastForegroundExePath);

        ActiveRuleStatusText.Text = g.ActiveRuleLabel;
        ActiveDeviceStrategiesPanel.Children.Clear();

        var deviceById = (App.LastDevices ?? [])
            .Where(d => d != null && !string.IsNullOrWhiteSpace(d.Id))
            .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(grp => grp.Key, grp => grp.First(), StringComparer.OrdinalIgnoreCase);

        var entries = new List<(string DeviceName, string StrategyText)>();
        foreach (var kv in g.LastOutcomeByDevice)
        {
            if (string.IsNullOrWhiteSpace(kv.Value) ||
                kv.Value.Contains("休眠中已跳过", StringComparison.Ordinal) ||
                kv.Value.Contains("未响应/休眠", StringComparison.Ordinal))
                continue;

            if (deviceById.TryGetValue(kv.Key, out var dev))
            {
                if (dev.IsSleeping || !dev.Connected)
                    continue;
                entries.Add((dev.Name, kv.Value));
            }
        }

        if (entries.Count == 0)
        {
            ActiveDeviceStrategiesPanel.Children.Add(new TextBlock
            {
                Text = "当前暂无正在调度的在线设备（仅显示唤醒并被规则调度的设备）",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = BrushRes("TextFillColorSecondaryBrush"),
            });
            return;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var (devName, strategyText) = entries[i];
            var itemBorder = new Border
            {
                Background = BrushRes("SubtleFillColorSecondaryBrush"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6),
            };
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock
            {
                Text = $"{i + 1}. {devName}",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            stack.Children.Add(new TextBlock
            {
                Text = strategyText,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = BrushRes("TextFillColorSecondaryBrush"),
            });
            itemBorder.Child = stack;
            ActiveDeviceStrategiesPanel.Children.Add(itemBorder);
        }
    }

    private void SetForegroundIcon(string? exePath)
    {
        string? iconPath = null;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            if (!_fgIconCache.TryGetValue(exePath, out iconPath))
            {
                iconPath = InstalledGameScanner.ExtractExeIcon(exePath);
                if (_fgIconCache.Count >= 64)
                    _fgIconCache.Clear();
                _fgIconCache[exePath] = iconPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath) &&
            DeviceImageService.CreateSourceFromFile(iconPath, 30) is { } src)
        {
            ForegroundIconHost.Child = new Image
            {
                Source = src,
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
            };
            return;
        }

        ForegroundIconHost.Child = new TextBlock
        {
            Text = "\uE737",
            FontFamily = Controls.UiBuilders.IconFont,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushRes("TextFillColorSecondaryBrush"),
        };
    }

    // ── 手动新建 / 运行进程导入 / 浏览 .exe ───────────────────────────────────

    private void RefreshRunningProcList()
    {
        _runningProcs = GameProfileAutoService.GetRunningForegroundCandidates();
        RunningProcCombo.Items.Clear();
        foreach (var p in _runningProcs)
            RunningProcCombo.Items.Add($"{p.ProcessName}  ({p.WindowTitle})");
    }

    private void OnRefreshRunningProcs(object? sender, RoutedEventArgs e) => RefreshRunningProcList();

    private void OnRunningProcSelected(object? sender, SelectionChangedEventArgs e)
    {
        int idx = RunningProcCombo.SelectedIndex;
        if (idx < 0 || idx >= _runningProcs.Count) return;
        var chosen = _runningProcs[idx];
        _browsedExePath = null;
        NewRuleProcessBox.Text = chosen.ProcessName;
        if (string.IsNullOrWhiteSpace(NewRuleNameBox.Text))
            NewRuleNameBox.Text = Path.GetFileNameWithoutExtension(chosen.ProcessName);
    }

    private async void OnBrowseExe(object? sender, RoutedEventArgs e)
    {
        if (_browsing) return;
        _browsing = true;
        try
        {
            var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (sp == null)
            {
                ShowStatus("文件选择器不可用。", InfoBarSeverity.Warning);
                return;
            }

            var picked = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择游戏可执行文件 (.exe)",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("可执行文件") { Patterns = ["*.exe"] },
                ],
            });
            var file = picked.FirstOrDefault();
            if (file != null)
            {
                _browsedExePath = file.TryGetLocalPath();
                NewRuleProcessBox.Text = file.Name;
                if (string.IsNullOrWhiteSpace(NewRuleNameBox.Text))
                    NewRuleNameBox.Text = Path.GetFileNameWithoutExtension(file.Name);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"文件选择器打开失败：{ex.Message}", InfoBarSeverity.Warning);
        }
        finally
        {
            _browsing = false;
        }
    }

    private static List<string> SplitProcessNames(string? raw) => (raw ?? "")
        .Split([',', ';', '，', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private void OnAddManualGame(object sender, RoutedEventArgs e)
    {
        var procs = SplitProcessNames(NewRuleProcessBox.Text);
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

    // ── 全局设置 / 立即检测 ────────────────────────────────────────────────────

    private void OnGlobalSettingsToggled(object? sender, RoutedEventArgs e)
    {
        if (_binding || App.Settings?.Current == null || AutoSwitchToggle == null || NotifySwitchToggle == null) return;
        try
        {
            var s = App.Settings.Current;
            s.AutoSwitchEnabled = AutoSwitchToggle.IsChecked == true;
            s.NotifyOnProfileSwitch = NotifySwitchToggle.IsChecked == true;
            App.Settings.Save();
            App.GameAuto?.ClearAppliedCache();
            _ = App.GameAuto?.ForceReevaluateAsync();
            UpdateStatusBanner();
        }
        catch (Exception ex)
        {
            Diag.Log($"OnGlobalSettingsToggled 异常: {ex.Message}");
            ShowStatus($"切换自动切换开关失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnFullscreenMasterToggled(object? sender, RoutedEventArgs e)
    {
        if (_binding || App.Settings?.Current == null || FullscreenMasterToggle == null) return;
        try
        {
            App.Settings.Current.EnableExclusiveFullscreenFallback = FullscreenMasterToggle.IsChecked == true;
            App.Settings.Save();
            RebuildStrategyCards();
            App.GameAuto?.ClearAppliedCache();
            _ = App.GameAuto?.ForceReevaluateAsync();
        }
        catch (Exception ex)
        {
            Diag.Log($"OnFullscreenMasterToggled 异常: {ex.Message}");
            ShowStatus($"切换全屏策略总开关失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnForceReevaluate(object? sender, RoutedEventArgs e)
    {
        if (_forceEvaluating) return;
        if (App.GameAuto == null) return;
        _forceEvaluating = true;
        if (sender is Button btn) btn.IsEnabled = false;
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
            if (sender is Button button) button.IsEnabled = true;
            _forceEvaluating = false;
        }
    }

    // ── 按设备目标（槽位 / 预设含 DPI、回报率、LOD / 软件宏） ─────────────────

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
        if (preset.LodCode is { } lod) summary += $" · LOD#{lod}";
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

    private Control BuildPerMouseTargetPanel(Dictionary<string, string> targetMap, bool isEnabled = true, bool isExitRestore = false)
    {
        var panel = new StackPanel { Spacing = 8 };
        var devs = CollectTunableEntries(targetMap);
        if (devs.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "当前未检测到可调节的鼠标或手柄设备；连接后此处将自动列出每台设备供单独指派。",
                Foreground = BrushRes("TextFillColorSecondaryBrush"),
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

    private Control CreateDeviceTargetRow(
        string deviceId,
        string displayName,
        List<TargetOption> options,
        Dictionary<string, string> targetMap,
        bool isEnabled = true,
        Action? onBeforeSave = null,
        Action? onAfterSave = null,
        bool stacked = false)
    {
        var titleText = new TextBlock
        {
            Text = displayName,
            FontSize = 12,
            FontWeight = stacked ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = stacked ? new Thickness(0) : new Thickness(0, 0, 10, 0),
        };

        var combo = new ComboBox
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemsSource = options,
            IsEnabled = isEnabled,
            Margin = stacked ? new Thickness(0) : new Thickness(0, 0, 8, 0),
        };
        targetMap.TryGetValue(deviceId, out string? currentTarget);
        int selectedIdx = options.FindIndex(o => string.Equals(o.Value, currentTarget ?? "", StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;

        string MacroKey()
        {
            targetMap.TryGetValue(deviceId, out var target);
            var parsed = GameProfileRuleMatcher.ParseTarget(target);
            return $"{deviceId}|{parsed.OnboardSlot ?? 0}";
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (_binding) return;
            if (combo.SelectedItem is not TargetOption opt) return;

            string oldKey = MacroKey();
            onBeforeSave?.Invoke();
            if (string.IsNullOrEmpty(opt.Value))
                targetMap.Remove(deviceId);
            else
                targetMap[deviceId] = opt.Value;

            var bindings = App.Settings.Current.LocalButtonMacroBindings
                ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string newKey = MacroKey();
            if (!string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase) &&
                bindings.TryGetValue(oldKey, out string? macroId))
            {
                bindings.Remove(oldKey);
                bindings[newKey] = macroId;
            }

            App.Settings.Save();
            App.GameAuto?.ClearAppliedCache();
            onAfterSave?.Invoke();
        };

        if (stacked)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = SolidColorBrush.Parse("#1A808080"),
                Background = SolidColorBrush.Parse("#0A808080"),
                Padding = new Thickness(10, 8),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children = { titleText, combo },
                },
            };
        }

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

        row.Children.Add(titleText);
        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);

        return row;
    }

    // ── 全屏 / 退出恢复策略 ────────────────────────────────────────────────────

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
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
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
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
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

            StrategyCardsHost.Children.Clear();
            StrategyCardsHost.Children.Add(card1);
            StrategyCardsHost.Children.Add(card2);
        }
        finally
        {
            _binding = wasBinding;
        }
    }

    private static Control BuildStrategyCard(
        string title,
        string description,
        ComboBox modeDropdown,
        Control targetsContent)
    {
        var card = new Border
        {
            Background = BrushRes("SubtleFillColorSecondaryBrush"),
            BorderBrush = BrushRes("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
        };
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushRes("TextFillColorSecondaryBrush"),
        });
        stack.Children.Add(modeDropdown);
        stack.Children.Add(new Border { Height = 1, Background = BrushRes("CardStrokeColorDefaultBrush") });
        stack.Children.Add(targetsContent);
        card.Child = stack;
        return card;
    }

    // ── 硬件预设 ────────────────────────────────────────────────────────────────

    private void RebuildDeviceQuickCards()
    {
        var devs = App.LastDevices.Where(d => d.Kind is DeviceKind.Mouse or DeviceKind.Controller).ToList();
        DevicePresetsQuickHost.Children.Clear();

        Panel container;
        if (_isCardMode)
        {
            _quickCardsGrid = new UniformGrid
            {
                Columns = ComputeRightPaneColumns(DevicePresetsQuickHost),
                VerticalAlignment = VerticalAlignment.Top,
            };
            container = _quickCardsGrid;
        }
        else
        {
            _quickCardsGrid = null;
            container = new StackPanel { Spacing = 8 };
        }

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
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = _isCardMode ? new Thickness(0, 0, 10, 10) : new Thickness(0),
                Background = BrushRes("SubtleFillColorSecondaryBrush"),
                BorderBrush = BrushRes("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
            };
            var stack = new StackPanel { Spacing = 8 };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.Children.Add(UiBuilders.CreateDeviceAvatar(d, 40));

            var meta = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            meta.Children.Add(new TextBlock
            {
                Text = $"{d.Name}" + (d.IsSleeping ? " [休眠]" : ""),
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            meta.Children.Add(new TextBlock
            {
                Text = $"{statusLine} · 已存专属预设 {savedCount} 个",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = BrushRes("TextFillColorSecondaryBrush"),
            });
            Grid.SetColumn(meta, 1);
            head.Children.Add(meta);
            stack.Children.Add(head);

            var btns = new WrapPanel();
            if (slots.Count > 0)
            {
                var probeBtn = new Button
                {
                    Content = "读取厂家板载预设",
                    Padding = new Thickness(10, 4, 10, 4),
                    FontSize = 12,
                    IsEnabled = !d.IsSleeping,
                    Margin = new Thickness(0, 0, 6, 4),
                };
                probeBtn.Click += async (_, _) =>
                {
                    if (_probing) return;
                    _probing = true;
                    probeBtn.IsEnabled = false;
                    try
                    {
                        var manifest = await App.Settings.ProbeAllOnboardSlotsAsync(App.Devices, d.Id);
                        RefreshAllUi();
                        ShowStatus(manifest != null
                            ? $"已读取 {d.Name} 的 {manifest.Slots.Count} 个厂家板载配置并更新到游戏配置选项。"
                            : $"无法读取 {d.Name}：设备处于休眠状态，请先唤醒。",
                            manifest != null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                    }
                    catch (Exception ex)
                    {
                        ShowStatus($"读取厂家板载预设失败：{ex.Message}", InfoBarSeverity.Error);
                    }
                    finally
                    {
                        probeBtn.IsEnabled = !d.IsSleeping;
                        _probing = false;
                    }
                };
                btns.Children.Add(probeBtn);
            }

            var detailBtn = new Button
            {
                Content = "打开设备预设管理 →",
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 4),
            };
            detailBtn.Click += (_, _) => MainWindow.Instance?.OpenDevice(d.Id);
            btns.Children.Add(detailBtn);

            stack.Children.Add(btns);
            card.Child = stack;
            container.Children.Add(card);
        }

        DevicePresetsQuickHost.Children.Add(container);
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
        QuickPresetBtn.IsEnabled = devs.Count > 0;
    }

    private async void OnCapturePreset(object? sender, RoutedEventArgs e)
    {
        if (_capturing) return;
        if (CaptureMouseCombo.SelectedItem is not ComboBoxItem item || item.Tag is not PeripheralDevice dev)
            return;

        _capturing = true;
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
        catch (Exception ex)
        {
            ShowStatus($"保存预设失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            CapturePresetBtn.IsEnabled = CaptureMouseCombo.Items.Count > 0;
            _capturing = false;
        }
    }

    /// <summary>按设备生成 DPI / 回报率 / LOD 参数预设；NumberBox.Value 为非空 double，用 double.IsNaN 判空。</summary>
    private async void OnQuickPreset(object? sender, RoutedEventArgs e)
    {
        if (_quickCapturing) return;
        if (CaptureMouseCombo.SelectedItem is not ComboBoxItem item || item.Tag is not PeripheralDevice dev)
        {
            ShowStatus("请先选择目标硬件。", InfoBarSeverity.Warning);
            return;
        }

        _quickCapturing = true;
        QuickPresetBtn.IsEnabled = false;
        try
        {
            var caps = await App.Devices.QueryCapabilitiesAsync(dev.Id);
            if (!caps.IsOnline)
            {
                ShowStatus($"无法读取 {dev.Name}：设备当前处于休眠或离线，请唤醒后再试。", InfoBarSeverity.Warning);
                return;
            }

            string presetName = string.IsNullOrWhiteSpace(PresetNameBox.Text)
                ? $"{dev.Name} 自定义参数预设"
                : PresetNameBox.Text.Trim();
            var preset = MouseHardwarePreset.CaptureFrom(presetName, dev.Id, dev.Name, caps);

            double dpi = QuickDpiBox.Value is { } dpiValue ? dpiValue : double.NaN;
            double polling = QuickPollingBox.Value is { } pollingValue ? pollingValue : double.NaN;
            double lod = QuickLodBox.Value is { } lodValue ? lodValue : double.NaN;
            if (!double.IsNaN(dpi) && dpi >= 100)
                preset.DpiX = preset.DpiY = (int)dpi;
            if (!double.IsNaN(polling) && polling > 0)
                preset.PollingRateHz = (int)polling;
            if (!double.IsNaN(lod) && lod >= 0)
                preset.LodCode = (int)lod;

            App.Settings.Current.Presets.Add(preset);
            App.Settings.Save();
            PresetNameBox.Text = "";
            RefreshAllUi();
            ShowStatus($"已生成参数预设「{preset.Name}」（{FormatPresetSummary(preset)}），可在游戏规则中按设备指派。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"生成参数预设失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            QuickPresetBtn.IsEnabled = CaptureMouseCombo.Items.Count > 0;
            _quickCapturing = false;
        }
    }

    private void RebuildPresetsUi()
    {
        var presets = App.Settings.Current.Presets;
        PresetsHost.Children.Clear();
        if (presets.Count == 0)
        {
            _presetsGrid = null;
            PresetsHost.Children.Add(new TextBlock
            {
                Text = "暂无已保存的硬件预设（可在各设备页面顶部“保存预设”，也可在此处抓取）。",
                FontSize = 12,
                Foreground = BrushRes("TextFillColorSecondaryBrush"),
            });
            return;
        }

        Panel container;
        if (_isCardMode)
        {
            _presetsGrid = new UniformGrid
            {
                Columns = ComputeRightPaneColumns(PresetsHost),
                VerticalAlignment = VerticalAlignment.Top,
            };
            container = _presetsGrid;
        }
        else
        {
            _presetsGrid = null;
            container = new StackPanel { Spacing = 8 };
        }

        foreach (var preset in presets.ToList())
        {
            var card = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = _isCardMode ? new Thickness(0, 0, 10, 10) : new Thickness(0),
                Background = BrushRes("SubtleFillColorSecondaryBrush"),
                BorderBrush = BrushRes("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
            };

            var parts = new List<string>();
            if (preset.DpiX is { } x)
                parts.Add(preset.DpiY is { } y && y != x ? $"DPI X={x}/Y={y}" : $"DPI {x}");
            if (preset.PollingRateHz is { } rate) parts.Add($"{rate} Hz");
            if (preset.PerformanceMode is { } pm) parts.Add($"模式#{pm}");
            if (preset.LodCode is { } lodCode) parts.Add($"LOD#{lodCode}");
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
                stack.Children.Add(new TextBlock { Text = preset.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                stack.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", parts),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = BrushRes("TextFillColorSecondaryBrush"),
                });
                delBtn.HorizontalAlignment = HorizontalAlignment.Left;
                stack.Children.Add(delBtn);
                card.Child = stack;
            }
            else
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = preset.Name, FontWeight = FontWeight.SemiBold });
                info.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", parts),
                    FontSize = 12,
                    Foreground = BrushRes("TextFillColorSecondaryBrush"),
                });
                row.Children.Add(info);
                delBtn.Margin = new Thickness(10, 0, 0, 0);
                delBtn.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(delBtn, 1);
                row.Children.Add(delBtn);
                card.Child = row;
            }
            container.Children.Add(card);
        }

        PresetsHost.Children.Add(container);
    }

    // ── 工具 ────────────────────────────────────────────────────────────────────

    private static Control CreateGameIconElement(string? iconPath, double size)
    {
        var border = new Border
        {
            Width = size + 6,
            Height = size + 6,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)),
        };
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            var source = DeviceImageService.CreateSourceFromFile(iconPath, size);
            if (source != null)
            {
                border.Child = new Image
                {
                    Source = source,
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Uniform,
                };
                return border;
            }
        }

        border.Child = new TextBlock
        {
            Text = "\uE7FC",
            FontFamily = Controls.UiBuilders.IconFont,
            FontSize = Math.Max(16, size * 0.5),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        return border;
    }

    private static IBrush BrushRes(string key)
    {
        try
        {
            if (Application.Current != null &&
                Application.Current.TryGetResource(key, null, out var value) &&
                value is IBrush brush)
                return brush;
        }
        catch { }
        return new SolidColorBrush(Color.FromArgb(255, 140, 140, 140));
    }

    private void ShowStatus(string msg, InfoBarSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(msg))
            MainWindow.HideToast();
        else
            MainWindow.ShowToast(msg, severity);
    }
}
