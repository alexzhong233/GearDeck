using System.Runtime.InteropServices;
using GearDeck.Core;
using GearDeck.Core.Models;

namespace GearDeck.App.Services;

/// <summary>
/// 鼠标宏测试回放与软件备用触发引擎：
/// - 优先通过鼠标硬件板载闪存执行原生宏（如 Incott09 320B 宏）；
/// - 对于仅开放按键重映射但未开放多步闪存协议的鼠标，支持全局无阻塞低级钩子按侧键/中键触发已录制的宏。
/// </summary>
public sealed class MacroExecutionService : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int LlhfInjected = 0x00000001;

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightdown = 0x0008;
    private const uint MouseeventfRightup = 0x0010;
    private const uint MouseeventfMiddledown = 0x0020;
    private const uint MouseeventfMiddleup = 0x0040;

    private readonly object _playLock = new();
    private CancellationTokenSource? _activePlayCts;
    private string? _activeToggleTrigger;
    private volatile bool _isPlaying;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle;
    private LowLevelMouseProc? _hookProc;
    private volatile bool _disposed;

    public static IReadOnlyList<(string Key, string Label)> SoftwareTriggerButtons { get; } =
    [
        ("XButton1", "鼠标侧键 4 (后退键 XButton1)"),
        ("XButton2", "鼠标侧键 5 (前进键 XButton2)"),
        ("Middle", "鼠标中键 (滚轮按下 Middle)"),
    ];

    public void SyncHookState()
    {
        if (_disposed) return;
        bool needHook = App.Settings.Current.LocalButtonMacroBindings.Values.Any(v => !string.IsNullOrWhiteSpace(v));
        if (needHook && _hookThread == null)
            StartHook();
        else if (!needHook && _hookThread != null)
            StopHook();
    }

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

        var pressedCodes = new HashSet<byte>();
        try
        {
            int maxLoops = macro.LoopMode == MacroLoopMode.RepeatCount
                ? Math.Clamp(macro.RepeatCount, 1, 100)
                : 1000;

            for (int loop = 0; loop < maxLoops; loop++)
            {
                if (cts.Token.IsCancellationRequested) break;
                foreach (var step in macro.Steps)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    SendSingleStep(step.KeyCode, step.IsPress);
                    if (step.IsPress) pressedCodes.Add(step.KeyCode);
                    else pressedCodes.Remove(step.KeyCode);

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
            foreach (byte stuck in pressedCodes)
                SendSingleStep(stuck, isPress: false);

            lock (_playLock)
            {
                if (ReferenceEquals(_activePlayCts, cts))
                    _isPlaying = false;
            }
        }
    }

    public void CancelPlayback()
    {
        lock (_playLock)
        {
            _activePlayCts?.Cancel();
            _activeToggleTrigger = null;
            _isPlaying = false;
        }
    }

    private void StartHook()
    {
        _hookProc ??= MouseHookCallback;
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _hookHandle = SetWindowsHookExW(WhMouseLl, _hookProc, IntPtr.Zero, 0);
            if (_hookHandle == IntPtr.Zero) return;
            while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "GearDeck.MouseMacroHook",
        };
        _hookThread.Start();
    }

    private void StopHook()
    {
        if (_hookThreadId != 0)
            PostThreadMessageW(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _hookThread = null;
        _hookThreadId = 0;
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

                string? macroId = triggerKey != null ? ResolveBoundMacroId(triggerKey) : null;
                if (triggerKey != null && !string.IsNullOrWhiteSpace(macroId))
                {
                    var macro = App.Settings.Current.Macros.FirstOrDefault(m => m.Id == macroId);
                    if (macro != null && macro.Steps.Count > 0)
                    {
                        bool isDown = msg is WmMButtonDown or WmXButtonDown;
                        if (isDown)
                        {
                            HandleMacroTriggerDown(triggerKey, macro);
                        }
                        else if (macro.LoopMode == MacroLoopMode.WhileHeld)
                        {
                            CancelPlayback();
                        }
                        return (IntPtr)1;
                    }
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static string? ResolveBoundMacroId(string triggerKey)
    {
        var map = App.Settings.Current.LocalButtonMacroBindings;
        foreach (var d in App.LastDevices.Where(x => x.Kind == DeviceKind.Mouse && x.Connected && !x.IsSleeping))
        {
            if (map.TryGetValue($"{d.Id}|{triggerKey}", out var scopedId) && !string.IsNullOrWhiteSpace(scopedId))
                return scopedId;
        }
        return map.TryGetValue(triggerKey, out var globalId) && !string.IsNullOrWhiteSpace(globalId)
            ? globalId
            : null;
    }

    private void HandleMacroTriggerDown(string triggerKey, OnboardMacroDefinition macro)
    {
        if (macro.LoopMode == MacroLoopMode.ToggleUntilKey && _isPlaying && _activeToggleTrigger == triggerKey)
        {
            CancelPlayback();
            return;
        }
        _activeToggleTrigger = macro.LoopMode == MacroLoopMode.ToggleUntilKey ? triggerKey : null;
        _ = Task.Run(() => PlayMacroAsync(macro));
    }

    private static void SendSingleStep(byte keyCode, bool isPress)
    {
        INPUT input;
        if (keyCode is HidKeyCatalog.MouseLeftCode or HidKeyCatalog.MouseRightCode or HidKeyCatalog.MouseMiddleCode)
        {
            uint flags = keyCode switch
            {
                HidKeyCatalog.MouseLeftCode => isPress ? MouseeventfLeftdown : MouseeventfLeftup,
                HidKeyCatalog.MouseRightCode => isPress ? MouseeventfRightdown : MouseeventfRightup,
                _ => isPress ? MouseeventfMiddledown : MouseeventfMiddleup,
            };
            input = new INPUT
            {
                type = InputMouse,
                U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } },
            };
        }
        else
        {
            int vk = HidKeyCatalog.VirtualKeyFromUsage(keyCode);
            if (vk == 0) return;
            input = new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        dwFlags = isPress ? 0 : KeyeventfKeyup,
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

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

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
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
