using HidSharp;

namespace GearDeck.Core.Hid;

internal sealed record HidReportIds(IReadOnlyList<byte> FeatureIds, IReadOnlyList<byte> OutputIds, IReadOnlyList<byte> InputIds);

internal static class HidReportInspector
{
    public static HidReportIds GetReportIds(HidDevice device)
    {
        var feat = new List<byte>();
        var outIds = new List<byte>();
        var inIds = new List<byte>();
        try
        {
            var desc = device.GetReportDescriptor();
            foreach (var r in desc.FeatureReports)
                if (!feat.Contains(r.ReportID)) feat.Add(r.ReportID);
            foreach (var r in desc.OutputReports)
                if (!outIds.Contains(r.ReportID)) outIds.Add(r.ReportID);
            foreach (var r in desc.InputReports)
                if (!inIds.Contains(r.ReportID)) inIds.Add(r.ReportID);
        }
        catch
        {
            // 部分虚拟或受限 HID 节点在查询描述符时会抛出异常，安全降级为 Report ID 0
        }

        if (feat.Count == 0) feat.Add(0);
        if (outIds.Count == 0) outIds.Add(0);
        if (inIds.Count == 0) inIds.Add(0);
        return new HidReportIds(feat, outIds, inIds);
    }
}
