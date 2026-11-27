using System.Globalization;

namespace GearDeck.Core.Providers;

/// <summary>键盘分区枚举（用于单键批量涂色与界面辅助）。</summary>
public sealed class Hi98KeyZone
{
    public static readonly Hi98KeyZone FunctionRow = new("F 功能区");
    public static readonly Hi98KeyZone MainAlpha = new("主字母区");
    public static readonly Hi98KeyZone Modifiers = new("修饰与控制");
    public static readonly Hi98KeyZone Navigation = new("导航与方向键");
    public static readonly Hi98KeyZone Numpad = new("数字小键盘");
    public static readonly Hi98KeyZone Wasd = new("WASD 游戏区");

    public static IReadOnlyList<Hi98KeyZone> All { get; } =
        [FunctionRow, MainAlpha, Modifiers, Navigation, Numpad, Wasd];

    private Hi98KeyZone(string rawValue) => RawValue = rawValue;

    public string RawValue { get; }

    public override string ToString() => RawValue;
}

/// <summary>
/// 按键层：3 个可编程层（Fn+W 生效）+ 2 个 Fn+E 固件只读 Mac 预设层。
/// </summary>
public sealed class Hi98KeyLayer
{
    public static readonly Hi98KeyLayer Base = new(0, "默认层", "可编程主层 (Fn+W 模式生效，支持完整 Mac 键位与改键)", true, true, 0x00, 0x00);
    public static readonly Hi98KeyLayer Fn1 = new(1, "Fn1 层", "按住 Fn 触发层 (Fn+W 模式生效)", true, false, 0x01, 0x00);
    public static readonly Hi98KeyLayer Fn2 = new(2, "Fn2 层", "扩展 Fn2 触发层 (Fn+W 模式生效)", true, false, 0x02, 0x00);
    public static readonly Hi98KeyLayer MacPresetBase = new(3, "Mac 预设 (只读)", "Fn+E 固件内置默认层 (硬件只读参考)", false, true, 0x00, 0x01);
    public static readonly Hi98KeyLayer MacPresetFn1 = new(4, "Mac Fn (只读)", "Fn+E 固件内置 Fn 层 (硬件只读参考)", false, false, 0x01, 0x01);

    public static IReadOnlyList<Hi98KeyLayer> WritableLayers { get; } = [Base, Fn1, Fn2];

    public static IReadOnlyList<Hi98KeyLayer> UiDisplayOrder { get; } =
        [Base, Fn1, Fn2, MacPresetBase, MacPresetFn1];

    public static IReadOnlyList<Hi98KeyLayer> All => UiDisplayOrder;

    private Hi98KeyLayer(int rawValue, string title, string subtitle, bool isWritable,
        bool isBaseLayer, byte wiredSub, byte wiredPacketIndex)
    {
        RawValue = rawValue;
        Title = title;
        Subtitle = subtitle;
        IsWritable = isWritable;
        IsBaseLayer = isBaseLayer;
        WiredSub = wiredSub;
        WiredPacketIndex = wiredPacketIndex;
    }

    public int RawValue { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public bool IsWritable { get; }
    public bool IsBaseLayer { get; }
    public byte WiredSub { get; }
    public byte WiredPacketIndex { get; }
    public byte WirelessSub => WiredSub;

    public static Hi98KeyLayer FromRawValue(int value) => value switch
    {
        0 => Base,
        1 => Fn1,
        2 => Fn2,
        3 => MacPresetBase,
        4 => MacPresetFn1,
        _ => Base,
    };

    public override string ToString() => Title;
}

/// <summary>键盘连接通道（严格区分有线可写与无线只读）。</summary>
public enum Hi98ConnectionTransport
{
    UsbWired = 0,
    Wireless24G = 1,
    Bluetooth = 2,
    Disconnected = 3,
}

public static class Hi98ConnectionTransportExtensions
{
    public static string RawValue(this Hi98ConnectionTransport transport) => transport switch
    {
        Hi98ConnectionTransport.UsbWired => "USB 有线",
        Hi98ConnectionTransport.Wireless24G => "2.4G 无线",
        Hi98ConnectionTransport.Bluetooth => "蓝牙",
        _ => "未连接",
    };

    /// <summary>铁律：仅 USB-C 有线模式允许写板载 EEPROM / Flash。</summary>
    public static bool CanWriteEEPROM(this Hi98ConnectionTransport transport)
        => transport == Hi98ConnectionTransport.UsbWired;

    public static bool CanStreamLiveRGB(this Hi98ConnectionTransport transport)
        => transport is Hi98ConnectionTransport.UsbWired or Hi98ConnectionTransport.Wireless24G;

    public static string ShortBadge(this Hi98ConnectionTransport transport) => transport switch
    {
        Hi98ConnectionTransport.UsbWired => "USB 有线",
        Hi98ConnectionTransport.Wireless24G => "2.4G 无线",
        Hi98ConnectionTransport.Bluetooth => "蓝牙模式",
        _ => "未连接",
    };
}

/// <summary>修饰键位掩码（对应 4 字节键位记录 Byte 1）。</summary>
[Flags]
public enum Hi98ModifierMask : byte
{
    None = 0x00,
    LeftControl = 0x01,
    LeftShift = 0x02,
    LeftAlt = 0x04,
    LeftCommand = 0x08,
    RightControl = 0x10,
    RightShift = 0x20,
    RightAlt = 0x40,
    RightCommand = 0x80,
}

public static class Hi98ModifierMaskExtensions
{
    public static IReadOnlyList<string> MacLabels(this Hi98ModifierMask mask)
    {
        var result = new List<string>();
        if (mask.HasFlag(Hi98ModifierMask.LeftControl)) result.Add("⌃Ctrl");
        if (mask.HasFlag(Hi98ModifierMask.LeftShift)) result.Add("⇧Shift");
        if (mask.HasFlag(Hi98ModifierMask.LeftAlt)) result.Add("⌥Opt");
        if (mask.HasFlag(Hi98ModifierMask.LeftCommand)) result.Add("⌘Cmd");
        if (mask.HasFlag(Hi98ModifierMask.RightControl)) result.Add("R⌃");
        if (mask.HasFlag(Hi98ModifierMask.RightShift)) result.Add("R⇧");
        if (mask.HasFlag(Hi98ModifierMask.RightAlt)) result.Add("R⌥");
        if (mask.HasFlag(Hi98ModifierMask.RightCommand)) result.Add("R⌘");
        return result;
    }

    public static Hi98ModifierMask? FromHidModifierUsage(byte usage) => usage switch
    {
        0xE0 => Hi98ModifierMask.LeftControl,
        0xE1 => Hi98ModifierMask.LeftShift,
        0xE2 => Hi98ModifierMask.LeftAlt,
        0xE3 => Hi98ModifierMask.LeftCommand,
        0xE4 => Hi98ModifierMask.RightControl,
        0xE5 => Hi98ModifierMask.RightShift,
        0xE6 => Hi98ModifierMask.RightAlt,
        0xE7 => Hi98ModifierMask.RightCommand,
        _ => null,
    };

    public static byte? SingleHidUsage(this Hi98ModifierMask mask) => mask switch
    {
        Hi98ModifierMask.LeftControl => 0xE0,
        Hi98ModifierMask.LeftShift => 0xE1,
        Hi98ModifierMask.LeftAlt => 0xE2,
        Hi98ModifierMask.LeftCommand => 0xE3,
        Hi98ModifierMask.RightControl => 0xE4,
        Hi98ModifierMask.RightShift => 0xE5,
        Hi98ModifierMask.RightAlt => 0xE6,
        Hi98ModifierMask.RightCommand => 0xE7,
        _ => null,
    };
}

/// <summary>键盘 4 字节键位记录语义类型。</summary>
public abstract record Hi98KeyRecordKind
{
    public sealed record Empty : Hi98KeyRecordKind;
    public sealed record Standard(Hi98ModifierMask Modifiers, byte Usage, byte SecondaryUsage) : Hi98KeyRecordKind;
    public sealed record FnKey : Hi98KeyRecordKind;
    public sealed record ConsumerMedia(ushort Usage) : Hi98KeyRecordKind;
    public sealed record SystemFunction(byte Code) : Hi98KeyRecordKind;
    public sealed record LightingControl(byte B1, byte B2, byte B3) : Hi98KeyRecordKind;
    public sealed record Macro(byte Mode, byte Count, byte MacroIndex) : Hi98KeyRecordKind;
    public sealed record Raw(byte B0, byte B1, byte B2, byte B3) : Hi98KeyRecordKind;
}

/// <summary>4 字节键位记录（对应 EEPROM Keymap 槽位）。</summary>
public sealed class Hi98KeyBindingRecord : IEquatable<Hi98KeyBindingRecord>
{
    public byte[] Bytes { get; }

    public Hi98KeyBindingRecord(IEnumerable<byte> bytes)
    {
        var padded = bytes.Take(4).ToList();
        while (padded.Count < 4) padded.Add(0);
        Bytes = padded.ToArray();
    }

    public Hi98KeyBindingRecord(byte b0, byte b1, byte b2, byte b3) => Bytes = [b0, b1, b2, b3];

    public static Hi98KeyBindingRecord Empty { get; } = new(0x00, 0x00, 0x00, 0x00);
    public static Hi98KeyBindingRecord FnKey { get; } = new(0x0D, 0x00, 0x00, 0x00);

    public static Hi98KeyBindingRecord Key(byte usage, Hi98ModifierMask modifiers = Hi98ModifierMask.None,
        byte secondaryUsage = 0x00)
    {
        if (modifiers == Hi98ModifierMask.None && Hi98ModifierMaskExtensions.FromHidModifierUsage(usage) is { } mod)
            return new Hi98KeyBindingRecord(0x00, (byte)mod, 0x00, 0x00);
        return new Hi98KeyBindingRecord(0x00, (byte)modifiers, secondaryUsage, usage);
    }

