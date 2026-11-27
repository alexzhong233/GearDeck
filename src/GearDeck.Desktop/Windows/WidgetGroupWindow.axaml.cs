using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using GearDeck.Core.Models;
using GearDeck.Desktop.Controls;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Windows;

/// <summary>
/// 任务栏分组点击后弹出的轻量无边框半透明置顶小窗；点击设备调用 MainWindow.Instance?.OpenDevice(id)。
/// </summary>
public sealed partial class WidgetGroupWindow : Window
{
    private static readonly FontFamily IconFont = Controls.UiBuilders.IconFont;
    private static WidgetGroupWindow? _open;

    public string GroupId { get; }

    public static bool IsOpen => _open != null;

    public WidgetGroupWindow() : this("builtin:mouse")
    {
    }

    public WidgetGroupWindow(string groupId)
    {
        InitializeComponent();
        GroupId = string.IsNullOrWhiteSpace(groupId) ? "builtin:mouse" : groupId;
        Title = "GearDeck Flyout";
        _open = this;

        Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, this)) _open = null;
        };

        RebuildDevices();
    }

    /// <summary>供既有按 Rect 锚点反射创建路径兼容。</summary>
    public WidgetGroupWindow(string groupId, Rect anchor) : this(groupId)
    {
        if (anchor.Width > 0 && anchor.Height > 0)
            UpdateAnchor((int)anchor.X, (int)anchor.Y, (int)anchor.Width, (int)anchor.Height);
    }

    /// <summary>在给定位置与尺寸显示分组小窗；重复调用会先关闭旧实例。</summary>
    public static WidgetGroupWindow? ShowNear(int x, int y, int w, int h, string groupId)
    {
        WidgetGroupWindow? created = null;
        void Run()
        {
            CloseIfOpenCore();
            created = new WidgetGroupWindow(groupId);
            created.UpdateAnchor(x, y, w, h);
            created.Show();
            _open = created;
        }
        if (Dispatcher.UIThread.CheckAccess()) Run();
        else Dispatcher.UIThread.Invoke(Run);
        return created;
    }

    public static void CloseIfOpen()
    {
        if (Dispatcher.UIThread.CheckAccess()) CloseIfOpenCore();
        else Dispatcher.UIThread.Invoke(CloseIfOpenCore);
    }

    private static void CloseIfOpenCore()
    {
        var window = _open;
        _open = null;
        if (window != null)
        {
            try { window.Close(); }
            catch { }
        }
    }

    /// <summary>更新浮窗的物理像素位置与尺寸。</summary>
    public void UpdateAnchor(int x, int y, int w, int h)
    {
        Position = new PixelPoint(x, y);
        if (w > 0) Width = Math.Clamp(w, 230, 460);
        if (h > 0) Height = Math.Clamp(h, 110, 520);
        Topmost = true;
    }

    private void RebuildDevices()
    {
        var members = MembersOf(GroupId);
        string label = GroupId switch
        {
            "builtin:mouse" => "鼠标组",
            "builtin:keyboard" => "键盘组",
            "builtin:headset" => "耳机组",
            "builtin:controller" => "手柄组",
            _ => App.Settings.Current.CustomGroups.FirstOrDefault(g => g.Id == GroupId)?.DisplayName ?? "设备组",
        };
        TitleText.Text = label;

        string glyph = GroupIconCatalog.ResolveEffectiveGlyph(
            new TaskbarGroupConfig { GroupId = GroupId, Visible = true }, members);
        IconText.Text = glyph;
        IconText.FontFamily = IconFont;

        DeviceHost.Children.Clear();
        if (members.Count == 0)
        {
            DeviceHost.Children.Add(new TextBlock
            {
                Text = "无设备",
                FontSize = 13,
                Margin = new Thickness(8, 12, 8, 12),
                Foreground = new SolidColorBrush(Colors.LightGray),
            });
            return;
        }

        foreach (var device in members)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 8, 10, 8),
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(UiBuilders.CreateDeviceAvatar(device, 28));

            var info = new StackPanel { Spacing = 1, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = device.Name,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
            });
            info.Children.Add(new TextBlock
            {
                Text = device.IsSleeping ? $"{device.Provider} · 休眠" : $"{device.Provider} · {device.Transport}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.LightGray),
            });
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            var battery = new TextBlock
            {
                Text = BatteryText(device),
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(battery, 2);
            row.Children.Add(battery);

            card.Child = row;
            card.Tapped += (_, _) =>
            {
                MainWindow.Instance?.OpenDevice(device.Id);
                Close();
            };
            DeviceHost.Children.Add(card);
        }
    }

    private static string BatteryText(PeripheralDevice device)
    {
        var cached = App.Settings.GetLastKnownBattery(device.Id);
        bool isDual = device.LeftEarPercent.HasValue || device.RightEarPercent.HasValue
                      || (cached != null && (cached.LeftEarPercent.HasValue || cached.RightEarPercent.HasValue));
        if (device.FormatDualEarSummary(compact: true) is { } dual)
            return dual;
        if (isDual && cached != null)
        {
            string l = cached.LeftEarPercent is { } lv ? $"{lv}%" : "--";
            string r = cached.RightEarPercent is { } rv ? $"{rv}%" : "--";
            return $"L{l} R{r} (历史)";
        }
        if (device.BatteryPercent is { } p)
            return device.State == BatteryState.Charging ? $"{p}%⚡" : $"{p}%";
        if (cached?.BatteryPercent is { } hp)
            return device.IsSleeping ? $"{hp}% (休眠)" : $"{hp}% (历史)";
        return "--";
    }

    private static List<PeripheralDevice> MembersOf(string groupId)
    {
        var all = App.LastDevices;
        return groupId switch
        {
            "builtin:mouse" => all.Where(d => d.Kind == DeviceKind.Mouse).ToList(),
            "builtin:keyboard" => all.Where(d => d.Kind == DeviceKind.Keyboard).ToList(),
            "builtin:headset" => all.Where(d => d.Kind == DeviceKind.Headset).ToList(),
            "builtin:controller" => all.Where(d => d.Kind == DeviceKind.Controller).ToList(),
            _ => all.Where(d => App.Settings.Current.CustomGroups
                .FirstOrDefault(g => g.Id == groupId)?.MemberIds.Contains(d.Id) == true).ToList(),
        };
    }
}
