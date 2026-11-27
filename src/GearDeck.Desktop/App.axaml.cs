using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GearDeck.Core;
using GearDeck.Core.Models;
using GearDeck.Core.Providers;
using GearDeck.Core.Services;
using GearDeck.Desktop.Services;
using GearDeck.Desktop.Windows;

namespace GearDeck.Desktop;

public partial class App : Application
{
    private const string ActivateEventName = @"Local\GearDeck.Desktop.Activate";

    private MainWindow? _mainWindow;
    private static Mutex? _singleInstance;
    private static FileStream? _macLockStream;
    private static EventWaitHandle? _activateEvent;
    private static DispatcherTimer? _commandTimer;
    private static FileSystemWatcher? _commandWatcher;
    private static long _lastCommandSeq;
    private static long _lastPositionSeq;
    private static DateTime _lastCommandWriteUtc;
    private static DateTime _lastPositionWriteUtc;
    private static long _startedAt;
    private static long _lastResumeRefreshTicks;
    private static volatile bool _systemShuttingDown;

    public static DeviceManager Devices { get; private set; } = null!;
    public static SettingsService Settings { get; private set; } = null!;
    public static AppUpdateService Updater { get; private set; } = null!;
    public static TrayHostService? Tray { get; private set; }
    public static GameProfileAutoService? GameAuto { get; private set; }
    public static MacroExecutionService? MacroRunner { get; private set; }
    public static IReadOnlyList<PeripheralDevice> LastDevices { get; private set; } = [];
    public static bool IsExiting { get; private set; }
    public static event Action? WidgetPositionChanged;

    private static string StateDir => Path.Combine(
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GearDeck");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        try { Environment.CurrentDirectory = AppContext.BaseDirectory; } catch { }

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Diag.LogCrash("UIThread.UnhandledException", e.Exception);
            e.Handled = true;
        };
        MacAccessibilityGuard.Initialize();

        var cmdArgs = Environment.GetCommandLineArgs();
        bool waitRestart = cmdArgs.Any(a => string.Equals(a, "--wait-restart", StringComparison.OrdinalIgnoreCase));

        if (OperatingSystem.IsWindows())
        {
            _singleInstance = new Mutex(true, @"Local\GearDeck.SingleInstance", out bool created);
            if (!created && waitRestart)
            {
                try { created = _singleInstance.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { created = true; }
            }
            if (!created)
            {
                _singleInstance.Dispose();
                _singleInstance = null;
                WakeExistingInstance();
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
                    d.Shutdown(0);
                Environment.Exit(0);
                return;
            }
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (!TryAcquireMacSingleInstanceLock())
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
                    d.Shutdown(0);
                Environment.Exit(0);
                return;
            }
        }

        _startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Settings = new SettingsService();
        Diag.VerboseEnabled = Settings.Current.DebugModeEnabled;
        AtkModelCatalog.SeedPersistentReceiverModels(Settings.Current.KnownReceiverModels);
        ReceiverModelRegistry.Seed(Settings.Current.KnownReceiverModels, Settings.Current.DeviceCustomNames);

        var mirrorArg = cmdArgs.FirstOrDefault(a => a.StartsWith("--mirror=", StringComparison.OrdinalIgnoreCase));
        if (mirrorArg is { Length: > 9 })
            Settings.Current.GitHubMirrorUrl = mirrorArg[9..];
        Settings.Save();

        Updater = new AppUpdateService();
        ExternalConfiguratorGuard.AutoYieldEnabled = Settings.Current.AutoYieldToVendorSoftware;
        NativeNotificationService.Initialize();

