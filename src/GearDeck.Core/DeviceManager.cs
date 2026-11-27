using HidSharp;
using GearDeck.Core.Models;
using GearDeck.Core.Providers;
using GearDeck.Core.Services;

namespace GearDeck.Core;

/// <summary>聚合所有品牌提供器并行轮询电量与参数调节，监听 USB/HID 热插拔实时刷新，并将双模有线充电与 2.4G 端自动归一。</summary>
public sealed class DeviceManager : IDisposable
{
    private readonly List<IPeripheralProvider> _providers;
    private readonly Dictionary<string, PeripheralDevice> _lastKnown = new();
    private readonly Dictionary<IPeripheralProvider, List<PeripheralDevice>> _lastRawByProvider = new();
    private readonly Dictionary<string, string> _activeTransportRouting = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _hidLock = new(1, 1);
    private Timer? _timer;
    private Timer? _hotplugDebounceTimer;
    private bool _hotplugSubscribed;
    private int _pendingHotplugRefresh;
    private bool _disposed;
    private readonly object _gate = new();
    private int _running;

    public event Action<IReadOnlyList<PeripheralDevice>>? DevicesUpdated;
    public event Action? ScanCompleted;

    /// <summary>最近一次扫描完成（ScanCompleted 触发）的 UTC 时间，用于按需防抖刷新。</summary>
    public DateTimeOffset LastScanCompletedUtc { get; private set; } = DateTimeOffset.MinValue;

    public DeviceManager(IEnumerable<IPeripheralProvider> providers)
    {
        _providers = providers.ToList();
    }

