using System.Runtime.InteropServices;

namespace GearDeck.Core.Services;

public sealed record MacAppInfo(int Pid, string LocalizedName, string BundleId);

/// <summary>
/// 原生 macOS NSWorkspace 前台与运行应用查询（零 osascript 子进程，零自动化弹窗）。
/// 非 macOS 平台全部方法安全空返回。
/// </summary>
public static class MacWorkspaceHelper
{
    private const string ObjCLib = "/usr/lib/libobjc.A.dylib";
    private const string AppKitLib = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private static int _appKitLoaded;

    private static void EnsureAppKitLoaded()
    {
        if (Interlocked.CompareExchange(ref _appKitLoaded, 1, 0) == 0 && OperatingSystem.IsMacOS())
        {
            NativeLibrary.TryLoad(AppKitLib, out _);
        }
    }

    public static MacAppInfo? GetFrontmostApp()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            EnsureAppKitLoaded();
            IntPtr ws = SharedWorkspace();
            if (ws == IntPtr.Zero) return null;
            IntPtr app = objc_msgSend(ws, sel_registerName("frontmostApplication"));
            return ReadAppInfo(app);
        }
        catch
        {
            return null;
        }
    }

    public static List<MacAppInfo> GetRunningApps()
    {
        var list = new List<MacAppInfo>();
        if (!OperatingSystem.IsMacOS()) return list;
        try
        {
            EnsureAppKitLoaded();
            IntPtr ws = SharedWorkspace();
            if (ws == IntPtr.Zero) return list;
            IntPtr arr = objc_msgSend(ws, sel_registerName("runningApplications"));
            if (arr == IntPtr.Zero) return list;

            long count = objc_msgSend_nint(arr, sel_registerName("count"));
            IntPtr selAt = sel_registerName("objectAtIndex:");
            for (long i = 0; i < count && i < 512; i++)
            {
                IntPtr app = objc_msgSend_nint_arg(arr, selAt, (nint)i);
                if (ReadAppInfo(app) is { } info)
                    list.Add(info);
            }
        }
        catch { }
        return list;
    }

    public static bool IsProcessAlive(int pid)
    {
        if (!OperatingSystem.IsMacOS() || pid <= 0) return false;
        return sys_kill(pid, 0) == 0;
    }

    private static IntPtr SharedWorkspace()
    {
        IntPtr cls = objc_getClass("NSWorkspace");
        if (cls == IntPtr.Zero) return IntPtr.Zero;
        return objc_msgSend(cls, sel_registerName("sharedWorkspace"));
    }

    private static MacAppInfo? ReadAppInfo(IntPtr app)
    {
        if (app == IntPtr.Zero) return null;
        int pid = objc_msgSend_int(app, sel_registerName("processIdentifier"));
        string name = ReadNsString(objc_msgSend(app, sel_registerName("localizedName")));
        string bundleId = ReadNsString(objc_msgSend(app, sel_registerName("bundleIdentifier")));
        if (pid <= 0 && string.IsNullOrEmpty(name) && string.IsNullOrEmpty(bundleId))
            return null;
        return new MacAppInfo(pid, name, bundleId);
    }

    private static string ReadNsString(IntPtr nsStr)
    {
        if (nsStr == IntPtr.Zero) return string.Empty;
        IntPtr utf8 = objc_msgSend(nsStr, sel_registerName("UTF8String"));
        return utf8 == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(utf8) ?? string.Empty);
    }

    [DllImport(ObjCLib)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjCLib)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern int objc_msgSend_int(IntPtr receiver, IntPtr selector);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_nint(IntPtr receiver, IntPtr selector);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend_nint_arg(IntPtr receiver, IntPtr selector, nint index);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "kill")] private static extern int sys_kill(int pid, int sig);
}
