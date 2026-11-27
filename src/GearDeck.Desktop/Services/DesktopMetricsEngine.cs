using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GearDeck.Desktop.Services;

public sealed record MetricDefinition(string Id, string ShortLabel, string FullName, string Glyph, bool HasPercentageBar);

public sealed class SystemMetricsSnapshot
{
    public double CpuPercent { get; init; }
    public double? CpuFreqGhz { get; init; }
    public double RamPercent { get; init; }
    public double MemUsedGb { get; init; }
    public double MemTotalGb { get; init; }
    public double? GpuPercent { get; init; }
    public int? GpuTempC { get; init; }
    public double? VramUsedGb { get; init; }
    public double? VramTotalGb { get; init; }
    public double NetDownBytesPerSec { get; init; }
    public double NetUpBytesPerSec { get; init; }
    public double DiskActivePercent { get; init; }
    public string SystemDriveLabel { get; init; } = "C:";
    public double DiskSpaceUsedPercent { get; init; }
    public double DiskFreeGb { get; init; }
    public TimeSpan Uptime { get; init; }
}

/// <summary>
/// 跨平台系统指标引擎：Windows 使用 kernel32/user32/iphlpapi/PDH/NVML 与 Win32RegistryHelper；
/// macOS 使用 vm_stat/sysctl/DriveInfo/NetworkInterface 安全采样，缺失指标留空不抛异常。
/// </summary>
public static class DesktopMetricsEngine
{
    public static IReadOnlyList<MetricDefinition> AllMetrics { get; } =
    [
        new("CPU", "CPU 占用", "CPU 使用率", "\uE950", true),
        new("CPU_FREQ", "CPU 频率", "CPU 实时频率", "\uE945", false),
        new("RAM", "内存占用", "内存使用率", "\uE964", true),
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
        "CPU", "CPU_FREQ", "RAM", "GPU", "GPU_TEMP", "VRAM", "NET_DOWN", "NET_UP", "DISK", "DISK_SPACE", "UPTIME"
    ];

    private const double Gb = 1073741824.0;

    private static readonly object Gate = new();
    private static SystemMetricsSnapshot _lastSnapshot = new();
    private static long _lastSampleMs;

    private static ulong _lastIdle, _lastKernel, _lastUser;
    private static long _lastNetRx, _lastNetTx;
    private static long _lastNetTick;
    private static double _baseCpuGhz;

    // NVML (NVIDIA GPU 精准指标，可选)
    private static bool _nvmlProbed;
    private static bool _nvmlOk;
    private static IntPtr _nvmlDevice;
    private static NvmlGetUtil? _nvmlGetUtil;
    private static NvmlGetTemp? _nvmlGetTemp;
    private static NvmlGetMem? _nvmlGetMem;

    // PDH (通用 CPU 性能 / 磁盘活动 / Windows GPU 引擎与显存)
    private static bool _pdhProbed;
    private static IntPtr _pdhQuery;
    private static IntPtr _pdhCpuPerf;
    private static IntPtr _pdhDiskTime;
    private static IntPtr _pdhGpu3d;
    private static IntPtr _pdhGpuVram;
    private static bool _pdhPrimed;

