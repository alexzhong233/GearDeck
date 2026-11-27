using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GearDeck.Core;
using GearDeck.Core.Models;
using GearDeck.Core.Services;

namespace GearDeck.Desktop.Services;

/// <summary>设备组：内置组（按类型）或自定义组。</summary>
public sealed class DeviceGroup
{
    public required string Id { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>内置组按 DeviceKind 自动聚合；自定义组用成员 id。</summary>
    public string? BuiltinKind { get; set; }

    public List<string> MemberIds { get; set; } = [];
}

public sealed class TaskbarGroupConfig
{
    public required string GroupId { get; set; }
    public bool Visible { get; set; } = true;
    public string? DefaultDeviceId { get; set; }
    public string? CustomGlyph { get; set; }
    public string? CustomIconPath { get; set; }
}

public sealed class DeviceLowBatteryPolicy
{
    public bool Enabled { get; set; } = true;
    public int? CustomThresholdPercent { get; set; }
    public bool WindowsNotification { get; set; } = true;
}

public sealed class CachedOnboardSlotInfo
{
    public int SlotIndex { get; set; }
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
}

public sealed class CachedDeviceOnboardManifest
{
    public int SlotCount { get; set; }
    public int ActiveSlot { get; set; }
    public DateTimeOffset LastProbedAt { get; set; }
    public List<CachedOnboardSlotInfo> Slots { get; set; } = [];
}

public sealed class SensitivityConverterPrefs
{
    public string BaseGameId { get; set; } = "cs2";
    public double BaseSensitivity { get; set; } = 1.0;
    public double BaseFov { get; set; } = 90;
    public string TargetGameId { get; set; } = "valorant";
    public double TargetFov { get; set; } = 103;
    public int ConversionMethod { get; set; }
    public double BaseDpi { get; set; } = 800;
    public bool SameDpi { get; set; } = true;
    public double TargetDpi { get; set; } = 800;
    public int BaseWidth { get; set; } = 1920;
    public int BaseHeight { get; set; } = 1080;
    public bool BaseStretched { get; set; }
    public bool SameResolution { get; set; } = true;
    public int TargetWidth { get; set; } = 1920;
    public int TargetHeight { get; set; } = 1080;
    public bool TargetStretched { get; set; }
    public Dictionary<string, double> PerGameFovs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AppSettings
{
    public bool WidgetVisible { get; set; } = true;

    public bool WidgetEmbedded { get; set; } = true;

    /// <summary>任务栏贴片浮窗触发方式：true=鼠标悬浮自动弹出，false=左键点击弹出。</summary>
    public bool WidgetFlyoutOnHover { get; set; }

    /// <summary>水平偏移（DIP，正数向右，负数向左）。</summary>
    public int WidgetOffsetX { get; set; }

    public bool PositionMigratedV2 { get; set; }

    public int WidgetFloatX { get; set; } = -1;

    public int WidgetFloatY { get; set; } = -1;

    public int WidgetHeight { get; set; } = 40;

    public int WidgetFontSize { get; set; } = 13;

    public int WidgetBlockWidth { get; set; } = 92;

    public string MonitorTopMetric { get; set; } = "CPU";

    public string MonitorBottomMetric { get; set; } = "RAM";

    public List<TaskbarGroupConfig> TaskbarGroups { get; set; } = [];

    /// <summary>left / right。</summary>
    public string WidgetAlignment { get; set; } = "right";

    /// <summary>贴片上显示的组 id。</summary>
    public string WidgetGroupId { get; set; } = "builtin:mouse";

    public List<DeviceGroup> CustomGroups { get; set; } = [];

    public bool MonitorEnabled { get; set; }

    public List<string> MonitorFlyoutMetrics { get; set; } = [];

    public int PollIntervalSeconds { get; set; } = 15;

    public int GameAutoPollIntervalMs { get; set; } = 1000;

    public int LowBatteryThreshold { get; set; } = BatteryHistoryHelper.DefaultLowBatteryThreshold;

    public bool GlobalLowBatteryNotificationEnabled { get; set; } = true;

    public Dictionary<string, CachedBatteryRecord> LastKnownBatteries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool DebugModeEnabled { get; set; }

    public bool NotifyOnProfileSwitch { get; set; }

    public bool AutoYieldToVendorSoftware { get; set; } = true;

    public List<string> RemovedBuiltinGroups { get; set; } = [];

    public Dictionary<string, string> KnownReceiverModels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> DeviceCustomNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool UseCardLayout { get; set; } = true;

    public Dictionary<string, DeviceLowBatteryPolicy> DeviceLowBatteryPolicies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> DeviceImageOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> AutoCloudDeviceImages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string CustomCloudGalleryManifestUrl { get; set; } = "";

    // ── 在线检查更新与自定义 GitHub 镜像站 ────────────────────────────────────
    public string GitHubMirrorUrl { get; set; } = "";

    public bool CheckUpdateOnStartup { get; set; } = true;

    public string UpdateRepoSlug { get; set; } = "alexzhong233/GearDeck";

    // ── macOS 菜单栏状态（默认平铺显示） ────────────────────────────────────────
    public bool MacStatusBarTiled { get; set; } = true;

    public bool MacStatusBarShowMetrics { get; set; }

    // ── 托盘常驻与开机自启 ─────────────────────────────────────────────────
    public bool CloseToTray { get; set; } = true;

    public bool LaunchMinimizedToTray { get; set; }

    public bool LaunchOnStartup { get; set; }

    // ── 按游戏 / 进程 / 独占全屏自动切换配置与硬件宏库 ─────────────────────────
    public bool AutoSwitchEnabled { get; set; } = true;

    public bool EnableExclusiveFullscreenFallback { get; set; }

    /// <summary>开启全屏独占切换后：false=包含无边框全屏，true=仅原生 Direct3D 独占全屏 (QUNS_BUSY)。</summary>
    public bool OnlyTrueExclusiveFullscreen { get; set; }

    public bool RestoreDesktopTargetsOnExit { get; set; } = true;

    /// <summary>只要检测到全屏独占中的窗口/对应游戏进程尚未退出，切出焦点 (Alt+Tab) 时不切回桌面默认配置。</summary>
    public bool KeepProfileUntilWindowExits { get; set; } = true;

    public List<MouseHardwarePreset> Presets { get; set; } = [];

    public List<GameAutoRule> GameRules { get; set; } = [];

    public Dictionary<string, string> FullscreenFallbackTargets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> DesktopDefaultTargets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, CachedDeviceOnboardManifest> DeviceOnboardManifests { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool SoftwareMacrosEnabled { get; set; } = true;

    public List<OnboardMacroDefinition> Macros { get; set; } = [];

    /// <summary>Key = "{deviceId}|{slotIndex}", Value = macroId（仅用于无板载多步宏闪存的鼠标做本地软件宏绑定）。</summary>
    public Dictionary<string, string> LocalButtonMacroBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public SensitivityConverterPrefs SensitivityPrefs { get; set; } = new();

    public ExitRestoreMode GetExitRestoreMode()
    {
        if (!RestoreDesktopTargetsOnExit)
            return ExitRestoreMode.NeverRestore;
        return KeepProfileUntilWindowExits ? ExitRestoreMode.WhenWindowExits : ExitRestoreMode.WhenFocusLeaves;
    }

    public void SetExitRestoreMode(ExitRestoreMode mode)
    {
        switch (mode)
        {
            case ExitRestoreMode.WhenWindowExits:
                RestoreDesktopTargetsOnExit = true;
                KeepProfileUntilWindowExits = true;
                break;
            case ExitRestoreMode.WhenFocusLeaves:
                RestoreDesktopTargetsOnExit = true;
                KeepProfileUntilWindowExits = false;
                break;
            default:
                RestoreDesktopTargetsOnExit = false;
                break;
        }
    }

    [JsonIgnore]
    public static AppSettings Default { get; } = new();
}

public sealed class SettingsService
{
    private static readonly string Dir = GetSettingsDir();

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly string LaunchAgentPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "com.geardeck.desktop.plist");

    // 与 Widget 的 SystemMetricsEngine.DefaultFlyoutMetricIds 保持一致；Desktop 不引用 Widget 项目。
    private static readonly string[] DefaultFlyoutMetricIds =
    [
        "CPU", "CPU_FREQ", "RAM", "GPU", "GPU_TEMP", "VRAM", "NET_DOWN", "NET_UP", "DISK", "DISK_SPACE", "UPTIME"
    ];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public object SyncRoot { get; } = new();

    public AppSettings Current { get; private set; } = new();

    public SettingsService()
    {
        MigrateLegacyAppDataIfNeeded();
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
        EnsureDefaults();
        if (Current.LaunchOnStartup)
            ApplyStartupRegistry(true);
    }

    private static Environment.SpecialFolder SettingsRootFolder =>
        OperatingSystem.IsWindows() ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.ApplicationData;

    private static string GetSettingsDir()
        => Path.Combine(Environment.GetFolderPath(SettingsRootFolder), "GearDeck");

    private static void MigrateLegacyAppDataIfNeeded()
    {
        try
        {
            string legacyDir = Path.Combine(Environment.GetFolderPath(SettingsRootFolder), "PeripheralHub");
            if (File.Exists(FilePath) || !Directory.Exists(legacyDir))
                return;

            Directory.CreateDirectory(Dir);

            foreach (string fileName in new[] { "settings.json", "device-images.json", "widget-position.json", "openmouse-games.json" })
            {
                string src = Path.Combine(legacyDir, fileName);
                string dst = Path.Combine(Dir, fileName);
                if (!File.Exists(src) || File.Exists(dst)) continue;

                if (fileName == "settings.json")
                {
                    string raw = File.ReadAllText(src)
                        .Replace(@"\\PeripheralHub\\", @"\\GearDeck\\", StringComparison.OrdinalIgnoreCase)
                        .Replace(@"\PeripheralHub\", @"\GearDeck\", StringComparison.OrdinalIgnoreCase);
                    File.WriteAllText(dst, raw);
                }
                else
                {
                    File.Copy(src, dst, overwrite: false);
                }
            }

            foreach (string subDir in new[] { "device-images", "image-cache", "game-icons" })
            {
                string srcSub = Path.Combine(legacyDir, subDir);
                string dstSub = Path.Combine(Dir, subDir);
                if (Directory.Exists(srcSub))
                    CopyDirectoryRecursive(srcSub, dstSub);
            }
        }
        catch { }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(targetDir, Path.GetFileName(file));
            if (!File.Exists(destFile))
                File.Copy(file, destFile, overwrite: false);
        }
        foreach (string dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
    }

    public void EnsureDefaults()
    {
        Current.RemovedBuiltinGroups ??= [];
        foreach (var id in new[] { "builtin:mouse", "builtin:keyboard", "builtin:headset", "builtin:controller" })
        {
            if (Current.RemovedBuiltinGroups.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (Current.TaskbarGroups.All(g => g.GroupId != id))
                Current.TaskbarGroups.Add(new TaskbarGroupConfig { GroupId = id, Visible = id != "builtin:controller" });
        }
        foreach (var g in Current.CustomGroups)
        {
            if (Current.TaskbarGroups.All(t => t.GroupId != g.Id))
                Current.TaskbarGroups.Add(new TaskbarGroupConfig { GroupId = g.Id, Visible = true });
        }
        if (!Current.PositionMigratedV2)
        {
            Current.WidgetOffsetX = 0;
            Current.PositionMigratedV2 = true;
        }
        if (Current.MonitorFlyoutMetrics == null || Current.MonitorFlyoutMetrics.Count == 0)
        {
            Current.MonitorFlyoutMetrics = [.. DefaultFlyoutMetricIds];
        }
        Current.Presets ??= [];
        Current.GameRules ??= [];
        Current.FullscreenFallbackTargets ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Current.DesktopDefaultTargets ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Current.DeviceOnboardManifests ??= new Dictionary<string, CachedDeviceOnboardManifest>(StringComparer.OrdinalIgnoreCase);
        Current.Macros ??= [];
        Current.LocalButtonMacroBindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Current.DeviceLowBatteryPolicies ??= new Dictionary<string, DeviceLowBatteryPolicy>(StringComparer.OrdinalIgnoreCase);
        Current.LastKnownBatteries ??= new Dictionary<string, CachedBatteryRecord>(StringComparer.OrdinalIgnoreCase);
        Current.KnownReceiverModels ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Current.DeviceCustomNames ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Current.DeviceImageOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Current.AutoCloudDeviceImages ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Current.CustomCloudGalleryManifestUrl ??= "";
        Current.GitHubMirrorUrl ??= "";
        Current.PollIntervalSeconds = Math.Clamp(Current.PollIntervalSeconds <= 0 ? 15 : Current.PollIntervalSeconds, 5, 300);
        Current.GameAutoPollIntervalMs = Math.Clamp(Current.GameAutoPollIntervalMs <= 0 ? 1000 : Current.GameAutoPollIntervalMs, 250, 5000);
        if (string.IsNullOrWhiteSpace(Current.UpdateRepoSlug))
            Current.UpdateRepoSlug = "alexzhong233/GearDeck";
        foreach (var rule in Current.GameRules)
        {
            rule.ProcessNames ??= [];
            rule.PerMouseTargets ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        foreach (var m in Current.DeviceOnboardManifests.Values)
            m.Slots ??= [];
    }

    public const int DefaultLowBatteryThreshold = BatteryHistoryHelper.DefaultLowBatteryThreshold;

    public DeviceLowBatteryPolicy GetLowBatteryPolicy(string deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId) &&
            Current.DeviceLowBatteryPolicies.TryGetValue(deviceId, out var policy))
            return policy;
        return new DeviceLowBatteryPolicy();
    }

    public int GetEffectiveLowBatteryThreshold(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return DefaultLowBatteryThreshold;
        var policy = GetLowBatteryPolicy(deviceId);
        return policy.CustomThresholdPercent is { } c
            ? Math.Clamp(c, 5, 80)
            : DefaultLowBatteryThreshold;
    }

    public bool IsDeviceLowBattery(string deviceId, int batteryPercent)
    {
        var policy = GetLowBatteryPolicy(deviceId);
        return policy.Enabled && batteryPercent <= GetEffectiveLowBatteryThreshold(deviceId);
    }

    public bool ShouldNotifyLowBattery(string deviceId, int batteryPercent)
    {
        if (!Current.GlobalLowBatteryNotificationEnabled)
            return false;
        var policy = GetLowBatteryPolicy(deviceId);
        return policy.Enabled && policy.WindowsNotification && batteryPercent <= GetEffectiveLowBatteryThreshold(deviceId);
    }

    public CachedBatteryRecord? GetLastKnownBattery(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        lock (SyncRoot)
        {
            return Current.LastKnownBatteries.TryGetValue(deviceId, out var rec) ? rec : null;
        }
    }

    public bool RecordObservedDevices(IEnumerable<PeripheralDevice> devices)
    {
        bool changed;
        lock (SyncRoot)
        {
            changed = BatteryHistoryHelper.RecordObservedDevices(Current.LastKnownBatteries, devices);
        }
        if (changed)
            Save();
        return changed;
    }

    public bool UpdateKnownReceiverModels(IEnumerable<PeripheralDevice> devices)
    {
        bool changed = false;
        lock (SyncRoot)
        {
            foreach (var (key, val) in ReceiverModelRegistry.SnapshotKnown())
            {
                if (!string.IsNullOrWhiteSpace(val) &&
                    (!Current.KnownReceiverModels.TryGetValue(key, out var prev) || !string.Equals(prev, val, StringComparison.Ordinal)))
                {
                    Current.KnownReceiverModels[key] = val;
                    changed = true;
                }
            }
            foreach (var d in devices)
            {
                if (d.Id.StartsWith("atk:", StringComparison.OrdinalIgnoreCase)
                    && !GearDeck.Core.Providers.AtkModelCatalog.IsGenericReceiverName(d.Name))
                {
                    if (!Current.KnownReceiverModels.TryGetValue(d.Id, out var prev)
                        || !string.Equals(prev, d.Name, StringComparison.Ordinal))
                    {
                        Current.KnownReceiverModels[d.Id] = d.Name;
                        changed = true;
                    }
                }
            }
        }
        return changed;
    }

    public static string BuildCapsSummary(DeviceTuningCapabilities caps)
    {
        var parts = new List<string>();
        if (caps.SupportsDpi && caps.CurrentDpiX > 0)
        {
            parts.Add(caps.SupportsIndependentXY && caps.CurrentDpiY != caps.CurrentDpiX
                ? $"X:{caps.CurrentDpiX}/Y:{caps.CurrentDpiY} DPI"
                : $"{caps.CurrentDpiX} DPI");
        }
        if (caps.CurrentPollingRate > 0)
            parts.Add($"{caps.CurrentPollingRate}Hz");
        if (caps.SupportedPerformanceModes.Count > 0)
        {
            var pm = caps.SupportedPerformanceModes.FirstOrDefault(m => m.Code == caps.CurrentPerformanceMode);
            if (pm != null) parts.Add(pm.Label);
        }
        return string.Join(" · ", parts);
    }

    public void RecordCapabilitiesManifest(string deviceId, DeviceTuningCapabilities caps)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || !caps.IsOnline || caps.OnboardProfileCount <= 0)
            return;

        if (!Current.DeviceOnboardManifests.TryGetValue(deviceId, out var manifest))
        {
            manifest = new CachedDeviceOnboardManifest();
            Current.DeviceOnboardManifests[deviceId] = manifest;
        }

        manifest.SlotCount = caps.OnboardProfileCount;
        manifest.ActiveSlot = Math.Clamp(caps.ActiveOnboardProfile, 0, Math.Max(0, caps.OnboardProfileCount - 1));
        manifest.LastProbedAt = DateTimeOffset.Now;

        string activeSummary = BuildCapsSummary(caps);
        for (int i = 0; i < caps.OnboardProfileCount; i++)
        {
            string name = i < caps.OnboardProfileNames.Count && !string.IsNullOrWhiteSpace(caps.OnboardProfileNames[i])
                ? caps.OnboardProfileNames[i]
                : $"板载配置 {i + 1}";

            var existing = manifest.Slots.FirstOrDefault(s => s.SlotIndex == i);
            if (existing == null)
            {
                existing = new CachedOnboardSlotInfo { SlotIndex = i, Name = name };
                manifest.Slots.Add(existing);
            }
            else
            {
                existing.Name = name;
            }

            if (i == manifest.ActiveSlot && !string.IsNullOrEmpty(activeSummary))
                existing.Summary = activeSummary;
        }

        manifest.Slots = manifest.Slots
            .Where(s => s.SlotIndex >= 0 && s.SlotIndex < caps.OnboardProfileCount)
            .OrderBy(s => s.SlotIndex)
            .ToList();
        Save();
    }

