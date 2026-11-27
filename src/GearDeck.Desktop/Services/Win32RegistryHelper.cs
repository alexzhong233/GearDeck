using System.Runtime.InteropServices;
using System.Text;

namespace GearDeck.Desktop.Services;

/// <summary>注册表根键（刻意不引用 Microsoft.Win32.Registry）。</summary>
public enum Win32RegistryRoot
{
    HKCU,
    HKLM,
}

/// <summary>
/// 纯 advapi32.dll P/Invoke 注册表访问器；所有公开调用均使用 OperatingSystem.IsWindows() 守卫，
/// 非 Windows 平台返回安全默认值而不触发 DllNotFoundException。
/// </summary>
public static class Win32RegistryHelper
{
    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorNoMoreItems = 259;

    private const uint RegSz = 1;
    private const uint RegBinary = 3;
    private const uint RegDword = 4;

    private const uint KeyRead = 0x00020019;
    private const uint KeyWrite = 0x00020006;
    private const uint KeyAllAccess = 0x000F003F;

    private static readonly IntPtr HKeyCurrentUser = new(unchecked((int)0x80000001));
    private static readonly IntPtr HKeyLocalMachine = new(unchecked((int)0x80000002));

    private static IntPtr RootHandle(Win32RegistryRoot root)
        => root == Win32RegistryRoot.HKLM ? HKeyLocalMachine : HKeyCurrentUser;

    public static string? ReadString(Win32RegistryRoot root, string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;

        IntPtr hKey = IntPtr.Zero;
        try
        {
            if (RegOpenKeyExW(RootHandle(root), subKey, 0, KeyRead, out hKey) != ErrorSuccess)
                return null;

            uint type;
            uint size = 0;
            if (RegQueryValueExW(hKey, valueName, IntPtr.Zero, out type, null, ref size) != ErrorSuccess)
                return null;
            if (size == 0 || size > 1024 * 1024)
                return null;

            var buffer = new byte[size];
            if (RegQueryValueExW(hKey, valueName, IntPtr.Zero, out type, buffer, ref size) != ErrorSuccess)
                return null;

            return Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0');
        }
        finally
        {
            if (hKey != IntPtr.Zero)
                RegCloseKey(hKey);
        }
    }

    public static bool WriteString(Win32RegistryRoot root, string subKey, string valueName, string value)
    {
        if (!OperatingSystem.IsWindows()) return false;

        IntPtr hKey = IntPtr.Zero;
        try
        {
            if (RegCreateKeyExW(RootHandle(root), subKey, 0, null, 0, KeyAllAccess,
                    IntPtr.Zero, out hKey, out _) != ErrorSuccess)
                return false;

            byte[] data = Encoding.Unicode.GetBytes(value + "\0");
            return RegSetValueExW(hKey, valueName, 0, RegSz, data, (uint)data.Length) == ErrorSuccess;
        }
        finally
        {
            if (hKey != IntPtr.Zero)
                RegCloseKey(hKey);
        }
    }

    public static bool DeleteValue(Win32RegistryRoot root, string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows()) return true;

