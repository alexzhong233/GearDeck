using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

#if GEARDECK_DESKTOP
namespace GearDeck.Desktop.Services;
#else
namespace GearDeck.Metrics;
#endif

public sealed record HwInfoSnapshot(
    bool Connected,
    int? CpuTempC,
    double? CpuFreqGhz,
    string? GpuName,
    double? GpuPercent,
    int? GpuTempC,
    double? VramUsedGb,
    double? VramTotalGb)
{
    public static HwInfoSnapshot Empty { get; } = new(false, null, null, null, null, null, null, null);
}

/// <summary>
/// HWiNFO Shared Memory v2 只读桥接：
/// 仅在 Windows 用户已运行 HWiNFO 且开启 Shared Memory Support 时无感读取，不引入任何 Ring-0 驱动。
/// </summary>
public static class HwInfoSharedMemoryReader
{
    private const uint HwInfoSignature = 0x53695748; // 'HWiS'
    private const int HeaderSize = 44;
    private const int MinSensorSize = 264;
    private const int MinReadingSize = 312;
    private const uint MaxSensors = 1024;
    private const uint MaxReadings = 8192;
    private const int MaxMapBytes = 8 * 1024 * 1024;

    private const uint FileMapRead = 0x0004;
    private const double Gb = 1073741824.0;

    private const int ReadingTemp = 1;
    private const int ReadingClock = 6;
    private const int ReadingUsage = 7;

    private static readonly object ReadGate = new();
    private static long _lastAttemptMs;
    private static HwInfoSnapshot _cached = HwInfoSnapshot.Empty;

    public static bool Enabled { get; set; } = true;

    public static HwInfoSnapshot LastSnapshot
    {
        get { lock (ReadGate) return _cached; }
    }

    public static HwInfoSnapshot TryRead(long nowMs)
    {
        if (!Enabled || !OperatingSystem.IsWindows())
            return HwInfoSnapshot.Empty;

        lock (ReadGate)
        {
            int intervalMs = _cached.Connected ? 750 : 5000;
            if (_lastAttemptMs != 0 && nowMs - _lastAttemptMs < intervalMs)
                return _cached;
            _lastAttemptMs = nowMs;

            var snap = ReadMappingOnce();
            _cached = snap;
            return snap;
        }
    }

    public static void ResetCache()
    {
        lock (ReadGate)
        {
            _lastAttemptMs = 0;
            _cached = HwInfoSnapshot.Empty;
        }
    }