    public List<CachedOnboardSlotInfo> GetDeviceOnboardSlots(PeripheralDevice? dev, string deviceId)
    {
        if (Current.DeviceOnboardManifests.TryGetValue(deviceId, out var manifest) && manifest.Slots.Count > 0)
            return manifest.Slots.OrderBy(s => s.SlotIndex).ToList();

        int defaultCount = EstimateDefaultOnboardSlotCount(dev, deviceId);
        if (defaultCount <= 0) return [];
        return Enumerable.Range(0, defaultCount)
            .Select(i => new CachedOnboardSlotInfo { SlotIndex = i, Name = $"板载配置 {i + 1}" })
            .ToList();
    }

    public static int EstimateDefaultOnboardSlotCount(PeripheralDevice? dev, string deviceId)
    {
        string id = deviceId.ToLowerInvariant();
        string prov = dev?.Provider?.ToLowerInvariant() ?? "";
        if (id.StartsWith("logi:") || prov.Contains("logitech")) return 5;
        if (id.StartsWith("wlmouse:") || id.StartsWith("lamzu:") || id.StartsWith("atk:") ||
            id.StartsWith("mchose:") || id.StartsWith("aula:") || id.StartsWith("flydigi:") ||
            prov is "wlmouse" or "lamzu" or "atk" or "mchose" or "aula" or "flydigi")
            return 4;
        return 0;
    }

