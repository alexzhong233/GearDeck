using GearDeck.Core.Models;

namespace GearDeck.Desktop.Services;

public sealed record PresetGroupIcon(string Glyph, string Label);

public static class GroupIconCatalog
{
    public static IReadOnlyList<PresetGroupIcon> Presets { get; } =
    [
        new("\uE962", "鼠标"),
        new("\uE765", "键盘"),
        new("\uE95B", "耳机"),
        new("\uEC4F", "耳麦"),
        new("\uE7FC", "手柄"),
        new("\uE7C8", "准星"),
        new("\uE945", "闪电"),
        new("\uE701", "无线"),
        new("\uE702", "蓝牙"),
        new("\uE9F5", "芯片"),
        new("\uE83F", "电池"),
        new("\uE734", "星标"),
        new("\uE720", "麦克风"),
        new("\uE8D6", "音箱"),
        new("\uE975", "主机"),
        new("\uEC4A", "仪表"),
    ];

    public static string ResolveDefaultGlyph(string groupId, IEnumerable<PeripheralDevice> members)
    {
        return groupId switch
        {
            "builtin:keyboard" => "\uE765",
            "builtin:headset" => "\uE95B",
            "builtin:controller" => "\uE7FC",
            _ => members.FirstOrDefault()?.Kind switch
            {
                DeviceKind.Keyboard => "\uE765",
                DeviceKind.Headset => "\uE95B",
                DeviceKind.Controller => "\uE7FC",
                _ => "\uE962",
            },
        };
    }

    public static string ResolveEffectiveGlyph(TaskbarGroupConfig cfg, IEnumerable<PeripheralDevice> members)
    {
        return !string.IsNullOrWhiteSpace(cfg.CustomGlyph)
            ? cfg.CustomGlyph
            : ResolveDefaultGlyph(cfg.GroupId, members);
    }

    public static string? ResolveValidIconPath(TaskbarGroupConfig cfg)
    {
        return !string.IsNullOrWhiteSpace(cfg.CustomIconPath) && File.Exists(cfg.CustomIconPath)
            ? cfg.CustomIconPath
            : null;
    }
}
