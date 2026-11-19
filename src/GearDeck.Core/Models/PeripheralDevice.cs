namespace GearDeck.Core.Models;

public enum DeviceKind
{
    Mouse,
    Keyboard,
    Headset,
    Controller,
}

public enum BatteryState
{
    Unknown,
    Normal,
    Charging,
    FullyCharged,
}

/// <summary>一台外设的快照（电量轮询结果）。</summary>
public sealed class PeripheralDevice
{
    /// <summary>稳定 id：provider 前缀 + VID/PID（+ 序列号）。</summary>
    public required string Id { get; init; }

    public required string Name { get; set; }

    public required DeviceKind Kind { get; set; }

    public required string Provider { get; init; }

    /// <summary>0..100，未知为 null（不虚构 0%）。</summary>
    public int? BatteryPercent { get; set; }

    public BatteryState State { get; set; } = BatteryState.Unknown;

    public bool Connected { get; set; }

    public bool IsSleeping { get; set; }

    /// <summary>usb / 2.4g / bluetooth / wired 等。</summary>
    public string Transport { get; set; } = "unknown";

    public string? Note { get; set; }

    public DateTimeOffset LastUpdated { get; set; }
}