    public static Hi98KeyBindingRecord Consumer(ushort usage)
        => new(0x02, 0x00, (byte)((usage >> 8) & 0xFF), (byte)(usage & 0xFF));

    public static Hi98KeyBindingRecord Macro(byte index, byte mode = 1, byte count = 1)
        => new(0x03, mode, mode == 1 ? Math.Max((byte)1, count) : (byte)1, index);

    public static Hi98KeyBindingRecord System(byte code) => new(0x07, 0x00, 0x00, code);

    public static Hi98KeyBindingRecord Lighting(byte b1, byte b2, byte b3 = 0x00) => new(0x08, b1, b2, b3);

    public bool IsEmpty => Bytes[0] == 0 && Bytes[1] == 0 && Bytes[2] == 0 && Bytes[3] == 0;

    public bool IsFnKey => Bytes[0] == 0x0D;

    public Hi98KeyRecordKind Kind
    {
        get
        {
            if (IsEmpty) return new Hi98KeyRecordKind.Empty();
            return Bytes[0] switch
            {
                0x00 => new Hi98KeyRecordKind.Standard((Hi98ModifierMask)Bytes[1], Bytes[3], Bytes[2]),
                0x02 => new Hi98KeyRecordKind.ConsumerMedia((ushort)((Bytes[2] << 8) | Bytes[3])),
                0x03 => new Hi98KeyRecordKind.Macro(Bytes[1], Bytes[2], Bytes[3]),
                0x07 => new Hi98KeyRecordKind.SystemFunction(Bytes[3]),
                0x08 => new Hi98KeyRecordKind.LightingControl(Bytes[1], Bytes[2], Bytes[3]),
                0x0D => new Hi98KeyRecordKind.FnKey(),
                _ => new Hi98KeyRecordKind.Raw(Bytes[0], Bytes[1], Bytes[2], Bytes[3]),
            };
        }
    }

    /// <summary>仅代表单个标准 HID Usage（含 0xE0..0xE7 与 0xFF Fn）时返回编码。</summary>
    public byte? SingleHidUsageOrFn
    {
        get
        {
            if (IsFnKey) return 0xFF;
            if (Bytes[0] == 0x00 && Bytes[2] == 0x00)
            {
                if (Bytes[3] == 0x00 && ((Hi98ModifierMask)Bytes[1]).SingleHidUsage() is { } modUsage)
                    return modUsage;
                if (Bytes[1] == 0x00 && Bytes[3] != 0x00)
                    return Bytes[3];
            }
            return null;
        }
    }

    public string DisplayLabel => Kind switch
    {
        Hi98KeyRecordKind.Empty => "禁用",
        Hi98KeyRecordKind.FnKey => "Fn",
        Hi98KeyRecordKind.Standard standard => StandardLabel(standard),
        Hi98KeyRecordKind.ConsumerMedia media => Hi98KeycodeCatalog.ConsumerLabel(media.Usage),
        Hi98KeyRecordKind.SystemFunction sys => Hi98KeycodeCatalog.SystemFunctionLabel(sys.Code),
        Hi98KeyRecordKind.LightingControl light => Hi98KeycodeCatalog.LightingControlLabel(light.B1, light.B2),
        Hi98KeyRecordKind.Macro macro => $"M{macro.MacroIndex + 1}({(macro.Mode == 2 ? "按住" : macro.Mode == 4 ? "循环" : $"×{macro.Count}")})",
        Hi98KeyRecordKind.Raw raw => string.Concat(raw.B0.ToString("X2", CultureInfo.InvariantCulture),
            raw.B1.ToString("X2", CultureInfo.InvariantCulture),
            raw.B2.ToString("X2", CultureInfo.InvariantCulture),
            raw.B3.ToString("X2", CultureInfo.InvariantCulture)),
        _ => "禁用",
    };

    private static string StandardLabel(Hi98KeyRecordKind.Standard standard)
    {
        if (standard.Usage == 0 && standard.SecondaryUsage == 0
            && standard.Modifiers.SingleHidUsage() is { } modUsage)
            return Hi98KeycodeCatalog.ShortLabel(modUsage);

        var parts = new List<string>(standard.Modifiers.MacLabels());
        if (standard.SecondaryUsage != 0)
            parts.Add(Hi98KeycodeCatalog.ShortLabel(standard.SecondaryUsage));
        if (standard.Usage != 0)
            parts.Add(Hi98KeycodeCatalog.ShortLabel(standard.Usage));
        return parts.Count == 0 ? "禁用" : string.Join("+", parts);
    }

    public string HexString => string.Join(" ", Bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    public bool Equals(Hi98KeyBindingRecord? other)
        => other is not null && Bytes.AsSpan().SequenceEqual(other.Bytes);

    public override bool Equals(object? obj) => obj is Hi98KeyBindingRecord other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var b in Bytes) hash.Add(b);
        return hash.ToHashCode();
    }

    public override string ToString() => HexString;
}

/// <summary>键码选择器分类。</summary>
public sealed class Hi98KeycodeOptionCategory
{
    public static readonly Hi98KeycodeOptionCategory Alphanumeric = new("字母与数字");
    public static readonly Hi98KeycodeOptionCategory FunctionKeys = new("功能键 F1-F24");
    public static readonly Hi98KeycodeOptionCategory Modifiers = new("修饰键 (Mac/Win)");
    public static readonly Hi98KeycodeOptionCategory Navigation = new("方向与编辑");
    public static readonly Hi98KeycodeOptionCategory Numpad = new("数字小键盘");
    public static readonly Hi98KeycodeOptionCategory MacMedia = new("macOS 媒体与系统");
    public static readonly Hi98KeycodeOptionCategory Shortcuts = new("常用组合快捷键");
    public static readonly Hi98KeycodeOptionCategory Lighting = new("板载灯效控制");
    public static readonly Hi98KeycodeOptionCategory Firmware = new("硬件模式/切换");
    public static readonly Hi98KeycodeOptionCategory Special = new("特殊键 (Fn/禁用)");

    public static IReadOnlyList<Hi98KeycodeOptionCategory> All { get; } =
        [Alphanumeric, FunctionKeys, Modifiers, Navigation, Numpad, MacMedia, Shortcuts, Lighting, Firmware, Special];

    private Hi98KeycodeOptionCategory(string rawValue) => RawValue = rawValue;

    public string RawValue { get; }

    public override string ToString() => RawValue;
}

/// <summary>键码选择器条目。</summary>
public sealed class Hi98KeycodeOption
{
    public Hi98KeycodeOption(string? id, string label, string detail, Hi98KeycodeOptionCategory category,
        Hi98KeyBindingRecord record)
    {
        Id = id ?? $"{category.RawValue}:{label}:{record.HexString}";
        Label = label;
        Detail = detail;
        Category = category;
        Record = record;
    }

    public string Id { get; }
    public string Label { get; }
    public string Detail { get; }
    public Hi98KeycodeOptionCategory Category { get; }
    public Hi98KeyBindingRecord Record { get; }
}

/// <summary>完整 USB HID / Consumer / 固件功能键码目录。</summary>
public static class Hi98KeycodeCatalog
{
    private static readonly Dictionary<byte, string> HidShortLabels = new()
    {
        [0x04] = "A", [0x05] = "B", [0x06] = "C", [0x07] = "D", [0x08] = "E", [0x09] = "F",
        [0x0A] = "G", [0x0B] = "H", [0x0C] = "I", [0x0D] = "J", [0x0E] = "K", [0x0F] = "L",
        [0x10] = "M", [0x11] = "N", [0x12] = "O", [0x13] = "P", [0x14] = "Q", [0x15] = "R",
        [0x16] = "S", [0x17] = "T", [0x18] = "U", [0x19] = "V", [0x1A] = "W", [0x1B] = "X",
        [0x1C] = "Y", [0x1D] = "Z",
        [0x1E] = "1", [0x1F] = "2", [0x20] = "3", [0x21] = "4", [0x22] = "5",
        [0x23] = "6", [0x24] = "7", [0x25] = "8", [0x26] = "9", [0x27] = "0",
        [0x28] = "Enter", [0x29] = "Esc", [0x2A] = "⌫", [0x2B] = "Tab", [0x2C] = "Space",
        [0x2D] = "-", [0x2E] = "=", [0x2F] = "[", [0x30] = "]", [0x31] = "\\",
        [0x32] = "ISO#", [0x33] = ";", [0x34] = "'", [0x35] = "`", [0x36] = ",",
        [0x37] = ".", [0x38] = "/", [0x39] = "Caps",
        [0x3A] = "F1", [0x3B] = "F2", [0x3C] = "F3", [0x3D] = "F4", [0x3E] = "F5", [0x3F] = "F6",
        [0x40] = "F7", [0x41] = "F8", [0x42] = "F9", [0x43] = "F10", [0x44] = "F11", [0x45] = "F12",
        [0x46] = "PrtSc", [0x47] = "ScrLk", [0x48] = "Pause", [0x49] = "Ins", [0x4A] = "Home",
        [0x4B] = "PgUp", [0x4C] = "Del", [0x4D] = "End", [0x4E] = "PgDn",
        [0x4F] = "→", [0x50] = "←", [0x51] = "↓", [0x52] = "↑",
        [0x53] = "Num", [0x54] = "N/", [0x55] = "N*", [0x56] = "N-", [0x57] = "N+", [0x58] = "NEnt",
        [0x59] = "N1", [0x5A] = "N2", [0x5B] = "N3", [0x5C] = "N4", [0x5D] = "N5",
        [0x5E] = "N6", [0x5F] = "N7", [0x60] = "N8", [0x61] = "N9", [0x62] = "N0", [0x63] = "N.",
        [0x64] = "ISO\\", [0x65] = "Menu",
        [0x68] = "F13", [0x69] = "F14", [0x6A] = "F15", [0x6B] = "F16", [0x6C] = "F17", [0x6D] = "F18",
        [0x6E] = "F19", [0x6F] = "F20", [0x70] = "F21", [0x71] = "F22", [0x72] = "F23", [0x73] = "F24",
        [0xE0] = "LCtrl", [0xE1] = "LShift", [0xE2] = "LOpt", [0xE3] = "LCmd",
        [0xE4] = "RCtrl", [0xE5] = "RShift", [0xE6] = "ROpt", [0xE7] = "RCmd",
        [0xFF] = "Fn",
    };

