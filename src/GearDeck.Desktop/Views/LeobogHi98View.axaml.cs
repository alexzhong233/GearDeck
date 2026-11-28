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
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using GearDeck.Core;
using GearDeck.Core.Hid;
using GearDeck.Core.Providers;
using GearDeck.Core.Services;

namespace GearDeck.Desktop.Views;

/// <summary>
/// LEOBOG Hi98C Pro 独立驱动控制台：按键映射、RGB 灯光、硬件设置/备份与诊断日志。
/// 全部 HID I/O 在 Task.Run 中执行，回到 UI 前检查 IsLoaded，避免阻塞与已卸载访问。
/// </summary>
public partial class LeobogHi98View : UserControl
{
    private const int LiveStreamIntervalMs = 50; // 20 FPS 上限，防无线丢键
    private static readonly int[] PaletteIndices = [0, 1, 2, 3, 4, 5, 6];
    private static readonly string[] PaletteNames = ["正红", "翡翠绿", "宝石蓝", "明黄", "霓虹紫", "电光青", "纯白"];
    private static readonly Color[] PaletteColors =
    [
        Color.FromRgb(255, 0, 0), Color.FromRgb(0, 255, 0), Color.FromRgb(0, 51, 255),
        Color.FromRgb(255, 230, 0), Color.FromRgb(255, 0, 255), Color.FromRgb(0, 255, 255),
        Color.FromRgb(255, 255, 255),
    ];
    private static readonly Hi98RgbColor[] QuickSwatches =
    [
        new(0, 229, 255), new(160, 40, 255), new(255, 0, 128), new(255, 95, 0),
        new(40, 235, 125), new(255, 220, 40), new(255, 255, 255), new(0, 0, 0),
    ];

    private sealed class KeycapCell
    {
        public Border? Box;
        public TextBlock? Label;
        public TextBlock? SubLabel;
    }

    private readonly string _deviceId;
    private LeobogHi98BackupService? _backupService;

    private Hi98ConnectionTransport _transport = Hi98ConnectionTransport.Disconnected;
    private Hi98LedConfigBlock? _ledConfig;
    private Hi98MatrixCalibrationMap _calibration = Hi98CLayout.DefaultCalibration;
    private Hi98KeyLayer _activeLayer = Hi98KeyLayer.Base;

    private readonly Dictionary<int, Dictionary<int, Hi98KeyBindingRecord>> _pendingByLayer = [];
    private readonly Dictionary<int, Hi98KeyBindingRecord> _currentBindings = [];
    private readonly Dictionary<string, KeycapCell> _keymapCells = [];
    private readonly Dictionary<string, KeycapCell> _rgbCells = [];
    private readonly Dictionary<int, Hi98RgbColor> _perKeyColors = [];

    private string? _selectedKeyId;
    private Hi98KeycodeOptionCategory? _selectedCategory;
    private byte _selectedEffectId = 10;
    private int _selectedColorIndex = 7;
    private byte _selectedBrightness = 4;
    private byte _selectedSpeed = 2;

    private bool _busy;
    private bool _binding;
    private bool _initialized;
    private bool _liveStreaming;
    private bool _liveSending;
    private bool _rgbPointerDown;
    private Hi98RgbColor _brush = Hi98RgbColor.CyberCyan;
    private DispatcherTimer? _liveTimer;

    private readonly List<string> _log = [];
    private readonly List<(string Path, Hi98EepromBackupSnapshot Snapshot)> _backups = [];

    public LeobogHi98View() : this(string.Empty) { }

    public LeobogHi98View(string deviceId)
    {
        _deviceId = deviceId ?? string.Empty;
        InitializeComponent();

        WireEvents();
        BuildLayerButtons();
        BuildComboKeys();
        BuildCategoryButtons();
        BuildKeycodeOptions();
        BuildEffectButtons();
        BuildPalette();
        BuildSwatches();
        BuildZoneButtons();
        BuildHardwareCombos();
        BuildCanvases();

        _selectedKeyId = Hi98CLayout.Keys.FirstOrDefault()?.Id;
        RefreshIdentity();
        UpdateMacBanner();
        RefreshInspector();
        UpdatePendingIndicators();
        RenderRgbCanvas();
        SetTab("keymap");
        AppendLog($"打开 LEOBOG Hi98 控制台（{_deviceId}）");
    }

    private LeobogHi98BackupService Backup => _backupService ??= new LeobogHi98BackupService();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (App.Devices == null) return;

