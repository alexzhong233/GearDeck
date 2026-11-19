using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GearDeck.Core;
using GearDeck.Core.Models;

namespace GearDeck.App.Services;

public sealed record RunningWindowProcess(string ProcessName, string WindowTitle);

/// <summary>
/// 前台游戏 / 进程 / 独占全屏监测与按鼠标独立切换配置文件引擎：
/// - 仅对当前处于唤醒在线状态 (!IsSleeping) 的鼠标/手柄执行配置切换，绝不阻塞或误报错已休眠的无线设备；
/// - 当开启“窗口/游戏未退出时保持当前配置”且全屏独占窗口或游戏进程仍在运行时（例如 Alt+Tab 临时切出），不会切换回默认配置；
/// - 当休眠设备被晃动唤醒后，自动为其补切当前生效的游戏/全屏/桌面配置；
/// - 每次实际生效切换后可弹出 Windows 原生通知告知用户具体设备切到了哪个预设/槽位。
/// </summary>
public sealed class GameProfileAutoService : IDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private const uint MonitorDefaultToNearest = 2;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsCaption = 0x00C00000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int DwmwaCloaked = 14;

    private static readonly HashSet<string> ExcludedFullscreenClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland", "ForegroundStaging", "NotifyIconOverflowWindow",
    };

    private static readonly HashSet<string> IgnoredPickerProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "GearDeck.App.exe", "GearDeck.Widget.exe", "TextInputHost.exe",
        "SearchHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "explorer.exe", "ApplicationFrameHost.exe",
    };

    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly Dictionary<string, string> _lastAppliedByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastOutcomeByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lastAwakeMouseIds = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private int _ticking;
    private volatile bool _disposed;

    private string? _pendingRuleKey;
    private long _pendingSinceMs;
    private GameProfileResolution? _pendingResolution;
    private GameProfileResolution? _lastResolution;

    private IntPtr _latchedHwnd;
    private uint _latchedPid;
    private string _latchedProcName = "";
    private List<string> _latchedRuleProcs = [];
    private GameProfileResolution? _latchedResolution;

    public string LastForegroundProcess { get; private set; } = "";
    public string LastForegroundTitle { get; private set; } = "";
    public bool LastIsFullscreen { get; private set; }
    public string ActiveRuleKey { get; private set; } = "";
    public string ActiveRuleLabel { get; private set; } = "未匹配（保持当前鼠标配置）";
    public ActiveSwitchReason ActiveReason { get; private set; } = ActiveSwitchReason.None;
    public IReadOnlyDictionary<string, string> LastOutcomeByDevice => _lastOutcomeByDevice;

    public event Action? StatusChanged;

    public void Start()
    {
        App.Devices.DevicesUpdated += OnDevicesUpdated;
        _timer ??= new Timer(_ => _ = PollAsync(force: false), null, 1200, 350);
    }

    public void ClearAppliedCache()
    {
        _lastAppliedByDevice.Clear();
    }

    public async Task ForceReevaluateAsync()
    {
        _lastAppliedByDevice.Clear();
        _pendingRuleKey = null;
        _latchedResolution = null;
        _latchedHwnd = IntPtr.Zero;
        _latchedPid = 0;
        await PollAsync(force: true);
    }

    public Task ApplyRuleManuallyAsync(GameAutoRule rule)
    {
        var res = new GameProfileResolution(
            ActiveSwitchReason.ProcessRule,
            $"rule:{rule.Id}",
            rule.Name,
            rule.PerMouseTargets);
        return ApplyResolutionAsync(res, forceAll: true, hwnd: IntPtr.Zero, pid: 0, procName: rule.ProcessNames.FirstOrDefault() ?? "", ruleProcs: rule.ProcessNames);
    }

    private void OnDevicesUpdated(IReadOnlyList<PeripheralDevice> devices)
    {
        if (_disposed) return;
        var currentAwake = devices
            .Where(d => d.Kind is DeviceKind.Mouse or DeviceKind.Controller && d.Connected && !d.IsSleeping)
            .Select(d => d.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newlyWoken = currentAwake.Where(id => !_lastAwakeMouseIds.Contains(id)).ToList();
        _lastAwakeMouseIds.Clear();
        foreach (string id in currentAwake)
            _lastAwakeMouseIds.Add(id);

        if (newlyWoken.Count > 0)
        {
            foreach (string id in newlyWoken)
                _lastAppliedByDevice.Remove(id);

            if (App.Settings.Current.AutoSwitchEnabled &&
                _lastResolution is { Reason: not ActiveSwitchReason.None } res)
            {
                _ = Task.Run(() => ApplyResolutionAsync(res, forceAll: false, IntPtr.Zero, 0, "", []));
            }
        }
    }

    private async Task PollAsync(bool force)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var settings = App.Settings.Current;
            var (hwnd, pid, procName, title, isFs, isSelf) = InspectForeground();
            if (!isSelf)
            {
                LastForegroundProcess = procName;
                LastForegroundTitle = title;
                LastIsFullscreen = isFs;
            }

            if (!settings.AutoSwitchEnabled)
            {
                ActiveRuleKey = "";
                ActiveRuleLabel = "自动切换已暂停";
                ActiveReason = ActiveSwitchReason.None;
                _latchedResolution = null;
                StatusChanged?.Invoke();
                return;
            }

            if (isSelf && !force)
                return;

            var candidate = GameProfileRuleMatcher.Resolve(
                new GameProfileMatchContext(LastForegroundProcess, LastForegroundTitle, LastIsFullscreen),
                settings.GameRules,
                settings.FullscreenFallbackTargets,
                settings.EnableExclusiveFullscreenFallback,
                settings.DesktopDefaultTargets,
                settings.RestoreDesktopTargetsOnExit);

            bool latchedAlive = IsLatchedSessionStillAlive();
            var resolution = GameProfileRuleMatcher.ApplyExitRetention(
                candidate,
                _latchedResolution,
                settings.KeepProfileUntilWindowExits,
                latchedAlive);

            bool isHoldingWhileUnfocused = ReferenceEquals(resolution, _latchedResolution)
                && candidate.Reason != resolution.Reason
                && _latchedResolution != null;

            string displayLabel = isHoldingWhileUnfocused && !string.IsNullOrEmpty(_latchedProcName)
                ? $"{resolution.RuleLabel}（{_latchedProcName} 窗口未退出 · 保持中）"
                : resolution.RuleLabel;

            if (resolution.RuleKey != candidate.RuleKey && resolution.RuleKey == _latchedResolution?.RuleKey)
            {
                resolution = resolution with { RuleLabel = displayLabel };
            }

            if (force)
            {
                List<string> rProcs = FindMatchingRuleProcesses(settings.GameRules, LastForegroundProcess);
                await ApplyResolutionAsync(resolution, forceAll: true, hwnd, pid, LastForegroundProcess, rProcs);
                return;
            }

            if (resolution.RuleKey == ActiveRuleKey)
            {
                // 如果刚刚仍在全屏窗口内，持续刷新锁存句柄（防止游戏重启子窗口接管）
                if (resolution.Reason is ActiveSwitchReason.ProcessRule or ActiveSwitchReason.ExclusiveFullscreen
                    && !isHoldingWhileUnfocused && hwnd != IntPtr.Zero && pid != 0)
                {
                    _latchedHwnd = hwnd;
                    _latchedPid = pid;
                    if (!string.IsNullOrWhiteSpace(LastForegroundProcess))
                        _latchedProcName = LastForegroundProcess;
                }

                if (!string.Equals(ActiveRuleLabel, displayLabel, StringComparison.Ordinal))
                    ActiveRuleLabel = displayLabel;

                _pendingRuleKey = null;
                StatusChanged?.Invoke();
                return;
            }

            long now = Environment.TickCount64;
            if (!string.Equals(_pendingRuleKey, resolution.RuleKey, StringComparison.Ordinal))
            {
                _pendingRuleKey = resolution.RuleKey;
                _pendingSinceMs = now;
                _pendingResolution = resolution;
                return;
            }

            int requiredDebounceMs = resolution.Reason is ActiveSwitchReason.ProcessRule or ActiveSwitchReason.ExclusiveFullscreen
                ? 250
                : 1500;
            if (now - _pendingSinceMs >= requiredDebounceMs && _pendingResolution != null)
            {
                var toApply = _pendingResolution;
                _pendingRuleKey = null;
                List<string> rProcs = toApply.Reason == ActiveSwitchReason.ProcessRule
                    ? FindMatchingRuleProcesses(settings.GameRules, LastForegroundProcess)
                    : [];
                await ApplyResolutionAsync(toApply, forceAll: false, hwnd, pid, LastForegroundProcess, rProcs);
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"GameProfileAutoService 异常: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private async Task ApplyResolutionAsync(
        GameProfileResolution resolution,
        bool forceAll,
        IntPtr hwnd,
        uint pid,
        string procName,
        List<string> ruleProcs)
    {
        await _applyLock.WaitAsync();
        try
        {
            _lastResolution = resolution;
            ActiveRuleKey = resolution.RuleKey;
            ActiveReason = resolution.Reason;
            ActiveRuleLabel = string.IsNullOrWhiteSpace(resolution.RuleLabel)
                ? "未匹配（保持当前鼠标配置）"
                : resolution.RuleLabel;

            if (resolution.Reason is ActiveSwitchReason.ProcessRule or ActiveSwitchReason.ExclusiveFullscreen)
            {
                _latchedResolution = resolution;
                if (hwnd != IntPtr.Zero) _latchedHwnd = hwnd;
                if (pid != 0) _latchedPid = pid;
                if (!string.IsNullOrWhiteSpace(procName)) _latchedProcName = procName;
                if (ruleProcs.Count > 0) _latchedRuleProcs = [.. ruleProcs];
            }
            else
            {
                _latchedResolution = null;
                _latchedHwnd = IntPtr.Zero;
                _latchedPid = 0;
                _latchedProcName = "";
                _latchedRuleProcs = [];
            }

            if (resolution.Reason == ActiveSwitchReason.None || resolution.PerMouseTargets.Count == 0)
            {
                StatusChanged?.Invoke();
                return;
            }

            var mouseMap = App.LastDevices
                .Where(d => d.Kind is DeviceKind.Mouse or DeviceKind.Controller && d.Connected)
                .ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

            var presetMap = App.Settings.Current.Presets
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var switchedNotifications = new List<(string DeviceName, string TargetLabel)>();

            foreach (var (deviceId, rawTarget) in resolution.PerMouseTargets)
            {
                if (!mouseMap.TryGetValue(deviceId, out var dev)) continue;
                var parsed = GameProfileRuleMatcher.ParseTarget(rawTarget);
                if (parsed.IsEmpty) continue;

                if (dev.IsSleeping)
                {
                    _lastOutcomeByDevice[deviceId] = $"{DateTime.Now:HH:mm:ss} 休眠中已跳过（唤醒后自动切换）";
                    continue;
                }

                if (!forceAll &&
                    _lastAppliedByDevice.TryGetValue(deviceId, out var prevTarget) &&
                    string.Equals(prevTarget, rawTarget, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DeviceTuningUpdate? update = null;
                string desc = rawTarget;
                if (parsed.OnboardSlot is { } slot)
                {
                    update = new DeviceTuningUpdate { ActiveOnboardProfile = slot };
                    desc = $"板载配置 {slot + 1}";
                }
                else if (parsed.PresetId is { } pidKey && presetMap.TryGetValue(pidKey, out var preset))
                {
                    update = preset.ToUpdate();
                    desc = $"预设「{preset.Name}」";
                }

                if (update == null) continue;

                var result = await App.Devices.ApplySettingsAsync(deviceId, update);
                if (result.Success && (result.Capabilities == null || result.Capabilities.IsOnline))
                {
                    _lastAppliedByDevice[deviceId] = rawTarget;
                    _lastOutcomeByDevice[deviceId] = $"{DateTime.Now:HH:mm:ss} 已切换 → {desc}";
                    switchedNotifications.Add((dev.Name, desc));
                }
                else
                {
                    _lastOutcomeByDevice[deviceId] = $"{DateTime.Now:HH:mm:ss} 未响应/休眠，已跳过 ({result.Message})";
                }

                Diag.Log($"[GameSwitch] rule={resolution.RuleLabel} dev={deviceId} target={rawTarget} ok={result.Success} msg={result.Message}");
            }

            if (switchedNotifications.Count > 0)
                NativeNotificationService.NotifyProfileSwitch(resolution.RuleLabel, switchedNotifications);

            StatusChanged?.Invoke();
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private bool IsLatchedSessionStillAlive()
    {
        if (_latchedResolution == null)
            return false;

        if (_latchedResolution.Reason == ActiveSwitchReason.ExclusiveFullscreen)
        {
            if (_latchedHwnd != IntPtr.Zero && IsWindow(_latchedHwnd) && IsProcessAlive(_latchedPid))
                return true;
            return false;
        }

        if (_latchedResolution.Reason == ActiveSwitchReason.ProcessRule)
        {
            if (_latchedPid != 0 && IsProcessAlive(_latchedPid))
                return true;
            foreach (string candidate in _latchedRuleProcs)
            {
                string stem = Path.GetFileNameWithoutExtension(candidate.Trim());
                if (string.IsNullOrEmpty(stem)) continue;
                try
                {
                    if (Process.GetProcessesByName(stem).Length > 0)
                        return true;
                }
                catch { }
            }
        }

        return false;
    }

    private static List<string> FindMatchingRuleProcesses(IReadOnlyList<GameAutoRule> rules, string fgProc)
    {
        foreach (var r in rules)
        {
            if (r.Enabled && GameProfileRuleMatcher.ProcessMatches(r, fgProc))
                return r.ProcessNames;
        }
        return [];
    }

    private static bool IsProcessAlive(uint pid)
    {
        if (pid == 0) return false;
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            return GetExitCodeProcess(h, out uint exitCode) && exitCode == StillActive;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static List<RunningWindowProcess> GetRunningForegroundCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<RunningWindowProcess>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return true;

            string proc = ResolveProcessName(pid);
            if (string.IsNullOrEmpty(proc) || IgnoredPickerProcesses.Contains(proc)) return true;

            string title = ReadWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            if (seen.Add(proc))
                list.Add(new RunningWindowProcess(proc, title));
            return true;
        }, IntPtr.Zero);

        return list.OrderBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (IntPtr Hwnd, uint Pid, string ProcessName, string Title, bool IsFullscreen, bool IsSelf) InspectForeground()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return (IntPtr.Zero, 0, "", "", false, false);

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return (IntPtr.Zero, 0, "", "", false, false);

        bool isSelf = pid == (uint)Environment.ProcessId;
        string procName = ResolveProcessName(pid);
        if (!isSelf && string.Equals(procName, "GearDeck.Widget.exe", StringComparison.OrdinalIgnoreCase))
            isSelf = true;

        string title = ReadWindowTitle(hwnd);
        if (isSelf)
            return (hwnd, pid, procName, title, false, true);

        bool exclusiveQuns = SHQueryUserNotificationState(out int qns) == 0 && qns == 3;
        bool onlyNative = App.Settings?.Current.OnlyTrueExclusiveFullscreen == true;
        bool borderlessFs = !onlyNative && IsFullscreenWindow(hwnd);
        return (hwnd, pid, procName, title, exclusiveQuns || borderlessFs, false);
    }

    private static bool IsFullscreenWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;

        int cloaked = 0;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        var clsSb = new StringBuilder(128);
        GetClassNameW(hwnd, clsSb, clsSb.Capacity);
        string cls = clsSb.ToString();
        if (!string.IsNullOrEmpty(cls) && ExcludedFullscreenClasses.Contains(cls))
            return false;

        int exStyle = GetWindowLongW(hwnd, GwlExStyle);
        if ((exStyle & (WsExToolWindow | WsExTransparent | WsExNoActivate)) != 0)
            return false;

        int style = GetWindowLongW(hwnd, GwlStyle);
        if ((style & WsCaption) == WsCaption)
            return false;

        if (!GetWindowRect(hwnd, out RECT rc)) return false;
        IntPtr mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (mon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(mon, ref mi)) return false;

        return rc.Left <= mi.rcMonitor.Left && rc.Top <= mi.rcMonitor.Top &&
               rc.Right >= mi.rcMonitor.Right && rc.Bottom >= mi.rcMonitor.Bottom;
    }

    private static string ResolveProcessName(uint pid)
    {
        IntPtr hProc = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (hProc != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(512);
                int len = sb.Capacity;
                if (QueryFullProcessImageNameW(hProc, 0, sb, ref len) && len > 0)
                    return Path.GetFileName(sb.ToString(0, len));
            }
            finally
            {
                CloseHandle(hProc);
            }
        }
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch
        {
            return "";
        }
    }

    private static string ReadWindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        int len = GetWindowTextW(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len).Trim() : "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        App.Devices.DevicesUpdated -= OnDevicesUpdated;
        _timer?.Dispose();
        _applyLock.Dispose();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
