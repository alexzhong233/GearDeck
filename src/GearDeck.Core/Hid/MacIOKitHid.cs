using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GearDeck.Core.Hid;

public enum MacHidAccessState
{
    NotApplicable,
    Granted,
    Denied,
    Unknown,
}

public sealed record MacHidInterfaceInfo(
    ulong RegistryEntryId,
    ushort VendorId,
    ushort ProductId,
    ushort PrimaryUsagePage,
    ushort PrimaryUsage,
    int MaxInputReportSize,
    int MaxOutputReportSize,
    int MaxFeatureReportSize,
    int LocationId,
    string Transport,
    string Product,
    string Manufacturer,
    string SerialNumber,
    byte[] ReportDescriptor,
    bool HasMouseUsage,
    bool HasKeyboardUsage,
    IReadOnlyList<(ushort UsagePage, ushort Usage)> UsagePairs)
{
    public bool HasUsage(ushort page, ushort usage)
    {
        if (PrimaryUsagePage == page && PrimaryUsage == usage)
            return true;
        foreach (var pair in UsagePairs)
        {
            if (pair.UsagePage == page && pair.Usage == usage)
                return true;
        }
        return false;
    }
}

/// <summary>
/// 原生 macOS IOKit HID 封装：
/// - 通过 IOServiceGetMatchingServices("IOHIDDevice") 枚举设备（不调用 IOHIDManagerOpen，即使未授权输入监控也能发现设备）；
/// - 提供 IOHIDCheckAccess / IOHIDRequestAccess 权限探测与系统设置跳转；
/// - 通过 IOHIDDeviceCreate + IOHIDDeviceOpen + 当前线程 CFRunLoop 回调完成 Feature/Output/Input 收发。
/// 非 macOS 平台全部方法安全空返回，不触发 P/Invoke。
/// </summary>
public static class MacIOKitHid
{
    private const string IoKitLib = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int kIOHIDRequestTypeListenEvent = 1;
    private const int kIOHIDAccessTypeGranted = 0;
    private const int kIOHIDAccessTypeDenied = 1;
    private const uint kCFStringEncodingUTF8 = 0x08000100;
    private const int kCFNumberSInt64Type = 4;

    internal const int kIOReturnSuccess = 0;
    internal const int kIOReturnNotPermitted = unchecked((int)0xE00002E2);
    internal const int kIOHIDReportTypeInput = 0;
    internal const int kIOHIDReportTypeOutput = 1;
    internal const int kIOHIDReportTypeFeature = 2;

    private static readonly ConcurrentDictionary<string, string> BlockedDevicesMap = new(StringComparer.OrdinalIgnoreCase);

    public static bool HasPermissionDeniedDevices => !BlockedDevicesMap.IsEmpty;

    public static IReadOnlyList<string> BlockedDevices => BlockedDevicesMap.Values.Distinct().OrderBy(x => x).ToList();

    public static event Action? PermissionStateChanged;

    public static void TrackPermissionDenied(ushort vid, ushort pid, string label)
    {
        string key = $"{vid:X4}:{pid:X4}";
        string value = string.IsNullOrWhiteSpace(label) ? key : $"{label} ({key})";
        if (BlockedDevicesMap.TryAdd(key, value) || BlockedDevicesMap.TryUpdate(key, value, value))
            PermissionStateChanged?.Invoke();
    }

    public static void ClearPermissionDenied(ushort? vid = null, ushort? pid = null)
    {
        bool changed = false;
        if (vid.HasValue && pid.HasValue)
        {
            changed = BlockedDevicesMap.TryRemove($"{vid.Value:X4}:{pid.Value:X4}", out _);
        }
        else if (!BlockedDevicesMap.IsEmpty)
        {
            BlockedDevicesMap.Clear();
            changed = true;
        }
        if (changed)
            PermissionStateChanged?.Invoke();
    }

    public static MacHidAccessState CheckInputMonitoringAccess()
    {
        if (!OperatingSystem.IsMacOS())
            return MacHidAccessState.NotApplicable;
        try
        {
            int code = IOHIDCheckAccess(kIOHIDRequestTypeListenEvent);
            return code switch
            {
                kIOHIDAccessTypeGranted => MacHidAccessState.Granted,
                kIOHIDAccessTypeDenied => MacHidAccessState.Denied,
                _ => MacHidAccessState.Unknown,
            };
        }
        catch
        {
            return MacHidAccessState.Unknown;
        }
    }

