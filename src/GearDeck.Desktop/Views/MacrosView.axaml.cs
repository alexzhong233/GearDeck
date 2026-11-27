using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using GearDeck.Core.Models;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Views;

/// <summary>
/// 鼠标宏中心：一级视图展示宏预设卡片列表，二级视图采用草稿隔离方式进行新建与编辑。
/// </summary>
public partial class MacrosView : UserControl
{
    private static readonly Dictionary<string, byte> AvaloniaKeyUsageMap = BuildAvaloniaKeyUsageMap();

    private string? _editingMacroId;
    private OnboardMacroDefinition? _draftMacro;
    private UniformGrid? _libraryGrid;
    private bool _binding = true;
    private bool _recording;
    private bool _busyTest;
    private bool _busyImport;
    private bool _busyExport;
    private int _stopBtnMinX, _stopBtnMinY, _stopBtnMaxX, _stopBtnMaxY;
    private bool _hasStopBtnBounds;
    private readonly Stopwatch _recWatch = new();
    private long _lastRecMs;

    public MacrosView()
    {
        _binding = true;
        InitializeComponent();
        WireEvents();
        PopulateManualKeyCombo();
        LoadGlobalState();
        UpdatePlatformBanners();
        RenderMacroLibrary();
        ShowListView();
        Loaded += (_, _) => UpdatePlatformBanners();
        Unloaded += OnUnloaded;
        _binding = false;
    }

    private static bool IsRunnerElevated => MacroExecutionService.IsProcessElevated();