        App.Devices.DevicesUpdated += OnDevicesUpdated;
        if (!_initialized)
        {
            _initialized = true;
            _ = InitializeAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (App.Devices != null)
            App.Devices.DevicesUpdated -= OnDevicesUpdated;
        StopLiveStreaming(silent: true);
        _liveTimer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDevicesUpdated(IReadOnlyList<GearDeck.Core.Models.PeripheralDevice> devices)
        => App.UiPost(() =>
        {
            RefreshIdentity();
            UpdateBatteryFromDevice();
        });

    // ── 初始化 ─────────────────────────────────────────────────────────────────

    private async Task InitializeAsync()
    {
        try
        {
            var loadedCalibration = await Task.Run(() =>
            {
                try { return Backup.LoadCalibration(); }
                catch { return null; }
            });
            if (!IsLoaded) return;
            if (loadedCalibration != null)
            {
                _calibration = loadedCalibration;
                AppendLog($"载入矩阵校准：{_calibration.SourceDescription}");
            }

            RenderKeymap();
            RefreshInspector();
            await LoadBatteryAsync();
            await SyncFromKeyboardAsync();
            await RefreshBackupsAsync();
            await RefreshDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            Diag.Log($"LeobogHi98View.InitializeAsync 异常: {ex.Message}");
            if (IsLoaded) ShowStatus($"初始化失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void RefreshIdentity()
    {
        _transport = string.IsNullOrWhiteSpace(_deviceId)
            ? Hi98ConnectionTransport.Disconnected
            : LeobogProvider.ResolveTransport(_deviceId);

        var device = App.LastDevices.FirstOrDefault(d =>
            string.Equals(d.Id, _deviceId, StringComparison.OrdinalIgnoreCase));
        TitleText.Text = device?.Name ?? "LEOBOG Hi98 驱动控制台";
        SubtitleText.Text = string.IsNullOrWhiteSpace(_deviceId)
            ? "未指定设备"
            : $"LEOBOG Hi98C Pro · 101 键 · {_transport.RawValue()}";

        TransportText.Text = _transport.ShortBadge();
        TransportBadge.Background = new SolidColorBrush(_transport switch
        {
            Hi98ConnectionTransport.UsbWired => Color.FromArgb(40, 16, 185, 129),
            Hi98ConnectionTransport.Wireless24G => Color.FromArgb(40, 56, 189, 248),
            Hi98ConnectionTransport.Bluetooth => Color.FromArgb(40, 168, 85, 247),
            _ => Color.FromArgb(40, 148, 163, 184),
        });
        TransportText.Foreground = new SolidColorBrush(_transport switch
        {
            Hi98ConnectionTransport.UsbWired => Color.FromRgb(52, 211, 153),
            Hi98ConnectionTransport.Wireless24G => Color.FromRgb(125, 211, 252),
            Hi98ConnectionTransport.Bluetooth => Color.FromRgb(196, 181, 253),
            _ => Color.FromRgb(148, 163, 184),
        });

        bool writable = _transport.CanWriteEEPROM();
        WriteProtectText.Text = writable ? "USB 有线 · 可写入" : "无线只读保护";
        WriteProtectBadge.Background = new SolidColorBrush(writable
            ? Color.FromArgb(40, 16, 185, 129)
            : Color.FromArgb(40, 251, 146, 60));
        WriteProtectText.Foreground = new SolidColorBrush(writable
            ? Color.FromRgb(52, 211, 153)
            : Color.FromRgb(253, 186, 116));

        RgbHintText.Text = writable
            ? "当前为 USB-C 有线模式，可直接写入键盘闪存。"
            : "无线模式仅可预览；请接 USB-C 有线后写入键盘闪存。";
        HardwareNoteText.Text = writable
            ? "板载配置读取成功后可保存；休眠时间限制 60-1200 秒，禁止写入 0（永不休眠）。"
            : "无线模式严格只读：可查看参数，写入需切换 USB-C 有线。";

        UpdateActionAvailability();
        UpdateBatteryFromDevice();
    }

    private void WireEvents()
    {
        BackBtn.Click += (_, _) => MainWindow.Instance?.OpenDevicesPage();
        BatteryButton.Click += async (_, _) => await LoadBatteryAsync();
        SyncBtn.Click += async (_, _) => await SyncFromKeyboardAsync();
        CommitKeymapBtn.Click += async (_, _) => await CommitCurrentLayerAsync();

        TabKeymapBtn.Click += (_, _) => SetTab("keymap");
        TabRgbBtn.Click += (_, _) => SetTab("rgb");
        TabSettingsBtn.Click += (_, _) => SetTab("settings");
        TabDiagnosticsBtn.Click += (_, _) => SetTab("diagnostics");

        StageMacBtn.Click += (_, _) => StageMacLayout(announce: true);
        CopyToWritableBtn.Click += (_, _) => StageMacLayout(announce: true);
        EnableMacEditableBtn.Click += async (_, _) => await EnableMacEditableAsync();
        QuickStageMacToWinBtn.Click += (_, _) => StageMacLayout(announce: true);
        QuickEnableMacEditableBtn.Click += async (_, _) => await EnableMacEditableAsync();

        PresetModifiersBtn.Click += (_, _) => ApplyMacModifierPreset(swap: false);
        PresetSwapModifiersBtn.Click += (_, _) => ApplyMacModifierPreset(swap: true);
        PresetStdFunctionBtn.Click += (_, _) => ApplyMacFunctionKeyPreset(standard: true);
        PresetMediaFnBtn.Click += (_, _) => ApplyMacFunctionKeyPreset(standard: false);

        RevertKeyBtn.Click += (_, _) => RevertSelectedKey();
        ApplyComboBtn.Click += (_, _) => ApplyCustomCombo();
        SearchBox.TextChanged += (_, _) => BuildKeycodeOptions();

        BrightnessSlider.ValueChanged += (_, e) =>
        {
            _selectedBrightness = (byte)Math.Clamp((int)Math.Round(e.NewValue), 0, 9);
            BrightnessValueText.Text = $"亮度：{_selectedBrightness} / 9";
        };
        SpeedSlider.ValueChanged += (_, e) =>
        {
            _selectedSpeed = (byte)Math.Clamp((int)Math.Round(e.NewValue), 0, 4);
            SpeedValueText.Text = $"速度：{_selectedSpeed} / 4";
        };
        RainbowBtn.Click += (_, _) => SelectColorIndex(7);
        SaveOnboardBtn.Click += async (_, _) => await SaveOnboardLightingAsync();

        FillAllBtn.Click += (_, _) => PaintAll(_brush);
        ClearAllBtn.Click += (_, _) => PaintAll(Hi98RgbColor.Black);
        LivePreviewBtn.Click += (_, _) => ToggleLiveStreaming();
        BurnStaticBtn.Click += async (_, _) => await BurnStaticRgbAsync();

        SaveHardwareBtn.Click += async (_, _) => await SaveHardwareSettingsAsync();
        ImportBackupBtn.Click += async (_, _) => await ImportAndRestoreAsync();
        OpenBackupFolderBtn.Click += async (_, _) => await OpenBackupFolderAsync();
        RestoreFnBtn.Click += async (_, _) => await RestoreFnAsync();
        RefreshBackupsBtn.Click += async (_, _) => await RefreshBackupsAsync();

        OpenPermissionBtn.Click += (_, _) => MacIOKitHid.OpenInputMonitoringSettings();
        RetryPermissionBtn.Click += async (_, _) =>
        {
            if (OperatingSystem.IsMacOS()) MacIOKitHid.RequestInputMonitoringAccess();
            await SyncFromKeyboardAsync();
            await RefreshDiagnosticsAsync();
        };
        RescanBtn.Click += async (_, _) => await RefreshDiagnosticsAsync();

        CopyLogsBtn.Click += async (_, _) => await CopyLogsAsync();
        ClearLogsBtn.Click += (_, _) =>
        {
            _log.Clear();
            LogBox.Text = string.Empty;
        };

        RgbCanvas.PointerPressed += OnRgbPointerPressed;
        RgbCanvas.PointerMoved += OnRgbPointerMoved;
        RgbCanvas.PointerReleased += (_, _) => _rgbPointerDown = false;
        RgbCanvas.PointerCaptureLost += (_, _) => _rgbPointerDown = false;
    }

    // ── 页签与状态 ─────────────────────────────────────────────────────────────

    private void SetTab(string tab)
    {
        PanelKeymap.IsVisible = tab == "keymap";
        PanelRgb.IsVisible = tab == "rgb";
        PanelSettings.IsVisible = tab == "settings";
        PanelDiagnostics.IsVisible = tab == "diagnostics";

        SetActive(TabKeymapBtn, tab == "keymap");
        SetActive(TabRgbBtn, tab == "rgb");
        SetActive(TabSettingsBtn, tab == "settings");
        SetActive(TabDiagnosticsBtn, tab == "diagnostics");

        if (tab == "diagnostics") _ = RefreshDiagnosticsAsync();
        if (tab == "settings") _ = RefreshBackupsAsync();
    }

    private static void SetActive(Button button, bool active)
    {
        if (active) button.Classes.Add("accent");
        else button.Classes.Remove("accent");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ShortStatusText.Text = busy ? "正在与键盘通信…" : string.Empty;
        UpdateActionAvailability();
    }

    private void UpdateActionAvailability()
    {
        bool ready = !_busy;
        bool writable = _transport.CanWriteEEPROM();
        bool layerWritable = _activeLayer.IsWritable;

        SyncBtn.IsEnabled = ready && !string.IsNullOrWhiteSpace(_deviceId);
        BatteryButton.IsEnabled = ready;
        CommitKeymapBtn.IsEnabled = ready && writable && TotalPendingCount > 0;
        StageMacBtn.IsEnabled = ready;
        CopyToWritableBtn.IsEnabled = ready;
        EnableMacEditableBtn.IsEnabled = ready && writable;
        QuickStageMacToWinBtn.IsEnabled = ready;
        QuickEnableMacEditableBtn.IsEnabled = ready && writable;
        PresetModifiersBtn.IsEnabled = ready && writable && layerWritable;
        PresetSwapModifiersBtn.IsEnabled = ready && writable && layerWritable;
        PresetStdFunctionBtn.IsEnabled = ready && writable && layerWritable;
        PresetMediaFnBtn.IsEnabled = ready && writable && layerWritable;
        RevertKeyBtn.IsEnabled = ready && writable && layerWritable && _selectedKeyId != null;
        ApplyComboBtn.IsEnabled = ready && writable && layerWritable && _selectedKeyId != null;
        SaveOnboardBtn.IsEnabled = ready && writable && _ledConfig != null;
        BurnStaticBtn.IsEnabled = ready && writable;
        SaveHardwareBtn.IsEnabled = ready && writable && _ledConfig != null;
        RestoreFnBtn.IsEnabled = ready && writable;
        ImportBackupBtn.IsEnabled = ready;
        LivePreviewBtn.IsEnabled = ready && _transport.CanStreamLiveRGB();
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(message))
            MainWindow.HideToast();
        else
            MainWindow.ShowToast(message, severity);
    }

    private void AppendLog(string message)
    {
        _log.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        if (_log.Count > 400) _log.RemoveRange(0, _log.Count - 400);
        if (LogBox != null) LogBox.Text = string.Join(Environment.NewLine, _log);
    }

    private async Task CopyLogsAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(string.Join(Environment.NewLine, _log));
                ShowStatus("已复制全部操作日志到剪贴板。", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"复制日志失败：{ex.Message}", InfoBarSeverity.Warning);
        }
    }

    // ── 电量 ───────────────────────────────────────────────────────────────────

    private void UpdateBatteryFromDevice()
    {
        var device = App.LastDevices.FirstOrDefault(d =>
            string.Equals(d.Id, _deviceId, StringComparison.OrdinalIgnoreCase));
        if (device?.BatteryPercent is { } pct && _transport != Hi98ConnectionTransport.Disconnected)
        {
            BatteryText.Text = $"电量 {pct}%";
            BatteryBar.Value = pct;
        }
        else if (_transport == Hi98ConnectionTransport.UsbWired)
        {
            BatteryText.Text = "有线 · 充电中";
            BatteryBar.Value = 100;
        }
    }

    private async Task LoadBatteryAsync()
    {
        if (string.IsNullOrWhiteSpace(_deviceId) || _busy) return;
        var transport = _transport;
        if (transport == Hi98ConnectionTransport.Disconnected)
        {
            BatteryText.Text = "电量 --";
            BatteryBar.Value = 0;
            return;
        }

        SetBusy(true);
        try
        {
            var result = await Task.Run(() =>
            {
                int? pct = LeobogProvider.ReadBatteryOnce(_deviceId, out bool charging, out bool denied);
                return (Percent: pct, Charging: charging, Denied: denied);
            });
            if (!IsLoaded) return;

            if (transport == Hi98ConnectionTransport.UsbWired || result.Charging)
            {
                BatteryText.Text = "有线 · 充电中";
                BatteryBar.Value = 100;
                AppendLog("USB 有线模式：报告充电中（不提供百分比）。");
            }
            else if (result.Percent is int p)
            {
                BatteryText.Text = $"电量 {p}%";
                BatteryBar.Value = p;
                AppendLog($"无线电量读取：{p}%");
            }
            else if (result.Denied)
            {
                BatteryText.Text = "电量 --（需输入监控）";
                BatteryBar.Value = 0;
                ShowStatus("读取电量需要 macOS「输入监控」权限，请在诊断页授权。", InfoBarSeverity.Warning);
            }
            else
            {
                BatteryText.Text = "电量 --（休眠/无数据）";
                BatteryBar.Value = 0;
            }
        }
        catch (Exception ex)
        {
            if (IsLoaded) BatteryText.Text = "电量读取失败";
            AppendLog($"电量读取异常：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ── 键位读取与同步 ─────────────────────────────────────────────────────────

    private sealed class LayerSnapshot
    {
        public byte[]? Page;
        public Hi98LedConfigBlock? LedConfig;
        public Hi98MatrixCalibrationMap? Calibration;
        public string? Error;
        public bool PermissionDenied;
    }

    private async Task SyncFromKeyboardAsync()
    {
        if (string.IsNullOrWhiteSpace(_deviceId) || _busy) return;

        SetBusy(true);
        var layer = _activeLayer;
        try
        {
            if (_transport != Hi98ConnectionTransport.UsbWired && layer.IsWritable)
                AppendLog($"无线读取「{layer.Title}」键位页（请求命令 0x01 / 返回 0x41）…");

            var snapshot = await Task.Run(() => ReadLayerSnapshot(_deviceId, layer, _transport));
            if (!IsLoaded) return;

            if (snapshot.Calibration != null)
                _calibration = snapshot.Calibration;
            if (snapshot.LedConfig != null)
            {
                _ledConfig = snapshot.LedConfig;
                ApplyLedConfigToUi();
            }

            if (snapshot.Page != null)
            {
                ApplyPageToCurrent(snapshot.Page);
                AppendLog($"已同步「{layer.Title}」：{_currentBindings.Count} 个键位槽。");
                ShowStatus($"已从键盘同步「{layer.Title}」。", InfoBarSeverity.Success);
            }
            else
            {
                AppendLog($"同步失败：{snapshot.Error ?? "未知错误"}");
                ShowStatus(snapshot.Error ?? "读取键位失败：键盘可能休眠，请轻按按键唤醒后重试。",
                    snapshot.PermissionDenied ? InfoBarSeverity.Error : InfoBarSeverity.Warning);
            }

            RenderKeymap();
            RefreshInspector();
            UpdateMacBanner();
            UpdatePendingIndicators();
            UpdateActionAvailability();
            await RefreshDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            Diag.Log($"LeobogHi98View.SyncFromKeyboardAsync 异常: {ex.Message}");
            if (IsLoaded) ShowStatus($"同步失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private LayerSnapshot ReadLayerSnapshot(string deviceId, Hi98KeyLayer layer, Hi98ConnectionTransport transport)
    {
        var result = new LayerSnapshot();
        bool perm = false;
        string? error = null;

        result.LedConfig = LeobogProvider.ReadLedConfigBlock(deviceId, out perm, out error);
        if (perm) result.PermissionDenied = true;

        bool perm2 = false;
        string? pageError = null;
        result.Page = LeobogProvider.ReadKeymapPage(deviceId, layer, out perm2, out pageError);
        if (perm2) result.PermissionDenied = true;
        result.Error = pageError ?? error;

        if (result.Page != null && layer.IsBaseLayer && transport == Hi98ConnectionTransport.UsbWired)
        {
            var macPage = LeobogProvider.ReadKeymapPage(deviceId, Hi98KeyLayer.MacPresetBase, out _, out _);
            result.Calibration = Hi98MatrixCalibrator.Calibrate(result.Page, macPage);
            try { Backup.SaveCalibration(result.Calibration); } catch { }
        }
        return result;
    }

    private void ApplyPageToCurrent(byte[] page)
    {
        _currentBindings.Clear();
        foreach (var spec in Hi98CLayout.Keys)
        {
            int slot = SlotFor(spec);
            int offset = slot * 4;
            if (offset + 4 <= page.Length)
                _currentBindings[slot] = new Hi98KeyBindingRecord(page.AsSpan(offset, 4).ToArray());
        }
    }

    private int SlotFor(Hi98KeySlotSpec spec) => _calibration.MatrixIndexForKeyID(spec.Id);

    private Dictionary<int, Hi98KeyBindingRecord> PendingFor(Hi98KeyLayer layer)
    {
        if (!_pendingByLayer.TryGetValue(layer.RawValue, out var map))
        {
            map = [];
            _pendingByLayer[layer.RawValue] = map;
        }
        return map;
    }

    private int PendingCount => PendingFor(_activeLayer).Count;

    private int TotalPendingCount => Hi98KeyLayer.WritableLayers.Sum(l => PendingFor(l).Count);

    private Hi98KeyBindingRecord GetDisplayRecord(Hi98KeySlotSpec spec)
    {
        int slot = SlotFor(spec);
        if (PendingFor(_activeLayer).TryGetValue(slot, out var pending)) return pending;
        if (_currentBindings.TryGetValue(slot, out var current)) return current;
        return Hi98CLayout.DefaultRecordFor(spec, _activeLayer);
    }

    // ── 层切换 / Mac 模式 ─────────────────────────────────────────────────────

    private void BuildLayerButtons()
    {
        LayerButtonsPanel.Children.Clear();
        foreach (var layer in Hi98KeyLayer.UiDisplayOrder)
        {
            int modified = PendingFor(layer).Count;
            string text = modified > 0 ? $"{layer.Title} ({modified})" : layer.Title;
            var btn = new Button { Content = text, Classes = { "pill" }, Margin = new Thickness(0, 0, 6, 6) };
            if (layer.RawValue == _activeLayer.RawValue) SetActive(btn, true);
            ToolTip.SetTip(btn, layer.Subtitle);
            btn.Click += async (_, _) => await SelectLayerAsync(layer);
            LayerButtonsPanel.Children.Add(btn);
        }
    }

    private async Task SelectLayerAsync(Hi98KeyLayer layer)
    {
        if (_busy) return;
        _activeLayer = layer;
        _currentBindings.Clear();
        _selectedKeyId = Hi98CLayout.Keys.FirstOrDefault()?.Id;
        BuildLayerButtons();
        UpdateMacBanner();
        RenderKeymap();
        RefreshInspector();
        UpdatePendingIndicators();
        UpdateActionAvailability();
        await SyncFromKeyboardAsync();
    }

    private void UpdateMacBanner()
    {
        bool macMode = _ledConfig?.OsMode == Hi98OsMode.Mac;
        MacBanner.IsVisible = macMode;
        ReadOnlyBanner.IsVisible = !_activeLayer.IsWritable;
        EnableMacEditableBtn.IsEnabled = !_busy && _transport.CanWriteEEPROM();
        QuickEnableMacEditableBtn.IsEnabled = !_busy && _transport.CanWriteEEPROM();
        LayerHintText.Text = _activeLayer switch
        {
            _ when !_activeLayer.IsWritable => "当前为固件只读参考层，可选择「复制到可编程层去改键」。",
            _ when macMode => "当前系统模式为 Fn+E Mac 预设：建议先启用 Mac 可改键模式再编辑。",
            _ when _transport == Hi98ConnectionTransport.UsbWired => "USB-C 有线：编辑后点击右上角「写入改键」并等待 400ms 回读校验。",
            _ => "无线模式：可读取与本地暂存，写入需连接 USB-C 有线。",
        };
    }

    private void StageMacLayout(bool announce)
    {
        var basePending = PendingFor(Hi98KeyLayer.Base);
        var fn1Pending = PendingFor(Hi98KeyLayer.Fn1);
        var baseKeyIds = new[] { "lwin", "lalt" }.Concat(Enumerable.Range(1, 12).Select(i => $"f{i}"));
        foreach (string keyId in baseKeyIds)
        {
            var spec = Hi98CLayout.KeyById.GetValueOrDefault(keyId);
            if (spec == null) continue;
            basePending[SlotFor(spec)] = Hi98CLayout.DefaultRecordFor(spec, Hi98KeyLayer.MacPresetBase);
        }
        for (int i = 1; i <= 12; i++)
        {
            var spec = Hi98CLayout.KeyById.GetValueOrDefault($"f{i}");
            if (spec != null)
                fn1Pending[SlotFor(spec)] = Hi98KeyBindingRecord.Key((byte)(0x3A + i - 1));
        }
        if (Hi98CLayout.KeyById.GetValueOrDefault("lwin") is { } lwinSpec)
            fn1Pending[SlotFor(lwinSpec)] = Hi98KeyBindingRecord.Empty;

        _activeLayer = Hi98KeyLayer.Base;
        _currentBindings.Clear();
        BuildLayerButtons();
        UpdateMacBanner();
        RenderKeymap();
        RefreshInspector();
        UpdatePendingIndicators();
        UpdateActionAvailability();
        AppendLog("已暂存 Mac 出厂键位（⌥Opt / ⌘Cmd 与 F1-F12 多媒体）到可编程默认层与 Fn1 层，并保留其他自定义改键。");
        if (announce)
            ShowStatus("已将 Mac 键位映射到可编程默认层与 Fn1 层（保留其余改键）；点击「写入改键」在有线模式写入生效。",
                InfoBarSeverity.Informational);
    }

    private async Task EnableMacEditableAsync()
    {
        if (!_transport.CanWriteEEPROM())
        {
            ShowStatus("写入 Mac 键位需先连接 USB-C 有线数据线。", InfoBarSeverity.Warning);
            return;
        }
        if (_busy) return;

        StageMacLayout(announce: false);
        var baseUpdates = new Dictionary<int, Hi98KeyBindingRecord>(PendingFor(Hi98KeyLayer.Base));
        var fn1Updates = new Dictionary<int, Hi98KeyBindingRecord>(PendingFor(Hi98KeyLayer.Fn1));

        SetBusy(true);
        try
        {
            var outcome = await Task.Run(() =>
            {
                var messages = new List<string>();
                bool ok = true;
                var baseResult = LeobogProvider.WriteKeymapLayerWired(_deviceId, Hi98KeyLayer.Base, baseUpdates,
                    "启用 Mac 可改键模式（默认层）", Backup);
                messages.Add(baseResult.Message);
                ok &= baseResult.Success;

                var fn1Result = LeobogProvider.WriteKeymapLayerWired(_deviceId, Hi98KeyLayer.Fn1, fn1Updates,
                    "启用 Mac 可改键模式（Fn1 层）", Backup);
                messages.Add(fn1Result.Message);
                ok &= fn1Result.Success;

                if (ok)
                {
                    var cfg = LeobogProvider.ReadLedConfigBlock(_deviceId, out _, out _);
                    if (cfg != null)
                    {
                        cfg.OsMode = Hi98OsMode.Windows;
                        cfg.WinKeyLocked = false;
                        var cfgResult = LeobogProvider.WriteLedConfigWired(_deviceId, cfg,
                            "切换至 Fn+W 可改键模式并解锁 Win/⌘", Backup);
                        messages.Add(cfgResult.Message);
                        ok &= cfgResult.Success;
                    }
                }
                return (Ok: ok, Message: string.Join(" ", messages));
            });
            if (!IsLoaded) return;

            if (outcome.Ok)
            {
                PendingFor(Hi98KeyLayer.Base).Clear();
                PendingFor(Hi98KeyLayer.Fn1).Clear();
                ShowStatus("已启用 Mac 可改键模式：Mac 键位已写入可编程层并切换至 Fn+W。",
                    InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus(outcome.Message, InfoBarSeverity.Error);
            }
            AppendLog($"启用 Mac 可改键模式：{outcome.Message}");
        }
        catch (Exception ex)
        {
            if (IsLoaded) ShowStatus($"启用 Mac 可改键模式失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            UpdatePendingIndicators();
            BuildLayerButtons();
            await SyncFromKeyboardAsync();
        }
    }

    private void ApplyMacModifierPreset(bool swap)
    {
        if (!EnsureEditable()) return;
        byte first = 0xE2;
        byte second = 0xE3;
        if (swap) (first, second) = (second, first);

        AssignRecordByKeyId("lwin", Hi98KeyBindingRecord.Key(first));
        AssignRecordByKeyId("lalt", Hi98KeyBindingRecord.Key(second));
        RenderKeymap();
        RefreshInspector();
        UpdatePendingIndicators();
        ShowStatus(swap ? "已暂存互换后的 ⌥/⌘ 修饰键（左二=⌘，靠空格=⌥）。" : "已暂存标准 Mac 修饰键（左二=⌥，靠空格=⌘）。",
            InfoBarSeverity.Informational);
    }

    private void ApplyMacFunctionKeyPreset(bool standard)
    {
        if (!EnsureEditable()) return;
        for (int i = 1; i <= 12; i++)
        {
            var spec = Hi98CLayout.KeyById.GetValueOrDefault($"f{i}");
            if (spec == null) continue;
            var record = standard
                ? Hi98KeyBindingRecord.Key((byte)(0x3A + i - 1))
                : Hi98CLayout.DefaultRecordFor(spec, Hi98KeyLayer.MacPresetBase);
            PendingFor(_activeLayer)[SlotFor(spec)] = record;
        }
        RenderKeymap();
        RefreshInspector();
        UpdatePendingIndicators();
        ShowStatus(standard
                ? "已暂存 F1-F12 直出标准功能键（媒体键可移至 Fn1 层）。"
                : "已暂存 Mac F1-F12 出厂多媒体键。",
            InfoBarSeverity.Informational);
    }

    private bool EnsureEditable()
    {
        if (!_transport.CanWriteEEPROM())
        {
            ShowStatus("无线模式严格只读：请连接 USB-C 有线后再改键。", InfoBarSeverity.Warning);
            return false;
        }
        if (!_activeLayer.IsWritable)
        {
            ShowStatus($"「{_activeLayer.Title}」为固件只读层，请切换到 Base / Fn1 / Fn2。",
                InfoBarSeverity.Warning);
            return false;
        }
        return true;
    }

    private void AssignRecordByKeyId(string keyId, Hi98KeyBindingRecord record)
    {
        var spec = Hi98CLayout.KeyById.GetValueOrDefault(keyId);
        if (spec == null) return;
        PendingFor(_activeLayer)[SlotFor(spec)] = record;
    }

    // ── 键盘画布 ───────────────────────────────────────────────────────────────

    private void BuildCanvases()
    {
        KeymapCanvas.Children.Clear();
        RgbCanvas.Children.Clear();
        _keymapCells.Clear();
        _rgbCells.Clear();

        foreach (var spec in Hi98CLayout.Keys)
        {
            var keymapCell = CreateKeycap(spec, interactive: true);
            KeymapCanvas.Children.Add(keymapCell.Box!);
            _keymapCells[spec.Id] = keymapCell;

            var rgbCell = CreateKeycap(spec, interactive: false);
            RgbCanvas.Children.Add(rgbCell.Box!);
            _rgbCells[spec.Id] = rgbCell;
        }
    }

    private KeycapCell CreateKeycap(Hi98KeySlotSpec spec, bool interactive)
    {
        double left = spec.X - Hi98CLayout.CanvasOriginX;
        double top = spec.Y - Hi98CLayout.CanvasOriginY;

        var label = new TextBlock
        {
            Text = spec.Label,
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.FromArgb(242, 255, 255, 255)),
        };
        var content = new StackPanel
        {
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(label);

        TextBlock? subLabel = null;
        if (!string.IsNullOrWhiteSpace(spec.SubLabel))
        {
            subLabel = new TextBlock
            {
                Text = spec.SubLabel,
                FontSize = 7,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            };
            content.Children.Add(subLabel);
        }

        var box = new Border
        {
            Width = spec.Width,
            Height = spec.Height,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Background = new SolidColorBrush(Color.FromRgb(35, 40, 51)),
            Child = content,
            Tag = spec,
        };
        ToolTip.SetTip(box, $"{spec.Label} · 矩阵 #{SlotFor(spec)}");
        Canvas.SetLeft(box, left);
        Canvas.SetTop(box, top);

        if (interactive)
        {
            box.PointerPressed += OnKeymapKeyPressed;
        }
        else
        {
            box.PointerPressed += OnRgbKeyPressed;
        }

        return new KeycapCell { Box = box, Label = label, SubLabel = subLabel };
    }

    private void OnKeymapKeyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: Hi98KeySlotSpec spec }) return;
        SelectKey(spec);
        e.Handled = true;
    }

    private void SelectKey(Hi98KeySlotSpec spec)
    {
        _selectedKeyId = spec.Id;
        RenderKeymap();
        RefreshInspector();
    }

    private Hi98KeySlotSpec? SelectedSpec
        => _selectedKeyId == null ? null : Hi98CLayout.KeyById.GetValueOrDefault(_selectedKeyId);

    private void RenderKeymap()
    {
        var accent = new SolidColorBrush(Color.FromRgb(0, 120, 215));
        var pendingBrush = new SolidColorBrush(Color.FromRgb(34, 211, 238));
        var fnBrush = new SolidColorBrush(Color.FromArgb(190, 251, 146, 60));
        var idleBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        var dimText = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        var normalText = new SolidColorBrush(Color.FromArgb(242, 255, 255, 255));

        var pending = PendingFor(_activeLayer);
        foreach (var spec in Hi98CLayout.Keys)
        {
            if (!_keymapCells.TryGetValue(spec.Id, out var cell) || cell.Box == null || cell.Label == null)
                continue;

            var record = GetDisplayRecord(spec);
            int slot = SlotFor(spec);
            bool isSelected = string.Equals(_selectedKeyId, spec.Id, StringComparison.Ordinal);
            bool isPending = pending.ContainsKey(slot);
            bool isFn = record.IsFnKey;
            bool empty = record.IsEmpty;

            string label = _activeLayer.IsBaseLayer ? record.DisplayLabel : (empty ? spec.Label : record.DisplayLabel);
            if (label.Length > 10) label = label[..10];
            cell.Label.Text = label;
            cell.Label.FontWeight = isSelected || isFn ? FontWeight.Bold : FontWeight.Medium;
            cell.Label.Foreground = empty && !_activeLayer.IsBaseLayer ? dimText : normalText;

            if (cell.SubLabel != null)
                cell.SubLabel.IsVisible = _activeLayer.IsBaseLayer;

            cell.Box.BorderThickness = new Thickness(isSelected ? 2 : 1);
            cell.Box.BorderBrush = isSelected ? accent : isPending ? pendingBrush : isFn ? fnBrush : idleBrush;
            cell.Box.Background = new SolidColorBrush(isSelected
                ? Color.FromRgb(30, 58, 95)
                : (isFn ? Color.FromRgb(48, 38, 26) : Color.FromRgb(35, 40, 51)));
        }
    }

    private void RenderRgbCanvas()
    {
        var idleBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        foreach (var spec in Hi98CLayout.Keys)
        {
            if (!_rgbCells.TryGetValue(spec.Id, out var cell) || cell.Box == null) continue;
            int slot = SlotFor(spec);
            if (_perKeyColors.TryGetValue(slot, out var color))
            {
                var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
                cell.Box.Background = brush;
                cell.Box.BorderBrush = color.IsBlack ? idleBrush : brush;
            }
            else
            {
                cell.Box.Background = new SolidColorBrush(Color.FromRgb(30, 34, 43));
                cell.Box.BorderBrush = idleBrush;
            }
        }
    }

    private void OnRgbKeyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: Hi98KeySlotSpec spec }) return;
        PaintKey(spec, _brush);
        e.Handled = true;
    }

    private void OnRgbPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(RgbCanvas).Properties.IsLeftButtonPressed) return;
        _rgbPointerDown = true;
        e.Pointer.Capture(RgbCanvas);
        PaintAt(e.GetPosition(RgbCanvas));
    }

    private void OnRgbPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_rgbPointerDown) return;
        if (!e.GetCurrentPoint(RgbCanvas).Properties.IsLeftButtonPressed)
        {
            _rgbPointerDown = false;
            return;
        }
        PaintAt(e.GetPosition(RgbCanvas));
    }

    private void PaintAt(Point point)
    {
        double x = point.X + Hi98CLayout.CanvasOriginX;
        double y = point.Y + Hi98CLayout.CanvasOriginY;
        var spec = Hi98CLayout.Keys.FirstOrDefault(k =>
            x >= k.X && x <= k.X + k.Width && y >= k.Y && y <= k.Y + k.Height);
        if (spec != null) PaintKey(spec, _brush);
    }

    private void PaintKey(Hi98KeySlotSpec spec, Hi98RgbColor color)
    {
        _perKeyColors[SlotFor(spec)] = color;
        if (_rgbCells.TryGetValue(spec.Id, out var cell) && cell.Box != null)
        {
            var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            cell.Box.Background = brush;
            cell.Box.BorderBrush = color.IsBlack
                ? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
                : brush;
        }
        LiveStatusText.Text = $"已涂色 {_perKeyColors.Count} 键";
    }

    private void PaintAll(Hi98RgbColor color)
    {
        foreach (var spec in Hi98CLayout.Keys)
            _perKeyColors[SlotFor(spec)] = color;
        RenderRgbCanvas();
        LiveStatusText.Text = color.IsBlack ? "已全部熄灭" : $"已填充 {_perKeyColors.Count} 键";
    }

    private void PaintZone(Hi98KeyZone zone, Hi98RgbColor color)
    {
        foreach (var spec in Hi98CLayout.Keys.Where(k => k.Zone == zone))
            _perKeyColors[SlotFor(spec)] = color;
        RenderRgbCanvas();
        LiveStatusText.Text = $"已填充区域：{zone.RawValue}";
    }

    // ── 键码目录与检查器 ───────────────────────────────────────────────────────

    private void BuildComboKeys()
    {
        ComboKeyCombo.Items.Clear();
        var seen = new HashSet<byte>();
        foreach (var option in Hi98KeycodeCatalog.AllOptions)
        {
            if (option.Record.SingleHidUsageOrFn is not { } usage) continue;
            if (usage is 0xFF or 0x00 || usage >= 0xE0) continue;
            if (!seen.Add(usage)) continue;
            ComboKeyCombo.Items.Add(new ComboBoxItem { Content = option.Label, Tag = usage });
        }
        if (ComboKeyCombo.Items.Count > 0) ComboKeyCombo.SelectedIndex = 0;
    }

    private void BuildCategoryButtons()
    {
        CategoryPanel.Children.Clear();
        CategoryPanel.Children.Add(CreateCategoryButton("全部", null));
        foreach (var category in Hi98KeycodeOptionCategory.All)
            CategoryPanel.Children.Add(CreateCategoryButton(category.RawValue, category));
    }

    private Button CreateCategoryButton(string title, Hi98KeycodeOptionCategory? category)
    {
        var btn = new Button { Content = title, Classes = { "pill" }, Margin = new Thickness(0, 0, 6, 6) };
        if (_selectedCategory == category) SetActive(btn, true);
        btn.Click += (_, _) =>
        {
            _selectedCategory = category;
            foreach (var child in CategoryPanel.Children.OfType<Button>())
                SetActive(child, string.Equals(child.Content as string, title, StringComparison.Ordinal));
            BuildKeycodeOptions();
        };
        return btn;
    }

    private void BuildKeycodeOptions()
    {
        KeycodeOptionsPanel.Children.Clear();
        string query = (SearchBox.Text ?? string.Empty).Trim();
        foreach (var option in Hi98KeycodeCatalog.AllOptions)
        {
            if (_selectedCategory != null && option.Category != _selectedCategory) continue;
            if (query.Length > 0
                && !option.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !option.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

            var content = new StackPanel { Spacing = 1 };
            content.Children.Add(new TextBlock
            {
                Text = option.Label,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(option.Detail) ? option.Record.HexString : option.Detail,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromArgb(160, 148, 163, 184)),
            });

            var btn = new Button
            {
                Content = content,
                Width = 132,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 5),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Tag = option,
            };
            btn.Click += (_, _) =>
            {
                if (btn.Tag is Hi98KeycodeOption opt) AssignRecord(opt.Record, opt.Label);
            };
            KeycodeOptionsPanel.Children.Add(btn);
        }

        bool editable = _transport.CanWriteEEPROM() && _activeLayer.IsWritable && !_busy;
        foreach (var btn in KeycodeOptionsPanel.Children.OfType<Button>())
            btn.IsEnabled = editable;
    }

    private void AssignRecord(Hi98KeyBindingRecord record, string label)
    {
        if (!EnsureEditable()) return;
        var spec = SelectedSpec;
        if (spec == null)
        {
            ShowStatus("请先在画布上选择一个键位。", InfoBarSeverity.Warning);
            return;
        }
        PendingFor(_activeLayer)[SlotFor(spec)] = record;
        AppendLog($"暂存「{spec.Label}」→ {label} ({record.HexString})");
        RenderKeymap();
        RefreshInspector();
        UpdatePendingIndicators();
        UpdateActionAvailability();
    }

    private void RefreshInspector()
    {
        var spec = SelectedSpec;
        if (spec == null)
        {
            SelectedKeyText.Text = "未选择键位";
            BindingText.Text = "-";
            HexText.Text = "-";
            MatrixText.Text = "-";
            FnWarningText.IsVisible = false;
            return;
        }

        var record = GetDisplayRecord(spec);
        SelectedKeyText.Text = string.IsNullOrWhiteSpace(spec.SubLabel)
            ? spec.Label
            : $"{spec.Label} · {spec.SubLabel}";
        BindingText.Text = $"{record.DisplayLabel}（{_activeLayer.Title}）";
        HexText.Text = $"[{record.HexString}]";
        MatrixText.Text = $"矩阵 #{SlotFor(spec)} · 键位 K{spec.KeyCodeIndex} · {spec.Zone.RawValue}";
        FnWarningText.IsVisible = _activeLayer.IsBaseLayer && record.IsFnKey;
    }

    private void RevertSelectedKey()
    {
        if (!EnsureEditable()) return;
        var spec = SelectedSpec;
        if (spec == null) return;
        PendingFor(_activeLayer)[SlotFor(spec)] = Hi98CLayout.DefaultRecordFor(spec, _activeLayer);
        RenderKeymap();
        RefreshInspector();
        UpdatePendingIndicators();
        ShowStatus($"已将「{spec.Label}」恢复为出厂默认（待写入）。", InfoBarSeverity.Informational);
    }

    private void ApplyCustomCombo()
    {
        if (!EnsureEditable()) return;
        var spec = SelectedSpec;
        if (spec == null || ComboKeyCombo.SelectedItem is not ComboBoxItem { Tag: byte usage })
        {
            ShowStatus("请先选择键位与主键。", InfoBarSeverity.Warning);
            return;
        }

        var mods = Hi98ModifierMask.None;
        if (CtrlModChk.IsChecked == true) mods |= Hi98ModifierMask.LeftControl;
        if (ShiftModChk.IsChecked == true) mods |= Hi98ModifierMask.LeftShift;
        if (OptModChk.IsChecked == true) mods |= Hi98ModifierMask.LeftAlt;
        if (CmdModChk.IsChecked == true) mods |= Hi98ModifierMask.LeftCommand;

        var record = Hi98KeyBindingRecord.Key(usage, mods);
        PendingFor(_activeLayer)[SlotFor(spec)] = record;
        RenderKeymap();
        RefreshInspector();
        UpdatePendingIndicators();
        ShowStatus($"已暂存组合键 {record.DisplayLabel} →「{spec.Label}」。", InfoBarSeverity.Informational);
    }

    private void UpdatePendingIndicators()
    {
        int currentCount = PendingCount;
        int totalCount = TotalPendingCount;
        CommitKeymapBtn.Content = $"写入改键 ({totalCount})";
        PendingCountText.Text = totalCount == currentCount
            ? $"待写入 {totalCount} 处"
            : $"当前层 {currentCount} 处 · 全层共待写入 {totalCount} 处";
        UpdateActionAvailability();
    }

    private async Task CommitCurrentLayerAsync()
    {
        if (!_transport.CanWriteEEPROM())
        {
            ShowStatus("安全保护：仅 USB-C 有线模式允许写入板载改键。", InfoBarSeverity.Warning);
            return;
        }

        var dirtyLayers = Hi98KeyLayer.WritableLayers
            .Where(l => PendingFor(l).Count > 0)
            .Select(l => (Layer: l, Updates: new Dictionary<int, Hi98KeyBindingRecord>(PendingFor(l))))
            .ToList();
        if (dirtyLayers.Count == 0) return;

        SetBusy(true);
        try
        {
            var results = await Task.Run(() =>
            {
                var list = new List<(Hi98KeyLayer Layer, Hi98WriteOutcome Outcome)>(dirtyLayers.Count);
                foreach (var (layer, updates) in dirtyLayers)
                {
                    var outcome = LeobogProvider.WriteKeymapLayerWired(
                        _deviceId, layer, updates, $"写入「{layer.Title}」改键", Backup);
                    list.Add((layer, outcome));
                    if (!outcome.Success) break;
                }
                return list;
            });
            if (!IsLoaded) return;

            bool allOk = results.All(r => r.Outcome.Success);
            foreach (var (layer, outcome) in results)
            {
                AppendLog($"写入「{layer.Title}」：{outcome.Message}");
                if (outcome.Success)
                {
                    if (layer.RawValue == _activeLayer.RawValue && outcome.Data != null)
                        ApplyPageToCurrent(outcome.Data);
                    PendingFor(layer).Clear();
                }
            }

            if (results.Count > 0)
            {
                var last = results[^1];
                string summary = allOk && results.Count > 1
                    ? $"已写入 {results.Count} 个可编程层并完成 400ms 回读校验。"
                    : last.Outcome.Message;
                ShowStatus(summary, allOk ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            }

            RenderKeymap();
            RefreshInspector();
            UpdatePendingIndicators();
            BuildLayerButtons();
        }
        catch (Exception ex)
        {
            if (IsLoaded) ShowStatus($"写入改键失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ── RGB 灯光 ───────────────────────────────────────────────────────────────

    private void BuildEffectButtons()
    {
        EffectButtonsPanel.Children.Clear();
        foreach (var effect in Hi98OnboardLightingEffect.AllEffects)
        {
            byte id = effect.Id;
            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock
            {
                Text = effect.Name,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"hw {effect.Id}",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(160, 148, 163, 184)),
            });
            var btn = new Button
            {
                Content = content,
                Width = 158,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(9, 6),
                Tag = id,
            };
            btn.Click += (_, _) => SelectEffect(id);
            ToolTip.SetTip(btn, effect.Description);
            EffectButtonsPanel.Children.Add(btn);
        }
        SelectEffect(_selectedEffectId);
    }

    private void SelectEffect(byte id)
    {
        _selectedEffectId = id;
        var effect = Hi98OnboardLightingEffect.Find(id);
        EffectDescText.Text = effect == null ? $"灯效 0x{id:X2}" : $"{effect.Name} · {effect.Description}";

        _binding = true;
        try
        {
            if (effect != null)
            {
                SpeedSlider.IsEnabled = effect.SupportsSpeed;
                RainbowBtn.IsEnabled = effect.SupportsColor;
                foreach (var btn in PalettePanel.Children.OfType<Button>())
                    btn.IsEnabled = effect.SupportsColor;
            }
            if (_ledConfig?.SlotForEffect(id) is { } slot)
            {
                _selectedBrightness = slot.Brightness;
                _selectedSpeed = slot.Speed;
                _selectedColorIndex = slot.ColorIndex;
                BrightnessSlider.Value = slot.Brightness;
                SpeedSlider.Value = slot.Speed;
            }
        }
        finally
        {
            _binding = false;
        }

        BrightnessValueText.Text = $"亮度：{_selectedBrightness} / 9";
        SpeedValueText.Text = $"速度：{_selectedSpeed} / 4";

        foreach (var btn in EffectButtonsPanel.Children.OfType<Button>())
            SetActive(btn, btn.Tag is byte value && value == id);
        UpdateColorIndicators();
    }

    private void BuildPalette()
    {
        PalettePanel.Children.Clear();
        for (int i = 0; i < PaletteIndices.Length; i++)
        {
            int index = PaletteIndices[i];
            var btn = new Button
            {
                Width = 28,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(PaletteColors[i]),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                Tag = index,
                Content = string.Empty,
            };
            ToolTip.SetTip(btn, PaletteNames[i]);
            btn.Click += (_, _) => SelectColorIndex(index);
            PalettePanel.Children.Add(btn);
        }
        UpdateColorIndicators();
    }

    private void SelectColorIndex(int index)
    {
        _selectedColorIndex = index;
        UpdateColorIndicators();
        if (_ledConfig?.SlotForEffect(_selectedEffectId) is { } slot)
        {
            _selectedBrightness = slot.Brightness;
            _selectedSpeed = slot.Speed;
        }
        ShowStatus(index == 7 ? "已选择七彩混光循环 (ColorIndex = 7)。" : $"已选择调色板颜色 #{index}。",
            InfoBarSeverity.Informational);
    }

    private void UpdateColorIndicators()
    {
        foreach (var btn in PalettePanel.Children.OfType<Button>())
        {
            bool active = btn.Tag is int index && index == _selectedColorIndex;
            btn.BorderThickness = new Thickness(active ? 3 : 1);
            btn.BorderBrush = new SolidColorBrush(active
                ? Color.FromRgb(0, 120, 215)
                : Color.FromArgb(90, 255, 255, 255));
        }
        SetActive(RainbowBtn, _selectedColorIndex == 7);
    }

    private void ApplyLedConfigToUi()
    {
        if (_ledConfig == null) return;
        var effect = Hi98OnboardLightingEffect.Find(_ledConfig.EffectHardwareID);
        _selectedEffectId = effect?.Id ?? _ledConfig.EffectHardwareID;
        SelectEffect(_selectedEffectId);

        _binding = true;
        try
        {
            int sleep = Math.Clamp(_ledConfig.SleepTimerSeconds, 60, 1200);
            SelectComboByTag(SleepCombo, NearestSleep(sleep));
            SelectComboByIndex(OsModeCombo, (int)_ledConfig.OsMode);
            SelectComboByIndex(SideLightCombo, _ledConfig.SideLightModeRaw);
            WinLockSwitch.IsChecked = _ledConfig.WinKeyLocked;
        }
        finally
        {
            _binding = false;
        }
    }

    private async Task SaveOnboardLightingAsync()
    {
        if (!_transport.CanWriteEEPROM())
        {
            ShowStatus("安全保护：灯效写入仅支持 USB-C 有线模式；无线可先实时预览。", InfoBarSeverity.Warning);
            return;
        }
        if (_ledConfig == null)
        {
            ShowStatus("请先点击「从键盘同步」读取板载灯光配置。", InfoBarSeverity.Warning);
            return;
        }
        if (_busy) return;

        var next = new Hi98LedConfigBlock(_ledConfig.RawBytes);
        next.EffectHardwareID = _selectedEffectId;
        next.IsPerKeyCustomMode = _selectedEffectId == Hi98OnboardLightingEffect.CustomPerKeyHardwareID;
        if (_selectedEffectId > 0)
        {
            next.SetSlot(new Hi98OnboardEffectSlot(_selectedBrightness, _selectedSpeed, (byte)_selectedColorIndex),
                _selectedEffectId);
        }

        SetBusy(true);
        try
        {
            var outcome = await Task.Run(() => LeobogProvider.WriteLedConfigWired(
                _deviceId, next, $"保存板载灯效 {_selectedEffectId:X2}", Backup));
            if (!IsLoaded) return;
            ShowStatus(outcome.Message, outcome.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            AppendLog($"保存灯效：{outcome.Message}");
            if (outcome.Success && outcome.LedConfig != null)
            {
                _ledConfig = outcome.LedConfig;
                ApplyLedConfigToUi();
            }
        }
        catch (Exception ex)
        {
            if (IsLoaded) ShowStatus($"保存灯效失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BuildSwatches()
    {
        SwatchPanel.Children.Clear();
        foreach (var swatch in QuickSwatches)
        {
            var color = swatch;
            var btn = new Button
            {
                Width = 26,
                Height = 24,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                Tag = color,
                Content = string.Empty,
            };
            ToolTip.SetTip(btn, color.IsBlack ? "熄灭橡皮擦 (#000000)" : color.HexString);
            btn.Click += (_, _) =>
            {
                _brush = color;
                BrushHexBox.Text = color.HexString;
                foreach (var child in SwatchPanel.Children.OfType<Button>())
                {
                    bool active = child.Tag is Hi98RgbColor c && c == color;
                    child.BorderThickness = new Thickness(active ? 2 : 1);
                    child.BorderBrush = new SolidColorBrush(active
                        ? Color.FromRgb(0, 120, 215)
                        : Color.FromArgb(90, 255, 255, 255));
                }
            };
            SwatchPanel.Children.Add(btn);
        }
        BrushHexBox.TextChanged += (_, _) =>
        {
            if (_binding) return;
            _brush = Hi98RgbColor.FromHex(BrushHexBox.Text ?? string.Empty);
        };
    }

    private void BuildZoneButtons()
    {
        ZonePanel.Children.Clear();
        foreach (var zone in Hi98KeyZone.All)
        {
            var z = zone;
            var btn = new Button { Content = z.RawValue, Classes = { "pill" }, Margin = new Thickness(0, 0, 6, 6) };
            btn.Click += (_, _) => PaintZone(z, _brush);
            ZonePanel.Children.Add(btn);
        }
        var allBtn = new Button { Content = "全盘填充", Classes = { "pill" }, Margin = new Thickness(0, 0, 6, 6) };
        allBtn.Click += (_, _) => PaintAll(_brush);
        ZonePanel.Children.Add(allBtn);
        var clearBtn = new Button { Content = "全部熄灭", Classes = { "pill" } };
        clearBtn.Click += (_, _) => PaintAll(Hi98RgbColor.Black);
        ZonePanel.Children.Add(clearBtn);
    }

    private void ToggleLiveStreaming()
    {
        if (_liveStreaming)
        {
            StopLiveStreaming(silent: false);
            return;
        }
        if (!_transport.CanStreamLiveRGB())
        {
            ShowStatus("当前通道不支持实时 RGB 预览（蓝牙模式请改用有线或 2.4G）。", InfoBarSeverity.Warning);
            return;
        }

        _liveStreaming = true;
        LivePreviewBtn.Content = "停止实时预览";
        LiveStatusText.Text = "实时预览中（20 FPS）";
        if (_liveTimer == null)
        {
            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LiveStreamIntervalMs) };
            _liveTimer.Tick += OnLiveTick;
        }
        _liveTimer.Start();
        AppendLog("开启 20FPS 实时 RGB 预览。");
    }

    private void StopLiveStreaming(bool silent)
    {
        if (!_liveStreaming) return;
        _liveStreaming = false;
        _liveTimer?.Stop();
        if (LivePreviewBtn != null) LivePreviewBtn.Content = "开启 20FPS 实时预览";
        if (LiveStatusText != null) LiveStatusText.Text = "实时预览已停止";
        if (!silent) AppendLog("停止实时 RGB 预览。");
    }

    private void OnLiveTick(object? sender, EventArgs e)
    {
        if (!_liveStreaming || _busy || _liveSending || !IsLoaded) return;
        var snapshot = new Dictionary<int, Hi98RgbColor>(_perKeyColors);
        _liveSending = true;
        _ = Task.Run(() =>
        {
            try { LeobogProvider.SendLiveRgbFrame(_deviceId, snapshot); }
            catch { }
            finally { _liveSending = false; }
        });
    }

    private async Task BurnStaticRgbAsync()
    {
        if (!_transport.CanWriteEEPROM())
        {
            ShowStatus("安全保护：单键静态配色烧录仅支持 USB-C 有线模式。", InfoBarSeverity.Warning);
            return;
        }
        if (_busy) return;

        var colors = new Dictionary<int, Hi98RgbColor>(_perKeyColors);
        SetBusy(true);
        try
        {
            var outcome = await Task.Run(() => LeobogProvider.WritePerKeyRgbWired(
                _deviceId, colors, "烧录单键静态配色", Backup));
            if (!IsLoaded) return;
            ShowStatus(outcome.Message, outcome.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            AppendLog($"烧录单键配色：{outcome.Message}");
            if (outcome.Success && outcome.LedConfig != null)
            {
                _ledConfig = outcome.LedConfig;
                ApplyLedConfigToUi();
            }
        }
        catch (Exception ex)
        {
            if (IsLoaded) ShowStatus($"烧录单键配色失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ── 硬件设置 ───────────────────────────────────────────────────────────────

    private void BuildHardwareCombos()
    {
        SleepCombo.Items.Clear();
        for (int sec = 60; sec <= 1200; sec += 30)
        {
            string text = sec < 60 ? $"{sec} 秒" : sec % 60 == 0 ? $"{sec / 60} 分钟（{sec} 秒）" : $"{sec} 秒";
            SleepCombo.Items.Add(new ComboBoxItem { Content = text, Tag = sec });
        }
        if (SleepCombo.Items.Count > 0) SleepCombo.SelectedIndex = 0;

        OsModeCombo.Items.Clear();
        foreach (Hi98OsMode mode in Enum.GetValues<Hi98OsMode>())
        {
            OsModeCombo.Items.Add(new ComboBoxItem { Content = mode.Title(), Tag = (int)mode });
        }
        OsModeCombo.SelectedIndex = 0;

        SideLightCombo.Items.Clear();
        foreach (Hi98SideLightMode mode in Enum.GetValues<Hi98SideLightMode>())
        {
            SideLightCombo.Items.Add(new ComboBoxItem { Content = $"{mode.Title()}（{(int)mode}）", Tag = (int)mode });
        }
        SideLightCombo.SelectedIndex = 0;
    }

    private static int NearestSleep(int seconds)
    {
        int clamped = Math.Clamp(seconds, 60, 1200);
        int stepped = (int)(Math.Round(clamped / 30.0) * 30);
        return Math.Clamp(stepped, 60, 1200);
    }

    private static void SelectComboByTag(ComboBox combo, int tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem { Tag: int value } && value == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private static void SelectComboByIndex(ComboBox combo, int index)
    {
        if (index >= 0 && index < combo.Items.Count)
            combo.SelectedIndex = index;
    }

    private async Task SaveHardwareSettingsAsync()
    {
        if (!_transport.CanWriteEEPROM())
        {
            ShowStatus("安全保护：硬件参数仅支持 USB-C 有线保存。", InfoBarSeverity.Warning);
            return;
        }
        if (_ledConfig == null)
        {
            ShowStatus("请先点击「从键盘同步」读取板载配置。", InfoBarSeverity.Warning);
            return;
        }
        if (_busy) return;

        int sleep = (SleepCombo.SelectedItem as ComboBoxItem)?.Tag is int sec ? sec : 300;
        sleep = Math.Clamp(sleep, 60, 1200);
        var osMode = (Hi98OsMode)((OsModeCombo.SelectedItem as ComboBoxItem)?.Tag is int os ? os : 0);
        var sideMode = (Hi98SideLightMode)((SideLightCombo.SelectedItem as ComboBoxItem)?.Tag is int side ? side : 0);
        bool winLock = WinLockSwitch.IsChecked == true;

        var next = new Hi98LedConfigBlock(_ledConfig.RawBytes);
        next.SleepTimerSeconds = sleep;
        next.OsMode = osMode;
        next.SideLightMode = sideMode;
        next.WinKeyLocked = winLock;

        SetBusy(true);
        try
        {
            var outcome = await Task.Run(() => LeobogProvider.WriteLedConfigWired(
                _deviceId, next, "保存硬件设置", Backup));
            if (!IsLoaded) return;
            ShowStatus(outcome.Message, outcome.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            AppendLog($"保存硬件设置：{outcome.Message}");
            if (outcome.Success && outcome.LedConfig != null)
            {
                _ledConfig = outcome.LedConfig;
                ApplyLedConfigToUi();
                UpdateMacBanner();
            }
        }
        catch (Exception ex)
        {
            if (IsLoaded) ShowStatus($"保存硬件设置失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ── 备份与恢复 ─────────────────────────────────────────────────────────────

    private async Task RefreshBackupsAsync()
    {
        try
        {
            var list = await Task.Run(() =>
            {
                try { return Backup.ListBackups(); }
                catch { return []; }
            });
            if (!IsLoaded) return;
            _backups.Clear();
            _backups.AddRange(list);
            BuildBackupList();
        }
        catch (Exception ex)
        {
            Diag.Log($"LeobogHi98View.RefreshBackupsAsync 异常: {ex.Message}");
        }
    }

    private void BuildBackupList()
    {
        BackupList.Children.Clear();
        BackupEmptyText.IsVisible = _backups.Count == 0;
        bool writable = _transport.CanWriteEEPROM();

        foreach (var (path, snapshot) in _backups.Take(12))
        {
            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(snapshot.Reason) ? "（未命名快照）" : snapshot.Reason,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
            });
            var metaParts = new List<string> { snapshot.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") };
            if (!string.IsNullOrWhiteSpace(snapshot.ModelName)) metaParts.Add(snapshot.ModelName);
            if (snapshot.Layers.Count > 0) metaParts.Add($"键位层 ×{snapshot.Layers.Count}");
            if (snapshot.LedConfig != null) metaParts.Add("LED 配置");
            if (snapshot.PerKeyRgb != null) metaParts.Add("单键 RGB");
            info.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", metaParts),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(160, 148, 163, 184)),
            });

            var exportBtn = new Button { Content = "导出", Classes = { "pill" }, Tag = snapshot, Margin = new Thickness(0, 0, 6, 0) };
            exportBtn.Click += async (_, _) => await ExportSnapshotAsync(snapshot);

            var restoreBtn = new Button
            {
                Content = "有线回滚",
                Classes = { "pill", "accent" },
                Tag = snapshot,
                IsEnabled = writable && !_busy,
            };
            restoreBtn.Click += async (_, _) => await RestoreSnapshotAsync(snapshot);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            row.Children.Add(info);
            Grid.SetColumn(exportBtn, 1);
            row.Children.Add(exportBtn);
            Grid.SetColumn(restoreBtn, 2);
            row.Children.Add(restoreBtn);

            BackupList.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 7),
                Child = row,
            });
        }
    }

    private async Task ExportSnapshotAsync(Hi98EepromBackupSnapshot snapshot)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is not { } storage) return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出 Hi98 EEPROM 快照",
                SuggestedFileName = $"hi98-backup-{snapshot.CreatedAt.LocalDateTime:yyyyMMdd-HHmmss}.json",
                FileTypeChoices =
                [
                    new FilePickerFileType("JSON 快照") { Patterns = ["*.json"] },
                ],
            });
            string? path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            await Task.Run(() => Backup.ExportBackup(snapshot, path));
            if (!IsLoaded) return;
            ShowStatus($"已导出快照到 {Path.GetFileName(path)}。", InfoBarSeverity.Success);
            AppendLog($"导出备份：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            if (IsLoaded) ShowStatus($"导出快照失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task RestoreSnapshotAsync(Hi98EepromBackupSnapshot snapshot)
    {
        if (!_transport.CanWriteEEPROM())
        {
            ShowStatus("安全保护：仅 USB-C 有线模式允许恢复 EEPROM 快照。", InfoBarSeverity.Warning);
            return;
        }
        if (_busy) return;

        bool confirmed = await ConfirmAsync("有线回滚快照",
            $"将把「{snapshot.Reason}」写回键盘板载闪存，当前配置会被覆盖。是否继续？", "开始回滚");
        if (!confirmed) return;

        SetBusy(true);
        try
        {
            var outcome = await Task.Run(() => LeobogProvider.RestoreSnapshotWired(_deviceId, snapshot, Backup));
            if (!IsLoaded) return;
            ShowStatus(outcome.Message, outcome.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            AppendLog($"恢复快照：{outcome.Message}");
            if (outcome.Success)
            {
                await SyncFromKeyboardAsync();
                await RefreshBackupsAsync();
            }
        }
        catch (Exception ex)
        {
            if (IsLoaded) ShowStatus($"恢复快照失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ImportAndRestoreAsync()
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is not { } storage) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入 Hi98 EEPROM 快照",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("JSON 快照") { Patterns = ["*.json"] },
                ],
            });
            string? path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            var snapshot = await Task.Run(() => Backup.ImportBackup(path));
            if (!IsLoaded) return;
            AppendLog($"导入快照：{Path.GetFileName(path)}");
            await RestoreSnapshotAsync(snapshot);
        }
        catch (Exception ex)
        {
            if (IsLoaded) ShowStatus($"导入快照失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task RestoreFnAsync()
    {
        if (!_transport.CanWriteEEPROM())
        {
            ShowStatus("安全保护：紧急恢复 Fn 键需要 USB-C 有线模式。", InfoBarSeverity.Warning);
            return;
        }
        if (_busy) return;

        int fnSlot = SlotFor(Hi98CLayout.KeyById["fn"]);
        bool confirmed = await ConfirmAsync("紧急恢复 Fn 键",
            $"仅把默认层物理 Fn 槽位（矩阵 #{fnSlot}）修复为 Fn [0D 00 00 00]，不影响其他键位。是否继续？",
            "恢复 Fn 键");
        if (!confirmed) return;

        SetBusy(true);
        try
        {
            var outcome = await Task.Run(() => LeobogProvider.SurgicalRestoreFnWired(_deviceId, fnSlot, Backup));
            if (!IsLoaded) return;
            ShowStatus(outcome.Message, outcome.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            AppendLog($"紧急恢复 Fn：{outcome.Message}");
            if (outcome.Success) await SyncFromKeyboardAsync();
        }
        catch (Exception ex)
        {
            if (IsLoaded) ShowStatus($"恢复 Fn 键失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task OpenBackupFolderAsync()
    {
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher != null)
                await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Backup.BackupsDirectory));
        }
        catch (Exception ex)
        {
            ShowStatus($"打开备份目录失败：{ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ── 诊断 ───────────────────────────────────────────────────────────────────

    private async Task RefreshDiagnosticsAsync()
    {
        InputMonitorText.Text = OperatingSystem.IsMacOS()
            ? $"macOS 输入监控：{MacIOKitHid.CheckInputMonitoringAccess()}。打开 520B Feature / 20B Output 配置通道需要该权限。"
            : "Windows：HID 直连访问，无需额外的输入监控授权。";

        CalibrationText.Text =
            $"矩阵校准源：{_calibration.SourceDescription} · 匹配 {_calibration.MatchedCount}/{Hi98CLayout.Keys.Count} · " +
            $"Fn 槽位：{( _calibration.DetectedFnMatrixIndices.Count > 0 ? string.Join(", ", _calibration.DetectedFnMatrixIndices) : "未知")}";

        if (string.IsNullOrWhiteSpace(_deviceId))
        {
            InterfaceList.Children.Clear();
            return;
        }

        try
        {
            var interfaces = await Task.Run(() => LeobogProvider.InspectInterfaces(_deviceId));
            if (!IsLoaded) return;

            InterfaceList.Children.Clear();
            if (interfaces.Count == 0)
            {
                InterfaceList.Children.Add(new TextBlock
                {
                    Text = "未匹配到 LEOBOG HID 接口（键盘可能未连接或休眠）。",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(160, 148, 163, 184)),
                });
                return;
            }

            foreach (var iface in interfaces)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,90,150,150,*") };
                AddCell(row, 0, $"{iface.VendorId:X4}:{iface.ProductId:X4}");
                AddCell(row, 1, iface.Transport);
                AddCell(row, 2, $"0x{iface.UsagePage:X4}:0x{iface.Usage:X4}");
                AddCell(row, 3, $"{iface.MaxIn}/{iface.MaxOut}/{iface.MaxFeat}");
                AddCell(row, 4, $"{iface.Product} · {iface.OpenStatus}");

                InterfaceList.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(16, 128, 128, 128)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 5),
                    Child = row,
                });
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"LeobogHi98View.RefreshDiagnosticsAsync 异常: {ex.Message}");
        }
    }

    private static void AddCell(Grid row, int column, string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(200, 203, 213, 225)),
        };
        Grid.SetColumn(block, column);
        row.Children.Add(block);
    }
}