        IntPtr hKey = IntPtr.Zero;
        try
        {
            int open = RegOpenKeyExW(RootHandle(root), subKey, 0, KeyWrite, out hKey);
            if (open == ErrorFileNotFound)
                return true;
            if (open != ErrorSuccess)
                return false;

            int result = RegDeleteValueW(hKey, valueName);
            return result == ErrorSuccess || result == ErrorFileNotFound;
        }
        finally
        {
            if (hKey != IntPtr.Zero)
                RegCloseKey(hKey);
        }
    }

    public static IReadOnlyList<string> GetSubKeyNames(Win32RegistryRoot root, string subKey)
    {
        if (!OperatingSystem.IsWindows()) return [];

        var names = new List<string>();
        IntPtr hKey = IntPtr.Zero;
        try
        {
            if (RegOpenKeyExW(RootHandle(root), subKey, 0, KeyRead, out hKey) != ErrorSuccess)
                return names;

            for (uint index = 0; ; index++)
            {
                var buffer = new StringBuilder(256);
                uint capacity = (uint)buffer.Capacity;
                int result = RegEnumKeyExW(hKey, index, buffer, ref capacity,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out _);
                if (result == ErrorNoMoreItems)
                    break;
                if (result != ErrorSuccess)
                    break;
                names.Add(buffer.ToString());
            }
        }
        finally
        {
            if (hKey != IntPtr.Zero)
                RegCloseKey(hKey);
        }
        return names;
    }

    /// <summary>读取 REG_DWORD (32位整数) 值。</summary>
    public static int? ReadInt32(Win32RegistryRoot root, string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;

        IntPtr hKey = IntPtr.Zero;
        try
        {
            if (RegOpenKeyExW(RootHandle(root), subKey, 0, KeyRead, out hKey) != ErrorSuccess)
                return null;

            uint size = 4;
            var buffer = new byte[4];
            if (RegQueryValueExW(hKey, valueName, IntPtr.Zero, out uint type, buffer, ref size) != ErrorSuccess)
                return null;
            if (type != RegDword || size < 4)
                return null;

            return BitConverter.ToInt32(buffer, 0);
        }
        finally
        {
            if (hKey != IntPtr.Zero)
                RegCloseKey(hKey);
        }
    }

    /// <summary>读取 REG_BINARY 值，用于开机自启的 StartupApproved 状态检测。</summary>
    public static byte[]? ReadBinary(Win32RegistryRoot root, string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;

        IntPtr hKey = IntPtr.Zero;
        try
        {
            if (RegOpenKeyExW(RootHandle(root), subKey, 0, KeyRead, out hKey) != ErrorSuccess)
                return null;

            uint type;
            uint size = 0;
            if (RegQueryValueExW(hKey, valueName, IntPtr.Zero, out type, null, ref size) != ErrorSuccess)
                return null;
            if (size == 0 || size > 1024 * 1024)
                return null;

            var buffer = new byte[size];
            if (RegQueryValueExW(hKey, valueName, IntPtr.Zero, out type, buffer, ref size) != ErrorSuccess)
                return null;
            return buffer;
        }
        finally
        {
            if (hKey != IntPtr.Zero)
                RegCloseKey(hKey);
        }
    }

    /// <summary>写入 REG_BINARY 值，用于恢复开机自启的 StartupApproved 状态。</summary>
    public static bool WriteBinary(Win32RegistryRoot root, string subKey, string valueName, byte[] value)
    {
        if (!OperatingSystem.IsWindows()) return false;

        IntPtr hKey = IntPtr.Zero;
        try
        {
            if (RegCreateKeyExW(RootHandle(root), subKey, 0, null, 0, KeyAllAccess,
                    IntPtr.Zero, out hKey, out _) != ErrorSuccess)
                return false;

            return RegSetValueExW(hKey, valueName, 0, RegBinary, value, (uint)value.Length) == ErrorSuccess;
        }
        finally
        {
            if (hKey != IntPtr.Zero)
                RegCloseKey(hKey);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint DwLowDateTime;
        public uint DwHighDateTime;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegOpenKeyExW")]
    private static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegCreateKeyExW")]
    private static extern int RegCreateKeyExW(IntPtr hKey, string lpSubKey, uint reserved, string? lpClass,
        uint dwOptions, uint samDesired, IntPtr lpSecurityAttributes, out IntPtr phkResult, out uint lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegCloseKey")]
    private static extern int RegCloseKey(IntPtr hKey);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegQueryValueExW")]
    private static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved,
        out uint lpType, [Out] byte[]? lpData, ref uint lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegSetValueExW")]
    private static extern int RegSetValueExW(IntPtr hKey, string lpValueName, uint reserved,
        uint dwType, byte[] lpData, uint cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegDeleteValueW")]
    private static extern int RegDeleteValueW(IntPtr hKey, string lpValueName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegEnumKeyExW")]
    private static extern int RegEnumKeyExW(IntPtr hKey, uint dwIndex, StringBuilder lpName,
        ref uint lpcchName, IntPtr lpReserved, IntPtr lpClass, IntPtr lpcchClass, out FileTime lpftLastWriteTime);
}
