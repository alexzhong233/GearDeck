using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using GearDeck.Core;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;

namespace GearDeck.Desktop.Services;

/// <summary>
/// 原生 macOS 菜单栏状态项（NSStatusItem + NSMenu）：
/// - 默认平铺展示启用外设分组图标与实时电量/状态（带等宽数字字体防抖动）；
/// - 菜单栏点击弹出动态菜单，可直接点击具体设备打开详情页、切换平铺/精简模式、一键打开输入监控设置；
/// - 注册 NSWorkspaceDidWakeNotification 唤醒回调。
/// </summary>
public sealed class MacStatusBarService : IDisposable
{
    private const string ObjCLib = "/usr/lib/libobjc.A.dylib";
    private const string AppKitLib = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint kCFStringEncodingUTF8 = 0x08000100;
    private const double NSVariableStatusItemLength = -1.0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MenuActionDelegate(IntPtr self, IntPtr cmd, IntPtr sender);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WakeNotificationDelegate(IntPtr self, IntPtr cmd, IntPtr notification);

    private static readonly object ClassGate = new();
    private static IntPtr s_targetClass;
    private static MenuActionDelegate? s_menuActionTrampoline;
    private static WakeNotificationDelegate? s_wakeTrampoline;
    private static WakeNotificationDelegate? s_powerOffTrampoline;
    private static MacStatusBarService? s_activeInstance;

    private readonly Action _onOpenMain;
    private readonly Action<string> _onOpenDevice;
    private readonly Action _onOpenGames;
    private readonly Action _onOpenMacros;
    private readonly Action _onOpenWidget;
    private readonly Action _onRefreshDevices;
    private readonly Action _onExit;
    private readonly Action? _onSystemResume;
    private readonly Action? _onSystemPowerOff;

    private readonly Dictionary<int, Action> _tagActions = new();
    private IntPtr _statusItem;
    private IntPtr _menu;
    private IntPtr _targetInstance;
    private IntPtr _monoFont;
    private IntPtr _wakeNotifName;
    private IntPtr _powerOffNotifName;
    private DispatcherTimer? _metricsTimer;
    private bool _wakeRegistered;
    private bool _disposed;
    private IReadOnlyList<PeripheralDevice> _lastDevices = [];
    private SystemMetricsSnapshot? _lastMetrics;

    public MacStatusBarService(
        Action onOpenMain,
        Action<string> onOpenDevice,
        Action onOpenGames,
        Action onOpenMacros,
        Action onOpenWidget,
        Action onRefreshDevices,
        Action onExit,
        Action? onSystemResume,
        Action? onSystemPowerOff = null)
    {
        _onOpenMain = onOpenMain;
        _onOpenDevice = onOpenDevice;
        _onOpenGames = onOpenGames;
        _onOpenMacros = onOpenMacros;
        _onOpenWidget = onOpenWidget;
        _onRefreshDevices = onRefreshDevices;
        _onExit = onExit;
        _onSystemResume = onSystemResume;
        _onSystemPowerOff = onSystemPowerOff;

        if (!OperatingSystem.IsMacOS())
            return;

        if (Dispatcher.UIThread.CheckAccess())
            InitializeNative();
        else
            Dispatcher.UIThread.Post(InitializeNative);
    }