    public static bool RequestInputMonitoringAccess()
    {
        if (!OperatingSystem.IsMacOS())
            return false;
        try
        {
            bool ok = IOHIDRequestAccess(kIOHIDRequestTypeListenEvent);
            PermissionStateChanged?.Invoke();
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public static void OpenInputMonitoringSettings()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        try
        {
            Process.Start(new ProcessStartInfo("open")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent" },
            });
        }
        catch { }
    }

    public static List<MacHidInterfaceInfo> Enumerate(int? vendorId = null, int? productId = null)
    {
        var results = new List<MacHidInterfaceInfo>();
        if (!OperatingSystem.IsMacOS())
            return results;

        uint iterator = 0;
        try
        {
            IntPtr matching = IOServiceMatching("IOHIDDevice");
            if (matching == IntPtr.Zero)
                return results;

            // IOServiceGetMatchingServices 会接管并释放 matching 字典，不能再 CFRelease(matching)
            if (IOServiceGetMatchingServices(0, matching, out iterator) != kIOReturnSuccess || iterator == 0)
                return results;

            uint service;
            while ((service = IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    int vid = (int)(ReadIntProperty(service, "VendorID") ?? 0);
                    int pid = (int)(ReadIntProperty(service, "ProductID") ?? 0);
                    if (vendorId.HasValue && vid != vendorId.Value) continue;
                    if (productId.HasValue && pid != productId.Value) continue;

                    ulong entryId = 0;
                    _ = IORegistryEntryGetRegistryEntryID(service, out entryId);

                    ushort primaryPage = (ushort)Math.Clamp(ReadIntProperty(service, "PrimaryUsagePage") ?? 0, 0, ushort.MaxValue);
                    ushort primaryUsage = (ushort)Math.Clamp(ReadIntProperty(service, "PrimaryUsage") ?? 0, 0, ushort.MaxValue);
                    int maxIn = (int)(ReadIntProperty(service, "MaxInputReportSize") ?? 0);
                    int maxOut = (int)(ReadIntProperty(service, "MaxOutputReportSize") ?? 0);
                    int maxFeat = (int)(ReadIntProperty(service, "MaxFeatureReportSize") ?? 0);
                    int locId = (int)(ReadIntProperty(service, "LocationID") ?? 0);
                    string transport = ReadStringProperty(service, "Transport");
                    string product = ReadStringProperty(service, "Product");
                    string manufacturer = ReadStringProperty(service, "Manufacturer");
                    string serial = ReadStringProperty(service, "SerialNumber");
                    byte[] desc = ReadDataProperty(service, "ReportDescriptor");
                    var pairs = ReadUsagePairs(service);

                    bool hasMouse = (primaryPage == 0x0001 && primaryUsage == 0x0002)
                        || pairs.Any(p => p.UsagePage == 0x0001 && p.Usage == 0x0002);
                    bool hasKeyboard = (primaryPage == 0x0001 && primaryUsage == 0x0006)
                        || pairs.Any(p => p.UsagePage == 0x0001 && p.Usage == 0x0006);

                    results.Add(new MacHidInterfaceInfo(
                        entryId,
                        (ushort)Math.Clamp(vid, 0, ushort.MaxValue),
                        (ushort)Math.Clamp(pid, 0, ushort.MaxValue),
                        primaryPage,
                        primaryUsage,
                        maxIn,
                        maxOut,
                        maxFeat,
                        locId,
                        transport,
                        product,
                        manufacturer,
                        serial,
                        desc,
                        hasMouse,
                        hasKeyboard,
                        pairs));
                }
                catch { }
                finally
                {
                    _ = IOObjectRelease(service);
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"MacIOKitHid.Enumerate 异常: {ex.Message}");
        }
        finally
        {
            if (iterator != 0)
                _ = IOObjectRelease(iterator);
        }

        return results;
    }

    public sealed class MacHidConnection : IDisposable
    {
        private readonly IntPtr _deviceRef;
        private readonly IntPtr _runLoop;
        private readonly IntPtr _runLoopMode;
        private readonly IntPtr _callbackBuffer;
        private readonly int _callbackBufferSize;
        private readonly Queue<byte[]> _queue = new();
        private readonly object _queueLock = new();
        private readonly IOHIDReportCallback _callback;
        private GCHandle _selfHandle;
        private bool _opened;
        private bool _scheduled;
        private bool _disposed;

        public MacHidInterfaceInfo Info { get; }
        public int LastOpenReturn { get; private set; }
        public bool PermissionDenied => LastOpenReturn == kIOReturnNotPermitted;

        private MacHidConnection(
            MacHidInterfaceInfo info,
            IntPtr deviceRef,
            int lastReturn,
            bool opened)
        {
            Info = info;
            _deviceRef = deviceRef;
            LastOpenReturn = lastReturn;
            _opened = opened;
            _runLoop = CFRunLoopGetCurrent();
            _runLoopMode = GetDefaultRunLoopMode();
            _callbackBufferSize = Math.Clamp(Math.Max(128, info.MaxInputReportSize + 8), 128, 4096);
            _callbackBuffer = Marshal.AllocHGlobal(_callbackBufferSize);
            _callback = OnInputReport;
            _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);

            if (opened && _deviceRef != IntPtr.Zero && _runLoop != IntPtr.Zero && _runLoopMode != IntPtr.Zero)
            {
                IOHIDDeviceScheduleWithRunLoop(_deviceRef, _runLoop, _runLoopMode);
                IOHIDDeviceRegisterInputReportCallback(
                    _deviceRef,
                    _callbackBuffer,
                    _callbackBufferSize,
                    _callback,
                    GCHandle.ToIntPtr(_selfHandle));
                _scheduled = true;
            }
        }

        public static MacHidConnection? TryOpen(MacHidInterfaceInfo info, out int openReturn)
        {
            openReturn = kIOReturnSuccess;
            if (!OperatingSystem.IsMacOS() || info.RegistryEntryId == 0)
                return null;

            uint service = 0;
            IntPtr devRef = IntPtr.Zero;
            try
            {
                IntPtr matching = IORegistryEntryIDMatching(info.RegistryEntryId);
                if (matching == IntPtr.Zero)
                    return null;
                service = IOServiceGetMatchingService(0, matching);
                if (service == 0)
                    return null;

                devRef = IOHIDDeviceCreate(IntPtr.Zero, service);
                if (devRef == IntPtr.Zero)
                    return null;

                openReturn = IOHIDDeviceOpen(devRef, 0);
                if (openReturn != kIOReturnSuccess)
                {
                    if (openReturn == kIOReturnNotPermitted)
                        TrackPermissionDenied(info.VendorId, info.ProductId, info.Product);
                    CFRelease(devRef);
                    devRef = IntPtr.Zero;
                    return null;
                }

                ClearPermissionDenied(info.VendorId, info.ProductId);
                var conn = new MacHidConnection(info, devRef, openReturn, opened: true);
                devRef = IntPtr.Zero; // 移交所有权
                return conn;
            }
            catch
            {
                if (devRef != IntPtr.Zero)
                    CFRelease(devRef);
                return null;
            }
            finally
            {
                if (service != 0)
                    _ = IOObjectRelease(service);
            }
        }

        public void DrainInputReports()
        {
            if (_disposed || !_opened) return;
            if (_scheduled && _runLoopMode != IntPtr.Zero)
                _ = CFRunLoopRunInMode(_runLoopMode, 0.005, true);
            lock (_queueLock)
                _queue.Clear();
        }

        public bool WriteOutputReport(byte reportId, ReadOnlySpan<byte> data)
        {
            if (_disposed || !_opened || _deviceRef == IntPtr.Zero || data.IsEmpty)
                return false;

            byte[] payload = (reportId == 0 && data.Length == 65 && data[0] == 0)
                ? data.Slice(1).ToArray()
                : data.ToArray();

            int ret = IOHIDDeviceSetReport(_deviceRef, kIOHIDReportTypeOutput, reportId, ref payload[0], payload.Length);
            if (ret == kIOReturnNotPermitted)
            {
                LastOpenReturn = ret;
                TrackPermissionDenied(Info.VendorId, Info.ProductId, Info.Product);
            }
            return ret == kIOReturnSuccess;
        }

        public byte[]? ReadInputReport(int timeoutMs, Func<byte[], bool>? match = null)
        {
            if (_disposed || !_opened) return null;

            var sw = Stopwatch.StartNew();
            do
            {
                lock (_queueLock)
                {
                    while (_queue.Count > 0)
                    {
                        var pkt = _queue.Dequeue();
                        if (match == null || match(pkt))
                            return pkt;
                    }
                }

                if (_scheduled && _runLoopMode != IntPtr.Zero)
                {
                    double remSec = Math.Clamp((timeoutMs - sw.ElapsedMilliseconds) / 1000.0, 0.005, 0.05);
                    _ = CFRunLoopRunInMode(_runLoopMode, remSec, true);
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
            while (sw.ElapsedMilliseconds < timeoutMs);

            lock (_queueLock)
            {
                while (_queue.Count > 0)
                {
                    var pkt = _queue.Dequeue();
                    if (match == null || match(pkt))
                        return pkt;
                }
            }
            return null;
        }

        public byte[]? ExchangeOutput(byte reportId, byte[] request, int timeoutMs, Func<byte[], bool>? match = null)
        {
            DrainInputReports();
            if (!WriteOutputReport(reportId, request))
                return null;
            return ReadInputReport(timeoutMs, match);
        }

        public List<byte[]> CollectInputReports(int timeoutMs, Func<byte[], bool> accept, Func<List<byte[]>, bool> isComplete)
        {
            var collected = new List<byte[]>();
            if (_disposed || !_opened) return collected;

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                int remaining = Math.Max(20, timeoutMs - (int)sw.ElapsedMilliseconds);
                var pkt = ReadInputReport(Math.Min(remaining, 120), accept);
                if (pkt != null)
                {
                    collected.Add(pkt);
                    if (isComplete(collected))
                        break;
                }
            }
            return collected;
        }

        public byte[]? GetFeatureReport(byte reportId, int length)
        {
            if (_disposed || !_opened || _deviceRef == IntPtr.Zero || length <= 0)
                return null;

            var buf = new byte[length];
            buf[0] = reportId;
            nint actualLen = length;
            int ret = IOHIDDeviceGetReport(_deviceRef, kIOHIDReportTypeFeature, reportId, buf, ref actualLen);
            if (ret != kIOReturnSuccess || actualLen <= 0)
                return null;

            int copyLen = Math.Min((int)actualLen, buf.Length);
            if (copyLen == buf.Length) return buf;
            var trimmed = new byte[copyLen];
            Array.Copy(buf, trimmed, copyLen);
            return trimmed;
        }

        public bool SetFeatureReport(byte reportId, ReadOnlySpan<byte> data)
        {
            if (_disposed || !_opened || _deviceRef == IntPtr.Zero || data.IsEmpty)
                return false;

            var payload = data.ToArray();
            int ret = IOHIDDeviceSetReport(_deviceRef, kIOHIDReportTypeFeature, reportId, ref payload[0], payload.Length);
            return ret == kIOReturnSuccess;
        }

        private static void OnInputReport(
            IntPtr context,
            int result,
            IntPtr sender,
            int type,
            uint reportID,
            IntPtr report,
            nint reportLength)
        {
            if (context == IntPtr.Zero || report == IntPtr.Zero || reportLength <= 0)
                return;
            try
            {
                var handle = GCHandle.FromIntPtr(context);
                if (handle.Target is not MacHidConnection conn || conn._disposed)
                    return;

                int len = (int)Math.Min(reportLength, 4096);
                var raw = new byte[len];
                Marshal.Copy(report, raw, 0, len);

                byte[] packet;
                if (reportID > 0 && raw.Length > 0 && raw[0] != (byte)(reportID & 0xFF))
                {
                    packet = new byte[raw.Length + 1];
                    packet[0] = (byte)(reportID & 0xFF);
                    Array.Copy(raw, 0, packet, 1, raw.Length);
                }
                else
                {
                    packet = raw;
                }

                lock (conn._queueLock)
                {
                    if (conn._queue.Count >= 256)
                        conn._queue.Dequeue();
                    conn._queue.Enqueue(packet);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_deviceRef != IntPtr.Zero)
                {
                    if (_scheduled)
                    {
                        IOHIDDeviceRegisterInputReportCallback(_deviceRef, IntPtr.Zero, 0, null, IntPtr.Zero);
                        if (_runLoop != IntPtr.Zero && _runLoopMode != IntPtr.Zero)
                            IOHIDDeviceUnscheduleFromRunLoop(_deviceRef, _runLoop, _runLoopMode);
                    }
                    if (_opened)
                        _ = IOHIDDeviceClose(_deviceRef, 0);
                    CFRelease(_deviceRef);
                }
            }
            catch { }
            finally
            {
                _opened = false;
                if (_selfHandle.IsAllocated)
                    _selfHandle.Free();
                if (_callbackBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(_callbackBuffer);
            }
        }
    }

    // ── CoreFoundation 属性读取辅助 ───────────────────────────────────────────

    private static IntPtr? ReadProperty(uint service, string key)
    {
        IntPtr cfKey = CFStringCreateWithCString(IntPtr.Zero, key, kCFStringEncodingUTF8);
        if (cfKey == IntPtr.Zero) return null;
        try
        {
            IntPtr val = IORegistryEntryCreateCFProperty(service, cfKey, IntPtr.Zero, 0);
            return val == IntPtr.Zero ? null : val;
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    private static long? ReadIntProperty(uint service, string key)
    {
        if (ReadProperty(service, key) is not { } prop) return null;
        try
        {
            return ReadCfNumber(prop);
        }
        finally
        {
            CFRelease(prop);
        }
    }

    private static long? ReadCfNumber(IntPtr prop)
    {
        if (prop == IntPtr.Zero || CFGetTypeID(prop) != CFNumberGetTypeID())
            return null;
        return CFNumberGetValue(prop, kCFNumberSInt64Type, out long v) ? v : null;
    }

    private static string ReadStringProperty(uint service, string key)
    {
        if (ReadProperty(service, key) is not { } prop) return "";
        try
        {
            if (CFGetTypeID(prop) != CFStringGetTypeID()) return "";
            var buf = new byte[512];
            return CFStringGetCString(prop, buf, buf.Length, kCFStringEncodingUTF8)
                ? Encoding.UTF8.GetString(buf).TrimEnd('\0')
                : "";
        }
        finally
        {
            CFRelease(prop);
        }
    }

    private static byte[] ReadDataProperty(uint service, string key)
    {
        if (ReadProperty(service, key) is not { } prop) return [];
        try
        {
            if (CFGetTypeID(prop) != CFDataGetTypeID()) return [];
            int len = (int)Math.Clamp(CFDataGetLength(prop), 0, 65536);
            if (len == 0) return [];
            IntPtr ptr = CFDataGetBytePtr(prop);
            if (ptr == IntPtr.Zero) return [];
            var data = new byte[len];
            Marshal.Copy(ptr, data, 0, len);
            return data;
        }
        finally
        {
            CFRelease(prop);
        }
    }

    private static List<(ushort UsagePage, ushort Usage)> ReadUsagePairs(uint service)
    {
        var pairs = new List<(ushort, ushort)>();
        if (ReadProperty(service, "DeviceUsagePairs") is not { } arr)
            return pairs;
        try
        {
            if (CFGetTypeID(arr) != CFArrayGetTypeID())
                return pairs;

            nint count = CFArrayGetCount(arr);
            IntPtr pageKey = CFStringCreateWithCString(IntPtr.Zero, "DeviceUsagePage", kCFStringEncodingUTF8);
            IntPtr usageKey = CFStringCreateWithCString(IntPtr.Zero, "DeviceUsage", kCFStringEncodingUTF8);
            try
            {
                nint dictType = CFDictionaryGetTypeID();
                for (nint i = 0; i < count; i++)
                {
                    IntPtr item = CFArrayGetValueAtIndex(arr, i);
                    if (item == IntPtr.Zero || CFGetTypeID(item) != dictType)
                        continue;
                    long? p = pageKey != IntPtr.Zero ? ReadCfNumber(CFDictionaryGetValue(item, pageKey)) : null;
                    long? u = usageKey != IntPtr.Zero ? ReadCfNumber(CFDictionaryGetValue(item, usageKey)) : null;
                    if (p.HasValue && u.HasValue)
                        pairs.Add(((ushort)Math.Clamp(p.Value, 0, ushort.MaxValue), (ushort)Math.Clamp(u.Value, 0, ushort.MaxValue)));
                }
            }
            finally
            {
                if (pageKey != IntPtr.Zero) CFRelease(pageKey);
                if (usageKey != IntPtr.Zero) CFRelease(usageKey);
            }
        }
        finally
        {
            CFRelease(arr);
        }
        return pairs;
    }

    private static IntPtr _cachedDefaultMode;

    private static IntPtr GetDefaultRunLoopMode()
    {
        if (_cachedDefaultMode != IntPtr.Zero)
            return _cachedDefaultMode;
        if (NativeLibrary.TryLoad(CoreFoundationLib, out var cf) &&
            NativeLibrary.TryGetExport(cf, "kCFRunLoopDefaultMode", out var sym))
        {
            _cachedDefaultMode = Marshal.ReadIntPtr(sym);
        }
        return _cachedDefaultMode;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void IOHIDReportCallback(
        IntPtr context,
        int result,
        IntPtr sender,
        int type,
        uint reportID,
        IntPtr report,
        nint reportLength);

    [DllImport(IoKitLib)] private static extern int IOHIDCheckAccess(int requestType);
    [DllImport(IoKitLib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool IOHIDRequestAccess(int requestType);
    [DllImport(IoKitLib)] private static extern IntPtr IOServiceMatching([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(IoKitLib)] private static extern IntPtr IORegistryEntryIDMatching(ulong entryID);
    [DllImport(IoKitLib)] private static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint existing);
    [DllImport(IoKitLib)] private static extern uint IOServiceGetMatchingService(uint mainPort, IntPtr matching);
    [DllImport(IoKitLib)] private static extern uint IOIteratorNext(uint iterator);
    [DllImport(IoKitLib)] private static extern int IOObjectRelease(uint obj);
    [DllImport(IoKitLib)] private static extern int IORegistryEntryGetRegistryEntryID(uint entry, out ulong entryID);
    [DllImport(IoKitLib)] private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);
    [DllImport(IoKitLib)] private static extern IntPtr IOHIDDeviceCreate(IntPtr allocator, uint service);
    [DllImport(IoKitLib)] private static extern int IOHIDDeviceOpen(IntPtr device, uint options);
    [DllImport(IoKitLib)] private static extern int IOHIDDeviceClose(IntPtr device, uint options);
    [DllImport(IoKitLib)] private static extern void IOHIDDeviceScheduleWithRunLoop(IntPtr device, IntPtr runLoop, IntPtr runLoopMode);
    [DllImport(IoKitLib)] private static extern void IOHIDDeviceUnscheduleFromRunLoop(IntPtr device, IntPtr runLoop, IntPtr runLoopMode);
    [DllImport(IoKitLib)] private static extern void IOHIDDeviceRegisterInputReportCallback(IntPtr device, IntPtr report, nint reportLength, IOHIDReportCallback? callback, IntPtr context);
    [DllImport(IoKitLib)] private static extern int IOHIDDeviceSetReport(IntPtr device, int reportType, nint reportID, ref byte report, nint reportLength);
    [DllImport(IoKitLib)] private static extern int IOHIDDeviceGetReport(IntPtr device, int reportType, nint reportID, [Out] byte[] report, ref nint pReportLength);

    [DllImport(CoreFoundationLib)] private static extern void CFRelease(IntPtr cf);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);
    [DllImport(CoreFoundationLib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr theString, [Out] byte[] buffer, nint bufferSize, uint encoding);
    [DllImport(CoreFoundationLib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, int theType, out long valuePtr);
    [DllImport(CoreFoundationLib)] private static extern nint CFDataGetLength(IntPtr theData);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFDataGetBytePtr(IntPtr theData);
    [DllImport(CoreFoundationLib)] private static extern nint CFArrayGetCount(IntPtr theArray);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr theArray, nint idx);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFDictionaryGetValue(IntPtr theDict, IntPtr key);
    [DllImport(CoreFoundationLib)] private static extern nint CFGetTypeID(IntPtr cf);
    [DllImport(CoreFoundationLib)] private static extern nint CFStringGetTypeID();
    [DllImport(CoreFoundationLib)] private static extern nint CFNumberGetTypeID();
    [DllImport(CoreFoundationLib)] private static extern nint CFDataGetTypeID();
    [DllImport(CoreFoundationLib)] private static extern nint CFArrayGetTypeID();
    [DllImport(CoreFoundationLib)] private static extern nint CFDictionaryGetTypeID();
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFRunLoopGetCurrent();
    [DllImport(CoreFoundationLib)] private static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);
}
