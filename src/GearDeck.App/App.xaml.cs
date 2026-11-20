using System.Text.Json;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using GearDeck.App.Services;
using GearDeck.Core;
using GearDeck.Core.Models;
using GearDeck.Core.Providers;
using GearDeck.Core.Services;
using Windows.Graphics;

namespace GearDeck.App;

public partial class App : Application
{
    private const string ActivateEventName = @"Local\GearDeck.Activate";

    public static DeviceManager Devices { get; private set; } = null!;
    public static SettingsService Settings { get; private set; } = null!;
    public static AppUpdateService Updater { get; private set; } = null!;
    public static TrayIconService? TrayIcon { get; private set; }
    public static GameProfileAutoService? GameAuto { get; private set; }
    public static MacroExecutionService? MacroRunner { get; private set; }
    public static DispatcherQueue? UiQueue { get; private set; }
    public static IReadOnlyList<PeripheralDevice> LastDevices { get; private set; } = [];
    public static bool IsExiting { get; private set; }
    public static event Action? WidgetPositionChanged;

    private static Mutex? _singleInstance;
    private static EventWaitHandle? _activateEvent;
    private static DispatcherTimer? _commandTimer;
    private static long _lastCommandSeq;
    private static long _lastPositionSeq;
    private static long _startedAt;

    private static string StateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GearDeck");