    public static string ShortLabel(byte usage)
        => HidShortLabels.TryGetValue(usage, out var label) ? label : $"0x{usage:X2}";

    public static string ConsumerLabel(ushort usage) => usage switch
    {
        0x006F => "亮度+",
        0x0070 => "亮度-",
        0x00B5 => "下一曲",
        0x00B6 => "上一曲",
        0x00B7 => "停止",
        0x00CD => "播放/暂停",
        0x00E2 => "静音",
        0x00E9 => "音量+",
        0x00EA => "音量-",
        0x018A => "邮件",
        0x0192 => "计算器",
        0x0194 => "访达/文件",
        0x0221 => "搜索",
        0x0223 => "浏览器主页",
        0x0224 => "后退",
        0x0225 => "前进",
        0x022A => "书签",
        _ => $"媒体 0x{usage:X4}",
    };

    public static string SystemFunctionLabel(byte code) => code switch
    {
        0x01 => "锁Win键",
        0x04 => "恢复出厂",
        0x05 => "蓝牙通道1",
        0x06 => "蓝牙通道2",
        0x07 => "蓝牙通道3",
        0x08 => "2.4G模式",
        0x0A => "无线配对",
        0x11 => "电量显示",
        0x18 => "Win系统",
        0x19 => "Android",
        0x1A => "Mac系统",
        _ => $"系统 0x{code:X2}",
    };

    public static string LightingControlLabel(byte b1, byte b2) => (b1, b2) switch
    {
        (0x00, 0x00) => "切灯效",
        (0x00, 0x01) => "侧灯模式",
        (0x02, 0x00) => "切灯色",
        (0x03, 0x01) => "灯效亮度+",
        (0x03, 0x02) => "灯效亮度-",
        (0x04, 0x01) => "灯效速度+",
        (0x04, 0x02) => "灯效速度-",
        _ => $"灯光 {b1:X2}{b2:X2}",
    };

    public static IReadOnlyList<Hi98KeycodeOption> AllOptions { get; } = BuildAllOptions();

    private static IReadOnlyList<Hi98KeycodeOption> BuildAllOptions()
    {
        var list = new List<Hi98KeycodeOption>();

        for (int i = 0; i < 26; i++)
        {
            byte code = (byte)(0x04 + i);
            list.Add(new(null, ((char)('A' + i)).ToString(), $"HID 0x{code:X2}",
                Hi98KeycodeOptionCategory.Alphanumeric, Hi98KeyBindingRecord.Key(code)));
        }

        var digits = new (byte Code, string Label)[]
        {
            (0x1E, "1 !"), (0x1F, "2 @"), (0x20, "3 #"), (0x21, "4 $"), (0x22, "5 %"),
            (0x23, "6 ^"), (0x24, "7 &"), (0x25, "8 *"), (0x26, "9 ("), (0x27, "0 )"),
        };
        foreach (var (code, label) in digits)
            list.Add(new(null, label, $"HID 0x{code:X2}", Hi98KeycodeOptionCategory.Alphanumeric,
                Hi98KeyBindingRecord.Key(code)));

        var symbols = new (byte Code, string Label)[]
        {
            (0x29, "Esc"), (0x35, "` ~"), (0x2D, "- _"), (0x2E, "= +"), (0x2A, "Backspace ⌫"),
            (0x2B, "Tab ⇥"), (0x2F, "[ {"), (0x30, "] }"), (0x31, "\\ |"), (0x39, "Caps Lock"),
            (0x33, "; :"), (0x34, "' \""), (0x28, "Enter ↩"), (0x36, ", <"), (0x37, ". >"),
            (0x38, "/ ?"), (0x2C, "Space 空格"), (0x65, "Menu / App"),
        };
        foreach (var (code, label) in symbols)
            list.Add(new(null, label, $"HID 0x{code:X2}", Hi98KeycodeOptionCategory.Alphanumeric,
                Hi98KeyBindingRecord.Key(code)));

        for (int i = 1; i <= 12; i++)
        {
            byte code = (byte)(0x3A + i - 1);
            list.Add(new(null, $"F{i}", $"HID 0x{code:X2}", Hi98KeycodeOptionCategory.FunctionKeys,
                Hi98KeyBindingRecord.Key(code)));
        }
        for (int i = 13; i <= 24; i++)
        {
            byte code = (byte)(0x68 + i - 13);
            list.Add(new(null, $"F{i}", "macOS 扩展功能键", Hi98KeycodeOptionCategory.FunctionKeys,
                Hi98KeyBindingRecord.Key(code)));
        }

        var mods = new (byte Code, string Label, string Detail)[]
        {
            (0xE0, "Left Control ⌃", "左 Control"),
            (0xE1, "Left Shift ⇧", "左 Shift"),
            (0xE2, "Left Option / Alt ⌥", "Mac 左 Option"),
            (0xE3, "Left Command / Win ⌘", "Mac 左 Command"),
            (0xE4, "Right Control ⌃", "右 Control"),
            (0xE5, "Right Shift ⇧", "右 Shift"),
            (0xE6, "Right Option / Alt ⌥", "Mac 右 Option"),
            (0xE7, "Right Command / Win ⌘", "Mac 右 Command"),
        };
        foreach (var (code, label, detail) in mods)
            list.Add(new(null, label, detail, Hi98KeycodeOptionCategory.Modifiers, Hi98KeyBindingRecord.Key(code)));

        var navs = new (byte Code, string Label)[]
        {
            (0x46, "Print Screen"), (0x47, "Scroll Lock"), (0x48, "Pause / Break"),
            (0x49, "Insert"), (0x4A, "Home"), (0x4B, "Page Up"),
            (0x4C, "Delete ⌦"), (0x4D, "End"), (0x4E, "Page Down"),
            (0x52, "Up ↑"), (0x51, "Down ↓"), (0x50, "Left ←"), (0x4F, "Right →"),
        };
        foreach (var (code, label) in navs)
            list.Add(new(null, label, $"HID 0x{code:X2}", Hi98KeycodeOptionCategory.Navigation,
                Hi98KeyBindingRecord.Key(code)));

        var numpad = new (byte Code, string Label)[]
        {
            (0x53, "Num Lock"), (0x54, "Num /"), (0x55, "Num *"), (0x56, "Num -"),
            (0x57, "Num +"), (0x58, "Num Enter"), (0x59, "Num 1"), (0x5A, "Num 2"),
            (0x5B, "Num 3"), (0x5C, "Num 4"), (0x5D, "Num 5"), (0x5E, "Num 6"),
            (0x5F, "Num 7"), (0x60, "Num 8"), (0x61, "Num 9"), (0x62, "Num 0"),
            (0x63, "Num ."),
        };
        foreach (var (code, label) in numpad)
            list.Add(new(null, label, "数字小键盘", Hi98KeycodeOptionCategory.Numpad,
                Hi98KeyBindingRecord.Key(code)));

        var media = new (ushort Usage, string Label, string Detail)[]
        {
            (0x006F, "屏幕亮度 +", "macOS 屏幕亮度增加"),
            (0x0070, "屏幕亮度 -", "macOS 屏幕亮度降低"),
            (0x00E9, "音量 +", "系统音量增加"),
            (0x00EA, "音量 -", "系统音量减少"),
            (0x00E2, "静音", "系统静音切换"),
            (0x00CD, "播放 / 暂停", "媒体播放控制"),
            (0x00B5, "下一曲", "切换下一首"),
            (0x00B6, "上一曲", "切换上一首"),
            (0x0221, "Spotlight / 搜索", "打开系统搜索"),
            (0x0223, "浏览器主页", "打开默认浏览器"),
            (0x018A, "邮件", "打开邮件客户端"),
            (0x0192, "计算器", "打开计算器"),
        };
        foreach (var (usage, label, detail) in media)
            list.Add(new(null, label, detail, Hi98KeycodeOptionCategory.MacMedia,
                Hi98KeyBindingRecord.Consumer(usage)));

        var shortcuts = new (string Label, string Detail, Hi98ModifierMask Modifiers, byte Usage)[]
        {
            ("⌘ + C", "复制 (Mac)", Hi98ModifierMask.LeftCommand, 0x06),
            ("⌘ + V", "粘贴 (Mac)", Hi98ModifierMask.LeftCommand, 0x19),
            ("⌘ + X", "剪切 (Mac)", Hi98ModifierMask.LeftCommand, 0x1B),
            ("⌘ + Z", "撤销 (Mac)", Hi98ModifierMask.LeftCommand, 0x1D),
            ("⌘ + ⇧ + Z", "重做 (Mac)", Hi98ModifierMask.LeftCommand | Hi98ModifierMask.LeftShift, 0x1D),
            ("⌘ + S", "保存 (Mac)", Hi98ModifierMask.LeftCommand, 0x16),
            ("⌘ + A", "全选 (Mac)", Hi98ModifierMask.LeftCommand, 0x04),
            ("⌘ + W", "关闭标签/窗口 (Mac)", Hi98ModifierMask.LeftCommand, 0x1A),
            ("⌘ + Q", "退出应用 (Mac)", Hi98ModifierMask.LeftCommand, 0x14),
            ("⌘ + Space", "Spotlight 搜索 (Mac)", Hi98ModifierMask.LeftCommand, 0x2C),
            ("⌘ + ⇧ + 4", "区域截图 (Mac)", Hi98ModifierMask.LeftCommand | Hi98ModifierMask.LeftShift, 0x21),
            ("⌘ + ⇧ + 5", "录屏与截图工具 (Mac)", Hi98ModifierMask.LeftCommand | Hi98ModifierMask.LeftShift, 0x22),
            ("⌃ + ⌘ + Q", "立即锁屏 (Mac)", Hi98ModifierMask.LeftControl | Hi98ModifierMask.LeftCommand, 0x14),
            ("⌃ + ↑", "调度中心 Mission Control", Hi98ModifierMask.LeftControl, 0x52),
            ("⌃ + ←", "左侧桌面空间", Hi98ModifierMask.LeftControl, 0x50),
            ("⌃ + →", "右侧桌面空间", Hi98ModifierMask.LeftControl, 0x4F),
            ("⌘ + Tab", "切换应用 (Mac)", Hi98ModifierMask.LeftCommand, 0x2B),
            ("⌃ + C", "终端中断 SIGINT", Hi98ModifierMask.LeftControl, 0x06),
        };
        foreach (var (label, detail, modifiers, usage) in shortcuts)
            list.Add(new(null, label, detail, Hi98KeycodeOptionCategory.Shortcuts,
                Hi98KeyBindingRecord.Key(usage, modifiers)));

        var lights = new (string Label, byte B1, byte B2)[]
        {
            ("切换板载灯效", 0x00, 0x00),
            ("切换灯效颜色", 0x02, 0x00),
            ("灯光亮度 +", 0x03, 0x01),
            ("灯光亮度 -", 0x03, 0x02),
            ("灯光速度 +", 0x04, 0x01),
            ("灯光速度 -", 0x04, 0x02),
            ("侧灯模式切换", 0x00, 0x01),
        };
        foreach (var (label, b1, b2) in lights)
            list.Add(new(null, label, "硬件内置灯光指令", Hi98KeycodeOptionCategory.Lighting,
                Hi98KeyBindingRecord.Lighting(b1, b2)));

        var fwFuncs = new (byte Code, string Label, string Detail)[]
        {
            (0x1A, "切换 Mac 模式", "同 Fn+E"),
            (0x18, "切换 Win 模式", "同 Fn+W"),
            (0x19, "切换 Android 模式", "同 Fn+Q"),
            (0x08, "切换 2.4G 模式", "同 Fn+`"),
            (0x05, "蓝牙设备 1", "同 Fn+1"),
            (0x06, "蓝牙设备 2", "同 Fn+2"),
            (0x07, "蓝牙设备 3", "同 Fn+3"),
            (0x11, "显示硬件电量条", "同 Fn+B"),
            (0x01, "锁定 Win 键", "同 Fn+Win"),
        };
        foreach (var (code, label, detail) in fwFuncs)
            list.Add(new(null, label, detail, Hi98KeycodeOptionCategory.Firmware,
                Hi98KeyBindingRecord.System(code)));

        list.Add(new(null, "Fn 切层键", "按住切换到 Fn 层（全盘至少保留一个）",
            Hi98KeycodeOptionCategory.Special, Hi98KeyBindingRecord.FnKey));
        list.Add(new(null, "禁用按键", "按下无输出",
            Hi98KeycodeOptionCategory.Special, Hi98KeyBindingRecord.Empty));

        return list;
    }
}

