using System.Globalization;

namespace GearDeck.Core.Services;

public enum FovBasis
{
    Horizontal4By3HorPlus,   // CS2 / Apex / Deadlock：滑块代表 4:3 基准 HFOV，宽屏按 Hor+ 扩展
    Horizontal16By9HorPlus,  // COD / OW2 / 三角洲 / PUBG / ABI / 卡拉彼丘 / 漫威争锋 / Aimlabs：滑块代表 16:9 基准 HFOV
    Horizontal16By9Locked,   // VALORANT / 堡垒之夜：锁定 16:9 HFOV (103° / 80°)
    Vertical,                // R6 / 战地 / 塔科夫 / THE FINALS / Hunt：滑块直接代表垂直 VFOV
}

public enum SensitivityScaleKind
{
    Linear,
    PubgExponential,
    Affine,
}

public enum SensitivityConversionMethod
{
    Cm360,               // 360° 物理转身等距（腰射通用默认）
    FocalLengthMdv0,     // FOV 焦距缩放（MDV 0% / tan(VFOV/2) 等比）
    MonitorDistanceH100, // 水平屏幕边缘对齐（MDH 100% / 实际 HFOV 线比）
}

public sealed record GameSensitivityProfile(
    string Id,
    string Name,
    string ShortTag,
    string Category,
    string AccentHex,
    double YawDegreesPerCount,
    SensitivityScaleKind ScaleKind,
    FovBasis FovBasis,
    double DefaultFov,
    double MinFov,
    double MaxFov,
    bool FovLocked,
    int RecommendedDecimals,
    string SettingPath,
    string ConfigTip,
    string AdsAndPitfallTip,
    string? ConfigExportFormat = null,
    double ConfigExportMultiplier = 1.0,
    double YawOffsetDegreesPerCount = 0.0,
    double ConfigExportOffset = 0.0,
    bool PhysicalYawAffectedByFov = false,
    string? FormulaSourceNote = null);

public sealed record ResolutionPreset(
    string Id,
    string Label,
    int Width,
    int Height,
    bool StretchedTo16By9);

public sealed record SensitivityConversionRequest(
    string BaseGameId,
    double BaseSensitivity,
    double BaseFov,
    string TargetGameId,
    double TargetFov,
    double BaseDpi = 800,
    double TargetDpi = 800,
    int BaseWidth = 3840,
    int BaseHeight = 2160,
    bool BaseStretchedTo16By9 = false,
    int TargetWidth = 3840,
    int TargetHeight = 2160,
    bool TargetStretchedTo16By9 = false,
    SensitivityConversionMethod Method = SensitivityConversionMethod.Cm360);

public sealed record SensitivityConversionResult(
    GameSensitivityProfile BaseGame,
    GameSensitivityProfile TargetGame,
    double TargetSensitivityCm360,
    double TargetSensitivityFovScaled,
    double TargetSensitivityMdh100,
    double TargetSensitivitySelected,
    string FormattedSelected,
    string FormattedCm360,
    string FormattedFovScaled,
    string FormattedMdh100,
    double BaseCm360,
    double TargetCm360,
    double BaseIn360,
    double TargetIn360,
    double BaseEdpi,
    double TargetEdpi,
    double BaseActualHFov,
    double BaseActualVFov,
    double TargetActualHFov,
    double TargetActualVFov,
    double FovScaleRatio,
    double Mdh100ScaleRatio,
    double BaseHorizontalStretchFactor,
    double TargetHorizontalStretchFactor,
    double TargetDegreesPerCount,
    double PixelsPerCountAtTargetRes,
    double MinSpeedCmPerSecFor2000Hz,
    string? ConfigSnippet,
    IReadOnlyList<string> DynamicWarnings);

