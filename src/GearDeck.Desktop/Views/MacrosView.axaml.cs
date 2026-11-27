using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using GearDeck.Core.Models;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Views;

public partial class MacrosView : UserControl
{
    // 视图由 MainWindow 缓存复用；开关状态按进程保存，不改动其他服务的持久化结构。
    private static bool s_globalEnabled = true;
    private static readonly Dictionary<string, byte> AvaloniaKeyUsageMap = BuildAvaloniaKeyUsageMap();

    private string? _selectedMacroId;
    private bool _binding = true;
    private bool _recording;
    private bool _busyTest;
    private bool _busyImport;
    private bool _busyExport;
    private readonly Stopwatch _recWatch = new();
    private long _lastRecMs;

    public MacrosView()
    {
        _binding = true;
        InitializeComponent();
        WireEvents();
        EnsureAtLeastOneMacro();
        PopulateManualKeyCombo();
        LoadGlobalState();
        UpdatePlatformBanners();
        RenderMacroLibrary();
        BindEditorFields();
        Unloaded += OnUnloaded;
        _binding = false;
    }

    // App.MacroRunner 没有 IsElevated 属性，统一走 MacroExecutionService.IsProcessElevated() 做 UIPI 检测。
    private static bool IsRunnerElevated => App.MacroRunner != null && MacroExecutionService.IsProcessElevated();

    private OnboardMacroDefinition? CurrentMacro
    {
        get
        {
            var list = App.Settings.Current.Macros;
            if (list.Count == 0) return null;
            return list.FirstOrDefault(m => m.Id == _selectedMacroId) ?? list[0];
        }
    }

    // ── 初始化与事件接线 ─────────────────────────────────────────────────────

    private void WireEvents()
    {
        GlobalEnableSwitch.IsCheckedChanged += OnGlobalEnableChanged;
        RestartAdminBtn.Click += OnRestartAsAdmin;

        NewMacroBtn.Click += OnNewMacro;
        CopyMacroBtn.Click += OnCopyMacro;
        DeleteMacroBtn.Click += OnDeleteMacro;
        ImportBtn.Click += OnImportClick;
        ExportBtn.Click += OnExportClick;

        MacroNameBox.TextChanged += (_, _) => { if (!_binding) CommitMacroName(); };
        MacroLoopCombo.SelectionChanged += OnLoopModeChanged;
        // Value 在 BindEditorFields() 中用代码赋值，避免在 AXAML 内联 Value 与 ValueChanged 冲突。
        MacroRepeatBox.ValueChanged += (_, _) => OnRepeatValueChanged();

        RecordMacroBtn.Click += OnRecordClick;
        AddKeyPressBtn.Click += OnAddKeyPress;
        AddKeyReleaseBtn.Click += OnAddKeyRelease;
        AddLeftClickBtn.Click += OnAddLeftClick;
        AddRightClickBtn.Click += OnAddRightClick;
        AddMiddleClickBtn.Click += OnAddMiddleClick;
        AddDelayBtn.Click += OnAddDelay;
        ClearStepsBtn.Click += OnClearSteps;

        RecordFocusTrap.KeyDown += OnRecorderKeyDown;
        RecordFocusTrap.KeyUp += OnRecorderKeyUp;
        RecordFocusTrap.PointerPressed += OnRecorderPointerPressed;
        RecordFocusTrap.PointerReleased += OnRecorderPointerReleased;

        SaveMacroBtn.Click += OnSaveMacro;
        TestPlayBtn.Click += OnTestPlayClick;
        StopPlayBtn.Click += OnStopPlayClick;

        WorkspaceGrid.SizeChanged += OnWorkspaceSizeChanged;
    }

