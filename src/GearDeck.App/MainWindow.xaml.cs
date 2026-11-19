using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using GearDeck.App.Pages;
using GearDeck.Core.Models;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace GearDeck.App;

public sealed partial class MainWindow : Window
{
    private sealed record NavSearchTarget(string Title, string Subtitle, string TargetKey)
    {
        public override string ToString() => $"{Title} · {Subtitle}";
    }

    public static MainWindow? Instance { get; set; }
    private WidgetGroupWindow? _groupWindow;
    private WidgetMonitorWindow? _monitorWindow;
    private string? _activeDeviceId;
    private bool _suppressNavHandler;

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) =>
        {
            if (App.IsExiting || !App.Settings.Current.CloseToTray)
                App.ExitApplication();
        };
        Title = $"GearDeck v{Services.AppUpdateService.CurrentVersion}";
        TitleBarText.Text = $"GearDeck v{Services.AppUpdateService.CurrentVersion}";
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
                AppWindow.SetIcon(iconPath);
        }
        catch { }

        SystemBackdrop = new MicaBackdrop();
        ConfigureGalleryTitleBar();

        var area = DisplayArea.Primary.WorkArea;
        int winW = Math.Clamp(Math.Min(1260, area.Width - 60), 860, 1480);
        int winH = Math.Clamp(Math.Min(820, area.Height - 60), 620, 960);
        AppWindow.Resize(new SizeInt32(winW, winH));
        AppWindow.Move(new PointInt32(
            area.X + Math.Max(0, (area.Width - winW) / 2),
            area.Y + Math.Max(0, (area.Height - winH) / 2)));

        UpdateDebugVisibility();

        var args = Environment.GetCommandLineArgs();
        var pageArg = args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
        var start = pageArg is { Length: > 7 } ? pageArg[7..] : null;
        Type startPage = start switch
        {
            "games" => typeof(GamesPage),
            "macros" => typeof(MacrosPage),
            "widget" => typeof(WidgetPage),
            "monitor" => typeof(MonitorPage),
            "diagnostics" => typeof(DiagnosticsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DevicesPage),
        };
        var devArg = args.FirstOrDefault(a => a.StartsWith("--device=", StringComparison.OrdinalIgnoreCase));

        App.Devices.DevicesUpdated += OnDevicesUpdated;
        App.Updater.StateChanged += () => App.UiQueue?.TryEnqueue(UpdateVersionBadge);
        UpdateVersionBadge();
        RebuildDeviceMenus(App.LastDevices);

        if (devArg is { Length: > 9 })
        {
            string devId = devArg[9..];
            _activeDeviceId = devId;
            SelectDeviceInNav(devId);
            ContentFrame.Navigate(typeof(DeviceDetailPage), devId);
            return;
        }

        ContentFrame.Navigate(startPage);
        SyncNavSelectionToPage(startPage);
    }

    private void ConfigureGalleryTitleBar()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var tb = AppWindow.TitleBar;
                tb.ExtendsContentIntoTitleBar = true;
                tb.PreferredHeightOption = TitleBarHeightOption.Tall;
                ApplyTitleBarThemeColors();
            }

            RootGrid.ActualThemeChanged += (_, _) =>
            {
                ApplyTitleBarThemeColors();
                UpdateTitleBarNonClientRegions();
            };
            Activated += (_, e) => ApplyTitleBarActiveOpacity(e.WindowActivationState != WindowActivationState.Deactivated);
            AppTitleBar.Loaded += (_, _) => UpdateTitleBarNonClientRegions();
            AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarNonClientRegions();
            TitleBarSearchBox.SizeChanged += (_, _) => UpdateTitleBarNonClientRegions();
        }
        catch { }
    }

    private void ApplyTitleBarActiveOpacity(bool active)
    {
        double opacity = active ? 1.0 : 0.6;
        TitleBarBackBtn.Opacity = opacity;
        TitleBarPaneBtn.Opacity = opacity;
        TitleBarText.Opacity = opacity;
    }

    private void ApplyTitleBarThemeColors()
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;
            var tb = AppWindow.TitleBar;
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;

            bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
            tb.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
            tb.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            tb.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
            tb.ButtonInactiveForegroundColor = dark
                ? Windows.UI.Color.FromArgb(140, 255, 255, 255)
                : Windows.UI.Color.FromArgb(140, 0, 0, 0);
            tb.ButtonHoverBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(24, 255, 255, 255)
                : Windows.UI.Color.FromArgb(18, 0, 0, 0);
            tb.ButtonPressedBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(16, 255, 255, 255)
                : Windows.UI.Color.FromArgb(12, 0, 0, 0);
        }
        catch { }
    }

    private void UpdateTitleBarNonClientRegions()
    {
        try
        {
            if (AppTitleBar.XamlRoot == null || AppTitleBar.ActualWidth <= 0) return;
            double scale = AppTitleBar.XamlRoot.RasterizationScale;
            if (scale <= 0) scale = 1.0;

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                double rightInset = AppWindow.TitleBar.RightInset / scale;
                if (rightInset > 0)
                    RightCaptionColumn.Width = new GridLength(rightInset + 8);
            }

            var passthrough = new List<RectInt32>();
            foreach (FrameworkElement ctrl in new FrameworkElement[] { TitleBarBackBtn, TitleBarPaneBtn, TitleBarSearchBox })
            {
                if (ctrl.Visibility != Visibility.Visible || ctrl.ActualWidth <= 0 || ctrl.ActualHeight <= 0)
                    continue;
                GeneralTransform gt = ctrl.TransformToVisual(null);
                Point pt = gt.TransformPoint(new Point(0, 0));
                passthrough.Add(new RectInt32(
                    (int)Math.Floor(pt.X * scale),
                    (int)Math.Floor(pt.Y * scale),
                    (int)Math.Ceiling(ctrl.ActualWidth * scale),
                    (int)Math.Ceiling(ctrl.ActualHeight * scale)));
            }

            if (passthrough.Count > 0)
            {
                var nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
                nonClient.SetRegionRects(NonClientRegionKind.Passthrough, [.. passthrough]);
            }
        }
        catch { }
    }

    private void OnTitleBarBackClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CanGoBack)
            ContentFrame.GoBack();
    }

    private void OnTitleBarPaneClick(object sender, RoutedEventArgs e)
    {
        Nav.IsPaneOpen = !Nav.IsPaneOpen;
    }

    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        TitleBarBackBtn.IsEnabled = ContentFrame.CanGoBack;
        if (e.SourcePageType == typeof(DeviceDetailPage) && e.Parameter is string devId)
        {
            _activeDeviceId = devId;
            SelectDeviceInNav(devId);
        }
        else
        {
            _activeDeviceId = null;
            SyncNavSelectionToPage(e.SourcePageType);
        }
    }

    private void SyncNavSelectionToPage(Type pageType)
    {
        _suppressNavHandler = true;
        Nav.SelectedItem = pageType == typeof(GamesPage) ? GamesNav
            : pageType == typeof(MacrosPage) ? MacrosNav
            : pageType == typeof(WidgetPage) ? WidgetNav
            : pageType == typeof(MonitorPage) ? MonitorNav
            : pageType == typeof(DiagnosticsPage) ? DiagnosticsNav
            : pageType == typeof(SettingsPage) ? SettingsNav
            : DevicesNav;
        _suppressNavHandler = false;
    }

    private List<NavSearchTarget> BuildSearchCandidates()
    {
        var items = new List<NavSearchTarget>
        {
            new("设备总览", "页面", "page:devices"),
            new("游戏与配置切换", "本机游戏库 / 全屏与退出策略", "page:games"),
            new("鼠标宏中心", "外置宏录制与多设备绑定", "page:macros"),
            new("任务栏贴片", "贴片位置 / 样式 / 分组图标", "page:widget"),
            new("系统监控", "CPU / GPU / 内存 / 网速监测", "page:monitor"),
            new("设置", "版本更新 / GitHub 镜像 / 托盘 / 低电量阈值 / 外置图片规则", "page:settings"),
        };
        if (App.Settings.Current.DebugModeEnabled)
            items.Add(new("诊断 (Debug)", "HID 节点日志与原始探测", "page:diagnostics"));

        foreach (var d in App.LastDevices)
            items.Add(new(d.Name, $"{d.Provider} · {d.Kind}", $"device:{d.Id}"));

        return items;
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        string q = sender.Text.Trim();
        var all = BuildSearchCandidates();
        sender.ItemsSource = string.IsNullOrEmpty(q)
            ? all.Take(8).ToList()
            : all.Where(x => x.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                             x.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase))
                 .Take(10)
                 .ToList();
    }

    private void OnSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is NavSearchTarget target)
            NavigateBySearchTarget(target);
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is NavSearchTarget chosen)
        {
            NavigateBySearchTarget(chosen);
            return;
        }
        string q = args.QueryText.Trim();
        if (string.IsNullOrEmpty(q)) return;
        var first = BuildSearchCandidates().FirstOrDefault(x =>
            x.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            x.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (first != null)
            NavigateBySearchTarget(first);
    }

    private void NavigateBySearchTarget(NavSearchTarget target)
    {
        TitleBarSearchBox.Text = "";
        if (target.TargetKey.StartsWith("device:", StringComparison.Ordinal))
        {
            OpenDevice(target.TargetKey[7..]);
            return;
        }
        Type? page = target.TargetKey switch
        {
            "page:games" => typeof(GamesPage),
            "page:macros" => typeof(MacrosPage),
            "page:widget" => typeof(WidgetPage),
            "page:monitor" => typeof(MonitorPage),
            "page:diagnostics" => typeof(DiagnosticsPage),
            "page:settings" => typeof(SettingsPage),
            _ => typeof(DevicesPage),
        };
        _activeDeviceId = null;
        SyncNavSelectionToPage(page);
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    public void UpdateVersionBadge()
    {
        SettingsUpdateBadge.Visibility = App.Updater?.LastOutcome?.HasUpdate == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public void UpdateDebugVisibility()
    {
        bool debug = App.Settings.Current.DebugModeEnabled;
        DiagnosticsNav.Visibility = debug ? Visibility.Visible : Visibility.Collapsed;
        if (!debug && ContentFrame.CurrentSourcePageType == typeof(DiagnosticsPage))
        {
            _suppressNavHandler = true;
            Nav.SelectedItem = SettingsNav;
            _suppressNavHandler = false;
            ContentFrame.Navigate(typeof(SettingsPage));
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!App.IsExiting && App.Settings.Current.CloseToTray)
        {
            args.Cancel = true;
            try { AppWindow.Hide(); }
            catch { }
        }
    }

    public static void ShowOrActivate()
    {
        if (Instance == null)
            Instance = new MainWindow();

        try
        {
            Instance.AppWindow.Show();
            IntPtr hwnd = WindowNative.GetWindowHandle(Instance);
            if (hwnd != IntPtr.Zero)
            {
                if (IsIconic(hwnd))
                    ShowWindow(hwnd, 9);
                else
                    ShowWindow(hwnd, 5);
                SetForegroundWindow(hwnd);
            }
        }
        catch { }

        Instance.Activate();
    }

    public void OpenDevice(string deviceId)
    {
        ShowOrActivate();
        _activeDeviceId = deviceId;
        RebuildDeviceMenus(App.LastDevices);
        SelectDeviceInNav(deviceId);
        ContentFrame.Navigate(typeof(DeviceDetailPage), deviceId);
    }

    public void OpenGamesPage()
    {
        ShowOrActivate();
        _activeDeviceId = null;
        _suppressNavHandler = true;
        Nav.SelectedItem = GamesNav;
        _suppressNavHandler = false;
        if (ContentFrame.CurrentSourcePageType != typeof(GamesPage))
            ContentFrame.Navigate(typeof(GamesPage));
    }

    public void OpenMacrosPage()
    {
        ShowOrActivate();
        _activeDeviceId = null;
        _suppressNavHandler = true;
        Nav.SelectedItem = MacrosNav;
        _suppressNavHandler = false;
        if (ContentFrame.CurrentSourcePageType != typeof(MacrosPage))
            ContentFrame.Navigate(typeof(MacrosPage));
    }

    public void OpenMonitorPage()
    {
        ShowOrActivate();
        _activeDeviceId = null;
        _suppressNavHandler = true;
        Nav.SelectedItem = MonitorNav;
        _suppressNavHandler = false;
        if (ContentFrame.CurrentSourcePageType != typeof(MonitorPage))
            ContentFrame.Navigate(typeof(MonitorPage));
    }

    private void SelectDeviceInNav(string deviceId)
    {
        string expectedTag = $"device:{deviceId}";
        foreach (var parent in new[] { MouseNav, KeyboardNav, HeadsetNav, ControllerNav })
        {
            foreach (var child in parent.MenuItems.OfType<NavigationViewItem>())
            {
                if (string.Equals(child.Tag as string, expectedTag, StringComparison.OrdinalIgnoreCase))
                {
                    _suppressNavHandler = true;
                    parent.IsExpanded = true;
                    Nav.SelectedItem = child;
                    _suppressNavHandler = false;
                    return;
                }
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public void ShowMonitorFlyout(RectInt32? anchor = null)
    {
        if (_monitorWindow is { IsClosed: false })
        {
            _monitorWindow.UpdateAnchor(anchor);
            return;
        }
        try { _groupWindow?.Close(); } catch { }
        _monitorWindow = new WidgetMonitorWindow(anchor);
    }

    public void CloseMonitorFlyout()
    {
        try { _monitorWindow?.Close(); } catch { }
        _monitorWindow = null;
    }

    public void Show() => Activate();

    public void ShowGroupFlyout(string groupId, RectInt32? anchor = null)
    {
        CloseMonitorFlyout();
        var devices = App.LastDevices;
        string title = groupId switch
        {
            "builtin:mouse" => "鼠标",
            "builtin:keyboard" => "键盘",
            "builtin:headset" => "耳机",
            "builtin:controller" => "手柄",
            _ => App.Settings.Current.CustomGroups.FirstOrDefault(g => g.Id == groupId)?.DisplayName ?? "设备组",
        };
        var members = groupId switch
        {
            "builtin:mouse" => devices.Where(d => d.Kind == DeviceKind.Mouse).ToList(),
            "builtin:keyboard" => devices.Where(d => d.Kind == DeviceKind.Keyboard).ToList(),
            "builtin:headset" => devices.Where(d => d.Kind == DeviceKind.Headset).ToList(),
            "builtin:controller" => devices.Where(d => d.Kind == DeviceKind.Controller).ToList(),
            _ => devices.Where(d => App.Settings.Current.CustomGroups
                .FirstOrDefault(g => g.Id == groupId)?.MemberIds.Contains(d.Id) == true).ToList(),
        };
        var cfg = App.Settings.Current.TaskbarGroups.FirstOrDefault(g => g.GroupId == groupId);
        string glyph = cfg != null
            ? Services.GroupIconCatalog.ResolveEffectiveGlyph(cfg, members)
            : Services.GroupIconCatalog.ResolveDefaultGlyph(groupId, members);
        string? iconPath = cfg != null ? Services.GroupIconCatalog.ResolveValidIconPath(cfg) : null;
        try { _groupWindow?.Close(); } catch { }
        _groupWindow = new WidgetGroupWindow(title, members, anchor, glyph, iconPath);
        _groupWindow.Activate();
    }

    private void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> devices)
    {
        App.UiQueue?.TryEnqueue(() => RebuildDeviceMenus(devices));
    }

    private void RebuildDeviceMenus(IReadOnlyList<PeripheralDevice> devices)
    {
        Fill(MouseNav, devices.Where(d => d.Kind == DeviceKind.Mouse));
        Fill(KeyboardNav, devices.Where(d => d.Kind == DeviceKind.Keyboard));
        Fill(HeadsetNav, devices.Where(d => d.Kind == DeviceKind.Headset));
        Fill(ControllerNav, devices.Where(d => d.Kind == DeviceKind.Controller));

        MouseNav.IsExpanded = devices.Any(d => d.Kind == DeviceKind.Mouse);
        KeyboardNav.IsExpanded = devices.Any(d => d.Kind == DeviceKind.Keyboard);
        HeadsetNav.IsExpanded = devices.Any(d => d.Kind == DeviceKind.Headset);
        ControllerNav.IsExpanded = devices.Any(d => d.Kind == DeviceKind.Controller);

        if (!string.IsNullOrEmpty(_activeDeviceId) &&
            ContentFrame.CurrentSourcePageType == typeof(DeviceDetailPage))
        {
            SelectDeviceInNav(_activeDeviceId);
        }
    }

    private static void Fill(NavigationViewItem parent, IEnumerable<PeripheralDevice> devices)
    {
        parent.MenuItems.Clear();
        foreach (var d in devices.OrderBy(d => d.Name))
        {
            var battery = d.BatteryPercent is { } p ? $" · {p}%" : (d.IsSleeping ? " · 休眠" : "");
            parent.MenuItems.Add(new NavigationViewItem
            {
                Content = $"{d.Name}{battery}",
                Tag = $"device:{d.Id}",
            });
        }
        if (parent.MenuItems.Count == 0)
            parent.MenuItems.Add(new NavigationViewItem { Content = "（未发现设备）", IsEnabled = false });
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavHandler) return;
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
            return;

        if (tag is "mouse" or "keyboard" or "headset" or "controller")
        {
            item.IsExpanded = true;
            return;
        }

        if (tag.StartsWith("device:", StringComparison.Ordinal))
        {
            string devId = tag[7..];
            _activeDeviceId = devId;
            ContentFrame.Navigate(typeof(DeviceDetailPage), devId);
            return;
        }

        _activeDeviceId = null;
        Type? page = tag switch
        {
            "devices" => typeof(DevicesPage),
            "games" => typeof(GamesPage),
            "macros" => typeof(MacrosPage),
            "widget" => typeof(WidgetPage),
            "monitor" => typeof(MonitorPage),
            "diagnostics" => typeof(DiagnosticsPage),
            "settings" => typeof(SettingsPage),
            _ => null,
        };
        if (page != null && ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }
}