    public async Task<CachedDeviceOnboardManifest?> ProbeAllOnboardSlotsAsync(DeviceManager manager, string deviceId)
    {
        var initial = await manager.QueryCapabilitiesAsync(deviceId);
        if (!initial.IsOnline)
            return null;

        RecordCapabilitiesManifest(deviceId, initial);
        if (initial.OnboardProfileCount <= 1)
            return Current.DeviceOnboardManifests.GetValueOrDefault(deviceId);

        int originalSlot = Math.Clamp(initial.ActiveOnboardProfile, 0, initial.OnboardProfileCount - 1);
        try
        {
            for (int i = 0; i < initial.OnboardProfileCount; i++)
            {
                if (i == originalSlot) continue;
                var res = await manager.ApplySettingsAsync(deviceId, new DeviceTuningUpdate { ActiveOnboardProfile = i });
                if (res.Capabilities != null && res.Capabilities.IsOnline)
                {
                    RecordCapabilitiesManifest(deviceId, res.Capabilities);
                }
                else
                {
                    var caps = await manager.QueryCapabilitiesAsync(deviceId);
                    if (caps.IsOnline) RecordCapabilitiesManifest(deviceId, caps);
                }
            }
        }
        finally
        {
            var restored = await manager.ApplySettingsAsync(deviceId, new DeviceTuningUpdate { ActiveOnboardProfile = originalSlot });
            if (restored.Capabilities != null && restored.Capabilities.IsOnline)
                RecordCapabilitiesManifest(deviceId, restored.Capabilities);
        }

        return Current.DeviceOnboardManifests.GetValueOrDefault(deviceId);
    }

