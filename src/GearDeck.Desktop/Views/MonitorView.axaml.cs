using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GearDeck.Desktop.Services;
using GearDeck.Desktop.Windows;

namespace GearDeck.Desktop.Views;

/// <summary>
/// Avalonia 版系统监控页：12 项指标实时面板 + 任务栏双指标配置 + 桌面悬浮条开关。
/// </summary>
public partial class MonitorView : UserControl
{
    private static readonly FontFamily IconFont = Controls.UiBuilders.IconFont;
    private static IBrush ResolveBrush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var r) == true && r is IBrush b
            ? b
            : fallback;
    private readonly List<(string Id, Border Card, CheckBox Check, TextBlock Value, ProgressBar? Bar)> _rows = [];
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _binding = true;
    private bool _updating;

    public MonitorView()
    {
        InitializeComponent();
        WireEvents();
        LoadSettings();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _binding = false;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        App.MonitorEnabledChanged += OnExternalMonitorEnabledChanged;
        FloatingMonitorWindow.StateChanged += OnFloatingStateChanged;
        LoadSettings();
        OnFloatingStateChanged();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        App.MonitorEnabledChanged -= OnExternalMonitorEnabledChanged;
        FloatingMonitorWindow.StateChanged -= OnFloatingStateChanged;
        _liveTimer.Stop();
    }

    private void OnExternalMonitorEnabledChanged(bool _)
        => App.UiPost(() =>
        {
            bool previous = _binding;
            _binding = true;
            try
            {
                var s = App.Settings.Current;
                CollectionSwitch.IsChecked = s.MonitorCollectionEnabled;
                MonitorSwitch.IsChecked = s.MonitorEnabled;
                ApplyMonitorEnabledState();
                UpdateStatusText();
            }
            finally { _binding = previous; }
        });

    private void WireEvents()
    {
        CollectionSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            App.SetMonitorCollectionEnabled(CollectionSwitch.IsChecked == true);
            ApplyMonitorEnabledState();
            UpdateStatusText();
        };
        MonitorSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            if ((MonitorSwitch.IsChecked == true) != App.Settings.Current.MonitorEnabled)
                App.ToggleMonitor();
            bool prev = _binding;
            _binding = true;
            try { CollectionSwitch.IsChecked = App.Settings.Current.MonitorCollectionEnabled; }
            finally { _binding = prev; }
            ApplyMonitorEnabledState();
            UpdateStatusText();
        };
        HwInfoSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            App.SetHwInfoBridgeEnabled(HwInfoSwitch.IsChecked == true);
            UpdateStatusText();
        };
        TopMetricBox.SelectionChanged += OnTaskbarMetricChanged;
        BottomMetricBox.SelectionChanged += OnTaskbarMetricChanged;
        FloatingToggleBtn.Click += (_, _) =>
        {
            if (!App.Settings.Current.MonitorCollectionEnabled)
            {
                App.SetMonitorCollectionEnabled(true);
                bool prev = _binding;
                _binding = true;
                try { CollectionSwitch.IsChecked = true; }
                finally { _binding = prev; }
                ApplyMonitorEnabledState();
            }
            FloatingMonitorWindow.ShowOrToggle();
        };
        GoWidgetBtn.Click += (_, _) => MainWindow.Instance?.OpenWidgetPage();
        SelectAllMetricsBtn.Click += (_, _) =>
        {
            var allIds = DesktopMetricsEngine.AllMetrics
                .Where(m => !string.Equals(m.Id, "MEM_USED", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Id)
                .ToList();
            ApplyMetricSelectionPreset(allIds);
        };
        ResetMetricsBtn.Click += (_, _) => ApplyMetricSelectionPreset(DesktopMetricsEngine.DefaultFlyoutMetricIds);
        _liveTimer.Tick += (_, _) => UpdateLiveValues();
    }

    private void LoadSettings()
    {
        bool previous = _binding;
        _binding = true;
        try
        {
            App.Settings.EnsureDefaults();
            var s = App.Settings.Current;
            CollectionSwitch.IsChecked = s.MonitorCollectionEnabled;
            MonitorSwitch.IsChecked = s.MonitorEnabled;
            bool isWin = OperatingSystem.IsWindows();
            HwInfoSwitch.IsVisible = isWin;
            HwInfoSwitch.IsChecked = s.HwInfoBridgeEnabled;
            HwInfoStatusText.IsVisible = isWin;
            PopulateMetricCombo(TopMetricBox, s.MonitorTopMetric, allowNone: false);
            PopulateMetricCombo(BottomMetricBox, s.MonitorBottomMetric, allowNone: true);
            ApplyMonitorEnabledState();
            UpdateStatusText();
        }
        finally { _binding = previous; }
    }

    private void ApplyMonitorEnabledState()
    {
        bool collecting = App.Settings.Current.MonitorCollectionEnabled;
        MonitorSwitch.IsEnabled = collecting;
        HwInfoSwitch.IsEnabled = collecting;
        TopMetricBox.IsEnabled = collecting;
        BottomMetricBox.IsEnabled = collecting;
        FloatingToggleBtn.IsEnabled = collecting;
        MonitorDisabledNotice.IsVisible = !collecting;
        MetricsHeaderPanel.IsVisible = collecting;
        MetricsHost.IsVisible = collecting;

        if (!collecting)
        {
            _liveTimer.Stop();
            return;
        }

        if (_rows.Count == 0)
            BuildMetricCards();
        else
            UpdateLiveValues();
        if (!_liveTimer.IsEnabled)
            _liveTimer.Start();
    }

    private void BuildMetricCards()
    {
        var selected = GetSelectedMetricIdSet();
        var snap = DesktopMetricsEngine.Sample(ComputeSelectedScope(selected));
        MetricsHost.Children.Clear();
        _rows.Clear();

        foreach (var def in DesktopMetricsEngine.AllMetrics)
        {
            // MEM_USED 数值已在 RAM 卡片（如 75% · 12.0 / 16.0 GB）完整展示，监控页不重复造卡
            if (string.Equals(def.Id, "MEM_USED", StringComparison.OrdinalIgnoreCase))
                continue;

            bool isChecked = selected.Contains(def.Id);
            var card = new Border
            {
                Width = 306,
                Margin = new Thickness(0, 0, 10, 10),
                Opacity = isChecked ? 1.0 : 0.58,
                Background = ResolveBrush("CardBackgroundFillColorDefaultBrush", Brushes.Transparent),
                BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush", Brushes.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
            };
            var stack = new StackPanel { Spacing = 8 };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock
            {
                Text = def.Glyph,
                FontFamily = IconFont,
                FontSize = 18,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            var title = new StackPanel { Spacing = 1, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = def.FullName, FontWeight = FontWeight.SemiBold });
            Grid.SetColumn(title, 1);
            head.Children.Add(title);

            var check = new CheckBox
            {
                IsChecked = isChecked,
                MinWidth = 0,
                Padding = new Thickness(0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            ToolTip.SetTip(check, "勾选以实时采集并在悬浮窗显示");
            Grid.SetColumn(check, 2);
            head.Children.Add(check);
            stack.Children.Add(head);

            var value = new TextBlock
            {
                Text = isChecked ? DesktopMetricsEngine.FormatDetailed(def.Id, snap) : "已暂停采集",
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
            };
            stack.Children.Add(value);

            ProgressBar? bar = null;
            if (def.HasPercentageBar)
            {
                bar = new ProgressBar
                {
                    Maximum = 100,
                    Minimum = 0,
                    Value = isChecked ? (DesktopMetricsEngine.GetPercentage(def.Id, snap) ?? 0) : 0,
                    Height = 3,
                    MinHeight = 3,
                    IsVisible = isChecked && DesktopMetricsEngine.GetPercentage(def.Id, snap).HasValue,
                };
                stack.Children.Add(bar);
            }

            check.IsCheckedChanged += (_, _) => OnMetricCardCheckChanged(check);

            card.Child = stack;
            MetricsHost.Children.Add(card);
            _rows.Add((def.Id, card, check, value, bar));
        }
    }

    private void UpdateLiveValues()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var selected = GetSelectedMetricIdSet();
            var snap = DesktopMetricsEngine.Sample(ComputeSelectedScope(selected));
            foreach (var (id, card, _, text, bar) in _rows)
            {
                bool active = selected.Contains(id);
                card.Opacity = active ? 1.0 : 0.58;
                if (!active)
                {
                    text.Text = "已暂停采集";
                    if (bar != null) bar.IsVisible = false;
                    continue;
                }

                text.Text = DesktopMetricsEngine.FormatDetailed(id, snap);
                if (bar == null) continue;
                if (DesktopMetricsEngine.GetPercentage(id, snap) is { } pct)
                {
                    bar.IsVisible = true;
                    bar.Value = Math.Clamp(pct, 0, 100);
                }
                else
                {
                    bar.IsVisible = false;
                }
            }
            UpdateHwInfoStatusText(snap);
        }
        finally { _updating = false; }
    }

    private void OnMetricCardCheckChanged(CheckBox source)
    {
        if (_binding) return;
        var nextIds = _rows.Where(r => r.Check.IsChecked == true).Select(r => r.Id).ToList();
        if (nextIds.Count == 0)
        {
            bool prev = _binding;
            _binding = true;
            try { source.IsChecked = true; }
            finally { _binding = prev; }
            MainWindow.ShowToast(
                "至少保留 1 项监控指标；若需全部停用请关闭上方总开关。",
                FluentAvalonia.UI.Controls.InfoBarSeverity.Warning);
            return;
        }

        App.Settings.Current.MonitorFlyoutMetrics = nextIds;
        App.Settings.Save();
        WidgetMonitorWindow.RefreshSelectionIfOpen();
        UpdateLiveValues();
    }

    private void ApplyMetricSelectionPreset(IEnumerable<string> metricIds)
    {
        var chosen = new HashSet<string>(metricIds, StringComparer.OrdinalIgnoreCase);
        var ordered = DesktopMetricsEngine.AllMetrics
            .Where(m => !string.Equals(m.Id, "MEM_USED", StringComparison.OrdinalIgnoreCase) && chosen.Contains(m.Id))
            .Select(m => m.Id)
            .ToList();
        if (ordered.Count == 0)
            ordered = [.. DesktopMetricsEngine.DefaultFlyoutMetricIds];

        bool prev = _binding;
        _binding = true;
        try
        {
            var set = new HashSet<string>(ordered, StringComparer.OrdinalIgnoreCase);
            foreach (var r in _rows)
                r.Check.IsChecked = set.Contains(r.Id);
        }
        finally { _binding = prev; }

        App.Settings.Current.MonitorFlyoutMetrics = ordered;
        App.Settings.Save();
        WidgetMonitorWindow.RefreshSelectionIfOpen();
        UpdateLiveValues();
    }

    private static HashSet<string> GetSelectedMetricIdSet()
    {
        var saved = App.Settings.Current.MonitorFlyoutMetrics;
        var list = saved is { Count: > 0 }
            ? saved
            : DesktopMetricsEngine.DefaultFlyoutMetricIds.ToList();
        return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
    }

    private static MetricScope ComputeSelectedScope(HashSet<string> selected)
        => selected.Count > 0 ? SystemMetricsEngine.FromMetricIds(selected.ToArray()) : MetricScope.None;

    private static void PopulateMetricCombo(ComboBox box, string selectedId, bool allowNone)
    {
        box.Items.Clear();
        if (allowNone)
            box.Items.Add(new ComboBoxItem { Content = "不显示（仅单行）", Tag = "NONE" });
        foreach (var m in DesktopMetricsEngine.AllMetrics)
            box.Items.Add(new ComboBoxItem { Content = $"{m.FullName}（{m.ShortLabel}）", Tag = m.Id });

        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private void OnTaskbarMetricChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_binding) return;
        if (TopMetricBox.SelectedItem is ComboBoxItem top && top.Tag is string topId)
            App.Settings.Current.MonitorTopMetric = topId;
        if (BottomMetricBox.SelectedItem is ComboBoxItem bottom && bottom.Tag is string bottomId)
            App.Settings.Current.MonitorBottomMetric = bottomId;
        App.Settings.Save();
        App.RefreshWidget();
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        string surface = OperatingSystem.IsMacOS() ? "菜单栏" : "任务栏贴片";
        var s = App.Settings.Current;
        if (!s.MonitorCollectionEnabled)
        {
            StatusText.Text = $"监控采集已关闭（零采样开销）；{surface}只显示外设电量。";
            return;
        }
        if (!s.MonitorEnabled)
        {
            StatusText.Text = $"仅在本页与桌面悬浮条采集查看；{surface}未显示系统监控分组。";
            return;
        }
        string top = DesktopMetricsEngine.AllMetrics
            .FirstOrDefault(m => m.Id == s.MonitorTopMetric)?.ShortLabel
            ?? s.MonitorTopMetric;
        string bottom = s.MonitorBottomMetric == "NONE"
            ? "隐藏"
            : (DesktopMetricsEngine.AllMetrics
                .FirstOrDefault(m => m.Id == s.MonitorBottomMetric)?.ShortLabel
               ?? s.MonitorBottomMetric);
        StatusText.Text = OperatingSystem.IsMacOS()
            ? $"已启用：菜单栏显示「{top} / {bottom}」，点击顶部「菜单栏状态」可打开桌面悬浮窗。"
            : $"已启用：任务栏显示「{top} / {bottom}」，鼠标移至监控区域自动弹出详细悬浮窗。";
        UpdateHwInfoStatusText(DesktopMetricsEngine.LastSnapshot);
    }

    private void UpdateHwInfoStatusText(SystemMetricsSnapshot snap)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var s = App.Settings.Current;
        if (!s.MonitorCollectionEnabled)
        {
            HwInfoStatusText.Text = "";
            return;
        }
        if (!s.HwInfoBridgeEnabled)
        {
            HwInfoStatusText.Text = string.IsNullOrWhiteSpace(snap.ActiveGpuName)
                ? "HWiNFO 桥接已关闭，使用系统原生采集。"
                : $"HWiNFO 桥接已关闭，当前主显卡：{snap.ActiveGpuName}";
            return;
        }
        HwInfoStatusText.Text = snap.HwInfoConnected
            ? (string.IsNullOrWhiteSpace(snap.ActiveGpuName)
                ? "HWiNFO 桥接：已连接（共享内存实时读取 CPU 温度与多卡数据）"
                : $"HWiNFO 桥接：已连接 · {snap.ActiveGpuName}")
            : (string.IsNullOrWhiteSpace(snap.ActiveGpuName)
                ? "系统原生采集（开启 HWiNFO Shared Memory 可补充 Intel/AMD CPU 与非 NVIDIA 显卡温度）"
                : $"系统原生采集 · {snap.ActiveGpuName}（开启 HWiNFO Shared Memory 可补充 CPU/GPU 温度）");
    }

    private void OnFloatingStateChanged()
        => FloatingToggleBtn.Content = FloatingMonitorWindow.IsOpen
            ? "关闭桌面悬浮监控条"
            : "显示桌面悬浮监控条";
}