/// <summary>物理键规格（精确对应 LEOBOG Hi98C Pro / Hi98 3-Mode 101 键配列）。</summary>
public sealed class Hi98KeySlotSpec
{
    public Hi98KeySlotSpec(string id, int keyCodeIndex, string label, string? subLabel, int row,
        double x1, double y1, double x2, double y2, Hi98KeyZone zone, int defaultMatrixIndex,
        Hi98KeyBindingRecord defaultRecord)
    {
        Id = id;
        KeyCodeIndex = keyCodeIndex;
        Label = label;
        SubLabel = subLabel;
        Row = row;
        X = x1;
        Y = y1;
        Width = x2 - x1;
        Height = y2 - y1;
        Zone = zone;
        DefaultMatrixIndex = defaultMatrixIndex;
        DefaultRecord = defaultRecord;
        DefaultHidUsage = defaultRecord.SingleHidUsageOrFn ?? (byte)0;
    }

    public string Id { get; }
    public int KeyCodeIndex { get; }
    public string Label { get; }
    public string? SubLabel { get; }
    public int Row { get; }
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public Hi98KeyZone Zone { get; }
    public int DefaultMatrixIndex { get; }
    public Hi98KeyBindingRecord DefaultRecord { get; }
    public byte DefaultHidUsage { get; }
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;
}

/// <summary>矩阵校准结果。</summary>
public sealed class Hi98MatrixCalibrationMap
{
    public DateTimeOffset CalibratedAt { get; set; }
    public string SourceDescription { get; set; } = "";
    public Dictionary<string, int> KeyToMatrix { get; set; } = [];
    public List<int> DetectedFnMatrixIndices { get; set; } = [];
    public int MatchedCount { get; set; }
    public List<string> FallbackKeyIDs { get; set; } = [];
    public List<int> UnmappedNonZeroSlots { get; set; } = [];

    public bool IsFullyVerified => FallbackKeyIDs.Count == 0 && MatchedCount == Hi98CLayout.Keys.Count;

    public int MatrixIndexForKeyID(string id)
        => KeyToMatrix.TryGetValue(id, out int index) ? index : Hi98CLayout.DefaultMatrixIndexFor(id) ?? 0;

    public string? KeyIDForMatrixIndex(int index)
    {
        foreach (var pair in KeyToMatrix)
            if (pair.Value == index) return pair.Key;
        return null;
    }

    /// <summary>
    /// 仅允许 Fn 与 RCtrl 在槽位 53/59 间适配硬件批次差异，其余按键强制锁定出厂槽位。
    /// </summary>
    public Hi98MatrixCalibrationMap Sanitized()
    {
        var variantSlots = new HashSet<int> { 53, 59 };
        int? fnSlot = KeyToMatrix.TryGetValue("fn", out int fn) ? fn : null;
        int? rctrlSlot = KeyToMatrix.TryGetValue("rctrl", out int rctrl) ? rctrl : null;
        bool validVariantPair = fnSlot.HasValue && rctrlSlot.HasValue
            && variantSlots.Contains(fnSlot.Value) && variantSlots.Contains(rctrlSlot.Value)
            && fnSlot.Value != rctrlSlot.Value;

        var map = new Dictionary<string, int>();
        foreach (var spec in Hi98CLayout.Keys)
        {
            map[spec.Id] = spec.Id switch
            {
                "fn" => validVariantPair ? fnSlot!.Value : spec.DefaultMatrixIndex,
                "rctrl" => validVariantPair ? rctrlSlot!.Value : spec.DefaultMatrixIndex,
                _ => spec.DefaultMatrixIndex,
            };
        }

        return new Hi98MatrixCalibrationMap
        {
            CalibratedAt = CalibratedAt,
            SourceDescription = SourceDescription,
            KeyToMatrix = map,
            DetectedFnMatrixIndices = [.. DetectedFnMatrixIndices],
            MatchedCount = MatchedCount,
            FallbackKeyIDs = [.. FallbackKeyIDs],
            UnmappedNonZeroSlots = [.. UnmappedNonZeroSlots],
        };
    }
}

/// <summary>LEOBOG Hi98C Pro 101 键（类 98 配列）官方布局与矩阵定义。</summary>
public static class Hi98CLayout
{
    public const int MatrixSlotCount = 126; // 6 × 21
    public const int LayerByteLength = MatrixSlotCount * 4; // 504 bytes
    public const double CanvasOriginX = 16;
    public const double CanvasOriginY = 18;
    public const double CanvasWidth = 670;
    public const double CanvasHeight = 208;

    private static readonly Hi98KeyZone ZFunction = Hi98KeyZone.FunctionRow;
    private static readonly Hi98KeyZone ZAlpha = Hi98KeyZone.MainAlpha;
    private static readonly Hi98KeyZone ZMod = Hi98KeyZone.Modifiers;
    private static readonly Hi98KeyZone ZNav = Hi98KeyZone.Navigation;
    private static readonly Hi98KeyZone ZPad = Hi98KeyZone.Numpad;
    private static readonly Hi98KeyZone ZWasd = Hi98KeyZone.Wasd;

    private static readonly Hi98KeySlotSpec[] KeysArray = BuildKeys();

    public static IReadOnlyList<Hi98KeySlotSpec> Keys => KeysArray;

    public static IReadOnlyDictionary<string, Hi98KeySlotSpec> KeyById { get; } =
        KeysArray.ToDictionary(k => k.Id);

    public static int? DefaultMatrixIndexFor(string id)
        => KeyById.TryGetValue(id, out var spec) ? spec.DefaultMatrixIndex : null;

    private static Hi98KeySlotSpec K(string id, int keyCodeIndex, string label, int row,
        double x1, double y1, double x2, double y2, Hi98KeyZone zone, int matrixIndex,
        Hi98KeyBindingRecord record, string? subLabel = null)
        => new(id, keyCodeIndex, label, subLabel, row, x1, y1, x2, y2, zone, matrixIndex, record);

    private static Hi98KeyBindingRecord Key(byte usage) => Hi98KeyBindingRecord.Key(usage);