    public static SystemMetricsSnapshot Sample()
    {
        lock (Gate)
        {
            long now = Environment.TickCount64;
            if (_lastSampleMs != 0 && now - _lastSampleMs < 750)
                return _lastSnapshot;
            _lastSampleMs = now;

            bool isWindows = OperatingSystem.IsWindows();
            double cpu = isWindows ? SampleCpu() : SampleMacCpu();
            var (ramPct, memUsedGb, memTotalGb) = isWindows ? SampleMemory() : SampleMacMemory();
            var (netDown, netUp) = SampleNetwork(now);
            var (driveLabel, diskUsedPct, diskFreeGb) = SampleDiskSpace();

            double? cpuPerfPct = null;
            double diskActivePct = 0;
            double? pdhGpuPct = null;
            double? pdhVramUsedGb = null;

            if (isWindows)
            {
                EnsureBaseCpuFreq();
                EnsurePdh();
                EnsureNvml();

                if (_pdhQuery != IntPtr.Zero && PdhCollectQueryData(_pdhQuery) == 0)
                {
                    if (_pdhPrimed)
                    {
                        cpuPerfPct = ReadPdhDouble(_pdhCpuPerf);
                        diskActivePct = Math.Clamp(ReadPdhDouble(_pdhDiskTime) ?? 0, 0, 100);
                        pdhGpuPct = SumPdhWildcard(_pdhGpu3d);
                        if (SumPdhWildcard(_pdhGpuVram) is { } vBytes && vBytes > 0)
                            pdhVramUsedGb = vBytes / Gb;
                    }
                    _pdhPrimed = true;
                }
            }

            double? cpuFreqGhz = null;
            if (isWindows)
            {
                if (_baseCpuGhz > 0)
                {
                    cpuFreqGhz = cpuPerfPct is { } p && p > 0
                        ? Math.Round(_baseCpuGhz * p / 100.0, 2)
                        : _baseCpuGhz;
                }
            }
            else
            {
                string hz = RunCommand("/usr/sbin/sysctl", ["-n", "hw.cpufrequency"]);
                if (ParseDouble(hz) is { } frequency && frequency > 0)
                    cpuFreqGhz = Math.Round(frequency / 1000000000.0, 2);
            }

            double? gpuPct = null;
            int? gpuTempC = null;
            double? vramUsedGb = pdhVramUsedGb;
            double? vramTotalGb = null;

            if (isWindows && _nvmlOk && _nvmlDevice != IntPtr.Zero)
            {
                if (_nvmlGetUtil != null && _nvmlGetUtil(_nvmlDevice, out var util) == 0)
                    gpuPct = util.Gpu;
                if (_nvmlGetTemp != null && _nvmlGetTemp(_nvmlDevice, 0, out uint temp) == 0 && temp < 150)
                    gpuTempC = (int)temp;
                if (_nvmlGetMem != null && _nvmlGetMem(_nvmlDevice, out var mem) == 0 && mem.Total > 0)
                {
                    vramUsedGb = mem.Used / Gb;
                    vramTotalGb = mem.Total / Gb;
                }
            }

            if (gpuPct == null && pdhGpuPct is { } pg)
                gpuPct = Math.Clamp(pg, 0, 100);

            _lastSnapshot = new SystemMetricsSnapshot
            {
                CpuPercent = cpu,
                CpuFreqGhz = cpuFreqGhz,
                RamPercent = ramPct,
                MemUsedGb = memUsedGb,
                MemTotalGb = memTotalGb,
                GpuPercent = gpuPct,
                GpuTempC = gpuTempC,
                VramUsedGb = vramUsedGb,
                VramTotalGb = vramTotalGb,
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
            "RAM" or "MEM" => $"{s.RamPercent:F0}% ({s.MemUsedGb:F1} / {s.MemTotalGb:F1} GB)",
            "MEM_USED" => $"{s.MemUsedGb:F1} / {s.MemTotalGb:F1} GB",
            "GPU" => s.GpuPercent is { } g ? $"{g:F0}%" : "无读数",
            "GPU_TEMP" => s.GpuTempC is { } t ? $"{t} °C" : "未支持",
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
        // sysctl vm.loadavg 返回 1/5/15 分钟负载；按逻辑核心数折算为近似占用率。
        string load = RunCommand("/usr/sbin/sysctl", ["-n", "vm.loadavg"]);
        string[] parts = load.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || ParseDouble(parts[0]) is not { } oneMin)
            return 0;
        int cores = Math.Max(1, Environment.ProcessorCount);
        return Math.Clamp(oneMin / cores * 100.0, 0, 100);
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
            long rx = 0, tx = 0;
            if (OperatingSystem.IsWindows())
            {
                (rx, tx) = ReadWinIfCounters();
            }
            else
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    var st = ni.GetIPv4Statistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
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

    private static (long Rx, long Tx) ReadWinIfCounters()
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
            long rx = 0, tx = 0;
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
            return (root.TrimEnd('\\', '/'), usedPct, free / Gb);
        }
        catch
        {
            return (OperatingSystem.IsWindows() ? "C:" : "/", 0, 0);
        }
    }

    private static void EnsureBaseCpuFreq()
    {
        if (_baseCpuGhz > 0 || !OperatingSystem.IsWindows()) return;
        try
        {
            int? mhz = Win32RegistryHelper.ReadInt32(
                Win32RegistryRoot.HKLM,
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "~MHz");
            if (mhz is > 0)
                _baseCpuGhz = Math.Round(mhz.Value / 1000.0, 2);
        }
        catch { }
    }

    private static void EnsurePdh()
    {
        if (_pdhProbed) return;
        _pdhProbed = true;
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _pdhQuery) != 0 || _pdhQuery == IntPtr.Zero)
                return;
            PdhAddEnglishCounterW(_pdhQuery, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _pdhCpuPerf);
            PdhAddEnglishCounterW(_pdhQuery, @"\PhysicalDisk(_Total)\% Disk Time", IntPtr.Zero, out _pdhDiskTime);
            PdhAddEnglishCounterW(_pdhQuery, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _pdhGpu3d);
            PdhAddEnglishCounterW(_pdhQuery, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _pdhGpuVram);
            PdhCollectQueryData(_pdhQuery);
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

    private static double? SumPdhWildcard(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        try
        {
            uint bufSize = 0, itemCount = 0;
            uint st = PdhGetFormattedCounterArrayW(counter, 0x00000200, ref bufSize, ref itemCount, IntPtr.Zero);
            if (st != 0x800007D2 || bufSize == 0 || itemCount == 0 || itemCount > 512) // PDH_MORE_DATA
                return null;

            IntPtr mem = Marshal.AllocHGlobal((int)bufSize);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, 0x00000200, ref bufSize, ref itemCount, mem) != 0)
                    return null;
                int stride = Marshal.SizeOf<PdhFmtCounterValueItem>();
                double sum = 0;
                int validCount = 0;
                for (int i = 0; i < itemCount; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(mem + i * stride);
                    if (item.Value.CStatus <= 1 && item.Value.DoubleValue >= 0)
                    {
                        sum += item.Value.DoubleValue;
                        validCount++;
                    }
                }
                return validCount > 0 ? sum : null;
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }
        catch
        {
            return null;
        }
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

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
    [DllImport("iphlpapi.dll")] private static extern uint GetIfTable(IntPtr pIfTable, ref uint pdwSize, bool bOrder);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);
}
