using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

#if GEARDECK_DESKTOP
namespace GearDeck.Desktop.Services;
#else
namespace GearDeck.Metrics;
#endif

[Flags]
public enum MetricScope
{
    None = 0,
    Cpu = 1 << 0,
    CpuFreq = 1 << 1,
    Memory = 1 << 2,
    Gpu = 1 << 3,
    GpuTemp = 1 << 4,
    Vram = 1 << 5,
    Network = 1 << 6,
    DiskActive = 1 << 7,
    DiskSpace = 1 << 8,
    Uptime = 1 << 9,
    CpuTemp = 1 << 10,
    All = Cpu | CpuFreq | CpuTemp | Memory | Gpu | GpuTemp | Vram | Network | DiskActive | DiskSpace | Uptime,
}

public sealed record MetricDefinition(string Id, string ShortLabel, string FullName, string Glyph, bool HasPercentageBar);

public sealed record GpuAdapterInfo(string Name, string LuidKey, uint VendorId, double DedicatedVramGb, double SharedMemGb, bool IsDiscrete);

public sealed class SystemMetricsSnapshot
{
    public double CpuPercent { get; init; }
    public double? CpuFreqGhz { get; init; }
    public int? CpuTempC { get; init; }
    public double RamPercent { get; init; }
    public double MemUsedGb { get; init; }
    public double MemTotalGb { get; init; }
    public double? GpuPercent { get; init; }
    public int? GpuTempC { get; init; }
    public double? VramUsedGb { get; init; }
    public double? VramTotalGb { get; init; }
    public string? ActiveGpuName { get; init; }
    public bool HwInfoConnected { get; init; }
    public double NetDownBytesPerSec { get; init; }
    public double NetUpBytesPerSec { get; init; }
    public double DiskActivePercent { get; init; }
    public string SystemDriveLabel { get; init; } = "C:";
    public double DiskSpaceUsedPercent { get; init; }
    public double DiskFreeGb { get; init; }
    public TimeSpan Uptime { get; init; }
}

/// <summary>
/// 跨平台系统指标共享引擎：支持按 <see cref="MetricScope"/> 惰性初始化 NVML 与 PDH 计数器，
/// 避免未启用 GPU/磁盘指标时加载 nvml.dll 或多余 PDH 句柄。
/// </summary>
public static class SystemMetricsEngine
{
    public static IReadOnlyList<MetricDefinition> AllMetrics { get; } =
    [
#if GEARDECK_DESKTOP
        new("CPU", "CPU 占用", "CPU 使用率", "\uE9F5", true),
        new("CPU_FREQ", "CPU 频率", "CPU 实时频率", "\uE945", false),
        new("CPU_TEMP", "CPU 温度", "CPU 核心温度", "\uE9CA", false),
        new("RAM", "内存占用", "内存使用率", "\uE7F1", true),
#else
        new("CPU", "CPU 占用", "CPU 使用率", "\uE950", true),
        new("CPU_FREQ", "CPU 频率", "CPU 实时频率", "\uE945", false),
        new("CPU_TEMP", "CPU 温度", "CPU 核心温度", "\uE9CA", false),
        new("RAM", "内存占用", "内存使用率", "\uE964", true),
#endif
        new("MEM_USED", "已用内存", "已用物理内存", "\uE7F8", true),
        new("GPU", "GPU 占用", "GPU 核心占用", "\uE7F4", true),
        new("GPU_TEMP", "GPU 温度", "GPU 核心温度", "\uE9CA", false),
        new("VRAM", "显存占用", "GPU 显存占用", "\uE74E", true),
        new("NET_DOWN", "下行网速", "网络下载速率", "\uE896", false),
        new("NET_UP", "上行网速", "网络上传速率", "\uE898", false),
        new("DISK", "磁盘活动", "磁盘读写占用", "\uEDA2", true),
        new("DISK_SPACE", "系统盘", "系统盘空间占用", "\uE8B7", true),
        new("UPTIME", "开机时长", "系统运行时长", "\uE916", false),
    ];

    public static IReadOnlyList<string> DefaultFlyoutMetricIds { get; } =
    [
        "CPU", "CPU_FREQ", "CPU_TEMP", "RAM", "GPU", "GPU_TEMP", "VRAM", "NET_DOWN", "NET_UP", "DISK", "DISK_SPACE", "UPTIME"
    ];

    public static bool HwInfoBridgeEnabled
    {
        get => HwInfoSharedMemoryReader.Enabled;
        set => HwInfoSharedMemoryReader.Enabled = value;
    }

    private const double Gb = 1073741824.0;

    private static readonly object Gate = new();
    private static SystemMetricsSnapshot _lastSnapshot = new();
    private static long _lastSampleMs;
    private static MetricScope _lastSampleScope;

    private static ulong _lastIdle, _lastKernel, _lastUser;
    private static ulong _lastMacCpuUser, _lastMacCpuSys, _lastMacCpuIdle, _lastMacCpuNice;
    private static bool _hasMacCpuBaseline;
    private static long _macTotalMemBytes;
    private static int _macPageSize;
    private static long _lastMacGpuSampleMs;
    private static double? _cachedMacGpuPct;
    private static double? _cachedMacVramUsedGb;
    private static long _lastMacGpuTempSampleMs;
    private static int? _cachedMacCpuTempC;
    private static int? _cachedMacGpuTempC;
    private static long _lastMacCpuFreqSampleMs;
    private static double? _cachedMacCpuFreqGhz;
    private static bool _macDvfsProbed;
    private static double[] _macPCoreFreqsGhz = [];
    private static double[] _macECoreFreqsGhz = [];
    private static bool _macIoReportInitAttempted;
    private static IntPtr _macIoReportSub;
    private static IntPtr _macIoReportSubCh;
    private static IntPtr _prevMacIoReportSample;
    private static IntPtr _cfStrIoReportChannels;
    private static ulong _lastNetRx, _lastNetTx;
    private static long _lastNetTick;
    private static double _baseCpuGhz;

    // NVML (NVIDIA GPU 精准指标，仅在需要 GPU/VRAM 时惰性加载)
    private static bool _nvmlProbed;
    private static bool _nvmlOk;
    private static IntPtr _nvmlDevice;
    private static NvmlGetUtil? _nvmlGetUtil;
    private static NvmlGetTemp? _nvmlGetTemp;
    private static NvmlGetMem? _nvmlGetMem;

    // PDH & DXGI (按需添加所需计数器，并按显卡 LUID 隔离集显/独显)
    private static bool _dxgiProbed;
    private static List<GpuAdapterInfo> _dxgiAdapters = [];
    private static bool _pdhQueryOpened;
    private static IntPtr _pdhQuery;
    private static IntPtr _pdhCpuPerf;
    private static IntPtr _pdhDiskTime;
    private static IntPtr _pdhGpu3d;
    private static IntPtr _pdhGpuVram;
    private static IntPtr _pdhGpuSharedVram;
    private static MetricScope _pdhAddedFlags;
    private static bool _pdhPrimed;

