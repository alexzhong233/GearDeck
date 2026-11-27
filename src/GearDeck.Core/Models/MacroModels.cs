namespace GearDeck.Core.Models;

public enum MacroLoopMode
{
    /// <summary>Repeat fixed number of cycles.</summary>
    RepeatCount = 0,
    /// <summary>Repeat while button is held down.</summary>
    WhileHeld = 1,
    /// <summary>Toggle continuous playback until button/Esc key pressed.</summary>
    ToggleUntilKey = 2,
    /// <summary>Play macro sequence once per trigger press (Razer Synapse Play Once).</summary>
    PlayOnce = 3,
}

public sealed class MacroStep
{
    /// <summary>
    /// Standard HID keyboard usage (0x04..0xE7) or mouse key:
    /// 0xF0 = Left click, 0xF1 = Right click, 0xF2 = Middle click.
    /// </summary>
    public byte KeyCode { get; set; }

    public bool IsPress { get; set; }

    public int DelayMs { get; set; }

    public MacroStep() { }

    public MacroStep(byte keyCode, bool isPress, int delayMs)
    {
        KeyCode = keyCode;
        IsPress = isPress;
        DelayMs = Math.Clamp(delayMs, 0, 65535);
    }

    public string DisplayName => HidKeyCatalog.DescribeKeyCode(KeyCode);
}

public sealed class OnboardMacroDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "新硬件宏";
    public MacroLoopMode LoopMode { get; set; } = MacroLoopMode.RepeatCount;
    public int RepeatCount { get; set; } = 1;
    public List<MacroStep> Steps { get; set; } = [];
}

/// <summary>标准 HID 键盘 Usage ID 与鼠标动作编码表，供板载按键映射、硬件宏录制与软件回放共用。</summary>
public static class HidKeyCatalog
{
    public const byte MouseLeftCode = 0xF0;
    public const byte MouseRightCode = 0xF1;
    public const byte MouseMiddleCode = 0xF2;
    public const byte MouseXButton1Code = 0xF3;
    public const byte MouseXButton2Code = 0xF4;
    public const byte MouseWheelUpCode = 0xF5;
    public const byte MouseWheelDownCode = 0xF6;

    public static bool IsWheelCode(byte code) => code is MouseWheelUpCode or MouseWheelDownCode;

    public const byte ModLeftCtrl = 0x01;
    public const byte ModLeftShift = 0x02;
    public const byte ModLeftAlt = 0x04;
    public const byte ModLeftWin = 0x08;

    public sealed record KeyEntry(byte Usage, string Label, int VirtualKey);

    public static readonly IReadOnlyList<KeyEntry> AllKeys = BuildKeys();

    private static List<KeyEntry> BuildKeys()
    {
        var list = new List<KeyEntry>(96)
        {
            new(MouseLeftCode, "鼠标左键", 0x01),
            new(MouseRightCode, "鼠标右键", 0x02),
            new(MouseMiddleCode, "鼠标中键", 0x04),
            new(MouseXButton1Code, "鼠标侧键4 (后退)", 0x05),
            new(MouseXButton2Code, "鼠标侧键5 (前进)", 0x06),
            new(MouseWheelUpCode, "鼠标滚轮向上", 0),
            new(MouseWheelDownCode, "鼠标滚轮向下", 0),
        };
        for (int i = 0; i < 26; i++)
            list.Add(new((byte)(0x04 + i), ((char)('A' + i)).ToString(), 'A' + i));
        for (int i = 0; i < 9; i++)
            list.Add(new((byte)(0x1E + i), (i + 1).ToString(), '1' + i));
        list.Add(new(0x27, "0", '0'));
        for (int i = 0; i < 12; i++)
            list.Add(new((byte)(0x3A + i), $"F{i + 1}", 0x70 + i));

        list.AddRange(
        [
            new(0x28, "Enter", 0x0D),
            new(0x29, "Esc", 0x1B),
            new(0x2A, "Backspace", 0x08),
            new(0x2B, "Tab", 0x09),
            new(0x2C, "Space", 0x20),
            new(0x2D, "- _", 0xBD),
            new(0x2E, "= +", 0xBB),
            new(0x2F, "[ {", 0xDB),
            new(0x30, "] }", 0xDD),
            new(0x33, "; :", 0xBA),
            new(0x36, ", <", 0xBC),
            new(0x37, ". >", 0xBE),
            new(0x38, "/ ?", 0xBF),
            new(0x35, "` ~", 0xC0),
            new(0x39, "Caps Lock", 0x14),
            new(0x46, "Print Screen", 0x2C),
            new(0x49, "Insert", 0x2D),
            new(0x4A, "Home", 0x24),
            new(0x4B, "Page Up", 0x21),
            new(0x4C, "Delete", 0x2E),
            new(0x4D, "End", 0x23),
            new(0x4E, "Page Down", 0x22),
            new(0x4F, "Right", 0x27),
            new(0x50, "Left", 0x25),
            new(0x51, "Down", 0x28),
            new(0x52, "Up", 0x26),
            new(0xE0, "Left Ctrl", 0xA2),
            new(0xE1, "Left Shift", 0xA0),
            new(0xE2, "Left Alt", 0xA4),
            new(0xE3, "Left Win", 0x5B),
        ]);
        return list;
    }

    public static byte? UsageFromVirtualKey(int vk)
    {
        if (vk == 0x11) return 0xE0; // VK_CONTROL
        if (vk == 0x10) return 0xE1; // VK_SHIFT
        if (vk == 0x12) return 0xE2; // VK_MENU
        foreach (var k in AllKeys)
            if (k.VirtualKey == vk) return k.Usage;
        return null;
    }

    public static int VirtualKeyFromUsage(byte usage)
    {
        foreach (var k in AllKeys)
            if (k.Usage == usage) return k.VirtualKey;
        return 0;
    }

    public static string DescribeKeyCode(byte code)
    {
        foreach (var k in AllKeys)
            if (k.Usage == code) return k.Label;
        return $"Key 0x{code:X2}";
    }

    public static string FormatKeyboardBinding(byte modifiers, byte usage) => $"Keyboard:{modifiers}:{usage}";

    public static bool TryParseKeyboardBinding(string action, out byte modifiers, out byte usage)
    {
        modifiers = 0;
        usage = 0;
        if (!action.StartsWith("Keyboard:", StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = action.Split(':');
        if (parts.Length != 3) return false;
        return byte.TryParse(parts[1], out modifiers) && byte.TryParse(parts[2], out usage) && usage != 0;
    }

    public static string FormatModifiersAndKey(byte modifiers, byte usage)
    {
        var list = new List<string>(4);
        if ((modifiers & ModLeftCtrl) != 0) list.Add("Ctrl");
        if ((modifiers & ModLeftShift) != 0) list.Add("Shift");
        if ((modifiers & ModLeftAlt) != 0) list.Add("Alt");
        if ((modifiers & ModLeftWin) != 0) list.Add("Win");
        list.Add(DescribeKeyCode(usage));
        return string.Join(" + ", list);
    }
}
