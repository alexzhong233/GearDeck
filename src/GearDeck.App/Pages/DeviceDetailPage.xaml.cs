using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using GearDeck.App.Controls;
using GearDeck.App.Services;
using GearDeck.Core;
using GearDeck.Core.Models;
using GearDeck.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace GearDeck.App.Pages;

public sealed partial class DeviceDetailPage : Page
{
    private static readonly int[] CommonDpiPresets = [400, 800, 1200, 1600, 2400, 3200, 6400];

    private string? _deviceId;
    private DeviceTuningCapabilities? _caps;
    private bool _binding;
    private bool _busy;
    private DeviceTuningUpdate? _queuedUpdate;

    private string _activeTab = "perf";
    private LocalDriverInfo? _localDriver;
    private WebDriverInfo? _webDriver;

    private List<LodOption> _lodOptions = [];
    private bool _lodUsesDirectMm;
    private double _lodMinMm = 0.7;

    private bool _analogModeInitialized;
    private bool _analogIndependent;
    private bool _analogEditingRight;
    private AnalogButtonConfig _leftAnalog = new(0, 4, true, 3, true, 4);
    private AnalogButtonConfig _rightAnalog = new(1, 4, true, 3, true, 4);

    private Border[] _leftBarSegments = [];
    private Border[] _rightBarSegments = [];
    private TextBlock? _leftBarSummary;
    private TextBlock? _rightBarSummary;
    private DispatcherQueueTimer? _analogPressTimer;
    private double _leftTravel;
    private double _rightTravel;
    private double _leftRtExtreme;
    private double _rightRtExtreme;
    private bool _leftTriggered;
    private bool _rightTriggered;

    private DispatcherQueueTimer? _debounceTimer;
    private DeviceTuningUpdate? _debouncedPending;

    private static readonly int[] AutoWakeBackoffMs = [3000, 8000, 20000];
    private int _autoWakeAttempts;
    private long _nextAutoWakeTick;

    public DeviceDetailPage()
    {
        InitializeComponent();
        WireControlEvents();
        InitializeDebounceTimer();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RootShellGrid.PointerMoved += OnPagePointerMoved;
        RootShellGrid.SizeChanged += (_, e) =>
        {
            HeroColumnDef.Width = new GridLength(e.NewSize.Width < 860 ? 228 : 284);
        };
    }

