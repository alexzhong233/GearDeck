using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>品牌协议提供器：枚举设备并读取电量。</summary>
public interface IPeripheralProvider
{
    string Name { get; }

    /// <summary>扫描当前所有可读设备并返回快照。实现必须自行容错，不允许抛异常。</summary>
    IReadOnlyList<PeripheralDevice> Enumerate();
}

/// <summary>支持读取与写入 DPI、回报率、LOD、休眠时间等硬件参数的品牌提供器。</summary>
public interface ITunablePeripheralProvider : IPeripheralProvider
{
    bool CanHandleDevice(string deviceId);

    DeviceTuningCapabilities QueryCapabilities(string deviceId);

    DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update);
}
