using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GearDeck.Core.Hid;

/// <summary>
/// 直接 Win32 HID feature 通道。部分厂商设备（雷蛇、怒喵等）只允许 GENERIC_WRITE 或 0 权限打开，
/// HidSharp 的读写模式会 ACCESS_DENIED；支持指定 Report ID 读取 Feature Report。
/// </summary>
public sealed class RawHidFeature : IDisposable
{
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;

    private readonly SafeFileHandle _handle;

    private RawHidFeature(SafeFileHandle handle) => _handle = handle;

    public static RawHidFeature? TryOpen(string devicePath)
    {
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

    public bool SetFeature(byte[] data) => HidD_SetFeature(_handle, data, (uint)data.Length);

    public byte[]? GetFeature(int length, byte reportId = 0)
    {
        if (length <= 0) return null;
        var buffer = new byte[length];
        buffer[0] = reportId;
        return HidD_GetFeature(_handle, buffer, (uint)buffer.Length) ? buffer : null;
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] buffer, uint length);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buffer, uint length);
}
