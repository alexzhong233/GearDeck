using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using GearDeck.App.Controls;
using GearDeck.App.Services;
using GearDeck.Core;
using GearDeck.Core.Models;

namespace GearDeck.App.Pages;

public sealed partial class DeviceDetailPage : Page
{
    private static readonly int[] CommonDpiPresets = [400, 800, 1200, 1600, 2400, 3200, 6400];

    private string? _deviceId;
    private DeviceTuningCapabilities? _caps;
    private bool _binding;
    private bool _busy;

    public DeviceDetailPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            _deviceId = e.Parameter as string;
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
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Devices.DevicesUpdated -= OnUpdated;
        DeviceImageService.ImagesUpdated -= OnImagesUpdated;
    }

    private void OnUpdated(IReadOnlyList<PeripheralDevice> devices) => App.UiQueue?.TryEnqueue(ShowOverview);
    private void OnImagesUpdated() => App.UiQueue?.TryEnqueue(ShowOverview);

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
                AvatarHost.Children.Clear();
                return;
            }
            TitleText.Text = d.Name;
            SubtitleText.Text = $"{d.Provider} · {d.Transport} · {d.Kind}";
            BatteryText.Text = d.BatteryPercent is { } p ? $"{p}%" : "--";
            StateText.Text = d.State switch
            {
                BatteryState.Charging => "充电中 ⚡",
                BatteryState.FullyCharged => "已充满",
                _ => d.IsSleeping ? "无线休眠中" : (d.Connected ? "已连接" : "离线"),
            };
            NoteText.Text = d.Note ?? "";
            AvatarHost.Children.Clear();
            AvatarHost.Children.Add(UiBuilders.CreateDeviceAvatar(d, 74));
        }
        catch (Exception ex)
        {
            Diag.Log($"ShowOverview 异常: {ex.Message}");
        }
    }

    private async void OnPickDeviceImage(object sender, RoutedEventArgs e)
    {
        var d = App.LastDevices.FirstOrDefault(x => x.Id == _deviceId);
        if (d == null) return;
        if (await DeviceImagePickerDialog.ShowAsync(this.XamlRoot, d))
            ShowOverview();
    }

    // ── 本设备独立低电量提醒策略 ──────────────────────────────────────────────────
    private void BindDeviceLowBatteryPolicy()
    {
        if (string.IsNullOrWhiteSpace(_deviceId)) return;
        _binding = true;
        try
        {
            var policy = App.Settings.GetLowBatteryPolicy(_deviceId);
            DeviceLowBatEnableSwitch.IsOn = policy.Enabled;
            DeviceLowBatCustomSwitch.IsOn = policy.CustomThresholdPercent.HasValue;
            DeviceLowBatNotifySwitch.IsOn = policy.WindowsNotification;

            int eff = App.Settings.GetEffectiveLowBatteryThreshold(_deviceId);
            SafeSetSlider(DeviceLowBatSlider, 5, 80, 1, eff);
            DeviceLowBatThresholdRow.Visibility = policy.CustomThresholdPercent.HasValue ? Visibility.Visible : Visibility.Collapsed;
            DeviceLowBatValueText.Text = policy.CustomThresholdPercent.HasValue ? $"独立阈值 {eff}%" : $"跟随全局 ({eff}%)";
        }
        finally
        {
            _binding = false;
        }
    }

    private void OnDeviceLowBatPolicyChanged(object sender, RoutedEventArgs e)
    {
        if (_binding || string.IsNullOrWhiteSpace(_deviceId)) return;
        var map = App.Settings.Current.DeviceLowBatteryPolicies;
        var policy = new DeviceLowBatteryPolicy
        {
            Enabled = DeviceLowBatEnableSwitch.IsOn,
            CustomThresholdPercent = DeviceLowBatCustomSwitch.IsOn
                ? Math.Clamp((int)Math.Round(DeviceLowBatSlider.Value), 5, 80)
                : null,
            WindowsNotification = DeviceLowBatNotifySwitch.IsOn,
        };
        map[_deviceId] = policy;
        App.Settings.Save();
        App.RefreshWidget();

        DeviceLowBatThresholdRow.Visibility = policy.CustomThresholdPercent.HasValue ? Visibility.Visible : Visibility.Collapsed;
        int eff = App.Settings.GetEffectiveLowBatteryThreshold(_deviceId);
        DeviceLowBatValueText.Text = policy.CustomThresholdPercent.HasValue ? $"独立阈值 {eff}%" : $"跟随全局 ({eff}%)";
    }

    private void OnDeviceLowBatSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding || string.IsNullOrWhiteSpace(_deviceId)) return;
        int val = Math.Clamp((int)Math.Round(e.NewValue), 5, 80);
        DeviceLowBatValueText.Text = $"独立阈值 {val}%";
        if (!DeviceLowBatCustomSwitch.IsOn) return;

        App.Settings.Current.DeviceLowBatteryPolicies[_deviceId] = new DeviceLowBatteryPolicy
        {
            Enabled = DeviceLowBatEnableSwitch.IsOn,
            CustomThresholdPercent = val,
            WindowsNotification = DeviceLowBatNotifySwitch.IsOn,
        };
        App.Settings.Save();
        App.RefreshWidget();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        ReadCapsBtn.IsEnabled = !busy;
        ApplyDpiBtn.IsEnabled = !busy;
        ApplyPowerBtn.IsEnabled = !busy;
        HostModeSwitch.IsEnabled = !busy;
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

            var cachedSlots = !string.IsNullOrWhiteSpace(_deviceId)
                ? App.Settings.GetDeviceOnboardSlots(currentDev, _deviceId)
                : [];
            bool hasOnboardProfiles = caps.OnboardProfileCount > 0 || cachedSlots.Count > 0;
            bool hasAnyTunable = caps.SupportsDpi || caps.SupportedPollingRates.Count > 0 || caps.SupportedLodOptions.Count > 0;
            bool isConfigurableKind = currentDev?.Kind is DeviceKind.Mouse or DeviceKind.Controller;

            ProfileCard.Visibility = (isConfigurableKind || (caps.IsOnline && (hasOnboardProfiles || hasAnyTunable)))
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
                DpiXLabel.Text = independent ? "水平灵敏度 (X DPI)" : "灵敏度 (DPI)";

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
                SafeSetSlider(AsymLiftSlider, 2, 26, 1, lift);
                SafeSetSlider(AsymLandSlider, 1, 25, 1, land);
                AsymLiftText.Text = $"{lift} 档";
                AsymLandText.Text = $"{land} 档";
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

            // 对所有鼠标均显示原生按键重映射 & 宏库入口（即便暂时休眠也保留提示与快捷跳转）
            var dev = App.LastDevices.FirstOrDefault(x => x.Id == _deviceId);
            bool showMacroCard = caps.ButtonBindings.Count > 0 || caps.SupportsOnboardMacroUpload || dev?.Kind == DeviceKind.Mouse;
            MacroAndButtonsCard.Visibility = showMacroCard ? Visibility.Visible : Visibility.Collapsed;
            if (showMacroCard)
                BindButtonMappingsAndQuickMacros(caps);

            bool anyControl = caps.SupportsHostModeSwitch || caps.SupportsDpi || caps.SupportedPollingRates.Count > 0
                              || hasLod || hasAsym || hasDebounce || hasAngle || caps.SupportsSleepTimeout || hasLowBat
                              || hasPerfModes || hasSwitches || caps.ButtonBindings.Count > 0;
            FallbackCard.Visibility = anyControl ? Visibility.Collapsed : Visibility.Visible;
            if (!anyControl)
            {
                FallbackTitle.Text = caps.IsOnline ? "硬件参数说明" : "设备休眠或未唤醒";
                FallbackMessage.Text = caps.StatusMessage ?? "当前设备暂无可调节的公开 HID 参数。";
            }

            if (!caps.IsOnline)
                ShowStatus(caps.StatusMessage ?? "设备当前未唤醒。", InfoBarSeverity.Warning);
            else if (caps.SupportsHostModeSwitch && !caps.IsHostMode)
                ShowStatus(caps.StatusMessage ?? "当前处于板载内存模式。", InfoBarSeverity.Informational);
            else if (!string.IsNullOrWhiteSpace(caps.StatusMessage))
                ShowStatus(caps.StatusMessage, InfoBarSeverity.Informational);
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
                : (cached != null && !string.IsNullOrWhiteSpace(cached.Name) ? cached.Name : $"板载配置 {i + 1}");

            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock
            {
                Text = slot == activeSlot ? $"{baseName} (当前)" : baseName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
            });
            stack.Children.Add(new TextBlock
            {
                Text = !string.IsNullOrWhiteSpace(cached?.Summary)
                    ? cached.Summary
                    : (caps.IsOnline ? "点击切换并读取" : "待唤醒读取"),
                FontSize = 11,
                Opacity = 0.85,
            });

            var btn = new Button
            {
                Content = stack,
                Padding = new Thickness(14, 8, 14, 8),
                IsEnabled = caps.IsOnline,
            };
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
            {
                ShowStatus($"已读取 {manifest.Slots.Count} 个厂家板载配置并同步至游戏切换下拉菜单。", InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus("设备未唤醒或不支持板载槽位读取。", InfoBarSeverity.Warning);
            }
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
                Text = "该设备尚无已保存预设；调整完上方 DPI/回报率/模式后输入名称保存即可。",
                FontSize = 12,
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

            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
            };
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var meta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(new TextBlock
            {
                Text = preset.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
            });
            meta.Children.Add(new TextBlock
            {
                Text = parts.Count > 0 ? string.Join(" · ", parts) : "硬件参数预设",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            row.Children.Add(meta);

            var btns = new ResponsiveWrapPanel { Spacing = 6, LineSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var applyBtn = new Button
            {
                Content = "写入设备",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
                IsEnabled = _caps is { IsOnline: true },
            };
            applyBtn.Click += (_, _) => _ = ApplyUpdateAsync(preset.ToUpdate());
            btns.Children.Add(applyBtn);

            var updateBtn = new Button
            {
                Content = "当前覆盖",
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
                IsEnabled = _caps is { IsOnline: true },
            };
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
            btns.Children.Add(updateBtn);

            var delBtn = new Button { Content = "删除", Padding = new Thickness(8, 4, 8, 4), FontSize = 12 };
            delBtn.Click += (_, _) =>
            {
                App.Settings.Current.Presets.RemoveAll(p => p.Id == preset.Id);
                App.Settings.Save();
                RefreshDeviceSavedPresets();
            };
            btns.Children.Add(delBtn);

            Grid.SetColumn(btns, 1);
            row.Children.Add(btns);
            card.Child = row;
            DeviceSavedPresetsPanel.Children.Add(card);
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
        {
            MacroNoteText.Text = "设备当前处于无线休眠或待唤醒状态（按键板载映射需唤醒后读取；宏库编排在左侧「鼠标宏中心」永久可用）。";
        }
        else if (!string.IsNullOrWhiteSpace(caps.MacroCapabilityNote))
        {
            MacroNoteText.Text = caps.MacroCapabilityNote;
        }
        else
        {
            MacroNoteText.Text = "可在「鼠标宏中心」统一录制与编辑动作，再在下方快速绑定或写入本设备。";
        }

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
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
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
        s.StepFrequency = Math.Max(1, step);
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
        int x = (int)Math.Round(DpiXBox.Value);
        int y = SyncXySwitch.IsOn ? x : (int)Math.Round(DpiYBox.Value);
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

        int maxStages = _caps.DeviceId.StartsWith("razer:", StringComparison.OrdinalIgnoreCase) ? 5 : 6;
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
                MinWidth = 104,
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
        LodButtonsPanel.Children.Clear();
        foreach (var opt in options)
        {
            int code = opt.Code;
            var btn = new Button { Content = opt.Label, MinWidth = 108, Padding = new Thickness(14, 8, 14, 8) };
            if (code == currentLod)
                btn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            btn.Click += (_, _) => _ = ApplyUpdateAsync(new DeviceTuningUpdate { LodCode = code });
            LodButtonsPanel.Children.Add(btn);
        }
    }

    private async Task ApplyUpdateAsync(DeviceTuningUpdate update)
    {
        if (string.IsNullOrWhiteSpace(_deviceId) || _busy) return;
        SetBusy(true);
        try
        {
            var res = await App.Devices.ApplySettingsAsync(_deviceId, update);
            if (res.Capabilities != null)
                BindCapabilities(res.Capabilities);
            ShowStatus(res.Message, res.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Task ApplyDpiPresetAsync(int dpi)
    {
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

    private Task ApplyCurrentDpiFromUiAsync()
    {
        if (double.IsNaN(DpiXBox.Value)) return Task.CompletedTask;
        int x = (int)Math.Round(DpiXBox.Value);
        int y = (_caps?.SupportsIndependentXY == true && !SyncXySwitch.IsOn && !double.IsNaN(DpiYBox.Value))
            ? (int)Math.Round(DpiYBox.Value)
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
        return ApplyUpdateAsync(update);
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
        DpiXLabel.Text = sync ? "灵敏度 (DPI)" : "水平灵敏度 (X DPI)";
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
    }

    private void OnDpiXBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_binding || double.IsNaN(args.NewValue)) return;
        _binding = true;
        DpiXSlider.Value = args.NewValue;
        if (SyncXySwitch.IsOn)
        {
            DpiYSlider.Value = args.NewValue;
            DpiYBox.Value = args.NewValue;
        }
        _binding = false;
        UpdateDpiSummaryText();
    }

    private void OnDpiYSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding) return;
        _binding = true;
        DpiYBox.Value = e.NewValue;
        _binding = false;
        UpdateDpiSummaryText();
    }

    private void OnDpiYBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_binding || double.IsNaN(args.NewValue)) return;
        _binding = true;
        DpiYSlider.Value = args.NewValue;
        _binding = false;
        UpdateDpiSummaryText();
    }

    private void OnAddStage(object sender, RoutedEventArgs e)
    {
        if (_caps == null) return;
        int maxStages = _caps.DeviceId.StartsWith("razer:", StringComparison.OrdinalIgnoreCase) ? 5 : 6;
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

    private void OnApplyDpi(object sender, RoutedEventArgs e) => _ = ApplyCurrentDpiFromUiAsync();

    private void OnAsymSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding) return;
        int lift = Math.Clamp((int)Math.Round(AsymLiftSlider.Value), 2, 26);
        int land = Math.Clamp((int)Math.Round(AsymLandSlider.Value), 1, 25);
        if (land >= lift)
        {
            land = lift - 1;
            _binding = true;
            AsymLandSlider.Value = land;
            _binding = false;
        }
        AsymLiftText.Text = $"{lift} 档";
        AsymLandText.Text = $"{land} 档";
    }

    private void OnApplyAsymLod(object sender, RoutedEventArgs e)
    {
        int lift = Math.Clamp((int)Math.Round(AsymLiftSlider.Value), 2, 26);
        int land = Math.Clamp((int)Math.Round(AsymLandSlider.Value), 1, lift - 1);
        _ = ApplyUpdateAsync(new DeviceTuningUpdate
        {
            AsymmetricLod = (AsymLodSwitch.IsOn, lift, land),
        });
    }

    private void OnDebounceSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
        => DebounceValueText.Text = $"{(int)Math.Round(e.NewValue)} ms";

    private void OnApplyDebounce(object sender, RoutedEventArgs e)
        => _ = ApplyUpdateAsync(new DeviceTuningUpdate { DebounceMs = (int)Math.Round(DebounceSlider.Value) });

    private void OnAngleSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
        => AngleValueText.Text = $"{(int)Math.Round(e.NewValue)}°";

    private void OnApplyAngle(object sender, RoutedEventArgs e)
        => _ = ApplyUpdateAsync(new DeviceTuningUpdate { AngleTuningDeg = (int)Math.Round(AngleSlider.Value) });

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

    private void OnRetryProbe(object sender, RoutedEventArgs e) => _ = LoadCapabilitiesAsync();
    private void OnRefresh(object sender, RoutedEventArgs e) => _ = App.Devices.RefreshAsync();

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
}