    /// <summary>纯托管解析 HWiNFO Shared Memory v2 二进制块，支持跨平台单元测试验证。</summary>
    public static HwInfoSnapshot Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize)
            return HwInfoSnapshot.Empty;

        uint sig = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(0, 4));
        if (sig != HwInfoSignature)
            return HwInfoSnapshot.Empty;

        uint sensorOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(20, 4));
        uint sensorSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(24, 4));
        uint numSensors = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(28, 4));
        uint readingOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(32, 4));
        uint readingSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(36, 4));
        uint numReadings = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(40, 4));

        if (sensorSize < MinSensorSize || readingSize < MinReadingSize ||
            numSensors > MaxSensors || numReadings > MaxReadings)
        {
            return HwInfoSnapshot.Empty;
        }

        long sensorsEnd = (long)sensorOffset + (long)sensorSize * numSensors;
        long readingsEnd = (long)readingOffset + (long)readingSize * numReadings;
        if (sensorsEnd > buffer.Length || readingsEnd > buffer.Length)
            return HwInfoSnapshot.Empty;

        var sensorNames = new string[numSensors];
        var isCpuSensor = new bool[numSensors];
        var isGpuSensor = new bool[numSensors];

        for (uint i = 0; i < numSensors; i++)
        {
            int pos = (int)sensorOffset + (int)(i * sensorSize);
            string origName = ReadFixedCString(buffer.Slice(pos + 8, 128));
            sensorNames[i] = origName;
            isGpuSensor[i] = IsGpuSensorName(origName);
            isCpuSensor[i] = !isGpuSensor[i] && IsCpuSensorName(origName);
        }

        int? cpuTemp = null;
        int cpuTempPriority = int.MinValue;
        double effClockSum = 0;
        int effClockCount = 0;
        double rawClockSum = 0;
        int rawClockCount = 0;

        var gpuCandidates = new Dictionary<uint, GpuCandidate>();

        for (uint i = 0; i < numReadings; i++)
        {
            int pos = (int)readingOffset + (int)(i * readingSize);
            int type = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(pos, 4));
            uint sensorIdx = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(pos + 4, 4));
            if (sensorIdx >= numSensors)
                continue;

            string label = ReadFixedCString(buffer.Slice(pos + 12, 128));
            string unit = ReadFixedCString(buffer.Slice(pos + 268, 16));
            double val = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(pos + 284, 8)));
            if (double.IsNaN(val) || double.IsInfinity(val))
                continue;

            if (isCpuSensor[sensorIdx])
            {
                if (type == ReadingTemp && val >= 5 && val <= 125)
                {
                    int prio = ScoreCpuTempLabel(label);
                    if (prio > cpuTempPriority)
                    {
                        cpuTempPriority = prio;
                        cpuTemp = (int)Math.Round(val);
                    }
                }
                else if (type == ReadingClock)
                {
                    double ghz = NormalizeClockToGhz(val, unit);
                    if (ghz is >= 0.3 and <= 9.0)
                    {
                        if (label.Contains("Effective Clock", StringComparison.OrdinalIgnoreCase))
                        {
                            effClockSum += ghz;
                            effClockCount++;
                        }
                        else if (IsCoreClockLabel(label))
                        {
                            rawClockSum += ghz;
                            rawClockCount++;
                        }
                    }
                }
            }
            else if (isGpuSensor[sensorIdx])
            {
                if (!gpuCandidates.TryGetValue(sensorIdx, out var cand))
                {
                    cand = new GpuCandidate(CleanGpuName(sensorNames[sensorIdx]));
                    gpuCandidates[sensorIdx] = cand;
                }

                if (type == ReadingTemp && val >= 5 && val <= 130)
                {
                    int prio = ScoreGpuTempLabel(label);
                    if (prio > cand.TempPriority)
                    {
                        cand.TempPriority = prio;
                        cand.TempC = (int)Math.Round(val);
                    }
                }
                else if (type == ReadingUsage && val >= 0 && val <= 100)
                {
                    int loadPrio = ScoreGpuLoadLabel(label);
                    if (loadPrio > cand.LoadPriority)
                    {
                        cand.LoadPriority = loadPrio;
                        cand.GpuPercent = Math.Clamp(val, 0, 100);
                    }
                    else if (label.Contains("GPU Memory Usage", StringComparison.OrdinalIgnoreCase) && val > 0.5)
                    {
                        cand.MemUsagePct = val;
                    }
                }
                else if (val >= 0 && IsVramUsedLabel(label))
                {
                    int vramPrio = ScoreVramLabel(label);
                    double gb = NormalizeBytesOrMbToGb(val, unit);
                    if (gb >= 0 && vramPrio > cand.VramUsedPriority)
                    {
                        cand.VramUsedPriority = vramPrio;
                        cand.VramUsedGb = gb;
                    }
                }
                else if (val > 0 && IsVramTotalLabel(label))
                {
                    double gb = NormalizeBytesOrMbToGb(val, unit);
                    if (gb > 0)
                        cand.VramTotalGb = gb;
                }
            }
        }

        double? cpuFreq = null;
        if (effClockCount > 0)
            cpuFreq = Math.Round(effClockSum / effClockCount, 2);
        else if (rawClockCount > 0)
            cpuFreq = Math.Round(rawClockSum / rawClockCount, 2);

        GpuCandidate? bestGpu = PickBestGpu(gpuCandidates.Values);
        if (bestGpu is { VramTotalGb: null, VramUsedGb: > 0.05, MemUsagePct: > 1.0 })
            bestGpu.VramTotalGb = Math.Round(bestGpu.VramUsedGb.Value * 100.0 / bestGpu.MemUsagePct.Value, 1);

        return new HwInfoSnapshot(
            Connected: true,
            CpuTempC: cpuTemp,
            CpuFreqGhz: cpuFreq,
            GpuName: bestGpu?.Name,
            GpuPercent: bestGpu?.GpuPercent,
            GpuTempC: bestGpu?.TempC,
            VramUsedGb: bestGpu?.VramUsedGb,
            VramTotalGb: bestGpu?.VramTotalGb);
    }

    private static GpuCandidate? PickBestGpu(IEnumerable<GpuCandidate> candidates)
    {
        GpuCandidate? best = null;
        int bestScore = int.MinValue;
        foreach (var c in candidates)
        {
            int score = ScoreGpuName(c.Name);
            if ((c.VramTotalGb ?? 0) > 2.0 || (c.VramUsedGb ?? 0) > 2.0)
                score += 40;
            if ((c.GpuPercent ?? 0) > 5.0)
                score += 10;
            if (c.TempC.HasValue)
                score += 5;
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best;
    }

    private static int ScoreGpuName(string name)
    {
        int score = 0;
        if (ContainsAny(name, "GeForce", "RTX", "GTX", "Quadro", "RX ", "Radeon RX", "Radeon Pro", "Arc A", "Arc B", "Discrete", "dGPU"))
            score += 100;
        if (ContainsAny(name, "UHD Graphics", "HD Graphics", "Iris", "Radeon(TM) Graphics", "Radeon Graphics", "Vega", "Integrated", "iGPU", "APU"))
            score -= 50;
        return score;
    }

    private static bool IsCpuSensorName(string name)
        => ContainsAny(name, "CPU", "Ryzen", "Threadripper", "EPYC", "Core i", "Core Ultra", "Pentium", "Celeron", "Xeon", "Processor");

    private static bool IsGpuSensorName(string name)
        => ContainsAny(name, "GPU", "GeForce", "Radeon", "NVIDIA", "Intel Arc", "Intel Iris", "Intel UHD", "Intel HD", "Graphics");

    private static int ScoreCpuTempLabel(string label)
    {
        if (label.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase)) return 100;
        if (label.Equals("CPU Package", StringComparison.OrdinalIgnoreCase)) return 95;
        if (label.Contains("CPU Die", StringComparison.OrdinalIgnoreCase)) return 90;
        if (label.Equals("Core Max", StringComparison.OrdinalIgnoreCase)) return 80;
        if (label.Contains("CPU (Tdie)", StringComparison.OrdinalIgnoreCase)) return 75;
        if (label.Equals("CPU", StringComparison.OrdinalIgnoreCase)) return 70;
        if (label.Contains("Package", StringComparison.OrdinalIgnoreCase)) return 60;
        if (label.Contains("Core", StringComparison.OrdinalIgnoreCase) && !label.Contains("Distance", StringComparison.OrdinalIgnoreCase)) return 40;
        return int.MinValue;
    }

    private static bool IsCoreClockLabel(string label)
        => (label.Contains("Core", StringComparison.OrdinalIgnoreCase) && label.Contains("Clock", StringComparison.OrdinalIgnoreCase))
           && !label.Contains("Uncore", StringComparison.OrdinalIgnoreCase)
           && !label.Contains("Memory", StringComparison.OrdinalIgnoreCase)
           && !label.Contains("Bus", StringComparison.OrdinalIgnoreCase)
           && !label.Contains("Ring", StringComparison.OrdinalIgnoreCase);

    private static int ScoreGpuTempLabel(string label)
    {
        if (label.Equals("GPU Temperature", StringComparison.OrdinalIgnoreCase)) return 100;
        if (label.Equals("GPU Thermal Diode", StringComparison.OrdinalIgnoreCase)) return 95;
        if (label.Contains("GPU Core Temperature", StringComparison.OrdinalIgnoreCase)) return 90;
        if (label.Contains("GPU Temperature", StringComparison.OrdinalIgnoreCase) && !label.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)) return 80;
        if (label.Contains("GPU Hot Spot", StringComparison.OrdinalIgnoreCase)) return 40;
        return int.MinValue;
    }

    private static int ScoreGpuLoadLabel(string label)
    {
        if (label.Equals("GPU Core Load", StringComparison.OrdinalIgnoreCase)) return 100;
        if (label.Equals("GPU Utilization", StringComparison.OrdinalIgnoreCase)) return 95;
        if (label.Equals("GPU D3D Usage", StringComparison.OrdinalIgnoreCase)) return 80;
        return int.MinValue;
    }

    private static bool IsVramUsedLabel(string label)
        => label.Equals("GPU Memory Allocated", StringComparison.OrdinalIgnoreCase)
           || label.Equals("GPU D3D Memory Dedicated", StringComparison.OrdinalIgnoreCase)
           || label.Contains("GPU Memory Used", StringComparison.OrdinalIgnoreCase);

    private static int ScoreVramLabel(string label)
    {
        if (label.Equals("GPU Memory Allocated", StringComparison.OrdinalIgnoreCase)) return 100;
        if (label.Equals("GPU D3D Memory Dedicated", StringComparison.OrdinalIgnoreCase)) return 90;
        return 70;
    }

    private static bool IsVramTotalLabel(string label)
        => label.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase)
           || label.Equals("GPU Dedicated Memory Total", StringComparison.OrdinalIgnoreCase);

    private static double NormalizeClockToGhz(double value, string unit)
    {
        if (unit.Equals("GHz", StringComparison.OrdinalIgnoreCase)) return value;
        if (unit.Equals("Hz", StringComparison.OrdinalIgnoreCase)) return value / 1_000_000_000.0;
        return value / 1000.0; // HWiNFO 默认 MHz
    }

    private static double NormalizeBytesOrMbToGb(double value, string unit)
    {
        if (unit.Equals("GB", StringComparison.OrdinalIgnoreCase)) return value;
        if (unit.Equals("KB", StringComparison.OrdinalIgnoreCase)) return value / (1024.0 * 1024.0);
        if (unit.Equals("B", StringComparison.OrdinalIgnoreCase) || unit.Equals("Bytes", StringComparison.OrdinalIgnoreCase)) return value / Gb;
        return value / 1024.0; // HWiNFO 默认 MB
    }

    private static string CleanGpuName(string raw)
    {
        string cleaned = Regex.Replace(raw ?? "", @"^GPU\s*\[#\d+\]:\s*", "", RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrEmpty(cleaned) ? "GPU" : cleaned;
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        foreach (string term in terms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ReadFixedCString(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        if (nul >= 0)
            bytes = bytes[..nul];
        return Encoding.UTF8.GetString(bytes).Trim();
    }

    private static HwInfoSnapshot ReadMappingOnce()
    {
        IntPtr map = OpenFileMappingW(FileMapRead, false, @"Global\HWiNFO_SENS_SM2");
        if (map == IntPtr.Zero)
            map = OpenFileMappingW(FileMapRead, false, @"Local\HWiNFO_SENS_SM2");
        if (map == IntPtr.Zero)
            return HwInfoSnapshot.Empty;

        IntPtr view = IntPtr.Zero;
        try
        {
            view = MapViewOfFile(map, FileMapRead, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero)
                return HwInfoSnapshot.Empty;

            if (VirtualQuery(view, out var mbi, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0 ||
                mbi.RegionSize < (nuint)HeaderSize)
            {
                return HwInfoSnapshot.Empty;
            }

            int mappedLimit = (int)Math.Min((ulong)mbi.RegionSize, (ulong)MaxMapBytes);
            byte[] header = new byte[HeaderSize];
            Marshal.Copy(view, header, 0, HeaderSize);

            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
            if (sig != HwInfoSignature)
                return HwInfoSnapshot.Empty;

            uint sOff = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4));
            uint sSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4));
            uint sNum = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28, 4));
            uint rOff = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32, 4));
            uint rSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36, 4));
            uint rNum = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40, 4));
            if (sNum > MaxSensors || rNum > MaxReadings)
                return HwInfoSnapshot.Empty;

            long required = Math.Max((long)sOff + (long)sSize * sNum, (long)rOff + (long)rSize * rNum);
            if (required < HeaderSize || required > mappedLimit)
                return HwInfoSnapshot.Empty;

            byte[] raw = new byte[required];
            Marshal.Copy(view, raw, 0, (int)required);
            return Parse(raw);
        }
        catch
        {
            return HwInfoSnapshot.Empty;
        }
        finally
        {
            if (view != IntPtr.Zero) UnmapViewOfFile(view);
            CloseHandle(map);
        }
    }

    private sealed class GpuCandidate(string name)
    {
        public string Name { get; } = name;
        public int TempPriority = int.MinValue;
        public int? TempC;
        public int LoadPriority = int.MinValue;
        public double? GpuPercent;
        public int VramUsedPriority = int.MinValue;
        public double? VramUsedGb;
        public double? VramTotalGb;
        public double? MemUsagePct;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nuint dwLength);
}