    private static Hi98KeySlotSpec[] BuildKeys() =>
    [
        // Row 0: Esc, F1-F12, Del, PrtSc, ScrLk, Home, End
        K("esc", 1, "Esc", 0, 21, 25, 42, 48, ZFunction, 0, Key(0x29)),
        K("f1", 2, "F1", 0, 87, 24, 108, 47, ZFunction, 12, Key(0x3A)),
        K("f2", 3, "F2", 0, 120, 25, 141, 48, ZFunction, 18, Key(0x3B)),
        K("f3", 4, "F3", 0, 152, 25, 173, 48, ZFunction, 24, Key(0x3C)),
        K("f4", 5, "F4", 0, 185, 25, 206, 48, ZFunction, 30, Key(0x3D)),
        K("f5", 6, "F5", 0, 235, 24, 256, 47, ZFunction, 36, Key(0x3E)),
        K("f6", 7, "F6", 0, 268, 25, 289, 48, ZFunction, 42, Key(0x3F)),
        K("f7", 8, "F7", 0, 300, 25, 321, 48, ZFunction, 48, Key(0x40)),
        K("f8", 9, "F8", 0, 334, 25, 355, 48, ZFunction, 54, Key(0x41)),
        K("f9", 10, "F9", 0, 383, 24, 404, 47, ZFunction, 60, Key(0x42)),
        K("f10", 11, "F10", 0, 416, 24, 437, 47, ZFunction, 66, Key(0x43)),
        K("f11", 12, "F11", 0, 449, 25, 470, 48, ZFunction, 72, Key(0x44)),
        K("f12", 13, "F12", 0, 482, 25, 503, 48, ZFunction, 78, Key(0x45)),
        K("delete", 51, "Del", 0, 521, 24, 542, 47, ZNav, 84, Key(0x4C)),
        K("prtsc", 14, "PrtSc", 0, 559, 25, 580, 48, ZNav, 90, Key(0x46)),
        K("scrlk", 15, "ScrLk", 0, 593, 25, 614, 48, ZNav, 96, Key(0x47)),
        K("home", 31, "Home", 0, 626, 24, 647, 47, ZNav, 102, Key(0x4A)),
        K("end", 52, "End", 0, 659, 24, 680, 47, ZNav, 108, Key(0x4D)),

        // Row 1: `, 1-0, -, =, Backspace, Ins, NumLock, N/, N*, N-
        K("grave", 16, "`", 1, 21, 65, 42, 88, ZAlpha, 1, Key(0x35)),
        K("1", 17, "1", 1, 54, 65, 75, 88, ZAlpha, 7, Key(0x1E)),
        K("2", 18, "2", 1, 87, 65, 108, 88, ZAlpha, 13, Key(0x1F)),
        K("3", 19, "3", 1, 120, 65, 141, 88, ZAlpha, 19, Key(0x20)),
        K("4", 20, "4", 1, 153, 65, 174, 88, ZAlpha, 25, Key(0x21)),
        K("5", 21, "5", 1, 185, 65, 206, 88, ZAlpha, 31, Key(0x22)),
        K("6", 22, "6", 1, 218, 65, 239, 88, ZAlpha, 37, Key(0x23)),
        K("7", 23, "7", 1, 251, 65, 272, 88, ZAlpha, 43, Key(0x24)),
        K("8", 24, "8", 1, 284, 65, 305, 88, ZAlpha, 49, Key(0x25)),
        K("9", 25, "9", 1, 317, 65, 338, 88, ZAlpha, 55, Key(0x26)),
        K("0", 26, "0", 1, 351, 65, 372, 88, ZAlpha, 61, Key(0x27)),
        K("minus", 27, "-", 1, 383, 65, 404, 88, ZAlpha, 67, Key(0x2D)),
        K("equal", 28, "=", 1, 416, 65, 437, 88, ZAlpha, 73, Key(0x2E)),
        K("backspace", 29, "Backspace", 1, 448, 65, 502, 88, ZMod, 79, Key(0x2A)),
        K("insert", 30, "Ins", 1, 520, 64, 541, 87, ZNav, 85, Key(0x49)),
        K("numlock", 33, "Num", 1, 560, 64, 581, 87, ZPad, 91, Key(0x53)),
        K("num_slash", 34, "/", 1, 592, 64, 613, 87, ZPad, 97, Key(0x54)),
        K("num_star", 35, "*", 1, 625, 64, 646, 87, ZPad, 103, Key(0x55)),
        K("num_minus", 36, "-", 1, 659, 65, 680, 88, ZPad, 109, Key(0x56)),

        // Row 2: Tab, QWERTYUIOP[], \, PgUp, Num7, Num8, Num9, Num+ (跨行)
        K("tab", 37, "Tab", 2, 21, 97, 60, 120, ZMod, 2, Key(0x2B)),
        K("q", 38, "Q", 2, 71, 97, 92, 120, ZAlpha, 8, Key(0x14)),
        K("w", 39, "W", 2, 104, 97, 125, 120, ZWasd, 14, Key(0x1A)),
        K("e", 40, "E", 2, 136, 97, 157, 120, ZAlpha, 20, Key(0x08)),
        K("r", 41, "R", 2, 169, 97, 190, 120, ZAlpha, 26, Key(0x15)),
        K("t", 42, "T", 2, 202, 97, 223, 120, ZAlpha, 32, Key(0x17)),
        K("y", 43, "Y", 2, 235, 97, 256, 120, ZAlpha, 38, Key(0x1C)),
        K("u", 44, "U", 2, 268, 97, 289, 120, ZAlpha, 44, Key(0x18)),
        K("i", 45, "I", 2, 301, 97, 322, 120, ZAlpha, 50, Key(0x0C)),
        K("o", 46, "O", 2, 334, 97, 355, 120, ZAlpha, 56, Key(0x12)),
        K("p", 47, "P", 2, 366, 97, 387, 120, ZAlpha, 62, Key(0x13)),
        K("lbracket", 48, "[", 2, 399, 97, 420, 120, ZAlpha, 68, Key(0x2F)),
        K("rbracket", 49, "]", 2, 432, 97, 453, 120, ZAlpha, 74, Key(0x30)),
        K("backslash", 50, "\\", 2, 462, 98, 502, 121, ZAlpha, 80, Key(0x31)),
        K("pgup", 32, "PgUp", 2, 521, 98, 542, 121, ZNav, 86, Key(0x4B)),
        K("num7", 54, "7", 2, 560, 97, 581, 120, ZPad, 92, Key(0x5F)),
        K("num8", 55, "8", 2, 592, 97, 613, 120, ZPad, 98, Key(0x60)),
        K("num9", 56, "9", 2, 625, 97, 646, 120, ZPad, 104, Key(0x61)),
        K("num_plus", 57, "+", 2, 658, 96, 679, 154, ZPad, 110, Key(0x57)),

        // Row 3: CapsLock, ASDFGHJKL;', Enter, PgDn, Num4, Num5, Num6
        K("capslock", 58, "Caps", 3, 21, 129, 67, 152, ZMod, 3, Key(0x39)),
        K("a", 59, "A", 3, 79, 130, 100, 153, ZWasd, 9, Key(0x04)),
        K("s", 60, "S", 3, 112, 130, 133, 153, ZWasd, 15, Key(0x16)),
        K("d", 61, "D", 3, 145, 130, 166, 153, ZWasd, 21, Key(0x07)),
        K("f", 62, "F", 3, 177, 130, 198, 153, ZAlpha, 27, Key(0x09)),
        K("g", 63, "G", 3, 210, 130, 231, 153, ZAlpha, 33, Key(0x0A)),
        K("h", 64, "H", 3, 243, 130, 264, 153, ZAlpha, 39, Key(0x0B)),
        K("j", 65, "J", 3, 276, 130, 297, 153, ZAlpha, 45, Key(0x0D)),
        K("k", 66, "K", 3, 309, 130, 330, 153, ZAlpha, 51, Key(0x0E)),
        K("l", 67, "L", 3, 342, 130, 363, 153, ZAlpha, 57, Key(0x0F)),
        K("semicolon", 68, ";", 3, 374, 130, 395, 153, ZAlpha, 63, Key(0x33)),
        K("quote", 69, "'", 3, 407, 130, 428, 153, ZAlpha, 69, Key(0x34)),
        K("enter", 70, "Enter", 3, 438, 130, 502, 153, ZMod, 81, Key(0x28)),
        K("pgdn", 53, "PgDn", 3, 521, 130, 542, 153, ZNav, 87, Key(0x4E)),
        K("num4", 71, "4", 3, 558, 129, 579, 152, ZPad, 93, Key(0x5C)),
        K("num5", 72, "5", 3, 592, 129, 613, 152, ZPad, 99, Key(0x5D)),
        K("num6", 73, "6", 3, 625, 129, 646, 152, ZPad, 105, Key(0x5E)),

        // Row 4: LShift, ZXCVBNM,./, RShift, Up, Num1, Num2, Num3, NumEnter (跨行)
        K("lshift", 74, "Shift", 4, 21, 162, 85, 185, ZMod, 4, Key(0xE1)),
        K("z", 75, "Z", 4, 96, 162, 117, 185, ZAlpha, 10, Key(0x1D)),
        K("x", 76, "X", 4, 129, 162, 150, 185, ZAlpha, 16, Key(0x1B)),
        K("c", 77, "C", 4, 162, 162, 183, 185, ZAlpha, 22, Key(0x06)),
        K("v", 78, "V", 4, 194, 162, 215, 185, ZAlpha, 28, Key(0x19)),
        K("b", 79, "B", 4, 227, 162, 248, 185, ZAlpha, 34, Key(0x05)),
        K("n", 80, "N", 4, 260, 162, 281, 185, ZAlpha, 40, Key(0x11)),
        K("m", 81, "M", 4, 293, 162, 314, 185, ZAlpha, 46, Key(0x10)),
        K("comma", 82, ",", 4, 326, 162, 347, 185, ZAlpha, 52, Key(0x36)),
        K("period", 83, ".", 4, 359, 162, 380, 185, ZAlpha, 58, Key(0x37)),
        K("slash", 84, "/", 4, 391, 162, 412, 185, ZAlpha, 64, Key(0x38)),
        K("rshift", 85, "RShift", 4, 422, 162, 470, 185, ZMod, 82, Key(0xE5)),
        K("up", 86, "↑", 4, 481, 162, 502, 185, ZNav, 88, Key(0x52)),
        K("num1", 87, "1", 4, 559, 162, 580, 185, ZPad, 94, Key(0x59)),
        K("num2", 88, "2", 4, 592, 162, 613, 185, ZPad, 100, Key(0x5A)),
        K("num3", 89, "3", 4, 625, 162, 646, 185, ZPad, 106, Key(0x5B)),
        K("num_enter", 101, "NEnt", 4, 658, 161, 679, 219, ZPad, 112, Key(0x58)),

        // Row 5: LCtrl, LWin/Cmd, LAlt/Opt, Space, Fn, RCtrl, Left, Down, Right, Num0, Num.
        K("lctrl", 90, "Ctrl", 5, 21, 194, 51, 217, ZMod, 5, Key(0xE0), "⌃"),
        K("lwin", 91, "Win", 5, 62, 194, 92, 217, ZMod, 11, Key(0xE3), "⌥/⌘"),
        K("lalt", 92, "Alt", 5, 101, 194, 133, 217, ZMod, 17, Key(0xE2), "⌘/⌥"),
        K("space", 93, "Space", 5, 143, 194, 339, 217, ZAlpha, 35, Key(0x2C)),
        K("fn", 94, "Fn", 5, 347, 194, 379, 217, ZMod, 53, Hi98KeyBindingRecord.FnKey),
        K("rctrl", 95, "RCtrl", 5, 389, 194, 421, 217, ZMod, 59, Key(0xE4), "R⌥/R⌃"),
        K("left", 96, "←", 5, 448, 195, 469, 218, ZNav, 83, Key(0x50)),
        K("down", 97, "↓", 5, 481, 194, 502, 217, ZNav, 89, Key(0x51)),
        K("right", 98, "→", 5, 514, 195, 535, 218, ZNav, 95, Key(0x4F)),
        K("num0", 99, "0", 5, 560, 194, 612, 217, ZPad, 101, Key(0x62)),
        K("num_dot", 100, ".", 5, 624, 195, 645, 218, ZPad, 107, Key(0x63)),
    ];

