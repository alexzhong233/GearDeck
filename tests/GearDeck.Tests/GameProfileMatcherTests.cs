using GearDeck.Core.Models;
using Xunit;

public class GameProfileMatcherTests
{
    [Fact]
    public void NormalizeProcessName_HandlesPathsCasingAndExeSuffix()
    {
        Assert.Equal("cs2", GameProfileRuleMatcher.NormalizeProcessName("CS2.exe"));
        Assert.Equal("cs2", GameProfileRuleMatcher.NormalizeProcessName(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe"));
        Assert.Equal("valorant-win64-shipping", GameProfileRuleMatcher.NormalizeProcessName("VALORANT-Win64-Shipping"));
    }

    [Fact]
    public void Resolve_AssignsDistinctProfilesPerMouseForSingleProcess()
    {
        var rules = new List<GameAutoRule>
        {
            new()
            {
                Id = "r_cs2",
                Name = "CS2 Rule",
                Enabled = true,
                ProcessNames = ["cs2.exe"],
                RequireFullscreen = false,
                PerMouseTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["razer:00C1"] = "preset:fps_800",
                    ["compx:36A7:A856"] = "slot:2",
                    ["logi:C54F:01"] = "slot:0",
                },
            },
        };

        var desktop = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["razer:00C1"] = "preset:desktop_1600",
            ["compx:36A7:A856"] = "slot:0",
        };
        var fsFallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["razer:00C1"] = "preset:fs_generic",
        };

        // 1. Inside cs2.exe -> picks per-mouse distinct targets from r_cs2
        var cs2Res = GameProfileRuleMatcher.Resolve(
            new GameProfileMatchContext(@"D:\Games\cs2.exe", "Counter-Strike 2", true),
            rules, fsFallback, true, desktop, true);
        Assert.Equal(ActiveSwitchReason.ProcessRule, cs2Res.Reason);
        Assert.Equal("rule:r_cs2", cs2Res.RuleKey);
        Assert.Equal("preset:fps_800", cs2Res.PerMouseTargets["razer:00C1"]);
        Assert.Equal("slot:2", cs2Res.PerMouseTargets["compx:36A7:A856"]);
        Assert.Equal("slot:0", cs2Res.PerMouseTargets["logi:C54F:01"]);

        var t1 = GameProfileRuleMatcher.ParseTarget(cs2Res.PerMouseTargets["razer:00C1"]);
        Assert.Null(t1.OnboardSlot);
        Assert.Equal("fps_800", t1.PresetId);

        var t2 = GameProfileRuleMatcher.ParseTarget(cs2Res.PerMouseTargets["compx:36A7:A856"]);
        Assert.Equal(2, t2.OnboardSlot);
        Assert.Null(t2.PresetId);

        // 2. Unmatched exclusive fullscreen app -> hits ExclusiveFullscreen fallback
        var fsRes = GameProfileRuleMatcher.Resolve(
            new GameProfileMatchContext("eldenring.exe", "ELDEN RING", true),
            rules, fsFallback, true, desktop, true);
        Assert.Equal(ActiveSwitchReason.ExclusiveFullscreen, fsRes.Reason);
        Assert.Equal("preset:fs_generic", fsRes.PerMouseTargets["razer:00C1"]);

