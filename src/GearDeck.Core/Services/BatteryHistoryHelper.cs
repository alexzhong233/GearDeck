using GearDeck.Core.Models;

namespace GearDeck.Core.Services;

public sealed class CachedBatteryRecord
{
    public int? BatteryPercent { get; set; }
    public int? LeftEarPercent { get; set; }
    public int? RightEarPercent { get; set; }
    public int? CaseBatteryPercent { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public readonly record struct ChipTextResult(
    string Text,
    string TextTop,
    string TextBottom,
    int? EffectivePercent,
    bool UsedHistoricalFallback);

/// <summary>
/// 设备历史电量持久化记录与任务栏双耳 / 单电量显示文本构建器。
/// 保证只记录真实观测过的硬件电量，绝不凭空估算未见过电量的设备。
/// </summary>
public static class BatteryHistoryHelper
{
    public const int DefaultLowBatteryThreshold = 20;

    public static bool RecordObservedDevices(
        IDictionary<string, CachedBatteryRecord> cache,
        IEnumerable<PeripheralDevice> devices)
    {
        bool changed = false;
        foreach (var d in devices)
        {
            if (string.IsNullOrWhiteSpace(d.Id)) continue;

            int? bat = d.BatteryPercent is >= 0 and <= 100 ? d.BatteryPercent : null;
            int? left = d.LeftEarPercent is >= 0 and <= 100 ? d.LeftEarPercent : null;
            int? right = d.RightEarPercent is >= 0 and <= 100 ? d.RightEarPercent : null;
            int? box = d.CaseBatteryPercent is >= 0 and <= 100 ? d.CaseBatteryPercent : null;

            if (bat == null && left == null && right == null && box == null)
                continue;

            if (!cache.TryGetValue(d.Id, out var existing))
            {
                cache[d.Id] = new CachedBatteryRecord
                {
                    BatteryPercent = bat,
                    LeftEarPercent = left,
                    RightEarPercent = right,
                    CaseBatteryPercent = box,
                    UpdatedUtc = DateTime.UtcNow,
                };
                changed = true;
                continue;
            }

            int? nextBat = bat ?? existing.BatteryPercent;
            int? nextLeft = left ?? existing.LeftEarPercent;
            int? nextRight = right ?? existing.RightEarPercent;
            int? nextBox = box ?? existing.CaseBatteryPercent;

            if (nextBat != existing.BatteryPercent
                || nextLeft != existing.LeftEarPercent
                || nextRight != existing.RightEarPercent
                || nextBox != existing.CaseBatteryPercent)
            {
                existing.BatteryPercent = nextBat;
                existing.LeftEarPercent = nextLeft;
                existing.RightEarPercent = nextRight;
                existing.CaseBatteryPercent = nextBox;
                existing.UpdatedUtc = DateTime.UtcNow;
                changed = true;
            }
        }
        return changed;
    }

    public static ChipTextResult BuildChipText(
        PeripheralDevice d,
        string label,
        bool showPct,
        CachedBatteryRecord? cached)
    {
        int? liveLeft = d.LeftEarPercent is >= 0 and <= 100 ? d.LeftEarPercent : null;
        int? liveRight = d.RightEarPercent is >= 0 and <= 100 ? d.RightEarPercent : null;

        bool isDualEar = liveLeft.HasValue
                         || liveRight.HasValue
                         || (cached != null && (cached.LeftEarPercent.HasValue || cached.RightEarPercent.HasValue)
                             && (d.IsSleeping || d.BatteryPercent == null));

        if (isDualEar)
        {
            int? effL = liveLeft ?? cached?.LeftEarPercent;
            int? effR = liveRight ?? cached?.RightEarPercent;
            bool usedHist = (liveLeft == null && effL.HasValue) || (liveRight == null && effR.HasValue);
            int? effPct = effL.HasValue && effR.HasValue
                ? Math.Min(effL.Value, effR.Value)
                : (effL ?? effR);

            if (!showPct)
            {
                string lblOnly = string.IsNullOrEmpty(label) ? "L/R" : label;
                return new ChipTextResult(lblOnly, "", "", effPct, usedHist);
            }

            string topCore = $"L {(effL.HasValue ? $"{effL}%" : "--")}{(d.LeftEarCharging ? "⚡" : "")}";
            string botCore = $"R {(effR.HasValue ? $"{effR}%" : "--")}{(d.RightEarCharging ? "⚡" : "")}";
            string textTop = string.IsNullOrEmpty(label) ? topCore : $"{label} {topCore}";
            string textBottom = botCore;
            string flat = $"L{(effL.HasValue ? $"{effL}%" : "--")}/R{(effR.HasValue ? $"{effR}%" : "--")}";
            string text = string.IsNullOrEmpty(label) ? flat : $"{label} {flat}";
            return new ChipTextResult(text, textTop, textBottom, effPct, usedHist);
        }

        int? liveBat = d.BatteryPercent is >= 0 and <= 100 ? d.BatteryPercent : null;
        bool usedSingleHist = false;
        int? singlePct = liveBat;
        if (singlePct == null && cached?.BatteryPercent is >= 0 and <= 100)
        {
            singlePct = cached.BatteryPercent;
            usedSingleHist = true;
        }

        string pctText = singlePct.HasValue
            ? singlePct.Value + (d.State == BatteryState.Charging ? "%⚡" : "%")
            : "--";

        string singleText = !showPct
            ? (!string.IsNullOrEmpty(label) ? label : pctText)
            : (!string.IsNullOrEmpty(label) ? $"{label} {pctText}" : pctText);

        return new ChipTextResult(singleText, "", "", singlePct, usedSingleHist);
    }

    /// <summary>
    /// 从分组在线成员中挑选用于展示的设备：默认设备唤醒时优先展示，否则让唤醒/充电中的设备顶上，
    /// 全员休眠时再按默认设备 -> 有电量 -> 第一个成员的顺序回退，避免贴片/弹窗卡在休眠设备的旧电量。
    /// </summary>
    public static PeripheralDevice? SelectGroupDisplayDevice(
        IReadOnlyList<PeripheralDevice> members,
        string? defaultDeviceId)
    {
        if (members.Count == 0) return null;

        bool HasLiveBattery(PeripheralDevice d) =>
            d.BatteryPercent is >= 0 and <= 100
            || d.LeftEarPercent is >= 0 and <= 100
            || d.RightEarPercent is >= 0 and <= 100;

        bool IsAwakeLive(PeripheralDevice d) => !d.IsSleeping && HasLiveBattery(d);

        PeripheralDevice? defaultMatch = !string.IsNullOrWhiteSpace(defaultDeviceId)
            ? members.FirstOrDefault(m => string.Equals(m.Id, defaultDeviceId, StringComparison.OrdinalIgnoreCase))
            : null;

        // 1. 若用户显式固定/选择了分组内某台设备，始终优先展示该设备（包括其休眠或仅保留历史电量时）
        if (defaultMatch != null)
            return defaultMatch;

        // 2. 自动模式（或固定设备当前未接入）：优先唤醒且有实时电量的成员，充电中的排最前
        var awakeLive = members.Where(IsAwakeLive).ToList();
        if (awakeLive.Count > 0)
            return awakeLive.FirstOrDefault(m => m.State == BatteryState.Charging) ?? awakeLive[0];

        // 3. 全员休眠或均无实时电量：回退到有电量成员 -> 第一个成员
        return members.FirstOrDefault(HasLiveBattery) ?? members[0];
    }
}
