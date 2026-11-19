namespace GearDeck.Core.Models;

public sealed record DpiStageEntry(int X, int Y);

/// <summary>
/// 用户保存的硬件参数预设（可作用于任意鼠标，或按游戏规则一对一绑定某只鼠标）。
/// </summary>
public sealed class MouseHardwarePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "新硬件预设";
    public string SourceDeviceId { get; set; } = "";
    public string SourceDeviceName { get; set; } = "";

    public int? DpiX { get; set; }
    public int? DpiY { get; set; }
    public List<DpiStageEntry>? DpiStages { get; set; }
    public int? ActiveDpiStage { get; set; }
    public int? PollingRateHz { get; set; }
    public int? PerformanceMode { get; set; }
    public int? LodCode { get; set; }
    public bool? AsymmetricLodEnabled { get; set; }
    public int? AsymmetricLiftOff { get; set; }
    public int? AsymmetricLanding { get; set; }
    public bool? MotionSync { get; set; }
    public bool? AngleSnapping { get; set; }
    public bool? RippleControl { get; set; }
    public bool? LongRangeMode { get; set; }
    public int? AngleTuningDeg { get; set; }
    public int? DebounceMs { get; set; }
    public int? SleepSeconds { get; set; }
    public AnalogSwitchUpdate? AnalogSwitches { get; set; }

    public DeviceTuningUpdate ToUpdate()
    {
        List<(int X, int Y)>? stages = DpiStages is { Count: > 0 }
            ? DpiStages.Select(s => (s.X, s.Y)).ToList()
            : null;
        (int X, int Y)? dpi = DpiX is { } x ? (x, DpiY ?? x) : null;
        (bool Enabled, int LiftOff, int Landing)? asym = AsymmetricLodEnabled is { } en
            ? (en, AsymmetricLiftOff ?? 16, AsymmetricLanding ?? 11)
            : null;

        return new DeviceTuningUpdate
        {
            Dpi = dpi,
            DpiStages = stages,
            ActiveDpiStage = ActiveDpiStage,
            PollingRateHz = PollingRateHz,
            PerformanceMode = PerformanceMode,
            LodCode = LodCode,
            AsymmetricLod = asym,
            MotionSync = MotionSync,
            AngleSnapping = AngleSnapping,
            RippleControl = RippleControl,
            LongRangeMode = LongRangeMode,
            AngleTuningDeg = AngleTuningDeg,
            DebounceMs = DebounceMs,
            SleepSeconds = SleepSeconds,
            AnalogSwitches = AnalogSwitches,
        };
    }

    public static MouseHardwarePreset CaptureFrom(string name, string deviceId, string deviceName, DeviceTuningCapabilities caps) => new()
    {
        Name = name,
        SourceDeviceId = deviceId,
        SourceDeviceName = deviceName,
        DpiX = caps.SupportsDpi ? caps.CurrentDpiX : null,
        DpiY = caps.SupportsDpi ? caps.CurrentDpiY : null,
        DpiStages = caps.DpiStages.Count > 0 ? caps.DpiStages.Select(s => new DpiStageEntry(s.X, s.Y)).ToList() : null,
        ActiveDpiStage = caps.DpiStages.Count > 0 ? caps.ActiveDpiStage : null,
        PollingRateHz = caps.CurrentPollingRate > 0 ? caps.CurrentPollingRate : null,
        PerformanceMode = caps.SupportedPerformanceModes.Count > 0 ? caps.CurrentPerformanceMode : null,
        LodCode = caps.SupportedLodOptions.Count > 0 ? caps.CurrentLod : null,
        AsymmetricLodEnabled = caps.SupportsAsymmetricLod ? caps.AsymmetricLodEnabled : null,
        AsymmetricLiftOff = caps.SupportsAsymmetricLod ? caps.AsymmetricLiftOff : null,
        AsymmetricLanding = caps.SupportsAsymmetricLod ? caps.AsymmetricLanding : null,
        MotionSync = caps.SupportsMotionSync ? caps.MotionSync : null,
        AngleSnapping = caps.SupportsAngleSnapping ? caps.AngleSnapping : null,
        RippleControl = caps.SupportsRippleControl ? caps.RippleControl : null,
        LongRangeMode = caps.SupportsLongRangeMode ? caps.LongRangeMode : null,
        AngleTuningDeg = caps.SupportsAngleTuning ? caps.AngleTuningDeg : null,
        DebounceMs = caps.SupportsDebounce ? caps.CurrentDebounceMs : null,
        SleepSeconds = caps.SupportsSleepTimeout ? caps.CurrentSleepSeconds : null,
        AnalogSwitches = caps.SupportsAnalogSwitchTuning && caps.LeftAnalogButton != null && caps.RightAnalogButton != null
            ? new AnalogSwitchUpdate(
                (caps.LeftAnalogButton with { ButtonIndex = 0 }) != (caps.RightAnalogButton with { ButtonIndex = 0 }),
                caps.LeftAnalogButton,
                caps.RightAnalogButton)
            : null,
    };
}

/// <summary>
/// 单款游戏 / 进程自动切换规则：支持为每只连接中的鼠标分别指定不同的板载配置槽位或参数预设。
/// </summary>
public sealed class GameAutoRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "新游戏规则";
    public bool Enabled { get; set; } = true;

    /// <summary>匹配的进程名列表（如 cs2.exe, valorant-win64-shipping.exe）。</summary>
    public List<string> ProcessNames { get; set; } = [];

    /// <summary>若勾选，仅当该进程同时处于全屏/独占全屏态时才触发。</summary>
    public bool RequireFullscreen { get; set; }

    /// <summary>
    /// 按 DeviceId 映射目标配置：
    /// - "slot:0".."slot:3" => 切换板载硬件配置槽位
    /// - "preset:&lt;id&gt;" => 写入指定硬件参数预设
    /// </summary>
    public Dictionary<string, string> PerMouseTargets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