public static class SensitivityConverter
{
    public static readonly IReadOnlyList<GameSensitivityProfile> AllGames =
    [
        new(
            "cs2",
            "Counter-Strike 2 (CS2)",
            "Source 2",
            "战术射击",
            "#E09F3E",
            0.022,
            SensitivityScaleKind.Linear,
            FovBasis.Horizontal4By3HorPlus,
            90, 60, 120,
            false, 3,
            "设置 → 键盘/鼠标 → 鼠标灵敏度",
            "控制台输入 sensitivity <值>（确认 m_yaw 为默认 0.022，原始输入已默认开启）",
            "狙击镜倍率 zoom_sensitivity_ratio 为相对系数，换 DPI 或腰射后无需修改；4:3 拉伸到 16:9 时物理 cm/360° 不变，但视觉横向会拉宽 1.33 倍。",
            "sensitivity {0}", 1.0),

        new(
            "valorant",
            "无畏契约 (VALORANT)",
            "Unreal · 锁FOV",
            "战术射击",
            "#FF4655",
            0.07,
            SensitivityScaleKind.Linear,
            FovBasis.Horizontal16By9Locked,
            103, 103, 103,
            true, 3,
            "设置 → 常规 → 鼠标 → 灵敏度：瞄准",
            "游戏内直接输入小数（支持 3 位小数），建议开启「原始输入缓冲」",
            "无畏契约横向 FOV 固定锁定 103°（超宽屏自动黑边），开镜/狙击倍率为相对系数无需重调。"),

        new(
            "delta_force",
            "三角洲行动 (Delta Force)",
            "线性 · 搜打撤",
            "大逃杀 / 搜打撤",
            "#00C49A",
            0.03,
            SensitivityScaleKind.Linear,
            FovBasis.Horizontal16By9HorPlus,
            100, 90, 120,
            false, 2,
            "设置 → 鼠标与键盘 → 鼠标灵敏度",
            "PC 端线性换算，建议关闭鼠标加速并保持线性转向模式",
            "若开启了「按倍率同步开镜灵敏度」，改完基础灵敏度即可；若使用各倍镜独立数值，换 DPI 时高倍镜需同步等比调整。",
            FormulaSourceNote: "Yaw 取值来源分歧：KovaaK 为 0.03、Aiming.Pro 为 0.01；建议实测 360° 转向距离确认。"),

        new(
            "apex",
            "Apex Legends",
            "Source",
            "大逃杀 / 搜打撤",
            "#DA292A",
            0.022,
            SensitivityScaleKind.Linear,
            FovBasis.Horizontal4By3HorPlus,
            110, 70, 110,
            false, 2,
            "设置 → 鼠标/键盘 → 鼠标灵敏度",
            "%USERPROFILE%\\Saved Games\\Respawn\\Apex\\local\\settings.cfg",
            "Apex 的 FOV 滑块基于 4:3 基准（设为 110 时 16:9 实际横向 FOV 约 123.3°）；开镜倍率为相对系数，按需微调即可。",
            "mouse_sensitivity \"{0}\"", 1.0),

        new(
            "cod",
            "使命召唤 (MW3 / BO6 / 战区)",
            "IW 引擎",
            "英雄射击",
            "#6CC24A",
            0.0066,
            SensitivityScaleKind.Linear,
            FovBasis.Horizontal16By9HorPlus,
            120, 60, 120,
            false, 2,
            "设置 → 键盘与鼠标 → 瞄准 → 鼠标灵敏度",
            "开镜灵敏度过渡建议选「即时」，瞄准模式选「相对 (Relative)」",
            "COD 相对模式默认显示器距离系数 1.33（追求跟枪一致可改 0.00 即 MDV 0%），开镜 FOV 建议选「受影响 (Affected)」。"),

        new(
            "pubg",
            "绝地求生 (PUBG)",
            "非线性指数",
            "大逃杀 / 搜打撤",
            "#F2A900",
            0.0555556,
            SensitivityScaleKind.PubgExponential,
            FovBasis.Horizontal16By9HorPlus,
            90, 80, 103,
            false, 0,
            "设置 → 控制 → 常规灵敏度",
            "%LOCALAPPDATA%\\TslGame\\Saved\\Config\\WindowsNoEditor\\GameUserSettings.ini",
            "PUBG 是非线性指数曲线（每 ±15 档速度翻倍/减半）：DPI 翻倍（800→1600）务必把常规及所有独立倍镜灵敏度统一减 15，切勿直接除以 2！",
            "MouseSensitivity={0}", 1.0,
            PhysicalYawAffectedByFov: true),

        new(
            "ow2",
            "守望先锋 2 (Overwatch 2)",
            "线性",
            "英雄射击",
            "#FA9C1E",
            0.0066,
            SensitivityScaleKind.Linear,
            FovBasis.Horizontal16By9HorPlus,
            103, 80, 103,
            false, 2,
            "选项设置 → 控制 → 鼠标 → 灵敏度",
            "黑百合/安娜/艾什开镜相对灵敏度推荐约 37.89%（MDV 0% 焦距一致）",
            "OW2 与 COD 共用 0.0066 Yaw；可在控制里开启「高精度鼠标输入」降低点击延迟。"),

        new(
            "r6",
            "彩虹六号：围攻 (R6 Siege)",
            "垂直 FOV",
            "战术射击",
            "#4A90E2",
            0.018 / Math.PI,
            SensitivityScaleKind.Linear,
            FovBasis.Vertical,
            84, 60, 90,
            false, 0,
            "选项 → 控制 → 水平 / 垂直灵敏度",
            "Documents\\My Games\\Rainbow Six - Siege\\<UID>\\GameSettings.ini",
            "彩六使用垂直 VFOV（默认 MultiplierUnit=0.02 时滑块仅整数）；高 DPI 下若整数格太粗，可把 ini 里的 MouseSensitivityMultiplierUnit 从 0.02 改成 0.002，并将换算结果 ×10 填入以获得 10 倍精细度。",
            "MouseYawSensitivity={0}  (MultiplierUnit=0.02)", 1.0),

        new(
            "battlefield_v",
            "战地系列 (BF1 / BFV / 2042)",
            "Frostbite · 垂直FOV · 仿射",
            "大逃杀 / 搜打撤",
            "#2B7FFF",
            0.0034377486,
            SensitivityScaleKind.Affine,
            FovBasis.Vertical,
            55, 55, 105,
            false, 1,
            "选项 → 控制 → 士兵鼠标灵敏度（百分比滑块）",
            "Documents\\Battlefield <版本>\\settings\\PROFSAVE_profile_synced → GstInput.MouseSensitivity",
            "强烈建议开启「统一士兵瞄准 (USA)」；若游戏内整数滑块精度不够，直接复制上方 GstInput.MouseSensitivity 浮点值写入配置文件最准。",
            "GstInput.MouseSensitivity {0}", 0.0015,
            YawOffsetDegreesPerCount: 0.011459162,
            ConfigExportOffset: 0.005),

        new(
            "battlefield_6",
            "战地风云 6 (Battlefield 6)",
            "Frostbite · 水平FOV · 仿射",
            "大逃杀 / 搜打撤",
            "#2B7FFF",
            0.0017362342283,
            SensitivityScaleKind.Affine,
            FovBasis.Horizontal16By9HorPlus,
            90, 60, 120,
            false, 3,
            "选项 → 控制 → 士兵鼠标灵敏度（百分比滑块）",
            "Documents\\Battlefield 6\\settings\\PROFSAVE_profile_synced → GstInput.MouseSensitivity",
            "BF 6 使用 16:9 基准 HFOV；强烈建议开启「统一士兵瞄准 (USA)」，配置浮点值比游戏内滑块更精确。",
            "GstInput.MouseSensitivity {0}", 0.00075,
            YawOffsetDegreesPerCount: 0.002314981532,
            ConfigExportOffset: 0.001),

        new(
            "tarkov",
            "逃离塔科夫 (Tarkov)",
            "垂直 FOV · 护甲惩罚",
            "大逃杀 / 搜打撤",
            "#A68B5B",
            0.125,
            SensitivityScaleKind.Linear,
            FovBasis.Vertical,
            63, 50, 75,
            false, 2,
            "设置 → 控制 → 鼠标灵敏度 / 瞄准灵敏度",
            "腰射与瞄准 (Aiming) 为独立数值，换 DPI 时两项需同步调整",
            "塔科夫头盔与重甲自带「转向速度惩罚」，会实时乘在真实灵敏度上；换算值为无惩罚裸装基准，建议按常用配装微调 +5%~10%。"),

        new(
            "abi",
            "暗区突围：无限 (ABI)",
            "线性 · 搜打撤",
            "大逃杀 / 搜打撤",
            "#D97706",
            0.1024,
            SensitivityScaleKind.Linear,
            FovBasis.Horizontal16By9HorPlus,
            90, 75, 110,
            false, 2,
            "设置 → 控制 → 视角灵敏度",
            "游戏内 UI 保留 2 位小数，高 DPI 下建议优先以 2 位小数对齐",
            "若关闭了倍镜同步、使用独立开镜灵敏度，更换 DPI 时各倍镜需按同一比例同步换算。"),

        new(
            "strinova",
            "卡拉彼丘 (Strinova)",
            "TPS / 弦化",
            "英雄射击",
            "#EC4899",
            0.01388194363,
            SensitivityScaleKind.Linear,
            FovBasis.Horizontal16By9HorPlus,
            90, 80, 105,
            false, 2,
            "设置 → 操作 → 鼠标灵敏度",
            "弦化/肩射与开镜可单独微调，基础数值遵循线性比例",
            "第三人称越肩视角与第一人称视觉角速度略有差异，腰射用 360° 等距后，开镜建议按体感微调。"),

        new(
            "deadlock",
            "Deadlock",
            "Source 2",
            "英雄射击",
            "#8B5CF6",
            0.044,
            SensitivityScaleKind.Linear,
            FovBasis.Horizontal4By3HorPlus,
            90, 70, 110,
            false, 2,
            "设置 → 选项 → 鼠标灵敏度",
            "Source 2 系但 Yaw 为 0.044（CS2 / Apex 的 2 倍），同 DPI 下数值约为 CS2 / Apex 的一半",
            "Deadlock 为第三人称越肩射击，若觉得近身拉枪偏快，可对比使用「FOV 焦距缩放」参考值。",
            "sensitivity {0}", 1.0),

        new(
            "the_finals",
            "THE FINALS",
            "Unreal · 垂直FOV",
            "英雄射击",
            "#E11D48",
            0.001,
            SensitivityScaleKind.Linear,
            FovBasis.Vertical,
            90, 71, 100,
            false, 0,
            "设置 → 鼠标与键盘 → 鼠标视角灵敏度",
            "支持开启「焦距缩放灵敏度 (Focal Length Sensitivity Scaling)」以自动对齐开镜",
            "THE FINALS 采用垂直 VFOV（设为 90° 时 16:9 实际横向 FOV 约 121.3°），与 CS2/瓦 FOV 差距较大，强烈建议同时参考「FOV 焦距缩放」结果。"),

        new(
            "marvel_rivals",
            "漫威争锋 (Marvel Rivals)",
            "Unreal · TPS",
            "英雄射击",
            "#3B82F6",
            0.0175,
            SensitivityScaleKind.Linear,
            FovBasis.Horizontal16By9HorPlus,
            103, 80, 110,
            false, 2,
            "设置 → 键鼠 → 战斗 → 水平 / 垂直灵敏度",
            "Yaw 恰好为无畏契约的 1/4（同 DPI 下数值 = 瓦灵敏度 × 4）",
            "第三人称多英雄机制差异较大，近战英雄可适当在换算值基础上略微提高，狙击/射手英雄直接用换算值即可。"),

        new(
            "aimlabs",
            "Aimlabs (CS2 基准)",
            "练枪工具",
            "练枪 / 拟真",
            "#06B6D4",
            0.022,
            SensitivityScaleKind.Linear,
            FovBasis.Horizontal16By9HorPlus,
            103, 60, 130,
            false, 3,
            "Settings → Controls → Game Profile 选择目标游戏或 CS2",
            "在 Aimlabs 中务必把 FOV 与目标主力游戏设为完全一致再练枪",
            "练枪软件的核心是「FOV + cm/360°」双一致；也可以直接在 Aimlabs 的 Game Profile 里选你正在玩的游戏并填相同灵敏度。"),

        new(
            "hunt",
            "猎杀：对决 1896 (Hunt: Showdown 1896)",
            "垂直 FOV",
            "大逃杀 / 搜打撤",
            "#B7410E",
            0.0429718162181364,
            SensitivityScaleKind.Linear,
            FovBasis.Vertical,
            55, 55, 90,
            false, 2,
            "设置 → 控制 → 鼠标灵敏度",
            "游戏内以百分比滑块呈现，建议按 2 位小数对齐",
            "Hunt 使用垂直 VFOV 且滑块下限为 55°；开镜倍率为相对系数，换 DPI 后无需重调。"),

        new(
            "fortnite",
            "堡垒之夜 (Fortnite)",
            "Unreal · 锁FOV",
            "大逃杀 / 搜打撤",
            "#9B59B6",
            0.005555,
            SensitivityScaleKind.Linear,
            FovBasis.Horizontal16By9Locked,
            80, 80, 80,
            true, 1,
            "设置 → 输入 → 鼠标灵敏度",
            "游戏内以 1 位小数呈现，FOV 固定锁定 80°（超宽屏自动黑边）",
            "堡垒之夜横向 FOV 锁定 80°，改动显示器比例不影响实际 HFOV；开镜倍率为相对系数。"),
    ];

