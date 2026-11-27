using GearDeck.Core.Models;

namespace GearDeck.Desktop.Services;

/// <summary>任务栏/浮窗分组设备成员解析共用助手。</summary>
public static class TaskbarGroupHelper
{
    public static List<PeripheralDevice> GetMembers(string groupId, IReadOnlyList<PeripheralDevice> devices, bool connectedOnly = false)
    {
        IEnumerable<PeripheralDevice> q = groupId switch
        {
            "builtin:mouse" => devices.Where(d => d.Kind == DeviceKind.Mouse),
            "builtin:keyboard" => devices.Where(d => d.Kind == DeviceKind.Keyboard),
            "builtin:headset" => devices.Where(d => d.Kind == DeviceKind.Headset),
            "builtin:controller" => devices.Where(d => d.Kind == DeviceKind.Controller),
            _ => devices.Where(d => App.Settings.Current.CustomGroups
                .FirstOrDefault(g => g.Id == groupId)?.MemberIds.Contains(d.Id) == true),
        };
        if (connectedOnly)
            q = q.Where(d => d.Connected);
        return q.ToList();
    }
}