    /// <summary>各物理层在 Hi98C Pro 硬件出厂时的默认 4 字节键位记录。</summary>
    public static Hi98KeyBindingRecord DefaultRecordFor(Hi98KeySlotSpec spec, Hi98KeyLayer layer)
    {
        switch (layer.RawValue)
        {
            case 0: // Base
                return spec.DefaultRecord;

            case 3: // MacPresetBase
                return spec.Id switch
                {
                    "lwin" => Key(0xE2),                                         // LOpt ⌥
                    "lalt" => Key(0xE3),                                         // LCmd ⌘
                    "f1" => Hi98KeyBindingRecord.Consumer(0x0070),               // 屏幕亮度-
                    "f2" => Hi98KeyBindingRecord.Consumer(0x006F),               // 屏幕亮度+
                    "f3" => Hi98KeyBindingRecord.Key(0x2B, Hi98ModifierMask.LeftCommand), // ⌘+Tab
                    "f4" => Hi98KeyBindingRecord.Key(0x0B, Hi98ModifierMask.LeftCommand), // ⌘+H
                    "f5" => Hi98KeyBindingRecord.Lighting(0x03, 0x02),           // 灯效亮度-
                    "f6" => Hi98KeyBindingRecord.Lighting(0x03, 0x01),           // 灯效亮度+
                    "f7" => Hi98KeyBindingRecord.Consumer(0x00B6),               // 上一曲
                    "f8" => Hi98KeyBindingRecord.Consumer(0x00CD),               // 播放/暂停
                    "f9" => Hi98KeyBindingRecord.Consumer(0x00B5),               // 下一曲
                    "f10" => Hi98KeyBindingRecord.Consumer(0x00E2),              // 静音
                    "f11" => Hi98KeyBindingRecord.Consumer(0x00EA),              // 音量-
                    "f12" => Hi98KeyBindingRecord.Consumer(0x00E9),              // 音量+
                    _ => spec.DefaultRecord,
                };

            case 4: // MacPresetFn1
                if (spec.Id.Length == 2 && spec.Id[0] == 'f'
                    && int.TryParse(spec.Id.AsSpan(1), out int fNum) && fNum >= 1 && fNum <= 12)
                    return Key((byte)(0x3A + fNum - 1));
                return spec.Id switch
                {
                    "esc" => Hi98KeyBindingRecord.System(0x04),
                    "grave" => Hi98KeyBindingRecord.System(0x08),
                    "1" => Hi98KeyBindingRecord.System(0x05),
                    "2" => Hi98KeyBindingRecord.System(0x06),
                    "3" => Hi98KeyBindingRecord.System(0x07),
                    "q" => Hi98KeyBindingRecord.System(0x19),
                    "w" => Hi98KeyBindingRecord.System(0x18),
                    "e" => Hi98KeyBindingRecord.System(0x1A),
                    "b" => Hi98KeyBindingRecord.System(0x11),
                    "fn" => Hi98KeyBindingRecord.FnKey,
                    _ => Hi98KeyBindingRecord.Empty,
                };

            case 1: // Fn1
                return spec.Id switch
                {
                    "esc" => Hi98KeyBindingRecord.System(0x04),
                    "grave" => Hi98KeyBindingRecord.System(0x08),
                    "1" => Hi98KeyBindingRecord.System(0x05),
                    "2" => Hi98KeyBindingRecord.System(0x06),
                    "3" => Hi98KeyBindingRecord.System(0x07),
                    "q" => Hi98KeyBindingRecord.System(0x19),
                    "w" => Hi98KeyBindingRecord.System(0x18),
                    "e" => Hi98KeyBindingRecord.System(0x1A),
                    "b" => Hi98KeyBindingRecord.System(0x11),
                    "lwin" => Hi98KeyBindingRecord.System(0x01),
                    "f1" => Hi98KeyBindingRecord.Consumer(0x0223),
                    "f2" => Hi98KeyBindingRecord.Consumer(0x018A),
                    "f3" => Hi98KeyBindingRecord.Key(0x2B, Hi98ModifierMask.LeftAlt),
                    "f4" => Hi98KeyBindingRecord.Key(0x08, Hi98ModifierMask.LeftCommand),
                    "f5" => Hi98KeyBindingRecord.Lighting(0x03, 0x02),
                    "f6" => Hi98KeyBindingRecord.Lighting(0x03, 0x01),
                    "f7" => Hi98KeyBindingRecord.Consumer(0x00B6),
                    "f8" => Hi98KeyBindingRecord.Consumer(0x00CD),
                    "f9" => Hi98KeyBindingRecord.Consumer(0x00B5),
                    "f10" => Hi98KeyBindingRecord.Consumer(0x00E2),
                    "f11" => Hi98KeyBindingRecord.Consumer(0x00EA),
                    "f12" => Hi98KeyBindingRecord.Consumer(0x00E9),
                    "fn" => Hi98KeyBindingRecord.FnKey,
                    _ => Hi98KeyBindingRecord.Empty,
                };

            case 2: // Fn2
                return Hi98KeyBindingRecord.Empty;

            default:
                return spec.DefaultRecord;
        }
    }

    public static Hi98MatrixCalibrationMap DefaultCalibration { get; } = new()
    {
        CalibratedAt = DateTimeOffset.FromUnixTimeSeconds(0),
        SourceDescription = "官方 Hi98 3-Mode KB.ini 默认矩阵",
        KeyToMatrix = KeysArray.ToDictionary(k => k.Id, k => k.DefaultMatrixIndex),
        DetectedFnMatrixIndices = [53],
        MatchedCount = KeysArray.Length,
        FallbackKeyIDs = [],
        UnmappedNonZeroSlots = [],
    };
}

/// <summary>根据真机读取的 504 字节自动校准 101 键 ↔ matrixIndex (0..&lt;126) 映射。</summary>
public static class Hi98MatrixCalibrator
{
    private static List<Hi98KeyBindingRecord> DecodeSlots(ReadOnlySpan<byte> payload)
    {
        var slots = new List<Hi98KeyBindingRecord>(Hi98CLayout.MatrixSlotCount);
        for (int idx = 0; idx < Hi98CLayout.MatrixSlotCount; idx++)
        {
            int offset = idx * 4;
            slots.Add(offset + 4 <= payload.Length
                ? new Hi98KeyBindingRecord(payload.Slice(offset, 4).ToArray())
                : Hi98KeyBindingRecord.Empty);
        }
        return slots;
    }

    private static bool Matches(Hi98KeyBindingRecord actual, Hi98KeyBindingRecord expected,
        bool allowRAltForRCtrl = false)
    {
        if (actual.Equals(expected)) return true;
        if (expected.SingleHidUsageOrFn is { } expectedUsage && actual.SingleHidUsageOrFn is { } actualUsage)
        {
            if (actualUsage == expectedUsage) return true;
            if (allowRAltForRCtrl && expectedUsage == 0xE4 && actualUsage == 0xE6) return true;
        }
        return false;
    }