    private static void EnsureAtLeastOneMacro()
    {
        if (App.Settings.Current.Macros.Count > 0) return;
        var def = new OnboardMacroDefinition
        {
            Name = "示例：Ctrl+C 复制宏",
            LoopMode = MacroLoopMode.RepeatCount,
            RepeatCount = 1,
            Steps =
            [
                new MacroStep(0xE0, true, 12),
                new MacroStep(0x06, true, 15),
                new MacroStep(0x06, false, 12),
                new MacroStep(0xE0, false, 12),
            ],
        };
        App.Settings.Current.Macros.Add(def);
        App.Settings.Save();
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
        WithBinding(() => GlobalEnableSwitch.IsChecked = s_globalEnabled);
        UpdateGlobalUi();
    }

    private void UpdatePlatformBanners()
    {
        bool windows = OperatingSystem.IsWindows();
        bool supported = App.MacroRunner?.IsPlaybackSupported ?? false;
        AdminBanner.IsVisible = windows && !IsRunnerElevated;
        MacOsBanner.IsVisible = !windows || !supported;
        MacOsNoticeText.Text = MacroExecutionService.MacOsPlaybackNotice
            + "。当前为仅配置管理模式：可新建、编辑、导入导出宏，但不会在本地执行按键回放。";
    }

    private void UpdateGlobalUi()
    {
        GlobalStateText.Text = s_globalEnabled
            ? "全局宏已启用：试播与录制可正常使用，侧键绑定按各宏的触发方式生效。"
            : "全局宏已禁用：试播与录制已暂停，已保存的宏与绑定仍保留。";
        TestPlayBtn.IsEnabled = s_globalEnabled;
        RecordMacroBtn.IsEnabled = s_globalEnabled;
    }

    private void WithBinding(Action action)
    {
        bool previous = _binding;
        _binding = true;
        try { action(); }
        finally { _binding = previous; }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e) => StopRecording();

    // ── 全局开关与管理员重启 ─────────────────────────────────────────────────

    private void OnGlobalEnableChanged(object? sender, RoutedEventArgs e)
    {
        if (_binding) return;
        s_globalEnabled = GlobalEnableSwitch.IsChecked == true;
        if (s_globalEnabled) App.MacroRunner?.SyncHookState();
        else
        {
            App.MacroRunner?.CancelPlayback();
            StopRecording();
            PlaybackBar.IsVisible = false;
            PlaybackBar.IsIndeterminate = false;
        }
        UpdateGlobalUi();
    }

    private void OnRestartAsAdmin(object? sender, RoutedEventArgs e)
    {
        if (!MacroExecutionService.RestartAsAdmin())
            ShowStatus("已取消管理员重启，或启动失败。", InfoBarSeverity.Warning);
    }

    // ── 宏库 ─────────────────────────────────────────────────────────────────

