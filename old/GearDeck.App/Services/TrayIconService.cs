using System.Runtime.InteropServices;
using GearDeck.Core;

namespace GearDeck.App.Services;

/// <summary>
/// 原生 Win32 系统托盘图标与右键菜单服务：
/// 支持点击关闭主窗口后常驻托盘、左键单击/双击恢复面板、右键快捷菜单以及 Explorer 重启后自动重建图标。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int WmUser = 0x0400;
    private const uint WmTrayIcon = WmUser + 0x101;
    private const uint WmRetryTray = WmUser + 0x102;
    private const uint WmCommand = 0x0111;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmDestroy = 0x0002;
    private static readonly IntPtr PbtApmResumeSuspend = new(0x0007);
    private static readonly IntPtr PbtApmResumeAutomatic = new(0x0012);
    private const uint WmNull = 0x0000;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;

    private const uint CmdOpenMain = 1001;
    private const uint CmdOpenGames = 1002;
    private const uint CmdToggleWidget = 1003;
    private const uint CmdRefreshDevices = 1004;
    private const uint CmdOpenMacros = 1005;
    private const uint CmdExit = 1009;

    private readonly Action _onOpenMain;
    private readonly Action _onOpenGames;
    private readonly Action _onOpenMacros;
    private readonly Action _onToggleWidget;
    private readonly Action _onRefreshDevices;
    private readonly Action _onExit;
    private readonly Action? _onSystemResume;

    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _iconOwned;
    private uint _taskbarCreatedMsg;
    private WndProcDelegate? _wndProc;
    private volatile bool _disposed;
    private volatile bool _trayAdded;

    public TrayIconService(
        Action onOpenMain,
        Action onOpenGames,
        Action onOpenMacros,
        Action onToggleWidget,
        Action onRefreshDevices,
        Action onExit,
        Action? onSystemResume = null)
    {
        _onOpenMain = onOpenMain;
        _onOpenGames = onOpenGames;
        _onOpenMacros = onOpenMacros;
        _onToggleWidget = onToggleWidget;
        _onRefreshDevices = onRefreshDevices;
        _onExit = onExit;
        _onSystemResume = onSystemResume;

        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "GearDeck.TrayIcon",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(1500);
    }

    public void UpdateTooltip(string tip)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        var nid = CreateIconData();
        nid.uFlags = NifTip;
        nid.szTip = Truncate(tip, 120);
        Shell_NotifyIconW(NimModify, ref nid);
    }

    public void ShowBalloon(string title, string body)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        var nid = CreateIconData();
        nid.uFlags = NifInfo;
        nid.szInfoTitle = Truncate(title, 60);
        nid.szInfo = Truncate(body, 250);
        nid.dwInfoFlags = 1; // NIIF_INFO
        Shell_NotifyIconW(NimModify, ref nid);
    }

    private void RunMessageLoop()
    {
        try
        {
            string className = $"GearDeckTray_{Environment.ProcessId}";
            _wndProc = WindowProc;
            _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = className,
            };
            RegisterClassExW(ref wc);

            _hwnd = CreateWindowExW(
                0, className, "GearDeckTrayHost", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            LoadTrayIcon();
            AddOrRestoreTrayIcon();
        }
        catch (Exception ex)
        {
            Diag.Log($"TrayIcon 初始化异常: {ex.Message}");
        }
        finally
        {
            _ready.Set();
        }

        if (_hwnd == IntPtr.Zero) return;

        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private void LoadTrayIcon()
    {
        string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(icoPath))
        {
            const uint IMAGE_ICON = 1;
            const uint LR_LOADFROMFILE = 0x0010;
            const uint LR_DEFAULTSIZE = 0x0040;
            _hIcon = LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            _iconOwned = _hIcon != IntPtr.Zero;
        }
        if (_hIcon == IntPtr.Zero)
        {
            _hIcon = LoadIconW(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
            _iconOwned = false;
        }
    }

    private NOTIFYICONDATAW CreateIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = WmTrayIcon,
        hIcon = _hIcon,
        szTip = "GearDeck · 外设调节与游戏配置",
        szInfo = "",
        szInfoTitle = "",
    };

    private void AddOrRestoreTrayIcon(bool scheduleRetry = true)
    {
        if (_hwnd == IntPtr.Zero || _disposed) return;
        var nid = CreateIconData();
        if (Shell_NotifyIconW(NimAdd, ref nid))
        {
            _trayAdded = true;
            return;
        }

        // NIM_ADD 失败时图标可能仍残留（Explorer 重启/同名同 ID），先用 NIM_MODIFY 接管
        if (Shell_NotifyIconW(NimModify, ref nid))
        {
            _trayAdded = true;
            return;
        }

        // MODIFY 也失败则清理可能残留的同名/同 ID 图标，再重试一次 ADD
        Shell_NotifyIconW(NimDelete, ref nid);
        if (Shell_NotifyIconW(NimAdd, ref nid))
        {
            _trayAdded = true;
            return;
        }

        _trayAdded = false;
        int err = Marshal.GetLastWin32Error();
        Diag.Log($"Shell_NotifyIconW(NIM_ADD) 失败 err={err}");
        if (scheduleRetry)
            ScheduleTrayRetry();
    }

    private void ScheduleTrayRetry()
    {
        var hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || _disposed) return;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(1500);
                if (_disposed || _trayAdded) return;
                PostMessageW(hwnd, WmRetryTray, IntPtr.Zero, IntPtr.Zero);
            }
        });
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            _trayAdded = false;
            AddOrRestoreTrayIcon(scheduleRetry: true);
            return IntPtr.Zero;
        }

        if (msg == WmRetryTray)
        {
            if (!_trayAdded)
                AddOrRestoreTrayIcon(scheduleRetry: false);
            return IntPtr.Zero;
        }

        if (msg == WmTrayIcon)
        {
            uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
            if (ev is WmLButtonUp or WmLButtonDblClk)
            {
                SafeInvoke(_onOpenMain);
                return IntPtr.Zero;
            }
            if (ev is WmRButtonUp or WmContextMenu)
            {
                ShowContextMenu(hwnd);
                return IntPtr.Zero;
            }
        }
        else if (msg == WmCommand)
        {
            uint cmd = (uint)(wParam.ToInt64() & 0xFFFF);
            switch (cmd)
            {
                case CmdOpenMain: SafeInvoke(_onOpenMain); break;
                case CmdOpenGames: SafeInvoke(_onOpenGames); break;
                case CmdOpenMacros: SafeInvoke(_onOpenMacros); break;
                case CmdToggleWidget: SafeInvoke(_onToggleWidget); break;
                case CmdRefreshDevices: SafeInvoke(_onRefreshDevices); break;
                case CmdExit: SafeInvoke(_onExit); break;
            }
            return IntPtr.Zero;
        }
        else if (msg == WmPowerBroadcast && (wParam == PbtApmResumeAutomatic || wParam == PbtApmResumeSuspend))
        {
            if (_onSystemResume != null)
                SafeInvoke(_onSystemResume);
            return (IntPtr)1;
        }
        else if (msg == WmDestroy)
        {
            RemoveTrayIcon();
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void SafeInvoke(Action action)
    {
        try { action(); }
        catch { }
    }

    private void ShowContextMenu(IntPtr hwnd)
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            const uint MF_STRING = 0x0000;
            const uint MF_SEPARATOR = 0x0800;
            AppendMenuW(menu, MF_STRING, CmdOpenMain, "打开主面板 (GearDeck)");
            AppendMenuW(menu, MF_STRING, CmdOpenGames, "游戏与进程配置切换");
            AppendMenuW(menu, MF_STRING, CmdOpenMacros, "鼠标宏中心");
            AppendMenuW(menu, MF_STRING, CmdToggleWidget,
                App.Settings?.Current.WidgetVisible == true ? "隐藏任务栏贴片" : "显示任务栏贴片");
            AppendMenuW(menu, MF_STRING, CmdRefreshDevices, "立即刷新外设状态");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CmdExit, "彻底退出 GearDeck");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(hwnd);
            const uint TPM_RIGHTBUTTON = 0x0002;
            const uint TPM_BOTTOMALIGN = 0x0020;
            TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, hwnd, IntPtr.Zero);
            PostMessageW(hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void RemoveTrayIcon()
    {
        if (_hwnd == IntPtr.Zero) return;
        var nid = CreateIconData();
        Shell_NotifyIconW(NimDelete, ref nid);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                RemoveTrayIcon();
                PostMessageW(_hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            }
            if (_iconOwned && _hIcon != IntPtr.Zero)
                DestroyIcon(_hIcon);
        }
        catch { }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
