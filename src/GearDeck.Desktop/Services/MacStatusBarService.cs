using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using GearDeck.Core;
using GearDeck.Core.Hid;
using GearDeck.Core.Models;
using SkiaSharp;

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

    public void SetDockIconVisible(bool visible)
    {
        if (_disposed || !OperatingSystem.IsMacOS()) return;
        void Apply()
        {
            if (_disposed) return;
            try
            {
                IntPtr nsApp = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                if (nsApp == IntPtr.Zero) return;
                // NSApplicationActivationPolicyRegular = 0 (Dock + Menu), NSApplicationActivationPolicyAccessory = 1 (Menu bar only)
                objc_msgSend_nint_void(nsApp, sel_registerName("setActivationPolicy:"), visible ? 0 : 1);
                if (visible)
                    objc_msgSend_bool_void(nsApp, sel_registerName("activateIgnoringOtherApps:"), true);
            }
            catch (Exception ex)
            {
                Diag.Log($"MacStatusBarService 切换 Dock 图标可见性失败: {ex.Message}");
            }
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
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
                if (_disposed || !IsBarMonitorActive(App.Settings?.Current))
                {
                    _metricsTimer?.Stop();
                    return;
                }
                SampleAndRefreshMetricsAsync();
            };
            SyncMetricsTimerState(IsBarMonitorActive(App.Settings?.Current));
            RefreshNative();
        }
        catch (Exception ex)
        {
            Diag.Log($"MacStatusBarService 初始化失败: {ex.Message}");
        }
    }

    private static bool IsBarMonitorActive(AppSettings? settings)
        => settings is { MonitorCollectionEnabled: true, MonitorEnabled: true };

    private void SyncMetricsTimerState(bool enabled)
    {
        if (!enabled)
        {
            _metricsTimer?.Stop();
            _lastMetrics = null;
            return;
        }
        if (_metricsTimer is { IsEnabled: false })
            _metricsTimer.Start();
        if (_lastMetrics == null)
            SampleAndRefreshMetricsAsync();
    }

    private void SampleAndRefreshMetricsAsync()
    {
        var settings = App.Settings?.Current ?? AppSettings.Default;
        if (!IsBarMonitorActive(settings)) return;
        var scope = SystemMetricsEngine.FromMetricIds(settings.MonitorTopMetric, settings.MonitorBottomMetric);
        if (scope == MetricScope.None) return;

        _ = Task.Run(() =>
        {
            try
            {
                if (_disposed || !IsBarMonitorActive(App.Settings?.Current)) return;
                var snap = DesktopMetricsEngine.Sample(scope);
                _lastMetrics = snap;
                Dispatcher.UIThread.Post(RefreshNative);
            }
            catch { }
        });
    }

    private void RefreshNative()
    {
        if (_disposed || _statusItem == IntPtr.Zero || _menu == IntPtr.Zero) return;
        try
        {
            var settings = App.Settings?.Current ?? AppSettings.Default;
            SyncMetricsTimerState(IsBarMonitorActive(settings));

            bool permDenied = MacIOKitHid.HasPermissionDeniedDevices ||
                              (OperatingSystem.IsMacOS() && MacIOKitHid.CheckInputMonitoringAccess() == MacHidAccessState.Denied);

            var layout = MacStatusBarFormatter.BuildLayout(_lastDevices, settings, _lastMetrics, permDenied);
            IntPtr btn = objc_msgSend(_statusItem, sel_registerName("button"));
            if (btn != IntPtr.Zero)
            {
                if (MacStatusBarFormatter.HasStackedMonitor(layout) &&
                    RenderStatusBarTemplatePng(layout) is { } rendered)
                {
                    IntPtr nsImage = CreateTemplateNsImage(rendered.PngBytes, rendered.WidthPt, rendered.HeightPt);
                    if (nsImage != IntPtr.Zero)
                    {
                        try
                        {
                            objc_msgSend_ptr(btn, sel_registerName("setImage:"), nsImage);
                            IntPtr emptyStr = CreateNsString(string.Empty);
                            if (emptyStr != IntPtr.Zero)
                            {
                                try { objc_msgSend_ptr(btn, sel_registerName("setTitle:"), emptyStr); }
                                finally { CFRelease(emptyStr); }
                            }
                        }
                        finally
                        {
                            objc_msgSend(nsImage, sel_registerName("release"));
                        }
                        RebuildMenu(settings, permDenied);
                        return;
                    }
                }

                objc_msgSend_ptr(btn, sel_registerName("setImage:"), IntPtr.Zero);
                if (_monoFont != IntPtr.Zero)
                    objc_msgSend_ptr(btn, sel_registerName("setFont:"), _monoFont);
                string title = MacStatusBarFormatter.BuildTitle(_lastDevices, settings, _lastMetrics, permDenied);
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

    public static (byte[] PngBytes, double WidthPt, double HeightPt)? RenderStatusBarTemplatePng(
        MacStatusBarLayout layout,
        SKColor? inkColor = null)
    {
        if (layout == null) return null;

        const int scale = 2;
        const int heightPx = 44; // 22pt @2x Retina
        const float padX = 6f;
        const float iconSize = 26f;
        const float iconGap = 6f;
        const float segGap = 18f;

        SKColor ink = inkColor ?? SKColors.Black;

        using var monoTypeface = SKTypeface.FromFamilyName("SF Mono", SKFontStyle.Bold)
            ?? SKTypeface.FromFamilyName("Menlo", SKFontStyle.Bold)
            ?? SKTypeface.Default;
        using var singlePaint = new SKPaint
        {
            Typeface = monoTypeface,
            TextSize = 25f,
            Color = ink,
            IsAntialias = true,
            SubpixelText = true,
            Style = SKPaintStyle.Fill,
        };
        using var stackedPaint = new SKPaint
        {
            Typeface = monoTypeface,
            TextSize = 19f,
            Color = ink,
            IsAntialias = true,
            SubpixelText = true,
            Style = SKPaintStyle.Fill,
        };
        using var iconTextPaint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = 23f,
            Color = ink,
            IsAntialias = true,
            SubpixelText = true,
            Style = SKPaintStyle.Fill,
        };
        using var fillPaint = new SKPaint { Color = ink, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var strokePaint = new SKPaint
        {
            Color = ink,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.6f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };

        float totalW = padX;
        var slots = layout.Slots;
        if (slots.Count == 0)
        {
            totalW += MeasureSegmentedText(layout.FallbackText, singlePaint);
        }
        else
        {
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                float w;
                if (slot.IsMonitor)
                {
                    bool hasTop = !string.IsNullOrWhiteSpace(slot.MonitorTopText);
                    bool hasBot = !string.IsNullOrWhiteSpace(slot.MonitorBottomText);
                    w = hasTop && hasBot
                        ? Math.Max(
                            MeasureSegmentedText(slot.MonitorTopText!, stackedPaint),
                            MeasureSegmentedText(slot.MonitorBottomText!, stackedPaint))
                        : MeasureSegmentedText((hasTop ? slot.MonitorTopText : slot.MonitorBottomText) ?? "", singlePaint);
                }
                else if (slot.Device is { } d)
                {
                    bool hasIcon = !string.IsNullOrEmpty(d.Symbol);
                    w = (hasIcon ? iconSize + iconGap : 0f) + MeasureSegmentedText(d.ValueText, singlePaint);
                }
                else
                {
                    continue;
                }

                totalW += w;
                if (i < slots.Count - 1)
                    totalW += segGap;
            }
        }

        totalW += padX;
        int widthPx = Math.Clamp((int)Math.Ceiling(totalW), 24, 1600);
        if (widthPx % 2 != 0) widthPx++;

        using var bmp = new SKBitmap(widthPx, heightPx, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            float x = padX;

            if (slots.Count == 0)
            {
                DrawTextCenteredY(canvas, layout.FallbackText, x, heightPx * 0.5f, singlePaint);
            }
            else
            {
                for (int i = 0; i < slots.Count; i++)
                {
                    var slot = slots[i];
                    if (slot.IsMonitor)
                    {
                        bool hasTop = !string.IsNullOrWhiteSpace(slot.MonitorTopText);
                        bool hasBot = !string.IsNullOrWhiteSpace(slot.MonitorBottomText);
                        if (hasTop && hasBot)
                        {
                            float wTop = DrawTextCenteredY(canvas, slot.MonitorTopText!, x, 11.5f, stackedPaint);
                            float wBot = DrawTextCenteredY(canvas, slot.MonitorBottomText!, x, 32.5f, stackedPaint);
                            x += Math.Max(wTop, wBot) + segGap;
                        }
                        else if (hasTop || hasBot)
                        {
                            string line = hasTop ? slot.MonitorTopText! : slot.MonitorBottomText!;
                            float w = DrawTextCenteredY(canvas, line, x, heightPx * 0.5f, singlePaint);
                            x += w + segGap;
                        }
                    }
                    else if (slot.Device is { } d)
                    {
                        if (!string.IsNullOrEmpty(d.Symbol))
                        {
                            DrawDeviceVectorOrSymbol(canvas, d, x, heightPx * 0.5f, iconSize, iconTextPaint, fillPaint, strokePaint);
                            x += iconSize + iconGap;
                        }
                        float textW = DrawTextCenteredY(canvas, d.ValueText, x, heightPx * 0.5f, singlePaint);
                        x += textW + segGap;
                    }
                }
            }
            canvas.Flush();
        }

        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        if (data == null) return null;
        return (data.ToArray(), widthPx / (double)scale, heightPx / (double)scale);
    }

    private static float MeasureSegmentedText(string text, SKPaint primaryPaint)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var origTf = primaryPaint.Typeface;
        float total = 0f;
        try
        {
            foreach (var rune in text.EnumerateRunes())
            {
                string s = rune.ToString();
                primaryPaint.Typeface = origTf != null && origTf.ContainsGlyph(rune.Value)
                    ? origTf
                    : SKFontManager.Default.MatchCharacter(rune.Value) ?? origTf;
                total += primaryPaint.MeasureText(s);
            }
        }
        finally
        {
            primaryPaint.Typeface = origTf;
        }
        return total;
    }

    private static float DrawTextCenteredY(SKCanvas canvas, string text, float x, float centerY, SKPaint primaryPaint)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var origTf = primaryPaint.Typeface;
        var m = primaryPaint.FontMetrics;
        float baseline = centerY - (m.Ascent + m.Descent) * 0.5f;
        float curX = x;
        try
        {
            foreach (var rune in text.EnumerateRunes())
            {
                string s = rune.ToString();
                primaryPaint.Typeface = origTf != null && origTf.ContainsGlyph(rune.Value)
                    ? origTf
                    : SKFontManager.Default.MatchCharacter(rune.Value) ?? origTf;
                canvas.DrawText(s, curX, baseline, primaryPaint);
                curX += primaryPaint.MeasureText(s);
            }
        }
        finally
        {
            primaryPaint.Typeface = origTf;
        }
        return curX - x;
    }

    private static void DrawDeviceVectorOrSymbol(
        SKCanvas canvas,
        MacStatusBarDeviceItem item,
        float left,
        float centerY,
        float size,
        SKPaint symbolPaint,
        SKPaint fill,
        SKPaint stroke)
    {
        if (!string.IsNullOrEmpty(item.CustomGlyph))
        {
            DrawTextCenteredY(canvas, item.Symbol, left, centerY, symbolPaint);
            return;
        }

        float cx = left + size * 0.5f;
        float cy = centerY;
        switch (item.Kind)
        {
            case DeviceKind.Mouse:
                canvas.DrawRoundRect(new SKRect(cx - 6.5f, cy - 10f, cx + 6.5f, cy + 10f), 6.2f, 6.2f, stroke);
                canvas.DrawLine(cx, cy - 6.2f, cx, cy - 2.2f, stroke);
                break;
            case DeviceKind.Keyboard:
                canvas.DrawRoundRect(new SKRect(cx - 11f, cy - 7f, cx + 11f, cy + 7f), 2.5f, 2.5f, stroke);
                canvas.DrawLine(cx - 7f, cy - 2.5f, cx - 3.5f, cy - 2.5f, stroke);
                canvas.DrawLine(cx - 1f, cy - 2.5f, cx + 2.5f, cy - 2.5f, stroke);
                canvas.DrawLine(cx + 5f, cy - 2.5f, cx + 7f, cy - 2.5f, stroke);
                canvas.DrawLine(cx - 6f, cy + 2.5f, cx + 6f, cy + 2.5f, stroke);
                break;
            case DeviceKind.Headset:
                using (var arc = new SKPath())
                {
                    arc.AddArc(new SKRect(cx - 9f, cy - 9f, cx + 9f, cy + 7f), 180, 180);
                    canvas.DrawPath(arc, stroke);
                }
                canvas.DrawRoundRect(new SKRect(cx - 10.5f, cy - 1f, cx - 6.5f, cy + 8.5f), 2f, 2f, fill);
                canvas.DrawRoundRect(new SKRect(cx + 6.5f, cy - 1f, cx + 10.5f, cy + 8.5f), 2f, 2f, fill);
                break;
            case DeviceKind.Controller:
                canvas.DrawRoundRect(new SKRect(cx - 11f, cy - 7f, cx + 11f, cy + 6.5f), 5.5f, 5.5f, stroke);
                canvas.DrawLine(cx - 6.5f, cy - 0.5f, cx - 2.5f, cy - 0.5f, stroke);
                canvas.DrawLine(cx - 4.5f, cy - 2.5f, cx - 4.5f, cy + 1.5f, stroke);
                canvas.DrawCircle(cx + 5f, cy - 1f, 1.5f, fill);
                break;
            default:
                DrawTextCenteredY(canvas, item.Symbol, left, centerY, symbolPaint);
                break;
        }
    }

    private static IntPtr CreateTemplateNsImage(byte[] pngBytes, double widthPt, double heightPt)
    {
        if (pngBytes.Length == 0) return IntPtr.Zero;
        IntPtr mem = Marshal.AllocHGlobal(pngBytes.Length);
        try
        {
            Marshal.Copy(pngBytes, 0, mem, pngBytes.Length);
            IntPtr nsData = objc_msgSend(objc_getClass("NSData"), sel_registerName("alloc"));
            nsData = objc_msgSend_initWithBytesLength(nsData, sel_registerName("initWithBytes:length:"), mem, (nuint)pngBytes.Length);
            if (nsData == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                IntPtr img = objc_msgSend(objc_getClass("NSImage"), sel_registerName("alloc"));
                img = objc_msgSend_ptr_ret(img, sel_registerName("initWithData:"), nsData);
                if (img != IntPtr.Zero)
                {
                    objc_msgSend_cgsize(img, sel_registerName("setSize:"), widthPt, heightPt);
                    objc_msgSend_bool_void(img, sel_registerName("setTemplate:"), true);
                }
                return img;
            }
            finally
            {
                objc_msgSend(nsData, sel_registerName("release"));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
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
            "菜单栏显示系统监控分组",
            App.ToggleMonitor,
            ref nextTag,
            checkedState: IsBarMonitorActive(settings));

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
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend_ptr_ret(IntPtr receiver, IntPtr selector, IntPtr arg1);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend_initWithBytesLength(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_cgsize(IntPtr receiver, IntPtr selector, double width, double height);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend_double(IntPtr receiver, IntPtr selector, double arg1);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend_double2(IntPtr receiver, IntPtr selector, double arg1, double arg2);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_nint_ret(IntPtr receiver, IntPtr selector);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_nint_void(IntPtr receiver, IntPtr selector, nint arg1);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_bool_void(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg1);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend_initMenuItem(IntPtr receiver, IntPtr selector, IntPtr title, IntPtr action, IntPtr keyEquiv);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_addObserver(IntPtr receiver, IntPtr selector, IntPtr observer, IntPtr sel, IntPtr name, IntPtr obj);
}