    public void UpdateDevices(IReadOnlyList<PeripheralDevice> devices, SystemMetricsSnapshot? metrics = null)
    {
        _lastDevices = devices ?? [];
        if (metrics != null) _lastMetrics = metrics;
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed || !OperatingSystem.IsMacOS()) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }
        RefreshNative();
    }

    public void UpdateTooltip(string tip)
    {
        if (_disposed || !OperatingSystem.IsMacOS()) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateTooltip(tip));
            return;
        }
        try
        {
            if (_statusItem == IntPtr.Zero) return;
            IntPtr btn = objc_msgSend(_statusItem, sel_registerName("button"));
            if (btn == IntPtr.Zero) return;
            IntPtr nsTip = CreateNsString(tip);
            if (nsTip == IntPtr.Zero) return;
            try { objc_msgSend_ptr(btn, sel_registerName("setToolTip:"), nsTip); }
            finally { CFRelease(nsTip); }
        }
        catch { }
    }

    private void InitializeNative()
    {
        if (_disposed || _statusItem != IntPtr.Zero) return;
        try
        {
            NativeLibrary.TryLoad(AppKitLib, out _);
            EnsureTargetClass();
            s_activeInstance = this;

            _targetInstance = objc_msgSend(objc_msgSend(s_targetClass, sel_registerName("alloc")), sel_registerName("init"));

            IntPtr sysBar = objc_msgSend(objc_getClass("NSStatusBar"), sel_registerName("systemStatusBar"));
            _statusItem = objc_msgSend_double(sysBar, sel_registerName("statusItemWithLength:"), NSVariableStatusItemLength);
            if (_statusItem != IntPtr.Zero)
                _statusItem = objc_msgSend(_statusItem, sel_registerName("retain"));

            _menu = objc_msgSend(objc_msgSend(objc_getClass("NSMenu"), sel_registerName("alloc")), sel_registerName("init"));
            if (_statusItem != IntPtr.Zero && _menu != IntPtr.Zero)
                objc_msgSend_ptr(_statusItem, sel_registerName("setMenu:"), _menu);

            _monoFont = objc_msgSend_double2(
                objc_getClass("NSFont"),
                sel_registerName("monospacedDigitSystemFontOfSize:weight:"),
                12.5,
                0.23);
            if (_monoFont != IntPtr.Zero)
                _monoFont = objc_msgSend(_monoFont, sel_registerName("retain"));

            RegisterWakeNotification();
            _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _metricsTimer.Tick += (_, _) =>
            {
                if (_disposed || App.Settings?.Current.MacStatusBarShowMetrics != true) return;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var snap = DesktopMetricsEngine.Sample();
                        _lastMetrics = snap;
                        Dispatcher.UIThread.Post(RefreshNative);
                    }
                    catch { }
                });
            };
            _metricsTimer.Start();
            RefreshNative();
        }
        catch (Exception ex)
        {
            Diag.Log($"MacStatusBarService 初始化失败: {ex.Message}");
        }
    }

    private void RefreshNative()
    {
        if (_disposed || _statusItem == IntPtr.Zero || _menu == IntPtr.Zero) return;
        try
        {
            var settings = App.Settings?.Current ?? AppSettings.Default;
            bool permDenied = MacIOKitHid.HasPermissionDeniedDevices ||
                              (OperatingSystem.IsMacOS() && MacIOKitHid.CheckInputMonitoringAccess() == MacHidAccessState.Denied);

            string title = MacStatusBarFormatter.BuildTitle(_lastDevices, settings, _lastMetrics, permDenied);
            IntPtr btn = objc_msgSend(_statusItem, sel_registerName("button"));
            if (btn != IntPtr.Zero)
            {
                if (_monoFont != IntPtr.Zero)
                    objc_msgSend_ptr(btn, sel_registerName("setFont:"), _monoFont);
                IntPtr nsTitle = CreateNsString(title);
                if (nsTitle != IntPtr.Zero)
                {
                    try { objc_msgSend_ptr(btn, sel_registerName("setTitle:"), nsTitle); }
                    finally { CFRelease(nsTitle); }
                }
            }

            RebuildMenu(settings, permDenied);
        }
        catch (Exception ex)
        {
            Diag.Log($"MacStatusBarService 刷新失败: {ex.Message}");
        }
    }

    private void RebuildMenu(AppSettings settings, bool permDenied)
    {
        objc_msgSend(_menu, sel_registerName("removeAllItems"));
        _tagActions.Clear();
        int nextTag = 1;

        AddMenuItem($"GearDeck v{AppUpdateService.CurrentVersion} · {_lastDevices.Count} 台设备", _onOpenMain, ref nextTag);

        if (permDenied)
        {
            AddMenuItem("⚠ 需要输入监控权限（点击打开系统设置）", MacIOKitHid.OpenInputMonitoringSettings, ref nextTag);
        }

        if (_lastDevices.Count > 0)
        {
            AddSeparator();
            foreach (var d in _lastDevices)
            {
                string id = d.Id;
                AddMenuItem(
                    MacStatusBarFormatter.FormatDeviceMenuLine(d, settings),
                    () => _onOpenDevice(id),
                    ref nextTag);
            }
        }

        AddSeparator();
        AddMenuItem(
            "菜单栏平铺显示所有外设",
            () =>
            {
                App.Settings.Current.MacStatusBarTiled = !App.Settings.Current.MacStatusBarTiled;
                App.Settings.Save();
                RefreshNative();
            },
            ref nextTag,
            checkedState: settings.MacStatusBarTiled);

        AddMenuItem(
            "菜单栏显示系统监控摘要",
            () =>
            {
                App.Settings.Current.MacStatusBarShowMetrics = !App.Settings.Current.MacStatusBarShowMetrics;
                App.Settings.Save();
                RefreshNative();
            },
            ref nextTag,
            checkedState: settings.MacStatusBarShowMetrics);

        AddSeparator();
        AddMenuItem("立即刷新外设状态", _onRefreshDevices, ref nextTag);
        AddMenuItem("打开 GearDeck 主界面", _onOpenMain, ref nextTag);
        AddMenuItem("游戏与配置切换", _onOpenGames, ref nextTag);
        AddMenuItem("鼠标宏中心", _onOpenMacros, ref nextTag);
        AddMenuItem("菜单栏状态设置", _onOpenWidget, ref nextTag);
        AddSeparator();
        AddMenuItem("退出 GearDeck", _onExit, ref nextTag);
    }

    private void AddMenuItem(string title, Action action, ref int nextTag, bool? checkedState = null)
    {
        int tag = nextTag++;
        _tagActions[tag] = action;

        IntPtr nsTitle = CreateNsString(title);
        IntPtr emptyKey = CreateNsString(string.Empty);
        IntPtr item = objc_msgSend(objc_getClass("NSMenuItem"), sel_registerName("alloc"));
        item = objc_msgSend_initMenuItem(item, sel_registerName("initWithTitle:action:keyEquivalent:"),
            nsTitle, sel_registerName("onMenuItem:"), emptyKey);
        if (nsTitle != IntPtr.Zero) CFRelease(nsTitle);
        if (emptyKey != IntPtr.Zero) CFRelease(emptyKey);
        if (item == IntPtr.Zero) return;

        objc_msgSend_ptr(item, sel_registerName("setTarget:"), _targetInstance);
        objc_msgSend_nint_void(item, sel_registerName("setTag:"), tag);
        if (checkedState.HasValue)
            objc_msgSend_nint_void(item, sel_registerName("setState:"), checkedState.Value ? 1 : 0);

        objc_msgSend_ptr(_menu, sel_registerName("addItem:"), item);
        objc_msgSend(item, sel_registerName("release"));
    }

    private void AddSeparator()
    {
        IntPtr sep = objc_msgSend(objc_getClass("NSMenuItem"), sel_registerName("separatorItem"));
        if (sep != IntPtr.Zero)
            objc_msgSend_ptr(_menu, sel_registerName("addItem:"), sep);
    }

    private void RegisterWakeNotification()
    {
        try
        {
            IntPtr ws = objc_msgSend(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
            if (ws == IntPtr.Zero) return;
            IntPtr nc = objc_msgSend(ws, sel_registerName("notificationCenter"));
            if (nc == IntPtr.Zero) return;
            _wakeNotifName = CreateNsString("NSWorkspaceDidWakeNotification");
            if (_wakeNotifName != IntPtr.Zero)
            {
                objc_msgSend_addObserver(
                    nc,
                    sel_registerName("addObserver:selector:name:object:"),
                    _targetInstance,
                    sel_registerName("onWakeNotification:"),
                    _wakeNotifName,
                    IntPtr.Zero);
                _wakeRegistered = true;
            }

            _powerOffNotifName = CreateNsString("NSWorkspaceWillPowerOffNotification");
            if (_powerOffNotifName != IntPtr.Zero)
            {
                objc_msgSend_addObserver(
                    nc,
                    sel_registerName("addObserver:selector:name:object:"),
                    _targetInstance,
                    sel_registerName("onPowerOffNotification:"),
                    _powerOffNotifName,
                    IntPtr.Zero);
                _wakeRegistered = true;
            }
        }
        catch { }
    }

    private static void EnsureTargetClass()
    {
        lock (ClassGate)
        {
            if (s_targetClass != IntPtr.Zero) return;
            IntPtr existing = objc_lookUpClass("GearDeckMenuTarget");
            if (existing != IntPtr.Zero)
            {
                s_targetClass = existing;
                return;
            }

            IntPtr cls = objc_allocateClassPair(objc_getClass("NSObject"), "GearDeckMenuTarget", 0);
            s_menuActionTrampoline = OnObjCMenuAction;
            s_wakeTrampoline = OnObjCWakeNotification;
            s_powerOffTrampoline = OnObjCPowerOffNotification;
            class_addMethod(cls, sel_registerName("onMenuItem:"),
                Marshal.GetFunctionPointerForDelegate(s_menuActionTrampoline), "v@:@");
            class_addMethod(cls, sel_registerName("onWakeNotification:"),
                Marshal.GetFunctionPointerForDelegate(s_wakeTrampoline), "v@:@");
            class_addMethod(cls, sel_registerName("onPowerOffNotification:"),
                Marshal.GetFunctionPointerForDelegate(s_powerOffTrampoline), "v@:@");
            objc_registerClassPair(cls);
            s_targetClass = cls;
        }
    }

    private static void OnObjCMenuAction(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        var inst = s_activeInstance;
        if (inst == null || inst._disposed || sender == IntPtr.Zero) return;
        try
        {
            int tag = (int)objc_msgSend_nint_ret(sender, sel_registerName("tag"));
            if (inst._tagActions.TryGetValue(tag, out var act))
                act();
        }
        catch { }
    }

    private static void OnObjCWakeNotification(IntPtr self, IntPtr cmd, IntPtr notification)
    {
        var inst = s_activeInstance;
        if (inst == null || inst._disposed) return;
        try { inst._onSystemResume?.Invoke(); }
        catch { }
    }

    private static void OnObjCPowerOffNotification(IntPtr self, IntPtr cmd, IntPtr notification)
    {
        var inst = s_activeInstance;
        if (inst == null || inst._disposed) return;
        try { inst._onSystemPowerOff?.Invoke(); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(s_activeInstance, this))
            s_activeInstance = null;

        if (!OperatingSystem.IsMacOS()) return;
        void Cleanup()
        {
            try
            {
                _metricsTimer?.Stop();
                _metricsTimer = null;
                if (_wakeRegistered && _targetInstance != IntPtr.Zero)
                {
                    IntPtr ws = objc_msgSend(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
                    IntPtr nc = ws != IntPtr.Zero ? objc_msgSend(ws, sel_registerName("notificationCenter")) : IntPtr.Zero;
                    if (nc != IntPtr.Zero)
                        objc_msgSend_ptr(nc, sel_registerName("removeObserver:"), _targetInstance);
                }
                if (_wakeNotifName != IntPtr.Zero) { CFRelease(_wakeNotifName); _wakeNotifName = IntPtr.Zero; }
                if (_powerOffNotifName != IntPtr.Zero) { CFRelease(_powerOffNotifName); _powerOffNotifName = IntPtr.Zero; }
                if (_statusItem != IntPtr.Zero)
                {
                    IntPtr sysBar = objc_msgSend(objc_getClass("NSStatusBar"), sel_registerName("systemStatusBar"));
                    if (sysBar != IntPtr.Zero)
                        objc_msgSend_ptr(sysBar, sel_registerName("removeStatusItem:"), _statusItem);
                    objc_msgSend(_statusItem, sel_registerName("release"));
                    _statusItem = IntPtr.Zero;
                }
                if (_menu != IntPtr.Zero) { objc_msgSend(_menu, sel_registerName("release")); _menu = IntPtr.Zero; }
                if (_monoFont != IntPtr.Zero) { objc_msgSend(_monoFont, sel_registerName("release")); _monoFont = IntPtr.Zero; }
                if (_targetInstance != IntPtr.Zero) { objc_msgSend(_targetInstance, sel_registerName("release")); _targetInstance = IntPtr.Zero; }
            }
            catch { }
        }

        if (Dispatcher.UIThread.CheckAccess()) Cleanup();
        else Dispatcher.UIThread.Post(Cleanup);
    }

    private static IntPtr CreateNsString(string text)
        => CFStringCreateWithCString(IntPtr.Zero, text ?? string.Empty, kCFStringEncodingUTF8);

    [DllImport(CoreFoundationLib)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);
    [DllImport(CoreFoundationLib)] private static extern void CFRelease(IntPtr cf);

    [DllImport(ObjCLib)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjCLib)] private static extern IntPtr objc_lookUpClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjCLib)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint extraBytes);
    [DllImport(ObjCLib)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjCLib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
    [DllImport(ObjCLib)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_ptr(IntPtr receiver, IntPtr selector, IntPtr arg1);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend_double(IntPtr receiver, IntPtr selector, double arg1);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend_double2(IntPtr receiver, IntPtr selector, double arg1, double arg2);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_nint_ret(IntPtr receiver, IntPtr selector);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_nint_void(IntPtr receiver, IntPtr selector, nint arg1);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend_initMenuItem(IntPtr receiver, IntPtr selector, IntPtr title, IntPtr action, IntPtr keyEquiv);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_addObserver(IntPtr receiver, IntPtr selector, IntPtr observer, IntPtr sel, IntPtr name, IntPtr obj);
}
