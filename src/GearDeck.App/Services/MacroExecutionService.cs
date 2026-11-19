using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using GearDeck.Core;
using GearDeck.Core.Models;

namespace GearDeck.App.Services;

/// <summary>
/// 雷蛇雷云风格鼠标宏回放与软件触发引擎：
/// - 支持四种触发模式：单次触发 (PlayOnce)、按住触发 (WhileHeld)、切换触发 (+Esc 停止)、指定次数 (RepeatCount)；
/// - 通过 RawInput (HWND_MESSAGE) 识别当前点击来源于哪一只实体鼠标（按 VID:PID 精确匹配并移除休眠误过滤）；
/// - SendInput 写入硬件 ScanCode 以兼容 DirectInput / 游戏窗口，并维护不可变快照防止 Hook 回调超时；
/// - 任何取消/结束都会严格清理已按下未松开的键/鼠标按键，彻底杜绝卡键。
/// </summary>
public sealed class MacroExecutionService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmInput = 0x00FF;
    private const int LlhfInjected = 0x00000001;
    private const int LlkhfInjected = 0x00000010;
    private const int VkEscape = 0x1B;

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfExtendedKey = 0x0001;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfScancode = 0x0008;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightdown = 0x0008;
    private const uint MouseeventfRightup = 0x0010;
    private const uint MouseeventfMiddledown = 0x0020;
    private const uint MouseeventfMiddleup = 0x0040;
    private const uint MouseeventfXdown = 0x0080;
    private const uint MouseeventfXup = 0x0100;

    private const uint RidevInputsink = 0x00000100;
    private const uint RidInput = 0x10000003;
    private const uint RidiDevicename = 0x20000007;
    private const uint RimTypeMouse = 0;
    private const int GwlpWndProc = -4;

    private sealed record BindingSnapshot(
        Dictionary<string, string> Bindings,
        Dictionary<string, OnboardMacroDefinition> MacrosById,
        List<(string Id, ushort? Vid, ushort? Pid)> Mice);

    private readonly object _playLock = new();
    private CancellationTokenSource? _activePlayCts;
    private string? _activeToggleTrigger;
    private string? _activeHeldTrigger;
    private volatile bool _isPlaying;

    private volatile BindingSnapshot _snapshot = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, OnboardMacroDefinition>(StringComparer.OrdinalIgnoreCase),
        []);

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _mouseHookHandle;
    private IntPtr _kbdHookHandle;
    private IntPtr _rawMsgHwnd;
    private IntPtr _origWndProc;
    private HookProc? _mouseHookProc;
    private HookProc? _kbdHookProc;
    private WndProcDelegate? _rawWndProc;
    private readonly Dictionary<IntPtr, (ushort Vid, ushort Pid)> _rawHandleCache = [];
    private int _lastRawVid;
    private int _lastRawPid;
    private long _lastRawTickMs;
    private bool _subscribedDevices;
    private volatile bool _disposed;

    public static IReadOnlyList<(string Key, string Label)> SoftwareTriggerButtons { get; } =
    [
        ("XButton1", "鼠标侧键 4 (后退键 XButton1)"),
        ("XButton2", "鼠标侧键 5 (前进键 XButton2)"),
        ("Middle", "鼠标中键 (滚轮按下 Middle)"),
    ];

    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool RestartAsAdmin()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return false;
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--page=macros",
            });
            App.ExitApplication();
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log($"管理员重启取消或失败: {ex.Message}");
            return false;
        }
    }

    public void SyncHookState()
    {
        if (_disposed) return;
        if (!_subscribedDevices && App.Devices != null)
        {
            _subscribedDevices = true;
            App.Devices.DevicesUpdated += _ => RefreshSnapshotOnly();
        }

        RefreshSnapshotOnly();
        bool needHook = _snapshot.Bindings.Count > 0;
        if (needHook && _hookThread == null)
            StartHook();
        else if (!needHook && _hookThread != null)
            StopHook();
    }

    private void RefreshSnapshotOnly()
    {
        var rawBindings = App.Settings.Current.LocalButtonMacroBindings;
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in rawBindings)
        {
            if (!string.IsNullOrWhiteSpace(kv.Value))
                bindings[kv.Key] = kv.Value;
        }

        var macros = App.Settings.Current.Macros
            .Where(m => !string.IsNullOrWhiteSpace(m.Id) && m.Steps.Count > 0)
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => CloneMacro(g.First()), StringComparer.OrdinalIgnoreCase);

        // 移除 !d.IsSleeping 限制：无线鼠标按下触发键的瞬间硬件已唤醒，不能因 UI 轮询缓存仍为 IsSleeping 而丢触发
        var mice = App.LastDevices
            .Where(d => d.Kind == DeviceKind.Mouse && d.Connected)
            .Select(d => (d.Id, d.VendorId, d.ProductId))
            .ToList();

        _snapshot = new BindingSnapshot(bindings, macros, mice);
    }

    private static OnboardMacroDefinition CloneMacro(OnboardMacroDefinition src) => new()
    {
        Id = src.Id,
        Name = src.Name,
        LoopMode = src.LoopMode,
        RepeatCount = src.RepeatCount,
        Steps = src.Steps.Select(s => new MacroStep(s.KeyCode, s.IsPress, s.DelayMs)).ToList(),
    };

    public async Task PlayMacroAsync(OnboardMacroDefinition macro, CancellationToken externalCt = default)
    {
        if (macro.Steps.Count == 0) return;

        CancellationTokenSource cts;
        lock (_playLock)
        {
            _activePlayCts?.Cancel();
            _activePlayCts?.Dispose();
            _activePlayCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            cts = _activePlayCts;
            _isPlaying = true;
        }

        var pressedCodes = new List<byte>();
        try
        {
            int maxLoops = macro.LoopMode switch
            {
                MacroLoopMode.PlayOnce => 1,
                MacroLoopMode.RepeatCount => Math.Clamp(macro.RepeatCount, 1, 100),
                MacroLoopMode.WhileHeld or MacroLoopMode.ToggleUntilKey => 10000,
                _ => 1,
            };

            for (int loop = 0; loop < maxLoops; loop++)
            {
                if (cts.Token.IsCancellationRequested) break;
                foreach (var step in macro.Steps)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    SendSingleStep(step.KeyCode, step.IsPress);
                    if (step.IsPress)
                    {
                        if (!pressedCodes.Contains(step.KeyCode))
                            pressedCodes.Add(step.KeyCode);
                    }
                    else
                    {
                        pressedCodes.Remove(step.KeyCode);
                    }

                    if (step.DelayMs > 0)
                        await Task.Delay(Math.Clamp(step.DelayMs, 1, 10000), cts.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Diag.Log($"Macro 播放异常: {ex.Message}");
        }
        finally
        {
            for (int i = pressedCodes.Count - 1; i >= 0; i--)
                SendSingleStep(pressedCodes[i], isPress: false);

            lock (_playLock)
            {
                if (ReferenceEquals(_activePlayCts, cts))
                {
                    _isPlaying = false;
                    _activeToggleTrigger = null;
                    _activeHeldTrigger = null;
                }
            }
        }
    }

    public void CancelPlayback()
    {
        lock (_playLock)
        {
            _activePlayCts?.Cancel();
            _activeToggleTrigger = null;
            _activeHeldTrigger = null;
            _isPlaying = false;
        }
    }

    private void StartHook()
    {
        _mouseHookProc ??= MouseHookCallback;
        _kbdHookProc ??= KeyboardHookCallback;
        _rawWndProc ??= RawInputWndProc;

        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            CreateRawInputMessageWindow();

            _mouseHookHandle = SetWindowsHookExW(WhMouseLl, _mouseHookProc, IntPtr.Zero, 0);
            _kbdHookHandle = SetWindowsHookExW(WhKeyboardLl, _kbdHookProc, IntPtr.Zero, 0);
            if (_mouseHookHandle == IntPtr.Zero)
                Diag.Log($"SetWindowsHookExW(WH_MOUSE_LL) 失败: err={Marshal.GetLastWin32Error()}");

            while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            if (_mouseHookHandle != IntPtr.Zero) UnhookWindowsHookEx(_mouseHookHandle);
            if (_kbdHookHandle != IntPtr.Zero) UnhookWindowsHookEx(_kbdHookHandle);
            if (_rawMsgHwnd != IntPtr.Zero) DestroyWindow(_rawMsgHwnd);
            _mouseHookHandle = IntPtr.Zero;
            _kbdHookHandle = IntPtr.Zero;
            _rawMsgHwnd = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "GearDeck.MouseMacroHook",
        };
        _hookThread.Start();
    }

    private void CreateRawInputMessageWindow()
    {
        try
        {
            _rawMsgHwnd = CreateWindowExW(
                0, "STATIC", "GearDeckRawInput", 0,
                0, 0, 0, 0,
                new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_rawMsgHwnd == IntPtr.Zero) return;

            _origWndProc = SetWindowLongPtrW(_rawMsgHwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_rawWndProc!));
            var rid = new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x02,
                dwFlags = RidevInputsink,
                hwndTarget = _rawMsgHwnd,
            };
            RegisterRawInputDevices([rid], 1, Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch (Exception ex)
        {
            Diag.Log($"RawInput 初始化失败: {ex.Message}");
        }
    }

    private IntPtr RawInputWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmInput)
        {
            HandleRawInput(lParam);
        }
        return _origWndProc != IntPtr.Zero
            ? CallWindowProcW(_origWndProc, hwnd, msg, wParam, lParam)
            : DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        try
        {
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            if (GetRawInputData(hRawInput, RidInput, IntPtr.Zero, ref size, headerSize) != 0 || size < headerSize)
                return;

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(hRawInput, RidInput, buf, ref size, headerSize) != size)
                    return;

                var hdr = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                if (hdr.dwType != RimTypeMouse || hdr.hDevice == IntPtr.Zero)
                    return;

                if (!_rawHandleCache.TryGetValue(hdr.hDevice, out var vidPid))
                {
                    vidPid = QueryDeviceVidPid(hdr.hDevice);
                    _rawHandleCache[hdr.hDevice] = vidPid;
                }

                if (vidPid.Vid != 0)
                {
                    Volatile.Write(ref _lastRawVid, vidPid.Vid);
                    Volatile.Write(ref _lastRawPid, vidPid.Pid);
                    Volatile.Write(ref _lastRawTickMs, Environment.TickCount64);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
        }
    }

    private static (ushort Vid, ushort Pid) QueryDeviceVidPid(IntPtr hDevice)
    {
        uint chars = 0;
        if (GetRawInputDeviceInfoW(hDevice, RidiDevicename, IntPtr.Zero, ref chars) != 0 || chars == 0)
            return (0, 0);

        var sb = new StringBuilder((int)chars + 1);
        if (GetRawInputDeviceInfoW(hDevice, RidiDevicename, sb, ref chars) == uint.MaxValue)
            return (0, 0);

        string name = sb.ToString();
        ushort vid = ParseHexTag(name, "VID_");
        ushort pid = ParseHexTag(name, "PID_");
        return (vid, pid);
    }

    private static ushort ParseHexTag(string s, string tag)
    {
        int idx = s.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        if (idx < 0 || idx + tag.Length + 4 > s.Length) return 0;
        return ushort.TryParse(s.AsSpan(idx + tag.Length, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort v)
            ? v
            : (ushort)0;
    }

    private void StopHook()
    {
        if (_hookThreadId != 0)
            PostThreadMessageW(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _hookThread = null;
        _hookThreadId = 0;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WmKeyDown or WmSysKeyDown)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((info.flags & LlkhfInjected) == 0 && info.vkCode == VkEscape)
                {
                    if (_isPlaying && !string.IsNullOrEmpty(_activeToggleTrigger))
                        CancelPlayback();
                }
            }
        }
        return CallNextHookEx(_kbdHookHandle, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & LlhfInjected) == 0)
            {
                int msg = wParam.ToInt32();
                string? triggerKey = msg switch
                {
                    WmMButtonDown or WmMButtonUp => "Middle",
                    WmXButtonDown or WmXButtonUp => (info.mouseData >> 16) switch
                    {
                        1 => "XButton1",
                        2 => "XButton2",
                        _ => null,
                    },
                    _ => null,
                };

                if (triggerKey != null)
                {
                    bool isDown = msg is WmMButtonDown or WmXButtonDown;
                    var macro = ResolveBoundMacro(triggerKey);
                    if (macro != null)
                    {
                        if (isDown)
                        {
                            HandleMacroTriggerDown(triggerKey, macro);
                        }
                        else if (macro.LoopMode == MacroLoopMode.WhileHeld ||
                                 string.Equals(_activeHeldTrigger, triggerKey, StringComparison.OrdinalIgnoreCase))
                        {
                            CancelPlayback();
                        }
                        return (IntPtr)1;
                    }
                    if (!isDown && string.Equals(_activeHeldTrigger, triggerKey, StringComparison.OrdinalIgnoreCase))
                    {
                        CancelPlayback();
                    }
                }
            }
        }
        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private OnboardMacroDefinition? ResolveBoundMacro(string triggerKey)
    {
        var snap = _snapshot;

        // 1. 若最近 300ms 内 RawInput 捕捉到了具体鼠标的 VID:PID，优先精确匹配该鼠绑定的宏
        long ageMs = Environment.TickCount64 - Volatile.Read(ref _lastRawTickMs);
        if (ageMs is >= 0 and <= 300)
        {
            ushort rawVid = (ushort)Volatile.Read(ref _lastRawVid);
            ushort rawPid = (ushort)Volatile.Read(ref _lastRawPid);
            if (rawVid != 0)
            {
                foreach (var m in snap.Mice)
                {
                    if (m.Vid == rawVid && (rawPid == 0 || m.Pid == rawPid) &&
                        snap.Bindings.TryGetValue($"{m.Id}|{triggerKey}", out string? exactMacroId) &&
                        snap.MacrosById.TryGetValue(exactMacroId, out var exactMacro))
                    {
                        return exactMacro;
                    }
                }
            }
        }

        // 2. 任意已接入鼠标（含刚按侧键唤醒、尚未等到下一轮轮询的无线鼠标）
        foreach (var m in snap.Mice)
        {
            if (snap.Bindings.TryGetValue($"{m.Id}|{triggerKey}", out string? scopedId) &&
                snap.MacrosById.TryGetValue(scopedId, out var scopedMacro))
            {
                return scopedMacro;
            }
        }

        // 3. 兜底查找任何带 "|triggerKey" 或全局 "triggerKey" 的绑定
        if (snap.Bindings.TryGetValue(triggerKey, out string? globalId) &&
            snap.MacrosById.TryGetValue(globalId, out var globalMacro))
        {
            return globalMacro;
        }

        return null;
    }

    private void HandleMacroTriggerDown(string triggerKey, OnboardMacroDefinition macro)
    {
        if (macro.LoopMode == MacroLoopMode.ToggleUntilKey &&
            _isPlaying &&
            string.Equals(_activeToggleTrigger, triggerKey, StringComparison.OrdinalIgnoreCase))
        {
            CancelPlayback();
            return;
        }

        lock (_playLock)
        {
            _activeToggleTrigger = macro.LoopMode == MacroLoopMode.ToggleUntilKey ? triggerKey : null;
            _activeHeldTrigger = macro.LoopMode == MacroLoopMode.WhileHeld ? triggerKey : null;
        }
        _ = Task.Run(() => PlayMacroAsync(macro));
    }

    private static void SendSingleStep(byte keyCode, bool isPress)
    {
        INPUT input;
        if (keyCode is HidKeyCatalog.MouseLeftCode or HidKeyCatalog.MouseRightCode or HidKeyCatalog.MouseMiddleCode
            or HidKeyCatalog.MouseXButton1Code or HidKeyCatalog.MouseXButton2Code)
        {
            uint mouseData = keyCode switch
            {
                HidKeyCatalog.MouseXButton1Code => 1u,
                HidKeyCatalog.MouseXButton2Code => 2u,
                _ => 0u,
            };
            uint flags = keyCode switch
            {
                HidKeyCatalog.MouseLeftCode => isPress ? MouseeventfLeftdown : MouseeventfLeftup,
                HidKeyCatalog.MouseRightCode => isPress ? MouseeventfRightdown : MouseeventfRightup,
                HidKeyCatalog.MouseMiddleCode => isPress ? MouseeventfMiddledown : MouseeventfMiddleup,
                _ => isPress ? MouseeventfXdown : MouseeventfXup,
            };
            input = new INPUT
            {
                type = InputMouse,
                U = new InputUnion { mi = new MOUSEINPUT { mouseData = mouseData, dwFlags = flags } },
            };
        }
        else
        {
            int vk = HidKeyCatalog.VirtualKeyFromUsage(keyCode);
            if (vk == 0) return;

            ushort scan = (ushort)MapVirtualKeyW((uint)vk, 0);
            bool extended = vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
                or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0xA3 or 0xA5;
            uint flags = (isPress ? 0u : KeyeventfKeyup) | (extended ? KeyeventfExtendedKey : 0u);
            if (scan != 0)
                flags |= KeyeventfScancode;

            input = new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        wScan = scan,
                        dwFlags = flags,
                    },
                },
            };
        }

        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPlayback();
        StopHook();
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, StringBuilder? pData, ref uint pcbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