    public static Hi98MatrixCalibrationMap Calibrate(byte[] layer0Payload, byte[]? macPresetPayload = null,
        string sourceDescription = "USB 有线 Layer 0 实机校准")
    {
        ArgumentNullException.ThrowIfNull(layer0Payload);

        var layer0Slots = DecodeSlots(layer0Payload);
        bool hasValidMacPreset = (macPresetPayload?.Length ?? 0) >= Hi98CLayout.LayerByteLength;
        var verifySlots = hasValidMacPreset ? DecodeSlots(macPresetPayload!) : layer0Slots;

        var keyToMatrix = new Dictionary<string, int>();
        var usedSlots = new HashSet<int>();
        int matchedCount = 0;
        var fallbackKeyIDs = new List<string>();

        var variantIDs = new HashSet<string> { "fn", "rctrl" };
        int[] variantSlots = [53, 59];

        Hi98KeyBindingRecord Expected(Hi98KeySlotSpec spec)
            => hasValidMacPreset
                ? Hi98CLayout.DefaultRecordFor(spec, Hi98KeyLayer.MacPresetBase)
                : spec.DefaultRecord;

        // 1. 锁定全部固定物理矩阵键位，避免用户改键或切换 Opt/Cmd 被误判为硬件换槽。
        foreach (var spec in Hi98CLayout.Keys.Where(s => !variantIDs.Contains(s.Id)))
        {
            int slot = spec.DefaultMatrixIndex;
            keyToMatrix[spec.Id] = slot;
            usedSlots.Add(slot);
            if (slot < verifySlots.Count && Matches(verifySlots[slot], Expected(spec)))
                matchedCount++;
            else
                fallbackKeyIDs.Add(spec.Id);
        }

        // 2. 仅允许 fn / rctrl 在批次差异候选槽位 {53, 59} 内对齐。
        foreach (var spec in Hi98CLayout.Keys.Where(s => variantIDs.Contains(s.Id)))
        {
            int defaultSlot = spec.DefaultMatrixIndex;
            if (defaultSlot < verifySlots.Count && !usedSlots.Contains(defaultSlot)
                && Matches(verifySlots[defaultSlot], Expected(spec), spec.Id == "rctrl"))
            {
                keyToMatrix[spec.Id] = defaultSlot;
                usedSlots.Add(defaultSlot);
                matchedCount++;
            }
        }

        foreach (var spec in Hi98CLayout.Keys.Where(s => variantIDs.Contains(s.Id) && !keyToMatrix.ContainsKey(s.Id)))
        {
            int? matchedSlot = null;
            foreach (int slot in variantSlots)
            {
                if (!usedSlots.Contains(slot) && slot < verifySlots.Count
                    && Matches(verifySlots[slot], Expected(spec), spec.Id == "rctrl"))
                {
                    matchedSlot = slot;
                    break;
                }
            }
            if (matchedSlot.HasValue)
            {
                keyToMatrix[spec.Id] = matchedSlot.Value;
                usedSlots.Add(matchedSlot.Value);
                matchedCount++;
            }
            else
            {
                fallbackKeyIDs.Add(spec.Id);
                int fallbackSlot = variantSlots.FirstOrDefault(s => !usedSlots.Contains(s), spec.DefaultMatrixIndex);
                keyToMatrix[spec.Id] = fallbackSlot;
                usedSlots.Add(fallbackSlot);
            }
        }

        var detectedFn = layer0Slots.Select((rec, idx) => (rec, idx))
            .Where(x => x.rec.IsFnKey).Select(x => x.idx).ToList();
        var unmappedNonZero = verifySlots.Select((rec, idx) => (rec, idx))
            .Where(x => !x.rec.IsEmpty && !usedSlots.Contains(x.idx)).Select(x => x.idx).ToList();

        return new Hi98MatrixCalibrationMap
        {
            CalibratedAt = DateTimeOffset.UtcNow,
            SourceDescription = sourceDescription,
            KeyToMatrix = keyToMatrix,
            DetectedFnMatrixIndices = detectedFn,
            MatchedCount = matchedCount,
            FallbackKeyIDs = fallbackKeyIDs,
            UnmappedNonZeroSlots = unmappedNonZero,
        }.Sanitized();
    }
}

/// <summary>RGB 颜色结构。</summary>
public sealed record Hi98RgbColor(byte R, byte G, byte B)
{
    public static Hi98RgbColor FromHex(string hex)
    {
        string cleaned = hex.Trim().Replace("#", "", StringComparison.Ordinal);
        if (cleaned.Length == 6 && uint.TryParse(cleaned, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out uint value))
            return new Hi98RgbColor((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
        return new Hi98RgbColor(0, 229, 255);
    }

    public string HexString => $"#{R:X2}{G:X2}{B:X2}";

    public bool IsBlack => R == 0 && G == 0 && B == 0;

    public static Hi98RgbColor Black => new(0, 0, 0);
    public static Hi98RgbColor White => new(255, 255, 255);
    public static Hi98RgbColor CyberCyan => new(0, 229, 255);
}

/// <summary>板载灯效定义（基于官方 KB.ini LedOpt 与实测）。</summary>
public sealed record Hi98OnboardLightingEffect(
    byte Id, int UiOrder, string Name, string Description, bool SupportsSpeed, bool SupportsColor,
    bool IsCustomPerKey = false)
{
    public const byte CustomPerKeyHardwareID = 32;

    public static IReadOnlyList<Hi98OnboardLightingEffect> AllEffects { get; } =
    [
        new(1, 1, "常亮 (Static)", "全键稳定单一背光", false, true),
        new(2, 2, "呼吸 (Breathing)", "柔和呼吸明暗交替", true, true),
        new(3, 3, "七彩渐变 (Spectrum)", "全键盘循环光谱渐变", true, false),
        new(4, 4, "行涟漪 (Row Ripple)", "按键沿所在行扩散光波", true, true),
        new(5, 5, "雨滴落雨 (Raindrops)", "随机键位雨滴闪烁", true, true),
        new(6, 6, "霓虹慢波 (Slow Wave)", "从左向右平滑渐变波浪", true, true),
        new(7, 7, "全盘涟漪 (Ripple)", "敲击向全键盘扩散水波", true, true),
        new(8, 8, "繁星点点 (Twinkle)", "星光随机闪烁", true, true),
        new(10, 9, "贪吃蛇 (Snake)", "灯带如游蛇穿梭全键行", true, true),
        new(11, 10, "随波逐流 (Stream)", "横向流体光效", true, true),
        new(12, 11, "单点即灭 (Reactive)", "按下点亮并渐隐", true, true),
        new(13, 12, "中心波浪 (Center Wave)", "由中心向两侧展开", true, true),
        new(15, 13, "彩虹轮盘 (Wheel)", "全盘旋转彩虹轮", true, false),
        new(16, 14, "瀑布流光 (Waterfall)", "自上而下彩色瀑布", true, false),
        new(17, 15, "中心快闪 (Flash)", "高节奏中心闪耀", true, true),
        new(32, 16, "单键自定义 (Per-Key)", "使用 0x06 自定义单键静态表", false, false, true),
        new(0, 17, "关闭背光 (Off)", "完全关闭键盘背光以省电", false, false),
    ];

    public static Hi98OnboardLightingEffect? Find(byte hardwareID)
        => hardwareID == 21
            ? AllEffects.First(e => e.Id == CustomPerKeyHardwareID)
            : AllEffects.FirstOrDefault(e => e.Id == hardwareID);
}

/// <summary>单个灯效槽位参数（128 字节配置块偏移 0x3A + 2*(hw-1)）。</summary>
public sealed class Hi98OnboardEffectSlot
{
    public Hi98OnboardEffectSlot(byte brightness = 4, byte speed = 2, byte colorIndex = 7)
    {
        Brightness = brightness;
        Speed = speed;
        ColorIndex = colorIndex;
    }

    public byte Brightness { get; set; } // 0..9
    public byte Speed { get; set; }       // 0..4
    public byte ColorIndex { get; set; }  // 0..6，7 = 七彩随机
}

/// <summary>键盘操作系统兼容模式。</summary>
public enum Hi98OsMode : byte
{
    Windows = 0,
    Android = 1,
    Mac = 2,
}

public static class Hi98OsModeExtensions
{
    /// <summary>仅 Windows (Fn+W, raw 0) 读取可编程默认层/Fn1/Fn2 键位与硬件宏。</summary>
    public static bool SupportsCustomKeymap(this Hi98OsMode mode) => mode == Hi98OsMode.Windows;

    public static string Title(this Hi98OsMode mode) => mode switch
    {
        Hi98OsMode.Windows => "自定义 / Win 模式 (Fn+W)",
        Hi98OsMode.Android => "Android 模式 (Fn+Q)",
        Hi98OsMode.Mac => "Mac 固定预设模式 (Fn+E)",
        _ => "未知模式",
    };
}

/// <summary>侧灯条模式。</summary>
public enum Hi98SideLightMode : byte
{
    Off = 0,
    Fill = 1,
    Breathing = 2,
    StaticOn = 3,
    BreathingAlt = 4,
}

public static class Hi98SideLightModeExtensions
{
    public static string Title(this Hi98SideLightMode mode) => mode switch
    {
        Hi98SideLightMode.Off => "关闭 / 电量指示",
        Hi98SideLightMode.Fill => "流光填充",
        Hi98SideLightMode.Breathing => "呼吸灯",
        Hi98SideLightMode.StaticOn => "常亮",
        Hi98SideLightMode.BreathingAlt => "渐变呼吸",
        _ => "未知模式",
    };
}

/// <summary>128 字节 LED 与硬件设置块解析/序列化模型。</summary>
public sealed class Hi98LedConfigBlock
{
    public const int ByteLength = 128;
    public static readonly byte[] MagicBytes = [0x5A, 0xA5];

    public const int PerKeyFlagOffset = 0x09;
    public const int EffectOffset = 0x0A;
    public const int WinLockOffset = 0x0F;
    public const int SideModeOffset = 0x12;
    public const int SleepOffset = 0x18; // 单位 30 秒；0 = 永不休眠（本模型禁止写入 0）
    public const int KnobModeOffset = 0x1A;
    public const int OsModeOffset = 0x1B;
    public const int SlotsBaseOffset = 0x3A;
    public const int MaxSlotEffectID = 34;

    /// <summary>完整保留读取到的 128 字节原始数据，只局部修改已知语义字段。</summary>
    public Hi98LedConfigBlock(byte[] rawBytes)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        if (!IsValidSignature(rawBytes))
            throw new ArgumentException(
                "读取到的键盘配置页缺少 0x5A 0xA5 完整性签名（可能键盘正在忙或休眠），请重试。",
                nameof(rawBytes));
        RawBytes = rawBytes.ToArray();
    }

    public byte[] RawBytes { get; }

    public static bool IsValidSignature(ReadOnlySpan<byte> bytes)
        => bytes.Length == ByteLength && bytes[ByteLength - 2] == MagicBytes[0] && bytes[ByteLength - 1] == MagicBytes[1];

    public static Hi98LedConfigBlock DefaultTemplate()
    {
        var bytes = new byte[ByteLength];
        bytes[EffectOffset] = 10;  // DefLedIndex=10 -> hw 10 (贪吃蛇)
        bytes[SleepOffset] = 10;   // 5 分钟 (10 * 30s)
        bytes[OsModeOffset] = 2;   // Mac 模式
        bytes[ByteLength - 2] = 0x5A;
        bytes[ByteLength - 1] = 0xA5;
        return new Hi98LedConfigBlock(bytes);
    }

    /// <summary>兼容检测：KB.ini 为 0..4，部分同芯固件槽位支持 0..9。</summary>
    public byte DetectedMaxBrightness
    {
        get
        {
            for (byte id = 1; id <= MaxSlotEffectID; id++)
            {
                int off = SlotsBaseOffset + 2 * (id - 1);
                if (off < ByteLength - 2 && RawBytes[off] > 4) return 9;
            }
            return 4;
        }
    }

    public bool IsPerKeyCustomMode
    {
        get => RawBytes[PerKeyFlagOffset] != 0;
        set => RawBytes[PerKeyFlagOffset] = value ? (byte)1 : (byte)0;
    }

    public byte EffectHardwareID
    {
        get => RawBytes[EffectOffset];
        set => RawBytes[EffectOffset] = value;
    }

    public bool WinKeyLocked
    {
        get => RawBytes[WinLockOffset] != 0;
        set => RawBytes[WinLockOffset] = value ? (byte)1 : (byte)0;
    }

    public byte SideLightModeRaw
    {
        get => RawBytes[SideModeOffset];
        set => RawBytes[SideModeOffset] = Math.Min((byte)4, value);
    }

    public Hi98SideLightMode SideLightMode
    {
        get => (Hi98SideLightMode)SideLightModeRaw;
        set => SideLightModeRaw = (byte)value;
    }

    public int SleepTimerSeconds
    {
        get => RawBytes[SleepOffset] * 30;
        set
        {
            // 硬约束：最小值 30s，严禁写 0，避免无线电池耗尽。
            int clamped = Math.Clamp(value, 30, 1800);
            RawBytes[SleepOffset] = (byte)(clamped / 30);
        }
    }

    public byte OsModeRaw
    {
        get => RawBytes[OsModeOffset];
        set => RawBytes[OsModeOffset] = Math.Min((byte)2, value);
    }

    public Hi98OsMode OsMode
    {
        get => (Hi98OsMode)OsModeRaw;
        set => OsModeRaw = (byte)value;
    }

    public Hi98OnboardEffectSlot? SlotForEffect(byte id)
    {
        if (id < 1 || id > MaxSlotEffectID) return null;
        int offset = SlotsBaseOffset + 2 * (id - 1);
        if (offset + 1 >= ByteLength - 2) return null;
        byte b = RawBytes[offset];
        byte sc = RawBytes[offset + 1];
        return new Hi98OnboardEffectSlot(
            Math.Min((byte)9, b),
            Math.Min((byte)4, (byte)((sc >> 4) & 0x0F)),
            Math.Min((byte)7, (byte)(sc & 0x0F)));
    }

    public void SetSlot(Hi98OnboardEffectSlot slot, byte id)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (id < 1 || id > MaxSlotEffectID) return;
        int offset = SlotsBaseOffset + 2 * (id - 1);
        if (offset + 1 >= ByteLength - 2) return;
        RawBytes[offset] = Math.Min((byte)9, slot.Brightness);
        RawBytes[offset + 1] = (byte)((Math.Min((byte)4, slot.Speed) << 4) | Math.Min((byte)7, slot.ColorIndex));
    }
}