    private void WireEvents()
    {
        GlobalEnableSwitch.IsCheckedChanged += OnGlobalEnableChanged;
        RestartAdminBtn.Click += OnRestartAsAdmin;
        OpenMacAccessibilityBtn.Click += (_, _) =>
        {
            MacroExecutionService.OpenMacAccessibilitySettings();
            UpdatePlatformBanners();
        };

        NewMacroBtn.Click += OnNewMacroClick;
        ImportBtn.Click += OnImportClick;
        ExportBtn.Click += OnExportClick;
        MacroLibraryHost.SizeChanged += (_, _) => UpdateLibraryColumns();

        BackToListBtn.Click += (_, _) => ExitEditorWithoutSaving();
        CancelEditBtn.Click += (_, _) => ExitEditorWithoutSaving();
        SaveMacroBtn.Click += OnSaveMacro;

        MacroNameBox.TextChanged += (_, _) =>
        {
            if (!_binding && _draftMacro != null)
                _draftMacro.Name = MacroNameBox.Text ?? "";
        };
        MacroLoopCombo.SelectionChanged += OnLoopModeChanged;
        MacroRepeatBox.ValueChanged += (_, _) => OnRepeatValueChanged();

        RecordMacroBtn.Click += OnRecordClick;
        AddKeyPressBtn.Click += (_, _) => AddDraftStep(SelectedManualUsage, true);
        AddKeyReleaseBtn.Click += (_, _) => AddDraftStep(SelectedManualUsage, false);
        AddLeftClickBtn.Click += (_, _) => AddDraftClickPair(HidKeyCatalog.MouseLeftCode);
        AddRightClickBtn.Click += (_, _) => AddDraftClickPair(HidKeyCatalog.MouseRightCode);
        AddMiddleClickBtn.Click += (_, _) => AddDraftClickPair(HidKeyCatalog.MouseMiddleCode);
        AddWheelUpBtn.Click += (_, _) => AddDraftWheelStep(HidKeyCatalog.MouseWheelUpCode);
        AddWheelDownBtn.Click += (_, _) => AddDraftWheelStep(HidKeyCatalog.MouseWheelDownCode);
        AddDelayBtn.Click += OnAddDelay;
        ClearStepsBtn.Click += OnClearSteps;

        RecordCaptureBanner.AddHandler(InputElement.KeyDownEvent, OnRecorderKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        RecordCaptureBanner.AddHandler(InputElement.KeyUpEvent, OnRecorderKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        RecordCaptureBanner.AddHandler(InputElement.PointerPressedEvent, OnRecorderPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        RecordCaptureBanner.AddHandler(InputElement.PointerReleasedEvent, OnRecorderPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        RecordCaptureBanner.AddHandler(InputElement.PointerWheelChangedEvent, OnRecorderPointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);

        TestPlayBtn.Click += OnTestPlayDraftClick;
        StopPlayBtn.Click += OnStopPlayClick;
    }

    private void PopulateManualKeyCombo()
    {
        ManualKeyCombo.Items.Clear();
        foreach (var key in HidKeyCatalog.AllKeys.Where(k => k.Usage < 0xF0))
            ManualKeyCombo.Items.Add(new ComboBoxItem { Content = key.Label, Tag = key.Usage });
        if (ManualKeyCombo.Items.Count > 0)
            ManualKeyCombo.SelectedIndex = 0;
    }

    private void LoadGlobalState()
    {
        WithBinding(() => GlobalEnableSwitch.IsChecked = App.Settings.Current.SoftwareMacrosEnabled);
        UpdateGlobalUi();
    }

    private void UpdatePlatformBanners()
    {
        bool windows = OperatingSystem.IsWindows();
        bool supported = App.MacroRunner?.IsPlaybackSupported ?? windows;
        AdminBanner.IsVisible = windows && !IsRunnerElevated;
        MacOsBanner.IsVisible = !windows || !supported;
        if (!windows)
        {
            bool trusted = MacroExecutionService.IsMacAccessibilityTrusted();
            MacOsNoticeText.Text = trusted
                ? "已获得 macOS「辅助功能」权限（仅开放本地宏创建、编辑、导入导出与板载录入；macOS 暂不启用全局软件回放）。"
                : MacroExecutionService.MacOsPlaybackNotice + "。当前未授予「辅助功能」权限，可点击右侧按钮在系统设置中授权。";
            OpenMacAccessibilityBtn.IsVisible = !trusted;
        }
        else
        {
            MacOsNoticeText.Text = MacroExecutionService.MacOsPlaybackNotice
                + "。当前为仅配置管理模式：可新建、编辑、导入导出宏，但不会在本地执行按键回放。";
            OpenMacAccessibilityBtn.IsVisible = false;
        }
    }

    private void UpdateGlobalUi()
    {
        bool enabled = App.Settings.Current.SoftwareMacrosEnabled;
        GlobalStateText.Text = enabled
            ? "侧键接管已开启：后台监听目标鼠标的已绑定侧键/中键，拦截原生功能并触发对应软件宏（键盘/鼠标板载宏不受此开关影响）。"
            : "侧键接管已关闭：已卸载后台键鼠钩子，鼠标侧键已恢复原生功能；您仍可正常新建、编辑与录制宏。";
        TestPlayBtn.IsEnabled = enabled;
        RecordMacroBtn.IsEnabled = true;
    }

    private void WithBinding(Action action)
    {
        bool previous = _binding;
        _binding = true;
        try { action(); }
        finally { _binding = previous; }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        StopRecording(saveDraft: false);
        App.MacroRunner?.CancelPlayback();
    }

    // ── 全局开关与管理员重启 ─────────────────────────────────────────────────

    private void OnGlobalEnableChanged(object? sender, RoutedEventArgs e)
    {
        if (_binding) return;
        bool enabled = GlobalEnableSwitch.IsChecked == true;
        App.Settings.Current.SoftwareMacrosEnabled = enabled;
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        if (!enabled)
            SetPlaybackBars(active: false);
        UpdateGlobalUi();
    }

    private void OnRestartAsAdmin(object? sender, RoutedEventArgs e)
    {
        if (!MacroExecutionService.RestartAsAdmin())
            ShowStatus("已取消管理员重启，或启动失败。", InfoBarSeverity.Warning);
    }

    // ── 一级列表视图 ─────────────────────────────────────────────────────────

    private void ShowListView()
    {
        MacroEditorView.IsVisible = false;
        MacroListView.IsVisible = true;
        RootScrollViewer.Offset = new Vector(0, 0);
    }

    private void EnterEditorView(OnboardMacroDefinition? source)
    {
        StopRecording(saveDraft: false);
        App.MacroRunner?.CancelPlayback();

        if (source == null)
        {
            _editingMacroId = null;
            _draftMacro = new OnboardMacroDefinition
            {
                Name = $"自定义宏 {App.Settings.Current.Macros.Count + 1}",
                LoopMode = MacroLoopMode.PlayOnce,
                RepeatCount = 1,
                Steps = [],
            };
            EditorTitleText.Text = "新建鼠标宏";
            EditorSubText.Text = "配置名称、触发方式与动作步骤后，点击右上角「保存并返回」写入宏库";
        }
        else
        {
            _editingMacroId = source.Id;
            _draftMacro = new OnboardMacroDefinition
            {
                Id = source.Id,
                Name = source.Name,
                LoopMode = source.LoopMode,
                RepeatCount = source.RepeatCount,
                Steps = NormalizeStepsForEditor(source.Steps),
            };
            EditorTitleText.Text = $"编辑宏 · {source.Name}";
            EditorSubText.Text = "当前在隔离草稿中修改，点击「保存并返回」生效，「取消」或返回将放弃未保存更改";
        }

        BindDraftEditorFields();
        MacroListView.IsVisible = false;
        MacroEditorView.IsVisible = true;
        RootScrollViewer.Offset = new Vector(0, 0);
    }

    private void ExitEditorWithoutSaving()
    {
        StopRecording(saveDraft: false);
        App.MacroRunner?.CancelPlayback();
        SetPlaybackBars(active: false);
        _editingMacroId = null;
        _draftMacro = null;
        ShowListView();
    }

    private int ComputeLibraryColumns()
    {
        double w = MacroLibraryHost.Bounds.Width;
        if (w <= 1) return 2;
        return w >= 1060 ? 3 : (w >= 660 ? 2 : 1);
    }

    private void UpdateLibraryColumns()
    {
        if (_libraryGrid == null) return;
        int cols = ComputeLibraryColumns();
        if (_libraryGrid.Columns != cols)
            _libraryGrid.Columns = cols;
    }

    private void RenderMacroLibrary()
    {
        var macros = App.Settings.Current.Macros;
        MacroLibraryHost.Children.Clear();

        if (macros.Count == 0)
        {
            _libraryGrid = null;
            MacroLibraryHost.Children.Add(new Border
            {
                Padding = new Thickness(24, 28),
                CornerRadius = new CornerRadius(10),
                Background = ResolveBrush("CardBackgroundFillColorDefaultBrush", Brushes.Transparent),
                BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush", Brushes.Gray),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "当前宏库为空",
                            FontSize = 15,
                            FontWeight = FontWeight.SemiBold,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = "点击右上角「+ 新建宏」进入录制与编排界面，支持键盘按键与鼠标全键位动作。",
                            FontSize = 12,
                            Foreground = ResolveBrush("TextFillColorSecondaryBrush", Brushes.Gray),
                            TextWrapping = TextWrapping.Wrap,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    },
                },
            });
            return;
        }

        _libraryGrid = new UniformGrid
        {
            Columns = ComputeLibraryColumns(),
            VerticalAlignment = VerticalAlignment.Top,
        };

        IBrush cardBg = ResolveBrush("CardBackgroundFillColorDefaultBrush", Brushes.Transparent);
        IBrush cardStroke = ResolveBrush("CardStrokeColorDefaultBrush", Brushes.Gray);
        IBrush pillBg = ResolveBrush("SubtleFillColorSecondaryBrush", Brushes.Transparent);
        IBrush secondaryText = ResolveBrush("TextFillColorSecondaryBrush", Brushes.Gray);
        IBrush accentBrush = ResolveBrush("AccentFillColorDefaultBrush", Brushes.CornflowerBlue);

        foreach (var macro in macros.ToList())
        {
            string loopText = macro.LoopMode switch
            {
                MacroLoopMode.PlayOnce => "单次",
                MacroLoopMode.WhileHeld => "按住循环",
                MacroLoopMode.ToggleUntilKey => "切换 (+Esc)",
                _ => $"循环 {macro.RepeatCount} 次",
            };
            string bound = DescribeBindingsForMacro(macro.Id);
            int actionCount = macro.Steps.Count(s => s.KeyCode != 0);
            string summary = $"{actionCount} 个动作 · 共 {macro.Steps.Sum(s => s.DelayMs)} ms";
            string preview = BuildStepsPreview(macro.Steps);

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            header.Children.Add(new TextBlock
            {
                Text = macro.Name,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var modeBadge = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3),
                Margin = new Thickness(8, 0, 0, 0),
                Background = pillBg,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = loopText, FontSize = 11, Foreground = secondaryText },
            };
            Grid.SetColumn(modeBadge, 1);
            header.Children.Add(modeBadge);

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(header);
            content.Children.Add(new TextBlock
            {
                Text = summary,
                FontSize = 12,
                Foreground = secondaryText,
            });
            content.Children.Add(new TextBlock
            {
                Text = preview,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = secondaryText,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"侧键绑定：{bound}",
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = bound.StartsWith("（") ? secondaryText : accentBrush,
            });

            var actions = new Grid();
            actions.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.2, GridUnitType.Star)));
            actions.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            actions.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            actions.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

