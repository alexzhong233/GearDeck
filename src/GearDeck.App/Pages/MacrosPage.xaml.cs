using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using GearDeck.App.Controls;
using GearDeck.App.Services;
using GearDeck.Core;
using GearDeck.Core.Models;
using Windows.System;

namespace GearDeck.App.Pages;

public sealed partial class MacrosPage : Page
{
    private string? _selectedMacroId;
    private DeviceTuningCapabilities? _probedCaps;
    private bool _isCardMode;
    private bool _binding;
    private bool _recording;
    private bool _isTwoCol = true;
    private readonly Stopwatch _recWatch = new();
    private long _lastRecMs;

    public MacrosPage()
    {
        InitializeComponent();
        _isCardMode = App.Settings.Current.UseCardLayout;
        MacroLibraryHost.IsCardMode = _isCardMode;

        ViewSwitchSlot.Children.Add(UiBuilders.CreateViewModeSwitcher(_isCardMode, isCard =>
        {
            _isCardMode = isCard;
            MacroLibraryHost.IsCardMode = isCard;
            RenderMacroLibrary();
        }));

        PopulateManualKeyCombo();
        RecordFocusTrap.ContextRequested += (_, args) => args.Handled = true;
        SizeChanged += (_, e) => UpdateWorkspaceLayout(e.NewSize.Width);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is string preselectDeviceId && !string.IsNullOrWhiteSpace(preselectDeviceId))
            RefreshTargetMice(preselectDeviceId);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isCardMode = App.Settings.Current.UseCardLayout;
        MacroLibraryHost.IsCardMode = _isCardMode;
        AdminPrivilegeBanner.IsOpen = !MacroExecutionService.IsProcessElevated();
        App.Devices.DevicesUpdated += OnDevicesUpdated;
        UpdateWorkspaceLayout(ActualWidth);
        EnsureAtLeastOneMacro();
        RenderMacroLibrary();
        BindEditorFields();
        RefreshTargetMice(null);
    }

    private void OnRestartAsAdmin(object sender, RoutedEventArgs e)
        => MacroExecutionService.RestartAsAdmin();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Devices.DevicesUpdated -= OnDevicesUpdated;
        StopRecording();
    }

    private void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> _)
    {
        App.UiQueue?.TryEnqueue(() => RefreshTargetMice(null));
    }

    private void UpdateWorkspaceLayout(double width)
    {
        bool twoCol = width >= 980;
        if (twoCol == _isTwoCol && WorkspaceGrid.ColumnDefinitions.Count > 0) return;
        _isTwoCol = twoCol;

        WorkspaceGrid.ColumnDefinitions.Clear();
        WorkspaceGrid.RowDefinitions.Clear();
        if (twoCol)
        {
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(LibraryPane, 0);
            Grid.SetColumn(LibraryPane, 0);
            Grid.SetRow(EditorPane, 0);
            Grid.SetColumn(EditorPane, 1);
            MacroLibraryHost.MinCardWidth = 220;
        }
        else
        {
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(LibraryPane, 0);
            Grid.SetColumn(LibraryPane, 0);
            Grid.SetRow(EditorPane, 1);
            Grid.SetColumn(EditorPane, 0);
            MacroLibraryHost.MinCardWidth = 260;
        }
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

    private OnboardMacroDefinition? CurrentMacro
    {
        get
        {
            var list = App.Settings.Current.Macros;
            if (list.Count == 0) return null;
            return list.FirstOrDefault(m => m.Id == _selectedMacroId) ?? list[0];
        }
    }

    private void RenderMacroLibrary()
    {
        var macros = App.Settings.Current.Macros;
        if (macros.Count > 0 && (string.IsNullOrEmpty(_selectedMacroId) || macros.All(m => m.Id != _selectedMacroId)))
            _selectedMacroId = macros[0].Id;

        var items = new List<UIElement>(macros.Count);
        foreach (var m in macros.ToList())
        {
            bool selected = m.Id == _selectedMacroId;
            int totalMs = m.Steps.Sum(s => s.DelayMs);
            string loopText = m.LoopMode switch
            {
                MacroLoopMode.PlayOnce => "单次触发",
                MacroLoopMode.WhileHeld => "按住触发",
                MacroLoopMode.ToggleUntilKey => "切换触发 (+Esc 停)",
                _ => $"指定 {m.RepeatCount} 次",
            };

            string boundSummary = DescribeBindingsForMacro(m.Id);

            var border = new Border
            {
                Background = selected
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(32, 56, 189, 248))
                    : (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderBrush = selected
                    ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                    : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(selected ? 2 : 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
            };

            if (_isCardMode)
            {
                var stack = new StackPanel { Spacing = 8 };
                stack.Children.Add(new TextBlock { Text = m.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                stack.Children.Add(new TextBlock
                {
                    Text = $"{m.Steps.Count} 步 · {totalMs} ms · {loopText}",
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
                if (!string.IsNullOrEmpty(boundSummary))
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = $"已绑：{boundSummary}",
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                    });
                }

                var btns = new ResponsiveWrapPanel { Spacing = 6, LineSpacing = 6 };
                var editBtn = new Button { Content = "编辑", Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
                editBtn.Click += (_, _) => SelectMacro(m.Id);
                var playBtn = new Button { Content = "试播", Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
                playBtn.Click += (_, _) => _ = App.MacroRunner?.PlayMacroAsync(m);
                var delBtn = new Button { Content = "删", Padding = new Thickness(8, 4, 8, 4), FontSize = 12, IsEnabled = macros.Count > 1 };
                delBtn.Click += (_, _) => DeleteMacro(m);
                btns.Children.Add(editBtn);
                btns.Children.Add(playBtn);
                btns.Children.Add(delBtn);
                stack.Children.Add(btns);
                border.Child = stack;
            }
            else
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold });
                info.Children.Add(new TextBlock
                {
                    Text = $"{m.Steps.Count} 步 · {totalMs} ms · {loopText}" + (string.IsNullOrEmpty(boundSummary) ? "" : $" · 已绑 {boundSummary}"),
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextWrapping = TextWrapping.Wrap,
                });
                row.Children.Add(info);

                var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                var editBtn = new Button { Content = "编辑", Padding = new Thickness(10, 4, 10, 4) };
                editBtn.Click += (_, _) => SelectMacro(m.Id);
                var playBtn = new Button { Content = "试播", Padding = new Thickness(10, 4, 10, 4) };
                playBtn.Click += (_, _) => _ = App.MacroRunner?.PlayMacroAsync(m);
                var delBtn = new Button { Content = "删除", Padding = new Thickness(8, 4, 8, 4), IsEnabled = macros.Count > 1 };
                delBtn.Click += (_, _) => DeleteMacro(m);
                btns.Children.Add(editBtn);
                btns.Children.Add(playBtn);
                btns.Children.Add(delBtn);
                Grid.SetColumn(btns, 1);
                row.Children.Add(btns);
                border.Child = row;
            }

            items.Add(border);
        }

        MacroLibraryHost.SetItems(items);
    }

    private static string DescribeBindingsForMacro(string macroId)
    {
        var matches = App.Settings.Current.LocalButtonMacroBindings
            .Where(kv => string.Equals(kv.Value, macroId, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key.Contains('|') ? kv.Key.Split('|')[1] : kv.Key)
            .Distinct()
            .ToList();
        return matches.Count == 0 ? "" : string.Join(", ", matches);
    }

    private void SelectMacro(string id)
    {
        _selectedMacroId = id;
        BindEditorFields();
        RenderMacroLibrary();
    }

    private void BindEditorFields()
    {
        var m = CurrentMacro;
        if (m == null) return;
        _binding = true;
        try
        {
            EditorTitleText.Text = $"宏动作编辑 · {m.Name}";
            MacroNameBox.Text = m.Name;
            string wantedTag = m.LoopMode.ToString();
            int selIdx = 0;
            for (int i = 0; i < MacroLoopCombo.Items.Count; i++)
            {
                if (MacroLoopCombo.Items[i] is ComboBoxItem ci &&
                    string.Equals(ci.Tag as string, wantedTag, StringComparison.OrdinalIgnoreCase))
                {
                    selIdx = i;
                    break;
                }
            }
            MacroLoopCombo.SelectedIndex = selIdx;
            MacroRepeatBox.Value = Math.Clamp(m.RepeatCount, 1, 99);
            MacroRepeatBox.IsEnabled = m.LoopMode == MacroLoopMode.RepeatCount;
            RenderMacroSteps(m);
        }
        finally
        {
            _binding = false;
        }
    }

    private void RenderMacroSteps(OnboardMacroDefinition m)
    {
        MacroStepsPanel.Children.Clear();
        if (m.Steps.Count == 0)
        {
            MacroStepsPanel.Children.Add(new TextBlock
            {
                Text = "当前宏暂无动作步骤；点击上方「开始录制键盘按键与延迟」或手动添加点击动作。",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            return;
        }

        for (int i = 0; i < m.Steps.Count; i++)
        {
            int idx = i;
            var s = m.Steps[i];
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = $"#{i + 1}  {s.DisplayName}  {(s.IsPress ? "按下 ↓" : "松开 ↑")}  →  延迟 {s.DelayMs} ms",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var del = new Button { Content = "删", Padding = new Thickness(8, 2, 8, 2), FontSize = 11 };
            del.Click += (_, _) =>
            {
                if (idx < m.Steps.Count)
                {
                    m.Steps.RemoveAt(idx);
                    App.Settings.Save();
                    RenderMacroSteps(m);
                    RenderMacroLibrary();
                }
            };
            Grid.SetColumn(del, 1);
            row.Children.Add(del);
            MacroStepsPanel.Children.Add(row);
        }
    }

    private void PopulateManualKeyCombo()
    {
        ManualKeyCombo.Items.Clear();
        foreach (var k in HidKeyCatalog.AllKeys.Where(k => k.Usage < 0xF0))
            ManualKeyCombo.Items.Add(new ComboBoxItem { Content = k.Label, Tag = k.Usage });
        if (ManualKeyCombo.Items.Count > 0)
            ManualKeyCombo.SelectedIndex = 0;
    }

    private void RefreshTargetMice(string? preferDeviceId)
    {
        string? curSelectedId = preferDeviceId ?? (TargetMouseCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var mice = App.LastDevices.Where(d => d.Kind == DeviceKind.Mouse).OrderBy(d => d.IsSleeping).ThenBy(d => d.Name).ToList();

        _binding = true;
        try
        {
            TargetMouseCombo.Items.Clear();
            int selIdx = -1;
            for (int i = 0; i < mice.Count; i++)
            {
                var m = mice[i];
                string state = m.IsSleeping ? " [休眠]" : " [在线]";
                TargetMouseCombo.Items.Add(new ComboBoxItem { Content = $"{m.Name} ({m.Provider}){state}", Tag = m.Id });
                if (curSelectedId != null && string.Equals(curSelectedId, m.Id, StringComparison.OrdinalIgnoreCase))
                    selIdx = i;
            }
            if (selIdx < 0 && mice.Count > 0) selIdx = 0;
            TargetMouseCombo.SelectedIndex = selIdx;
        }
        finally
        {
            _binding = false;
        }

        UpdateTargetButtonsForSelectedMouse();
    }

    private void OnTargetMouseChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_binding) return;
        _probedCaps = null;
        UpdateTargetButtonsForSelectedMouse();
    }

    private void OnTargetButtonChanged(object sender, SelectionChangedEventArgs e) => UpdateEnrollCapabilityNote();

    private void UpdateTargetButtonsForSelectedMouse()
    {
        TargetButtonCombo.Items.Clear();
        if (_probedCaps is { ButtonBindings.Count: > 0 })
        {
            foreach (var b in _probedCaps.ButtonBindings.Where(x => !x.IsReadOnly))
                TargetButtonCombo.Items.Add(new ComboBoxItem { Content = b.ButtonName, Tag = (b.SlotIndex, b.ButtonName) });
        }
        else
        {
            TargetButtonCombo.Items.Add(new ComboBoxItem { Content = "侧键后退 (M4 / XButton1)", Tag = (4, "XButton1") });
            TargetButtonCombo.Items.Add(new ComboBoxItem { Content = "侧键前进 (M5 / XButton2)", Tag = (5, "XButton2") });
            TargetButtonCombo.Items.Add(new ComboBoxItem { Content = "滚轮中键 (Middle)", Tag = (3, "Middle") });
        }
        if (TargetButtonCombo.Items.Count > 0)
            TargetButtonCombo.SelectedIndex = 0;

        UpdateEnrollCapabilityNote();
    }

    private void UpdateEnrollCapabilityNote()
    {
        string? devId = (TargetMouseCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrEmpty(devId))
        {
            EnrollCapabilityText.Text = "当前未发现鼠标设备；请连接接收器或有线鼠标后再试。";
            EnrollBtn.IsEnabled = false;
            return;
        }

        var dev = App.LastDevices.FirstOrDefault(d => d.Id == devId);
        EnrollBtn.IsEnabled = true;

        if (dev?.IsSleeping == true)
        {
            EnrollCapabilityText.Text = "目标鼠标当前处于休眠状态：如需写入硬件板载存储请先晃动唤醒；若直接点击录入，将自动为其绑定侧键/中键软件触发（唤醒后立即可用）。";
            EnrollSlotBox.IsEnabled = false;
            return;
        }

        if (_probedCaps is { SupportsOnboardMacroUpload: true })
        {
            EnrollCapabilityText.Text = $"硬件已就绪：此设备支持原生 {_probedCaps.OnboardMacroSlotCount} 槽位 320B 闪存板载宏，点击下方将直接烧录进鼠标硬件！";
            EnrollSlotBox.IsEnabled = true;
            return;
        }

        if (_probedCaps is { ButtonBindings.Count: > 0 })
        {
            EnrollCapabilityText.Text = "此鼠标支持硬件板载改键与单/组合键写入；多步带延迟复杂宏点击录入将自动配置为该设备的无阻塞侧键触发。";
            EnrollSlotBox.IsEnabled = false;
            return;
        }

        EnrollCapabilityText.Text = "可先点击「探测该鼠标硬件支持能力」读取板载宏/按键寄存器；或直接点击录入（自动择优写入硬件或侧键触发）。";
        EnrollSlotBox.IsEnabled = true;
    }

    private async void OnProbeTargetMouse(object sender, RoutedEventArgs e)
    {
        string? devId = (TargetMouseCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrEmpty(devId)) return;

        var caps = await App.Devices.QueryCapabilitiesAsync(devId);
        _probedCaps = caps;
        UpdateTargetButtonsForSelectedMouse();
        ShowStatus(caps.IsOnline
            ? $"已探测设备硬件能力：按键槽位 {caps.ButtonBindings.Count} 个，{(caps.SupportsOnboardMacroUpload ? "支持原生板载宏烧录" : "支持改键/侧键触发")}。"
            : $"设备当前休眠：{caps.StatusMessage}",
            caps.IsOnline ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private async void OnEnrollMacroToDevice(object sender, RoutedEventArgs e)
    {
        var macro = CurrentMacro;
        string? devId = (TargetMouseCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (macro == null || macro.Steps.Count == 0 || string.IsNullOrEmpty(devId))
        {
            ShowStatus("请先选择宏（且至少包含 1 个动作步骤）和目标鼠标设备。", InfoBarSeverity.Warning);
            return;
        }

        var dev = App.LastDevices.FirstOrDefault(d => d.Id == devId);
        int slotIdx = double.IsNaN(EnrollSlotBox.Value) ? 0 : Math.Clamp((int)Math.Round(EnrollSlotBox.Value), 0, 7);
        string btnText = (TargetButtonCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "XButton1";
        int wireSlot = TargetButtonCombo.SelectedItem is ComboBoxItem ci && ci.Tag is ValueTuple<int, string> tup ? tup.Item1 : 4;

        // 1. 先探测一次，看目标鼠当前是否醒着且支持 320B 原生板载宏烧录
        if (dev is { IsSleeping: false })
        {
            _probedCaps ??= await App.Devices.QueryCapabilitiesAsync(devId);
            if (_probedCaps is { IsOnline: true, SupportsOnboardMacroUpload: true })
            {
                var res = await App.Devices.ApplySettingsAsync(devId, new DeviceTuningUpdate
                {
                    UploadMacro = (slotIdx, wireSlot, macro),
                });
                ShowStatus(res.Message, res.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
                RenderMacroLibrary();
                return;
            }

            // 如果是单个键或标准修饰+键组合，尝试直接写入鼠标板载按键映射
            if (_probedCaps is { IsOnline: true, ButtonBindings.Count: > 0 } && TryExtractSimpleCombo(macro, out byte mods, out byte usage))
            {
                string hwBinding = HidKeyCatalog.FormatKeyboardBinding(mods, usage);
                var res = await App.Devices.ApplySettingsAsync(devId, new DeviceTuningUpdate
                {
                    ButtonBinding = (wireSlot, hwBinding),
                });
                if (res.Success)
                {
                    ShowStatus($"已将单/组合快捷键「{HidKeyCatalog.FormatModifiersAndKey(mods, usage)}」直接写入「{dev.Name}」硬件板载内存！", InfoBarSeverity.Success);
                    return;
                }
            }
        }

        // 2. 通用：绑定为目标设备对应的鼠标物理侧键/中键软件触发
        string? triggerKey = ResolveSoftwareTriggerKey(btnText, wireSlot);
        if (triggerKey == null)
        {
            ShowStatus("该固件未开放多步闪存烧录，软件触发仅可绑定至 侧键4(后退) / 侧键5(前进) / 中键。", InfoBarSeverity.Warning);
            return;
        }

        string key = $"{devId}|{triggerKey}";
        App.Settings.Current.LocalButtonMacroBindings[key] = macro.Id;
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderMacroLibrary();
        ShowStatus($"已将宏「{macro.Name}」录入并绑定到「{dev?.Name ?? devId}」的 {triggerKey} 按键！", InfoBarSeverity.Success);
    }

    private static string? ResolveSoftwareTriggerKey(string buttonLabel, int wireSlot)
    {
        if (buttonLabel.Contains("后退", StringComparison.OrdinalIgnoreCase) || buttonLabel.Contains("XButton1", StringComparison.OrdinalIgnoreCase) || buttonLabel.Contains("M4", StringComparison.OrdinalIgnoreCase))
            return "XButton1";
        if (buttonLabel.Contains("前进", StringComparison.OrdinalIgnoreCase) || buttonLabel.Contains("XButton2", StringComparison.OrdinalIgnoreCase) || buttonLabel.Contains("M5", StringComparison.OrdinalIgnoreCase))
            return "XButton2";
        if (buttonLabel.Contains("中", StringComparison.OrdinalIgnoreCase) || buttonLabel.Contains("Middle", StringComparison.OrdinalIgnoreCase))
            return "Middle";
        return wireSlot switch { 4 => "XButton1", 5 => "XButton2", 3 => "Middle", _ => null };
    }

    private static bool TryExtractSimpleCombo(OnboardMacroDefinition macro, out byte modifiers, out byte usage)
    {
        modifiers = 0;
        usage = 0;
        if (macro.LoopMode is not (MacroLoopMode.PlayOnce or MacroLoopMode.RepeatCount) ||
            (macro.LoopMode == MacroLoopMode.RepeatCount && macro.RepeatCount != 1) ||
            macro.Steps.Count > 6)
            return false;

        var nonModKeys = new List<byte>();
        foreach (var s in macro.Steps.Where(x => x.IsPress))
        {
            if (s.KeyCode >= 0xF0) return false; // mouse clicks stay multi-step/software
            if (s.KeyCode == 0xE0) modifiers |= HidKeyCatalog.ModLeftCtrl;
            else if (s.KeyCode == 0xE1) modifiers |= HidKeyCatalog.ModLeftShift;
            else if (s.KeyCode == 0xE2) modifiers |= HidKeyCatalog.ModLeftAlt;
            else if (s.KeyCode == 0xE3) modifiers |= HidKeyCatalog.ModLeftWin;
            else nonModKeys.Add(s.KeyCode);
        }
        if (nonModKeys.Count != 1) return false;
        usage = nonModKeys[0];
        return usage != 0;
    }

    private void OnUnbindSelectedDeviceButton(object sender, RoutedEventArgs e)
    {
        string? devId = (TargetMouseCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrEmpty(devId)) return;
        string btnText = (TargetButtonCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "";
        int wireSlot = TargetButtonCombo.SelectedItem is ComboBoxItem ci && ci.Tag is ValueTuple<int, string> tup ? tup.Item1 : 4;
        string? triggerKey = ResolveSoftwareTriggerKey(btnText, wireSlot);
        if (triggerKey == null) return;

        bool removed = App.Settings.Current.LocalButtonMacroBindings.Remove($"{devId}|{triggerKey}");
        removed |= App.Settings.Current.LocalButtonMacroBindings.Remove(triggerKey);
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderMacroLibrary();
        ShowStatus(removed ? $"已解除 {triggerKey} 的宏绑定。" : $"该键未绑定本地宏。", InfoBarSeverity.Informational);
    }

    private void OnNewMacro(object sender, RoutedEventArgs e)
    {
        var m = new OnboardMacroDefinition
        {
            Name = $"自定义宏 {App.Settings.Current.Macros.Count + 1}",
            LoopMode = MacroLoopMode.PlayOnce,
            RepeatCount = 1,
        };
        App.Settings.Current.Macros.Add(m);
        _selectedMacroId = m.Id;
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderMacroLibrary();
        BindEditorFields();
    }

    private void DeleteMacro(OnboardMacroDefinition m)
    {
        if (App.Settings.Current.Macros.Count <= 1) return;
        App.Settings.Current.Macros.Remove(m);
        var keys = App.Settings.Current.LocalButtonMacroBindings
            .Where(kv => kv.Value == m.Id).Select(kv => kv.Key).ToList();
        foreach (string k in keys) App.Settings.Current.LocalButtonMacroBindings.Remove(k);
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        _selectedMacroId = App.Settings.Current.Macros[0].Id;
        RenderMacroLibrary();
        BindEditorFields();
    }

    private void OnMacroPropertyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_binding || CurrentMacro == null) return;
        string? tag = (MacroLoopCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (Enum.TryParse<MacroLoopMode>(tag, out var parsedMode))
            CurrentMacro.LoopMode = parsedMode;
        MacroRepeatBox.IsEnabled = CurrentMacro.LoopMode == MacroLoopMode.RepeatCount;
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderMacroLibrary();
    }

    private void OnMacroRepeatChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_binding || CurrentMacro == null || double.IsNaN(args.NewValue)) return;
        CurrentMacro.RepeatCount = Math.Clamp((int)Math.Round(args.NewValue), 1, 99);
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderMacroLibrary();
    }

    private void OnRecordStart(object sender, RoutedEventArgs e)
    {
        _recording = true;
        _recWatch.Restart();
        _lastRecMs = 0;
        RecordMacroBtn.Content = "停止录制";
        RecordFocusTrap.Visibility = Visibility.Visible;
        RecordFocusTrap.Focus(FocusState.Programmatic);
    }

    private void OnRecordStop(object sender, RoutedEventArgs e) => StopRecording();

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        _recWatch.Stop();
        RecordMacroBtn.IsChecked = false;
        RecordMacroBtn.Content = "开始录制键盘按键与鼠标点击";
        RecordFocusTrap.Visibility = Visibility.Collapsed;
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderMacroLibrary();
    }

    private void OnRecorderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording || CurrentMacro == null) return;
        e.Handled = true;
        if (e.KeyStatus.WasKeyDown) return;
        RecordKeyEvent(e.Key, isPress: true);
    }

    private void OnRecorderKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording || CurrentMacro == null) return;
        e.Handled = true;
        RecordKeyEvent(e.Key, isPress: false);
    }

    private void OnRecorderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_recording || CurrentMacro == null) return;
        e.Handled = true;
        RecordFocusTrap.Focus(FocusState.Programmatic);
        byte? code = e.GetCurrentPoint(RecordFocusTrap).Properties.PointerUpdateKind switch
        {
            Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed => HidKeyCatalog.MouseLeftCode,
            Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed => HidKeyCatalog.MouseRightCode,
            Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed => HidKeyCatalog.MouseMiddleCode,
            Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed => HidKeyCatalog.MouseXButton1Code,
            Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed => HidKeyCatalog.MouseXButton2Code,
            _ => null,
        };
        if (code is { } c)
            RecordCodeEvent(c, isPress: true);
    }

    private void OnRecorderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_recording || CurrentMacro == null) return;
        e.Handled = true;
        byte? code = e.GetCurrentPoint(RecordFocusTrap).Properties.PointerUpdateKind switch
        {
            Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased => HidKeyCatalog.MouseLeftCode,
            Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased => HidKeyCatalog.MouseRightCode,
            Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased => HidKeyCatalog.MouseMiddleCode,
            Microsoft.UI.Input.PointerUpdateKind.XButton1Released => HidKeyCatalog.MouseXButton1Code,
            Microsoft.UI.Input.PointerUpdateKind.XButton2Released => HidKeyCatalog.MouseXButton2Code,
            _ => null,
        };
        if (code is { } c)
            RecordCodeEvent(c, isPress: false);
    }

    private void RecordKeyEvent(VirtualKey key, bool isPress)
    {
        byte? usage = HidKeyCatalog.UsageFromVirtualKey((int)key);
        if (usage is { } u)
            RecordCodeEvent(u, isPress);
    }

    private void RecordCodeEvent(byte usage, bool isPress)
    {
        var m = CurrentMacro;
        if (m == null) return;

        long now = _recWatch.ElapsedMilliseconds;
        int delay = m.Steps.Count > 0 ? Math.Clamp((int)(now - _lastRecMs), 5, 5000) : 15;
        if (m.Steps.Count > 0)
            m.Steps[^1].DelayMs = delay;

        m.Steps.Add(new MacroStep(usage, isPress, 15));
        _lastRecMs = now;
        RenderMacroSteps(m);
    }

    private void OnAddLeftClickStep(object sender, RoutedEventArgs e) => AddClickPair(HidKeyCatalog.MouseLeftCode);
    private void OnAddRightClickStep(object sender, RoutedEventArgs e) => AddClickPair(HidKeyCatalog.MouseRightCode);
    private void OnAddMiddleClickStep(object sender, RoutedEventArgs e) => AddClickPair(HidKeyCatalog.MouseMiddleCode);

    private void OnAddManualKeyClick(object sender, RoutedEventArgs e)
    {
        if (ManualKeyCombo.SelectedItem is ComboBoxItem ci && ci.Tag is byte usage)
            AddClickPair(usage);
    }

    private void AddClickPair(byte usage)
    {
        var m = CurrentMacro;
        if (m == null) return;
        int delay = double.IsNaN(ManualDelayBox.Value) ? 25 : Math.Clamp((int)Math.Round(ManualDelayBox.Value), 5, 5000);
        m.Steps.Add(new MacroStep(usage, true, delay));
        m.Steps.Add(new MacroStep(usage, false, delay));
        App.Settings.Save();
        RenderMacroSteps(m);
        RenderMacroLibrary();
    }

    private void OnClearMacroSteps(object sender, RoutedEventArgs e)
    {
        if (CurrentMacro == null) return;
        CurrentMacro.Steps.Clear();
        App.Settings.Save();
        RenderMacroSteps(CurrentMacro);
        RenderMacroLibrary();
    }

    private void OnSaveMacro(object sender, RoutedEventArgs e)
    {
        if (CurrentMacro == null) return;
        if (!string.IsNullOrWhiteSpace(MacroNameBox.Text))
            CurrentMacro.Name = MacroNameBox.Text.Trim();
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RenderMacroLibrary();
        BindEditorFields();
        ShowStatus($"宏「{CurrentMacro.Name}」已保存并同步触发配置。", InfoBarSeverity.Success);
    }

    private void OnTestPlayMacro(object sender, RoutedEventArgs e)
    {
        if (CurrentMacro == null || CurrentMacro.Steps.Count == 0) return;
        _ = App.MacroRunner?.PlayMacroAsync(CurrentMacro);
    }

    private void OnStopPlayMacro(object sender, RoutedEventArgs e) => App.MacroRunner?.CancelPlayback();

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = !string.IsNullOrWhiteSpace(message);
    }
}