    public static readonly IReadOnlyList<ResolutionPreset> ResolutionPresets =
    [
        new("3840x2160", "4K · 3840×2160 (16:9)", 3840, 2160, false),
        new("2560x1440", "2K · 2560×1440 (16:9)", 2560, 1440, false),
        new("1920x1080", "1080p · 1920×1080 (16:9)", 1920, 1080, false),
        new("1280x960_stretched", "1280×960 (4:3 拉伸到 16:9)", 1280, 960, true),
        new("1440x1080_stretched", "1440×1080 (4:3 拉伸到 16:9)", 1440, 1080, true),
        new("3440x1440", "3440×1440 (21:9 带鱼屏)", 3440, 1440, false),
        new("2560x1600", "2560×1600 (16:10)", 2560, 1600, false),
    ];

    private const double RadPerDegree = Math.PI / 180.0;
    private const double DegreesPerRadian = 180.0 / Math.PI;

    private static double DegToRad(double degrees) => degrees * RadPerDegree;

    private static double RadToDeg(double radians) => radians * DegreesPerRadian;

    private static double Clamp(double value, double min, double max) =>
        Math.Min(max, Math.Max(min, value));

    public static GameSensitivityProfile FindById(string? id)
    {
        if (id is null)
        {
            return AllGames[0];
        }

        if (string.Equals(id, "battlefield", StringComparison.OrdinalIgnoreCase))
        {
            id = "battlefield_v";
        }

        foreach (var game in AllGames)
        {
            if (string.Equals(game.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return game;
            }
        }

        return AllGames[0];
    }

    public static (double ActualHFov, double ActualVFov, double StretchFactor) ComputeActualFov(
        FovBasis basis,
        double sliderFov,
        int width,
        int height,
        bool stretchedTo16By9)
    {
        var fov = Clamp(sliderFov, 10.0, 175.0);
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var aspect = (double)width / height;

        double vf;
        double hf;

        switch (basis)
        {
            case FovBasis.Vertical:
                vf = fov;
                hf = RadToDeg(2.0 * Math.Atan(Math.Tan(DegToRad(vf / 2.0)) * aspect));
                break;

            case FovBasis.Horizontal4By3HorPlus:
                vf = RadToDeg(2.0 * Math.Atan(Math.Tan(DegToRad(fov / 2.0)) * 3.0 / 4.0));
                hf = RadToDeg(2.0 * Math.Atan(Math.Tan(DegToRad(vf / 2.0)) * aspect));
                break;

            case FovBasis.Horizontal16By9Locked:
                vf = RadToDeg(2.0 * Math.Atan(Math.Tan(DegToRad(fov / 2.0)) * 9.0 / 16.0));
                hf = fov;
                break;

            case FovBasis.Horizontal16By9HorPlus:
            default:
                vf = RadToDeg(2.0 * Math.Atan(Math.Tan(DegToRad(fov / 2.0)) * 9.0 / 16.0));
                hf = RadToDeg(2.0 * Math.Atan(Math.Tan(DegToRad(vf / 2.0)) * aspect));
                break;
        }

        var stretchFactor = stretchedTo16By9 && aspect < (16.0 / 9.0) - 0.01
            ? (16.0 / 9.0) / aspect
            : 1.0;

        return (hf, vf, stretchFactor);
    }

    public static double DegreesPerCountFromSens(
        GameSensitivityProfile game, double sens, double actualHFov = 90.0)
    {
        sens = game.ScaleKind == SensitivityScaleKind.Affine ? Math.Max(0.0, sens) : Math.Max(1e-6, sens);
        return game.ScaleKind switch
        {
            SensitivityScaleKind.PubgExponential =>
                0.00005555 * Math.Pow(10.0, sens / 50.0) * actualHFov,
            SensitivityScaleKind.Affine =>
                sens * game.YawDegreesPerCount + game.YawOffsetDegreesPerCount,
            _ => sens * game.YawDegreesPerCount,
        };
    }

    public static double SensFromDegreesPerCount(
        GameSensitivityProfile game, double degPerCount, double actualHFov = 90.0)
    {
        degPerCount = Math.Max(1e-9, degPerCount);
        return game.ScaleKind switch
        {
            SensitivityScaleKind.PubgExponential =>
                50.0 * Math.Log10(degPerCount / (0.00005555 * actualHFov)),
            SensitivityScaleKind.Affine =>
                Math.Max(0.0, (degPerCount - game.YawOffsetDegreesPerCount) / game.YawDegreesPerCount),
            _ => degPerCount / game.YawDegreesPerCount,
        };
    }

    public static SensitivityConversionResult Convert(SensitivityConversionRequest request)
    {
        var baseGame = FindById(request.BaseGameId);
        var targetGame = FindById(request.TargetGameId);

        var baseDpi = Math.Max(50.0, request.BaseDpi);
        var targetDpi = Math.Max(50.0, request.TargetDpi);
        var baseSens = baseGame.ScaleKind == SensitivityScaleKind.Affine
            ? Math.Max(0.0, request.BaseSensitivity)
            : Math.Max(1e-6, request.BaseSensitivity);
        var baseFov = baseGame.FovLocked ? baseGame.DefaultFov : Clamp(request.BaseFov, 10.0, 175.0);
        var targetFov = targetGame.FovLocked ? targetGame.DefaultFov : Clamp(request.TargetFov, 10.0, 175.0);

        var (baseHFov, baseVFov, baseStretch) = ComputeActualFov(
            baseGame.FovBasis, baseFov, request.BaseWidth, request.BaseHeight, request.BaseStretchedTo16By9);
        var (targetHFov, targetVFov, targetStretch) = ComputeActualFov(
            targetGame.FovBasis, targetFov, request.TargetWidth, request.TargetHeight, request.TargetStretchedTo16By9);

        var baseDegPerCount = DegreesPerCountFromSens(baseGame, baseSens, baseHFov);
        var baseCm360 = 914.4 / (baseDpi * baseDegPerCount);
        var baseIn360 = 360.0 / (baseDpi * baseDegPerCount);

        var targetDegPerCountCm360 = baseDegPerCount * (baseDpi / targetDpi);
        var fovScaleRatio = Math.Tan(DegToRad(targetVFov / 2.0)) / Math.Tan(DegToRad(baseVFov / 2.0));
        var mdh100ScaleRatio = targetHFov / baseHFov;

        var targetSensCm360 = SensFromDegreesPerCount(targetGame, targetDegPerCountCm360, targetHFov);
        var targetSensFovScaled = SensFromDegreesPerCount(targetGame, targetDegPerCountCm360 * fovScaleRatio, targetHFov);
        var targetSensMdh100 = SensFromDegreesPerCount(targetGame, targetDegPerCountCm360 * mdh100ScaleRatio, targetHFov);
        var targetSensSelected = request.Method switch
        {
            SensitivityConversionMethod.FocalLengthMdv0 => targetSensFovScaled,
            SensitivityConversionMethod.MonitorDistanceH100 => targetSensMdh100,
            _ => targetSensCm360,
        };

        var selectedDegPerCount = DegreesPerCountFromSens(targetGame, targetSensSelected, targetHFov);
        var targetCm360 = 914.4 / (targetDpi * selectedDegPerCount);
        var targetIn360 = 360.0 / (targetDpi * selectedDegPerCount);

        var pixelsPerCount = (Math.Max(1, request.TargetWidth) / 2.0)
            * Math.Tan(DegToRad(selectedDegPerCount))
            / Math.Tan(DegToRad(targetHFov / 2.0));
        var minSpeed2k = (2000.0 / targetDpi) * 2.54;

        var numberFormat = $"F{targetGame.RecommendedDecimals}";
        var formattedSelected = targetSensSelected.ToString(numberFormat, CultureInfo.InvariantCulture);
        var formattedCm360 = targetSensCm360.ToString(numberFormat, CultureInfo.InvariantCulture);
        var formattedFovScaled = targetSensFovScaled.ToString(numberFormat, CultureInfo.InvariantCulture);
        var formattedMdh100 = targetSensMdh100.ToString(numberFormat, CultureInfo.InvariantCulture);

        string? configSnippet = null;
        if (!string.IsNullOrEmpty(targetGame.ConfigExportFormat))
        {
            var hasConfigTransform = targetGame.ConfigExportMultiplier != 1.0 || targetGame.ConfigExportOffset != 0.0;
            var exportValue = hasConfigTransform
                ? (targetGame.ConfigExportOffset + targetSensSelected * targetGame.ConfigExportMultiplier)
                    .ToString("F6", CultureInfo.InvariantCulture)
                : formattedSelected;
            configSnippet = string.Format(CultureInfo.InvariantCulture, targetGame.ConfigExportFormat!, exportValue);
        }

        var warnings = new List<string>();
        if (baseGame.Id == "pubg" || targetGame.Id == "pubg")
        {
            warnings.Add("PUBG 为非线性指数曲线（每 ±15 档速度翻倍/减半）：DPI 翻倍时请固定减 15 档，而不是除以 2。");
        }

        if (targetGame.PhysicalYawAffectedByFov)
        {
            warnings.Add($"{targetGame.Name} 的物理转向速度随实际横向 FOV 变化，换算已按 {targetHFov:F1}° 实际 HFOV 计入；改动 FOV 后需重新换算。");
        }

        if (targetGame.Id == "r6" && targetDpi >= 1600)
        {
            warnings.Add("彩虹六号：可在 GameSettings.ini 中将 MouseSensitivityMultiplierUnit 设为 0.002，并把换算结果 ×10 填入，获得 10 倍精细度。");
        }

        if (targetGame.Id is "battlefield_v" or "battlefield_6")
        {
            warnings.Add("战地系列：可直接复制上方 GstInput.MouseSensitivity 浮点值写入 PROFSAVE_profile_synced，并开启「统一士兵瞄准 (USA)」。");
        }

        if (targetGame.Id == "delta_force")
        {
            warnings.Add("三角洲行动 Yaw 取值来源分歧：KovaaK 为 0.03、Aiming.Pro 为 0.01，建议实测 360° 转向距离确认。");
        }
        else if (!string.IsNullOrEmpty(targetGame.FormulaSourceNote))
        {
            warnings.Add(targetGame.FormulaSourceNote!);
        }

        if (targetGame.ScaleKind == SensitivityScaleKind.Affine &&
            (targetDegPerCountCm360 < targetGame.YawOffsetDegreesPerCount ||
             targetDegPerCountCm360 * fovScaleRatio < targetGame.YawOffsetDegreesPerCount ||
             targetDegPerCountCm360 * mdh100ScaleRatio < targetGame.YawOffsetDegreesPerCount))
        {
            warnings.Add("目标灵敏度低于引擎最小灵敏度下限，已钳位到 0；建议降低鼠标 DPI 或改用其他换算方法。");
        }

        if (targetGame.Id == "tarkov")
        {
            warnings.Add("塔科夫存在头盔/护甲转向速度惩罚，换算结果为无惩罚裸装基准，建议按常用配装实战微调 +5%~10%。");
        }

        var hfovDiff = Math.Abs(baseHFov - targetHFov);
        if (hfovDiff >= 8.0)
        {
            warnings.Add($"两边实际横向 FOV 相差 {hfovDiff:F1}°：「360° 等距」保持物理手感一致，「FOV 焦距缩放」保持屏幕视觉跟枪一致，请按用途取舍。");
        }

        if (pixelsPerCount >= 2.5)
        {
            warnings.Add($"当前目标分辨率 + DPI 下单步跳跃约 {pixelsPerCount:F2} px/count，4K 屏建议提高鼠标 DPI（如 1600）并降低游戏内灵敏度以获得更细腻的微调。");
        }

        if (request.BaseStretchedTo16By9 || request.TargetStretchedTo16By9)
        {
            warnings.Add("4:3 拉伸到 16:9 不改变物理 cm/360°，但视觉横向角速度会被放大约 1.33 倍。");
        }

        if (Math.Abs(baseDpi - targetDpi) >= 1.0)
        {
            warnings.Add($"已自动计入 DPI 变化（{baseDpi:F0} → {targetDpi:F0}）。");
        }

        return new SensitivityConversionResult(
            BaseGame: baseGame,
            TargetGame: targetGame,
            TargetSensitivityCm360: targetSensCm360,
            TargetSensitivityFovScaled: targetSensFovScaled,
            TargetSensitivityMdh100: targetSensMdh100,
            TargetSensitivitySelected: targetSensSelected,
            FormattedSelected: formattedSelected,
            FormattedCm360: formattedCm360,
            FormattedFovScaled: formattedFovScaled,
            FormattedMdh100: formattedMdh100,
            BaseCm360: baseCm360,
            TargetCm360: targetCm360,
            BaseIn360: baseIn360,
            TargetIn360: targetIn360,
            BaseEdpi: baseDpi * baseSens,
            TargetEdpi: targetDpi * targetSensSelected,
            BaseActualHFov: baseHFov,
            BaseActualVFov: baseVFov,
            TargetActualHFov: targetHFov,
            TargetActualVFov: targetVFov,
            FovScaleRatio: fovScaleRatio,
            Mdh100ScaleRatio: mdh100ScaleRatio,
            BaseHorizontalStretchFactor: baseStretch,
            TargetHorizontalStretchFactor: targetStretch,
            TargetDegreesPerCount: selectedDegPerCount,
            PixelsPerCountAtTargetRes: pixelsPerCount,
            MinSpeedCmPerSecFor2000Hz: minSpeed2k,
            ConfigSnippet: configSnippet,
            DynamicWarnings: warnings);
    }
}
