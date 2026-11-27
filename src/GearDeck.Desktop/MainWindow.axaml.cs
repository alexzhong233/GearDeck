using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using GearDeck.Core.Models;
using GearDeck.Desktop.Services;
using GearDeck.Desktop.Views;
using GearDeck.Desktop.Windows;

namespace GearDeck.Desktop;

public partial class MainWindow : AppWindow
{
    private sealed record NavEntry(string Key, string? DeviceId = null);

    private sealed record NavSearchTarget(string Title, string Subtitle, string TargetKey)
    {
        public override string ToString() => $"{Title} · {Subtitle}";
    }

    public static MainWindow? Instance { get; private set; }

    private readonly Dictionary<string, UserControl> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NavEntry> _history = new();
    private int _historyIndex = -1;
    private bool _suppressNavHandler;
    private bool _initialNavExpanded;
    private string? _activeDeviceId;
    private readonly DispatcherTimer _toastTimer = new();
    private int _toastSeq;

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        if (Program.UseSoftwareRendering)
        {
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            ApplySoftwareThemeBackground();
            if (Application.Current != null)
                Application.Current.ActualThemeVariantChanged += OnSoftwareThemeChanged;
        }

        Title = $"GearDeck v{AppUpdateService.CurrentVersion}";
        TitleBarText.Text = Title;
        if (OperatingSystem.IsMacOS())
        {
            TitleBarLeftPanel.Margin = new Thickness(78, 0, 0, 0);
            RightCaptionColumn.Width = 12;
            WidgetNav.Content = "菜单栏状态";
        }
        try
        {
            TitleBar.ExtendsContentIntoTitleBar = true;
            TitleBar.Height = 48;
            string pngPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.png");
            string icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            string? iconPath = System.IO.File.Exists(pngPath) ? pngPath : (System.IO.File.Exists(icoPath) ? icoPath : null);
            if (iconPath != null)
                Icon = new Avalonia.Media.Imaging.Bitmap(iconPath);
        }
        catch { }

        RootNavigation.SelectionChanged += OnNavSelectionChanged;
        TitleBarSearchBox.ItemFilter = (text, item) => item is NavSearchTarget t &&
            (string.IsNullOrWhiteSpace(text) ||
             t.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
             t.Subtitle.Contains(text, StringComparison.OrdinalIgnoreCase));
        TitleBarSearchBox.ItemsSource = BuildSearchCandidates();

