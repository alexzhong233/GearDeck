using GearDeck.Core.Models;
using GearDeck.Core.Providers;

namespace GearDeck.Core;

/// <summary>聚合所有品牌提供器并行轮询电量与参数调节；通过互斥锁序列化底层 HID 读写，避免 UI 卡顿或通道冲突。</summary>
public sealed class DeviceManager : IDisposable
{
    private readonly List<IPeripheralProvider> _providers;
    private readonly Dictionary<string, PeripheralDevice> _lastKnown = new();
    private readonly SemaphoreSlim _hidLock = new(1, 1);
    private Timer? _timer;
    private readonly object _gate = new();
    private int _running;

    public event Action<IReadOnlyList<PeripheralDevice>>? DevicesUpdated;
    public event Action? ScanCompleted;

    public DeviceManager(IEnumerable<IPeripheralProvider> providers)
    {
        _providers = providers.ToList();
    }

    public void Start(int intervalSeconds = 30)
    {
        Stop();
        _timer = new Timer(_ => RefreshAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(Math.Max(5, intervalSeconds)));
    }

    public void Stop() => _timer?.Dispose();

    public Task RefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return Task.CompletedTask;
        return Task.Run(async () =>
        {
            bool acquired = false;
            try
            {
                acquired = await _hidLock.WaitAsync(TimeSpan.FromSeconds(8));
                if (!acquired) return;

                var snapshots = new List<PeripheralDevice>();
                foreach (var line in Diag.DumpHidInventory())
                    Diag.Log(line);

                // 每个品牌并行扫（品牌内部自行顺序探测接口），总超时 6 秒
                var tasks = _providers.Select(async p =>
                {
                    var items = await Task.Run(() =>
                    {
                        try { return p.Enumerate(); }
                        catch (Exception ex)
                        {
                            Diag.Log($"{p.Name} 扫描异常: {ex.Message}");
                            return (IReadOnlyList<PeripheralDevice>)Array.Empty<PeripheralDevice>();
                        }
                    });
                    Diag.Log($"{p.Name}: {items.Count} 台");
                    return items;
                }).ToArray();
                Task.WaitAll(tasks, TimeSpan.FromSeconds(6));
                foreach (var t in tasks)
                    if (t.IsCompletedSuccessfully)
                        snapshots.AddRange(t.Result);

                lock (_gate)
                {
                    var seen = new HashSet<string>();
                    foreach (var s in snapshots)
                    {
                        seen.Add(s.Id);
                        _lastKnown[s.Id] = s;
                    }
                    foreach (var kv in _lastKnown.ToList())
                    {
                        if (!seen.Contains(kv.Key))
                        {
                            kv.Value.Connected = false;
                            kv.Value.Note ??= "休眠或未连接";
                        }
                    }

                    var list = _lastKnown.Values.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList();
                    DevicesUpdated?.Invoke(list);
                    ScanCompleted?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Diag.Log($"扫描汇总异常: {ex.Message}");
            }
            finally
            {
                if (acquired) _hidLock.Release();
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    public async Task<DeviceTuningCapabilities> QueryCapabilitiesAsync(string deviceId)
    {
        var provider = _providers.OfType<ITunablePeripheralProvider>().FirstOrDefault(p => p.CanHandleDevice(deviceId));
        if (provider == null)
        {
            return new DeviceTuningCapabilities
            {
                DeviceId = deviceId,
                IsOnline = true,
                StatusMessage = "当前品牌/型号仅支持电量监控，硬件参数协议尚未开放。",
            };
        }

        await _hidLock.WaitAsync();
        try
        {
            return await Task.Run(() => provider.QueryCapabilities(deviceId));
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
            _hidLock.Release();
        }
    }

    public async Task<DeviceTuningResult> ApplySettingsAsync(string deviceId, DeviceTuningUpdate update)
    {
        var provider = _providers.OfType<ITunablePeripheralProvider>().FirstOrDefault(p => p.CanHandleDevice(deviceId));
        if (provider == null)
            return new DeviceTuningResult(false, "该设备暂不支持写入硬件参数。");

        await _hidLock.WaitAsync();
        try
        {
            return await Task.Run(() => provider.ApplySettings(deviceId, update));
        }
        catch (Exception ex)
        {
            Diag.Log($"ApplySettings 异常 {deviceId}: {ex.Message}");
            return new DeviceTuningResult(false, $"写入硬件失败: {ex.Message}");
        }
        finally
        {
            _hidLock.Release();
        }
    }

    public IReadOnlyList<PeripheralDevice> LastSnapshot()
    {
        lock (_gate)
            return _lastKnown.Values.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList();
    }

    public void Dispose()
    {
        Stop();
        _hidLock.Dispose();
    }
}
