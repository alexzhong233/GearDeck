using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GearDeck.Core;
using GearDeck.Core.Models;
using GearDeck.Core.Services;

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
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    private static readonly HashSet<string> IgnoredPickerProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "GearDeck.App.exe", "GearDeck.Widget.exe", "TextInputHost.exe",
        "SearchHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "explorer.exe", "ApplicationFrameHost.exe",
    };

    private static readonly IReadOnlyDictionary<string, string> EmptyTargets = new Dictionary<string, string>();
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly Dictionary<string, string> _lastAppliedByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastOutcomeByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int? OnboardSlot, bool? WasHostMode, MouseHardwarePreset? Preset, bool SwitchedViaOnboardSlot)> _preEntrySnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lastAwakeMouseIds = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private int _ticking;
    private volatile bool _disposed;

    private string? _pendingRuleKey;
    private long _pendingSinceMs;
    private GameProfileResolution? _pendingResolution;
    private GameProfileResolution? _lastResolution;
    private long _lastRollbackRetryMs;
    private int _rollbackRetryCount;

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
        _rollbackRetryCount = 0;
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

            if (!settings.EnableExclusiveFullscreenFallback &&
                _latchedResolution?.Reason == ActiveSwitchReason.ExclusiveFullscreen)
            {
                _latchedResolution = null;
                _latchedHwnd = IntPtr.Zero;
                _latchedPid = 0;
            }

            bool latchedDiedWhileSelf = isSelf && _latchedResolution != null && !IsLatchedSessionStillAlive();
            bool pendingRollbackRetryWhileSelf = isSelf && _preEntrySnapshots.Count > 0 && ActiveReason == ActiveSwitchReason.DesktopDefault;
            if (latchedDiedWhileSelf)
            {
                LastIsFullscreen = false;
                LastForegroundProcess = "";
                LastForegroundTitle = "";
            }
            if (isSelf && !force && !latchedDiedWhileSelf && !pendingRollbackRetryWhileSelf)
                return;

            if (!force && ExternalConfiguratorGuard.IsBackgroundYieldActive(out var yieldReason))
            {
                ActiveRuleLabel = yieldReason ?? "已自动让位给官方驱动";
                StatusChanged?.Invoke();
                return;
            }

            var candidate = GameProfileRuleMatcher.Resolve(
                new GameProfileMatchContext(LastForegroundProcess, LastForegroundTitle, LastIsFullscreen),
                settings.GameRules,
                settings.FullscreenFallbackTargets,
                settings.EnableExclusiveFullscreenFallback,
                settings.EnableExclusiveFullscreenFallback ? settings.DesktopDefaultTargets : EmptyTargets,
                settings.EnableExclusiveFullscreenFallback ? settings.RestoreDesktopTargetsOnExit : true);

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

                if (resolution.Reason == ActiveSwitchReason.DesktopDefault
                    && _preEntrySnapshots.Count > 0
                    && _rollbackRetryCount < 6
                    && Environment.TickCount64 - _lastRollbackRetryMs >= 2500)
                {
                    _rollbackRetryCount++;
                    _lastRollbackRetryMs = Environment.TickCount64;
                    await ApplyResolutionAsync(resolution, forceAll: false, hwnd, pid, LastForegroundProcess, []);
                    if (_preEntrySnapshots.Count == 0)
                        _rollbackRetryCount = 0;
                    return;
                }

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

            int requiredDebounceMs = resolution.Reason switch
            {
                ActiveSwitchReason.ProcessRule => 300,
                ActiveSwitchReason.ExclusiveFullscreen => 600,
                ActiveSwitchReason.DesktopDefault when settings.KeepProfileUntilWindowExits => 500,
                _ => 1500,
            };
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
                _rollbackRetryCount = 0;
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

            bool hasDesktopRollbackWork = resolution.Reason == ActiveSwitchReason.DesktopDefault && _preEntrySnapshots.Count > 0;
            if (resolution.Reason == ActiveSwitchReason.None || (resolution.PerMouseTargets.Count == 0 && !hasDesktopRollbackWork))
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

            if (resolution.Reason == ActiveSwitchReason.DesktopDefault)
            {
                if (!App.Settings.Current.EnableExclusiveFullscreenFallback && _preEntrySnapshots.Count == 0)
                {
                    StatusChanged?.Invoke();
                    return;
                }

                var targetDeviceIds = resolution.PerMouseTargets.Keys
                    .Union(_preEntrySnapshots.Keys, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (string deviceId in targetDeviceIds)
                {
                    if (!mouseMap.TryGetValue(deviceId, out var dev)) continue;
                    resolution.PerMouseTargets.TryGetValue(deviceId, out var rawTarget);
                    var parsed = GameProfileRuleMatcher.ParseTarget(rawTarget);

                    if (parsed.KeepCurrent)
                    {
                        _preEntrySnapshots.Remove(deviceId);
                        continue;
                    }

                    if (dev.IsSleeping)
                    {
                        _lastOutcomeByDevice[deviceId] = $"{DateTime.Now:HH:mm:ss} 休眠中已跳过（唤醒后恢复）";
                        continue;
                    }

                    DeviceTuningUpdate? update = null;
                    string desc = "";

                    if (string.IsNullOrWhiteSpace(rawTarget) || parsed.RestorePrevious)
                    {
                        if (!_preEntrySnapshots.TryGetValue(deviceId, out var snap))
                            continue;

                        if (snap.SwitchedViaOnboardSlot && snap.OnboardSlot is { } prevSlot)
                        {
                            update = new DeviceTuningUpdate { ActiveOnboardProfile = prevSlot };
                            desc = $"开启前板载槽位 {prevSlot + 1}";
                        }
                        else if (snap.Preset != null)
                        {
                            update = snap.Preset.ToUpdate();
                            if (snap.WasHostMode == true)
                                update.SetHostMode = true;
                            desc = "开启前配置";
                        }
                        else if (snap.OnboardSlot is { } fallbackSlot)
                        {
                            update = new DeviceTuningUpdate { ActiveOnboardProfile = fallbackSlot };
                            desc = $"开启前板载槽位 {fallbackSlot + 1}";
                        }
                    }
                    else if (parsed.OnboardSlot is { } slot)
                    {
                        update = new DeviceTuningUpdate { ActiveOnboardProfile = slot };
                        desc = $"板载配置 {slot + 1}";
                    }
                    else if (parsed.PresetId is { } pidKey && presetMap.TryGetValue(pidKey, out var preset))
                    {
                        update = preset.ToUpdate();
                        desc = $"预设「{preset.Name}」";
                    }

                    if (update == null)
                    {
                        _preEntrySnapshots.Remove(deviceId);
                        continue;
                    }

                    var result = await App.Devices.ApplySettingsAsync(deviceId, update);
                    if (result.Success && (result.Capabilities == null || result.Capabilities.IsOnline))
                    {
                        _preEntrySnapshots.Remove(deviceId);
                        _lastAppliedByDevice.Remove(deviceId);
                        _lastOutcomeByDevice[deviceId] = $"{DateTime.Now:HH:mm:ss} 已恢复 → {desc}";
                        switchedNotifications.Add((dev.Name, desc));
                    }
                    else
                    {
                        _lastOutcomeByDevice[deviceId] = $"{DateTime.Now:HH:mm:ss} 未响应/休眠，已跳过 ({result.Message})";
                    }

                    Diag.Log($"[GameSwitchRollback] rule={resolution.RuleLabel} dev={deviceId} ok={result.Success} msg={result.Message}");
                }
            }
            else
            {
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

                    if (!_preEntrySnapshots.ContainsKey(deviceId))
                    {
                        try
                        {
                            var curCaps = await App.Devices.QueryCapabilitiesAsync(deviceId);
                            if (!curCaps.IsOnline)
                            {
                                await Task.Delay(150);
                                curCaps = await App.Devices.QueryCapabilitiesAsync(deviceId);
                            }

                            if (curCaps.IsOnline)
                            {
                                int? onboardSlot = curCaps.OnboardProfileCount > 0 ? curCaps.ActiveOnboardProfile : null;
                                bool? wasHostMode = curCaps.SupportsHostModeSwitch ? curCaps.IsHostMode : null;
                                bool inOnboardMode = !curCaps.SupportsHostModeSwitch || !curCaps.IsHostMode;
                                bool switchedViaSlot = parsed.OnboardSlot.HasValue && curCaps.OnboardProfileCount > 0 && inOnboardMode;
                                var snapPreset = MouseHardwarePreset.CaptureFrom("开启前快照", deviceId, dev.Name, curCaps);
                                _preEntrySnapshots[deviceId] = (onboardSlot, wasHostMode, snapPreset, switchedViaSlot);
                            }
                            else if (parsed.OnboardSlot.HasValue &&
                                     App.Settings.Current.DeviceOnboardManifests.TryGetValue(deviceId, out var manifest) &&
                                     manifest.SlotCount > 0)
                            {
                                int fallbackSlot = Math.Clamp(manifest.ActiveSlot, 0, manifest.SlotCount - 1);
                                _preEntrySnapshots[deviceId] = (fallbackSlot, null, null, true);
                            }
                        }
                        catch { }
                    }

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
            }

            if (resolution.Reason == ActiveSwitchReason.DesktopDefault && _preEntrySnapshots.Count == 0)
                _rollbackRetryCount = 0;

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
            bool winAlive = _latchedHwnd != IntPtr.Zero && IsWindow(_latchedHwnd);
            bool procAlive = _latchedPid != 0 && IsProcessAlive(_latchedPid);
            if (!winAlive || !procAlive)
                return false;

            bool isQunsForLatched = GetForegroundWindow() == _latchedHwnd
                && SHQueryUserNotificationState(out int qns) == 0 && qns == 3;
            var probe = ProbeWindow(_latchedHwnd, _latchedPid, _latchedProcName, isQunsForLatched);
            bool onlyNative = App.Settings?.Current.OnlyTrueExclusiveFullscreen == true;
            return FullscreenWindowClassifier.IsExclusiveFullscreenLatchAlive(winAlive, procAlive, probe, onlyNative);
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
        var probe = ProbeWindow(hwnd, pid, procName, exclusiveQuns);
        bool isFullscreen = probe != null && FullscreenWindowClassifier.IsFullscreen(probe, onlyNative);
        return (hwnd, pid, procName, title, isFullscreen, false);
    }

    private static WindowFullscreenProbe? ProbeWindow(IntPtr hwnd, uint pid, string procName, bool isQunsD3d)
    {
        if (hwnd == IntPtr.Zero) return null;
        bool visible = IsWindowVisible(hwnd);

        int cloaked = 0;
        bool isCloaked = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0;

        var clsSb = new StringBuilder(128);
        GetClassNameW(hwnd, clsSb, clsSb.Capacity);
        string cls = clsSb.ToString();

        bool minimized = IsIconic(hwnd);
        bool maximized = IsZoomed(hwnd);
        int style = GetWindowLongW(hwnd, GwlStyle);
        int exStyle = GetWindowLongW(hwnd, GwlExStyle);

        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out RECT rc, Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect(hwnd, out rc))
        {
            return null;
        }

        IntPtr mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (mon == IntPtr.Zero) return null;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(mon, ref mi)) return null;

        if (string.IsNullOrWhiteSpace(procName) && pid != 0)
            procName = ResolveProcessName(pid);

        return new WindowFullscreenProbe(
            ProcessName: procName,
            ClassName: cls,
            IsVisible: visible,
            IsCloaked: isCloaked,
            IsMinimized: minimized,
            IsMaximized: maximized,
            Style: style,
            ExStyle: exStyle,
            WindowLeft: rc.Left,
            WindowTop: rc.Top,
            WindowRight: rc.Right,
            WindowBottom: rc.Bottom,
            MonitorLeft: mi.rcMonitor.Left,
            MonitorTop: mi.rcMonitor.Top,
            MonitorRight: mi.rcMonitor.Right,
            MonitorBottom: mi.rcMonitor.Bottom,
            IsQunsD3dFullscreen: isQunsD3d);
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

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

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
