using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using GearDeck.Core;
using GearDeck.Core.Models;
using GearDeck.Core.Services;
using GearDeck.Desktop.Controls;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Views;

public partial class DeviceDetailView : UserControl
{
    private static readonly int[] CommonDpiPresets = [400, 800, 1200, 1600, 2400, 3200, 6400];
    private static readonly int[] AutoWakeBackoffMs = [3000, 8000, 20000];

    private string _deviceId = string.Empty;
    private DeviceTuningCapabilities? _caps;
    private bool _binding;
    private bool _busy;
    private bool _pickingImage;
    private DeviceTuningUpdate? _queuedUpdate;
    private string _activeTab = "perf";
    private LocalDriverInfo? _localDriver;
    private WebDriverInfo? _webDriver;
    private int? _turboModeCode;

    private List<LodOption> _lodOptions = [];
    private bool _lodUsesDirectMm;
    private double _lodMinMm = 0.7;

    private readonly DispatcherTimer _debounceTimer;
    private DeviceTuningUpdate? _debouncedPending;

    private bool _analogModeInitialized;
    private bool _analogIndependent;
    private bool _analogEditingRight;
    private AnalogButtonConfig _leftAnalog = new(0, 4, true, 3, true, 4);
    private AnalogButtonConfig _rightAnalog = new(1, 4, true, 3, true, 4);
    private Border[] _leftBarSegments = [];
    private Border[] _rightBarSegments = [];
    private TextBlock? _leftBarSummary;
    private TextBlock? _rightBarSummary;
    private DispatcherTimer? _analogPressTimer;
    private double _leftTravel;
    private double _rightTravel;
    private double _leftRtExtreme;
    private double _rightRtExtreme;
    private bool _leftTriggered;
    private bool _rightTriggered;
    private bool _macLeftPointerPressed;
    private bool _macRightPointerPressed;

    private bool _wakeOnMove = true;
    private int _autoWakeAttempts;
    private long _nextAutoWakeTick;
    private long _suppressPostBindUntilTick;

    public DeviceDetailView() : this(string.Empty) { }

    public DeviceDetailView(string deviceId)
    {
        _deviceId = deviceId ?? string.Empty;
        InitializeComponent();

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _debounceTimer.Tick += (_, _) => FlushDebouncedUpdate();

        if (OperatingSystem.IsMacOS())
            ToolTip.SetTip(PinTaskbarBtn, "固定到菜单栏");

        WireEvents();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (App.Devices == null) return;

        App.Devices.DevicesUpdated += OnDevicesUpdated;
        DeviceImageService.ImagesUpdated += OnImagesUpdated;
        ExternalConfiguratorGuard.StateChanged += OnGuardStateChanged;
        RootShellGrid.PointerMoved += OnPagePointerMoved;

        if (!string.IsNullOrWhiteSpace(_deviceId))
        {
            _autoWakeAttempts = 0;
            _nextAutoWakeTick = Environment.TickCount64 + AutoWakeBackoffMs[0];
            ShowOverview();
            BindDeviceLowBatteryPolicy();
            RefreshDeviceSavedPresets();
            BindQuickMacroControls();
            SelectTab(_activeTab);
            _ = LoadCapabilitiesAsync();
        }
        UpdateCoexistenceBanner();
        UpdateAnalogPressTimerState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (App.Devices != null)
            App.Devices.DevicesUpdated -= OnDevicesUpdated;
        DeviceImageService.ImagesUpdated -= OnImagesUpdated;
        ExternalConfiguratorGuard.StateChanged -= OnGuardStateChanged;
        RootShellGrid.PointerMoved -= OnPagePointerMoved;
        _debounceTimer.Stop();
        _debouncedPending = null;
        _queuedUpdate = null;
        _analogPressTimer?.Stop();
        MainWindow.HideToast();
        base.OnDetachedFromVisualTree(e);
    }

    private void WireEvents()
    {
        // XAML 已声明大部分事件处理器；此处只绑定 XAML 未声明的控件，避免重复订阅。
        BackBtn.Click += (_, _) => NavigateBack();
        ReadCapsBtn.Click += OnRetryProbe;
        DeviceLowBatThresholdBox.ValueChanged += (_, _) => OnDeviceLowBatThresholdChanged();
        LocalDriverBtn.Click += (_, _) => LaunchLocalDriver();
        WebDriverBtn.Click += (_, _) => LaunchWebDriver();
        ResumeCoexistenceBtn.Click += (_, _) => ResumeCoexistence();

        TabPerfBtn.Click += (_, _) => SelectTab("perf");
        TabButtonsBtn.Click += (_, _) => SelectTab("buttons");
        TabAdvancedBtn.Click += (_, _) => SelectTab("advanced");

        DpiXBox.ValueChanged += (_, _) => OnDpiXBoxChanged();
        DpiYBox.ValueChanged += (_, _) => OnDpiYBoxChanged();

        WakeOnMoveSwitch.IsCheckedChanged += (_, _) =>
        {
            _wakeOnMove = WakeOnMoveSwitch.IsChecked == true;
            ShowStatus(_wakeOnMove ? "已开启移动唤醒：移动鼠标可自动尝试唤醒休眠设备。" : "已关闭移动唤醒。",
                InfoBarSeverity.Informational);
        };

        AnalogLinkBothBtn.Click += (_, _) => SetAnalogMode(independent: false);
        AnalogIndepBtn.Click += (_, _) => SetAnalogMode(independent: true);
        AnalogSelectLeftBtn.Click += (_, _) => SelectAnalogSide(editRight: false);
        AnalogSelectRightBtn.Click += (_, _) => SelectAnalogSide(editRight: true);
        AnalogRtOffBtn.Click += (_, _) => SetAnalogRtEnabled(false);
        AnalogRtOnBtn.Click += (_, _) => SetAnalogRtEnabled(true);
        AnalogHapOffBtn.Click += (_, _) => SetAnalogHapticsEnabled(false);
        AnalogHapOnBtn.Click += (_, _) => SetAnalogHapticsEnabled(true);
        ApplyAnalogBtn.Click += (_, _) => CommitAnalogSwitchUpdate(immediate: true);

        AnalogPressTestPad.PointerPressed += OnAnalogPressDown;
        AnalogPressTestPad.PointerCaptureLost += (_, _) =>
        {
            _macLeftPointerPressed = false;
            _macRightPointerPressed = false;
        };

        AsymLiftSlider.ValueChanged += OnAsymSliderChanged;
        AsymLandSlider.ValueChanged += OnAsymSliderChanged;
        LowBatSlider.ValueChanged += (_, _) =>
            LowBatValueText.Text = $"低于 {(int)Math.Round(LowBatSlider.Value)}% 提醒";
    }