    public static MetricScope FromMetricIds(params string?[] ids)
    {
        MetricScope scope = MetricScope.None;
        foreach (string? raw in ids)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            scope |= raw.Trim().ToUpperInvariant() switch
            {
                "NONE" => MetricScope.None,
                "CPU" => MetricScope.Cpu,
                "CPU_FREQ" => MetricScope.CpuFreq,
                "CPU_TEMP" => MetricScope.CpuTemp,
                "RAM" or "MEM" or "MEM_USED" => MetricScope.Memory,
                "GPU" => MetricScope.Gpu,
                "GPU_TEMP" => MetricScope.GpuTemp,
                "VRAM" => MetricScope.Vram,
                "NET_DOWN" or "NET_UP" => MetricScope.Network,
                "DISK" => MetricScope.DiskActive,
                "DISK_SPACE" => MetricScope.DiskSpace,
                "UPTIME" => MetricScope.Uptime,
                _ => MetricScope.Cpu,
            };
        }
        return scope;
    }

    public static SystemMetricsSnapshot LastSnapshot
    {
        get { lock (Gate) return _lastSnapshot; }
    }

    public static SystemMetricsSnapshot Sample(MetricScope scope = MetricScope.All)
    {
        lock (Gate)
        {
            long now = Environment.TickCount64;
            if (_lastSampleMs != 0 && now - _lastSampleMs < 750 && (_lastSampleScope & scope) == scope)
                return _lastSnapshot;
            _lastSampleMs = now;
            _lastSampleScope = scope;

            if (scope == MetricScope.None)
                return _lastSnapshot;

            bool isWindows = OperatingSystem.IsWindows();
            double cpu = scope.HasFlag(MetricScope.Cpu)
                ? (isWindows ? SampleCpu() : SampleMacCpu())
                : _lastSnapshot.CpuPercent;

            var (ramPct, memUsedGb, memTotalGb) = scope.HasFlag(MetricScope.Memory)
                ? (isWindows ? SampleMemory() : SampleMacMemory())
                : (_lastSnapshot.RamPercent, _lastSnapshot.MemUsedGb, _lastSnapshot.MemTotalGb);

            var (netDown, netUp) = scope.HasFlag(MetricScope.Network)
                ? SampleNetwork(now)
                : (_lastSnapshot.NetDownBytesPerSec, _lastSnapshot.NetUpBytesPerSec);

            var (driveLabel, diskUsedPct, diskFreeGb) = scope.HasFlag(MetricScope.DiskSpace)
                ? SampleDiskSpace()
                : (_lastSnapshot.SystemDriveLabel, _lastSnapshot.DiskSpaceUsedPercent, _lastSnapshot.DiskFreeGb);

            bool needCpuFreq = scope.HasFlag(MetricScope.CpuFreq);
            bool needCpuTemp = scope.HasFlag(MetricScope.CpuTemp);
            bool needDiskActive = scope.HasFlag(MetricScope.DiskActive);
            bool needGpuAny = (scope & (MetricScope.Gpu | MetricScope.GpuTemp | MetricScope.Vram)) != 0;
            bool needPdh = isWindows && (needCpuFreq || needDiskActive || scope.HasFlag(MetricScope.Gpu) || scope.HasFlag(MetricScope.Vram));
            bool needHwInfo = isWindows && HwInfoBridgeEnabled &&
                              (needCpuFreq || needCpuTemp || needGpuAny);

            HwInfoSnapshot hwInfo = needHwInfo
                ? HwInfoSharedMemoryReader.TryRead(now)
                : HwInfoSnapshot.Empty;

            double? cpuPerfPct = null;
            double diskActivePct = _lastSnapshot.DiskActivePercent;
            Dictionary<string, double> gpu3dByLuid = [];
            Dictionary<string, double> dedicatedVramByLuid = [];
            Dictionary<string, double> sharedVramByLuid = [];

            if (isWindows)
            {
                if (needCpuFreq)
                    EnsureBaseCpuFreq();
                if (needPdh)
                {
                    EnsurePdhCounters(scope);
                    if (_pdhQuery != IntPtr.Zero && PdhCollectQueryData(_pdhQuery) == 0)
                    {
                        if (_pdhPrimed)
                        {
                            if (needCpuFreq)
                                cpuPerfPct = ReadPdhDouble(_pdhCpuPerf);
                            if (needDiskActive)
                                diskActivePct = Math.Clamp(ReadPdhDouble(_pdhDiskTime) ?? 0, 0, 100);
                            if (scope.HasFlag(MetricScope.Gpu))
                                gpu3dByLuid = SumPdhByLuid(_pdhGpu3d);
                            if (scope.HasFlag(MetricScope.Vram))
                            {
                                dedicatedVramByLuid = SumPdhByLuid(_pdhGpuVram);
                                sharedVramByLuid = SumPdhByLuid(_pdhGpuSharedVram);
                            }
                        }
                        _pdhPrimed = true;
                    }
                }
                if (needGpuAny)
                {
                    EnsureDxgiAdapters();
                    EnsureNvml();
                }
            }

            double? cpuFreqGhz = _lastSnapshot.CpuFreqGhz;
            if (needCpuFreq)
            {
                if (isWindows)
                {
                    if (hwInfo.Connected && hwInfo.CpuFreqGhz is { } hwFreq)
                    {
                        cpuFreqGhz = hwFreq;
                    }
                    else if (_baseCpuGhz > 0)
                    {
                        cpuFreqGhz = cpuPerfPct is { } p && p > 0
                            ? Math.Round(_baseCpuGhz * p / 100.0, 2)
                            : _baseCpuGhz;
                    }
                }
                else if (OperatingSystem.IsMacOS())
                {
                    cpuFreqGhz = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                        ? SampleMacArm64CpuFreqThrottled(now)
                        : SampleMacIntelCpuFreqThrottled(now);
                }
            }

            int? cpuTempC = _lastSnapshot.CpuTempC;
            double? gpuPct = _lastSnapshot.GpuPercent;
            int? gpuTempC = _lastSnapshot.GpuTempC;
            double? vramUsedGb = _lastSnapshot.VramUsedGb;
            double? vramTotalGb = _lastSnapshot.VramTotalGb;
            string? activeGpuName = _lastSnapshot.ActiveGpuName;

            if (OperatingSystem.IsMacOS() && (needCpuTemp || scope.HasFlag(MetricScope.GpuTemp)))
            {
                var (macCpuT, macGpuT) = SampleMacTempsThrottled(now);
                if (needCpuTemp)
                    cpuTempC = macCpuT;
                if (scope.HasFlag(MetricScope.GpuTemp))
                    gpuTempC = macGpuT;
            }
            else if (isWindows && needCpuTemp)
            {
                cpuTempC = hwInfo.Connected ? hwInfo.CpuTempC : null;
            }

            if (needGpuAny)
            {
                gpuPct = null;
                if (!OperatingSystem.IsMacOS())
                    gpuTempC = null;
                vramUsedGb = null;
                vramTotalGb = null;

                if (isWindows)
                {
                    var (dxgiGpuPct, dxgiVramUsed, dxgiVramTotal, dxgiGpuName) =
                        SelectPrimaryGpuByLuid(gpu3dByLuid, dedicatedVramByLuid, sharedVramByLuid, _dxgiAdapters);
                    gpuPct = dxgiGpuPct;
                    vramUsedGb = dxgiVramUsed;
                    vramTotalGb = dxgiVramTotal;
                    if (!string.IsNullOrWhiteSpace(dxgiGpuName))
                        activeGpuName = dxgiGpuName;

                    if (_nvmlOk && _nvmlDevice != IntPtr.Zero)
                    {
                        if (scope.HasFlag(MetricScope.Gpu) && _nvmlGetUtil != null && _nvmlGetUtil(_nvmlDevice, out var util) == 0)
                            gpuPct = util.Gpu;
                        if (scope.HasFlag(MetricScope.GpuTemp) && _nvmlGetTemp != null && _nvmlGetTemp(_nvmlDevice, 0, out uint temp) == 0 && temp < 150)
                            gpuTempC = (int)temp;
                        if (scope.HasFlag(MetricScope.Vram) && _nvmlGetMem != null && _nvmlGetMem(_nvmlDevice, out var mem) == 0 && mem.Total > 0)
                        {
                            vramUsedGb = mem.Used / Gb;
                            vramTotalGb = mem.Total / Gb;
                        }
                    }

                    if (hwInfo.Connected)
                    {
                        if (scope.HasFlag(MetricScope.Gpu) && hwInfo.GpuPercent is { } hwGpuPct)
                            gpuPct = hwGpuPct;
                        if (scope.HasFlag(MetricScope.GpuTemp) && hwInfo.GpuTempC is { } hwGpuTemp)
                            gpuTempC = hwGpuTemp;
                        if (scope.HasFlag(MetricScope.Vram) && hwInfo.VramUsedGb is { } hwVramUsed)
                            vramUsedGb = hwVramUsed;
                        if (scope.HasFlag(MetricScope.Vram) && hwInfo.VramTotalGb is { } hwVramTotal && hwVramTotal > 0)
                            vramTotalGb = hwVramTotal;
                        if (!string.IsNullOrWhiteSpace(hwInfo.GpuName))
                            activeGpuName = hwInfo.GpuName;
                    }
                }
                else if (OperatingSystem.IsMacOS())
                {
                    if (scope.HasFlag(MetricScope.Gpu) || scope.HasFlag(MetricScope.Vram))
                        (gpuPct, vramUsedGb) = SampleMacGpuThrottled(now);
                }

                if (gpuPct is { } gp)
                    gpuPct = Math.Clamp(gp, 0, 100);
            }

            _lastSnapshot = new SystemMetricsSnapshot
            {
                CpuPercent = cpu,
                CpuFreqGhz = cpuFreqGhz,
                CpuTempC = cpuTempC,
                RamPercent = ramPct,
                MemUsedGb = memUsedGb,
                MemTotalGb = memTotalGb,
                GpuPercent = gpuPct,
                GpuTempC = gpuTempC,
                VramUsedGb = vramUsedGb,
                VramTotalGb = vramTotalGb,
                ActiveGpuName = activeGpuName,
                HwInfoConnected = hwInfo.Connected,
                NetDownBytesPerSec = netDown,
                NetUpBytesPerSec = netUp,
                DiskActivePercent = diskActivePct,
                SystemDriveLabel = driveLabel,
                DiskSpaceUsedPercent = diskUsedPct,
                DiskFreeGb = diskFreeGb,
                Uptime = TimeSpan.FromMilliseconds(now),
            };
            return _lastSnapshot;
        }
    }

    public static string FormatTaskbar(string metricId, SystemMetricsSnapshot s)
    {
        return metricId?.ToUpperInvariant() switch
        {
            "NONE" => "",
            "CPU_FREQ" => s.CpuFreqGhz is { } f ? $"{f:F2}GHz" : "--GHz",
            "CPU_TEMP" => s.CpuTempC is { } ct ? $"CPU {ct}°C" : "CPU --°C",
            "RAM" or "MEM" => $"RAM {s.RamPercent:F0}%",
            "MEM_USED" => $"{s.MemUsedGb:F1}G",
            "GPU" => s.GpuPercent is { } g ? $"GPU {g:F0}%" : "GPU --",
            "GPU_TEMP" => s.GpuTempC is { } t ? $"GPU {t}°C" : "GPU --°C",
            "VRAM" => s.VramUsedGb is { } v ? $"VR {v:F1}G" : "VR --",
            "NET_DOWN" => $"↓ {FormatCompactSpeed(s.NetDownBytesPerSec)}",
            "NET_UP" => $"↑ {FormatCompactSpeed(s.NetUpBytesPerSec)}",
            "DISK" => $"DSK {s.DiskActivePercent:F0}%",
            "DISK_SPACE" => $"{s.SystemDriveLabel} {s.DiskSpaceUsedPercent:F0}%",
            "UPTIME" => $"UP {(int)s.Uptime.TotalHours:D2}:{s.Uptime.Minutes:D2}",
            _ => $"CPU {s.CpuPercent:F0}%",
        };
    }

    public static string FormatDetailed(string metricId, SystemMetricsSnapshot s)
    {
        return metricId?.ToUpperInvariant() switch
        {
            "CPU" => $"{s.CpuPercent:F1}%",
            "CPU_FREQ" => s.CpuFreqGhz is { } f ? $"{f:F2} GHz" : "--",
            "CPU_TEMP" => s.CpuTempC is { } ct ? $"{ct} °C" : (OperatingSystem.IsWindows() ? "需开启 HWiNFO" : "未支持"),
            "RAM" or "MEM" => $"{s.RamPercent:F0}% ({s.MemUsedGb:F1} / {s.MemTotalGb:F1} GB)",
            "MEM_USED" => $"{s.MemUsedGb:F1} / {s.MemTotalGb:F1} GB",
            "GPU" => s.GpuPercent is { } g ? $"{g:F0}%" : "无读数",
            "GPU_TEMP" => s.GpuTempC is { } t ? $"{t} °C" : (OperatingSystem.IsWindows() ? "需 NVML/HWiNFO" : "未支持"),
            "VRAM" => s.VramUsedGb is { } vu
                ? (s.VramTotalGb is { } vt && vt > 0 ? $"{vu:F1} / {vt:F1} GB" : $"{vu:F1} GB")
                : "无读数",
            "NET_DOWN" => FormatFullSpeed(s.NetDownBytesPerSec),
            "NET_UP" => FormatFullSpeed(s.NetUpBytesPerSec),
            "DISK" => $"{s.DiskActivePercent:F0}%",
            "DISK_SPACE" => $"{s.DiskSpaceUsedPercent:F0}% (余 {s.DiskFreeGb:F0} GB)",
            "UPTIME" => s.Uptime.TotalDays >= 1
                ? $"{(int)s.Uptime.TotalDays} 天 {s.Uptime.Hours} 小时"
                : $"{(int)s.Uptime.TotalHours:D2} 时 {s.Uptime.Minutes:D2} 分",
            _ => "--",
        };
    }

    public static double? GetPercentage(string metricId, SystemMetricsSnapshot s)
    {
        return metricId?.ToUpperInvariant() switch
        {
            "CPU" => s.CpuPercent,
            "RAM" or "MEM" or "MEM_USED" => s.RamPercent,
            "GPU" => s.GpuPercent,
            "VRAM" => s.VramUsedGb is { } u && s.VramTotalGb is { } t && t > 0 ? Math.Clamp(u * 100.0 / t, 0, 100) : null,
            "DISK" => s.DiskActivePercent,
            "DISK_SPACE" => s.DiskSpaceUsedPercent,
            _ => null,
        };
    }

    internal static string FormatCompactSpeed(double bps)
    {
        if (bps >= 1024 * 1024 * 1024) return $"{bps / (1024 * 1024 * 1024):F1}G";
        if (bps >= 100 * 1024 * 1024) return $"{bps / (1024 * 1024):F0}M";
        if (bps >= 1024 * 1024) return $"{bps / (1024 * 1024):F1}M";
        if (bps >= 1024) return $"{bps / 1024:F0}K";
        return $"{Math.Max(0, bps):F0}B";
    }

    internal static string FormatFullSpeed(double bps)
    {
        if (bps >= 1024 * 1024 * 1024) return $"{bps / (1024 * 1024 * 1024):F2} GB/s";
        if (bps >= 1024 * 1024) return $"{bps / (1024 * 1024):F2} MB/s";
        if (bps >= 1024) return $"{bps / 1024:F1} KB/s";
        return $"{Math.Max(0, bps):F0} B/s";
    }

    private static double SampleCpu()
    {
        double cpu = 0;
        if (GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            if (_lastUser > 0 || _lastKernel > 0)
            {
                ulong total = (kernel - _lastKernel) + (user - _lastUser);
                ulong busy = total - (idle - _lastIdle);
                if (total > 0) cpu = 100.0 * busy / total;
            }
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
        }
        return Math.Clamp(cpu, 0, 100);
    }

    private static double SampleMacCpu()
    {
        if (OperatingSystem.IsMacOS() && TrySampleMacHostCpu(out double nativePct))
            return nativePct;

        string load = RunCommand("/usr/sbin/sysctl", ["-n", "vm.loadavg"]);
        return ParseMacLoadAvgPercent(load, Environment.ProcessorCount);
    }

    public static double ParseMacLoadAvgPercent(string loadOutput, int processorCount)
    {
        if (string.IsNullOrWhiteSpace(loadOutput)) return 0;
        string cleaned = loadOutput.Replace("{", " ").Replace("}", " ");
        string[] parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || ParseDouble(parts[0]) is not { } oneMin)
            return 0;
        int cores = Math.Max(1, processorCount);
        return Math.Clamp(oneMin / cores * 100.0, 0, 100);
    }

    private static bool TrySampleMacHostCpu(out double percent)
    {
        percent = 0;
        try
        {
            var info = new uint[4];
            int count = 4;
            if (host_statistics(mach_host_self(), 3 /* HOST_CPU_LOAD_INFO */, info, ref count) != 0)
                return false;

            ulong user = info[0], sys = info[1], idle = info[2], nice = info[3];
            if (_hasMacCpuBaseline)
            {
                ulong dUser = user >= _lastMacCpuUser ? user - _lastMacCpuUser : 0;
                ulong dSys = sys >= _lastMacCpuSys ? sys - _lastMacCpuSys : 0;
                ulong dIdle = idle >= _lastMacCpuIdle ? idle - _lastMacCpuIdle : 0;
                ulong dNice = nice >= _lastMacCpuNice ? nice - _lastMacCpuNice : 0;
                ulong total = dUser + dSys + dIdle + dNice;
                if (total > 0)
                    percent = Math.Clamp(100.0 * (dUser + dSys + dNice) / total, 0, 100);
            }
            _lastMacCpuUser = user;
            _lastMacCpuSys = sys;
            _lastMacCpuIdle = idle;
            _lastMacCpuNice = nice;
            _hasMacCpuBaseline = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (double? GpuPct, double? VramUsedGb) SampleMacGpuThrottled(long nowMs)
    {
        if (_lastMacGpuSampleMs != 0 && nowMs - _lastMacGpuSampleMs < 2000)
            return (_cachedMacGpuPct, _cachedMacVramUsedGb);
        _lastMacGpuSampleMs = nowMs;

        try
        {
            string outText = RunCommand("/usr/sbin/ioreg", ["-r", "-d", "1", "-w", "0", "-c", "IOAccelerator"]);
            if (!string.IsNullOrWhiteSpace(outText))
            {
                var utilMatch = Regex.Match(outText, @"""Device Utilization %""\s*=\s*(\d+)");
                if (utilMatch.Success && double.TryParse(utilMatch.Groups[1].Value, out double u))
                    _cachedMacGpuPct = Math.Clamp(u, 0, 100);

                var memMatch = Regex.Match(outText, @"""In use system memory""\s*=\s*(\d+)");
                if (memMatch.Success && double.TryParse(memMatch.Groups[1].Value, out double bytes) && bytes > 0)
                    _cachedMacVramUsedGb = bytes / Gb;
            }
        }
        catch { }
        return (_cachedMacGpuPct, _cachedMacVramUsedGb);
    }

    private static double? SampleMacIntelCpuFreqThrottled(long nowMs)
    {
        if (_lastMacCpuFreqSampleMs != 0 && nowMs - _lastMacCpuFreqSampleMs < 2000)
            return _cachedMacCpuFreqGhz;
        _lastMacCpuFreqSampleMs = nowMs;

        string hz = RunCommand("/usr/sbin/sysctl", ["-n", "hw.cpufrequency"]);
        if (ParseDouble(hz) is { } frequency && frequency > 0)
            _cachedMacCpuFreqGhz = Math.Round(frequency / 1000000000.0, 2);
        return _cachedMacCpuFreqGhz;
    }

    private static double? SampleMacArm64CpuFreqThrottled(long nowMs)
    {
        if (_lastMacCpuFreqSampleMs != 0 && nowMs - _lastMacCpuFreqSampleMs < 2000)
            return _cachedMacCpuFreqGhz;
        _lastMacCpuFreqSampleMs = nowMs;

        try
        {
            EnsureMacDvfsTables();
            if (_macPCoreFreqsGhz.Length == 0 && _macECoreFreqsGhz.Length == 0)
                return _cachedMacCpuFreqGhz;

            if (!_macIoReportInitAttempted)
            {
                _macIoReportInitAttempted = true;
                IntPtr group = CreateCfStr("CPU Stats");
                IntPtr subGroup = CreateCfStr("CPU Core Performance States");
                _cfStrIoReportChannels = CreateCfStr("IOReportChannels");
                try
                {
                    IntPtr channels = IOReportCopyChannelsInGroup(group, subGroup, 0, 0, 0);
                    if (channels != IntPtr.Zero)
                    {
                        try
                        {
                            _macIoReportSub = IOReportCreateSubscription(IntPtr.Zero, channels, out _macIoReportSubCh, 0, IntPtr.Zero);
                        }
                        finally
                        {
                            CFRelease(channels);
                        }
                    }
                }
                finally
                {
                    if (group != IntPtr.Zero) CFRelease(group);
                    if (subGroup != IntPtr.Zero) CFRelease(subGroup);
                }
            }

            if (_macIoReportSub == IntPtr.Zero || _macIoReportSubCh == IntPtr.Zero || _cfStrIoReportChannels == IntPtr.Zero)
                return _cachedMacCpuFreqGhz;

            if (_prevMacIoReportSample == IntPtr.Zero)
            {
                _prevMacIoReportSample = IOReportCreateSamples(_macIoReportSub, _macIoReportSubCh, IntPtr.Zero);
                if (_prevMacIoReportSample == IntPtr.Zero)
                    return _cachedMacCpuFreqGhz;
                Thread.Sleep(60);
            }

            IntPtr currentSample = IOReportCreateSamples(_macIoReportSub, _macIoReportSubCh, IntPtr.Zero);
            if (currentSample == IntPtr.Zero)
                return _cachedMacCpuFreqGhz;

            IntPtr delta = IntPtr.Zero;
            try
            {
                delta = IOReportCreateSamplesDelta(_prevMacIoReportSample, currentSample, IntPtr.Zero);
                CFRelease(_prevMacIoReportSample);
                _prevMacIoReportSample = currentSample;
                currentSample = IntPtr.Zero;

                if (delta == IntPtr.Zero)
                    return _cachedMacCpuFreqGhz;

                IntPtr arr = CFDictionaryGetValue(delta, _cfStrIoReportChannels);
                if (arr == IntPtr.Zero)
                    return _cachedMacCpuFreqGhz;

                long chCount = CFArrayGetCount(arr);
                double pWeightedSum = 0, pResidencySum = 0;
                double allWeightedSum = 0, allResidencySum = 0;

                for (long i = 0; i < chCount; i++)
                {
                    IntPtr item = CFArrayGetValueAtIndex(arr, i);
                    string cname = ReadCfStr(IOReportChannelGetChannelName(item));
                    bool isP = cname.StartsWith("PCPU", StringComparison.Ordinal);
                    bool isE = cname.StartsWith("ECPU", StringComparison.Ordinal);
                    if (!isP && !isE) continue;

                    double[] table = isP ? _macPCoreFreqsGhz : _macECoreFreqsGhz;
                    if (table.Length == 0) continue;

                    int stateCount = IOReportStateGetCount(item);
                    var activeResidencies = new List<long>(stateCount);
                    for (int s = 0; s < stateCount; s++)
                    {
                        string sname = ReadCfStr(IOReportStateGetNameForIndex(item, s));
                        if (sname is "IDLE" or "DOWN" or "OFF") continue;
                        activeResidencies.Add(Math.Max(0L, IOReportStateGetResidency(item, s)));
                    }

                    if (activeResidencies.Count == 0) continue;
                    int offset = Math.Max(0, table.Length - activeResidencies.Count);
                    double coreWeighted = 0, coreRes = 0;
                    for (int idx = 0; idx < activeResidencies.Count; idx++)
                    {
                        long r = activeResidencies[idx];
                        if (r <= 0) continue;
                        double ghz = table[Math.Min(table.Length - 1, offset + idx)];
                        coreWeighted += ghz * r;
                        coreRes += r;
                    }
                    if (coreRes <= 0) continue;

                    allWeightedSum += coreWeighted;
                    allResidencySum += coreRes;
                    if (isP)
                    {
                        pWeightedSum += coreWeighted;
                        pResidencySum += coreRes;
                    }
                }

                if (pResidencySum > 0)
                    _cachedMacCpuFreqGhz = Math.Round(pWeightedSum / pResidencySum, 2);
                else if (allResidencySum > 0)
                    _cachedMacCpuFreqGhz = Math.Round(allWeightedSum / allResidencySum, 2);
            }
            finally
            {
                if (delta != IntPtr.Zero) CFRelease(delta);
                if (currentSample != IntPtr.Zero) CFRelease(currentSample);
            }
        }
        catch { }

        return _cachedMacCpuFreqGhz;
    }

    private static void EnsureMacDvfsTables()
    {
        if (_macDvfsProbed) return;
        _macDvfsProbed = true;

        string outText = RunCommand("/usr/sbin/ioreg", ["-r", "-d", "1", "-w", "0", "-c", "AppleARMIODevice"]);
        if (string.IsNullOrWhiteSpace(outText)) return;

        _macPCoreFreqsGhz = ParseDvfsFreqsGhz(outText, "voltage-states5-sram");
        _macECoreFreqsGhz = ParseDvfsFreqsGhz(outText, "voltage-states1-sram");
    }

    private static double[] ParseDvfsFreqsGhz(string ioregText, string keyName)
    {
        var match = Regex.Match(ioregText, $@"""{Regex.Escape(keyName)}""\s*=\s*<([0-9a-fA-F]+)>");
        if (!match.Success) return [];

        string hex = match.Groups[1].Value;
        int pairs = hex.Length / 16;
        if (pairs <= 0) return [];

        var freqs = new double[pairs];
        for (int i = 0; i < pairs; i++)
        {
            uint hz = uint.Parse(hex.Substring(i * 8 * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    | (uint.Parse(hex.Substring(i * 8 * 2 + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 8)
                    | (uint.Parse(hex.Substring(i * 8 * 2 + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 16)
                    | (uint.Parse(hex.Substring(i * 8 * 2 + 6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 24);
            freqs[i] = hz / 1_000_000_000.0;
        }
        return freqs;
    }

    private static (int? CpuTempC, int? GpuTempC) SampleMacTempsThrottled(long nowMs)
    {
        if (_lastMacGpuTempSampleMs != 0 && nowMs - _lastMacGpuTempSampleMs < 2000)
            return (_cachedMacCpuTempC, _cachedMacGpuTempC);
        _lastMacGpuTempSampleMs = nowMs;

        IntPtr client = IntPtr.Zero;
        IntPtr kPage = IntPtr.Zero, kUsage = IntPtr.Zero;
        IntPtr vPage = IntPtr.Zero, vUsage = IntPtr.Zero;
        IntPtr matching = IntPtr.Zero, services = IntPtr.Zero, prodKey = IntPtr.Zero;
        try
        {
            client = IOHIDEventSystemClientCreate(IntPtr.Zero);
            if (client == IntPtr.Zero) return (_cachedMacCpuTempC, _cachedMacGpuTempC);

            kPage = CreateCfStr("PrimaryUsagePage");
            kUsage = CreateCfStr("PrimaryUsage");
            int pageVal = 0xFF00;
            int usageVal = 5;
            vPage = CFNumberCreate(IntPtr.Zero, 3 /* kCFNumberSInt32Type */, ref pageVal);
            vUsage = CFNumberCreate(IntPtr.Zero, 3 /* kCFNumberSInt32Type */, ref usageVal);

            IntPtr[] keys = [kPage, kUsage];
            IntPtr[] vals = [vPage, vUsage];
            matching = CFDictionaryCreate(IntPtr.Zero, keys, vals, 2, IntPtr.Zero, IntPtr.Zero);
            IOHIDEventSystemClientSetMatching(client, matching);

            services = IOHIDEventSystemClientCopyServices(client);
            if (services == IntPtr.Zero) return (_cachedMacCpuTempC, _cachedMacGpuTempC);

            prodKey = CreateCfStr("Product");
            long count = CFArrayGetCount(services);
            double cpuSum = 0; int cpuCount = 0;
            double gpuSum = 0; int gpuCount = 0;
            double socSum = 0; int socCount = 0;

            for (long i = 0; i < count; i++)
            {
                IntPtr svc = CFArrayGetValueAtIndex(services, i);
                if (svc == IntPtr.Zero) continue;

                IntPtr propName = IOHIDServiceClientCopyProperty(svc, prodKey);
                string name;
                try { name = ReadCfStr(propName); }
                finally { if (propName != IntPtr.Zero) CFRelease(propName); }

                if (string.IsNullOrEmpty(name)) continue;

                IntPtr ev = IOHIDServiceClientCopyEvent(svc, 15 /* kIOHIDEventTypeTemperature */, 0, 0);
                if (ev == IntPtr.Zero) continue;
                double temp;
                try { temp = IOHIDEventGetFloatValue(ev, 15u << 16); }
                finally { CFRelease(ev); }

                if (double.IsNaN(temp) || temp <= 5.0 || temp >= 130.0) continue;

                string lower = name.ToLowerInvariant();
                if (lower.Contains("gpu", StringComparison.Ordinal) || Regex.IsMatch(lower, @"\btp\d+g\b"))
                {
                    gpuSum += temp;
                    gpuCount++;
                }
                else if (lower.Contains("pacc", StringComparison.Ordinal)
                      || lower.Contains("eacc", StringComparison.Ordinal)
                      || lower.Contains("cpu", StringComparison.Ordinal)
                      || Regex.IsMatch(lower, @"\btp\d+c\b"))
                {
                    cpuSum += temp;
                    cpuCount++;
                }
                else if (lower.Contains("tdie", StringComparison.Ordinal) || lower.Contains("soc", StringComparison.Ordinal))
                {
                    socSum += temp;
                    socCount++;
                }
            }

            if (cpuCount > 0)
                _cachedMacCpuTempC = (int)Math.Round(cpuSum / cpuCount);
            else if (socCount > 0)
                _cachedMacCpuTempC = (int)Math.Round(socSum / socCount);

            if (gpuCount > 0)
                _cachedMacGpuTempC = (int)Math.Round(gpuSum / gpuCount);
            else if (socCount > 0)
                _cachedMacGpuTempC = (int)Math.Round(socSum / socCount);
        }
        catch { }
        finally
        {
            if (prodKey != IntPtr.Zero) CFRelease(prodKey);
            if (services != IntPtr.Zero) CFRelease(services);
            if (matching != IntPtr.Zero) CFRelease(matching);
            if (vPage != IntPtr.Zero) CFRelease(vPage);
            if (vUsage != IntPtr.Zero) CFRelease(vUsage);
            if (kPage != IntPtr.Zero) CFRelease(kPage);
            if (kUsage != IntPtr.Zero) CFRelease(kUsage);
            if (client != IntPtr.Zero) CFRelease(client);
        }
        return (_cachedMacCpuTempC, _cachedMacGpuTempC);
    }

    private static IntPtr CreateCfStr(string s)
        => CFStringCreateWithCString(IntPtr.Zero, s ?? string.Empty, 0x08000100 /* kCFStringEncodingUTF8 */);

    private static string ReadCfStr(IntPtr cfStr)
    {
        if (cfStr == IntPtr.Zero) return "";
        byte[] buf = new byte[256];
        return CFStringGetCString(cfStr, buf, buf.Length, 0x08000100)
            ? System.Text.Encoding.UTF8.GetString(buf, 0, Array.IndexOf(buf, (byte)0) is int idx and >= 0 ? idx : buf.Length)
            : "";
    }

    private static (double RamPct, double UsedGb, double TotalGb) SampleMemory()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        double totalGb = mem.ullTotalPhys / Gb;
        double usedGb = (mem.ullTotalPhys > mem.ullAvailPhys ? mem.ullTotalPhys - mem.ullAvailPhys : 0) / Gb;
        double pct = mem.ullTotalPhys > 0 ? Math.Clamp(usedGb * 100.0 / totalGb, 0, 100) : 0;
        return (pct, usedGb, totalGb);
    }

    private static (double RamPct, double UsedGb, double TotalGb) SampleMacMemory()
    {
        if (TrySampleMacMemoryNative(out double usedGb, out double totalGb))
        {
            double pct = totalGb > 0 ? Math.Clamp(usedGb * 100.0 / totalGb, 0, 100) : 0;
            return (pct, usedGb, totalGb);
        }
        return SampleMacMemoryViaVmStat();
    }

    private static bool TrySampleMacMemoryNative(out double usedGb, out double totalGb)
    {
        usedGb = 0;
        totalGb = 0;
        try
        {
            if (_macTotalMemBytes <= 0)
            {
                nint len8 = sizeof(long);
                if (sysctlbyname("hw.memsize", out long totalBytes, ref len8, IntPtr.Zero, 0) == 0 && totalBytes > 0)
                    _macTotalMemBytes = totalBytes;
            }
            if (_macPageSize <= 0)
            {
                nint len4 = sizeof(int);
                if (sysctlbyname("hw.pagesize", out int ps, ref len4, IntPtr.Zero, 0) == 0 && ps > 0)
                    _macPageSize = ps;
            }
            if (_macTotalMemBytes <= 0)
                return false;

            int pageSize = _macPageSize > 0 ? _macPageSize : Environment.SystemPageSize;
            uint[] vm = new uint[104]; // sizeof(vm_statistics64) == 416 bytes
            int count = vm.Length;
            const int HOST_VM_INFO64 = 4;
            if (host_statistics64(mach_host_self(), HOST_VM_INFO64, vm, ref count) != 0 || count < 36)
                return false;

            // 与 macOS 活动监视器一致：App(internal - purgeable) + Wired(wire) + Compressed(compressor)
            long wire = vm[3];
            long purgeable = vm[22];
            long compressor = vm[32];
            long internalPages = vm[35];

            long appPages = Math.Max(0L, internalPages - purgeable);
            long usedPages = appPages + wire + compressor;
            if (usedPages <= 0)
            {
                long free = vm[0];
                long inactive = vm[2];
                long totalPages = _macTotalMemBytes / Math.Max(1, pageSize);
                usedPages = Math.Max(0L, totalPages - free - inactive);
            }

            totalGb = _macTotalMemBytes / Gb;
            usedGb = usedPages * (double)pageSize / Gb;
            return totalGb > 0;
        }
        catch
        {
            return false;
        }
    }

    private static (double RamPct, double UsedGb, double TotalGb) SampleMacMemoryViaVmStat()
    {
        try
        {
            string stat = RunCommand("/usr/bin/vm_stat", []);
            if (string.IsNullOrWhiteSpace(stat)) return (0, 0, 0);

            double pageSize = 16384;
            var sizeMatch = Regex.Match(stat, @"page size of (\d+) bytes", RegexOptions.IgnoreCase);
            if (sizeMatch.Success && long.TryParse(sizeMatch.Groups[1].Value, out long ps) && ps > 0)
                pageSize = ps;

            long active = ParseVmCount(stat, "Pages active:");
            long wired = ParseVmCount(stat, "Pages wired down:");
            long compressed = ParseVmCount(stat, "Pages occupied by compressor:");
            long free = ParseVmCount(stat, "Pages free:");
            long inactive = ParseVmCount(stat, "Pages inactive:");
            long speculative = ParseVmCount(stat, "Pages speculative:");

            double totalBytes = ParseDouble(RunCommand("/usr/sbin/sysctl", ["-n", "hw.memsize"])) ?? 0;
            double totalPages = totalBytes > 0 ? totalBytes / pageSize : 0;
            double usedPages = active + wired + compressed;
            if (usedPages <= 0)
                usedPages = Math.Max(0, totalPages - free - inactive - speculative);

            double totalGb = totalBytes / Gb;
            double usedGb = usedPages * pageSize / Gb;
            double pct = totalGb > 0 ? Math.Clamp(usedGb * 100.0 / totalGb, 0, 100) : 0;
            return (pct, usedGb, totalGb);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private static (double DownBps, double UpBps) SampleNetwork(long nowMs)
    {
        try
        {
            ulong rx = 0, tx = 0;
            if (OperatingSystem.IsWindows())
            {
                if (!TryReadWinIfCounters64(out rx, out tx))
                    (rx, tx) = ReadWinIfCounters32();
            }
            else
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    var st = ni.GetIPv4Statistics();
                    rx += (ulong)Math.Max(0, st.BytesReceived);
                    tx += (ulong)Math.Max(0, st.BytesSent);
                }
            }

            double down = 0, up = 0;
            if (_lastNetTick > 0 && nowMs > _lastNetTick)
            {
                double sec = (nowMs - _lastNetTick) / 1000.0;
                if (rx >= _lastNetRx) down = (rx - _lastNetRx) / sec;
                if (tx >= _lastNetTx) up = (tx - _lastNetTx) / sec;
            }
            _lastNetRx = rx;
            _lastNetTx = tx;
            _lastNetTick = nowMs;
            return (down, up);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static bool TryReadWinIfCounters64(out ulong rx, out ulong tx)
    {
        rx = 0;
        tx = 0;
        const int MibIfRow2Size = 1352;
        const int OffsetType = 1128;
        const int OffsetFlags = 1152;
        const int OffsetOperStatus = 1156;
        const int OffsetInOctets = 1208;
        const int OffsetOutOctets = 1280;

        if (GetIfTable2(out IntPtr tablePtr) != 0 || tablePtr == IntPtr.Zero)
            return false;

        try
        {
            int count = Marshal.ReadInt32(tablePtr);
            IntPtr basePtr = tablePtr + 8;
            for (int i = 0; i < count && i < 1024; i++)
            {
                IntPtr row = basePtr + i * MibIfRow2Size;
                uint ifType = unchecked((uint)Marshal.ReadInt32(row, OffsetType));
                byte flags = Marshal.ReadByte(row, OffsetFlags);
                int operStatus = Marshal.ReadInt32(row, OffsetOperStatus);
                if (operStatus != 1 /* IfOperStatusUp */ || ifType == 24 /* Loopback */ || ifType == 131 /* Tunnel */)
                    continue;
                if ((flags & 0x01 /* HardwareInterface */) == 0)
                    continue;

                rx += unchecked((ulong)Marshal.ReadInt64(row, OffsetInOctets));
                tx += unchecked((ulong)Marshal.ReadInt64(row, OffsetOutOctets));
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            FreeMibTable(tablePtr);
        }
    }

    private static (ulong Rx, ulong Tx) ReadWinIfCounters32()
    {
        uint size = 0;
        GetIfTable(IntPtr.Zero, ref size, false);
        if (size == 0 || size > 16 * 1024 * 1024) return (0, 0);

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetIfTable(buf, ref size, false) != 0) return (0, 0);
            int count = Marshal.ReadInt32(buf);
            int stride = Marshal.SizeOf<MIB_IFROW>();
            ulong rx = 0, tx = 0;
            for (int i = 0; i < count && i < 512; i++)
            {
                var row = Marshal.PtrToStructure<MIB_IFROW>(buf + 4 + i * stride);
                if (row.dwType == 24 /* MIB_IF_TYPE_LOOPBACK */) continue;
                rx += row.dwInOctets;
                tx += row.dwOutOctets;
            }
            return (rx, tx);
        }
        catch
        {
            return (0, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static (string DriveLabel, double UsedPct, double FreeGb) SampleDiskSpace()
    {
        try
        {
            string root = OperatingSystem.IsWindows()
                ? (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\")
                : "/";
            var di = new DriveInfo(root);
            double total = di.TotalSize;
            double free = di.AvailableFreeSpace;
            double usedPct = total > 0 ? Math.Clamp((total - free) * 100.0 / total, 0, 100) : 0;
            string label = root == "/" ? "/" : root.TrimEnd('\\', '/');
            return (string.IsNullOrEmpty(label) ? "/" : label, usedPct, free / Gb);
        }
        catch
        {
            return (OperatingSystem.IsWindows() ? "C:" : "/", 0, 0);
        }
    }

    private static void EnsureBaseCpuFreq()
    {
        if (_baseCpuGhz > 0 || !OperatingSystem.IsWindows()) return;
        IntPtr hKey = IntPtr.Zero;
        try
        {
            if (RegOpenKeyExW(HKeyLocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", 0, 0x00020019, out hKey) != 0)
                return;
            uint size = 4;
            byte[] buf = new byte[4];
            if (RegQueryValueExW(hKey, "~MHz", IntPtr.Zero, out uint type, buf, ref size) == 0 && type == 4 && size >= 4)
            {
                int mhz = BitConverter.ToInt32(buf, 0);
                if (mhz > 0)
                    _baseCpuGhz = Math.Round(mhz / 1000.0, 2);
            }
        }
        catch { }
        finally
        {
            if (hKey != IntPtr.Zero)
                RegCloseKey(hKey);
        }
    }

    private static void EnsurePdhCounters(MetricScope scope)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!_pdhQueryOpened)
            {
                _pdhQueryOpened = true;
                if (PdhOpenQueryW(null, IntPtr.Zero, out _pdhQuery) != 0 || _pdhQuery == IntPtr.Zero)
                {
                    _pdhQuery = IntPtr.Zero;
                    return;
                }
            }
            if (_pdhQuery == IntPtr.Zero) return;

            bool addedAny = false;
            if (scope.HasFlag(MetricScope.CpuFreq) && !_pdhAddedFlags.HasFlag(MetricScope.CpuFreq))
            {
                _pdhAddedFlags |= MetricScope.CpuFreq;
                PdhAddEnglishCounterW(_pdhQuery, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _pdhCpuPerf);
                addedAny = true;
            }
            if (scope.HasFlag(MetricScope.DiskActive) && !_pdhAddedFlags.HasFlag(MetricScope.DiskActive))
            {
                _pdhAddedFlags |= MetricScope.DiskActive;
                PdhAddEnglishCounterW(_pdhQuery, @"\PhysicalDisk(_Total)\% Disk Time", IntPtr.Zero, out _pdhDiskTime);
                addedAny = true;
            }
            if (scope.HasFlag(MetricScope.Gpu) && !_pdhAddedFlags.HasFlag(MetricScope.Gpu))
            {
                _pdhAddedFlags |= MetricScope.Gpu;
                PdhAddEnglishCounterW(_pdhQuery, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _pdhGpu3d);
                addedAny = true;
            }
            if (scope.HasFlag(MetricScope.Vram) && !_pdhAddedFlags.HasFlag(MetricScope.Vram))
            {
                _pdhAddedFlags |= MetricScope.Vram;
                PdhAddEnglishCounterW(_pdhQuery, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _pdhGpuVram);
                PdhAddEnglishCounterW(_pdhQuery, @"\GPU Adapter Memory(*)\Shared Usage", IntPtr.Zero, out _pdhGpuSharedVram);
                addedAny = true;
            }

            if (addedAny)
            {
                PdhCollectQueryData(_pdhQuery);
                _pdhPrimed = false;
            }
        }
        catch
        {
            _pdhQuery = IntPtr.Zero;
        }
    }

    private static double? ReadPdhDouble(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        try
        {
            if (PdhGetFormattedCounterValue(counter, 0x00000200, out _, out var val) == 0 && val.CStatus <= 1)
                return val.DoubleValue;
        }
        catch { }
        return null;
    }

    private static readonly Regex LuidRegex = new(@"luid_0x[0-9a-fA-F]+_0x[0-9a-fA-F]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static Dictionary<string, double> SumPdhByLuid(IntPtr counter)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (counter == IntPtr.Zero) return map;
        try
        {
            uint bufSize = 0, itemCount = 0;
            uint st = PdhGetFormattedCounterArrayW(counter, 0x00000200, ref bufSize, ref itemCount, IntPtr.Zero);
            if (st != 0x800007D2 || bufSize == 0 || itemCount == 0 || itemCount > 1024)
                return map;

            IntPtr mem = Marshal.AllocHGlobal((int)bufSize);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, 0x00000200, ref bufSize, ref itemCount, mem) != 0)
                    return map;
                int stride = Marshal.SizeOf<PdhFmtCounterValueItem>();
                for (int i = 0; i < itemCount; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(mem + i * stride);
                    if (item.Value.CStatus > 1 || item.Value.DoubleValue < 0)
                        continue;

                    string inst = Marshal.PtrToStringUni(item.Name) ?? "";
                    var m = LuidRegex.Match(inst);
                    string key = m.Success ? m.Value.ToLowerInvariant() : "__default__";
                    map[key] = map.TryGetValue(key, out double cur) ? cur + item.Value.DoubleValue : item.Value.DoubleValue;
                }
                return map;
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }
        catch
        {
            return map;
        }
    }

    /// <summary>按 LUID 隔离选择主显卡：默认优先独显；混合本独显空闲而集显高负载时切换为活跃显卡。</summary>
    public static (double? GpuPct, double? VramUsedGb, double? VramTotalGb, string? GpuName) SelectPrimaryGpuByLuid(
        IReadOnlyDictionary<string, double> gpu3dByLuid,
        IReadOnlyDictionary<string, double> dedicatedVramByLuid,
        IReadOnlyDictionary<string, double> sharedVramByLuid,
        IReadOnlyList<GpuAdapterInfo> adapters)
    {
        if (adapters.Count == 0)
        {
            string? fallbackKey = dedicatedVramByLuid
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .FirstOrDefault()
                ?? gpu3dByLuid.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();

            if (fallbackKey == null)
                return (null, null, null, null);

            double? fGpu = gpu3dByLuid.TryGetValue(fallbackKey, out double g) ? Math.Clamp(g, 0, 100) : null;
            double? fVram = dedicatedVramByLuid.TryGetValue(fallbackKey, out double dv) && dv > 0 ? dv / Gb : null;
            return (fGpu, fVram, null, null);
        }

        GpuAdapterInfo selected = adapters[0];
        if (adapters.Count > 1)
        {
            double selectedGpu = gpu3dByLuid.TryGetValue(selected.LuidKey, out double g0) ? g0 : 0;
            if (selectedGpu < 1.0)
            {
                var busyAlt = adapters
                    .Skip(1)
                    .Select(a => (Adapter: a, Load: gpu3dByLuid.TryGetValue(a.LuidKey, out double g) ? g : 0))
                    .Where(x => x.Load >= 5.0)
                    .OrderByDescending(x => x.Load)
                    .FirstOrDefault();
                if (busyAlt.Adapter != null)
                    selected = busyAlt.Adapter;
            }
        }

        double? gpuPct = gpu3dByLuid.TryGetValue(selected.LuidKey, out double load)
            ? Math.Clamp(load, 0, 100)
            : (gpu3dByLuid.Count > 0 ? 0.0 : null);

        double? vramUsed = null;
        double? vramTotal = null;
        bool hasDed = dedicatedVramByLuid.TryGetValue(selected.LuidKey, out double dedBytes);
        bool hasShared = sharedVramByLuid.TryGetValue(selected.LuidKey, out double shBytes);

        if (selected.IsDiscrete || selected.DedicatedVramGb >= 0.5)
        {
            if (hasDed) vramUsed = Math.Max(0, dedBytes / Gb);
            if (selected.DedicatedVramGb > 0.05) vramTotal = selected.DedicatedVramGb;
        }
        else
        {
            double dedGb = hasDed ? dedBytes / Gb : 0;
            if (dedGb >= 0.05 && selected.DedicatedVramGb >= 0.1)
            {
                vramUsed = dedGb;
                vramTotal = selected.DedicatedVramGb;
            }
            else if (hasShared)
            {
                vramUsed = Math.Max(0, shBytes / Gb);
                if (selected.SharedMemGb > 0.1) vramTotal = selected.SharedMemGb;
            }
        }

        return (gpuPct, vramUsed, vramTotal, selected.Name);
    }

    private static void EnsureDxgiAdapters()
    {
        if (_dxgiProbed) return;
        _dxgiProbed = true;
        if (!OperatingSystem.IsWindows()) return;

        IntPtr factory = IntPtr.Zero;
        try
        {
            Guid iidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(ref iidFactory1, out factory) != 0 || factory == IntPtr.Zero)
                return;

            var found = new List<GpuAdapterInfo>();
            var enumAdapters1 = GetComMethod<DxgiEnumAdapters1>(factory, 12);

            for (uint i = 0; i < 16; i++)
            {
                if (enumAdapters1(factory, i, out IntPtr adapter) != 0 || adapter == IntPtr.Zero)
                    break;
                try
                {
                    var getDesc1 = GetComMethod<DxgiGetDesc1>(adapter, 10);
                    if (getDesc1(adapter, out var desc) != 0)
                        continue;
                    if ((desc.Flags & 2u /* DXGI_ADAPTER_FLAG_SOFTWARE */) != 0 || desc.VendorId == 0x1414)
                        continue;

                    string name = (desc.Description ?? "").Trim();
                    double dedGb = desc.DedicatedVideoMemory / Gb;
                    double sharedGb = desc.SharedSystemMemory / Gb;
                    bool isDiscrete = ClassifyDiscreteGpu(name, desc.VendorId, dedGb);
                    string luid = $"luid_0x{unchecked((uint)desc.AdapterLuidHighPart):x8}_0x{desc.AdapterLuidLowPart:x8}";
                    found.Add(new GpuAdapterInfo(name, luid, desc.VendorId, dedGb, sharedGb, isDiscrete));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }

            _dxgiAdapters = found
                .OrderByDescending(a => a.IsDiscrete)
                .ThenByDescending(a => a.DedicatedVramGb)
                .ToList();
        }
        catch
        {
            _dxgiAdapters = [];
        }
        finally
        {
            if (factory != IntPtr.Zero)
                Marshal.Release(factory);
        }
    }

    private static bool ClassifyDiscreteGpu(string name, uint vendorId, double dedicatedVramGb)
    {
        bool looksIntegrated = name.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase)
            || name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Iris", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Vega", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Integrated", StringComparison.OrdinalIgnoreCase);
        bool looksDiscrete = name.Contains("RTX", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GTX", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)
            || name.Contains("RX ", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Arc A", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Arc B", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase);

        if (looksDiscrete) return true;
        if (looksIntegrated) return false;
        if (vendorId == 0x10DE) return true;
        return dedicatedVramGb >= 1.5;
    }

    private static TDelegate GetComMethod<TDelegate>(IntPtr comPtr, int slot) where TDelegate : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(comPtr);
        IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(fn);
    }

    private static void EnsureNvml()
    {
        if (_nvmlProbed) return;
        _nvmlProbed = true;
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            IntPtr lib = IntPtr.Zero;
            if (!NativeLibrary.TryLoad("nvml.dll", out lib))
            {
                string alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "NVIDIA Corporation", "NVSMI", "nvml.dll");
                if (File.Exists(alt))
                    NativeLibrary.TryLoad(alt, out lib);
            }
            if (lib == IntPtr.Zero) return;

            if (!NativeLibrary.TryGetExport(lib, "nvmlInit_v2", out IntPtr pInit)) return;
            var init = Marshal.GetDelegateForFunctionPointer<NvmlInit>(pInit);
            if (init() != 0) return;

            if (!NativeLibrary.TryGetExport(lib, "nvmlDeviceGetHandleByIndex_v2", out IntPtr pGet)) return;
            var getDev = Marshal.GetDelegateForFunctionPointer<NvmlGetDevice>(pGet);
            if (getDev(0, out _nvmlDevice) != 0 || _nvmlDevice == IntPtr.Zero) return;

            if (NativeLibrary.TryGetExport(lib, "nvmlDeviceGetUtilizationRates", out IntPtr pUtil))
                _nvmlGetUtil = Marshal.GetDelegateForFunctionPointer<NvmlGetUtil>(pUtil);
            if (NativeLibrary.TryGetExport(lib, "nvmlDeviceGetTemperature", out IntPtr pTemp))
                _nvmlGetTemp = Marshal.GetDelegateForFunctionPointer<NvmlGetTemp>(pTemp);
            if (NativeLibrary.TryGetExport(lib, "nvmlDeviceGetMemoryInfo", out IntPtr pMem))
                _nvmlGetMem = Marshal.GetDelegateForFunctionPointer<NvmlGetMem>(pMem);

            _nvmlOk = _nvmlGetUtil != null || _nvmlGetTemp != null || _nvmlGetMem != null;
        }
        catch
        {
            _nvmlOk = false;
        }
    }

    private static string RunCommand(string fileName, IEnumerable<string> arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in arguments)
                psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            if (p == null) return "";
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(1200))
            {
                try { p.Kill(entireProcessTree: true); }
                catch { }
            }
            return output.Trim();
        }
        catch
        {
            return "";
        }
    }

    private static long ParseVmCount(string stat, string key)
    {
        foreach (string line in stat.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
            var m = Regex.Match(trimmed, @"(\d+)");
            if (m.Success && long.TryParse(m.Groups[1].Value, out long v)) return v;
        }
        return 0;
    }

    private static double? ParseDouble(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    private static readonly IntPtr HKeyLocalMachine = new(unchecked((int)0x80000002));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetDevice(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetUtil(IntPtr device, out NvmlUtilization util);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetTemp(IntPtr device, uint sensorType, out uint temp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetMem(IntPtr device, out NvmlMemory memory);

    [StructLayout(LayoutKind.Sequential)] private struct NvmlUtilization { public uint Gpu; public uint Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MIB_IFROW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string wszName;
        public uint dwIndex;
        public uint dwType;
        public uint dwMtu;
        public uint dwSpeed;
        public uint dwPhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] bPhysAddr;
        public uint dwAdminStatus;
        public uint dwOperStatus;
        public uint dwLastChange;
        public uint dwInOctets;
        public uint dwInUcastPkts;
        public uint dwInNUcastPkts;
        public uint dwInDiscards;
        public uint dwInErrors;
        public uint dwInUnknownProtos;
        public uint dwOutOctets;
        public uint dwOutUcastPkts;
        public uint dwOutNUcastPkts;
        public uint dwOutDiscards;
        public uint dwOutErrors;
        public uint dwOutQLen;
        public uint dwDescrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] bDescr;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DxgiEnumAdapters1(IntPtr factory, uint adapter, out IntPtr ppAdapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DxgiGetDesc1(IntPtr adapter, out DxgiAdapterDesc1 desc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLowPart;
        public int AdapterLuidHighPart;
        public uint Flags;
    }

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
    [DllImport("iphlpapi.dll")] private static extern int GetIfTable2(out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern uint GetIfTable(IntPtr pIfTable, ref uint pdwSize, bool bOrder);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved, out uint lpType, [Out] byte[]? lpData, ref uint lpcbData);
    [DllImport("advapi32.dll")] private static extern int RegCloseKey(IntPtr hKey);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern uint mach_host_self();
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern int host_statistics(uint hostPriv, int flavor, [Out] uint[] hostInfoOut, ref int hostInfoOutCnt);
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern int host_statistics64(uint hostPriv, int flavor, [Out] uint[] hostInfoOut, ref int hostInfoOutCnt);
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern int sysctlbyname([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out long oldp, ref nint oldlenp, IntPtr newp, nint newlen);
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern int sysctlbyname([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out int oldp, ref nint oldlenp, IntPtr newp, nint newlen);

    private const string CfLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string IoKitLib = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string IoReportLib = "/usr/lib/libIOReport.dylib";

    [DllImport(CfLib)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);
    [DllImport(CfLib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr theString, [Out] byte[] buffer, long bufferSize, uint encoding);
    [DllImport(CfLib)] private static extern IntPtr CFNumberCreate(IntPtr allocator, int theType, ref int valuePtr);
    [DllImport(CfLib)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, long numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);
    [DllImport(CfLib)] private static extern IntPtr CFDictionaryGetValue(IntPtr theDict, IntPtr key);
    [DllImport(CfLib)] private static extern long CFArrayGetCount(IntPtr theArray);
    [DllImport(CfLib)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr theArray, long idx);
    [DllImport(CfLib)] private static extern void CFRelease(IntPtr cf);

    [DllImport(IoKitLib)] private static extern IntPtr IOHIDEventSystemClientCreate(IntPtr allocator);
    [DllImport(IoKitLib)] private static extern int IOHIDEventSystemClientSetMatching(IntPtr client, IntPtr match);
    [DllImport(IoKitLib)] private static extern IntPtr IOHIDEventSystemClientCopyServices(IntPtr client);
    [DllImport(IoKitLib)] private static extern IntPtr IOHIDServiceClientCopyProperty(IntPtr service, IntPtr property);
    [DllImport(IoKitLib)] private static extern IntPtr IOHIDServiceClientCopyEvent(IntPtr service, long type, int options, long timeout);
    [DllImport(IoKitLib)] private static extern double IOHIDEventGetFloatValue(IntPtr @event, uint field);

    [DllImport(IoReportLib)] private static extern IntPtr IOReportCopyChannelsInGroup(IntPtr group, IntPtr subGroup, ulong a, ulong b, ulong c);
    [DllImport(IoReportLib)] private static extern IntPtr IOReportCreateSubscription(IntPtr allocator, IntPtr desiredChannels, out IntPtr subbedChannels, ulong channelId, IntPtr options);
    [DllImport(IoReportLib)] private static extern IntPtr IOReportCreateSamples(IntPtr sub, IntPtr subbedChannels, IntPtr options);
    [DllImport(IoReportLib)] private static extern IntPtr IOReportCreateSamplesDelta(IntPtr a, IntPtr b, IntPtr options);
    [DllImport(IoReportLib)] private static extern IntPtr IOReportChannelGetChannelName(IntPtr channel);
    [DllImport(IoReportLib)] private static extern int IOReportStateGetCount(IntPtr channel);
    [DllImport(IoReportLib)] private static extern IntPtr IOReportStateGetNameForIndex(IntPtr channel, int index);
    [DllImport(IoReportLib)] private static extern long IOReportStateGetResidency(IntPtr channel, int index);
}