            var editBtn = ActionButton("编辑", accent: true, marginRight: 6);
            editBtn.Click += (_, _) => EnterEditorView(macro);

            var playBtn = ActionButton("试播", accent: false, marginRight: 6);
            playBtn.Click += (_, _) => _ = PlayMacroSafeAsync(macro, playBtn, fromList: true);

            var dupBtn = ActionButton("复制", accent: false, marginRight: 6);
            dupBtn.Click += (_, _) => DuplicateMacro(macro);

            var delBtn = ActionButton("删除", accent: false, marginRight: 0);
            delBtn.Click += (_, _) => DeleteMacro(macro);

            Grid.SetColumn(editBtn, 0);
            Grid.SetColumn(playBtn, 1);
            Grid.SetColumn(dupBtn, 2);
            Grid.SetColumn(delBtn, 3);
            actions.Children.Add(editBtn);
            actions.Children.Add(playBtn);
            actions.Children.Add(dupBtn);
            actions.Children.Add(delBtn);
            content.Children.Add(actions);

            _libraryGrid.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(16),
                CornerRadius = new CornerRadius(10),
                Background = cardBg,
                BorderBrush = cardStroke,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = content,
            });
        }

        MacroLibraryHost.Children.Add(_libraryGrid);
    }

    private static Button ActionButton(string content, bool accent, double marginRight)
    {
        var btn = new Button
        {
            Content = content,
            Padding = new Thickness(8, 5),
            FontSize = 12,
            Margin = new Thickness(0, 0, marginRight, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        if (accent)
            btn.Classes.Add("accent");
        return btn;
    }

    private static string BuildStepsPreview(IReadOnlyList<MacroStep> steps)
    {
        if (steps.Count == 0)
            return "动作序列：（暂无步骤，点击「编辑」添加或录制）";

        var parts = steps.Take(7).Select(s =>
            s.KeyCode == 0
                ? $"[{s.DelayMs}ms]"
                : HidKeyCatalog.IsWheelCode(s.KeyCode)
                    ? (s.KeyCode == HidKeyCatalog.MouseWheelUpCode ? "滚轮↑" : "滚轮↓")
                    : $"{s.DisplayName}{(s.IsPress ? "↓" : "↑")}");
        string text = string.Join(" → ", parts);
        if (steps.Count > 7)
            text += $" … (+{steps.Count - 7})";
        return text;
    }

    private static List<MacroStep> NormalizeStepsForEditor(IReadOnlyList<MacroStep> source)
    {
        var result = new List<MacroStep>(source.Count * 2);
        for (int i = 0; i < source.Count; i++)
        {
            var s = source[i];
            if (s.KeyCode == 0)
            {
                if (s.DelayMs > 0)
                    result.Add(new MacroStep(0, false, s.DelayMs));
                continue;
            }
            result.Add(new MacroStep(s.KeyCode, s.IsPress, 0));
            if (s.DelayMs > 0 && i < source.Count - 1)
                result.Add(new MacroStep(0, false, s.DelayMs));
        }
        return result;
    }

    private static string DescribeBindingsForMacro(string macroId)
    {
        if (string.IsNullOrWhiteSpace(macroId)) return "（未绑定侧键）";
        var matches = App.Settings.Current.LocalButtonMacroBindings
            .Where(kv => string.Equals(kv.Value, macroId, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key.Contains('|') ? kv.Key.Split('|')[1] : kv.Key)
            .Distinct()
            .ToList();
        return matches.Count == 0 ? "（未绑定侧键）" : string.Join(", ", matches);
    }

    private static IBrush ResolveBrush(string key, IBrush fallback)
    {
        try
        {
            if (Application.Current != null &&
                Application.Current.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) &&
                value is IBrush brush)
                return brush;
        }
        catch { }
        return fallback;
    }

    private void OnNewMacroClick(object? sender, RoutedEventArgs e)
        => EnterEditorView(source: null);

    private void DuplicateMacro(OnboardMacroDefinition source)
    {
        var copy = new OnboardMacroDefinition
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Name = $"{source.Name} 副本",
            LoopMode = source.LoopMode,
            RepeatCount = source.RepeatCount,
            Steps = source.Steps.Select(s => new MacroStep(s.KeyCode, s.IsPress, s.DelayMs)).ToList(),
        };
        int index = App.Settings.Current.Macros.IndexOf(source);
        App.Settings.Current.Macros.Insert(Math.Max(0, index + 1), copy);
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderMacroLibrary();
        ShowStatus($"已复制为「{copy.Name}」。", InfoBarSeverity.Success);
    }

    private void DeleteMacro(OnboardMacroDefinition macro)
    {
        App.Settings.Current.Macros.Remove(macro);
        var keys = App.Settings.Current.LocalButtonMacroBindings
            .Where(kv => string.Equals(kv.Value, macro.Id, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();
        foreach (string key in keys)
            App.Settings.Current.LocalButtonMacroBindings.Remove(key);
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderMacroLibrary();
        ShowStatus($"已删除宏「{macro.Name}」及其绑定。", InfoBarSeverity.Informational);
    }

    // ── 二级草稿编辑器 ───────────────────────────────────────────────────────

    private void BindDraftEditorFields()
    {
        var macro = _draftMacro;
        WithBinding(() =>
        {
            if (macro == null) return;
            MacroNameBox.Text = macro.Name;
            MacroRepeatBox.Value = Math.Clamp(macro.RepeatCount, 1, 99);
            MacroRepeatBox.IsEnabled = macro.LoopMode == MacroLoopMode.RepeatCount;
            StepDelayBox.Value = 25;

            int selected = 0;
            string wanted = macro.LoopMode.ToString();
            for (int i = 0; i < MacroLoopCombo.Items.Count; i++)
            {
                if (MacroLoopCombo.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Tag as string, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    selected = i;
                    break;
                }
            }
            MacroLoopCombo.SelectedIndex = selected;
        });
        RenderSteps(macro);
        BindingSummaryText.Text = _editingMacroId == null
            ? "新建宏保存后，可在宏中心卡片或外设侧键重映射中绑定此宏。"
            : $"侧键绑定：{DescribeBindingsForMacro(_editingMacroId)}";
    }

    private void OnLoopModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_binding || _draftMacro is not { } macro) return;
        if (MacroLoopCombo.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<MacroLoopMode>(item.Tag as string, out var mode))
            macro.LoopMode = mode;
        MacroRepeatBox.IsEnabled = macro.LoopMode == MacroLoopMode.RepeatCount;
    }

    private void OnRepeatValueChanged()
    {
        double raw = MacroRepeatBox.Value;
        if (_binding || _draftMacro is not { } macro || double.IsNaN(raw)) return;
        macro.RepeatCount = Math.Clamp((int)Math.Round(raw), 1, 99);
    }

    private static int ReadStepDelay(NumberBox box)
        => double.IsFinite(box.Value)
            ? Math.Clamp((int)Math.Round(box.Value), 5, 5000)
            : 25;

    private void RenderSteps(OnboardMacroDefinition? macro)
    {
        MacroStepsPanel.Children.Clear();
        if (macro == null || macro.Steps.Count == 0)
        {
            MacroStepsPanel.Children.Add(new TextBlock
            {
                Text = "当前草稿暂无动作步骤；点击「开始录制键鼠动作」或使用上方工具栏添加按键/点击/滚轮/间隔。",
                FontSize = 12,
                Foreground = ResolveBrush("TextFillColorSecondaryBrush", Brushes.Gray),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        IBrush secondaryText = ResolveBrush("TextFillColorSecondaryBrush", Brushes.Gray);
        for (int i = 0; i < macro.Steps.Count; i++)
        {
            int index = i;
            var step = macro.Steps[i];
            bool isInterval = step.KeyCode == 0;
            bool isWheel = HidKeyCatalog.IsWheelCode(step.KeyCode);

            string kindLabel = isInterval
                ? "⏱ 间隔"
                : isWheel
                    ? (step.KeyCode == HidKeyCatalog.MouseWheelUpCode ? "鼠标滚轮向上 ⬆" : "鼠标滚轮向下 ⬇")
                    : $"{step.DisplayName}  {(step.IsPress ? "↓ 按下" : "↑ 抬起")}";

            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
            };
            left.Children.Add(new TextBlock
            {
                Text = $"#{index + 1}",
                Width = 32,
                FontSize = 12,
                Foreground = secondaryText,
                VerticalAlignment = VerticalAlignment.Center,
            });
            left.Children.Add(new TextBlock
            {
                Text = kindLabel,
                MinWidth = isInterval ? 64 : 170,
                FontSize = 13,
                FontWeight = isInterval ? FontWeight.Normal : FontWeight.Medium,
                Foreground = isInterval ? secondaryText : ResolveBrush("TextFillColorPrimaryBrush", Brushes.White),
                VerticalAlignment = VerticalAlignment.Center,
            });

            if (isInterval)
            {
                var delayBox = new NumberBox { Width = 106, Maximum = 10000, Minimum = 1 };
                delayBox.Value = Math.Clamp(step.DelayMs, 1, 10000);
                delayBox.ValueChanged += (_, _) =>
                {
                    if (_binding) return;
                    if (double.IsFinite(delayBox.Value))
                        step.DelayMs = Math.Clamp((int)Math.Round(delayBox.Value), 1, 10000);
                };
                left.Children.Add(delayBox);
                left.Children.Add(new TextBlock
                {
                    Text = "ms",
                    FontSize = 12,
                    Foreground = secondaryText,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var up = new Button { Content = "↑", FontSize = 12, Padding = new Thickness(8, 3), IsEnabled = index > 0 };
            up.Click += (_, _) => MoveDraftStep(index, -1);
            var down = new Button { Content = "↓", FontSize = 12, Padding = new Thickness(8, 3), IsEnabled = index < macro.Steps.Count - 1 };
            down.Click += (_, _) => MoveDraftStep(index, 1);
            var remove = new Button { Content = "删除", FontSize = 12, Padding = new Thickness(8, 3) };
            remove.Click += (_, _) => RemoveDraftStep(index);
            actions.Children.Add(up);
            actions.Children.Add(down);
            actions.Children.Add(remove);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(left);
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            MacroStepsPanel.Children.Add(row);
        }
    }

    private void MoveDraftStep(int index, int delta)
    {
        if (_draftMacro is not { } macro) return;
        int target = index + delta;
        if (target < 0 || target >= macro.Steps.Count) return;
        (macro.Steps[index], macro.Steps[target]) = (macro.Steps[target], macro.Steps[index]);
        RenderSteps(macro);
    }

    private void RemoveDraftStep(int index)
    {
        if (_draftMacro is not { } macro) return;
        if (index < 0 || index >= macro.Steps.Count) return;
        macro.Steps.RemoveAt(index);
        RenderSteps(macro);
    }

    private byte SelectedManualUsage
        => ManualKeyCombo.SelectedItem is ComboBoxItem item && item.Tag is byte usage
            ? usage
            : (byte)0x04;

    private void AddDraftStep(byte usage, bool isPress)
    {
        if (_draftMacro is not { } macro) return;
        macro.Steps.Add(new MacroStep(usage, isPress, 0));
        RenderSteps(macro);
    }

    private void AddDraftWheelStep(byte wheelCode)
    {
        if (_draftMacro is not { } macro) return;
        macro.Steps.Add(new MacroStep(wheelCode, true, 0));
        RenderSteps(macro);
    }

    private void AddDraftClickPair(byte usage)
    {
        if (_draftMacro is not { } macro) return;
        int gap = ReadStepDelay(StepDelayBox);
        macro.Steps.Add(new MacroStep(usage, true, 0));
        if (gap > 0)
            macro.Steps.Add(new MacroStep(0, false, gap));
        macro.Steps.Add(new MacroStep(usage, false, 0));
        RenderSteps(macro);
    }

    private void OnAddDelay(object? sender, RoutedEventArgs e)
    {
        if (_draftMacro is not { } macro) return;
        macro.Steps.Add(new MacroStep(0, false, ReadStepDelay(StepDelayBox)));
        RenderSteps(macro);
    }

    private void OnClearSteps(object? sender, RoutedEventArgs e)
    {
        if (_draftMacro is not { } macro) return;
        macro.Steps.Clear();
        RenderSteps(macro);
    }

    private void OnSaveMacro(object? sender, RoutedEventArgs e)
    {
        if (_draftMacro is not { } draft) return;
        StopRecording(saveDraft: false);

        string name = string.IsNullOrWhiteSpace(MacroNameBox.Text)
            ? $"自定义宏 {App.Settings.Current.Macros.Count + 1}"
            : MacroNameBox.Text.Trim();
        draft.Name = name;

        var existing = _editingMacroId != null
            ? App.Settings.Current.Macros.FirstOrDefault(m => m.Id == _editingMacroId)
            : null;

        if (existing != null)
        {
            existing.Name = draft.Name;
            existing.LoopMode = draft.LoopMode;
            existing.RepeatCount = Math.Clamp(draft.RepeatCount, 1, 99);
            existing.Steps = draft.Steps.Select(s => new MacroStep(s.KeyCode, s.IsPress, s.DelayMs)).ToList();
        }
        else
        {
            App.Settings.Current.Macros.Add(new OnboardMacroDefinition
            {
                Id = Guid.NewGuid().ToString("N")[..10],
                Name = draft.Name,
                LoopMode = draft.LoopMode,
                RepeatCount = Math.Clamp(draft.RepeatCount, 1, 99),
                Steps = draft.Steps.Select(s => new MacroStep(s.KeyCode, s.IsPress, s.DelayMs)).ToList(),
            });
        }

        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderMacroLibrary();
        _editingMacroId = null;
        _draftMacro = null;
        ShowListView();
        ShowStatus($"宏「{name}」已保存并同步触发配置。", InfoBarSeverity.Success);
    }

    // ── 录制（仅写入草稿） ─────────────────────────────────────────────────

    private void OnRecordClick(object? sender, RoutedEventArgs e)
    {
        if (_binding) return;
        if (RecordMacroBtn.IsChecked == true)
            StartRecording();
        else
            StopRecording(saveDraft: true);
    }

    private void StartRecording()
    {
        _recording = true;
        _recWatch.Restart();
        _lastRecMs = 0;
        RecordMacroBtn.Content = "停止录制";
        RecordCaptureBanner.IsVisible = true;

        if (OperatingSystem.IsWindows() && App.MacroRunner != null)
        {
            CacheStopButtonScreenBounds();
            RecordStatusText.Text = "正在全局监听键鼠事件：屏幕任意位置的键盘按键、鼠标左/右/中/侧键按下抬起及滚轮滚动都会记录，并在动作之间插入真实毫秒间隔（已排除停止按钮本身），完成后点击上方「停止录制」。";
            App.MacroRunner.StartRecordingCapture(
                (usage, isPress) =>
                {
                    if (_recording)
                        RecordCodeEvent(usage, isPress);
                },
                IsPointInsideStopButton);
        }
        else
        {
            RecordCaptureBanner.MinHeight = 68;
            RecordStatusText.Text = "录制中：请在此高亮区域内按键、点击鼠标左/右/中/侧键或滑动滚轮，动作之间将自动插入真实毫秒间隔。";
            RecordCaptureBanner.Focus();
        }
    }

    private void CacheStopButtonScreenBounds()
    {
        try
        {
            var p0 = RecordMacroBtn.PointToScreen(new Point(0, 0));
            var p1 = RecordMacroBtn.PointToScreen(new Point(RecordMacroBtn.Bounds.Width, RecordMacroBtn.Bounds.Height));
            _stopBtnMinX = Math.Min(p0.X, p1.X) - 6;
            _stopBtnMinY = Math.Min(p0.Y, p1.Y) - 6;
            _stopBtnMaxX = Math.Max(p0.X, p1.X) + 6;
            _stopBtnMaxY = Math.Max(p0.Y, p1.Y) + 6;
            _hasStopBtnBounds = _stopBtnMaxX > _stopBtnMinX && _stopBtnMaxY > _stopBtnMinY;
        }
        catch
        {
            _hasStopBtnBounds = false;
        }
    }

    private bool IsPointInsideStopButton(int x, int y)
        => _hasStopBtnBounds && x >= _stopBtnMinX && x <= _stopBtnMaxX && y >= _stopBtnMinY && y <= _stopBtnMaxY;

    private void StopRecording(bool saveDraft)
    {
        if (!_recording) return;
        _recording = false;
        _hasStopBtnBounds = false;
        App.MacroRunner?.StopRecordingCapture();
        _recWatch.Stop();
        RecordMacroBtn.IsChecked = false;
        RecordMacroBtn.Content = "开始录制键鼠动作";
        RecordCaptureBanner.IsVisible = false;
        RecordCaptureBanner.MinHeight = 0;
        if (saveDraft && _draftMacro != null)
            RenderSteps(_draftMacro);
    }

    private void OnRecorderKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_recording || _draftMacro == null || OperatingSystem.IsWindows()) return;
        e.Handled = true;
        if (AvaloniaKeyUsageMap.TryGetValue(e.Key.ToString(), out byte usage))
            RecordCodeEvent(usage, isPress: true);
    }

    private void OnRecorderKeyUp(object? sender, KeyEventArgs e)
    {
        if (!_recording || _draftMacro == null || OperatingSystem.IsWindows()) return;
        e.Handled = true;
        if (AvaloniaKeyUsageMap.TryGetValue(e.Key.ToString(), out byte usage))
            RecordCodeEvent(usage, isPress: false);
    }

    private void OnRecorderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_recording || _draftMacro == null || OperatingSystem.IsWindows()) return;
        e.Handled = true;
        RecordCaptureBanner.Focus();
        byte? code = e.GetCurrentPoint(RecordCaptureBanner).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => HidKeyCatalog.MouseLeftCode,
            PointerUpdateKind.RightButtonPressed => HidKeyCatalog.MouseRightCode,
            PointerUpdateKind.MiddleButtonPressed => HidKeyCatalog.MouseMiddleCode,
            PointerUpdateKind.XButton1Pressed => HidKeyCatalog.MouseXButton1Code,
            PointerUpdateKind.XButton2Pressed => HidKeyCatalog.MouseXButton2Code,
            _ => null,
        };
        if (code is { } c) RecordCodeEvent(c, isPress: true);
    }

    private void OnRecorderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_recording || _draftMacro == null || OperatingSystem.IsWindows()) return;
        e.Handled = true;
        byte? code = e.GetCurrentPoint(RecordCaptureBanner).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonReleased => HidKeyCatalog.MouseLeftCode,
            PointerUpdateKind.RightButtonReleased => HidKeyCatalog.MouseRightCode,
            PointerUpdateKind.MiddleButtonReleased => HidKeyCatalog.MouseMiddleCode,
            PointerUpdateKind.XButton1Released => HidKeyCatalog.MouseXButton1Code,
            PointerUpdateKind.XButton2Released => HidKeyCatalog.MouseXButton2Code,
            _ => null,
        };
        if (code is { } c) RecordCodeEvent(c, isPress: false);
    }

    private void OnRecorderPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_recording || _draftMacro == null || OperatingSystem.IsWindows()) return;
        e.Handled = true;
        if (Math.Abs(e.Delta.Y) < 0.01) return;
        byte code = e.Delta.Y > 0 ? HidKeyCatalog.MouseWheelUpCode : HidKeyCatalog.MouseWheelDownCode;
        RecordCodeEvent(code, isPress: true);
    }

    private void RecordCodeEvent(byte usage, bool isPress)
    {
        if (_draftMacro is not { } macro) return;
        long now = _recWatch.ElapsedMilliseconds;
        if (macro.Steps.Count > 0)
        {
            int gapMs = Math.Clamp((int)(now - _lastRecMs), 1, 10000);
            macro.Steps.Add(new MacroStep(0, false, gapMs));
        }
        macro.Steps.Add(new MacroStep(usage, isPress, 0));
        _lastRecMs = now;
        RenderSteps(macro);
    }

    private static Dictionary<string, byte> BuildAvaloniaKeyUsageMap()
    {
        var map = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 26; i++)
            map[((char)('A' + i)).ToString()] = (byte)(0x04 + i);
        for (int i = 1; i <= 9; i++)
            map[$"D{i}"] = (byte)(0x1E + i - 1);
        map["D0"] = 0x27;
        for (int i = 1; i <= 12; i++)
            map[$"F{i}"] = (byte)(0x39 + i);

        map["Enter"] = 0x28;
        map["Return"] = 0x28;
        map["Escape"] = 0x29;
        map["Esc"] = 0x29;
        map["Back"] = 0x2A;
        map["Backspace"] = 0x2A;
        map["Tab"] = 0x2B;
        map["Space"] = 0x2C;
        map["OemMinus"] = 0x2D;
        map["OemPlus"] = 0x2E;
        map["OemOpenBrackets"] = 0x2F;
        map["OemCloseBrackets"] = 0x30;
        map["OemSemicolon"] = 0x33;
        map["OemComma"] = 0x36;
        map["OemPeriod"] = 0x37;
        map["OemQuestion"] = 0x38;
        map["Oem3"] = 0x35;
        map["OemTilde"] = 0x35;
        map["CapsLock"] = 0x39;
        map["Capital"] = 0x39;
        map["PrintScreen"] = 0x46;
        map["Snapshot"] = 0x46;
        map["Insert"] = 0x49;
        map["Home"] = 0x4A;
        map["PageUp"] = 0x4B;
        map["Delete"] = 0x4C;
        map["End"] = 0x4D;
        map["PageDown"] = 0x4E;
        map["Right"] = 0x4F;
        map["Left"] = 0x50;
        map["Down"] = 0x51;
        map["Up"] = 0x52;
        map["LCtrl"] = 0xE0;
        map["LControl"] = 0xE0;
        map["LShift"] = 0xE1;
        map["LAlt"] = 0xE2;
        map["LWin"] = 0xE3;
        map["LWindows"] = 0xE3;
        return map;
    }

    // ── 试播 ─────────────────────────────────────────────────────────────────

    private async void OnTestPlayDraftClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await PlayMacroSafeAsync(_draftMacro, sender as Button, fromList: false);
        }
        catch (Exception ex)
        {
            ShowStatus($"回放失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnStopPlayClick(object? sender, RoutedEventArgs e)
    {
        App.MacroRunner?.CancelPlayback();
        SetPlaybackBars(active: false);
        ShowStatus("已停止回放。", InfoBarSeverity.Informational);
    }

    private void SetPlaybackBars(bool active)
    {
        PlaybackBar.IsVisible = active;
        PlaybackBar.IsIndeterminate = active;
        ListPlaybackBar.IsVisible = active;
        ListPlaybackBar.IsIndeterminate = active;
    }

    private async Task PlayMacroSafeAsync(OnboardMacroDefinition? macro, Button? button, bool fromList)
    {
        if (_busyTest) return;
        if (macro == null || macro.Steps.Count == 0)
        {
            ShowStatus("当前宏没有可执行步骤，请先添加步骤。", InfoBarSeverity.Warning);
            return;
        }
        if (!App.Settings.Current.SoftwareMacrosEnabled)
        {
            ShowStatus("侧键接管当前已关闭，请先在列表页开启后再试播。", InfoBarSeverity.Warning);
            return;
        }
        if (App.MacroRunner == null || !App.MacroRunner.IsPlaybackSupported)
        {
            ShowStatus(MacroExecutionService.MacOsPlaybackNotice, InfoBarSeverity.Informational);
            return;
        }

        _busyTest = true;
        if (button != null) button.IsEnabled = false;
        if (fromList)
        {
            ListPlaybackBar.IsVisible = true;
            ListPlaybackBar.IsIndeterminate = true;
        }
        else
        {
            PlaybackBar.IsVisible = true;
            PlaybackBar.IsIndeterminate = true;
        }
        try
        {
            await App.MacroRunner.PlayMacroAsync(macro);
            ShowStatus("回放已结束。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"回放失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _busyTest = false;
            if (button != null) button.IsEnabled = true;
            SetPlaybackBars(active: false);
        }
    }

    // ── 导入 / 导出 JSON ─────────────────────────────────────────────────────

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (_busyImport || sender is not Button button) return;
        _busyImport = true;
        button.IsEnabled = false;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider == null)
            {
                ShowStatus("文件选择器不可用。", InfoBarSeverity.Warning);
                return;
            }
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入宏 JSON",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });
            var file = files.FirstOrDefault();
            if (file == null) return;

            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            string json = await reader.ReadToEndAsync();
            var imported = JsonSerializer.Deserialize<List<OnboardMacroDefinition>>(json);
            if (imported == null || imported.Count == 0)
            {
                ShowStatus("文件中没有可导入的宏。", InfoBarSeverity.Warning);
                return;
            }

            int count = 0;
            foreach (var source in imported)
            {
                if (source == null) continue;
                App.Settings.Current.Macros.Add(new OnboardMacroDefinition
                {
                    Id = Guid.NewGuid().ToString("N")[..10],
                    Name = string.IsNullOrWhiteSpace(source.Name) ? "导入宏" : source.Name,
                    LoopMode = source.LoopMode,
                    RepeatCount = Math.Clamp(source.RepeatCount, 1, 99),
                    Steps = source.Steps?
                        .Select(s => new MacroStep(s.KeyCode, s.IsPress, s.DelayMs))
                        .ToList() ?? [],
                });
                count++;
            }
            App.Settings.Save();
            App.MacroRunner?.SyncHookState();
            RenderMacroLibrary();
            ShowStatus($"已导入 {count} 个宏。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"导入失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _busyImport = false;
            button.IsEnabled = true;
        }
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_busyExport || sender is not Button button) return;
        _busyExport = true;
        button.IsEnabled = false;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider == null)
            {
                ShowStatus("文件选择器不可用。", InfoBarSeverity.Warning);
                return;
            }
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出宏 JSON",
                DefaultExtension = "json",
                SuggestedFileName = "geardeck-macros.json",
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });
            if (file == null) return;

            var payload = new
            {
                Version = 1,
                ExportedAt = DateTimeOffset.UtcNow,
                Macros = App.Settings.Current.Macros,
                Bindings = App.Settings.Current.LocalButtonMacroBindings,
            };
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            ShowStatus("宏库与侧键绑定已导出。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"导出失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _busyExport = false;
            button.IsEnabled = true;
        }
    }

    private static void ShowStatus(string message, InfoBarSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(message))
            MainWindow.HideToast();
        else
            MainWindow.ShowToast(message, severity);
    }
}