    public static (bool Success, string Detail) ApplyStartupRegistry(bool enable)
    {
        if (!OperatingSystem.IsWindows())
            return ApplyLaunchAgent(enable);

        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string approvedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string valueName = "GearDeck";
        try
        {
            Win32RegistryHelper.DeleteValue(Win32RegistryRoot.HKCU, runKeyPath, "PeripheralHub");
            if (!enable)
            {
                Win32RegistryHelper.DeleteValue(Win32RegistryRoot.HKCU, runKeyPath, valueName);
                Diag.Log("ApplyStartupRegistry(false): 已移除开机自启项");
                return (true, "");
            }

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                exePath = Path.Combine(AppContext.BaseDirectory, "GearDeck.Desktop.exe");

            string cmd = $"\"{exePath}\" --minimized";
            if (!Win32RegistryHelper.WriteString(Win32RegistryRoot.HKCU, runKeyPath, valueName, cmd))
            {
                Diag.Log("ApplyStartupRegistry 失败：无法打开/创建 HKCU Run 键");
                return (false, "无法打开注册表启动项");
            }

            // 若曾在任务管理器“启动应用”中禁用过，则将 StartupApproved 恢复为启用 (0x02)
            try
            {
                var state = Win32RegistryHelper.ReadBinary(Win32RegistryRoot.HKCU, approvedKeyPath, valueName);
                if (state is { Length: > 0 } && state[0] != 0x02)
                {
                    Win32RegistryHelper.WriteBinary(
                        Win32RegistryRoot.HKCU,
                        approvedKeyPath,
                        valueName,
                        [0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
                }
            }
            catch { }

            string? verified = Win32RegistryHelper.ReadString(Win32RegistryRoot.HKCU, runKeyPath, valueName);
            bool ok = !string.IsNullOrWhiteSpace(verified);
            Diag.Log($"ApplyStartupRegistry(true): ok={ok} cmd={cmd}");
            return ok ? (true, cmd) : (false, "写入后回读校验为空");
        }
        catch (Exception ex)
        {
            Diag.Log($"ApplyStartupRegistry({enable}) 异常: {ex.Message}");
            return (false, ex.Message);
        }
    }

    /// <summary>与 App 版同义的开机自启入口别名。</summary>
    public static (bool Success, string Detail) ApplyStartupRegistration(bool enable)
        => ApplyStartupRegistry(enable);

    private static (bool Success, string Detail) ApplyLaunchAgent(bool enable)
    {
        try
        {
            if (!enable)
            {
                if (File.Exists(LaunchAgentPath))
                    File.Delete(LaunchAgentPath);
                return (true, "");
            }

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                exePath = Path.Combine(AppContext.BaseDirectory, "GearDeck.Desktop");

            string dir = Path.GetDirectoryName(LaunchAgentPath)!;
            Directory.CreateDirectory(dir);
            string escaped = EscapeXml(exePath);
            string plist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>com.geardeck.desktop</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>{escaped}</string>
                        <string>--minimized</string>
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                    <key>KeepAlive</key>
                    <false/>
                </dict>
                </plist>
                """;
            File.WriteAllText(LaunchAgentPath, plist);
            return (true, $"\"{exePath}\" --minimized");
        }
        catch (Exception ex)
        {
            Diag.Log($"ApplyLaunchAgent({enable}) 异常: {ex.Message}");
            return (false, ex.Message);
        }
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    public static bool IsStartupRegistryEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return File.Exists(LaunchAgentPath);

        try
        {
            const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string approvedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
            const string valueName = "GearDeck";

            string? val = Win32RegistryHelper.ReadString(Win32RegistryRoot.HKCU, runKeyPath, valueName);
            if (string.IsNullOrWhiteSpace(val))
                return false;

            var state = Win32RegistryHelper.ReadBinary(Win32RegistryRoot.HKCU, approvedKeyPath, valueName);
            if (state is { Length: > 0 } && state[0] != 0x02)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Save()
    {
        try
        {
            string json;
            lock (SyncRoot)
            {
                json = JsonSerializer.Serialize(Current, JsonOpts);
                Directory.CreateDirectory(Dir);
                string tmpPath = $"{FilePath}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, FilePath, overwrite: true);
            }
        }
        catch
        {
            // 设置写失败不致命
        }
    }
}