    private void RenderMacroLibrary()
    {
        var macros = App.Settings.Current.Macros;
        if (macros.Count > 0 && (string.IsNullOrEmpty(_selectedMacroId) || macros.All(m => m.Id != _selectedMacroId)))
            _selectedMacroId = macros[0].Id;

        MacroLibraryHost.Children.Clear();
        foreach (var macro in macros.ToList())
        {
            bool selected = macro.Id == _selectedMacroId;
            string loopText = macro.LoopMode switch
            {
                MacroLoopMode.PlayOnce => "单次触发",
                MacroLoopMode.WhileHeld => "按住触发",
                MacroLoopMode.ToggleUntilKey => "切换触发 (+Esc 停)",
                _ => $"指定 {macro.RepeatCount} 次",
            };
            string bound = DescribeBindingsForMacro(macro.Id);
            string summary = $"{macro.Steps.Count} 步 · {macro.Steps.Sum(s => s.DelayMs)} ms · {loopText}";

            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(new TextBlock
            {
                Text = macro.Name,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            content.Children.Add(new TextBlock { Text = summary, FontSize = 12, Foreground = Brushes.Gray });
            if (!string.IsNullOrEmpty(bound))
                content.Children.Add(new TextBlock
                {
                    Text = $"已绑：{bound}",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.CornflowerBlue,
                });

            var buttons = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            var editBtn = ToolButton("编辑");
            editBtn.Click += (_, _) => SelectMacro(macro.Id);
            var playBtn = ToolButton("试播");
            playBtn.Click += (_, _) => _ = PlayMacroSafeAsync(macro, playBtn);
            var dupBtn = ToolButton("复制");
            dupBtn.Click += (_, _) => DuplicateMacro(macro);
            var delBtn = ToolButton("删除");
            delBtn.IsEnabled = macros.Count > 1;
            delBtn.Click += (_, _) => DeleteMacro(macro);
            buttons.Children.Add(editBtn);
            buttons.Children.Add(playBtn);
            buttons.Children.Add(dupBtn);
            buttons.Children.Add(delBtn);
            content.Children.Add(buttons);

            var card = new Border
            {
                Width = 236,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                Background = selected
                    ? new SolidColorBrush(Color.FromArgb(32, 56, 189, 248))
                    : Brushes.Transparent,
                BorderBrush = selected ? Brushes.CornflowerBlue : Brushes.Gray,
                BorderThickness = new Thickness(selected ? 2 : 1),
                Child = content,
            };
            MacroLibraryHost.Children.Add(card);
        }

        DeleteMacroBtn.IsEnabled = macros.Count > 1;
        CopyMacroBtn.IsEnabled = CurrentMacro != null;
    }

    private static Button ToolButton(string content)
        => new() { Content = content, Padding = new Thickness(10, 5), FontSize = 12 };

    private static string DescribeBindingsForMacro(string macroId)
    {
        var matches = App.Settings.Current.LocalButtonMacroBindings
            .Where(kv => string.Equals(kv.Value, macroId, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key.Contains('|') ? kv.Key.Split('|')[1] : kv.Key)
            .Distinct()
            .ToList();
        return matches.Count == 0 ? "（未绑定侧键）" : string.Join(", ", matches);
    }

    private void SelectMacro(string id)
    {
        _selectedMacroId = id;
        RenderMacroLibrary();
        BindEditorFields();
    }

    private void OnNewMacro(object? sender, RoutedEventArgs e)
    {
        var macro = new OnboardMacroDefinition
        {
            Name = $"自定义宏 {App.Settings.Current.Macros.Count + 1}",
            LoopMode = MacroLoopMode.PlayOnce,
            RepeatCount = 1,
        };
        App.Settings.Current.Macros.Add(macro);
        _selectedMacroId = macro.Id;
        PersistAndSync();
        ShowStatus($"已新建宏「{macro.Name}」。", InfoBarSeverity.Success);
    }

    private void OnCopyMacro(object? sender, RoutedEventArgs e)
    {
        if (CurrentMacro is { } source) DuplicateMacro(source);
    }

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
        _selectedMacroId = copy.Id;
        PersistAndSync();
        ShowStatus($"已复制为「{copy.Name}」。", InfoBarSeverity.Success);
    }

    private void OnDeleteMacro(object? sender, RoutedEventArgs e)
    {
        if (CurrentMacro is { } macro) DeleteMacro(macro);
    }

    private void DeleteMacro(OnboardMacroDefinition macro)
    {
        if (App.Settings.Current.Macros.Count <= 1)
        {
            ShowStatus("至少需要保留一个宏。", InfoBarSeverity.Warning);
            return;
        }
        App.Settings.Current.Macros.Remove(macro);
        var keys = App.Settings.Current.LocalButtonMacroBindings
            .Where(kv => string.Equals(kv.Value, macro.Id, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();
        foreach (string key in keys)
            App.Settings.Current.LocalButtonMacroBindings.Remove(key);
        _selectedMacroId = App.Settings.Current.Macros[0].Id;
        PersistAndSync();
        ShowStatus($"已删除宏「{macro.Name}」及其绑定。", InfoBarSeverity.Informational);
    }

    private void PersistAndSync()
    {
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderMacroLibrary();
        BindEditorFields();
    }

    // ── 编辑器字段 ───────────────────────────────────────────────────────────

    private void BindEditorFields()
    {
        var macro = CurrentMacro;
        WithBinding(() =>
        {
            if (macro == null)
            {
                EditorTitleText.Text = "宏动作录制与编辑";
                MacroNameBox.Text = "";
                return;
            }

            EditorTitleText.Text = $"宏动作编辑 · {macro.Name}";
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
        BindingSummaryText.Text = DescribeBindingsForMacro(macro?.Id ?? "");
    }

    private void CommitMacroName()
    {
        var macro = CurrentMacro;
        if (macro == null) return;
        if (!string.IsNullOrWhiteSpace(MacroNameBox.Text))
            macro.Name = MacroNameBox.Text.Trim();
        App.Settings.Save();
        RenderMacroLibrary();
        EditorTitleText.Text = $"宏动作编辑 · {macro.Name}";
    }

    private void OnLoopModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_binding || CurrentMacro is not { } macro) return;
        if (MacroLoopCombo.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<MacroLoopMode>(item.Tag as string, out var mode))
            macro.LoopMode = mode;
        MacroRepeatBox.IsEnabled = macro.LoopMode == MacroLoopMode.RepeatCount;
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderMacroLibrary();
    }

    private void OnRepeatValueChanged()
    {
        double raw = MacroRepeatBox.Value;
        if (_binding || CurrentMacro is not { } macro || double.IsNaN(raw)) return;
        macro.RepeatCount = Math.Clamp((int)Math.Round(raw), 1, 99);
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderMacroLibrary();
    }

    private static int ReadStepDelay(NumberBox box)
        => double.IsFinite(box.Value)
            ? Math.Clamp((int)Math.Round(box.Value), 5, 5000)
            : 25;

    // ── 动作序列 ─────────────────────────────────────────────────────────────

    private static string StepDescription(int index, MacroStep step)
        => step.KeyCode == 0
            ? $"#{index + 1}  延时等待 → {step.DelayMs} ms"
            : $"#{index + 1}  {step.DisplayName}  {(step.IsPress ? "按下 ↓" : "松开 ↑")}  → 延迟 {step.DelayMs} ms";

    private void RenderSteps(OnboardMacroDefinition? macro)
    {
        MacroStepsPanel.Children.Clear();
        if (macro == null || macro.Steps.Count == 0)
        {
            MacroStepsPanel.Children.Add(new TextBlock
            {
                Text = "当前宏暂无动作步骤；可先录制，或用上方按钮逐个添加按键按下/抬起、鼠标点击与延时。",
                FontSize = 12,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        for (int i = 0; i < macro.Steps.Count; i++)
        {
            int index = i;
            var step = macro.Steps[i];

            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            left.Children.Add(new TextBlock
            {
                Text = StepDescription(index, step),
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            var delayBox = new NumberBox { Width = 104, Minimum = 0, Maximum = 5000 };
            delayBox.Value = step.DelayMs;
            delayBox.ValueChanged += (_, _) =>
            {
                if (_binding) return;
                if (double.IsFinite(delayBox.Value))
                    step.DelayMs = Math.Clamp((int)Math.Round(delayBox.Value), 0, 5000);
            };
            left.Children.Add(delayBox);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            };
            var up = new Button { Content = "↑", FontSize = 12, Padding = new Thickness(8, 2, 8, 2), IsEnabled = index > 0 };
            up.Click += (_, _) => MoveStep(index, -1);
            var down = new Button { Content = "↓", FontSize = 12, Padding = new Thickness(8, 2, 8, 2), IsEnabled = index < macro.Steps.Count - 1 };
            down.Click += (_, _) => MoveStep(index, 1);
            var remove = new Button { Content = "删", FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
            remove.Click += (_, _) => RemoveStep(index);
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

    private void MoveStep(int index, int delta)
    {
        if (CurrentMacro is not { } macro) return;
        int target = index + delta;
        if (target < 0 || target >= macro.Steps.Count) return;
        (macro.Steps[index], macro.Steps[target]) = (macro.Steps[target], macro.Steps[index]);
        CommitSteps(macro);
    }

    private void RemoveStep(int index)
    {
        if (CurrentMacro is not { } macro) return;
        if (index < 0 || index >= macro.Steps.Count) return;
        macro.Steps.RemoveAt(index);
        CommitSteps(macro);
    }

    private void CommitSteps(OnboardMacroDefinition macro)
    {
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderSteps(macro);
        RenderMacroLibrary();
    }

    private byte SelectedManualUsage
        => ManualKeyCombo.SelectedItem is ComboBoxItem item && item.Tag is byte usage
            ? usage
            : (byte)0x04;

    private void OnAddKeyPress(object? sender, RoutedEventArgs e) => AddStep(SelectedManualUsage, true);

    private void OnAddKeyRelease(object? sender, RoutedEventArgs e) => AddStep(SelectedManualUsage, false);

    private void OnAddLeftClick(object? sender, RoutedEventArgs e) => AddClickPair(HidKeyCatalog.MouseLeftCode);

    private void OnAddRightClick(object? sender, RoutedEventArgs e) => AddClickPair(HidKeyCatalog.MouseRightCode);

    private void OnAddMiddleClick(object? sender, RoutedEventArgs e) => AddClickPair(HidKeyCatalog.MouseMiddleCode);

    private void OnAddDelay(object? sender, RoutedEventArgs e)
    {
        if (CurrentMacro is not { } macro) return;
        macro.Steps.Add(new MacroStep(0, false, ReadStepDelay(StepDelayBox)));
        CommitSteps(macro);
    }

    private void AddStep(byte usage, bool isPress)
    {
        if (CurrentMacro is not { } macro) return;
        macro.Steps.Add(new MacroStep(usage, isPress, ReadStepDelay(StepDelayBox)));
        CommitSteps(macro);
    }

    private void AddClickPair(byte usage)
    {
        if (CurrentMacro is not { } macro) return;
        int delay = ReadStepDelay(StepDelayBox);
        macro.Steps.Add(new MacroStep(usage, true, delay));
        macro.Steps.Add(new MacroStep(usage, false, delay));
        CommitSteps(macro);
    }

    private void OnClearSteps(object? sender, RoutedEventArgs e)
    {
        if (CurrentMacro is not { } macro) return;
        macro.Steps.Clear();
        CommitSteps(macro);
    }

    private void OnSaveMacro(object? sender, RoutedEventArgs e)
    {
        if (CurrentMacro is not { } macro) return;
        if (!string.IsNullOrWhiteSpace(MacroNameBox.Text))
            macro.Name = MacroNameBox.Text.Trim();
        PersistAndSync();
        ShowStatus($"宏「{macro.Name}」已保存并同步触发配置。", InfoBarSeverity.Success);
    }

    // ── 录制 ─────────────────────────────────────────────────────────────────

    private void OnRecordClick(object? sender, RoutedEventArgs e)
    {
        if (_binding) return;
        if (RecordMacroBtn.IsChecked == true)
        {
            if (!s_globalEnabled)
            {
                ShowStatus("全局宏已禁用，请先在上方启用后再录制。", InfoBarSeverity.Warning);
                RecordMacroBtn.IsChecked = false;
                return;
            }
            StartRecording();
        }
        else
            StopRecording();
    }

    private void StartRecording()
    {
        _recording = true;
        _recWatch.Restart();
        _lastRecMs = 0;
        RecordMacroBtn.Content = "停止录制";
        RecordFocusTrap.IsVisible = true;
        RecordFocusTrap.Focus();
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        _recWatch.Stop();
        RecordMacroBtn.IsChecked = false;
        RecordMacroBtn.Content = "开始录制键盘按键与鼠标点击";
        RecordFocusTrap.IsVisible = false;
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        var macro = CurrentMacro;
        if (macro != null)
        {
            RenderSteps(macro);
            RenderMacroLibrary();
        }
    }

    private void OnRecorderKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_recording || CurrentMacro == null) return;
        e.Handled = true;
        if (AvaloniaKeyUsageMap.TryGetValue(e.Key.ToString(), out byte usage))
            RecordCodeEvent(usage, isPress: true);
    }

    private void OnRecorderKeyUp(object? sender, KeyEventArgs e)
    {
        if (!_recording || CurrentMacro == null) return;
        e.Handled = true;
        if (AvaloniaKeyUsageMap.TryGetValue(e.Key.ToString(), out byte usage))
            RecordCodeEvent(usage, isPress: false);
    }

    private void OnRecorderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_recording || CurrentMacro == null) return;
        e.Handled = true;
        RecordFocusTrap.Focus();
        byte? code = e.GetCurrentPoint(RecordFocusTrap).Properties.PointerUpdateKind switch
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
        if (!_recording || CurrentMacro == null) return;
        e.Handled = true;
        byte? code = e.GetCurrentPoint(RecordFocusTrap).Properties.PointerUpdateKind switch
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

    private void RecordCodeEvent(byte usage, bool isPress)
    {
        if (CurrentMacro is not { } macro) return;
        long now = _recWatch.ElapsedMilliseconds;
        int delay = macro.Steps.Count > 0 ? Math.Clamp((int)(now - _lastRecMs), 5, 5000) : 15;
        if (macro.Steps.Count > 0)
            macro.Steps[^1].DelayMs = delay;
        macro.Steps.Add(new MacroStep(usage, isPress, 15));
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

    private async void OnTestPlayClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await PlayMacroSafeAsync(CurrentMacro, sender as Button);
        }
        catch (Exception ex)
        {
            ShowStatus($"回放失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnStopPlayClick(object? sender, RoutedEventArgs e)
    {
        App.MacroRunner?.CancelPlayback();
        PlaybackBar.IsIndeterminate = false;
        PlaybackBar.IsVisible = false;
        ShowStatus("已停止回放。", InfoBarSeverity.Informational);
    }

    private async Task PlayMacroSafeAsync(OnboardMacroDefinition? macro, Button? button)
    {
        if (_busyTest) return;
        if (macro == null || macro.Steps.Count == 0)
        {
            ShowStatus("请先选择包含动作步骤的宏。", InfoBarSeverity.Warning);
            return;
        }
        if (!s_globalEnabled)
        {
            ShowStatus("全局宏已禁用，请先在上方启用。", InfoBarSeverity.Warning);
            return;
        }
        if (App.MacroRunner == null || !App.MacroRunner.IsPlaybackSupported)
        {
            ShowStatus(MacroExecutionService.MacOsPlaybackNotice, InfoBarSeverity.Informational);
            return;
        }

        _busyTest = true;
        if (button != null) button.IsEnabled = false;
        PlaybackBar.IsVisible = true;
        PlaybackBar.IsIndeterminate = true;
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
            PlaybackBar.IsIndeterminate = false;
            PlaybackBar.IsVisible = false;
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
            _selectedMacroId = App.Settings.Current.Macros.LastOrDefault()?.Id;
            PersistAndSync();
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

    // ── 响应式布局与状态 ─────────────────────────────────────────────────────

    private void OnWorkspaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (WorkspaceGrid.ColumnDefinitions.Count < 2) return;
        if (e.NewSize.Width < 900)
        {
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetRow(EditorPane, 1);
            Grid.SetColumn(EditorPane, 0);
        }
        else
        {
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1.05, GridUnitType.Star);
            WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(1.25, GridUnitType.Star);
            Grid.SetRow(EditorPane, 0);
            Grid.SetColumn(EditorPane, 1);
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = !string.IsNullOrWhiteSpace(message);
    }
}