    private void WireControlEvents()
    {
        BackBtn.Click += (_, _) =>
        {
            if (MainWindow.Instance != null) MainWindow.Instance.OpenDevicesPage();
            else Frame.Navigate(typeof(DevicesPage));
        };
        ReadCapsBtn.Click += OnRetryProbe;
        PickImageBtn.Click += OnPickDeviceImage;
        PinTaskbarBtn.Click += OnPinToTaskbar;

        DeviceLowBatEnableChk.Checked += OnDeviceLowBatCheckChanged;
        DeviceLowBatEnableChk.Unchecked += OnDeviceLowBatCheckChanged;
        DeviceLowBatNotifyChk.Checked += OnDeviceLowBatCheckChanged;
        DeviceLowBatNotifyChk.Unchecked += OnDeviceLowBatCheckChanged;
        DeviceLowBatThresholdBox.ValueChanged += OnDeviceLowBatThresholdChanged;
        DeviceLowBatResetBtn.Click += OnDeviceLowBatReset;

        LocalDriverBtn.Click += (_, _) =>
        {
            if (_localDriver == null) return;
            var res = VendorDriverLauncher.LaunchLocal(_localDriver);
            ShowStatus(res.Message, res.Launched ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
            UpdateCoexistenceBanner();
        };

        WebDriverBtn.Click += (_, _) =>
        {
            if (_webDriver == null) return;
            var res = VendorDriverLauncher.LaunchWeb(_webDriver);
            ShowStatus(res.Message, res.Launched ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
            UpdateCoexistenceBanner();
        };

        ResumeCoexistenceBtn.Click += (_, _) =>
        {
            ExternalConfiguratorGuard.ResumeNow();
            UpdateCoexistenceBanner();
            _ = App.Devices.RefreshAsync(bypassCoexistenceGuard: true);
            _ = LoadCapabilitiesAsync();
            ShowStatus("已恢复 GearDeck 同步控制并重新读取设备参数。", InfoBarSeverity.Success);
        };

        TabPerfBtn.Click += (_, _) => SelectTab("perf");
        TabButtonsBtn.Click += (_, _) => SelectTab("buttons");
        TabAdvancedBtn.Click += (_, _) => SelectTab("advanced");

        SyncXySwitch.Toggled += OnSyncXyToggled;
        AddStageBtn.Click += OnAddStage;
        RemoveStageBtn.Click += OnRemoveStage;
        DpiXSlider.ValueChanged += OnDpiXSliderChanged;
        DpiXSlider.PointerCaptureLost += (_, _) => FlushDebouncedUpdate();
        DpiXBox.ValueChanged += OnDpiXBoxChanged;
        DpiXBox.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) App.UiQueue?.TryEnqueue(FlushDebouncedUpdate); };
        DpiXBox.LostFocus += (_, _) => FlushDebouncedUpdate();
        DpiYSlider.ValueChanged += OnDpiYSliderChanged;
        DpiYSlider.PointerCaptureLost += (_, _) => FlushDebouncedUpdate();
        DpiYBox.ValueChanged += OnDpiYBoxChanged;
        DpiYBox.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) App.UiQueue?.TryEnqueue(FlushDebouncedUpdate); };
        DpiYBox.LostFocus += (_, _) => FlushDebouncedUpdate();

        MotionSyncSwitch.Toggled += OnMotionSyncToggled;
        AngleSnapSwitch.Toggled += OnAngleSnapToggled;
        RippleSwitch.Toggled += OnRippleToggled;
        LongRangeSwitch.Toggled += OnLongRangeToggled;

        AnalogLinkBothBtn.Click += (_, _) => SetAnalogMode(independent: false);
        AnalogIndepBtn.Click += (_, _) => SetAnalogMode(independent: true);
        AnalogSelectLeftBtn.Click += (_, _) => SelectAnalogSide(editRight: false);
        AnalogSelectRightBtn.Click += (_, _) => SelectAnalogSide(editRight: true);
        AnalogActSlider.ValueChanged += OnAnalogActSliderChanged;
        AnalogActSlider.PointerCaptureLost += (_, _) => FlushDebouncedUpdate();
        AnalogRtOffBtn.Click += (_, _) => SetAnalogRtEnabled(false);
        AnalogRtOnBtn.Click += (_, _) => SetAnalogRtEnabled(true);
        AnalogHapOffBtn.Click += (_, _) => SetAnalogHapticsEnabled(false);
        AnalogHapOnBtn.Click += (_, _) => SetAnalogHapticsEnabled(true);
        AnalogHapSlider.ValueChanged += OnAnalogHapSliderChanged;
        AnalogHapSlider.PointerCaptureLost += (_, _) => FlushDebouncedUpdate();
        ApplyAnalogBtn.Click += (_, _) => CommitAnalogSwitchUpdate(immediate: true);

        QuickEnrollMacroBtn.Click += OnQuickEnrollMacro;
        OpenMacroHubBtn.Click += OnOpenMacroHub;

        LodSlider.ValueChanged += OnLodSliderChanged;
        LodSlider.PointerCaptureLost += (_, _) => FlushDebouncedUpdate();
        LodSlider.ThumbToolTipValueConverter = new CallbackThumbToolTipConverter(FormatLodTooltipValue);
        AsymLodSwitch.Toggled += OnApplyAsymLod;
        AsymLiftSlider.ValueChanged += OnAsymSliderChanged;
        AsymLandSlider.ValueChanged += OnAsymSliderChanged;
        AsymLiftSlider.ThumbToolTipValueConverter = new CallbackThumbToolTipConverter(v => $"{v:0.0} mm");
        AsymLandSlider.ThumbToolTipValueConverter = new CallbackThumbToolTipConverter(v => $"{v:0.0} mm");
        ApplyAsymLodBtn.Click += OnApplyAsymLod;

        AnalogPressTestPad.RightTapped += (_, e) => e.Handled = true;
        AnalogPressTestPad.PointerPressed += (_, _) => UpdateAnalogLiveSample();
        AnalogPressTestPad.PointerReleased += (_, _) => UpdateAnalogLiveSample();
        DebounceSlider.ValueChanged += OnDebounceSliderChanged;
        DebounceSlider.PointerCaptureLost += (_, _) => FlushDebouncedUpdate();
        AngleSlider.ValueChanged += OnAngleSliderChanged;
        AngleSlider.PointerCaptureLost += (_, _) => FlushDebouncedUpdate();
        SleepSlider.ValueChanged += OnSleepSliderChanged;
        LowBatSlider.ValueChanged += OnLowBatSliderChanged;
        ApplyPowerBtn.Click += OnApplyPower;

        HostModeSwitch.Toggled += OnHostModeToggled;
        ProbeAllSlotsBtn.Click += OnProbeAllOnboardSlots;
        SaveDevicePresetBtn.Click += OnSaveAsGamePreset;
    }

    private void InitializeDebounceTimer()
    {
        var dq = DispatcherQueue.GetForCurrentThread();
        if (dq == null) return;
        _debounceTimer = dq.CreateTimer();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(180);
        _debounceTimer.IsRepeating = false;
        _debounceTimer.Tick += (_, _) => FlushDebouncedUpdate();
    }

    private void ScheduleDebouncedUpdate(DeviceTuningUpdate update, int delayMs = 180)
    {
        _debouncedPending = _debouncedPending == null ? update : MergeUpdates(_debouncedPending, update);
        if (_debounceTimer == null)
        {
            _ = ApplyUpdateAsync(update);
            return;
        }
        _debounceTimer.Stop();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 80, 600));
        _debounceTimer.Start();
    }

    private void FlushDebouncedUpdate()
    {
        _debounceTimer?.Stop();
        var pending = _debouncedPending;
        _debouncedPending = null;
        if (pending != null)
            _ = ApplyUpdateAsync(pending);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            _deviceId = e.Parameter as string;
            _caps = null;
            _autoWakeAttempts = 0;
            _nextAutoWakeTick = Environment.TickCount64 + AutoWakeBackoffMs[0];
            _analogModeInitialized = false;
            _analogEditingRight = false;
            _leftTravel = _rightTravel = 0;
            _leftTriggered = _rightTriggered = false;
            SelectTab(_activeTab);
            ShowOverview();
            BindDeviceLowBatteryPolicy();
            RefreshDeviceSavedPresets();
            BindQuickMacroControls();
            _ = LoadCapabilitiesAsync();
        }
        catch (Exception ex)
        {
            Diag.Log($"DeviceDetailPage.OnNavigatedTo 异常: {ex.Message}");
            ShowStatus($"界面初始化异常: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Devices.DevicesUpdated += OnUpdated;
        DeviceImageService.ImagesUpdated += OnImagesUpdated;
        ExternalConfiguratorGuard.StateChanged += OnGuardStateChanged;
        UpdateCoexistenceBanner();
        UpdateAnalogPressTimerState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Devices.DevicesUpdated -= OnUpdated;
        DeviceImageService.ImagesUpdated -= OnImagesUpdated;
        ExternalConfiguratorGuard.StateChanged -= OnGuardStateChanged;
        _debounceTimer?.Stop();
        _analogPressTimer?.Stop();
    }

    private void OnUpdated(IReadOnlyList<PeripheralDevice> devices) => App.UiQueue?.TryEnqueue(ShowOverview);
    private void OnImagesUpdated() => App.UiQueue?.TryEnqueue(ShowOverview);
    private void OnGuardStateChanged() => App.UiQueue?.TryEnqueue(UpdateCoexistenceBanner);

    private void SelectTab(string tabKey)
    {
        _activeTab = tabKey is "buttons" or "advanced" ? tabKey : "perf";

        PanelPerf.Visibility = _activeTab == "perf" ? Visibility.Visible : Visibility.Collapsed;
        PanelButtons.Visibility = _activeTab == "buttons" ? Visibility.Visible : Visibility.Collapsed;
        PanelAdvanced.Visibility = _activeTab == "advanced" ? Visibility.Visible : Visibility.Collapsed;

        var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
        var normal = (Style)Application.Current.Resources["DefaultButtonStyle"];

        TabPerfBtn.Style = _activeTab == "perf" ? accent : normal;
        TabButtonsBtn.Style = _activeTab == "buttons" ? accent : normal;
        TabAdvancedBtn.Style = _activeTab == "advanced" ? accent : normal;

        UpdateAnalogPressTimerState();

        if (_caps is { IsOnline: false } && FallbackCard.Visibility == Visibility.Visible)
        {
            // FallbackCard 常驻于顶部，避免与 StatusInfoBar 重复显示同一段休眠文案
            StatusInfoBar.IsOpen = false;
        }
    }

    private void UpdateCoexistenceBanner()
    {
        bool active = ExternalConfiguratorGuard.IsBackgroundYieldActive(out var reason);
        CoexistenceBanner.IsOpen = active;
        if (active && !string.IsNullOrWhiteSpace(reason))
            CoexistenceText.Text = reason;
    }

    private void ShowOverview()
    {
        try
        {
            var d = App.LastDevices.FirstOrDefault(x => x.Id == _deviceId);
            if (d == null)
            {
                TitleText.Text = "设备未找到";
                SubtitleText.Text = "可能已断开；回到总览刷新后再试";
                BatteryText.Text = "--";
                OverviewBatteryText.Text = "--";
                OverviewPerfText.Text = "--";
                OverviewProtocolText.Text = "--";
                LocalDriverBtn.Visibility = Visibility.Collapsed;
                WebDriverBtn.Visibility = Visibility.Collapsed;
                AvatarHost.Children.Clear();
                return;
            }

            TitleText.Text = d.Name;
            SubtitleText.Text = $"设备 > {d.Provider} · {d.Transport} · {d.Kind}";

            string? dualEar = d.FormatDualEarSummary(compact: false);
            if (!string.IsNullOrWhiteSpace(dualEar))
            {
                BatteryText.Text = dualEar;
                OverviewBatteryText.Text = dualEar;
            }
            else
            {
                string bat = d.BatteryPercent is { } p ? $"{p}%" : "--";
                BatteryText.Text = bat;
                OverviewBatteryText.Text = d.State == BatteryState.Charging ? $"{bat} (充电中)" : bat;
            }

            bool isSleepingState = d.IsSleeping || (_caps is { IsOnline: false } && _caps.DeviceId == d.Id);
            StateText.Text = d.State switch
            {
                BatteryState.Charging => "● 充电中 ⚡",
                BatteryState.FullyCharged => "● 已充满",
                _ => isSleepingState ? "○ 无线休眠中" : (d.Connected ? "● 已连接" : "○ 离线"),
            };
            NoteText.Text = d.Note ?? "";

            var links = VendorDriverCatalog.Resolve(d);
            _localDriver = links.Local;
            _webDriver = links.Web;

            LocalDriverBtn.Visibility = _localDriver != null ? Visibility.Visible : Visibility.Collapsed;
            if (_localDriver != null)
                LocalDriverBtnText.Text = _localDriver.DisplayName;

            WebDriverBtn.Visibility = _webDriver != null ? Visibility.Visible : Visibility.Collapsed;
            if (_webDriver != null)
                WebDriverBtnText.Text = _webDriver.DisplayName;

            AvatarHost.Children.Clear();
            AvatarHost.Children.Add(UiBuilders.CreateHeroDeviceVisual(d, 168));

            UpdateOverviewKpis(d, _caps);
            UpdateCoexistenceBanner();
        }
        catch (Exception ex)
        {
            Diag.Log($"ShowOverview 异常: {ex.Message}");
        }
    }

    private void UpdateOverviewKpis(PeripheralDevice d, DeviceTuningCapabilities? caps)
    {
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

            string vidPid = (d.VendorId, d.ProductId) is ({ } v, { } p) ? $"{v:X4}:{p:X4}" : d.Provider;
            OverviewProtocolText.Text = caps.SupportsAnalogSwitchTuning
                ? $"{vidPid} · {caps.AnalogSwitchTechName}"
                : $"{vidPid} · {d.Kind}";
        }
        else
        {
            OverviewPerfText.Text = d.IsSleeping ? "待唤醒读取" : d.Transport;
            OverviewProtocolText.Text = (d.VendorId, d.ProductId) is ({ } v, { } p)
                ? $"{v:X4}:{p:X4} · {d.Provider}"
                : d.Provider;
        }
    }

    private async void OnPickDeviceImage(object sender, RoutedEventArgs e)
    {
        var d = App.LastDevices.FirstOrDefault(x => x.Id == _deviceId);
        if (d == null) return;
        if (await DeviceImagePickerDialog.ShowAsync(this.XamlRoot, d))
            ShowOverview();
    }

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
            DeviceLowBatStateText.Text = policy.CustomThresholdPercent.HasValue
                ? $"独立 {eff}%"
                : $"默认 {SettingsService.DefaultLowBatteryThreshold}%";
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
        int eff = App.Settings.GetEffectiveLowBatteryThreshold(_deviceId);
        DeviceLowBatStateText.Text = customThreshold.HasValue
            ? $"独立 {eff}%"
            : $"默认 {SettingsService.DefaultLowBatteryThreshold}%";
    }

    private void OnDeviceLowBatCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_binding || string.IsNullOrWhiteSpace(_deviceId)) return;
        SaveDeviceLowBatteryPolicy(App.Settings.GetLowBatteryPolicy(_deviceId).CustomThresholdPercent);
    }

    private void OnDeviceLowBatThresholdChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_binding || string.IsNullOrWhiteSpace(_deviceId) || double.IsNaN(args.NewValue)) return;
        SaveDeviceLowBatteryPolicy(Math.Clamp((int)Math.Round(args.NewValue), 5, 80));
    }

    private void OnDeviceLowBatReset(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_deviceId)) return;
        App.Settings.Current.DeviceLowBatteryPolicies.Remove(_deviceId);
        App.Settings.Save();
        App.RefreshWidget();
        BindDeviceLowBatteryPolicy();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ReadCapsBtn.IsEnabled = !busy;
        ProbeAllSlotsBtn.IsEnabled = !busy && _caps is { IsOnline: true, OnboardProfileCount: > 1 };
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = !string.IsNullOrWhiteSpace(message);
    }

    private async Task LoadCapabilitiesAsync()
    {
        if (string.IsNullOrWhiteSpace(_deviceId) || _busy) return;
        SetBusy(true);
        try
        {
            var caps = await App.Devices.QueryCapabilitiesAsync(_deviceId);
            BindCapabilities(caps);
        }
        catch (Exception ex)
        {
            Diag.Log($"LoadCapabilitiesAsync 异常: {ex.Message}");
            ShowStatus($"读取硬件参数失败: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BindCapabilities(DeviceTuningCapabilities caps)
    {
        _caps = caps;
        _binding = true;
        try
        {
            var currentDev = App.LastDevices.FirstOrDefault(x => x.Id == _deviceId);
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
            bool hasAnyTunable = caps.SupportsDpi || caps.SupportedPollingRates.Count > 0 || caps.SupportedLodOptions.Count > 0 || caps.SupportsAnalogSwitchTuning;
            bool isConfigurableKind = currentDev?.Kind is DeviceKind.Mouse or DeviceKind.Controller;

            TopProfileBarCard.Visibility = (isConfigurableKind || caps.SupportsHostModeSwitch || (caps.IsOnline && (hasOnboardProfiles || hasAnyTunable)))
                ? Visibility.Visible
                : Visibility.Collapsed;
            OnboardProfileSection.Visibility = hasOnboardProfiles ? Visibility.Visible : Visibility.Collapsed;
            ProbeAllSlotsBtn.Visibility = (caps.IsOnline && caps.OnboardProfileCount > 1)
                ? Visibility.Visible
                : Visibility.Collapsed;
            ProbeAllSlotsBtn.IsEnabled = !_busy && caps.IsOnline && caps.OnboardProfileCount > 1;
            SaveDevicePresetBtn.IsEnabled = caps.IsOnline && (hasAnyTunable || caps.OnboardProfileCount > 0);

            if (hasOnboardProfiles)
                BuildOnboardProfileButtons(caps, cachedSlots);
            RefreshDeviceSavedPresets();

            HostModeCard.Visibility = caps.SupportsHostModeSwitch ? Visibility.Visible : Visibility.Collapsed;
            if (caps.SupportsHostModeSwitch)
                HostModeSwitch.IsOn = caps.IsHostMode;

            DpiCard.Visibility = caps.SupportsDpi ? Visibility.Visible : Visibility.Collapsed;
            if (caps.SupportsDpi)
            {
                int minDpi = Math.Max(10, caps.MinDpi);
                int maxDpi = Math.Max(minDpi + 50, caps.MaxDpi);
                int step = Math.Max(1, caps.DpiStep);

                SyncXySwitch.Visibility = caps.SupportsIndependentXY ? Visibility.Visible : Visibility.Collapsed;
                bool independent = caps.SupportsIndependentXY && caps.CurrentDpiX != caps.CurrentDpiY;
                SyncXySwitch.IsOn = !independent;
                DpiYSection.Visibility = independent ? Visibility.Visible : Visibility.Collapsed;
                DpiXLabel.Text = independent ? "X 轴:" : "DPI:";

                ConfigureDpiRange(DpiXSlider, DpiXBox, minDpi, maxDpi, step, caps.CurrentDpiX);
                ConfigureDpiRange(DpiYSlider, DpiYBox, minDpi, maxDpi, step, caps.CurrentDpiY);

                DpiRangeHint.Text = $"硬件范围 {minDpi} - {maxDpi}（步进 {step}）";
                UpdateDpiSummaryText();
                BuildDpiPresets(minDpi, maxDpi);
                BuildDpiStages();
            }

            RateCard.Visibility = caps.SupportedPollingRates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (caps.SupportedPollingRates.Count > 0)
                BuildPollingRateGrid(caps.SupportedPollingRates, caps.CurrentPollingRate);

            bool hasPerfModes = caps.SupportedPerformanceModes.Count > 0;
            bool hasSwitches = caps.SupportsMotionSync || caps.SupportsAngleSnapping || caps.SupportsRippleControl || caps.SupportsLongRangeMode;
            ModeCard.Visibility = (hasPerfModes || hasSwitches) ? Visibility.Visible : Visibility.Collapsed;
            PerfModeSection.Visibility = hasPerfModes ? Visibility.Visible : Visibility.Collapsed;
            if (hasPerfModes)
                BuildPerformanceModeButtons(caps);

            MotionSyncSwitch.Visibility = caps.SupportsMotionSync ? Visibility.Visible : Visibility.Collapsed;
            if (caps.SupportsMotionSync) MotionSyncSwitch.IsOn = caps.MotionSync;
            AngleSnapSwitch.Visibility = caps.SupportsAngleSnapping ? Visibility.Visible : Visibility.Collapsed;
            if (caps.SupportsAngleSnapping) AngleSnapSwitch.IsOn = caps.AngleSnapping;
            RippleSwitch.Visibility = caps.SupportsRippleControl ? Visibility.Visible : Visibility.Collapsed;
            if (caps.SupportsRippleControl) RippleSwitch.IsOn = caps.RippleControl;
            LongRangeSwitch.Visibility = caps.SupportsLongRangeMode ? Visibility.Visible : Visibility.Collapsed;
            if (caps.SupportsLongRangeMode) LongRangeSwitch.IsOn = caps.LongRangeMode;

            bool hasLod = caps.SupportedLodOptions.Count > 0;
            bool hasAsym = caps.SupportsAsymmetricLod;
            bool hasDebounce = caps.SupportsDebounce;
            bool hasAngle = caps.SupportsAngleTuning;
            bool hasDiscreteSleep = caps.SupportsSleepTimeout && caps.SupportedSleepSeconds.Count > 0;
            bool hasSliderSleep = caps.SupportsSleepTimeout && caps.SupportedSleepSeconds.Count == 0;
            bool hasLowBat = caps.SupportsLowBatteryWarning;
            AdvancedCard.Visibility = (hasLod || hasAsym || hasDebounce || hasAngle || hasDiscreteSleep || hasSliderSleep || hasLowBat)
                ? Visibility.Visible
                : Visibility.Collapsed;

            LodSection.Visibility = hasLod ? Visibility.Visible : Visibility.Collapsed;
            if (hasLod)
                BuildLodButtons(caps.SupportedLodOptions, caps.CurrentLod);

            AsymLodSection.Visibility = hasAsym ? Visibility.Visible : Visibility.Collapsed;
            if (hasAsym)
            {
                AsymLodSwitch.IsOn = caps.AsymmetricLodEnabled;
                int lift = Math.Clamp(caps.AsymmetricLiftOff, 2, 26);
                int land = Math.Clamp(caps.AsymmetricLanding, 1, lift - 1);
                SafeSetSlider(AsymLiftSlider, 0.2, 2.6, 0.1, lift * 0.1);
                SafeSetSlider(AsymLandSlider, 0.1, 2.5, 0.1, land * 0.1);
                AsymLiftText.Text = $"{lift * 0.1:0.0} mm";
                AsymLandText.Text = $"{land * 0.1:0.0} mm";
            }

            DebounceSection.Visibility = hasDebounce ? Visibility.Visible : Visibility.Collapsed;
            if (hasDebounce)
            {
                int maxDb = Math.Max(1, caps.MaxDebounceMs);
                int curDb = Math.Clamp(caps.CurrentDebounceMs, 0, maxDb);
                SafeSetSlider(DebounceSlider, 0, maxDb, 1, curDb);
                DebounceValueText.Text = $"{curDb} ms";
            }

            AngleTuningSection.Visibility = hasAngle ? Visibility.Visible : Visibility.Collapsed;
            if (hasAngle)
            {
                int curAng = Math.Clamp(caps.AngleTuningDeg, -30, 30);
                SafeSetSlider(AngleSlider, -30, 30, 1, curAng);
                AngleValueText.Text = $"{curAng}°";
            }

            DiscreteSleepSection.Visibility = hasDiscreteSleep ? Visibility.Visible : Visibility.Collapsed;
            if (hasDiscreteSleep)
                BuildDiscreteSleepButtons(caps.SupportedSleepSeconds, caps.CurrentSleepSeconds);

            SleepSection.Visibility = hasSliderSleep ? Visibility.Visible : Visibility.Collapsed;
            if (hasSliderSleep)
            {
                int minMin = Math.Max(1, caps.MinSleepSeconds / 60);
                int maxMin = Math.Max(minMin + 1, caps.MaxSleepSeconds / 60);
                int curMin = Math.Clamp((int)Math.Round(caps.CurrentSleepSeconds / 60.0), minMin, maxMin);
                SafeSetSlider(SleepSlider, minMin, maxMin, 1, curMin);
                SleepValueText.Text = $"{curMin} 分钟后休眠";
            }

            LowBatWarnSection.Visibility = hasLowBat ? Visibility.Visible : Visibility.Collapsed;
            if (hasLowBat)
            {
                int curWarn = Math.Clamp(caps.CurrentLowBatteryWarningPercent, 5, 50);
                SafeSetSlider(LowBatSlider, 5, 50, 1, curWarn);
                LowBatValueText.Text = $"低于 {curWarn}% 提醒";
            }

            ApplyPowerBtn.Visibility = (hasSliderSleep || hasLowBat) ? Visibility.Visible : Visibility.Collapsed;

            BindAnalogSwitches(caps);

            bool showMacroCard = caps.ButtonBindings.Count > 0 || caps.SupportsOnboardMacroUpload || currentDev?.Kind == DeviceKind.Mouse;
            MacroAndButtonsCard.Visibility = showMacroCard ? Visibility.Visible : Visibility.Collapsed;
            if (showMacroCard)
                BindButtonMappingsAndQuickMacros(caps);

            bool anyControl = caps.SupportsHostModeSwitch || caps.SupportsDpi || caps.SupportedPollingRates.Count > 0
                              || hasLod || hasAsym || hasDebounce || hasAngle || caps.SupportsSleepTimeout || hasLowBat
                              || hasPerfModes || hasSwitches || caps.ButtonBindings.Count > 0 || caps.SupportsAnalogSwitchTuning;
            FallbackCard.Visibility = anyControl ? Visibility.Collapsed : Visibility.Visible;
            if (!anyControl)
            {
                FallbackTitle.Text = caps.IsOnline ? "硬件参数说明" : "设备休眠或未唤醒";
                FallbackMessage.Text = caps.StatusMessage ?? "当前设备暂无可调节的公开 HID 参数。";
            }

            if (caps.IsOnline)
            {
                _autoWakeAttempts = 0;
                if (caps.SupportsHostModeSwitch && !caps.IsHostMode)
                    ShowStatus(caps.StatusMessage ?? "当前处于板载内存模式。", InfoBarSeverity.Informational);
                else if (!string.IsNullOrWhiteSpace(caps.StatusMessage))
                    ShowStatus(caps.StatusMessage, InfoBarSeverity.Informational);
                else
                    StatusInfoBar.IsOpen = false;
            }
            else if (!anyControl && FallbackCard.Visibility == Visibility.Visible)
            {
                // FallbackCard 常驻于顶部，避免与 StatusInfoBar 重复显示同一段文字
                StatusInfoBar.IsOpen = false;
            }
            else
            {
                ShowStatus(caps.StatusMessage ?? "设备当前未唤醒。", InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"BindCapabilities 异常: {ex.Message}");
            ShowStatus($"参数界面渲染异常: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _binding = false;
        }
    }

    // ── HITS / AULA SSHS 2.0 触发点 & Rapid Trigger 调校 ─────────────────────────
    private void BindAnalogSwitches(DeviceTuningCapabilities caps)
    {
        if (!caps.SupportsAnalogSwitchTuning)
        {
            AnalogSwitchCard.Visibility = Visibility.Collapsed;
            UpdateAnalogPressTimerState();
            return;
        }

        AnalogSwitchCard.Visibility = Visibility.Visible;
        AnalogTechTitle.Text = caps.AnalogSwitchTechName;

        _leftAnalog = caps.LeftAnalogButton ?? new AnalogButtonConfig(0, 4, true, 3, true, Math.Min(4, caps.AnalogMaxHaptics));
        _rightAnalog = caps.RightAnalogButton ?? new AnalogButtonConfig(1, _leftAnalog.Actuation, _leftAnalog.RapidTriggerEnabled, _leftAnalog.RapidTrigger, _leftAnalog.HapticsEnabled, _leftAnalog.Haptics);

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
            var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
            var normal = (Style)Application.Current.Resources["DefaultButtonStyle"];

            AnalogLinkBothBtn.Style = !_analogIndependent ? accent : normal;
            AnalogIndepBtn.Style = _analogIndependent ? accent : normal;
            AnalogModeDescText.Text = _analogIndependent
                ? "独立调节：分别定制左主按键与右主按键的触发点、RT 与反馈强度"
                : "两个主按键：将相同数值实时同步应用到左右两个按键";

            AnalogSideSelectorPanel.Visibility = _analogIndependent ? Visibility.Visible : Visibility.Collapsed;
            AnalogSelectLeftBtn.Style = !_analogEditingRight ? accent : normal;
            AnalogSelectRightBtn.Style = _analogEditingRight ? accent : normal;

            ApplyAnalogBtn.Content = _analogIndependent
                ? (_analogEditingRight ? "应用右主按键参数" : "应用左主按键参数")
                : "应用到两个按键";

            var active = (_analogIndependent && _analogEditingRight) ? _rightAnalog : _leftAnalog;

            BuildAnalogVisualBars(caps.AnalogMaxActuation);

            // 触发点 1..maxActuation
            int maxAct = Math.Clamp(caps.AnalogMaxActuation, 5, 20);
            AnalogActuationPanel.Children.Clear();
            for (int lvl = 1; lvl <= maxAct; lvl++)
            {
                int v = lvl;
                var pill = new Button { Content = v.ToString(), MinWidth = 42, Padding = new Thickness(8, 6, 8, 6) };
                if (active.Actuation == v) pill.Style = accent;
                pill.Click += (_, _) => SetAnalogActuation(v, immediate: true);
                AnalogActuationPanel.Children.Add(pill);
            }
            SafeSetSlider(AnalogActSlider, 1, maxAct, 1, active.Actuation);
            AnalogActValueText.Text = $"当前触发点 {active.Actuation}/{maxAct}（1 次短按 — {maxAct} 次长按）";

            // Rapid Trigger 关 / 开 + 1..maxRt
            int maxRt = Math.Clamp(caps.AnalogMaxRapidTrigger, 3, 10);
            AnalogRtOffBtn.Style = !active.RapidTriggerEnabled ? accent : normal;
            AnalogRtOnBtn.Style = active.RapidTriggerEnabled ? accent : normal;
            AnalogRtPanel.Children.Clear();
            for (int rt = 1; rt <= maxRt; rt++)
            {
                int v = rt;
                var pill = new Button
                {
                    Content = v.ToString(),
                    MinWidth = 52,
                    Padding = new Thickness(10, 6, 10, 6),
                    IsEnabled = active.RapidTriggerEnabled,
                };
                if (active.RapidTriggerEnabled && active.RapidTrigger == v) pill.Style = accent;
                pill.Click += (_, _) => SetAnalogRtSensitivity(v);
                AnalogRtPanel.Children.Add(pill);
            }

            // 点击触感 / 反馈
            if (caps.SupportsAnalogHapticsSwitch)
            {
                AnalogHapDiscretePanel.Visibility = Visibility.Collapsed;
                AnalogHapSliderPanel.Visibility = Visibility.Visible;
                AnalogHapOffBtn.Style = !active.HapticsEnabled ? accent : normal;
                AnalogHapOnBtn.Style = active.HapticsEnabled ? accent : normal;
                int maxHap = Math.Max(1, caps.AnalogMaxHaptics);
                SafeSetSlider(AnalogHapSlider, 0, maxHap, maxHap >= 50 ? 5 : 1, active.Haptics);
                AnalogHapSlider.IsEnabled = active.HapticsEnabled;
                AnalogHapValueText.Text = active.HapticsEnabled ? $"{active.Haptics}%" : "关闭";
            }
            else
            {
                AnalogHapDiscretePanel.Visibility = Visibility.Visible;
                AnalogHapSliderPanel.Visibility = Visibility.Collapsed;
                AnalogHapDiscretePanel.Children.Clear();
                int maxHap = Math.Clamp(caps.AnalogMaxHaptics, 1, 10);
                for (int h = 0; h <= maxHap; h++)
                {
                    int v = h;
                    var pill = new Button
                    {
                        Content = v == 0 ? "0 关" : v.ToString(),
                        MinWidth = 52,
                        Padding = new Thickness(10, 6, 10, 6),
                    };
                    if (active.Haptics == v) pill.Style = accent;
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
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var segGrid = new Grid { ColumnSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var segments = new Border[totalSteps];
        for (int i = 0; i < totalSteps; i++)
        {
            segGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var box = new Border
            {
                Height = 8,
                CornerRadius = new CornerRadius(3),
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
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
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
        bool shouldRun = IsLoaded && _activeTab == "buttons" && AnalogSwitchCard.Visibility == Visibility.Visible;
        if (!shouldRun)
        {
            _analogPressTimer?.Stop();
            return;
        }

        if (_analogPressTimer == null)
        {
            var dq = DispatcherQueue.GetForCurrentThread();
            if (dq == null) return;
            _analogPressTimer = dq.CreateTimer();
            _analogPressTimer.Interval = TimeSpan.FromMilliseconds(25);
            _analogPressTimer.IsRepeating = true;
            _analogPressTimer.Tick += (_, _) => UpdateAnalogLiveSample();
        }

        if (!_analogPressTimer.IsRunning)
            _analogPressTimer.Start();
    }

    private void UpdateAnalogLiveSample()
    {
        if (AnalogSwitchCard.Visibility != Visibility.Visible || _leftBarSegments.Length == 0)
            return;

        bool swapped = GetSystemMetrics(23) != 0;
        int vkLeft = swapped ? 0x02 : 0x01;
        int vkRight = swapped ? 0x01 : 0x02;
        bool leftPressed = (GetAsyncKeyState(vkLeft) & 0x8000) != 0;
        bool rightPressed = (GetAsyncKeyState(vkRight) & 0x8000) != 0;

        StepAnalogSide(leftPressed, _leftAnalog, _leftBarSegments.Length, ref _leftTravel, ref _leftRtExtreme, ref _leftTriggered);
        StepAnalogSide(rightPressed, _rightAnalog, _rightBarSegments.Length, ref _rightTravel, ref _rightRtExtreme, ref _rightTriggered);

        RenderLiveAnalogBars();
    }

    private static void StepAnalogSide(bool pressed, AnalogButtonConfig cfg, int totalSteps, ref double travel, ref double extreme, ref bool triggered)
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

        if (_leftTravel <= 0.05 && _rightTravel <= 0.05)
        {
            AnalogLiveStateText.Text = "待按压测试";
        }
        else
        {
            int lSteps = Math.Max(1, _leftBarSegments.Length);
            int rSteps = Math.Max(1, _rightBarSegments.Length);
            int pctL = (int)Math.Round(_leftTravel * 100.0 / lSteps);
            int pctR = (int)Math.Round(_rightTravel * 100.0 / rSteps);
            AnalogLiveStateText.Text = $"L {pctL}% {(_leftTriggered ? "⚡触发" : "○")}  |  R {pctR}% {(_rightTriggered ? "⚡触发" : "○")}";
        }
    }

    private static void RenderSingleLiveBar(Border[] segments, TextBlock? summary, AnalogButtonConfig cfg, double travel, bool triggered)
    {
        if (segments.Length == 0) return;

        int totalSteps = segments.Length;
        int actIdx = Math.Clamp(cfg.Actuation, 1, totalSteps) - 1;
        int filledCount = travel > 0.05 ? Math.Clamp((int)Math.Ceiling(travel - 0.05), 1, totalSteps) : 0;

        var accentBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var idleBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(44, 128, 128, 128));
        var traceBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(72, 16, 185, 129));
        var pressingBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 56, 189, 248));
        var activeTriggeredBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 16, 185, 129));
        var actTriggeredMarkerBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 245, 158, 11));

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
            if (travel > 0.05)
            {
                int pct = (int)Math.Round(travel * 100.0 / totalSteps);
                summary.Text = $"力度 {pct}% {(triggered ? "● 已触发" : "○ 按压中")}";
            }
            else
            {
                summary.Text = FormatConfigSummary(cfg);
            }
        }
    }

    private void UpdateActiveAnalogConfig(Func<AnalogButtonConfig, AnalogButtonConfig> mutator)
    {
        if (_analogIndependent && _analogEditingRight)
        {
            _rightAnalog = mutator(_rightAnalog) with { ButtonIndex = 1 };
        }
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

    private void SetAnalogActuation(int lvl, bool immediate)
    {
        UpdateActiveAnalogConfig(c => c with { Actuation = lvl });
        RenderAnalogSwitchControls();
        CommitAnalogSwitchUpdate(immediate);
    }

    private void OnAnalogActSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
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

    private void OnAnalogHapSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding) return;
        int val = Math.Clamp((int)Math.Round(e.NewValue), 0, _caps?.AnalogMaxHaptics ?? 100);
        UpdateActiveAnalogConfig(c => c with { HapticsEnabled = true, Haptics = val });
        RenderAnalogSwitchControls();
        CommitAnalogSwitchUpdate(immediate: false);
    }

    private void CommitAnalogSwitchUpdate(bool immediate)
    {
        var update = new DeviceTuningUpdate
        {
            AnalogSwitches = new AnalogSwitchUpdate(_analogIndependent, _leftAnalog, _rightAnalog),
        };
        if (immediate)
        {
            _debounceTimer?.Stop();
            _debouncedPending = null;
            _ = ApplyUpdateAsync(update);
        }
        else
        {
            ScheduleDebouncedUpdate(update);
        }
    }

    // ── 板载配置槽位 & 预设 ─────────────────────────────────────────────────────
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
                Content = slot == activeSlot ? $"{baseName} ✓" : baseName,
                FontSize = 12,
                Padding = new Thickness(10, 5, 10, 5),
                IsEnabled = caps.IsOnline,
            };
            string tip = !string.IsNullOrWhiteSpace(cached?.Summary)
                ? $"{baseName}: {cached.Summary}"
                : (caps.IsOnline ? $"点击切换到 {baseName}" : "待唤醒后可切换");
            ToolTipService.SetToolTip(btn, tip);
            if (slot == activeSlot)
                btn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            btn.Click += (_, _) => _ = ApplyUpdateAsync(new DeviceTuningUpdate { ActiveOnboardProfile = slot });
            OnboardProfileButtonsPanel.Children.Add(btn);
        }
    }

    private async void OnProbeAllOnboardSlots(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_deviceId) || _busy) return;
        SetBusy(true);
        try
        {
            var manifest = await App.Settings.ProbeAllOnboardSlotsAsync(App.Devices, _deviceId);
            var caps = await App.Devices.QueryCapabilitiesAsync(_deviceId);
            BindCapabilities(caps);
            if (manifest != null)
                ShowStatus($"已读取 {manifest.Slots.Count} 个厂家板载配置并同步至游戏切换下拉菜单。", InfoBarSeverity.Success);
            else
                ShowStatus("设备未唤醒或不支持板载槽位读取。", InfoBarSeverity.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshDeviceSavedPresets()
    {
        if (DeviceSavedPresetsPanel == null || string.IsNullOrWhiteSpace(_deviceId)) return;
        DeviceSavedPresetsPanel.Children.Clear();

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
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
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
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 6, 4),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

            var applyBtn = new Button
            {
                Content = preset.Name,
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                Padding = new Thickness(9, 3, 9, 3),
                FontSize = 12,
                IsEnabled = _caps is { IsOnline: true },
            };
            ToolTipService.SetToolTip(applyBtn, parts.Count > 0 ? $"点击立即写入: {string.Join(" · ", parts)}" : "点击立即写入设备");
            applyBtn.Click += (_, _) => _ = ApplyUpdateAsync(preset.ToUpdate());
            row.Children.Add(applyBtn);

            var updateBtn = new Button
            {
                Content = "覆盖",
                Padding = new Thickness(7, 3, 7, 3),
                FontSize = 11,
                IsEnabled = _caps is { IsOnline: true },
            };
            ToolTipService.SetToolTip(updateBtn, "用当前设备参数覆盖此预设");
            updateBtn.Click += (_, _) =>
            {
                if (_caps is not { IsOnline: true }) return;
                var dev = App.LastDevices.FirstOrDefault(x => x.Id == _deviceId);
                var updated = MouseHardwarePreset.CaptureFrom(preset.Name, _deviceId, dev?.Name ?? _deviceId, _caps);
                updated.Id = preset.Id;
                int idx = App.Settings.Current.Presets.FindIndex(p => p.Id == preset.Id);
                if (idx >= 0) App.Settings.Current.Presets[idx] = updated;
                App.Settings.Save();
                RefreshDeviceSavedPresets();
                ShowStatus($"已使用当前硬件参数覆盖更新预设「{preset.Name}」。", InfoBarSeverity.Success);
            };
            row.Children.Add(updateBtn);

            var delBtn = new Button { Content = "×", Padding = new Thickness(6, 2, 6, 2), FontSize = 12 };
            ToolTipService.SetToolTip(delBtn, $"删除预设「{preset.Name}」");
            delBtn.Click += (_, _) =>
            {
                App.Settings.Current.Presets.RemoveAll(p => p.Id == preset.Id);
                App.Settings.Save();
                RefreshDeviceSavedPresets();
            };
            row.Children.Add(delBtn);

            chip.Child = row;
            DeviceSavedPresetsPanel.Children.Add(chip);
        }
    }

    private void OnSaveAsGamePreset(object sender, RoutedEventArgs e)
    {
        if (_caps == null || !_caps.IsOnline || string.IsNullOrWhiteSpace(_deviceId))
        {
            ShowStatus("请先唤醒设备并读取硬件参数后再保存预设。", InfoBarSeverity.Warning);
            return;
        }
        var dev = App.LastDevices.FirstOrDefault(x => x.Id == _deviceId);
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

    private void BuildPerformanceModeButtons(DeviceTuningCapabilities caps)
    {
        PerfModePanel.Children.Clear();
        PerfModeDescText.Text = "";
        foreach (var opt in caps.SupportedPerformanceModes)
        {
            int code = opt.Code;
            var btn = new Button { Content = opt.Label, Padding = new Thickness(14, 8, 14, 8) };
            if (code == caps.CurrentPerformanceMode)
            {
                btn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                PerfModeDescText.Text = opt.Description ?? "";
            }
            btn.Click += (_, _) => _ = ApplyUpdateAsync(new DeviceTuningUpdate { PerformanceMode = code });
            PerfModePanel.Children.Add(btn);
        }
    }

    private void BuildDiscreteSleepButtons(List<int> secondsList, int currentSeconds)
    {
        DiscreteSleepPanel.Children.Clear();
        foreach (int sec in secondsList)
        {
            int s = sec;
            string label = s == 0 ? "从不休眠" : s < 60 ? $"{s} 秒" : $"{s / 60} 分钟";
            var btn = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6) };
            if (s == currentSeconds)
                btn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            btn.Click += (_, _) => _ = ApplyUpdateAsync(new DeviceTuningUpdate { SleepSeconds = s });
            DiscreteSleepPanel.Children.Add(btn);
        }
    }

    private void BindButtonMappingsAndQuickMacros(DeviceTuningCapabilities caps)
    {
        if (!caps.IsOnline)
            MacroNoteText.Text = "设备当前处于无线休眠或待唤醒状态（按键板载映射需唤醒后读取；宏库编排在左侧「鼠标宏中心」永久可用）。";
        else if (!string.IsNullOrWhiteSpace(caps.MacroCapabilityNote))
            MacroNoteText.Text = caps.MacroCapabilityNote;
        else
            MacroNoteText.Text = "可在「鼠标宏中心」统一录制与编辑动作，再在下方快速绑定或写入本设备。";

        ButtonBindingsSection.Visibility = caps.ButtonBindings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ButtonBindingsPanel.Children.Clear();

        foreach (var slot in caps.ButtonBindings)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = slot.ButtonName,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
            });

            if (slot.IsReadOnly)
            {
                var ro = new TextBlock
                {
                    Text = $"{FormatBindingLabel(slot.CurrentAction)}（主键防误触锁定）",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                };
                Grid.SetColumn(ro, 1);
                row.Children.Add(ro);
            }
            else
            {
                var options = slot.AvailableActions.ToList();
                if (!options.Contains(slot.CurrentAction))
                    options.Insert(0, slot.CurrentAction);

                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var act in options)
                    combo.Items.Add(new ComboBoxItem { Content = FormatBindingLabel(act), Tag = act });
                combo.SelectedIndex = Math.Max(0, options.IndexOf(slot.CurrentAction));

                combo.SelectionChanged += (_, _) =>
                {
                    if (_binding) return;
                    if (combo.SelectedItem is ComboBoxItem item && item.Tag is string actionStr && actionStr != slot.CurrentAction)
                        _ = ApplyUpdateAsync(new DeviceTuningUpdate { ButtonBinding = (slot.SlotIndex, actionStr) });
                };
                Grid.SetColumn(combo, 1);
                row.Children.Add(combo);

                var kbBtn = new Button { Content = "设组合键..." };
                int slotIdx = slot.SlotIndex;
                string btnName = slot.ButtonName;
                kbBtn.Click += (_, _) => _ = ShowKeyboardShortcutDialogAsync(slotIdx, btnName);
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
            QuickMacroCombo.Items.Add(new ComboBoxItem { Content = $"{m.Name} ({m.Steps.Count} 步)", Tag = m.Id });
        if (QuickMacroCombo.Items.Count > 0) QuickMacroCombo.SelectedIndex = 0;

        QuickMacroBtnCombo.Items.Clear();
        foreach (var (key, label) in MacroExecutionService.SoftwareTriggerButtons)
            QuickMacroBtnCombo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        if (QuickMacroBtnCombo.Items.Count > 0) QuickMacroBtnCombo.SelectedIndex = 0;

        RefreshDeviceMacroBindingsSummary();
    }

    private void RefreshDeviceMacroBindingsSummary()
    {
        DeviceMacroBindingsSummary.Children.Clear();
        if (string.IsNullOrWhiteSpace(_deviceId)) return;

        var prefix = $"{_deviceId}|";
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
                Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            });
            var unbind = new Button { Content = "解绑", Padding = new Thickness(8, 2, 8, 2), FontSize = 11 };
            string k = kv.Key;
            unbind.Click += (_, _) =>
            {
                App.Settings.Current.LocalButtonMacroBindings.Remove(k);
                App.Settings.Save();
                App.MacroRunner?.SyncHookState();
                RefreshDeviceMacroBindingsSummary();
            };
            row.Children.Add(unbind);
            DeviceMacroBindingsSummary.Children.Add(row);
        }
    }

    private async void OnQuickEnrollMacro(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_deviceId)) return;
        if (QuickMacroCombo.SelectedItem is not ComboBoxItem mItem || mItem.Tag is not string macroId)
        {
            ShowStatus("请先前往「鼠标宏中心」创建一个宏。", InfoBarSeverity.Warning);
            return;
        }
        var macro = App.Settings.Current.Macros.FirstOrDefault(m => m.Id == macroId);
        if (macro == null || macro.Steps.Count == 0) return;

        string triggerKey = (QuickMacroBtnCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "XButton1";
        if (_caps is { IsOnline: true, SupportsOnboardMacroUpload: true })
        {
            int wireSlot = triggerKey switch { "Middle" => 3, "XButton2" => 5, _ => 4 };
            var res = await App.Devices.ApplySettingsAsync(_deviceId, new DeviceTuningUpdate
            {
                UploadMacro = (0, wireSlot, macro),
            });
            ShowStatus(res.Message, res.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            return;
        }

        App.Settings.Current.LocalButtonMacroBindings[$"{_deviceId}|{triggerKey}"] = macro.Id;
        App.Settings.Save();
        App.MacroRunner?.SyncHookState();
        RefreshDeviceMacroBindingsSummary();
        ShowStatus($"已将宏「{macro.Name}」绑定到本设备的 {triggerKey} 按键。", InfoBarSeverity.Success);
    }

    private void OnOpenMacroHub(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.OpenMacrosPage();

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

        var keyCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "目标按键（支持在下方框内直接按键盘捕获）" };
        foreach (var k in HidKeyCatalog.AllKeys.Where(k => k.Usage is >= 0x04 and < 0xF0))
            keyCombo.Items.Add(new ComboBoxItem { Content = k.Label, Tag = k.Usage });
        if (keyCombo.Items.Count > 0) keyCombo.SelectedIndex = 0;

        var captureBox = new TextBox { Header = "按键快速捕获区", PlaceholderText = "点击此处并按下目标按键..." };
        captureBox.PreviewKeyDown += (_, args) =>
        {
            args.Handled = true;
            byte? usage = HidKeyCatalog.UsageFromVirtualKey((int)args.Key);
            if (usage is { } u and < 0xE0)
            {
                for (int i = 0; i < keyCombo.Items.Count; i++)
                {
                    if (keyCombo.Items[i] is ComboBoxItem ci && ci.Tag is byte bu && bu == u)
                    {
                        keyCombo.SelectedIndex = i;
                        break;
                    }
                }
                captureBox.Text = $"已捕获: {HidKeyCatalog.DescribeKeyCode(u)}";
            }
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(modRow);
        panel.Children.Add(keyCombo);
        panel.Children.Add(captureBox);

        var dlg = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = $"为「{buttonName}」绑定硬件键盘快捷键",
            Content = panel,
            PrimaryButtonText = "写入鼠标硬件",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary &&
            keyCombo.SelectedItem is ComboBoxItem selected && selected.Tag is byte targetUsage)
        {
            byte mods = 0;
            if (ctrlChk.IsChecked == true) mods |= HidKeyCatalog.ModLeftCtrl;
            if (shiftChk.IsChecked == true) mods |= HidKeyCatalog.ModLeftShift;
            if (altChk.IsChecked == true) mods |= HidKeyCatalog.ModLeftAlt;
            if (winChk.IsChecked == true) mods |= HidKeyCatalog.ModLeftWin;

            string action = HidKeyCatalog.FormatKeyboardBinding(mods, targetUsage);
            await ApplyUpdateAsync(new DeviceTuningUpdate { ButtonBinding = (slotIndex, action) });
        }
    }

    private static void SafeSetSlider(Slider s, double min, double max, double step, double val)
    {
        if (min > max) (min, max) = (max, min);
        s.Minimum = Math.Min(s.Minimum, min);
        s.Maximum = Math.Max(s.Maximum, max);
        s.StepFrequency = Math.Max(0.05, step);
        s.Minimum = min;
        s.Maximum = max;
        s.Value = Math.Clamp(val, min, max);
    }

    private static void SafeSetNumberBox(NumberBox b, double min, double max, double step, double val)
    {
        if (min > max) (min, max) = (max, min);
        b.Minimum = Math.Min(b.Minimum, min);
        b.Maximum = Math.Max(b.Maximum, max);
        b.SmallChange = Math.Max(1, step);
        b.Minimum = min;
        b.Maximum = max;
        b.Value = Math.Clamp(val, min, max);
    }

    private static void ConfigureDpiRange(Slider slider, NumberBox box, int min, int max, int step, int value)
    {
        SafeSetSlider(slider, min, max, step, value);
        SafeSetNumberBox(box, min, max, step, value);
    }

    private void UpdateDpiSummaryText()
    {
        int x = double.IsNaN(DpiXBox.Value) ? (int)Math.Round(DpiXSlider.Value) : (int)Math.Round(DpiXBox.Value);
        int y = SyncXySwitch.IsOn ? x : (double.IsNaN(DpiYBox.Value) ? (int)Math.Round(DpiYSlider.Value) : (int)Math.Round(DpiYBox.Value));
        DpiSummaryText.Text = x == y ? $"当前生效：{x} DPI" : $"当前生效：X {x} / Y {y} DPI";
    }

    private void BuildDpiPresets(int min, int max)
    {
        DpiPresetsPanel.Children.Clear();
        int curX = (int)Math.Round(DpiXBox.Value);
        int curY = SyncXySwitch.IsOn ? curX : (int)Math.Round(DpiYBox.Value);
        foreach (int preset in CommonDpiPresets)
        {
            if (preset < min || preset > max) continue;
            var btn = new Button { Content = preset.ToString(), Padding = new Thickness(12, 6, 12, 6) };
            if (curX == preset && curY == preset)
                btn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            btn.Click += (_, _) => _ = ApplyDpiPresetAsync(preset);
            DpiPresetsPanel.Children.Add(btn);
        }
    }

    private void BuildDpiStages()
    {
        if (_caps == null || _caps.DpiStages.Count == 0)
        {
            DpiStagesSection.Visibility = Visibility.Collapsed;
            return;
        }
        DpiStagesSection.Visibility = Visibility.Visible;
        DpiStagesPanel.Children.Clear();

        for (int i = 0; i < _caps.DpiStages.Count; i++)
        {
            int stageIndex = i;
            var (sx, sy) = _caps.DpiStages[i];
            string text = sx == sy ? $"档位 {i + 1} · {sx}" : $"档位 {i + 1} · {sx}/{sy}";
            var btn = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6) };
            if (i == _caps.ActiveDpiStage)
                btn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
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
            var btn = new Button
            {
                Content = $"{rate} Hz",
                MinWidth = 98,
                Padding = new Thickness(14, 8, 14, 8),
            };
            if (rate == currentRate)
                btn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            btn.Click += (_, _) => _ = ApplyUpdateAsync(new DeviceTuningUpdate { PollingRateHz = rate });
            RatePanel.Children.Add(btn);
        }
    }

    private void BuildLodButtons(List<LodOption> options, int currentLod)
    {
        _lodOptions = options.ToList();
        LodButtonsPanel.Children.Clear();
        foreach (var opt in options)
        {
            int code = opt.Code;
            var btn = new Button { Content = opt.Label, MinWidth = 98, Padding = new Thickness(14, 8, 14, 8) };
            if (code == currentLod)
                btn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            btn.Click += (_, _) => _ = ApplyUpdateAsync(new DeviceTuningUpdate { LodCode = code });
            LodButtonsPanel.Children.Add(btn);
        }

        int activeIdx = Math.Max(0, _lodOptions.FindIndex(o => o.Code == currentLod));
        LodSlider.Visibility = _lodOptions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnLodSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding || _lodOptions.Count == 0) return;
        int idx = _lodUsesDirectMm
            ? Math.Clamp((int)Math.Round((e.NewValue - _lodMinMm) / 0.1), 0, _lodOptions.Count - 1)
            : Math.Clamp((int)Math.Round(e.NewValue), 0, _lodOptions.Count - 1);
        var opt = _lodOptions[idx];
        LodSliderValueText.Text = FormatLodLabel(opt);
        ScheduleDebouncedUpdate(new DeviceTuningUpdate { LodCode = opt.Code });
    }

    private string FormatLodTooltipValue(double val)
    {
        if (_lodUsesDirectMm)
            return $"{val:0.0} mm";
        if (_lodOptions.Count == 0)
            return $"{val:0.0} mm";
        int idx = Math.Clamp((int)Math.Round(val), 0, _lodOptions.Count - 1);
        double? mm = TryParseLodMm(_lodOptions[idx].Label);
        return mm.HasValue ? $"{mm.Value:0.0} mm" : _lodOptions[idx].Label;
    }

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
            System.Globalization.CultureInfo.InvariantCulture, out double mm)
            ? mm
            : null;
    }

    // ── 支持实时合并队列的硬件下发引擎 ──────────────────────────────────────────────
    private async Task ApplyUpdateAsync(DeviceTuningUpdate update)
    {
        if (string.IsNullOrWhiteSpace(_deviceId)) return;
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
                if (res.Capabilities != null)
                    BindCapabilities(res.Capabilities);
                ShowStatus(res.Message, res.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);

                next = _queuedUpdate;
                _queuedUpdate = null;
            }
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

    private Task ApplyDpiPresetAsync(int dpi)
    {
        _debounceTimer?.Stop();
        _debouncedPending = null;
        _binding = true;
        DpiXSlider.Value = dpi;
        DpiXBox.Value = dpi;
        if (SyncXySwitch.IsOn || _caps?.SupportsIndependentXY != true)
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
        int minDpi = Math.Max(10, _caps?.MinDpi ?? 50);
        int maxDpi = Math.Max(minDpi + 50, _caps?.MaxDpi ?? 45000);
        int step = Math.Max(1, _caps?.DpiStep ?? 50);

        double rawX = !double.IsNaN(DpiXBox.Value) ? DpiXBox.Value : DpiXSlider.Value;
        if (double.IsNaN(rawX)) return null;

        int x = SnapDpi((int)Math.Round(rawX), minDpi, maxDpi, step);
        int y = (_caps?.SupportsIndependentXY == true && !SyncXySwitch.IsOn)
            ? SnapDpi((int)Math.Round(!double.IsNaN(DpiYBox.Value) ? DpiYBox.Value : DpiYSlider.Value), minDpi, maxDpi, step)
            : x;

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

    private void OnHostModeToggled(object sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsHostModeSwitch) return;
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { SetHostMode = HostModeSwitch.IsOn });
    }

    private void OnMotionSyncToggled(object sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsMotionSync) return;
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { MotionSync = MotionSyncSwitch.IsOn });
    }

    private void OnAngleSnapToggled(object sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsAngleSnapping) return;
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { AngleSnapping = AngleSnapSwitch.IsOn });
    }

    private void OnRippleToggled(object sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsRippleControl) return;
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { RippleControl = RippleSwitch.IsOn });
    }

    private void OnLongRangeToggled(object sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsLongRangeMode) return;
        _ = ApplyUpdateAsync(new DeviceTuningUpdate { LongRangeMode = LongRangeSwitch.IsOn });
    }

    private void OnSyncXyToggled(object sender, RoutedEventArgs e)
    {
        if (_binding) return;
        bool sync = SyncXySwitch.IsOn;
        DpiYSection.Visibility = sync ? Visibility.Collapsed : Visibility.Visible;
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

    private void OnDpiXSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding) return;
        _binding = true;
        DpiXBox.Value = e.NewValue;
        if (SyncXySwitch.IsOn)
        {
            DpiYSlider.Value = e.NewValue;
            DpiYBox.Value = e.NewValue;
        }
        _binding = false;
        UpdateDpiSummaryText();
        if (BuildDpiUpdateFromUi() is { } u)
            ScheduleDebouncedUpdate(u);
    }

    private void OnDpiXBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_binding || double.IsNaN(args.NewValue)) return;
        int minDpi = Math.Max(10, _caps?.MinDpi ?? 50);
        int maxDpi = Math.Max(minDpi + 50, _caps?.MaxDpi ?? 45000);
        double clamped = Math.Clamp(args.NewValue, minDpi, maxDpi);

        _binding = true;
        DpiXSlider.Value = clamped;
        if (SyncXySwitch.IsOn)
        {
            DpiYSlider.Value = clamped;
            DpiYBox.Value = clamped;
        }
        _binding = false;
        UpdateDpiSummaryText();
        if (args.NewValue >= minDpi && BuildDpiUpdateFromUi() is { } u)
            ScheduleDebouncedUpdate(u, delayMs: 320);
    }

    private void OnDpiYSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding) return;
        _binding = true;
        DpiYBox.Value = e.NewValue;
        _binding = false;
        UpdateDpiSummaryText();
        if (BuildDpiUpdateFromUi() is { } u)
            ScheduleDebouncedUpdate(u);
    }

    private void OnDpiYBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_binding || double.IsNaN(args.NewValue)) return;
        int minDpi = Math.Max(10, _caps?.MinDpi ?? 50);
        int maxDpi = Math.Max(minDpi + 50, _caps?.MaxDpi ?? 45000);
        _binding = true;
        DpiYSlider.Value = Math.Clamp(args.NewValue, minDpi, maxDpi);
        _binding = false;
        UpdateDpiSummaryText();
        if (args.NewValue >= minDpi && BuildDpiUpdateFromUi() is { } u)
            ScheduleDebouncedUpdate(u, delayMs: 320);
    }

    private void OnAddStage(object sender, RoutedEventArgs e)
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

    private void OnRemoveStage(object sender, RoutedEventArgs e)
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

    private void OnAsymSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
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

    private void OnApplyAsymLod(object sender, RoutedEventArgs e)
    {
        if (_binding || _caps == null || !_caps.SupportsAsymmetricLod) return;
        int lift = Math.Clamp((int)Math.Round(AsymLiftSlider.Value * 10), 2, 26);
        int land = Math.Clamp((int)Math.Round(AsymLandSlider.Value * 10), 1, lift - 1);
        _ = ApplyUpdateAsync(new DeviceTuningUpdate
        {
            AsymmetricLod = (AsymLodSwitch.IsOn, lift, land),
        });
    }

    private void OnDebounceSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        int ms = (int)Math.Round(e.NewValue);
        DebounceValueText.Text = $"{ms} ms";
        if (!_binding)
            ScheduleDebouncedUpdate(new DeviceTuningUpdate { DebounceMs = ms });
    }

    private void OnAngleSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        int deg = (int)Math.Round(e.NewValue);
        AngleValueText.Text = $"{deg}°";
        if (!_binding)
            ScheduleDebouncedUpdate(new DeviceTuningUpdate { AngleTuningDeg = deg });
    }

    private void OnSleepSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
        => SleepValueText.Text = $"{(int)Math.Round(e.NewValue)} 分钟后休眠";

    private void OnLowBatSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
        => LowBatValueText.Text = $"低于 {(int)Math.Round(e.NewValue)}% 提醒";

    private void OnApplyPower(object sender, RoutedEventArgs e)
    {
        if (_caps == null) return;
        var update = new DeviceTuningUpdate();
        if (_caps.SupportsSleepTimeout && _caps.SupportedSleepSeconds.Count == 0)
            update.SleepSeconds = (int)Math.Round(SleepSlider.Value) * 60;
        if (_caps.SupportsLowBatteryWarning)
            update.LowBatteryWarningPercent = (int)Math.Round(LowBatSlider.Value);
        _ = ApplyUpdateAsync(update);
    }

    private void OnRetryProbe(object sender, RoutedEventArgs e)
    {
        _autoWakeAttempts = 0;
        _nextAutoWakeTick = Environment.TickCount64 + AutoWakeBackoffMs[0];
        _ = RefreshAndReloadAsync(manual: true);
    }

    private void OnPagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_busy || string.IsNullOrWhiteSpace(_deviceId) || _caps is not { IsOnline: false })
            return;
        if (_autoWakeAttempts >= AutoWakeBackoffMs.Length)
            return;
        long now = Environment.TickCount64;
        if (now < _nextAutoWakeTick)
            return;
        if (ExternalConfiguratorGuard.IsBackgroundYieldActive(out _))
            return;

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

    private void OnPinToTaskbar(object sender, RoutedEventArgs e)
    {
        if (_deviceId == null) return;
        var d = App.LastDevices.FirstOrDefault(x => x.Id == _deviceId);
        if (d == null) return;
        string groupId = d.Kind switch
        {
            DeviceKind.Keyboard => "builtin:keyboard",
            DeviceKind.Headset => "builtin:headset",
            DeviceKind.Controller => "builtin:controller",
            _ => "builtin:mouse",
        };
        App.Settings.EnsureDefaults();
        var cfg = App.Settings.Current.TaskbarGroups.First(g => g.GroupId == groupId);
        cfg.Visible = true;
        cfg.DefaultDeviceId = _deviceId;
        App.Settings.Current.WidgetVisible = true;
        App.Settings.Save();
        App.RefreshWidget();
        ShowStatus("已固定到任务栏对应分组。", InfoBarSeverity.Success);
    }

    private sealed class CallbackThumbToolTipConverter(Func<double, string> formatter)
        : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is double d ? formatter(d) : $"{value}";

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => DependencyProperty.UnsetValue;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