        Devices = new DeviceManager(new IPeripheralProvider[]
        {
            new RazerProvider(),
            new LogitechProvider(),
            new AtkProvider(),
            new CompXProvider(),
            new MchoseProvider(),
            new AngryMiaoProvider(),
            new AulaProvider(),
            new RogProvider(),
            new SonyInzoneProvider(),
            new FlydigiProvider(),
            new CorsairProvider(),
            new SteelSeriesProvider(),
            new HyperXProvider(),
            new AudezeProvider(),
            new MloongProvider(),
            new LeobogProvider(),
            new SinowealthProvider(),
            new RedragonProvider(),
            new OpenMouseProvider(),
            new ViaKeyboardProvider(),
            new GamepadBatteryProvider(),
            new BluetoothBatteryProvider(),
        });
        Devices.DevicesUpdated += OnDevicesUpdated;
        Devices.ScanCompleted += FlushDiag;
        Devices.Start(Settings.Current.PollIntervalSeconds);

        GameAuto = new GameProfileAutoService();
        GameAuto.Start();

        MacroRunner = new MacroExecutionService();
        MacroRunner.SyncHookState();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += OnDesktopShutdownRequested;

            bool startMinimized = cmdArgs.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase))
                || Settings.Current.LaunchMinimizedToTray;
            if (!startMinimized)
                ShowMainWindowCore();
        }

        Tray = new TrayHostService(
            onOpenMain: () => UiPost(ShowMainWindowCore),
            onOpenGames: () => UiPost(() => { ShowMainWindowCore(); _mainWindow?.OpenGamesPage(); }),
            onOpenMacros: () => UiPost(() => { ShowMainWindowCore(); _mainWindow?.OpenMacrosPage(); }),
            onToggleWidget: () => UiPost(ToggleWidget),
            onRefreshDevices: RequestRefresh,
            onExit: () => UiPost(() => ExitCore()),
            onSystemResume: ScheduleResumeRefresh,
            onOpenDevice: id => UiPost(() => { ShowMainWindowCore(); _mainWindow?.OpenDevice(id); }),
            onOpenWidget: () => UiPost(() => { ShowMainWindowCore(); _mainWindow?.OpenWidgetPage(); }),
            onSystemPowerOff: () => _systemShuttingDown = true);

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                if (this.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                {
                    activatable.Activated += (_, args) =>
                    {
                        if (args.Kind == ActivationKind.Reopen)
                            UiPost(ShowMainWindowCore);
                    };
                }
            }
            catch { }
        }

        if (OperatingSystem.IsWindows())
        {
            StartActivateListener();
            if (Settings.Current.WidgetVisible)
                WidgetProcess.Refresh();
            StartCommandWatcher();
        }

        if (Settings.Current.CheckUpdateOnStartup)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(4000);
                if (!IsExiting)
                    await Updater.CheckAsync();
            });
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(8000);
            if (!IsExiting && _mainWindow == null)
                TrimIdleProcessMemory();
        });

        base.OnFrameworkInitializationCompleted();
    }

    public static void UiPost(Action action) => Dispatcher.UIThread.Post(action);

    private static void WakeExistingInstance()
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            string path = Path.Combine(StateDir, "widget-command.json");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new
            {
                GroupId = "__activate__",
                Seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                X = 0,
                Y = 0,
                W = 0,
                H = 0,
            }));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var ev))
                {
                    using (ev)
                        ev.Set();
                }
            }
            catch { }
        }
    }

    private void StartActivateListener()
    {
        if (_activateEvent == null) return;
        var t = new Thread(() =>
        {
            while (!IsExiting && _activateEvent != null)
            {
                try
                {
                    if (_activateEvent.WaitOne())
                    {
                        if (IsExiting) break;
                        UiPost(ShowMainWindowCore);
                    }
                }
                catch { break; }
            }
        })
        {
            IsBackground = true,
            Name = "GearDeck.Desktop.ActivateListener",
        };
        t.Start();
    }

    private void StartCommandWatcher()
    {
        string cmdPath = Path.Combine(StateDir, "widget-command.json");
        string posPath = Path.Combine(StateDir, "widget-position.json");
        _lastCommandSeq = ReadInitialSeq(cmdPath);
        _lastPositionSeq = ReadInitialSeq(posPath);
        try { if (File.Exists(cmdPath)) _lastCommandWriteUtc = File.GetLastWriteTimeUtc(cmdPath); } catch { }
        try { if (File.Exists(posPath)) _lastPositionWriteUtc = File.GetLastWriteTimeUtc(posPath); } catch { }

        try
        {
            Directory.CreateDirectory(StateDir);
            _commandWatcher = new FileSystemWatcher(StateDir, "widget-*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler onFs = (_, _) => UiPost(() => { PollCommand(); PollPosition(); });
            _commandWatcher.Changed += onFs;
            _commandWatcher.Created += onFs;
            _commandWatcher.Renamed += (_, _) => UiPost(() => { PollCommand(); PollPosition(); });
        }
        catch { }

        _commandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _commandTimer.Tick += (_, _) => { PollCommand(); PollPosition(); };
        _commandTimer.Start();
    }

    private static long ReadInitialSeq(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("Seq", out var seqEl))
                    return seqEl.GetInt64();
            }
        }
        catch { }
        return 0;
    }

    private void PollCommand()
    {
        try
        {
            var path = Path.Combine(StateDir, "widget-command.json");
            if (!File.Exists(path)) return;
            DateTime writeUtc = File.GetLastWriteTimeUtc(path);
            if (writeUtc == _lastCommandWriteUtc) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            _lastCommandWriteUtc = writeUtc;
            var root = doc.RootElement;
            if (!root.TryGetProperty("Seq", out var seqEl)) return;
            long seq = seqEl.GetInt64();
            if (seq == _lastCommandSeq) return;
            string? groupId = root.TryGetProperty("GroupId", out var gEl) ? gEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(groupId)) return;

            if (groupId == "__activate__")
            {
                _lastCommandSeq = seq;
                ShowMainWindowCore();
                return;
            }

            if (seq < _startedAt) return;
            _lastCommandSeq = seq;

            Rect? anchor = null;
            if (root.TryGetProperty("X", out var xEl) && root.TryGetProperty("Y", out var yEl) &&
                root.TryGetProperty("W", out var wEl) && root.TryGetProperty("H", out var hEl))
            {
                anchor = new Rect(xEl.GetInt32(), yEl.GetInt32(), wEl.GetInt32(), hEl.GetInt32());
            }

            if (groupId == "__monitor__")
            {
                WidgetMonitorWindow.ShowOrUpdate(anchor, isHover: false);
                return;
            }

            if (groupId == "__monitor_hover__")
            {
                if (Settings.Current.WidgetFlyoutOnHover)
                    WidgetMonitorWindow.ShowOrUpdate(anchor, isHover: true);
                return;
            }

            if (groupId.StartsWith("__group_hover__:", StringComparison.Ordinal))
            {
                if (Settings.Current.WidgetFlyoutOnHover)
                {
                    string actualGroupId = groupId["__group_hover__:".Length..];
                    if (!string.IsNullOrWhiteSpace(actualGroupId))
                        WidgetGroupWindow.ShowStandalone(actualGroupId, anchor, isHover: true);
                }
                return;
            }

            WidgetGroupWindow.ShowStandalone(groupId, anchor, isHover: false);
        }
        catch { }
    }

    private static void PollPosition()
    {
        try
        {
            var path = Path.Combine(StateDir, "widget-position.json");
            if (!File.Exists(path)) return;
            DateTime writeUtc = File.GetLastWriteTimeUtc(path);
            if (writeUtc == _lastPositionWriteUtc) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            _lastPositionWriteUtc = writeUtc;
            var root = doc.RootElement;
            if (!root.TryGetProperty("Seq", out var seqEl)) return;
            long seq = seqEl.GetInt64();
            if (seq == _lastPositionSeq || seq < _startedAt) return;
            _lastPositionSeq = seq;

            bool changed = false;
            if (root.TryGetProperty("Align", out var aEl) && root.TryGetProperty("Offset", out var oEl))
            {
                string? align = aEl.GetString();
                int offset = oEl.GetInt32();
                if (align is "left" or "right" && (Settings.Current.WidgetAlignment != align || Settings.Current.WidgetOffsetX != offset))
                {
                    Settings.Current.WidgetAlignment = align;
                    Settings.Current.WidgetOffsetX = offset;
                    changed = true;
                }
            }
            if (root.TryGetProperty("FloatX", out var fxEl) && root.TryGetProperty("FloatY", out var fyEl))
            {
                int fx = fxEl.GetInt32();
                int fy = fyEl.GetInt32();
                if (fx >= -10000 && fy >= -10000 && (Settings.Current.WidgetFloatX != fx || Settings.Current.WidgetFloatY != fy))
                {
                    Settings.Current.WidgetFloatX = fx;
                    Settings.Current.WidgetFloatY = fy;
                    changed = true;
                }
            }
            if (changed)
            {
                Settings.Save();
                WidgetProcess.Refresh();
                WidgetPositionChanged?.Invoke();
            }
        }
        catch { }
    }

    private static void FlushDiag()
    {
        if (!Settings.Current.DebugModeEnabled)
            return;
        try
        {
            var dir = Path.Combine(StateDir, "logs");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "diag.log"), Diag.Snapshot());
        }
        catch { }
    }

    private static void ScheduleResumeRefresh()
    {
        if (IsExiting) return;
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastResumeRefreshTicks) < 3000)
            return;
        Interlocked.Exchange(ref _lastResumeRefreshTicks, now);

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            if (!IsExiting)
                await Devices.RefreshAsync();
        });
    }

    private static bool TryAcquireMacSingleInstanceLock()
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            string lockPath = Path.Combine(StateDir, ".geardeck.lock");
            _macLockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            byte[] pidBytes = System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
            _macLockStream.SetLength(0);
            _macLockStream.Write(pidBytes, 0, pidBytes.Length);
            _macLockStream.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void UpdateTrayTooltip(IReadOnlyList<PeripheralDevice> devices)
    {
        const string fallback = "GearDeck · 外设调节与游戏配置";
        var parts = new List<string>(3);
        foreach (var d in devices)
        {
            if (d.IsSleeping) continue;

            string? percentText;
            if (d.LeftEarPercent is >= 0 and <= 100 || d.RightEarPercent is >= 0 and <= 100)
                percentText = d.FormatDualEarSummary(compact: true);
            else if (d.BatteryPercent is >= 0 and <= 100)
                percentText = $"{d.BatteryPercent.Value}%" + (d.State == BatteryState.Charging ? "⚡" : "");
            else
                continue;

            if (string.IsNullOrEmpty(percentText)) continue;
            string name = string.IsNullOrWhiteSpace(d.Name) ? d.Kind.ToString() : d.Name;
            parts.Add($"{name}: {percentText}");
            if (parts.Count >= 3) break;
        }

        string tip = parts.Count > 0 ? string.Join(" | ", parts) : fallback;
        if (tip.Length > 120)
            tip = tip[..120];
        Tray?.UpdateTooltip(tip);
    }

    private static void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> devices)
    {
        LastDevices = devices;
        bool receiverChanged = Settings.UpdateKnownReceiverModels(devices);
        bool batChanged = Settings.RecordObservedDevices(devices);
        if (receiverChanged && !batChanged)
            Settings.Save();

        NativeNotificationService.CheckAndNotifyLowBattery(devices);
        _ = DeviceImageService.EnsureAutoCloudImagesAsync(devices);
        UpdateTrayTooltip(devices);
        Tray?.UpdateDevices(devices);
        if (OperatingSystem.IsWindows() && Settings.Current.WidgetVisible)
            WidgetProcess.Refresh();
    }

    public static void RequestRefresh() => _ = Devices?.RefreshAsync();

    public static void RefreshWidget()
    {
        if (OperatingSystem.IsMacOS())
        {
            Tray?.RefreshStatusBar();
            return;
        }
        if (!OperatingSystem.IsWindows()) return;
        if (Settings.Current.WidgetVisible) WidgetProcess.Refresh();
        else WidgetProcess.Stop();
    }

    public static void ToggleWidget()
    {
        Settings.Current.WidgetVisible = !Settings.Current.WidgetVisible;
        Settings.Save();
        RefreshWidget();
    }

    public static void ToggleMonitor()
    {
        Settings.Current.MonitorEnabled = !Settings.Current.MonitorEnabled;
        Settings.Save();
        if (OperatingSystem.IsWindows() && Settings.Current.WidgetVisible)
            WidgetProcess.Refresh();
    }

    public static void ShowMainWindow() => (Application.Current as App)?.ShowMainWindowCore();

    public static void Shutdown() => Exit();

    public static void Exit() => (Application.Current as App)?.ExitCore();

    public static bool ShouldCancelMacShutdown(bool isMacOs, bool isExiting, bool systemShuttingDown, bool closeToTray)
        => isMacOs && !isExiting && !systemShuttingDown && closeToTray;

    private void OnDesktopShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (ShouldCancelMacShutdown(
                OperatingSystem.IsMacOS(),
                IsExiting,
                _systemShuttingDown,
                Settings?.Current.CloseToTray == true))
        {
            e.Cancel = true;
            UiPost(HideMainWindowToTray);
            return;
        }

        if (!IsExiting)
            ExitCore(invokeDesktopShutdown: false);
    }

    private void HideMainWindowToTray()
    {
        if (IsExiting || _mainWindow is null) return;
        try
        {
            _mainWindow.Close();
        }
        catch { }
    }

    private void ShowMainWindowCore()
    {
        if (IsExiting) return;

        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closed += OnMainWindowClosed;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (IsExiting) return;
        if (!Settings.Current.CloseToTray)
        {
            ExitCore();
            return;
        }

        if (_mainWindow is not null)
        {
            _mainWindow.Closed -= OnMainWindowClosed;
            (_mainWindow as IDisposable)?.Dispose();
            _mainWindow = null;
            ScheduleClosedTrim();
        }
    }

    private void ScheduleClosedTrim()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            if (IsExiting || _mainWindow is not null)
                return;
            TrimIdleProcessMemory();
        });
    }

    private void TrimIdleProcessMemory()
    {
        if (IsExiting || _mainWindow is not null)
            return;
        try
        {
            bool noOpenWindows = ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime d
                || d.Windows.Count == 0;
            if (noOpenWindows)
                DeviceImageService.ClearBitmapCache();

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            if (OperatingSystem.IsWindows())
            {
                using var proc = Process.GetCurrentProcess();
                _ = SetProcessWorkingSetSize(proc.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
        }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    private void ExitCore(bool invokeDesktopShutdown = true)
    {
        if (IsExiting) return;
        IsExiting = true;

        try { _singleInstance?.ReleaseMutex(); } catch { }
        try { _singleInstance?.Dispose(); _singleInstance = null; } catch { }

        try
        {
            _commandTimer?.Stop();
            _commandWatcher?.Dispose();
            _commandWatcher = null;
            _activateEvent?.Set();
            _activateEvent?.Dispose();
            NativeNotificationService.Unregister();
            Tray?.Dispose();
            Tray = null;
            GameAuto?.Dispose();
            MacroRunner?.Dispose();
            WidgetProcess.Stop();
            Devices?.Stop();
        }
        catch { }

        if (_mainWindow is not null)
        {
            _mainWindow.Closed -= OnMainWindowClosed;
            try { _mainWindow.Close(); } catch { }
            _mainWindow = null;
        }

        try { Settings?.Save(); } catch { }
        try { _macLockStream?.Dispose(); _macLockStream = null; } catch { }

        if (invokeDesktopShutdown && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
