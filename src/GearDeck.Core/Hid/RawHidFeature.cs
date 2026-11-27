using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace GearDeck.Core.Hid;

/// <summary>
/// 跨平台 HID Feature Report 通道：
/// - Windows：直接 Win32 CreateFileW (GENERIC_WRITE / 0) + HidD_SetFeature/GetFeature，兼容只允许轻量打开的厂商设备；
/// - macOS / 非 Windows：自动回退 HidSharp HidStream (IOKit SetFeature/GetFeature)。
/// </summary>
public sealed class RawHidFeature : IDisposable
{
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;

    private readonly SafeFileHandle? _handle;
    private readonly HidStream? _stream;

    private RawHidFeature(SafeFileHandle handle) => _handle = handle;
    private RawHidFeature(HidStream stream) => _stream = stream;

    public static RawHidFeature? TryOpen(string devicePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var dev = HidSafe.EnumerateDevices()
                    .FirstOrDefault(d => string.Equals(d.DevicePath, devicePath, StringComparison.Ordinal));
                if (dev != null && dev.TryOpen(out var stream) && stream != null)
                    return new RawHidFeature(stream);
            }
            catch { }
            return null;
        }

        var handle = CreateFileW(devicePath, GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = CreateFileW(devicePath, 0, FileShareRead | FileShareWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        }
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return new RawHidFeature(handle);
    }

    public bool SetFeature(byte[] data)
    {
        if (_handle != null)
            return HidD_SetFeature(_handle, data, (uint)data.Length);
        if (_stream != null)
        {
            try { _stream.SetFeature(data); return true; }
            catch { return false; }
        }
        return false;
    }

    public byte[]? GetFeature(int length, byte reportId = 0)
    {
        if (length <= 0) return null;
        var buffer = new byte[length];
        buffer[0] = reportId;
        if (_handle != null)
            return HidD_GetFeature(_handle, buffer, (uint)buffer.Length) ? buffer : null;
        if (_stream != null)
        {
            try { _stream.GetFeature(buffer); return buffer; }
            catch { return null; }
        }
        return null;
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _stream?.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] buffer, uint length);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buffer, uint length);
}