    // ── 生命周期与状态 ───────────────────────────────────────────────────────────
    private void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> devices) => App.UiPost(ShowOverview);
    private void OnImagesUpdated() => App.UiPost(ShowOverview);
    private void OnGuardStateChanged() => App.UiPost(UpdateCoexistenceBanner);

    private void NavigateBack()
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is MainWindow main)
            {
                main.OpenDevicesPage();
                return;
            }
            if (MainWindow.Instance != null)
            {
                MainWindow.Instance.OpenDevicesPage();
                return;
            }
            var window = TopLevel.GetTopLevel(this) as Window;
            var host = window?.GetVisualDescendants()
                .OfType<ContentControl>()
                .FirstOrDefault(c => string.Equals(c.Name, "PageHost", StringComparison.OrdinalIgnoreCase));
            if (host != null)
                host.Content = new DevicesView();
        }
        catch (Exception ex)
        {
            Diag.Log($"DeviceDetailView.NavigateBack 异常: {ex.Message}");
        }
    }

    private void SelectTab(string tabKey)
    {
        _activeTab = tabKey is "buttons" or "advanced" ? tabKey : "perf";
        PanelPerf.IsVisible = _activeTab == "perf";
        PanelButtons.IsVisible = _activeTab == "buttons";
        PanelAdvanced.IsVisible = _activeTab == "advanced";

        SetActiveButton(TabPerfBtn, _activeTab == "perf");
        SetActiveButton(TabButtonsBtn, _activeTab == "buttons");
        SetActiveButton(TabAdvancedBtn, _activeTab == "advanced");

        UpdateAnalogPressTimerState();
        if (_caps is { IsOnline: false } && FallbackCard.IsVisible)
            MainWindow.HideToast();
    }

    private static void SetActiveButton(Button button, bool active)
    {
        if (active) button.Classes.Add("accent");
        else button.Classes.Remove("accent");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyText.IsVisible = busy;
        ReadCapsBtn.IsEnabled = !busy;
        ProbeAllSlotsBtn.IsEnabled = !busy && _caps is { IsOnline: true, OnboardProfileCount: > 1 };
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(message))
            MainWindow.HideToast();
        else
            MainWindow.ShowToast(message, severity);
    }

    private void UpdateCoexistenceBanner()
    {
        bool active = ExternalConfiguratorGuard.IsBackgroundYieldActive(out var reason);
        CoexistenceBanner.IsVisible = active;
        if (active && !string.IsNullOrWhiteSpace(reason))
            CoexistenceText.Text = reason;
    }

    private void LaunchLocalDriver()
    {
        if (_localDriver == null) return;
        try
        {
            var res = VendorDriverLauncher.LaunchLocal(_localDriver);
            ShowStatus(res.Message, res.Launched ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
            UpdateCoexistenceBanner();
        }
        catch (Exception ex)
        {
            ShowStatus($"打开本地驱动失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void LaunchWebDriver()
    {
        if (_webDriver == null) return;
        try
        {
            var res = VendorDriverLauncher.LaunchWeb(_webDriver);
            ShowStatus(res.Message, res.Launched ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
            UpdateCoexistenceBanner();
        }
        catch (Exception ex)
        {
            ShowStatus($"打开网页驱动失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void ResumeCoexistence()
    {
        try
        {
            ExternalConfiguratorGuard.ResumeNow();
            UpdateCoexistenceBanner();
            _ = App.Devices.RefreshAsync(bypassCoexistenceGuard: true);
            _ = LoadCapabilitiesAsync();
            ShowStatus("已恢复 GearDeck 同步控制并重新读取设备参数。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"恢复同步失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ── Hero 展示区 ──────────────────────────────────────────────────────────────
    private void ShowOverview()
    {
        try
        {
            var d = App.LastDevices.FirstOrDefault(x =>
                string.Equals(x.Id, _deviceId, StringComparison.OrdinalIgnoreCase));
            if (d == null)
            {
                TitleText.Text = "设备未找到";
                SubtitleText.Text = "可能已断开；回到总览刷新后再试";
                BatteryText.Text = "--";
                OverviewBatteryText.Text = "--";
                OverviewPerfText.Text = "--";
                OverviewProtocolText.Text = "--";
                LocalDriverBtn.IsVisible = false;
                WebDriverBtn.IsVisible = false;
                DriverButtonsPanel.IsVisible = false;
                AvatarHost.Children.Clear();
                return;
            }

            TitleText.Text = d.Name;
            SubtitleText.Text = $"设备 > {d.Provider} · {d.Transport} · {d.Kind}";

            var cached = App.Settings.GetLastKnownBattery(d.Id);
            var chip = BatteryHistoryHelper.BuildChipText(d, string.Empty, true, cached);
            string batText = !string.IsNullOrEmpty(chip.TextTop) && !string.IsNullOrEmpty(chip.TextBottom)
                ? $"{chip.TextTop}\n{chip.TextBottom}"
                : chip.Text;

            bool isTws = d.Kind == DeviceKind.Headset && (
                d.LeftEarPercent.HasValue || d.RightEarPercent.HasValue
                || (d.VendorId == 0x054C && d.ProductId == 0x0EC2)
                || (d.VendorId == 0x1038 && d.ProductId is 0x230A or 0x2317)
                || (cached != null && (cached.LeftEarPercent.HasValue || cached.RightEarPercent.HasValue)));

            if (isTws)
            {
                OverviewBatteryLabel.Text = "双耳 / 耳机盒";
                int? effL = d.LeftEarPercent ?? cached?.LeftEarPercent;
                int? effR = d.RightEarPercent ?? cached?.RightEarPercent;
                int? effCase = d.CaseBatteryPercent ?? cached?.CaseBatteryPercent;
                string earLine = $"L {(effL.HasValue ? $"{effL}%" : "--")}{(d.LeftEarCharging ? "⚡" : "")} · R {(effR.HasValue ? $"{effR}%" : "--")}{(d.RightEarCharging ? "⚡" : "")}";
                string caseLine = $"盒 {(effCase.HasValue ? $"{effCase}%" : "--")}{(d.CaseCharging ? "⚡" : "")}";
                OverviewBatteryText.Text = $"{earLine}\n{caseLine}";
                BatteryText.Text = effCase.HasValue ? $"{earLine}\n{caseLine}" : batText;
            }
            else if (d.CaseBatteryPercent.HasValue || cached?.CaseBatteryPercent.HasValue == true ||
                     d.Id.StartsWith("am:", StringComparison.OrdinalIgnoreCase))
            {
                OverviewBatteryLabel.Text = "鼠标 / 备用电池";
                int? effCase = d.CaseBatteryPercent ?? cached?.CaseBatteryPercent;
                string chg = d.CaseCharging ? "⚡" : "";
                OverviewBatteryText.Text = $"鼠标 {batText}\n备电 {(effCase.HasValue ? $"{effCase}%{chg}" : "--")}";
                BatteryText.Text = effCase.HasValue ? $"{batText}\n备 {effCase.Value}%{chg}" : batText;
            }
            else
            {
                OverviewBatteryLabel.Text = "电量 / 充电";
                BatteryText.Text = batText;
                OverviewBatteryText.Text = batText;
            }

            bool isSleepingState = d.IsSleeping || (_caps is { IsOnline: false } && _caps.DeviceId == d.Id);
            StateText.Text = d.State switch
            {
                BatteryState.Charging => "● 充电中 ⚡",
                BatteryState.FullyCharged => "● 已充满",
                _ => isSleepingState ? "○ 接收器已连接 · 休眠" : (d.Connected ? "● 在线" : "○ 离线"),
            };
            NoteText.Text = "";

            var links = VendorDriverCatalog.Resolve(d);
            _localDriver = links.Local;
            _webDriver = links.Web;
            LocalDriverBtn.IsVisible = _localDriver != null;
            if (_localDriver != null) LocalDriverBtnText.Text = _localDriver.DisplayName;
            WebDriverBtn.IsVisible = _webDriver != null;
            if (_webDriver != null) WebDriverBtnText.Text = _webDriver.DisplayName;
            DriverButtonsPanel.IsVisible = _localDriver != null || _webDriver != null;

            AvatarHost.Children.Clear();
            AvatarHost.Children.Add(CreateHeroDeviceVisual(d, 136));

            UpdateOverviewKpis(d, _caps);
            UpdateCoexistenceBanner();
        }
        catch (Exception ex)
        {
            Diag.Log($"ShowOverview 异常: {ex.Message}");
        }
    }

    /// <summary>Avalonia 版 UiBuilders.CreateHeroDeviceVisual：真实产品图优先，缺失时用纯文本符号兜底。</summary>
    private static Control CreateHeroDeviceVisual(PeripheralDevice device, double maxVisualSize = 168)
    {
        var source = DeviceImageService.LoadImageSource(device, maxVisualSize);
        if (source != null)
        {
            return new Image
            {
                Source = source,
                Stretch = Stretch.Uniform,
                MaxHeight = maxVisualSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        return new TextBlock
        {
            Text = FallbackGlyph(device.Kind),
            FontSize = Math.Max(28, maxVisualSize * 0.34),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static string FallbackGlyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Keyboard => "⌨",
        DeviceKind.Headset => "♪",
        DeviceKind.Controller => "◉",
        _ => "▰",
    };

    private void UpdateOverviewKpis(PeripheralDevice d, DeviceTuningCapabilities? caps)
    {
        bool isDpiCapable = caps?.SupportsDpi == true || d.Kind == DeviceKind.Mouse;
        OverviewPerfLabel.Text = isDpiCapable ? "DPI / 回报率" : "连接方式";

        string vidPid = (d.VendorId, d.ProductId) is ({ } v, { } p) ? $"{v:X4}:{p:X4}" : "HID";
        string providerPart = string.IsNullOrWhiteSpace(d.Provider) ? "未知协议" : d.Provider;
        string layered = $"{vidPid} · {d.Kind} · {providerPart}";

        if (caps is { IsOnline: true })
        {
            var perfParts = new List<string>();
            if (caps.SupportsDpi)
                perfParts.Add(caps.CurrentDpiX == caps.CurrentDpiY
                    ? $"{caps.CurrentDpiX} DPI"
                    : $"{caps.CurrentDpiX}/{caps.CurrentDpiY} DPI");
            if (caps.CurrentPollingRate > 0)
                perfParts.Add($"{caps.CurrentPollingRate} Hz");
            OverviewPerfText.Text = perfParts.Count > 0 ? string.Join(" · ", perfParts) : d.Transport;

            OverviewProtocolText.Text = caps.SupportsAnalogSwitchTuning
                ? $"{layered} · {caps.AnalogSwitchTechName}"
                : layered;
        }
        else
        {
            OverviewPerfText.Text = (d.IsSleeping && d.Kind == DeviceKind.Mouse) ? "待唤醒读取" : d.Transport;
            OverviewProtocolText.Text = layered;
        }
    }

    private async void OnPickDeviceImage(object? sender, RoutedEventArgs e)
    {
        if (_pickingImage) return;
        _pickingImage = true;
        PickImageBtn.IsEnabled = false;
        try
        {
            var d = App.LastDevices.FirstOrDefault(x =>
                string.Equals(x.Id, _deviceId, StringComparison.OrdinalIgnoreCase));
            if (d == null) return;
            if (await DeviceImagePickerDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, d))
                ShowOverview();
        }
        catch (Exception ex)
        {
            ShowStatus($"更换设备图失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _pickingImage = false;
            PickImageBtn.IsEnabled = true;
        }
    }

    private void OnPinToTaskbar(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_deviceId)) return;
            var d = App.LastDevices.FirstOrDefault(x =>
                string.Equals(x.Id, _deviceId, StringComparison.OrdinalIgnoreCase));
            if (d == null) return;
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
            cfg.DefaultDeviceId = _deviceId;
            App.Settings.Current.WidgetVisible = true;
            App.Settings.Save();
            App.RefreshWidget();
            ShowStatus(OperatingSystem.IsMacOS() ? "已固定到菜单栏对应分组。" : "已固定到任务栏对应分组。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(OperatingSystem.IsMacOS() ? $"固定到菜单栏失败：{ex.Message}" : $"固定到任务栏失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnRenameDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_deviceId)) return;
        var currentDev = App.LastDevices.FirstOrDefault(x =>
            string.Equals(x.Id, _deviceId, StringComparison.OrdinalIgnoreCase));
        if (currentDev == null) return;

        var box = new TextBox
        {
            Text = currentDev.Name,
            Watermark = "输入该接收器/设备对应的真实型号，如 LAMZU ORCUS",
        };
        var dialog = new ContentDialog
        {
            Title = "重命名 / 绑定设备真实型号",
            Content = box,
            PrimaryButtonText = "保存",
            SecondaryButtonText = "恢复默认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            string nextName = (box.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(nextName)) return;
            App.Settings.Current.DeviceCustomNames[_deviceId] = nextName;
            ReceiverModelRegistry.SetCustomName(_deviceId, nextName);
            App.Settings.Save();
            currentDev.Name = nextName;
            ShowOverview();
            App.RefreshWidget();
            _ = DeviceImageService.EnsureAutoCloudImagesAsync([currentDev], forceRecheck: true);
            ShowStatus($"已将设备型号保存为「{nextName}」。", InfoBarSeverity.Success);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            App.Settings.Current.DeviceCustomNames.Remove(_deviceId);
            ReceiverModelRegistry.SetCustomName(_deviceId, null);
            App.Settings.Save();
            App.RequestRefresh();
            ShowStatus("已清除自定义名称，正在重新读取硬件名称。", InfoBarSeverity.Informational);
        }
    }

    // ── 单设备低电量策略 ─────────────────────────────────────────────────────────
    private void BindDeviceLowBatteryPolicy()
    {
        if (string.IsNullOrWhiteSpace(_deviceId)) return;
        _binding = true;
        try
        {
            var policy = App.Settings.GetLowBatteryPolicy(_deviceId);
            int eff = App.Settings.GetEffectiveLowBatteryThreshold(_deviceId);
            DeviceLowBatEnableChk.IsChecked = policy.Enabled;
            DeviceLowBatNotifyChk.IsChecked = policy.WindowsNotification;
            SafeSetNumberBox(DeviceLowBatThresholdBox, 5, 80, 1, eff);
        }
        finally
        {
            _binding = false;
        }
    }

    private void SaveDeviceLowBatteryPolicy(int? customThreshold)
    {
        if (string.IsNullOrWhiteSpace(_deviceId)) return;
        App.Settings.Current.DeviceLowBatteryPolicies[_deviceId] = new DeviceLowBatteryPolicy
        {
            Enabled = DeviceLowBatEnableChk.IsChecked == true,
            CustomThresholdPercent = customThreshold,
            WindowsNotification = DeviceLowBatNotifyChk.IsChecked == true,
        };
        App.Settings.Save();
        App.RefreshWidget();
    }

    private void OnDeviceLowBatCheckChanged(object? sender, RoutedEventArgs e)
    {
        if (_binding || string.IsNullOrWhiteSpace(_deviceId)) return;
        SaveDeviceLowBatteryPolicy(App.Settings.GetLowBatteryPolicy(_deviceId).CustomThresholdPercent);
    }

    private void OnDeviceLowBatThresholdChanged()
    {
        if (_binding || string.IsNullOrWhiteSpace(_deviceId)) return;
        if (Environment.TickCount64 < _suppressPostBindUntilTick) return;
        double? raw = DeviceLowBatThresholdBox.Value;
        if (raw == null || double.IsNaN(raw.Value)) return;
        int clamped = Math.Clamp((int)Math.Round(raw.Value), 5, 80);
        if (clamped == App.Settings.GetEffectiveLowBatteryThreshold(_deviceId)) return;
        SaveDeviceLowBatteryPolicy(clamped);
    }

    // ── 能力读取与绑定 ───────────────────────────────────────────────────────────
    private async Task LoadCapabilitiesAsync()
    {
        if (string.IsNullOrWhiteSpace(_deviceId) || _busy) return;
        SetBusy(true);
        try
        {
            var caps = await App.Devices.QueryCapabilitiesAsync(_deviceId);
            if (!IsLoaded) return;
            BindCapabilities(caps);
        }
        catch (Exception ex)
        {
            Diag.Log($"LoadCapabilitiesAsync 异常: {ex.Message}");
            if (IsLoaded)
                ShowStatus($"读取硬件参数失败: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool IsLeobog() => !string.IsNullOrWhiteSpace(_deviceId)
        && _deviceId.StartsWith("leobog:", StringComparison.OrdinalIgnoreCase);

    private bool IsLeobogWireless() => IsLeobog()
        && !_deviceId.Contains("258A-010C", StringComparison.OrdinalIgnoreCase);

    private static int SafeSleepSeconds(int seconds) => seconds <= 0 ? 30 : seconds;

    private static int? FindTurboModeCode(DeviceTuningCapabilities caps)
        => caps.SupportedPerformanceModes.FirstOrDefault(o =>
               o.Label.Contains("竞技", StringComparison.Ordinal) ||
               o.Label.Contains("Turbo", StringComparison.OrdinalIgnoreCase) ||
               o.Label.Contains("Ultra", StringComparison.OrdinalIgnoreCase) ||
               o.Label.Contains("HP", StringComparison.OrdinalIgnoreCase))?.Code;

    private void BindCapabilities(DeviceTuningCapabilities caps)
    {
        _caps = caps;
        _debounceTimer.Stop();
        _debouncedPending = null;
        _binding = true;
        try
        {
            var currentDev = App.LastDevices.FirstOrDefault(x =>
                string.Equals(x.Id, _deviceId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(_deviceId) && caps.IsOnline)
                App.Settings.RecordCapabilitiesManifest(_deviceId, caps);

            if (currentDev != null)
            {
                UpdateOverviewKpis(currentDev, caps);
                if (currentDev.State == BatteryState.Normal && currentDev.Connected)
                    StateText.Text = caps.IsOnline ? "● 已连接" : "○ 无线休眠中";
            }

            var cachedSlots = !string.IsNullOrWhiteSpace(_deviceId)
                ? App.Settings.GetDeviceOnboardSlots(currentDev, _deviceId)
                : [];
            bool hasOnboardProfiles = caps.OnboardProfileCount > 0 || cachedSlots.Count > 0;
            bool hasAnyTunable = caps.SupportsDpi || caps.SupportedPollingRates.Count > 0
                || caps.SupportedLodOptions.Count > 0 || caps.SupportsAnalogSwitchTuning;
            bool isConfigurableKind = currentDev?.Kind is DeviceKind.Mouse or DeviceKind.Controller;
            bool leobogWireless = IsLeobogWireless();

            TopProfileBarCard.IsVisible = isConfigurableKind || caps.SupportsHostModeSwitch
                || (caps.IsOnline && (hasOnboardProfiles || hasAnyTunable));
            OnboardProfileButtonsPanel.IsVisible = hasOnboardProfiles;
            ProbeAllSlotsBtn.IsVisible = caps.IsOnline && caps.OnboardProfileCount > 1;
            ProbeAllSlotsBtn.IsEnabled = !_busy && caps.IsOnline && caps.OnboardProfileCount > 1;
            SaveDevicePresetBtn.IsEnabled = caps.IsOnline && (hasAnyTunable || caps.OnboardProfileCount > 0);

            if (hasOnboardProfiles)
                BuildOnboardProfileButtons(caps, cachedSlots);
            RefreshDeviceSavedPresets();

            HostModeCard.IsVisible = caps.SupportsHostModeSwitch;
            if (caps.SupportsHostModeSwitch)
                HostModeSwitch.IsChecked = !caps.IsHostMode;

            // ── DPI ──
            DpiCard.IsVisible = caps.SupportsDpi;
            if (caps.SupportsDpi)
            {
                int minDpi = Math.Max(10, caps.MinDpi);
                int maxDpi = Math.Max(minDpi + 50, caps.MaxDpi);
                int step = Math.Max(1, caps.DpiStep);

                SyncXySwitch.IsVisible = caps.SupportsIndependentXY;
                bool independent = caps.SupportsIndependentXY && caps.CurrentDpiX != caps.CurrentDpiY;
                SyncXySwitch.IsChecked = !independent;
                DpiYSection.IsVisible = independent;
                DpiXLabel.Text = independent ? "X 轴:" : "DPI:";

                ConfigureDpiRange(DpiXSlider, DpiXBox, minDpi, maxDpi, step, caps.CurrentDpiX);
                ConfigureDpiRange(DpiYSlider, DpiYBox, minDpi, maxDpi, step, caps.CurrentDpiY);

                DpiRangeHint.Text = $"硬件范围 {minDpi} - {maxDpi}（步进 {step}）";
                UpdateDpiSummaryText();
                BuildDpiPresets(minDpi, maxDpi);
                BuildDpiStages();
            }

            // ── 回报率 ──
            RateCard.IsVisible = caps.SupportedPollingRates.Count > 0;
            if (caps.SupportedPollingRates.Count > 0)
                BuildPollingRateGrid(caps.SupportedPollingRates, caps.CurrentPollingRate);

            // ── 性能模式与传感器增强 ──
            bool hasPerfModes = caps.SupportedPerformanceModes.Count > 0;
            bool hasSwitches = caps.SupportsMotionSync || caps.SupportsAngleSnapping
                || caps.SupportsRippleControl || caps.SupportsLongRangeMode;
            _turboModeCode = FindTurboModeCode(caps);
            ModeCard.IsVisible = hasPerfModes || hasSwitches;
            PerfModeSection.IsVisible = hasPerfModes;
            if (hasPerfModes)
                BuildPerformanceModeButtons(caps);

            MotionSyncSwitch.IsVisible = caps.SupportsMotionSync;
            if (caps.SupportsMotionSync) MotionSyncSwitch.IsChecked = caps.MotionSync;
            AngleSnapSwitch.IsVisible = caps.SupportsAngleSnapping;
            if (caps.SupportsAngleSnapping) AngleSnapSwitch.IsChecked = caps.AngleSnapping;
            RippleSwitch.IsVisible = caps.SupportsRippleControl;
            if (caps.SupportsRippleControl) RippleSwitch.IsChecked = caps.RippleControl;
            LongRangeSwitch.IsVisible = caps.SupportsLongRangeMode;
            if (caps.SupportsLongRangeMode)
                LongRangeSwitch.IsChecked = caps.LongRangeMode;
            WakeOnMoveSwitch.IsVisible = false;

            // ── 高级：LOD / 防抖 / 休眠（仅展示设备实际支持的能力） ──
            bool hasLod = caps.SupportedLodOptions.Count > 0;
            bool hasAsym = caps.SupportsAsymmetricLod;
            bool hasDebounce = caps.SupportsDebounce;
            bool hasAngle = caps.SupportsAngleTuning;
            bool hasDiscreteSleep = caps.SupportsSleepTimeout && caps.SupportedSleepSeconds.Count > 0;
            bool hasSliderSleep = caps.SupportsSleepTimeout && caps.SupportedSleepSeconds.Count == 0;
            bool hasLowBat = caps.SupportsLowBatteryWarning;
            AdvancedCard.IsVisible = hasLod || hasAsym || hasDebounce || hasAngle
                || hasDiscreteSleep || hasSliderSleep || hasLowBat;

            LodSection.IsVisible = hasLod;
            if (hasLod)
                BuildLodButtons(caps.SupportedLodOptions, caps.CurrentLod);

            AsymLodSection.IsVisible = hasAsym;
            if (hasAsym)
            {
                AsymLodSwitch.IsChecked = caps.AsymmetricLodEnabled;
                int lift = Math.Clamp(caps.AsymmetricLiftOff, 2, 26);
                int land = Math.Clamp(caps.AsymmetricLanding, 1, lift - 1);
                SafeSetSlider(AsymLiftSlider, 0.2, 2.6, 0.1, lift * 0.1);
                SafeSetSlider(AsymLandSlider, 0.1, 2.5, 0.1, land * 0.1);
                AsymLiftText.Text = $"{lift * 0.1:0.0} mm";
                AsymLandText.Text = $"{land * 0.1:0.0} mm";
            }

            DebounceSection.IsVisible = hasDebounce;
            if (hasDebounce)
            {
                int maxDb = Math.Max(1, caps.MaxDebounceMs);
                int curDb = Math.Clamp(caps.CurrentDebounceMs, 0, maxDb);
                SafeSetSlider(DebounceSlider, 0, maxDb, 1, curDb);
                DebounceValueText.Text = $"{curDb} ms";
            }

            AngleTuningSection.IsVisible = hasAngle;
            if (hasAngle)
            {
                int curAng = Math.Clamp(caps.AngleTuningDeg, -30, 30);
                SafeSetSlider(AngleSlider, -30, 30, 1, curAng);
                AngleValueText.Text = $"{curAng}°";
            }

            DiscreteSleepSection.IsVisible = hasDiscreteSleep;
            if (hasDiscreteSleep)
                BuildDiscreteSleepButtons(caps.SupportedSleepSeconds, caps.CurrentSleepSeconds);

            SleepSection.IsVisible = hasSliderSleep;
            if (hasSliderSleep)
            {
                int minMin = Math.Max(1, caps.MinSleepSeconds / 60);
                int maxMin = Math.Max(minMin + 1, caps.MaxSleepSeconds / 60);
                int curMin = Math.Clamp((int)Math.Round(caps.CurrentSleepSeconds / 60.0), minMin, maxMin);
                SafeSetSlider(SleepSlider, minMin, maxMin, 1, curMin);
                SleepValueText.Text = $"{curMin} 分钟后休眠";
            }

            LowBatWarnSection.IsVisible = hasLowBat;
            if (hasLowBat)
            {
                int curWarn = Math.Clamp(caps.CurrentLowBatteryWarningPercent, 5, 50);
                SafeSetSlider(LowBatSlider, 5, 50, 1, curWarn);
                LowBatValueText.Text = $"低于 {curWarn}% 提醒";
            }

            // 灯光调节：当前设备未提供鼠标 HID 写灯能力，默认屏蔽不展示
            LightingSection.IsVisible = false;

            LeobogReadOnlyText.IsVisible = leobogWireless;
            if (leobogWireless)
            {
                foreach (var btn in DiscreteSleepPanel.Children.OfType<Button>())
                    btn.IsEnabled = false;
                SleepSlider.IsEnabled = false;
                LowBatSlider.IsEnabled = false;
            }
            ApplyPowerBtn.IsVisible = (hasSliderSleep || hasLowBat) && !leobogWireless;

            BindHeadsetAudioAndLighting(caps);
            BindAnalogSwitches(caps);

            bool showMacroCard = caps.ButtonBindings.Count > 0
                || caps.SupportsOnboardMacroUpload
                || currentDev?.Kind == DeviceKind.Mouse;
            MacroAndButtonsCard.IsVisible = showMacroCard;
            if (showMacroCard)
                BindButtonMappingsAndQuickMacros(caps);

            bool anyControl = caps.SupportsHostModeSwitch || caps.SupportsDpi
                || caps.SupportedPollingRates.Count > 0 || hasLod || hasAsym || hasDebounce || hasAngle
                || caps.SupportsSleepTimeout || hasLowBat || hasPerfModes || hasSwitches
                || caps.ButtonBindings.Count > 0 || caps.SupportsOnboardMacroUpload || caps.SupportsAnalogSwitchTuning
                || caps.SupportsSidetone || caps.SupportsAncMode || caps.SupportsVoicePrompt
                || caps.SupportsEqualizer || caps.SupportsLighting || !string.IsNullOrWhiteSpace(caps.ChatMixSummary);
            FallbackCard.IsVisible = !anyControl;
            if (!anyControl)
            {
                FallbackTitle.Text = caps.IsOnline ? "硬件参数说明" : "设备休眠或未唤醒";
                FallbackMessage.Text = caps.StatusMessage ?? "当前设备暂无可调节的公开 HID 参数。";
            }

            bool hasPerfTab = DpiCard.IsVisible || RateCard.IsVisible || ModeCard.IsVisible
                || HeadsetAudioCard.IsVisible || DeviceLightingCard.IsVisible || FallbackCard.IsVisible;
            bool hasButtonsTab = AnalogSwitchCard.IsVisible || MacroAndButtonsCard.IsVisible;
            bool hasAdvTab = AdvancedCard.IsVisible;
            TabPerfBtn.IsVisible = hasPerfTab;
            TabButtonsBtn.IsVisible = hasButtonsTab;
            TabAdvancedBtn.IsVisible = hasAdvTab;
            TabNavBar.IsVisible = ((hasPerfTab ? 1 : 0) + (hasButtonsTab ? 1 : 0) + (hasAdvTab ? 1 : 0)) > 1;
            if ((_activeTab == "buttons" && !hasButtonsTab) || (_activeTab == "advanced" && !hasAdvTab))
                SelectTab("perf");

            if (caps.IsOnline)
                _autoWakeAttempts = 0;

            MainWindow.HideToast();
        }
        catch (Exception ex)
        {
            Diag.Log($"BindCapabilities 异常: {ex.Message}");
            ShowStatus($"参数界面渲染异常: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _binding = false;
            _suppressPostBindUntilTick = Environment.TickCount64 + 350;
        }
    }

    // ── DPI ──────────────────────────────────────────────────────────────────────
    private static void SafeSetSlider(Slider s, double min, double max, double step, double val)
    {
        if (min > max) (min, max) = (max, min);
        double clamped = Math.Clamp(val, min, max);
        double safeStep = Math.Max(0.05, step);
        if (s.Maximum < max) s.Maximum = max;
        if (Math.Abs(s.Minimum - min) > 0.0001) s.Minimum = min;
        if (Math.Abs(s.Maximum - max) > 0.0001) s.Maximum = max;
        if (Math.Abs(s.SmallChange - safeStep) > 0.0001) s.SmallChange = safeStep;
        if (double.IsNaN(s.Value) || Math.Abs(s.Value - clamped) > 0.0001) s.Value = clamped;
    }

    private static void SafeSetNumberBox(NumberBox b, double min, double max, double step, double val)
    {
        if (min > max) (min, max) = (max, min);
        double clamped = Math.Clamp(val, min, max);
        if (Math.Abs(b.Maximum - max) > 0.0001) b.Maximum = max;
        if (Math.Abs(b.Minimum - min) > 0.0001) b.Minimum = min;
        if (double.IsNaN(b.Value) || Math.Abs(b.Value - clamped) > 0.0001) b.Value = clamped;
        _ = step;
    }

    private static void ConfigureDpiRange(Slider slider, NumberBox box, int min, int max, int step, int value)
    {
        SafeSetSlider(slider, min, max, step, value);
        SafeSetNumberBox(box, min, max, step, value);
    }

    private void UpdateDpiSummaryText()
    {
        int x = DpiXBox.Value is { } bx && double.IsFinite(bx) ? (int)Math.Round(bx) : (int)Math.Round(DpiXSlider.Value);
        int y = SyncXySwitch.IsChecked == true
            ? x
            : (DpiYBox.Value is { } by && double.IsFinite(by) ? (int)Math.Round(by) : (int)Math.Round(DpiYSlider.Value));
        DpiSummaryText.Text = x == y ? $"当前生效：{x} DPI" : $"当前生效：X {x} / Y {y} DPI";
    }

    private void BuildDpiPresets(int min, int max)
    {
        DpiPresetsPanel.Children.Clear();
        int curX = DpiXBox.Value is { } bx && double.IsFinite(bx) ? (int)Math.Round(bx) : 0;
        int curY = SyncXySwitch.IsChecked == true ? curX
            : (DpiYBox.Value is { } by && double.IsFinite(by) ? (int)Math.Round(by) : curX);
        foreach (int preset in CommonDpiPresets)
        {
            if (preset < min || preset > max) continue;
            var btn = new Button { Content = preset.ToString(), Classes = { "pill" } };
            if (curX == preset && curY == preset) SetActiveButton(btn, true);
            btn.Click += (_, _) => _ = ApplyDpiPresetAsync(preset);
            DpiPresetsPanel.Children.Add(btn);
        }
    }

    private void BuildDpiStages()
    {
        if (_caps == null || _caps.DpiStages.Count == 0)
        {
            DpiStagesSection.IsVisible = false;
            return;
        }
        DpiStagesSection.IsVisible = true;
        DpiStagesPanel.Children.Clear();

        for (int i = 0; i < _caps.DpiStages.Count; i++)
        {
            int stageIndex = i;
            var (sx, sy) = _caps.DpiStages[i];
            string text = sx == sy ? $"档位 {i + 1} · {sx}" : $"档位 {i + 1} · {sx}/{sy}";
            var btn = new Button { Content = text, Classes = { "pill" } };
            if (i == _caps.ActiveDpiStage) SetActiveButton(btn, true);
            btn.Click += (_, _) => _ = ActivateDpiStageAsync(stageIndex);
            DpiStagesPanel.Children.Add(btn);
        }

        int maxStages = _caps.DeviceId.StartsWith("razer:", StringComparison.OrdinalIgnoreCase)
            ? 5
            : (_caps.DeviceId.StartsWith("am:", StringComparison.OrdinalIgnoreCase) ? 8 : 6);
        AddStageBtn.IsEnabled = _caps.SupportsDpiStagesWrite && _caps.DpiStages.Count < maxStages;
        RemoveStageBtn.IsEnabled = _caps.SupportsDpiStagesWrite && _caps.DpiStages.Count > 1;
    }

    private void BuildPollingRateGrid(List<int> rates, int currentRate)
    {
        RatePanel.Children.Clear();
        foreach (int hz in rates)
        {
            int rate = hz;
            var btn = new Button { Content = $"{rate} Hz", MinWidth = 98, Classes = { "pill" } };
            if (rate == currentRate) SetActiveButton(btn, true);
            btn.Click += (_, _) => _ = ApplyUpdateAsync(new DeviceTuningUpdate { PollingRateHz = rate });
            RatePanel.Children.Add(btn);
        }
    }

    private void BuildPerformanceModeButtons(DeviceTuningCapabilities caps)
    {
        PerfModePanel.Children.Clear();
        PerfModeDescText.Text = "";
        foreach (var opt in caps.SupportedPerformanceModes)
        {
            int code = opt.Code;
            var btn = new Button { Content = opt.Label, Classes = { "pill" } };
            if (code == caps.CurrentPerformanceMode)
            {
                SetActiveButton(btn, true);
                PerfModeDescText.Text = opt.Description ?? "";
            }
            btn.Click += (_, _) => _ = ApplyUpdateAsync(new DeviceTuningUpdate { PerformanceMode = code });
            PerfModePanel.Children.Add(btn);
        }
    }

    private void OnSyncXyToggled(object? sender, RoutedEventArgs e)
    {
        if (_binding) return;
        bool sync = SyncXySwitch.IsChecked == true;
        DpiYSection.IsVisible = !sync;
        DpiXLabel.Text = sync ? "DPI:" : "X 轴:";
        if (sync)
        {
            _binding = true;
            DpiYSlider.Value = DpiXSlider.Value;
            DpiYBox.Value = DpiXBox.Value;
            _binding = false;
        }
        UpdateDpiSummaryText();
    }

    private void OnDpiXSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        _binding = true;
        DpiXBox.Value = e.NewValue;
        if (SyncXySwitch.IsChecked == true)
        {
            DpiYSlider.Value = e.NewValue;
            DpiYBox.Value = e.NewValue;
        }
        _binding = false;
        UpdateDpiSummaryText();
        if (BuildDpiUpdateFromUi() is { } u) ScheduleDebouncedUpdate(u);
    }

    private void OnDpiXBoxChanged()
    {
        double raw = DpiXBox.Value;
        if (_binding || _caps == null || double.IsNaN(raw) || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        int minDpi = Math.Max(10, _caps.MinDpi);
        int maxDpi = Math.Max(minDpi + 50, _caps.MaxDpi);
        double clamped = Math.Clamp(raw, minDpi, maxDpi);
        _binding = true;
        DpiXSlider.Value = clamped;
        if (SyncXySwitch.IsChecked == true)
        {
            DpiYSlider.Value = clamped;
            DpiYBox.Value = clamped;
        }
        _binding = false;
        UpdateDpiSummaryText();
        if (raw >= minDpi && BuildDpiUpdateFromUi() is { } u) ScheduleDebouncedUpdate(u, 320);
    }

    private void OnDpiYSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        _binding = true;
        DpiYBox.Value = e.NewValue;
        _binding = false;
        UpdateDpiSummaryText();
        if (BuildDpiUpdateFromUi() is { } u) ScheduleDebouncedUpdate(u);
    }

    private void OnDpiYBoxChanged()
    {
        double raw = DpiYBox.Value;
        if (_binding || _caps == null || double.IsNaN(raw) || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        int minDpi = Math.Max(10, _caps.MinDpi);
        int maxDpi = Math.Max(minDpi + 50, _caps.MaxDpi);
        _binding = true;
        DpiYSlider.Value = Math.Clamp(raw, minDpi, maxDpi);
        _binding = false;
        UpdateDpiSummaryText();
        if (raw >= minDpi && BuildDpiUpdateFromUi() is { } u) ScheduleDebouncedUpdate(u, 320);
    }

    private void OnDpiSliderReleased(object? sender, PointerReleasedEventArgs e) => FlushDebouncedUpdate();

    private void OnAddStage(object? sender, RoutedEventArgs e)
    {
        if (_caps == null || _caps.DpiStages.Count == 0) return;
        int maxStages = _caps.DeviceId.StartsWith("razer:", StringComparison.OrdinalIgnoreCase)
            ? 5
            : (_caps.DeviceId.StartsWith("am:", StringComparison.OrdinalIgnoreCase) ? 8 : 6);
        if (_caps.DpiStages.Count >= maxStages) return;
        var stages = _caps.DpiStages.ToList();
        var last = stages[^1];
        int nextDpi = Math.Clamp(last.X + 400, _caps.MinDpi, _caps.MaxDpi);
        stages.Add((nextDpi, nextDpi));
        _ = ApplyUpdateAsync(new DeviceTuningUpdate
        {
            DpiStages = stages,
            ActiveDpiStage = stages.Count - 1,
            Dpi = (nextDpi, nextDpi),
        });
    }

    private void OnRemoveStage(object? sender, RoutedEventArgs e)
    {
        if (_caps == null || _caps.DpiStages.Count <= 1) return;
        var stages = _caps.DpiStages.ToList();
        stages.RemoveAt(stages.Count - 1);
        int active = Math.Clamp(_caps.ActiveDpiStage, 0, stages.Count - 1);
        _ = ApplyUpdateAsync(new DeviceTuningUpdate
        {
            DpiStages = stages,
            ActiveDpiStage = active,
            Dpi = stages[active],
        });
    }

    private Task ApplyDpiPresetAsync(int dpi)
    {
        _debounceTimer.Stop();
        _debouncedPending = null;
        _binding = true;
        DpiXSlider.Value = dpi;
        DpiXBox.Value = dpi;
        if (SyncXySwitch.IsChecked == true || _caps?.SupportsIndependentXY != true)
        {
            DpiYSlider.Value = dpi;
            DpiYBox.Value = dpi;
        }
        _binding = false;
        return ApplyCurrentDpiFromUiAsync();
    }

    private Task ActivateDpiStageAsync(int stageIndex)
    {
        if (_caps == null || stageIndex < 0 || stageIndex >= _caps.DpiStages.Count)
            return Task.CompletedTask;
        var (x, y) = _caps.DpiStages[stageIndex];
        return ApplyUpdateAsync(new DeviceTuningUpdate
        {
            DpiStages = _caps.DpiStages.ToList(),
            ActiveDpiStage = stageIndex,
            Dpi = (x, y),
        });
    }

    private DeviceTuningUpdate? BuildDpiUpdateFromUi()
    {
        if (_caps == null) return null;
        int minDpi = Math.Max(10, _caps.MinDpi);
        int maxDpi = Math.Max(minDpi + 50, _caps.MaxDpi);
        int step = Math.Max(1, _caps.DpiStep);

        double rawX = DpiXBox.Value is { } bx && double.IsFinite(bx) ? bx : DpiXSlider.Value;
        int x = SnapDpi((int)Math.Round(rawX), minDpi, maxDpi, step);
        int y = (_caps.SupportsIndependentXY && SyncXySwitch.IsChecked != true)
            ? SnapDpi((int)Math.Round(DpiYBox.Value is { } by && double.IsFinite(by) ? by : DpiYSlider.Value),
                minDpi, maxDpi, step)
            : x;

        if (x == _caps.CurrentDpiX && y == _caps.CurrentDpiY)
            return null;

        var update = new DeviceTuningUpdate { Dpi = (x, y) };
        if (_caps is { SupportsDpiStagesWrite: true, DpiStages.Count: > 0 })
        {
            var stages = _caps.DpiStages.ToList();
            int idx = Math.Clamp(_caps.ActiveDpiStage, 0, stages.Count - 1);
            stages[idx] = (x, y);
            update.DpiStages = stages;
            update.ActiveDpiStage = idx;
        }
        return update;
    }

    private static int SnapDpi(int val, int min, int max, int step)
    {
        int clamped = Math.Clamp(val, min, max);
        return Math.Clamp((int)Math.Round(clamped / (double)step) * step, min, max);
    }

    private Task ApplyCurrentDpiFromUiAsync()
    {
        var update = BuildDpiUpdateFromUi();
        return update != null ? ApplyUpdateAsync(update) : Task.CompletedTask;
    }

    // ── 开关 ─────────────────────────────────────────────────────────────────────
    private void OnHostModeToggled(object? sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsHostModeSwitch || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        bool targetHostMode = HostModeSwitch.IsChecked != true;
        if (targetHostMode == _caps.IsHostMode) return;
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { SetHostMode = targetHostMode });
    }

    private void BindHeadsetAudioAndLighting(DeviceTuningCapabilities caps)
    {
        bool hasChatMix = !string.IsNullOrWhiteSpace(caps.ChatMixSummary);
        bool hasAudio = caps.SupportsSidetone || caps.SupportsAncMode || caps.SupportsVoicePrompt
            || caps.SupportsEqualizer || hasChatMix;
        HeadsetAudioCard.IsVisible = hasAudio;
        if (hasAudio)
        {
            ChatMixText.IsVisible = hasChatMix;
            ChatMixText.Text = caps.ChatMixSummary ?? "";

            SidetoneSection.IsVisible = caps.SupportsSidetone;
            if (caps.SupportsSidetone)
            {
                SidetoneSwitch.IsChecked = caps.SidetoneEnabled;
                int maxSt = Math.Max(1, caps.MaxSidetoneLevel);
                int curSt = Math.Clamp(caps.CurrentSidetoneLevel, 0, maxSt);
                SafeSetSlider(SidetoneSlider, 0, maxSt, 1, curSt);
                SidetoneValueText.Text = $"{curSt} / {maxSt}";
            }

            AncSection.IsVisible = caps.SupportsAncMode && caps.SupportedAncModes.Count > 0;
            if (AncSection.IsVisible)
            {
                AncModesPanel.Children.Clear();
                foreach (var opt in caps.SupportedAncModes)
                {
                    int code = opt.Code;
                    var btn = new Button { Content = opt.Label, Classes = { "pill" } };
                    if (code == caps.CurrentAncMode) SetActiveButton(btn, true);
                    btn.Click += (_, _) => _ = ApplyUpdateAsync(new DeviceTuningUpdate { AncMode = code });
                    AncModesPanel.Children.Add(btn);
                }
            }

            EqSection.IsVisible = caps.SupportsEqualizer;
            if (caps.SupportsEqualizer)
            {
                EqEnableSwitch.IsChecked = caps.EqualizerEnabled;
                EqPresetsPanel.Children.Clear();
                foreach (var opt in caps.SupportedEqPresets)
                {
                    int code = opt.Code;
                    var btn = new Button { Content = opt.Label, Classes = { "pill" } };
                    if (caps.EqualizerEnabled && code == caps.CurrentEqPreset) SetActiveButton(btn, true);
                    btn.Click += (_, _) => _ = ApplyUpdateAsync(new DeviceTuningUpdate { Equalizer = (true, code) });
                    EqPresetsPanel.Children.Add(btn);
                }
                EqPresetsPanel.IsVisible = caps.SupportedEqPresets.Count > 0;
            }

            VoicePromptSwitch.IsVisible = caps.SupportsVoicePrompt;
            if (caps.SupportsVoicePrompt)
                VoicePromptSwitch.IsChecked = caps.VoicePromptEnabled;
        }

        DeviceLightingCard.IsVisible = caps.SupportsLighting;
        if (caps.SupportsLighting)
        {
            LightingModesPanel.Children.Clear();
            LightingModesPanel.IsVisible = caps.SupportedLightingModes.Count > 0;
            foreach (var opt in caps.SupportedLightingModes)
            {
                int mode = opt.Code;
                var btn = new Button { Content = opt.Label, Classes = { "pill" } };
                if (mode == caps.CurrentLightingMode) SetActiveButton(btn, true);
                btn.Click += (_, _) => _ = ApplyUpdateAsync(new DeviceTuningUpdate
                {
                    Lighting = (mode, (int)Math.Round(LightingBrightnessSlider.Value)),
                });
                LightingModesPanel.Children.Add(btn);
            }

            int maxB = Math.Max(1, caps.MaxLightingBrightness);
            int curB = Math.Clamp(caps.CurrentLightingBrightness, 0, maxB);
            SafeSetSlider(LightingBrightnessSlider, 0, maxB, 1, curB);
            LightingBrightnessText.Text = maxB == 100 ? $"{curB}%" : $"{curB} / {maxB}";
        }
    }

    private void OnSidetoneToggled(object? sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsSidetone || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        bool on = SidetoneSwitch.IsChecked == true;
        int lvl = (int)Math.Round(SidetoneSlider.Value);
        if (on && lvl == 0) lvl = Math.Max(1, _caps.MaxSidetoneLevel / 2);
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { Sidetone = (on, lvl) });
    }

    private void OnSidetoneSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_caps == null) return;
        int maxSt = Math.Max(1, _caps.MaxSidetoneLevel);
        SidetoneValueText.Text = $"{(int)Math.Round(e.NewValue)} / {maxSt}";
    }

    private void OnSidetoneSliderReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsSidetone) return;
        int lvl = (int)Math.Round(SidetoneSlider.Value);
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { Sidetone = (lvl > 0 && SidetoneSwitch.IsChecked != false, lvl) });
    }

    private void OnAncOrEqChanged() { }

    private void OnEqEnableToggled(object? sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsEqualizer || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        bool on = EqEnableSwitch.IsChecked == true;
        if (on == _caps.EqualizerEnabled) return;
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { Equalizer = (on, null) });
    }

    private void OnVoicePromptToggled(object? sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsVoicePrompt || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        bool on = VoicePromptSwitch.IsChecked == true;
        if (on == _caps.VoicePromptEnabled) return;
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { VoicePromptEnabled = on });
    }

    private void OnLightingBrightnessChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int maxB = _caps != null ? Math.Max(1, _caps.MaxLightingBrightness) : 100;
        int v = (int)Math.Round(e.NewValue);
        LightingBrightnessText.Text = maxB == 100 ? $"{v}%" : $"{v} / {maxB}";
    }

    private void OnLightingBrightnessReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsLighting) return;
        int v = (int)Math.Round(LightingBrightnessSlider.Value);
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { Lighting = (_caps.CurrentLightingMode, v) });
    }

    private void OnMotionSyncToggled(object? sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsMotionSync || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        bool target = MotionSyncSwitch.IsChecked == true;
        if (target == _caps.MotionSync) return;
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { MotionSync = target });
    }

    private void OnAngleSnapToggled(object? sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsAngleSnapping || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        bool target = AngleSnapSwitch.IsChecked == true;
        if (target == _caps.AngleSnapping) return;
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { AngleSnapping = target });
    }

    private void OnRippleToggled(object? sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsRippleControl || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        bool target = RippleSwitch.IsChecked == true;
        if (target == _caps.RippleControl) return;
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { RippleControl = target });
    }

    private void OnLongRangeToggled(object? sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsLongRangeMode || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        bool on = LongRangeSwitch.IsChecked == true;
        if (on == _caps.LongRangeMode) return;
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { LongRangeMode = on });
    }

    // ── 高级：LOD / 防抖 / 角度 / 休眠 / 灯光 ──────────────────────────────────────
    private void BuildLodButtons(List<LodOption> options, int currentLod)
    {
        _lodOptions = options.ToList();
        LodButtonsPanel.Children.Clear();
        foreach (var opt in options)
        {
            int code = opt.Code;
            var btn = new Button { Content = opt.Label, MinWidth = 98, Classes = { "pill" } };
            if (code == currentLod) SetActiveButton(btn, true);
            btn.Click += (_, _) => _ = ApplyUpdateAsync(new DeviceTuningUpdate { LodCode = code });
            LodButtonsPanel.Children.Add(btn);
        }

        int activeIdx = Math.Max(0, _lodOptions.FindIndex(o => o.Code == currentLod));
        LodSlider.IsVisible = _lodOptions.Count > 1;
        _lodUsesDirectMm = false;

        if (_lodOptions.Count > 1)
        {
            var mms = _lodOptions.Select(o => TryParseLodMm(o.Label)).ToList();
            bool uniformTenthMm = mms.All(m => m.HasValue)
                && Enumerable.Range(1, mms.Count - 1)
                    .All(i => Math.Abs((mms[i]!.Value - mms[i - 1]!.Value) - 0.1) < 0.015);
            if (uniformTenthMm)
            {
                _lodUsesDirectMm = true;
                _lodMinMm = mms[0]!.Value;
                SafeSetSlider(LodSlider, _lodMinMm, mms[^1]!.Value, 0.1, mms[activeIdx]!.Value);
            }
            else
            {
                SafeSetSlider(LodSlider, 0, _lodOptions.Count - 1, 1, activeIdx);
            }
        }
        LodSliderValueText.Text = _lodOptions.Count > 0 ? FormatLodLabel(_lodOptions[activeIdx]) : "-";
    }

    private void OnLodSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding || _lodOptions.Count == 0 || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        int idx = _lodUsesDirectMm
            ? Math.Clamp((int)Math.Round((e.NewValue - _lodMinMm) / 0.1), 0, _lodOptions.Count - 1)
            : Math.Clamp((int)Math.Round(e.NewValue), 0, _lodOptions.Count - 1);
        var opt = _lodOptions[idx];
        LodSliderValueText.Text = FormatLodLabel(opt);
        if (_caps != null && opt.Code == _caps.CurrentLod) return;
        ScheduleDebouncedUpdate(new DeviceTuningUpdate { LodCode = opt.Code });
    }

    private void OnLodSliderReleased(object? sender, PointerReleasedEventArgs e) => FlushDebouncedUpdate();

    private static string FormatLodLabel(LodOption opt)
    {
        double? mm = TryParseLodMm(opt.Label);
        if (mm.HasValue) return opt.Label;
        return double.TryParse(opt.Label, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double raw)
            ? $"{raw:0.0} mm"
            : opt.Label;
    }

    private static double? TryParseLodMm(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var m = Regex.Match(label, @"(\d+(?:\.\d+)?)\s*mm", RegexOptions.IgnoreCase);
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double mm) ? mm : null;
    }

    private void OnAsymSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding) return;
        int lift = Math.Clamp((int)Math.Round(AsymLiftSlider.Value * 10), 2, 26);
        int land = Math.Clamp((int)Math.Round(AsymLandSlider.Value * 10), 1, 25);
        if (land >= lift)
        {
            land = lift - 1;
            _binding = true;
            AsymLandSlider.Value = land * 0.1;
            _binding = false;
        }
        AsymLiftText.Text = $"{lift * 0.1:0.0} mm";
        AsymLandText.Text = $"{land * 0.1:0.0} mm";
    }

    private void OnApplyAsymLod(object? sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsAsymmetricLod) return;
        if (sender == AsymLodSwitch && Environment.TickCount64 < _suppressPostBindUntilTick) return;
        int lift = Math.Clamp((int)Math.Round(AsymLiftSlider.Value * 10), 2, 26);
        int land = Math.Clamp((int)Math.Round(AsymLandSlider.Value * 10), 1, lift - 1);
        bool enabled = AsymLodSwitch.IsChecked == true;
        if (sender == AsymLodSwitch && enabled == _caps.AsymmetricLodEnabled && lift == _caps.AsymmetricLiftOff && land == _caps.AsymmetricLanding)
            return;
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { AsymmetricLod = (enabled, lift, land) });
    }

    private void OnDebounceSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int ms = (int)Math.Round(e.NewValue);
        DebounceValueText.Text = $"{ms} ms";
        if (_binding || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        if (_caps != null && ms == _caps.CurrentDebounceMs) return;
        ScheduleDebouncedUpdate(new DeviceTuningUpdate { DebounceMs = ms });
    }

    private void OnDebounceSliderReleased(object? sender, PointerReleasedEventArgs e) => FlushDebouncedUpdate();

    private void OnAngleSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int deg = (int)Math.Round(e.NewValue);
        AngleValueText.Text = $"{deg}°";
        if (_binding || Environment.TickCount64 < _suppressPostBindUntilTick) return;
        if (_caps != null && deg == _caps.AngleTuningDeg) return;
        ScheduleDebouncedUpdate(new DeviceTuningUpdate { AngleTuningDeg = deg });
    }

    private void OnAngleSliderReleased(object? sender, PointerReleasedEventArgs e) => FlushDebouncedUpdate();

    private void BuildDiscreteSleepButtons(List<int> secondsList, int currentSeconds)
    {
        DiscreteSleepPanel.Children.Clear();
        foreach (int sec in secondsList)
        {
            int s = sec;
            string label = s == 0 ? "从不休眠" : s < 60 ? $"{s} 秒" : $"{s / 60} 分钟";
            var btn = new Button { Content = label, Classes = { "pill" } };
            if (s <= 0)
            {
                btn.IsEnabled = false;
                ToolTip.SetTip(btn, "禁止写入 0 休眠（永不休眠），已禁用该档位");
            }
            else
            {
                if (s == currentSeconds) SetActiveButton(btn, true);
                btn.Click += (_, _) =>
                {
                    if (IsLeobogWireless())
                    {
                        ShowStatus("LEOBOG Hi98C Pro 无线模式严格只读，无法写入休眠时间。", InfoBarSeverity.Warning);
                        return;
                    }
                    if (s <= 0)
                    {
                        ShowStatus("禁止写入 0 休眠（永不休眠）。", InfoBarSeverity.Warning);
                        return;
                    }
                    _ = ApplyUpdateAsync(new DeviceTuningUpdate { SleepSeconds = SafeSleepSeconds(s) });
                };
            }
            DiscreteSleepPanel.Children.Add(btn);
        }
    }

    private void OnSleepSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
        => SleepValueText.Text = $"{(int)Math.Round(e.NewValue)} 分钟后休眠";

    private void OnApplyPower(object? sender, RoutedEventArgs e)
    {
        if (_caps == null) return;
        if (IsLeobogWireless())
        {
            ShowStatus("LEOBOG Hi98C Pro 无线模式严格只读，无法写入休眠/省电参数。", InfoBarSeverity.Warning);
            return;
        }
        var update = new DeviceTuningUpdate();
        if (_caps.SupportsSleepTimeout && _caps.SupportedSleepSeconds.Count == 0)
            update.SleepSeconds = SafeSleepSeconds((int)Math.Round(SleepSlider.Value) * 60);
        if (_caps.SupportsLowBatteryWarning)
            update.LowBatteryWarningPercent = (int)Math.Round(LowBatSlider.Value);
        _ = ApplyUpdateAsync(update);
    }

    // ── 板载槽位与预设 ───────────────────────────────────────────────────────────
    private void BuildOnboardProfileButtons(DeviceTuningCapabilities caps, List<CachedOnboardSlotInfo> cachedSlots)
    {
        OnboardProfileButtonsPanel.Children.Clear();
        int count = caps.OnboardProfileCount > 0 ? caps.OnboardProfileCount : cachedSlots.Count;
        int activeSlot = caps.OnboardProfileCount > 0
            ? caps.ActiveOnboardProfile
            : (App.Settings.Current.DeviceOnboardManifests.TryGetValue(_deviceId ?? "", out var m) ? m.ActiveSlot : -1);

        for (int i = 0; i < count; i++)
        {
            int slot = i;
            var cached = cachedSlots.FirstOrDefault(s => s.SlotIndex == i);
            string baseName = i < caps.OnboardProfileNames.Count && !string.IsNullOrWhiteSpace(caps.OnboardProfileNames[i])
                ? caps.OnboardProfileNames[i]
                : (cached != null && !string.IsNullOrWhiteSpace(cached.Name) ? cached.Name : $"槽位 {i + 1}");
            var btn = new Button
            {
                Content = baseName,
                FontSize = 12,
                MinWidth = 58,
                Height = 30,
                Padding = new Thickness(12, 0),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                IsEnabled = caps.IsOnline,
            };
            string tip = !string.IsNullOrWhiteSpace(cached?.Summary)
                ? $"{baseName}: {cached.Summary}"
                : (caps.IsOnline ? $"点击切换到 {baseName}" : "待唤醒后可切换");
            ToolTip.SetTip(btn, tip);
            if (slot == activeSlot) SetActiveButton(btn, true);
            btn.Click += (_, _) => _ = ApplyUpdateAsync(new DeviceTuningUpdate { ActiveOnboardProfile = slot });
            OnboardProfileButtonsPanel.Children.Add(btn);
        }
    }

    private async void OnProbeAllOnboardSlots(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_deviceId) || _busy) return;
        SetBusy(true);
        try
        {
            var manifest = await App.Settings.ProbeAllOnboardSlotsAsync(App.Devices, _deviceId);
            var caps = await App.Devices.QueryCapabilitiesAsync(_deviceId);
            BindCapabilities(caps);
            if (manifest != null)
                ShowStatus($"已读取 {manifest.Slots.Count} 个厂家板载配置并同步至游戏切换下拉菜单。",
                    InfoBarSeverity.Success);
            else
                ShowStatus("设备未唤醒或不支持板载槽位读取。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowStatus($"扫描板载槽位失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshDeviceSavedPresets()
    {
        DeviceSavedPresetsPanel.Children.Clear();
        if (string.IsNullOrWhiteSpace(_deviceId)) return;

        var list = App.Settings.Current.Presets
            .Where(p => string.Equals(p.SourceDeviceId, _deviceId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (list.Count == 0)
        {
            DeviceSavedPresetsPanel.Children.Add(new TextBlock
            {
                Text = "暂无快速预设 · 可在此保存当前 DPI / 回报率 / RT 为一键切换预设",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush", Color.FromArgb(180, 150, 150, 150)),
            });
            return;
        }

        foreach (var preset in list)
        {
            var parts = new List<string>();
            if (preset.DpiX is { } x)
                parts.Add(preset.DpiY is { } y && y != x ? $"DPI X={x}/Y={y}" : $"DPI {x}");
            if (preset.PollingRateHz is { } rate) parts.Add($"{rate}Hz");
            if (preset.PerformanceMode is { } pm) parts.Add($"模式#{pm}");
            if (preset.LodCode is { } lod) parts.Add($"LOD#{lod}");
            if (preset.MotionSync == true) parts.Add("MotionSync");
            if (preset.DebounceMs is { } db) parts.Add($"防抖{db}ms");
            if (preset.AnalogSwitches != null) parts.Add($"触发{preset.AnalogSwitches.Left.Actuation}/RT");

            var chip = new Border
            {
                Background = ResolveThemeBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(24, 128, 128, 128)),
                BorderBrush = ResolveThemeBrush("CardStrokeColorDefaultBrush", Color.FromArgb(40, 128, 128, 128)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 6, 4),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

            var applyBtn = new Button
            {
                Content = preset.Name,
                Classes = { "accent", "pill" },
                IsEnabled = _caps is { IsOnline: true },
            };
            ToolTip.SetTip(applyBtn, parts.Count > 0 ? $"点击立即写入: {string.Join(" · ", parts)}" : "点击立即写入设备");
            applyBtn.Click += (_, _) => _ = ApplyUpdateAsync(preset.ToUpdate());
            row.Children.Add(applyBtn);

            var updateBtn = new Button
            {
                Content = "覆盖",
                Classes = { "pill" },
                IsEnabled = _caps is { IsOnline: true },
            };
            ToolTip.SetTip(updateBtn, "用当前设备参数覆盖此预设");
            updateBtn.Click += (_, _) =>
            {
                if (_caps is not { IsOnline: true }) return;
                try
                {
                    var dev = App.LastDevices.FirstOrDefault(x =>
                        string.Equals(x.Id, _deviceId, StringComparison.OrdinalIgnoreCase));
                    var updated = MouseHardwarePreset.CaptureFrom(preset.Name, _deviceId, dev?.Name ?? _deviceId, _caps);
                    updated.Id = preset.Id;
                    int idx = App.Settings.Current.Presets.FindIndex(p => p.Id == preset.Id);
                    if (idx >= 0) App.Settings.Current.Presets[idx] = updated;
                    App.Settings.Save();
                    RefreshDeviceSavedPresets();
                    ShowStatus($"已使用当前硬件参数覆盖更新预设「{preset.Name}」。", InfoBarSeverity.Success);
                }
                catch (Exception ex)
                {
                    ShowStatus($"覆盖预设失败：{ex.Message}", InfoBarSeverity.Error);
                }
            };
            row.Children.Add(updateBtn);

            var delBtn = new Button { Content = "×", Classes = { "pill" } };
            ToolTip.SetTip(delBtn, $"删除预设「{preset.Name}」");
            delBtn.Click += (_, _) =>
            {
                try
                {
                    App.Settings.Current.Presets.RemoveAll(p => p.Id == preset.Id);
                    App.Settings.Save();
                    RefreshDeviceSavedPresets();
                }
                catch (Exception ex)
                {
                    ShowStatus($"删除预设失败：{ex.Message}", InfoBarSeverity.Error);
                }
            };
            row.Children.Add(delBtn);
            chip.Child = row;
            DeviceSavedPresetsPanel.Children.Add(chip);
        }
    }

    private void OnSaveAsGamePreset(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_caps == null || !_caps.IsOnline || string.IsNullOrWhiteSpace(_deviceId))
            {
                ShowStatus("请先唤醒设备并读取硬件参数后再保存预设。", InfoBarSeverity.Warning);
                return;
            }
            var dev = App.LastDevices.FirstOrDefault(x =>
                string.Equals(x.Id, _deviceId, StringComparison.OrdinalIgnoreCase));
            string devName = dev?.Name ?? _deviceId;
            string name = string.IsNullOrWhiteSpace(SavePresetNameBox.Text)
                ? $"{devName} {(_caps.CurrentDpiX > 0 ? _caps.CurrentDpiX : 800)}DPI"
                : SavePresetNameBox.Text.Trim();

            var preset = MouseHardwarePreset.CaptureFrom(name, _deviceId, devName, _caps);
            App.Settings.Current.Presets.Add(preset);
            App.Settings.Save();
            SavePresetNameBox.Text = "";
            RefreshDeviceSavedPresets();
            ShowStatus($"已在本设备保存预设「{name}」，已同步加入到游戏规则下拉列表。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"保存预设失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ── 按键重映射与快速绑宏 ─────────────────────────────────────────────────────
    private void BindButtonMappingsAndQuickMacros(DeviceTuningCapabilities caps)
    {
        if (!caps.IsOnline)
            MacroNoteText.Text = "设备当前处于无线休眠或待唤醒状态（按键板载映射需唤醒后读取；软件宏绑定与宏库编排永久可用）。";
        else if (!string.IsNullOrWhiteSpace(caps.MacroCapabilityNote))
            MacroNoteText.Text = caps.MacroCapabilityNote;
        else if (caps.SupportsOnboardMacroUpload)
            MacroNoteText.Text = "支持「绑定软件宏」或「写入硬件宏」（写入板载宏存在风险，建议优先使用官方驱动/网页驱动）。";
        else
            MacroNoteText.Text = "当前设备暂不支持写入硬件板载宏，仅支持通过 GearDeck「绑定软件宏」（如需板载宏请使用厂商官方驱动/网页驱动）。";

        QuickUploadHardwareMacroBtn.IsVisible = caps.IsOnline && caps.SupportsOnboardMacroUpload;
        ButtonBindingsSection.IsVisible = caps.ButtonBindings.Count > 0;
        ButtonBindingsPanel.Children.Clear();

        foreach (var slot in caps.ButtonBindings)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(165)));
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            row.Children.Add(new TextBlock
            {
                Text = slot.ButtonName,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeight.SemiBold,
            });

            if (slot.IsReadOnly)
            {
                var ro = new TextBlock
                {
                    Text = $"{FormatBindingLabel(slot.CurrentAction)}（主键防误触锁定）",
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush", Color.FromArgb(180, 150, 150, 150)),
                };
                Grid.SetColumn(ro, 1);
                row.Children.Add(ro);
            }
            else
            {
                var options = slot.AvailableActions.ToList();
                if (!options.Contains(slot.CurrentAction))
                    options.Insert(0, slot.CurrentAction);

                var combo = new ComboBox
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(10, 0, 10, 0),
                };
                foreach (var act in options)
                    combo.Items.Add(new ComboBoxItem { Content = FormatBindingLabel(act), Tag = act });
                combo.SelectedIndex = Math.Max(0, options.IndexOf(slot.CurrentAction));
                combo.SelectionChanged += (_, _) =>
                {
                    if (_binding) return;
                    if (combo.SelectedItem is ComboBoxItem item && item.Tag is string actionStr
                        && actionStr != slot.CurrentAction)
                        _ = ApplyUpdateAsync(new DeviceTuningUpdate { ButtonBinding = (slot.SlotIndex, actionStr) });
                };
                Grid.SetColumn(combo, 1);
                row.Children.Add(combo);

                var kbBtn = new Button
                {
                    Content = "设组合键...",
                    Classes = { "pill" },
                    Margin = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                int slotIdx = slot.SlotIndex;
                string btnName = slot.ButtonName;
                kbBtn.Click += async (_, _) =>
                {
                    if (!kbBtn.IsEnabled) return;
                    kbBtn.IsEnabled = false;
                    try
                    {
                        await ShowKeyboardShortcutDialogAsync(slotIdx, btnName);
                    }
                    catch (Exception ex)
                    {
                        ShowStatus($"绑定快捷键失败：{ex.Message}", InfoBarSeverity.Error);
                    }
                    finally
                    {
                        kbBtn.IsEnabled = true;
                    }
                };
                Grid.SetColumn(kbBtn, 2);
                row.Children.Add(kbBtn);
            }
            ButtonBindingsPanel.Children.Add(row);
        }
        BindQuickMacroControls();
    }

    private void BindQuickMacroControls()
    {
        QuickMacroCombo.Items.Clear();
        foreach (var m in App.Settings.Current.Macros)
        {
            int actionCount = m.Steps.Count(s => s.KeyCode != 0);
            QuickMacroCombo.Items.Add(new ComboBoxItem { Content = $"{m.Name} ({actionCount} 动作)", Tag = m.Id });
        }
        if (QuickMacroCombo.Items.Count > 0) QuickMacroCombo.SelectedIndex = 0;

        QuickMacroBtnCombo.Items.Clear();
        var currentDev = App.LastDevices.FirstOrDefault(d => string.Equals(d.Id, _deviceId, StringComparison.OrdinalIgnoreCase));
        bool isKeyboardOnboard = currentDev?.Kind == DeviceKind.Keyboard && _caps is { SupportsOnboardMacroUpload: true };
        QuickBindSoftwareMacroBtn.IsVisible = !isKeyboardOnboard;

        if (isKeyboardOnboard && _caps != null)
        {
            int slots = Math.Clamp(_caps.OnboardMacroSlotCount, 1, 32);
            for (int i = 0; i < slots; i++)
                QuickMacroBtnCombo.Items.Add(new ComboBoxItem { Content = $"板载宏槽位 M{i}", Tag = $"Slot:{i}" });
        }
        else
        {
            foreach (var (key, label) in MacroExecutionService.SoftwareTriggerButtons)
                QuickMacroBtnCombo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        }
        if (QuickMacroBtnCombo.Items.Count > 0) QuickMacroBtnCombo.SelectedIndex = 0;

        RefreshDeviceMacroBindingsSummary();
    }

    private void RefreshDeviceMacroBindingsSummary()
    {
        DeviceMacroBindingsSummary.Children.Clear();
        if (string.IsNullOrWhiteSpace(_deviceId)) return;

        string prefix = $"{_deviceId}|";
        var matches = App.Settings.Current.LocalButtonMacroBindings
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var kv in matches)
        {
            string trigger = kv.Key[prefix.Length..];
            string macroName = App.Settings.Current.Macros.FirstOrDefault(m => m.Id == kv.Value)?.Name ?? kv.Value;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = $"已绑：{trigger} → 宏「{macroName}」",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ResolveThemeBrush("AccentTextFillColorPrimaryBrush", Color.FromRgb(0, 120, 215)),
            });
            var unbind = new Button { Content = "解绑", Classes = { "pill" } };
            string k = kv.Key;
            unbind.Click += (_, _) =>
            {
                try
                {
                    App.Settings.Current.LocalButtonMacroBindings.Remove(k);
                    App.Settings.Save();
                    App.MacroRunner?.SyncHookState();
                    RefreshDeviceMacroBindingsSummary();
                }
                catch (Exception ex)
                {
                    ShowStatus($"解绑失败：{ex.Message}", InfoBarSeverity.Error);
                }
            };
            row.Children.Add(unbind);
            DeviceMacroBindingsSummary.Children.Add(row);
        }
    }

    private bool TryGetQuickMacroSelection(out OnboardMacroDefinition macro, out string triggerKey)
    {
        macro = null!;
        triggerKey = (QuickMacroBtnCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "XButton1";
        if (string.IsNullOrWhiteSpace(_deviceId))
            return false;

        if (QuickMacroCombo.SelectedItem is not ComboBoxItem mItem || mItem.Tag is not string macroId)
        {
            ShowStatus("请先前往「鼠标宏中心」创建一个宏。", InfoBarSeverity.Warning);
            return false;
        }

        var found = App.Settings.Current.Macros.FirstOrDefault(m => m.Id == macroId);
        if (found == null || found.Steps.Count == 0)
        {
            ShowStatus("所选宏暂无动作步骤，请先在「鼠标宏中心」录制或添加动作。", InfoBarSeverity.Warning);
            return false;
        }

        macro = found;
        return true;
    }

    private void OnQuickBindSoftwareMacro(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !btn.IsEnabled) return;
        btn.IsEnabled = false;
        try
        {
            if (!TryGetQuickMacroSelection(out var macro, out string triggerKey))
                return;

            App.Settings.Current.LocalButtonMacroBindings[$"{_deviceId}|{triggerKey}"] = macro.Id;
            App.Settings.Save();
            App.MacroRunner?.SyncHookState();
            RefreshDeviceMacroBindingsSummary();
            ShowStatus($"已将软件宏「{macro.Name}」绑定到本设备的 {triggerKey} 按键。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"绑定软件宏失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private async void OnQuickUploadHardwareMacro(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !btn.IsEnabled) return;
        btn.IsEnabled = false;
        try
        {
            if (!TryGetQuickMacroSelection(out var macro, out string triggerKey))
                return;

            if (_caps is not { IsOnline: true, SupportsOnboardMacroUpload: true })
            {
                ShowStatus("当前设备离线或不支持写入硬件板载宏，请改用「绑定软件宏」。", InfoBarSeverity.Warning);
                return;
            }

            var dlg = new ContentDialog
            {
                Title = "写入硬件宏风险提示",
                Content = new TextBlock
                {
                    Text = $"即将向设备板载存储写入宏「{macro.Name}」（槽位：{triggerKey}）。\n\n"
                        + "⚠️ 风险说明：直接向设备内部 Flash/EEPROM 写入板载宏存在一定风险，若固件版本或协议布局存在差异，可能导致板载配置异常、按键失灵甚至需要重新恢复出厂设置。\n\n"
                        + "建议：强烈建议前往厂商官方网站或使用官方驱动/网页驱动写入板载宏；或改用更安全的「绑定软件宏」（仅在电脑端生效，不改动设备固件与板载存储）。",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 440,
                },
                PrimaryButtonText = "仍要写入硬件宏",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dlg.ShowAsync() != ContentDialogResult.Primary)
                return;

            int macroSlot;
            int wireSlot;
            if (triggerKey.StartsWith("Slot:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(triggerKey.AsSpan(5), out int parsedSlot))
            {
                macroSlot = Math.Max(0, parsedSlot);
                wireSlot = macroSlot;
            }
            else
            {
                wireSlot = triggerKey switch { "Middle" => 3, "XButton2" => 5, _ => 4 };
                macroSlot = wireSlot - 3;
            }

            var res = await App.Devices.ApplySettingsAsync(_deviceId!, new DeviceTuningUpdate
            {
                UploadMacro = (macroSlot, wireSlot, macro),
            });
            ShowStatus(res.Message, res.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowStatus($"写入硬件宏失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private void OnOpenMacroHub(object? sender, RoutedEventArgs e)
        => ShowStatus("请在主窗口左侧「鼠标宏中心」录制与编辑动作。", InfoBarSeverity.Informational);

    private static string FormatBindingLabel(string raw)
    {
        if (HidKeyCatalog.TryParseKeyboardBinding(raw, out byte mod, out byte usage))
            return $"键盘: {HidKeyCatalog.FormatModifiersAndKey(mod, usage)}";
        return raw;
    }

    private async Task ShowKeyboardShortcutDialogAsync(int slotIndex, string buttonName)
    {
        var ctrlChk = new CheckBox { Content = "Ctrl" };
        var shiftChk = new CheckBox { Content = "Shift" };
        var altChk = new CheckBox { Content = "Alt" };
        var winChk = new CheckBox { Content = "Win" };
        var modRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        modRow.Children.Add(ctrlChk);
        modRow.Children.Add(shiftChk);
        modRow.Children.Add(altChk);
        modRow.Children.Add(winChk);

        var keyCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var k in HidKeyCatalog.AllKeys.Where(k => k.Usage is >= 0x04 and < 0xF0))
            keyCombo.Items.Add(new ComboBoxItem { Content = k.Label, Tag = k.Usage });
        if (keyCombo.Items.Count > 0) keyCombo.SelectedIndex = 0;

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(modRow);
        panel.Children.Add(keyCombo);

        var dlg = new ContentDialog
        {
            Title = $"为「{buttonName}」绑定硬件键盘快捷键",
            Content = panel,
            PrimaryButtonText = "写入鼠标硬件",
            CloseButtonText = "取消",
        };
        await dlg.ShowAsync();
        if (keyCombo.SelectedItem is not ComboBoxItem selected || selected.Tag is not byte targetUsage)
            return;

        byte mods = 0;
        if (ctrlChk.IsChecked == true) mods |= HidKeyCatalog.ModLeftCtrl;
        if (shiftChk.IsChecked == true) mods |= HidKeyCatalog.ModLeftShift;
        if (altChk.IsChecked == true) mods |= HidKeyCatalog.ModLeftAlt;
        if (winChk.IsChecked == true) mods |= HidKeyCatalog.ModLeftWin;

        string action = HidKeyCatalog.FormatKeyboardBinding(mods, targetUsage);
        await ApplyUpdateAsync(new DeviceTuningUpdate { ButtonBinding = (slotIndex, action) });
    }

    // ── HITS / RT 微动调校 ───────────────────────────────────────────────────────
    private void BindAnalogSwitches(DeviceTuningCapabilities caps)
    {
        if (!caps.SupportsAnalogSwitchTuning)
        {
            AnalogSwitchCard.IsVisible = false;
            UpdateAnalogPressTimerState();
            return;
        }
        AnalogSwitchCard.IsVisible = true;
        AnalogTechTitle.Text = caps.AnalogSwitchTechName;
        _leftAnalog = caps.LeftAnalogButton ?? new AnalogButtonConfig(0, 4, true, 3, true, Math.Min(4, caps.AnalogMaxHaptics));
        _rightAnalog = caps.RightAnalogButton ?? new AnalogButtonConfig(1, _leftAnalog.Actuation,
            _leftAnalog.RapidTriggerEnabled, _leftAnalog.RapidTrigger, _leftAnalog.HapticsEnabled, _leftAnalog.Haptics);
        if (!_analogModeInitialized)
        {
            _analogIndependent = (_leftAnalog with { ButtonIndex = 0 }) != (_rightAnalog with { ButtonIndex = 0 });
            _analogModeInitialized = true;
        }
        RenderAnalogSwitchControls();
        UpdateAnalogPressTimerState();
    }

    private void RenderAnalogSwitchControls()
    {
        var caps = _caps;
        if (caps == null || !caps.SupportsAnalogSwitchTuning) return;
        bool wasBinding = _binding;
        _binding = true;
        try
        {
            SetActiveButton(AnalogLinkBothBtn, !_analogIndependent);
            SetActiveButton(AnalogIndepBtn, _analogIndependent);
            AnalogModeDescText.Text = _analogIndependent
                ? "独立调节：分别定制左主按键与右主按键的触发点、RT 与反馈强度"
                : "两个主按键：将相同数值实时同步应用到左右两个按键";
            AnalogSideSelectorPanel.IsVisible = _analogIndependent;
            SetActiveButton(AnalogSelectLeftBtn, !_analogEditingRight);
            SetActiveButton(AnalogSelectRightBtn, _analogEditingRight);
            ApplyAnalogBtn.Content = _analogIndependent
                ? (_analogEditingRight ? "应用右主按键参数" : "应用左主按键参数")
                : "应用到两个按键";

            var active = (_analogIndependent && _analogEditingRight) ? _rightAnalog : _leftAnalog;
            BuildAnalogVisualBars(caps.AnalogMaxActuation);

            int maxAct = Math.Clamp(caps.AnalogMaxActuation, 5, 20);
            AnalogActuationPanel.Children.Clear();
            for (int lvl = 1; lvl <= maxAct; lvl++)
            {
                int v = lvl;
                var pill = new Button { Content = v.ToString(), Classes = { "pill" } };
                if (active.Actuation == v) SetActiveButton(pill, true);
                pill.Click += (_, _) => SetAnalogActuation(v);
                AnalogActuationPanel.Children.Add(pill);
            }
            SafeSetSlider(AnalogActSlider, 1, maxAct, 1, active.Actuation);
            AnalogActValueText.Text = $"当前触发点 {active.Actuation}/{maxAct}（1 次短按 — {maxAct} 次长按）";

            int maxRt = Math.Clamp(caps.AnalogMaxRapidTrigger, 3, 10);
            SetActiveButton(AnalogRtOffBtn, !active.RapidTriggerEnabled);
            SetActiveButton(AnalogRtOnBtn, active.RapidTriggerEnabled);
            AnalogRtPanel.Children.Clear();
            for (int rt = 1; rt <= maxRt; rt++)
            {
                int v = rt;
                var pill = new Button { Content = v.ToString(), Classes = { "pill" }, IsEnabled = active.RapidTriggerEnabled };
                if (active.RapidTriggerEnabled && active.RapidTrigger == v) SetActiveButton(pill, true);
                pill.Click += (_, _) => SetAnalogRtSensitivity(v);
                AnalogRtPanel.Children.Add(pill);
            }

            if (caps.SupportsAnalogHapticsSwitch)
            {
                AnalogHapDiscretePanel.IsVisible = false;
                AnalogHapSliderPanel.IsVisible = true;
                SetActiveButton(AnalogHapOffBtn, !active.HapticsEnabled);
                SetActiveButton(AnalogHapOnBtn, active.HapticsEnabled);
                int maxHap = Math.Max(1, caps.AnalogMaxHaptics);
                SafeSetSlider(AnalogHapSlider, 0, maxHap, maxHap >= 50 ? 5 : 1, active.Haptics);
                AnalogHapSlider.IsEnabled = active.HapticsEnabled;
                AnalogHapValueText.Text = active.HapticsEnabled ? $"{active.Haptics}%" : "关闭";
            }
            else
            {
                AnalogHapDiscretePanel.IsVisible = true;
                AnalogHapSliderPanel.IsVisible = false;
                AnalogHapDiscretePanel.Children.Clear();
                int maxHap = Math.Clamp(caps.AnalogMaxHaptics, 1, 10);
                for (int h = 0; h <= maxHap; h++)
                {
                    int v = h;
                    var pill = new Button { Content = v == 0 ? "0 关" : v.ToString(), Classes = { "pill" } };
                    if (active.Haptics == v) SetActiveButton(pill, true);
                    pill.Click += (_, _) => SetAnalogDiscreteHaptics(v);
                    AnalogHapDiscretePanel.Children.Add(pill);
                }
            }
        }
        finally
        {
            _binding = wasBinding;
        }
    }

    private void BuildAnalogVisualBars(int maxActuation)
    {
        AnalogVisualBarsPanel.Children.Clear();
        int total = Math.Clamp(maxActuation, 5, 20);
        _leftBarSegments = BuildSingleAnalogBar("Left", _leftAnalog, total, out _leftBarSummary);
        _rightBarSegments = BuildSingleAnalogBar("Right", _rightAnalog, total, out _rightBarSummary);
        RenderLiveAnalogBars();
    }

    private Border[] BuildSingleAnalogBar(string label, AnalogButtonConfig cfg, int totalSteps, out TextBlock summaryBlock)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(46)));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush", Color.FromArgb(180, 150, 150, 150)),
        });

        var segGrid = new Grid
        {
            Margin = new Thickness(10, 0, 12, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var segments = new Border[totalSteps];
        for (int i = 0; i < totalSteps; i++)
        {
            segGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var box = new Border
            {
                Height = 8,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(2, 0),
            };
            Grid.SetColumn(box, i);
            segGrid.Children.Add(box);
            segments[i] = box;
        }
        Grid.SetColumn(segGrid, 1);
        row.Children.Add(segGrid);

        summaryBlock = new TextBlock
        {
            Text = FormatConfigSummary(cfg),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 116,
            Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush", Color.FromArgb(180, 150, 150, 150)),
        };
        Grid.SetColumn(summaryBlock, 2);
        row.Children.Add(summaryBlock);
        AnalogVisualBarsPanel.Children.Add(row);
        return segments;
    }

    private static string FormatConfigSummary(AnalogButtonConfig cfg)
        => cfg.RapidTriggerEnabled ? $"触发 {cfg.Actuation} · RT {cfg.RapidTrigger}" : $"触发 {cfg.Actuation} · RT 关";

    private void UpdateAnalogPressTimerState()
    {
        bool shouldRun = IsLoaded && _activeTab == "buttons" && AnalogSwitchCard.IsVisible;
        if (!shouldRun)
        {
            _analogPressTimer?.Stop();
            return;
        }
        if (_analogPressTimer == null)
        {
            _analogPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            _analogPressTimer.Tick += (_, _) => UpdateAnalogLiveSample();
        }
        if (!_analogPressTimer.IsEnabled)
            _analogPressTimer.Start();
    }

    private void OnAnalogPressDown(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(AnalogPressTestPad);
        if (point.Properties.IsLeftButtonPressed) _macLeftPointerPressed = true;
        if (point.Properties.IsRightButtonPressed) _macRightPointerPressed = true;
        UpdateAnalogLiveSample();
    }

    private void OnAnalogPressSample(object? sender, PointerReleasedEventArgs e)
    {
        var props = e.GetCurrentPoint(AnalogPressTestPad).Properties;
        _macLeftPointerPressed = props.IsLeftButtonPressed;
        _macRightPointerPressed = props.IsRightButtonPressed;
        UpdateAnalogLiveSample();
    }

    private void UpdateAnalogLiveSample()
    {
        try
        {
            if (!AnalogSwitchCard.IsVisible || _leftBarSegments.Length == 0)
                return;
            bool swapped = OperatingSystem.IsWindows() && GetSystemMetrics(23) != 0;
            int vkLeft = swapped ? 0x02 : 0x01;
            int vkRight = swapped ? 0x01 : 0x02;
            bool leftPressed = OperatingSystem.IsWindows()
                ? (GetAsyncKeyState(vkLeft) & 0x8000) != 0
                : _macLeftPointerPressed;
            bool rightPressed = OperatingSystem.IsWindows()
                ? (GetAsyncKeyState(vkRight) & 0x8000) != 0
                : _macRightPointerPressed;

            StepAnalogSide(leftPressed, _leftAnalog, _leftBarSegments.Length,
                ref _leftTravel, ref _leftRtExtreme, ref _leftTriggered);
            StepAnalogSide(rightPressed, _rightAnalog, _rightBarSegments.Length,
                ref _rightTravel, ref _rightRtExtreme, ref _rightTriggered);
            RenderLiveAnalogBars();
        }
        catch (Exception ex)
        {
            Diag.Log($"UpdateAnalogLiveSample 异常: {ex.Message}");
        }
    }

    private static void StepAnalogSide(bool pressed, AnalogButtonConfig cfg, int totalSteps,
        ref double travel, ref double extreme, ref bool triggered)
    {
        double actThreshold = Math.Clamp(cfg.Actuation, 1, totalSteps);
        if (pressed)
        {
            double stepUp = Math.Max(0.45, totalSteps * 0.22);
            travel = Math.Min(totalSteps, travel + stepUp);
        }
        else
        {
            double stepDown = Math.Max(0.50, totalSteps * 0.24);
            travel = Math.Max(0.0, travel - stepDown);
        }
        if (!cfg.RapidTriggerEnabled)
        {
            triggered = travel >= actThreshold - 0.15;
            extreme = travel;
            return;
        }
        double rtDelta = Math.Clamp(0.25 + (Math.Max(1, cfg.RapidTrigger) - 1) * 0.35, 0.25, totalSteps * 0.65);
        if (travel <= 0.15)
        {
            triggered = false;
            extreme = 0.0;
            return;
        }
        if (triggered)
        {
            extreme = Math.Max(extreme, travel);
            if (extreme - travel >= rtDelta)
            {
                triggered = false;
                extreme = travel;
            }
        }
        else
        {
            if (extreme == 0.0)
            {
                if (travel >= actThreshold - 0.15)
                {
                    triggered = true;
                    extreme = travel;
                }
            }
            else
            {
                extreme = Math.Min(extreme, travel);
                if (travel - extreme >= rtDelta && travel >= 0.8)
                {
                    triggered = true;
                    extreme = travel;
                }
            }
        }
    }

    private void RenderLiveAnalogBars()
    {
        RenderSingleLiveBar(_leftBarSegments, _leftBarSummary, _leftAnalog, _leftTravel, _leftTriggered);
        RenderSingleLiveBar(_rightBarSegments, _rightBarSummary, _rightAnalog, _rightTravel, _rightTriggered);
        AnalogLiveStateText.Text = _leftTravel <= 0.05 && _rightTravel <= 0.05
            ? "待按压测试"
            : $"L {(int)Math.Round(_leftTravel * 100.0 / Math.Max(1, _leftBarSegments.Length))}% {(_leftTriggered ? "⚡触发" : "○")}  |  R {(int)Math.Round(_rightTravel * 100.0 / Math.Max(1, _rightBarSegments.Length))}% {(_rightTriggered ? "⚡触发" : "○")}";
    }

    private static void RenderSingleLiveBar(Border[] segments, TextBlock? summary, AnalogButtonConfig cfg,
        double travel, bool triggered)
    {
        if (segments.Length == 0) return;
        int totalSteps = segments.Length;
        int actIdx = Math.Clamp(cfg.Actuation, 1, totalSteps) - 1;
        int filledCount = travel > 0.05 ? Math.Clamp((int)Math.Ceiling(travel - 0.05), 1, totalSteps) : 0;

        var accentBrush = new SolidColorBrush(Color.FromArgb(255, 0, 120, 215));
        var idleBrush = new SolidColorBrush(Color.FromArgb(44, 128, 128, 128));
        var traceBrush = new SolidColorBrush(Color.FromArgb(72, 16, 185, 129));
        var pressingBrush = new SolidColorBrush(Color.FromArgb(200, 56, 189, 248));
        var activeTriggeredBrush = new SolidColorBrush(Color.FromArgb(240, 16, 185, 129));
        var actTriggeredMarkerBrush = new SolidColorBrush(Color.FromArgb(255, 245, 158, 11));

        for (int i = 0; i < totalSteps; i++)
        {
            bool isAct = i == actIdx;
            bool isFilled = i < filledCount;
            segments[i].Height = isAct ? (triggered ? 15 : 13) : (isFilled ? 10 : 8);
            if (isFilled)
                segments[i].Background = triggered ? (isAct ? actTriggeredMarkerBrush : activeTriggeredBrush) : pressingBrush;
            else if (isAct)
                segments[i].Background = triggered ? actTriggeredMarkerBrush : accentBrush;
            else
                segments[i].Background = (cfg.RapidTriggerEnabled && i < actIdx) ? traceBrush : idleBrush;
        }

        if (summary != null)
        {
            summary.Text = travel > 0.05
                ? $"力度 {(int)Math.Round(travel * 100.0 / totalSteps)}% {(triggered ? "● 已触发" : "○ 按压中")}"
                : FormatConfigSummary(cfg);
        }
    }

    private void UpdateActiveAnalogConfig(Func<AnalogButtonConfig, AnalogButtonConfig> mutator)
    {
        if (_analogIndependent && _analogEditingRight)
            _rightAnalog = mutator(_rightAnalog) with { ButtonIndex = 1 };
        else
        {
            _leftAnalog = mutator(_leftAnalog) with { ButtonIndex = 0 };
            if (!_analogIndependent)
                _rightAnalog = _leftAnalog with { ButtonIndex = 1 };
        }
    }

    private void SetAnalogMode(bool independent)
    {
        _analogIndependent = independent;
        if (!independent)
        {
            _analogEditingRight = false;
            _rightAnalog = _leftAnalog with { ButtonIndex = 1 };
        }
        RenderAnalogSwitchControls();
    }

    private void SelectAnalogSide(bool editRight)
    {
        _analogEditingRight = editRight;
        RenderAnalogSwitchControls();
    }

    private void SetAnalogActuation(int lvl)
    {
        UpdateActiveAnalogConfig(c => c with { Actuation = lvl });
        RenderAnalogSwitchControls();
        CommitAnalogSwitchUpdate(immediate: true);
    }

    private void OnAnalogActSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding) return;
        int lvl = Math.Clamp((int)Math.Round(e.NewValue), 1, _caps?.AnalogMaxActuation ?? 10);
        UpdateActiveAnalogConfig(c => c with { Actuation = lvl });
        RenderAnalogSwitchControls();
        CommitAnalogSwitchUpdate(immediate: false);
    }

    private void SetAnalogRtEnabled(bool enabled)
    {
        UpdateActiveAnalogConfig(c => c with
        {
            RapidTriggerEnabled = enabled,
            RapidTrigger = Math.Max(1, c.RapidTrigger),
        });
        RenderAnalogSwitchControls();
        CommitAnalogSwitchUpdate(immediate: true);
    }

    private void SetAnalogRtSensitivity(int lvl)
    {
        UpdateActiveAnalogConfig(c => c with { RapidTriggerEnabled = true, RapidTrigger = lvl });
        RenderAnalogSwitchControls();
        CommitAnalogSwitchUpdate(immediate: true);
    }

    private void SetAnalogDiscreteHaptics(int level)
    {
        UpdateActiveAnalogConfig(c => c with { HapticsEnabled = level > 0, Haptics = level });
        RenderAnalogSwitchControls();
        CommitAnalogSwitchUpdate(immediate: true);
    }

    private void SetAnalogHapticsEnabled(bool enabled)
    {
        UpdateActiveAnalogConfig(c => c with { HapticsEnabled = enabled });
        RenderAnalogSwitchControls();
        CommitAnalogSwitchUpdate(immediate: true);
    }

    private void OnAnalogHapSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding) return;
        int val = Math.Clamp((int)Math.Round(e.NewValue), 0, _caps?.AnalogMaxHaptics ?? 100);
        UpdateActiveAnalogConfig(c => c with { HapticsEnabled = true, Haptics = val });
        RenderAnalogSwitchControls();
        CommitAnalogSwitchUpdate(immediate: false);
    }

    private void OnAnalogSliderReleased(object? sender, PointerReleasedEventArgs e) => FlushDebouncedUpdate();

    private void OnApplyAnalog(object? sender, RoutedEventArgs e) => CommitAnalogSwitchUpdate(immediate: true);

    private void CommitAnalogSwitchUpdate(bool immediate)
    {
        var update = new DeviceTuningUpdate
        {
            AnalogSwitches = new AnalogSwitchUpdate(_analogIndependent, _leftAnalog, _rightAnalog),
        };
        if (immediate)
        {
            _debounceTimer.Stop();
            _debouncedPending = null;
            _ = ApplyUpdateAsync(update);
        }
        else
        {
            ScheduleDebouncedUpdate(update);
        }
    }

    // ── 硬件下发引擎 ─────────────────────────────────────────────────────────────
    private void ScheduleDebouncedUpdate(DeviceTuningUpdate update, int delayMs = 180)
    {
        _debouncedPending = _debouncedPending == null ? update : MergeUpdates(_debouncedPending, update);
        _debounceTimer.Stop();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 80, 600));
        _debounceTimer.Start();
    }

    private void FlushDebouncedUpdate()
    {
        _debounceTimer.Stop();
        var pending = _debouncedPending;
        _debouncedPending = null;
        if (pending != null)
            _ = ApplyUpdateAsync(pending);
    }

    private static IBrush ResolveThemeBrush(string key, Color fallback)
    {
        if (Application.Current is { } app &&
            app.TryFindResource(key, app.ActualThemeVariant, out var res) &&
            res is IBrush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

    private async Task ApplyUpdateAsync(DeviceTuningUpdate update)
    {
        if (string.IsNullOrWhiteSpace(_deviceId) || !IsLoaded) return;
        if (_busy)
        {
            _queuedUpdate = _queuedUpdate == null ? update : MergeUpdates(_queuedUpdate, update);
            return;
        }
        SetBusy(true);
        try
        {
            DeviceTuningUpdate? next = update;
            while (next != null)
            {
                var res = await App.Devices.ApplySettingsAsync(_deviceId, next);
                if (!IsLoaded) return;
                if (res.Capabilities != null)
                    BindCapabilities(res.Capabilities);
                ShowStatus(res.Message, res.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
                next = _queuedUpdate;
                _queuedUpdate = null;
            }
        }
        catch (Exception ex)
        {
            if (IsLoaded)
                ShowStatus($"写入硬件参数失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static DeviceTuningUpdate MergeUpdates(DeviceTuningUpdate prev, DeviceTuningUpdate next) => new()
    {
        SetHostMode = next.SetHostMode ?? prev.SetHostMode,
        ActiveOnboardProfile = next.ActiveOnboardProfile ?? prev.ActiveOnboardProfile,
        Dpi = next.Dpi ?? prev.Dpi,
        DpiStages = next.DpiStages ?? prev.DpiStages,
        ActiveDpiStage = next.ActiveDpiStage ?? prev.ActiveDpiStage,
        PollingRateHz = next.PollingRateHz ?? prev.PollingRateHz,
        PerformanceMode = next.PerformanceMode ?? prev.PerformanceMode,
        LodCode = next.LodCode ?? prev.LodCode,
        AsymmetricLod = next.AsymmetricLod ?? prev.AsymmetricLod,
        MotionSync = next.MotionSync ?? prev.MotionSync,
        AngleSnapping = next.AngleSnapping ?? prev.AngleSnapping,
        RippleControl = next.RippleControl ?? prev.RippleControl,
        LongRangeMode = next.LongRangeMode ?? prev.LongRangeMode,
        AngleTuningDeg = next.AngleTuningDeg ?? prev.AngleTuningDeg,
        DebounceMs = next.DebounceMs ?? prev.DebounceMs,
        SleepSeconds = next.SleepSeconds ?? prev.SleepSeconds,
        LowBatteryWarningPercent = next.LowBatteryWarningPercent ?? prev.LowBatteryWarningPercent,
        ButtonBinding = next.ButtonBinding ?? prev.ButtonBinding,
        UploadMacro = next.UploadMacro ?? prev.UploadMacro,
        AnalogSwitches = next.AnalogSwitches ?? prev.AnalogSwitches,
    };

    private async void OnRetryProbe(object? sender, RoutedEventArgs e)
    {
        if (_busy || !ReadCapsBtn.IsEnabled) return;
        ReadCapsBtn.IsEnabled = false;
        try
        {
            _autoWakeAttempts = 0;
            _nextAutoWakeTick = Environment.TickCount64 + AutoWakeBackoffMs[0];
            await RefreshAndReloadAsync(manual: true);
        }
        catch (Exception ex)
        {
            ShowStatus($"重新读取失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            ReadCapsBtn.IsEnabled = true;
        }
    }

    private void OnPagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_wakeOnMove || _busy || string.IsNullOrWhiteSpace(_deviceId) || _caps is not { IsOnline: false })
            return;
        if (_autoWakeAttempts >= AutoWakeBackoffMs.Length) return;
        long now = Environment.TickCount64;
        if (now < _nextAutoWakeTick) return;
        if (ExternalConfiguratorGuard.IsBackgroundYieldActive(out _)) return;

        int delay = AutoWakeBackoffMs[_autoWakeAttempts];
        _autoWakeAttempts++;
        _nextAutoWakeTick = now + delay;
        _ = RefreshAndReloadAsync(manual: false);
    }

    private async Task RefreshAndReloadAsync(bool manual)
    {
        if (string.IsNullOrWhiteSpace(_deviceId) || _busy) return;
        try
        {
            if (manual || !ExternalConfiguratorGuard.IsBackgroundYieldActive(out _))
            {
                await App.Devices.RefreshAsync(bypassCoexistenceGuard: manual);
                ShowOverview();
            }
        }
        catch { }

        await LoadCapabilitiesAsync();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
