using HidSharp;

namespace GearDeck.Core.Hid;

/// <summary>
/// Safe wrappers around HidSharp metadata calls. Virtual/sensor HID interfaces
/// (e.g. Intel BT VHF / HID VHF drivers) throw DeviceIOException ("Failed to get info.")
/// when queried inside LINQ filters; these helpers isolate faults per interface.
/// </summary>
public static class HidSafe
{
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
