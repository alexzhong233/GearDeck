namespace GearDeck.Core.Models;

public enum ExitRestoreMode
{
    WhenWindowExits = 0,
    WhenFocusLeaves = 1,
    NeverRestore = 2,
}

public enum ActiveSwitchReason
{
    None = 0,
    ProcessRule = 1,
    ExclusiveFullscreen = 2,
    DesktopDefault = 3,
}

public sealed record GameProfileMatchContext(
    string ProcessName,
    string WindowTitle,
    bool IsExclusiveFullscreen);

public sealed record GameProfileResolution(
    ActiveSwitchReason Reason,
    string RuleKey,
    string RuleLabel,
    IReadOnlyDictionary<string, string> PerMouseTargets);

public sealed record ParsedMouseTarget(
    int? OnboardSlot,
    string? PresetId,
    bool RestorePrevious = false,
    bool KeepCurrent = false)
{
    public bool IsEmpty => OnboardSlot == null && string.IsNullOrWhiteSpace(PresetId) && !RestorePrevious && !KeepCurrent;
}

public static class GameProfileRuleMatcher
{
    public const string RestorePreviousTargetId = "__restore_prev__";
    public const string KeepCurrentTargetId = "__keep__";
    public static string NormalizeProcessName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string file = Path.GetFileName(raw.Trim().Trim('"'));
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            file = file[..^4];
        return file.Trim().ToLowerInvariant();
    }

    public static bool ProcessMatches(GameAutoRule rule, string foregroundProcess)
    {
        string fg = NormalizeProcessName(foregroundProcess);
        if (string.IsNullOrEmpty(fg)) return false;

        foreach (string candidate in rule.ProcessNames)
        {
            string norm = NormalizeProcessName(candidate);
            if (!string.IsNullOrEmpty(norm) && string.Equals(norm, fg, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static GameProfileResolution Resolve(
        GameProfileMatchContext ctx,
        IReadOnlyList<GameAutoRule> rules,
        IReadOnlyDictionary<string, string> fullscreenFallbackTargets,
        bool enableFullscreenFallback,
        IReadOnlyDictionary<string, string> desktopDefaultTargets,
        bool restoreDesktopOnExit)
    {
        foreach (var rule in rules)
        {
            if (!rule.Enabled || rule.PerMouseTargets.Count == 0) continue;
            if (!ProcessMatches(rule, ctx.ProcessName)) continue;
            if (rule.RequireFullscreen && !ctx.IsExclusiveFullscreen) continue;

            return new GameProfileResolution(
                ActiveSwitchReason.ProcessRule,
                $"rule:{rule.Id}",
                rule.Name,
                rule.PerMouseTargets);
        }

        if (enableFullscreenFallback && ctx.IsExclusiveFullscreen && HasAnyTarget(fullscreenFallbackTargets))
        {
            return new GameProfileResolution(
                ActiveSwitchReason.ExclusiveFullscreen,
                "__fullscreen__",
                "独占全屏通用配置",
                fullscreenFallbackTargets);
        }

        if (restoreDesktopOnExit)
        {
            return new GameProfileResolution(
                ActiveSwitchReason.DesktopDefault,
                "__desktop__",
                HasAnyTarget(desktopDefaultTargets) ? "桌面默认配置" : "回退开启前的模式",
                desktopDefaultTargets);
        }

        return new GameProfileResolution(
            ActiveSwitchReason.None,
            "",
            "",
            new Dictionary<string, string>());
    }

    /// <summary>
    /// 退出恢复保持策略：当启用“窗口/游戏未退出时保持当前配置”且之前激活的全屏窗口或游戏进程仍在运行中时，
    /// 即便 Alt+Tab 切出到桌面或其他窗口，也不会切回桌面默认配置。
    /// </summary>
    public static GameProfileResolution ApplyExitRetention(
        GameProfileResolution candidate,
        GameProfileResolution? latchedActive,
        bool keepUntilExit,
        bool isLatchedAlive)
    {
        if (candidate.Reason is ActiveSwitchReason.ProcessRule or ActiveSwitchReason.ExclusiveFullscreen)
            return candidate;

        if (keepUntilExit &&
            isLatchedAlive &&
            latchedActive is { Reason: ActiveSwitchReason.ProcessRule or ActiveSwitchReason.ExclusiveFullscreen })
        {
            return latchedActive;
        }

        return candidate;
    }

    public static ParsedMouseTarget ParseTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return new ParsedMouseTarget(null, null);

        string trimmed = target.Trim();
        if (string.Equals(trimmed, RestorePreviousTargetId, StringComparison.OrdinalIgnoreCase))
            return new ParsedMouseTarget(null, null, RestorePrevious: true);

        if (string.Equals(trimmed, KeepCurrentTargetId, StringComparison.OrdinalIgnoreCase))
            return new ParsedMouseTarget(null, null, KeepCurrent: true);

        if (trimmed.StartsWith("slot:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(trimmed.AsSpan(5), out int slot) && slot >= 0)
            return new ParsedMouseTarget(slot, null);

        if (trimmed.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
        {
            string id = trimmed[7..].Trim();
            if (!string.IsNullOrEmpty(id))
                return new ParsedMouseTarget(null, id);
        }

        return new ParsedMouseTarget(null, null);
    }

    private static bool HasAnyTarget(IReadOnlyDictionary<string, string> targets)
    {
        foreach (var kv in targets)
        {
            if (!ParseTarget(kv.Value).IsEmpty)
                return true;
        }
        return false;
    }
}