/// <summary>单键静态配色主题预设（101 键 Hi98C Pro 几何坐标实时渲染）。</summary>
public sealed class Hi98RgbThemePreset
{
    public Hi98RgbThemePreset(string id, string name, string description,
        Dictionary<int, Hi98RgbColor> colorsByMatrix)
    {
        Id = id;
        Name = name;
        Description = description;
        ColorsByMatrix = colorsByMatrix;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public Dictionary<int, Hi98RgbColor> ColorsByMatrix { get; }

    public static IReadOnlyList<Hi98RgbThemePreset> AllPresets { get; } = BuildPresets();

    private static Dictionary<int, Hi98RgbColor> Gradient((double Position, Hi98RgbColor Color)[] stops,
        bool directionX = true, Dictionary<string, Hi98RgbColor>? accents = null)
    {
        var xs = Hi98CLayout.Keys.Select(k => k.CenterX).ToList();
        var ys = Hi98CLayout.Keys.Select(k => k.CenterY).ToList();
        double minX = xs.Count > 0 ? xs.Min() : 0, maxX = xs.Count > 0 ? xs.Max() : 1;
        double minY = ys.Count > 0 ? ys.Min() : 0, maxY = ys.Count > 0 ? ys.Max() : 1;

        var result = new Dictionary<int, Hi98RgbColor>();
        foreach (var spec in Hi98CLayout.Keys)
        {
            double t = directionX
                ? (spec.CenterX - minX) / Math.Max(1, maxX - minX)
                : (spec.CenterY - minY) / Math.Max(1, maxY - minY);
            result[spec.DefaultMatrixIndex] = Interpolate(stops, t);
        }
        if (accents != null)
        {
            foreach (var (keyID, accentColor) in accents)
                if (Hi98CLayout.DefaultMatrixIndexFor(keyID) is { } idx)
                    result[idx] = accentColor;
        }
        return result;
    }

    private static Hi98RgbColor Interpolate((double Position, Hi98RgbColor Color)[] stops, double t)
    {
        double clamped = Math.Clamp(t, 0, 1);
        for (int i = 0; i < stops.Length - 1; i++)
        {
            var (t0, c0) = stops[i];
            var (t1, c1) = stops[i + 1];
            if (clamped <= t1)
            {
                double f = (clamped - t0) / Math.Max(0.0001, t1 - t0);
                return new Hi98RgbColor(
                    (byte)(c0.R + (c1.R - c0.R) * f),
                    (byte)(c0.G + (c1.G - c0.G) * f),
                    (byte)(c0.B + (c1.B - c0.B) * f));
            }
        }
        return stops.Length > 0 ? stops[^1].Color : Hi98RgbColor.White;
    }

    private static IReadOnlyList<Hi98RgbThemePreset> BuildPresets()
    {
        var whiteWASD = new Dictionary<string, Hi98RgbColor>
        {
            ["w"] = Hi98RgbColor.White, ["a"] = Hi98RgbColor.White,
            ["s"] = Hi98RgbColor.White, ["d"] = Hi98RgbColor.White,
            ["up"] = Hi98RgbColor.White, ["down"] = Hi98RgbColor.White,
            ["left"] = Hi98RgbColor.White, ["right"] = Hi98RgbColor.White,
            ["esc"] = new Hi98RgbColor(255, 85, 0),
        };

        var macStudio = new Dictionary<int, Hi98RgbColor>();
        foreach (var spec in Hi98CLayout.Keys)
        {
            macStudio[spec.DefaultMatrixIndex] = spec.Zone.RawValue switch
            {
                "数字小键盘" => new Hi98RgbColor(255, 175, 40),
                "修饰与控制" => new Hi98RgbColor(70, 130, 255),
                "导航与方向键" => new Hi98RgbColor(0, 210, 255),
                "F 功能区" => new Hi98RgbColor(180, 220, 255),
                _ => new Hi98RgbColor(220, 240, 255),
            };
        }
        if (Hi98CLayout.DefaultMatrixIndexFor("esc") is { } escIndex)
            macStudio[escIndex] = new Hi98RgbColor(40, 230, 120);

        return
        [
            new("cyberpunk", "赛博霓虹", "紫红到电光青横向渐变 + 白色 WASD",
                Gradient(
                    [(0.0, new Hi98RgbColor(120, 0, 255)), (0.5, new Hi98RgbColor(255, 0, 140)), (1.0, Hi98RgbColor.CyberCyan)],
                    accents: whiteWASD)),
            new("aurora", "极光青绿", "深邃极夜蓝过渡至极光翡翠绿与紫罗兰",
                Gradient(
                    [(0.0, new Hi98RgbColor(15, 30, 140)), (0.5, new Hi98RgbColor(0, 255, 145)), (1.0, new Hi98RgbColor(140, 0, 255))],
                    accents: new Dictionary<string, Hi98RgbColor>
                    {
                        ["esc"] = Hi98RgbColor.White,
                        ["enter"] = new Hi98RgbColor(0, 255, 255),
                    })),
            new("sunset", "日落熔金", "暮紫到暖橙与金黄",
                Gradient(
                    [(0.0, new Hi98RgbColor(65, 15, 120)), (0.55, new Hi98RgbColor(255, 95, 0)), (1.0, new Hi98RgbColor(255, 215, 0))],
                    accents: new Dictionary<string, Hi98RgbColor> { ["esc"] = Hi98RgbColor.White })),
            new("mac_studio", "macOS 办公专注", "冰蓝工作区 + 琥珀数字小键盘 + 绿色 Esc", macStudio),
            new("ocean", "深海浪潮", "自上而下海沫白到深海蓝",
                Gradient(
                    [(0.0, new Hi98RgbColor(200, 255, 255)), (0.4, new Hi98RgbColor(0, 220, 210)), (1.0, new Hi98RgbColor(0, 45, 175))],
                    directionX: false,
                    accents: whiteWASD)),
        ];
    }
}
