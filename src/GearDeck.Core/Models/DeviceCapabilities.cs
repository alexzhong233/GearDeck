namespace GearDeck.Core.Models;

public sealed record LodOption(int Code, string Label);

public sealed record PerformanceModeOption(int Code, string Label, string? Description = null);

public sealed record ButtonBindingSlot(
    int SlotIndex,
    string ButtonName,
    string CurrentAction,
    IReadOnlyList<string> AvailableActions,
    bool IsReadOnly = false);

public sealed class DeviceTuningCapabilities
{
    public required string DeviceId { get; init; }

    public bool IsOnline { get; set; }

    public string? StatusMessage { get; set; }

    // ── 主机 / 板载模式与多配置槽位 ───────────────────────────────────────────
    public bool SupportsHostModeSwitch { get; set; }

    public bool IsHostMode { get; set; } = true;

    /// <summary>板载配置槽位总数（>0 表示支持硬件板载游戏配置切换）。</summary>
    public int OnboardProfileCount { get; set; }

    /// <summary>当前激活板载配置槽位（0-based）。</summary>
    public int ActiveOnboardProfile { get; set; }

    public List<string> OnboardProfileNames { get; set; } = [];

    // ── DPI 与档位 ───────────────────────────────────────────────────────────
    public bool SupportsDpi { get; set; }

    public bool SupportsIndependentXY { get; set; }

    public int MinDpi { get; set; } = 100;

    public int MaxDpi { get; set; } = 32000;

    public int DpiStep { get; set; } = 50;

    public int CurrentDpiX { get; set; } = 800;

    public int CurrentDpiY { get; set; } = 800;

    public List<(int X, int Y)> DpiStages { get; set; } = [];

    /// <summary>0-based active DPI stage index.</summary>
    public int ActiveDpiStage { get; set; }

    public bool SupportsDpiStagesWrite { get; set; }

    // ── 回报率 ───────────────────────────────────────────────────────────────
    public List<int> SupportedPollingRates { get; set; } = [];

    public int CurrentPollingRate { get; set; }

    // ── 性能 / 传感器模式 ─────────────────────────────────────────────────────
    public List<PerformanceModeOption> SupportedPerformanceModes { get; set; } = [];

    public int CurrentPerformanceMode { get; set; }

    // ── 传感器微调与防抖 ──────────────────────────────────────────────────────
    public List<LodOption> SupportedLodOptions { get; set; } = [];

    public int CurrentLod { get; set; }

    public bool SupportsAsymmetricLod { get; set; }

    public bool AsymmetricLodEnabled { get; set; }

    public int AsymmetricLiftOff { get; set; } = 16;

    public int AsymmetricLanding { get; set; } = 11;

    public bool SupportsMotionSync { get; set; }
    public bool MotionSync { get; set; }

    public bool SupportsAngleSnapping { get; set; }
    public bool AngleSnapping { get; set; }

    public bool SupportsRippleControl { get; set; }
    public bool RippleControl { get; set; }

    public bool SupportsLongRangeMode { get; set; }
    public bool LongRangeMode { get; set; }

    public bool SupportsAngleTuning { get; set; }
    public int AngleTuningDeg { get; set; }

    public bool SupportsDebounce { get; set; }
    public int MaxDebounceMs { get; set; } = 15;
    public int CurrentDebounceMs { get; set; }

    // ── 休眠与低电量提醒 ─────────────────────────────────────────────────────
    public bool SupportsSleepTimeout { get; set; }

    public int MinSleepSeconds { get; set; } = 60;

    public int MaxSleepSeconds { get; set; } = 900;

    public int CurrentSleepSeconds { get; set; } = 300;

    public List<int> SupportedSleepSeconds { get; set; } = [];

    public bool SupportsLowBatteryWarning { get; set; }

    public int CurrentLowBatteryWarningPercent { get; set; } = 15;

    // ── 原生板载按键重映射与硬件宏 ───────────────────────────────────────────
    public List<ButtonBindingSlot> ButtonBindings { get; set; } = [];

    public bool SupportsOnboardMacroUpload { get; set; }

    public int OnboardMacroSlotCount { get; set; }

    public string? MacroCapabilityNote { get; set; }
}

public sealed class DeviceTuningUpdate
{
    public bool? SetHostMode { get; set; }

    public int? ActiveOnboardProfile { get; set; }

    public (int X, int Y)? Dpi { get; set; }

    public List<(int X, int Y)>? DpiStages { get; set; }

    public int? ActiveDpiStage { get; set; }

    public int? PollingRateHz { get; set; }

    public int? PerformanceMode { get; set; }

    public int? LodCode { get; set; }

    public (bool Enabled, int LiftOff, int Landing)? AsymmetricLod { get; set; }

    public bool? MotionSync { get; set; }

    public bool? AngleSnapping { get; set; }

    public bool? RippleControl { get; set; }

    public bool? LongRangeMode { get; set; }

    public int? AngleTuningDeg { get; set; }

    public int? DebounceMs { get; set; }

    public int? SleepSeconds { get; set; }

    public int? LowBatteryWarningPercent { get; set; }

    public (int SlotIndex, string Action)? ButtonBinding { get; set; }

    public (int MacroSlot, int TargetButtonSlot, OnboardMacroDefinition Macro)? UploadMacro { get; set; }
}

public sealed record DeviceTuningResult(bool Success, string Message, DeviceTuningCapabilities? Capabilities = null);