    public App()
    {
        _singleInstance = new Mutex(true, @"Local\GearDeck.SingleInstance", out bool created);
        if (!created)
        {
            WakeExistingInstance();
            Environment.Exit(0);
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            Diag.LogCrash("UI.UnhandledException", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Diag.LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Diag.LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Settings = new SettingsService();
        AtkModelCatalog.SeedPersistentReceiverModels(Settings.Current.KnownReceiverModels);
        var cmdArgs = Environment.GetCommandLineArgs();
        var mirrorArg = cmdArgs.FirstOrDefault(a => a.StartsWith("--mirror=", StringComparison.OrdinalIgnoreCase));
        if (mirrorArg is { Length: > 9 })
            Settings.Current.GitHubMirrorUrl = mirrorArg[9..];
        Settings.Save();
        Updater = new AppUpdateService();
        ExternalConfiguratorGuard.AutoYieldEnabled = Settings.Current.AutoYieldToVendorSoftware;
        NativeNotificationService.Initialize();
        Devices = new DeviceManager(new IPeripheralProvider[]
        {
            new RazerProvider(), new LogitechProvider(), new AtkProvider(), new CompXProvider(),
            new MchoseProvider(), new AngryMiaoProvider(), new AulaProvider(), new RogProvider(),
            new SonyInzoneProvider(), new FlydigiProvider(),
            new CorsairProvider(), new SteelSeriesProvider(), new HyperXProvider(),
            new GamepadBatteryProvider(), new BluetoothBatteryProvider(),
        });
        Devices.DevicesUpdated += OnDevicesUpdated;
        Devices.ScanCompleted += FlushDiag;
        Devices.Start(Settings.Current.PollIntervalSeconds);

        UiQueue = DispatcherQueue.GetForCurrentThread();
        StartActivateListener();

        MainWindow.Instance ??= new MainWindow();
        bool startMinimized = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase))
            || Settings.Current.LaunchMinimizedToTray;
        if (startMinimized)
        {
            try { MainWindow.Instance.AppWindow.Hide(); }
            catch { }
        }
        else
        {
            MainWindow.ShowOrActivate();
        }

        TrayIcon = new TrayIconService(
            onOpenMain: () => UiQueue?.TryEnqueue(MainWindow.ShowOrActivate),
            onOpenGames: () => UiQueue?.TryEnqueue(() => MainWindow.Instance?.OpenGamesPage()),
            onOpenMacros: () => UiQueue?.TryEnqueue(() => MainWindow.Instance?.OpenMacrosPage()),
            onToggleWidget: () => UiQueue?.TryEnqueue(ToggleWidget),
            onRefreshDevices: () => _ = Devices.RefreshAsync(),
            onExit: () => UiQueue?.TryEnqueue(ExitApplication));

        GameAuto = new GameProfileAutoService();
        GameAuto.Start();

        MacroRunner = new MacroExecutionService();
        MacroRunner.SyncHookState();

        if (Settings.Current.WidgetVisible)
            WidgetProcess.Refresh();
        StartCommandWatcher();

        bool selfUpdateTest = cmdArgs.Any(a => string.Equals(a, "--self-update-test", StringComparison.OrdinalIgnoreCase));
        if (selfUpdateTest)
        {
            _ = RunSelfUpdateTestFlowAsync();
        }
        else if (Settings.Current.CheckUpdateOnStartup)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(4000);
                if (!IsExiting)
                    await Updater.CheckAsync();
            });
        }
    }

    private static async Task RunSelfUpdateTestFlowAsync()
    {
        string updatesDir = Path.Combine(StateDir, "updates");
        string logPath = Path.Combine(updatesDir, "self-update-test.log");
        void LogLine(string msg)
        {
            try
            {
                Directory.CreateDirectory(updatesDir);
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        try
        {
            LogLine($"START pid={Environment.ProcessId} ver={AppUpdateService.CurrentVersion} baseDir={AppContext.BaseDirectory} mirror={Settings.Current.GitHubMirrorUrl}");
            await Task.Delay(1200);
            var outcome = await Updater.CheckAsync();
            LogLine($"CHECK success={outcome.Success} hasUpdate={outcome.HasUpdate} latest={outcome.LatestVersion} usedMirror={outcome.UsedMirror} endpoint={outcome.UsedEndpoint} warn={outcome.MirrorFallbackWarning} err={outcome.ErrorMessage}");

            if (outcome.HasUpdate && outcome.Release != null)
            {
                var progress = new Progress<(double Percent, string Status)>(p =>
                {
                    if ((int)p.Percent % 25 == 0 || p.Percent >= 90)
                        LogLine($"PROGRESS {p.Percent:F0}% {p.Status}");
                });
                await Updater.DownloadAndApplyAsync(outcome.Release, progress);
            }
        }
        catch (Exception ex)
        {
            LogLine($"EXCEPTION {ex}");
        }
    }

    private static void StartActivateListener()
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
                        UiQueue?.TryEnqueue(MainWindow.ShowOrActivate);
                    }
                }
                catch
                {
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "GearDeck.ActivateListener",
        };
        t.Start();
    }

    private static void StartCommandWatcher()
    {
        _lastCommandSeq = ReadInitialSeq(Path.Combine(StateDir, "widget-command.json"));
        _lastPositionSeq = ReadInitialSeq(Path.Combine(StateDir, "widget-position.json"));
        _commandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
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

    private static void PollCommand()
    {
        try
        {
            var path = Path.Combine(StateDir, "widget-command.json");
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("Seq", out var seqEl)) return;
            long seq = seqEl.GetInt64();
            if (seq == _lastCommandSeq) return;
            string? groupId = root.TryGetProperty("GroupId", out var gEl) ? gEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(groupId)) return;

            if (groupId == "__activate__")
            {
                _lastCommandSeq = seq;
                ShowMainWindow();
                return;
            }

            if (seq < _startedAt) return;
            _lastCommandSeq = seq;

            if (groupId == "__monitor__")
            {
                MainWindow.Instance?.CloseMonitorFlyout();
                ShowMainWindow();
                MainWindow.Instance?.OpenMonitorPage();
                return;
            }

            RectInt32? anchor = null;
            if (root.TryGetProperty("X", out var xEl) && root.TryGetProperty("Y", out var yEl) &&
                root.TryGetProperty("W", out var wEl) && root.TryGetProperty("H", out var hEl))
            {
                anchor = new RectInt32(xEl.GetInt32(), yEl.GetInt32(), wEl.GetInt32(), hEl.GetInt32());
            }

            if (groupId == "__monitor_hover__")
            {
                MainWindow.Instance?.ShowMonitorFlyout(anchor);
                return;
            }
            MainWindow.Instance?.ShowGroupFlyout(groupId, anchor);
        }
        catch { }
    }

    private static void PollPosition()
    {
        try
        {
            var path = Path.Combine(StateDir, "widget-position.json");
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
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
        try
        {
            var dir = Path.Combine(StateDir, "logs");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "diag.log"), Diag.Snapshot());
        }
        catch { }
    }

    private static void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> devices)
    {
        LastDevices = devices;
        bool receiverChanged = false;
        foreach (var d in devices)
        {
            if (d.Id.StartsWith("atk:", StringComparison.OrdinalIgnoreCase)
                && !AtkModelCatalog.IsGenericReceiverName(d.Name))
            {
                if (!Settings.Current.KnownReceiverModels.TryGetValue(d.Id, out var prev)
                    || !string.Equals(prev, d.Name, StringComparison.Ordinal))
                {
                    Settings.Current.KnownReceiverModels[d.Id] = d.Name;
                    receiverChanged = true;
                }
            }
        }
        bool batChanged = Settings.RecordObservedDevices(devices);
        if (receiverChanged && !batChanged)
            Settings.Save();

        NativeNotificationService.CheckAndNotifyLowBattery(devices);
        _ = DeviceImageService.EnsureAutoCloudImagesAsync(devices);
        if (Settings.Current.WidgetVisible)
            WidgetProcess.Refresh();
    }

    public static void RefreshWidget()
    {
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
        if (Settings.Current.WidgetVisible)
            WidgetProcess.Refresh();
    }

    public static void ShowMainWindow() => MainWindow.ShowOrActivate();

    public static void Shutdown() => ExitApplication();

    public static void ExitApplication()
    {
        if (IsExiting) return;
        IsExiting = true;
        try
        {
            _commandTimer?.Stop();
            _activateEvent?.Set();
            _activateEvent?.Dispose();
            NativeNotificationService.Unregister();
            TrayIcon?.Dispose();
            GameAuto?.Dispose();
            MacroRunner?.Dispose();
            WidgetProcess.Stop();
            Devices?.Stop();
        }
        catch { }
        try { _singleInstance?.ReleaseMutex(); } catch { }
        try { _singleInstance?.Dispose(); } catch { }
        Environment.Exit(0);
    }
}
