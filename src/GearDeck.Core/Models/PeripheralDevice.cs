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

    public ushort? VendorId { get; set; }

    public ushort? ProductId { get; set; }

    // ── TWS 双耳 / 充电仓独立电量（如 Sony INZONE Buds） ───────────────────────
    public int? LeftEarPercent { get; set; }

    public bool LeftEarCharging { get; set; }

    public int? RightEarPercent { get; set; }

    public bool RightEarCharging { get; set; }

    public int? CaseBatteryPercent { get; set; }

    public DateTimeOffset LastUpdated { get; set; }

    /// <summary>生成左右耳合并电量文本（如 L 85% · R 82%），非 TWS 双耳返回 null。</summary>
    public string? FormatDualEarSummary(bool compact = false)
    {
        if (!LeftEarPercent.HasValue && !RightEarPercent.HasValue)
            return null;

        string l = LeftEarPercent.HasValue ? $"{LeftEarPercent.Value}%" : "--";
        string r = RightEarPercent.HasValue ? $"{RightEarPercent.Value}%" : "--";
        string lChg = LeftEarCharging ? "⚡" : "";
        string rChg = RightEarCharging ? "⚡" : "";
        if (compact)
            return $"L{l}{lChg} R{r}{rChg}";

        string caseText = CaseBatteryPercent.HasValue ? $" · 盒 {CaseBatteryPercent.Value}%" : "";
        return $"L {l}{lChg} · R {r}{rChg}{caseText}";
    }
}
