using HidSharp;

namespace GearDeck.Core.Hid;

/// <summary>
/// Safe wrappers around HidSharp metadata calls. Virtual/sensor HID interfaces
/// (e.g. Intel BT VHF / HID VHF drivers) throw DeviceIOException ("Failed to get info.")
/// when queried inside LINQ filters; these helpers isolate faults per interface.
/// </summary>
public static class HidSafe
{
    private static readonly object SnapshotLock = new();
    private static List<HidDevice>? _scanSnapshot;
    private static int _snapshotDepth;

    /// <summary>
    /// 在单轮全量设备扫描期间缓存一次系统 HID 列表快照，避免 20 个 Provider 重复穿透底层枚举。
    /// 支持嵌套调用，引用计数归零后自动释放快照。
    /// </summary>
    public static IDisposable BeginScanSnapshot()
    {
        lock (SnapshotLock)
        {
            if (_snapshotDepth == 0 || _scanSnapshot == null)
            {
                try
                {
                    _scanSnapshot = DeviceList.Local.GetHidDevices()
                        .DistinctBy(d => d.DevicePath)
                        .ToList();
                }
                catch
                {
                    _scanSnapshot = [];
                }
            }
            _snapshotDepth++;
        }
        return new ScanSnapshotScope();
    }

    private sealed class ScanSnapshotScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            lock (SnapshotLock)
            {
                if (_snapshotDepth > 0 && --_snapshotDepth == 0)
                    _scanSnapshot = null;
            }
        }
    }

    public static string ProductName(HidDevice d)
    {
        try { return d.GetProductName() ?? ""; }
        catch { return ""; }
    }

    public static string Manufacturer(HidDevice d)
    {
        try { return d.GetManufacturer() ?? ""; }
        catch { return ""; }
    }

    public static string SerialNumber(HidDevice d)
    {
        try { return d.GetSerialNumber() ?? ""; }
        catch { return ""; }
    }

    public static int MaxInputLen(HidDevice d)
    {
        try { return d.GetMaxInputReportLength(); }
        catch { return 0; }
    }

    public static int MaxOutputLen(HidDevice d)
    {
        try { return d.GetMaxOutputReportLength(); }
        catch { return 0; }
    }

    public static int MaxFeatureLen(HidDevice d)
    {
        try { return d.GetMaxFeatureReportLength(); }
        catch { return 0; }
    }

    public static (ushort UsagePage, ushort Usage) PrimaryUsage(HidDevice d)
    {
        try
        {
            var desc = d.GetReportDescriptor();
            foreach (var item in desc.DeviceItems)
            {
                foreach (uint raw in item.Usages.GetAllValues())
                {
                    ushort page = (ushort)(raw >> 16);
                    ushort usage = (ushort)(raw & 0xFFFF);
                    if (page != 0)
                        return (page, usage);
                }
            }
        }
        catch { }
        return (0, 0);
    }

    public static List<HidDevice> EnumerateDevices()
    {
        List<HidDevice>? snap;
        lock (SnapshotLock)
            snap = _scanSnapshot;
        if (snap != null)
            return snap.ToList();

        try
        {
            return DeviceList.Local.GetHidDevices()
                .DistinctBy(d => d.DevicePath)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static List<HidDevice> EnumerateDevices(int vendorId, int productId)
    {
        List<HidDevice>? snap;
        lock (SnapshotLock)
            snap = _scanSnapshot;
        if (snap != null)
            return snap.Where(d => d.VendorID == vendorId && d.ProductID == productId).ToList();

        try
        {
            return DeviceList.Local.GetHidDevices(vendorId, productId)
                .DistinctBy(d => d.DevicePath)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static List<HidDevice> EnumerateDevicesByVendor(int vendorId)
    {
        List<HidDevice>? snap;
        lock (SnapshotLock)
            snap = _scanSnapshot;
        if (snap != null)
            return snap.Where(d => d.VendorID == vendorId).ToList();

        try
        {
            return DeviceList.Local.GetHidDevices(vendorId)
                .DistinctBy(d => d.DevicePath)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