        App.Devices.DevicesUpdated += OnDevicesUpdated;
        ToastInfoBar.Closed += (_, _) => HideToastCore();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            FadeOutToastCore();
        };
        Closed += (_, _) =>
        {
            _toastTimer.Stop();
            App.Devices.DevicesUpdated -= OnDevicesUpdated;
            if (Program.UseSoftwareRendering && Application.Current != null)
                Application.Current.ActualThemeVariantChanged -= OnSoftwareThemeChanged;
            try { PageHost.Content = null; } catch { }
            _pages.Clear();
            if (ReferenceEquals(Instance, this))
                Instance = null;
        };

        RebuildDeviceMenus(App.LastDevices);
        UpdateDebugVisibility();
        UpdateVersionBadge();
        Navigate("devices");
        MacAccessibilityGuard.PatchVisualSubtree(this);
        Opened += (_, _) => MacAccessibilityGuard.PatchVisualSubtree(this);
    }

    protected override AutomationPeer OnCreateAutomationPeer()
        => new MacAccessibilityGuard.SafeWindowAutomationPeer(this);

    private void OnSoftwareThemeChanged(object? sender, EventArgs e) => ApplySoftwareThemeBackground();

    private void ApplySoftwareThemeBackground()
    {
        if (Application.Current?.TryGetResource("SolidBackgroundFillColorBaseBrush", Application.Current.ActualThemeVariant, out var val) == true &&
            val is IBrush brush)
        {
            Background = brush;
        }
        else
        {
            bool dark = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            Background = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
        }
    }

    public static void ShowToast(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, int? durationMs = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            HideToast();
            return;
        }
        App.UiPost(() => Instance?.ShowToastCore(message, severity, durationMs));
    }

    public static void HideToast()
        => App.UiPost(() => Instance?.HideToastCore());

    private void ShowToastCore(string message, InfoBarSeverity severity, int? durationMs)
    {
        int seq = ++_toastSeq;
        _toastTimer.Stop();

        ToastInfoBar.Severity = severity;
        ToastInfoBar.Message = message;
        ToastInfoBar.IsOpen = true;
        ToastHost.RenderTransform = TransformOperations.Parse("translateX(28px)");
        ToastHost.Opacity = 0;
        ToastHost.IsVisible = true;

        Dispatcher.UIThread.Post(() =>
        {
            if (_toastSeq != seq) return;
            ToastHost.RenderTransform = TransformOperations.Parse("translateX(0px)");
            ToastHost.Opacity = 1;
        }, DispatcherPriority.Render);

        int timeout = durationMs ?? (severity is InfoBarSeverity.Warning or InfoBarSeverity.Error ? 5000 : 3500);
        if (timeout > 0)
        {
            _toastTimer.Interval = TimeSpan.FromMilliseconds(timeout);
            _toastTimer.Start();
        }
    }

    private void FadeOutToastCore()
    {
        int seq = _toastSeq;
        ToastHost.Opacity = 0;
        ToastHost.RenderTransform = TransformOperations.Parse("translateX(28px)");
        _ = System.Threading.Tasks.Task.Delay(230).ContinueWith(_ =>
        {
            App.UiPost(() =>
            {
                if (_toastSeq != seq) return;
                ToastInfoBar.IsOpen = false;
                ToastHost.IsVisible = false;
            });
        });
    }

    private void HideToastCore()
    {
        _toastSeq++;
        _toastTimer.Stop();
        ToastHost.Opacity = 0;
        ToastInfoBar.IsOpen = false;
        ToastHost.IsVisible = false;
    }

    public static void ShowOrActivate()
    {
        var window = Instance ?? new MainWindow();
        try
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
        }
        catch { }
    }

    public void UpdateVersionBadge()
    {
        try
        {
            SettingsUpdateBadge.IsVisible = App.Updater?.LastOutcome?.HasUpdate == true;
            TitleBarSearchBox.ItemsSource = BuildSearchCandidates();
        }
        catch { }
    }

    public void UpdateDebugVisibility()
    {
        DiagnosticsNav.IsVisible = App.Settings.Current.DebugModeEnabled;
    }

    public void OpenDevicesPage()
    {
        ShowOrActivate();
        Navigate("devices");
    }

    public void OpenDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;
        try { Show(); Activate(); } catch { }
        Navigate("device", deviceId);
    }

    public void OpenGamesPage()
    {
        ShowOrActivate();
        Navigate("games");
    }

    public void OpenMacrosPage()
    {
        ShowOrActivate();
        Navigate("macros");
    }

    public void OpenWidgetPage()
    {
        ShowOrActivate();
        Navigate("widget");
    }

    public void OpenMonitorPage()
    {
        ShowOrActivate();
        Navigate("monitor");
    }

    public void ShowGroupFlyout(string groupId, Rect? anchor = null)
    {
        try { WidgetGroupWindow.ShowStandalone(groupId, anchor, isHover: false); }
        catch { }
    }

    public void ShowMonitorFlyout(Rect? anchor = null)
    {
        try { WidgetMonitorWindow.ShowOrUpdate(anchor, isHover: false); }
        catch { }
    }

    public void CloseMonitorFlyout()
    {
        WidgetMonitorWindow.CloseIfOpen();
    }

    private void OnTitleBarBackClick(object? sender, RoutedEventArgs e)
    {
        if (_historyIndex <= 0)
            return;
        _historyIndex--;
        var entry = _history[_historyIndex];
        Navigate(entry.Key, entry.DeviceId, addHistory: false);
    }

    private void OnTitleBarPaneClick(object? sender, RoutedEventArgs e)
        => RootNavigation.IsPaneOpen = !RootNavigation.IsPaneOpen;

    private void Navigate(string key, string? deviceId = null, bool addHistory = true)
    {
        try
        {
            _suppressNavHandler = true;
            bool sameActiveDevice = key == "device"
                && !string.IsNullOrWhiteSpace(deviceId)
                && string.Equals(_activeDeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                && PageHost.Content is DeviceDetailView or LeobogHi98View;
            if (!sameActiveDevice)
                PageHost.Content = GetOrCreatePage(key, deviceId);
            if (PageHost.Content is DevicesView devices)
                devices.SetKindFilter(KeyToKind(key));

            _activeDeviceId = key == "device" ? deviceId : null;
            UpdateNavSelection(key, deviceId);

            if (addHistory)
            {
                if (_historyIndex < _history.Count - 1)
                    _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                var entry = new NavEntry(key, deviceId);
                if (_historyIndex < 0 || !Equals(_history[_historyIndex], entry))
                {
                    _history.Add(entry);
                    _historyIndex = _history.Count - 1;
                }
            }
            TitleBarBackBtn.IsEnabled = _historyIndex > 0;
        }
        finally
        {
            _suppressNavHandler = false;
        }
    }

    private static DeviceKind? KeyToKind(string key) => key switch
    {
        "mouse" => DeviceKind.Mouse,
        "keyboard" => DeviceKind.Keyboard,
        "headset" => DeviceKind.Headset,
        "controller" => DeviceKind.Controller,
        _ => null,
    };

    private void UpdateNavSelection(string key, string? deviceId)
    {
        NavigationViewItem? selected = key switch
        {
            "device" => FindDeviceNavItem(deviceId) ?? DevicesNav,
            "mouse" => MouseNav,
            "keyboard" => KeyboardNav,
            "headset" => HeadsetNav,
            "controller" => ControllerNav,
            "games" => GamesNav,
            "sensitivity" => SensitivityNav,
            "macros" => MacrosNav,
            "widget" => WidgetNav,
            "monitor" => MonitorNav,
            "diagnostics" => DiagnosticsNav,
            "settings" => SettingsNav,
            _ => DevicesNav,
        };
        if (RootNavigation.SelectedItem is FluentAvalonia.UI.Controls.NavigationViewItem prev && !ReferenceEquals(prev, selected))
            prev.IsSelected = false;
        foreach (var parent in new[] { MouseNav, KeyboardNav, HeadsetNav, ControllerNav })
        {
            foreach (var child in parent.MenuItems.OfType<FluentAvalonia.UI.Controls.NavigationViewItem>())
            {
                if (!ReferenceEquals(child, selected) && child.IsSelected)
                    child.IsSelected = false;
            }
        }
        RootNavigation.SelectedItem = selected;
        if (selected != null)
            selected.IsSelected = true;
    }

    private NavigationViewItem? FindDeviceNavItem(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;
        string expected = $"device:{deviceId}";
        foreach (var parent in new[] { MouseNav, KeyboardNav, HeadsetNav, ControllerNav })
        {
            foreach (var child in parent.MenuItems.OfType<NavigationViewItem>())
            {
                if (string.Equals(child.Tag as string, expected, StringComparison.OrdinalIgnoreCase))
                    return child;
            }
        }
        return null;
    }

    private void OnNavSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (_suppressNavHandler)
            return;
        if (RootNavigation.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
            return;

        if (tag is "mouse" or "keyboard" or "headset" or "controller")
        {
            item.IsExpanded = true;
            Navigate(tag);
            return;
        }
        if (tag.StartsWith("device:", StringComparison.Ordinal))
        {
            Navigate("device", tag[7..]);
            return;
        }
        Navigate(tag);
    }

    private UserControl GetOrCreatePage(string key, string? deviceId = null)
    {
        if (key == "device" && !string.IsNullOrWhiteSpace(deviceId))
        {
            if (deviceId.StartsWith("leobog:", StringComparison.OrdinalIgnoreCase))
                return new LeobogHi98View(deviceId);
            return new DeviceDetailView(deviceId);
        }

        string cacheKey = key == "device" ? "devices" : key;
        if (_pages.TryGetValue(cacheKey, out var existing))
            return existing;

        UserControl created = cacheKey switch
        {
            "devices" or "mouse" or "keyboard" or "headset" or "controller" => new DevicesView(),
            "games" => new GamesView(),
            "sensitivity" => new SensitivityView(),
            "macros" => new MacrosView(),
            "widget" => new WidgetView(),
            "monitor" => new MonitorView(),
            "diagnostics" => new DiagnosticsView(),
            "settings" => new SettingsView(),
            _ => new DevicesView(),
        };
        _pages[cacheKey] = created;
        return created;
    }

    private List<NavSearchTarget> BuildSearchCandidates()
    {
        var items = new List<NavSearchTarget>
        {
            new("设备总览", "页面", "page:devices"),
            new("游戏与配置切换", "本机游戏库 / 全屏与退出策略", "page:games"),
            new("跨游戏灵敏度换算", "16 款热门 FPS · FOV / DPI / 分辨率转换", "page:sensitivity"),
            new("鼠标宏中心", "外置宏录制与多设备绑定", "page:macros"),
            new(OperatingSystem.IsMacOS() ? "菜单栏状态" : "任务栏贴片",
                OperatingSystem.IsMacOS() ? "菜单栏平铺 / 分组图标 / 监控摘要" : "贴片位置 / 样式 / 分组图标",
                "page:widget"),
            new("系统监控", "CPU / GPU / 内存 / 网速监测", "page:monitor"),
            new("设置", "版本更新 / 镜像 / 托盘 / 低电量阈值", "page:settings"),
        };
        if (App.Settings.Current.DebugModeEnabled)
            items.Add(new NavSearchTarget("诊断 (Debug)", "HID 节点日志与原始探测", "page:diagnostics"));

        foreach (var d in App.LastDevices)
            items.Add(new NavSearchTarget(d.Name, $"{d.Provider} · {d.Kind}", $"device:{d.Id}"));
        return items;
    }

    private void OnSearchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TitleBarSearchBox.SelectedItem is NavSearchTarget target)
            NavigateBySearchTarget(target);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        string text = TitleBarSearchBox.Text ?? string.Empty;
        var target = TitleBarSearchBox.SelectedItem as NavSearchTarget
            ?? BuildSearchCandidates().FirstOrDefault(x =>
                x.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                x.Subtitle.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (target != null)
            NavigateBySearchTarget(target);
    }

    private void NavigateBySearchTarget(NavSearchTarget target)
    {
        TitleBarSearchBox.Text = string.Empty;
        TitleBarSearchBox.SelectedItem = null;
        if (target.TargetKey.StartsWith("device:", StringComparison.Ordinal))
        {
            OpenDevice(target.TargetKey[7..]);
            return;
        }
        Navigate(target.TargetKey.StartsWith("page:", StringComparison.Ordinal)
            ? target.TargetKey[5..]
            : target.TargetKey);
    }

    private void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> devices)
        => App.UiPost(() =>
        {
            RebuildDeviceMenus(devices);
            UpdateVersionBadge();
        });

    private void RebuildDeviceMenus(IReadOnlyList<PeripheralDevice> devices)
    {
        bool prevSuppress = _suppressNavHandler;
        _suppressNavHandler = true;
        try
        {
            Fill(MouseNav, devices.Where(d => d.Kind == DeviceKind.Mouse));
            Fill(KeyboardNav, devices.Where(d => d.Kind == DeviceKind.Keyboard));
            Fill(HeadsetNav, devices.Where(d => d.Kind == DeviceKind.Headset));
            Fill(ControllerNav, devices.Where(d => d.Kind == DeviceKind.Controller));

            if (!_initialNavExpanded && devices.Count > 0)
            {
                _initialNavExpanded = true;
                MouseNav.IsExpanded = devices.Any(d => d.Kind == DeviceKind.Mouse);
                KeyboardNav.IsExpanded = devices.Any(d => d.Kind == DeviceKind.Keyboard);
                HeadsetNav.IsExpanded = devices.Any(d => d.Kind == DeviceKind.Headset);
                ControllerNav.IsExpanded = devices.Any(d => d.Kind == DeviceKind.Controller);
            }

            if (!string.IsNullOrWhiteSpace(_activeDeviceId) && PageHost.Content is DeviceDetailView or LeobogHi98View)
                UpdateNavSelection("device", _activeDeviceId);
            MacAccessibilityGuard.PatchVisualSubtree(RootNavigation);
        }
        finally
        {
            _suppressNavHandler = prevSuppress;
        }
    }

    private static void Fill(NavigationViewItem parent, IEnumerable<PeripheralDevice> devices)
    {
        var ordered = devices.OrderBy(d => d.Name).ToList();
        var existing = parent.MenuItems.OfType<NavigationViewItem>().ToList();

        static (string Badge, bool Dim, string Tip) BuildBadge(PeripheralDevice d)
        {
            string? dual = d.FormatDualEarSummary(compact: true);
            if (!string.IsNullOrEmpty(dual))
                return (dual, false, $"{d.Name} · {dual}");
            if (d.BatteryPercent is { } p)
                return ($"{p}%", false, $"{d.Name} · {p}%");
            if (d.IsSleeping)
                return ("休眠", true, $"{d.Name} · 接收器已连接 · 休眠");
            return ("", !d.Connected, d.Name);
        }

        static Control BuildItemContent(string name, string badge, bool dim)
        {
            var grid = new Grid { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var nameBlock = new TextBlock
            {
                Text = name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(nameBlock, 0);
            grid.Children.Add(nameBlock);

            var badgeBlock = new TextBlock
            {
                Text = badge,
                FontSize = 11,
                Opacity = dim ? 0.65 : 1.0,
                Margin = new Thickness(8, 0, 0, 0),
                IsVisible = !string.IsNullOrEmpty(badge),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(badgeBlock, 1);
            grid.Children.Add(badgeBlock);

            return grid;
        }

        if (ordered.Count > 0 && existing.Count == ordered.Count
            && existing.Zip(ordered).All(pair =>
                string.Equals(pair.First.Tag as string, $"device:{pair.Second.Id}", StringComparison.OrdinalIgnoreCase)))
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                var d = ordered[i];
                var (badge, dim, tip) = BuildBadge(d);
                ToolTip.SetTip(existing[i], tip);
                if (existing[i].Content is Grid g && g.Children.Count >= 2
                    && g.Children[0] is TextBlock nb && g.Children[1] is TextBlock bb)
                {
                    if (!string.Equals(nb.Text, d.Name, StringComparison.Ordinal))
                        nb.Text = d.Name;
                    if (!string.Equals(bb.Text, badge, StringComparison.Ordinal))
                        bb.Text = badge;
                    bb.Opacity = dim ? 0.65 : 1.0;
                    bb.IsVisible = !string.IsNullOrEmpty(badge);
                }
                else
                {
                    existing[i].HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                    existing[i].Content = BuildItemContent(d.Name, badge, dim);
                }
            }
            return;
        }

        if (ordered.Count == 0 && existing.Count == 1 && !existing[0].IsEnabled)
            return;

        parent.MenuItems.Clear();
        foreach (var d in ordered)
        {
            var (badge, dim, tip) = BuildBadge(d);
            var item = new NavigationViewItem
            {
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Content = BuildItemContent(d.Name, badge, dim),
                Tag = $"device:{d.Id}",
            };
            ToolTip.SetTip(item, tip);
            parent.MenuItems.Add(item);
        }
        if (parent.MenuItems.Count == 0)
            parent.MenuItems.Add(new NavigationViewItem { Content = "（未发现设备）", IsEnabled = false });
    }
}