        // 3. Normal desktop app -> restores DesktopDefault
        var deskRes = GameProfileRuleMatcher.Resolve(
            new GameProfileMatchContext("explorer.exe", "Desktop", false),
            rules, fsFallback, true, desktop, true);
        Assert.Equal(ActiveSwitchReason.DesktopDefault, deskRes.Reason);
        Assert.Equal("preset:desktop_1600", deskRes.PerMouseTargets["razer:00C1"]);
        Assert.Equal("slot:0", deskRes.PerMouseTargets["compx:36A7:A856"]);
    }

    [Fact]
    public void Resolve_RespectsRequireFullscreenGate()
    {
        var rules = new List<GameAutoRule>
        {
            new()
            {
                Id = "r_val",
                Name = "Valorant FS",
                Enabled = true,
                ProcessNames = ["valorant.exe"],
                RequireFullscreen = true,
                PerMouseTargets = new Dictionary<string, string> { ["razer:00C1"] = "preset:val" },
            },
        };
        var desktop = new Dictionary<string, string> { ["razer:00C1"] = "preset:desk" };

        var windowed = GameProfileRuleMatcher.Resolve(
            new GameProfileMatchContext("valorant.exe", "VALORANT", IsExclusiveFullscreen: false),
            rules, new Dictionary<string, string>(), false, desktop, true);
        Assert.Equal(ActiveSwitchReason.DesktopDefault, windowed.Reason);

        var full = GameProfileRuleMatcher.Resolve(
            new GameProfileMatchContext("valorant.exe", "VALORANT", IsExclusiveFullscreen: true),
            rules, new Dictionary<string, string>(), false, desktop, true);
        Assert.Equal(ActiveSwitchReason.ProcessRule, full.Reason);
    }

    [Fact]
    public void GameAutoRule_DefaultsToDisabled_AndOverridesFullscreenOnlyWhenEnabled()
    {
        var defaultRule = new GameAutoRule
        {
            Id = "r_apex",
            Name = "Apex Legends",
            ProcessNames = ["r5apex.exe"],
            PerMouseTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["razer:00C1"] = "preset:apex_1600",
            },
        };

        Assert.False(defaultRule.Enabled);

        var fsFallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["razer:00C1"] = "preset:fs_generic",
        };
        var desktop = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["razer:00C1"] = "preset:desk",
        };

        // 1. Default-disabled per-game rule does NOT override ExclusiveFullscreen
        var fsWins = GameProfileRuleMatcher.Resolve(
            new GameProfileMatchContext("r5apex.exe", "Apex Legends", IsExclusiveFullscreen: true),
            [defaultRule], fsFallback, enableFullscreenFallback: true, desktop, restoreDesktopOnExit: true);
        Assert.Equal(ActiveSwitchReason.ExclusiveFullscreen, fsWins.Reason);
        Assert.Equal("preset:fs_generic", fsWins.PerMouseTargets["razer:00C1"]);

        // 2. Enabling the per-game rule overrides ExclusiveFullscreen in both fullscreen and windowed modes
        defaultRule.Enabled = true;
        var gameWinsFs = GameProfileRuleMatcher.Resolve(
            new GameProfileMatchContext("r5apex.exe", "Apex Legends", IsExclusiveFullscreen: true),
            [defaultRule], fsFallback, enableFullscreenFallback: true, desktop, restoreDesktopOnExit: true);
        Assert.Equal(ActiveSwitchReason.ProcessRule, gameWinsFs.Reason);
        Assert.Equal("preset:apex_1600", gameWinsFs.PerMouseTargets["razer:00C1"]);

        var gameWinsWindowed = GameProfileRuleMatcher.Resolve(
            new GameProfileMatchContext("r5apex.exe", "Apex Legends", IsExclusiveFullscreen: false),
            [defaultRule], fsFallback, enableFullscreenFallback: true, desktop, restoreDesktopOnExit: true);
        Assert.Equal(ActiveSwitchReason.ProcessRule, gameWinsWindowed.Reason);
        Assert.Equal("preset:apex_1600", gameWinsWindowed.PerMouseTargets["razer:00C1"]);

        // 3. Explicit KeepCurrent target in an enabled per-game rule also overrides ExclusiveFullscreen
        defaultRule.PerMouseTargets["razer:00C1"] = GameProfileRuleMatcher.KeepCurrentTargetId;
        var keepWinsFs = GameProfileRuleMatcher.Resolve(
            new GameProfileMatchContext("r5apex.exe", "Apex Legends", IsExclusiveFullscreen: true),
            [defaultRule], fsFallback, enableFullscreenFallback: true, desktop, restoreDesktopOnExit: true);
        Assert.Equal(ActiveSwitchReason.ProcessRule, keepWinsFs.Reason);
        Assert.Equal(GameProfileRuleMatcher.KeepCurrentTargetId, keepWinsFs.PerMouseTargets["razer:00C1"]);
    }
}