    public void Start(int intervalSeconds = 30)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer?.Dispose();
            _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(Math.Max(5, intervalSeconds)));
            if (!_hotplugSubscribed)
            {
                try
                {
                    DeviceList.Local.Changed += OnHidDeviceListChanged;
                    _hotplugSubscribed = true;
                }
                catch (Exception ex)
                {
                    Diag.Log($"HID 热插拔监听订阅失败: {ex.Message}");
                }
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _hotplugDebounceTimer?.Dispose();
            _hotplugDebounceTimer = null;
            if (_hotplugSubscribed)
            {
                try { DeviceList.Local.Changed -= OnHidDeviceListChanged; } catch { }
                _hotplugSubscribed = false;
            }
        }
    }

    private void OnHidDeviceListChanged(object? sender, DeviceListChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _hotplugDebounceTimer ??= new Timer(_ => _ = RefreshAsync(isHotplug: true), null, Timeout.Infinite, Timeout.Infinite);
            _hotplugDebounceTimer.Change(750, Timeout.Infinite);
        }
    }

    public string ResolveActiveDeviceId(string deviceId)
    {
        lock (_gate)
        {
            return _activeTransportRouting.TryGetValue(deviceId, out string? activeId) && !string.IsNullOrWhiteSpace(activeId)
                ? activeId
                : deviceId;
        }
    }

    /// <summary>距上次扫描完成超过 minAge 时才触发一次后台刷新，供任务栏分组点击等场景按需调用。</summary>
    public void RequestRefreshIfStale(TimeSpan minAge)
    {
        if (DateTimeOffset.UtcNow - LastScanCompletedUtc >= minAge)
            _ = RefreshAsync();
    }

    public Task RefreshAsync(bool bypassCoexistenceGuard = false, bool isHotplug = false)
    {
        if (!bypassCoexistenceGuard && ExternalConfiguratorGuard.IsBackgroundYieldActive(out string? yieldReason))
        {
            bool hasCached = false;
            lock (_gate)
            {
                if (_lastKnown.Count > 0)
                {
                    Diag.Log($"[Coexistence] 跳过本轮后台 HID 轮询: {yieldReason}");
                    LastScanCompletedUtc = DateTimeOffset.UtcNow;
                    hasCached = true;
                }
            }
            if (hasCached)
            {
                ScanCompleted?.Invoke();
                return Task.CompletedTask;
            }
        }

        if (_disposed || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            if (isHotplug && !_disposed)
                Interlocked.Exchange(ref _pendingHotplugRefresh, 1);
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            bool acquired = false;
            try
            {
                try
                {
                    acquired = await _hidLock.WaitAsync(TimeSpan.FromSeconds(8));
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                if (!acquired || _disposed) return;

                foreach (var line in Diag.DumpHidInventory())
                    Diag.Log(line);

                // 每个品牌并行扫（品牌内部自行顺序探测接口），总超时 6 秒
                var tasks = _providers.Select(async p =>
                {
                    try
                    {
                        var items = await Task.Run(() => p.Enumerate());
                        Diag.Log($"{p.Name}: {items.Count} 台");
                        return (Provider: p, Succeeded: true, Items: (IReadOnlyList<PeripheralDevice>)items);
                    }
                    catch (Exception ex)
                    {
                        Diag.Log($"{p.Name} 扫描异常: {ex.Message}");
                        return (Provider: p, Succeeded: false, Items: (IReadOnlyList<PeripheralDevice>)Array.Empty<PeripheralDevice>());
                    }
                }).ToArray();
                var allTasks = Task.WhenAll(tasks);
                bool allDone = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(6))) == allTasks;
                if (!allDone)
                    Diag.Log("部分设备提供器扫描超过 6s 超时，已先汇总已完成项");

                List<PeripheralDevice> list;
                lock (_gate)
                {
                    if (_disposed) return;
                    foreach (var t in tasks)
                    {
                        if (t.IsCompletedSuccessfully && t.Result.Succeeded)
                            _lastRawByProvider[t.Result.Provider] = t.Result.Items.ToList();
                    }

                    var allRaw = _lastRawByProvider.Values.SelectMany(x => x).ToList();
                    var unified = DeviceUnifier.Unify(allRaw);

                    _lastKnown.Clear();
                    foreach (var s in unified.Devices)
                        _lastKnown[s.Id] = s;

                    _activeTransportRouting.Clear();
                    foreach (var kv in unified.ActiveTransportRouting)
                        _activeTransportRouting[kv.Key] = kv.Value;

                    list = _lastKnown.Values.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList();
                    LastScanCompletedUtc = DateTimeOffset.UtcNow;
                }

                if (!_disposed)
                {
                    DevicesUpdated?.Invoke(list);
                    ScanCompleted?.Invoke();
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Diag.Log($"扫描汇总异常: {ex.Message}");
            }
            finally
            {
                if (acquired)
                {
                    try { _hidLock.Release(); } catch (ObjectDisposedException) { }
                }
                Interlocked.Exchange(ref _running, 0);
                if (!_disposed && Interlocked.Exchange(ref _pendingHotplugRefresh, 0) == 1)
                    _ = RefreshAsync(bypassCoexistenceGuard: false, isHotplug: false);
            }
        });
    }

    public async Task<DeviceTuningCapabilities> QueryCapabilitiesAsync(string deviceId)
    {
        if (_disposed)
        {
            return new DeviceTuningCapabilities
            {
                DeviceId = deviceId,
                IsOnline = false,
                StatusMessage = "设备管理器已关闭。",
            };
        }

        string effectiveId = ResolveActiveDeviceId(deviceId);
        var provider = _providers.OfType<ITunablePeripheralProvider>().FirstOrDefault(p => p.CanHandleDevice(effectiveId));
        if (provider == null && !string.Equals(effectiveId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            effectiveId = deviceId;
            provider = _providers.OfType<ITunablePeripheralProvider>().FirstOrDefault(p => p.CanHandleDevice(deviceId));
        }

        if (provider == null)
        {
            return new DeviceTuningCapabilities
            {
                DeviceId = deviceId,
                IsOnline = true,
                StatusMessage = "当前品牌/型号仅支持电量监控，硬件参数协议尚未开放。",
            };
        }

        bool acquired = false;
        try
        {
            await _hidLock.WaitAsync();
            acquired = true;
            if (_disposed)
                return new DeviceTuningCapabilities { DeviceId = deviceId, IsOnline = false, StatusMessage = "设备管理器已关闭。" };
            var caps = await Task.Run(() => provider.QueryCapabilities(effectiveId));
            caps.DeviceId = deviceId;
            return caps;
        }
        catch (ObjectDisposedException)
        {
            return new DeviceTuningCapabilities { DeviceId = deviceId, IsOnline = false, StatusMessage = "设备管理器已关闭。" };
        }
        catch (Exception ex)
        {
            Diag.Log($"QueryCapabilities 异常 {deviceId}: {ex.Message}");
            return new DeviceTuningCapabilities
            {
                DeviceId = deviceId,
                IsOnline = false,
                StatusMessage = $"读取参数异常: {ex.Message}",
            };
        }
        finally
        {
            if (acquired)
            {
                try { _hidLock.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    public async Task<DeviceTuningResult> ApplySettingsAsync(string deviceId, DeviceTuningUpdate update)
    {
        if (_disposed)
            return new DeviceTuningResult(false, "设备管理器已关闭。");

        string effectiveId = ResolveActiveDeviceId(deviceId);
        var provider = _providers.OfType<ITunablePeripheralProvider>().FirstOrDefault(p => p.CanHandleDevice(effectiveId));
        if (provider == null && !string.Equals(effectiveId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            effectiveId = deviceId;
            provider = _providers.OfType<ITunablePeripheralProvider>().FirstOrDefault(p => p.CanHandleDevice(deviceId));
        }

        if (provider == null)
            return new DeviceTuningResult(false, "该设备暂不支持写入硬件参数。");

        bool acquired = false;
        try
        {
            await _hidLock.WaitAsync();
            acquired = true;
            if (_disposed)
                return new DeviceTuningResult(false, "设备管理器已关闭。");
            var res = await Task.Run(() => provider.ApplySettings(effectiveId, update));
            if (res.Capabilities != null)
                res.Capabilities.DeviceId = deviceId;
            return res;
        }
        catch (ObjectDisposedException)
        {
            return new DeviceTuningResult(false, "设备管理器已关闭。");
        }
        catch (Exception ex)
        {
            Diag.Log($"ApplySettings 异常 {deviceId}: {ex.Message}");
            return new DeviceTuningResult(false, $"写入硬件失败: {ex.Message}");
        }
        finally
        {
            if (acquired)
            {
                try { _hidLock.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    public IReadOnlyList<PeripheralDevice> LastSnapshot()
    {
        lock (_gate)
            return _lastKnown.Values.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _hidLock.Dispose();
    }
}
