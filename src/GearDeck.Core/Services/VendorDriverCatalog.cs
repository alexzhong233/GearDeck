using GearDeck.Core.Models;

namespace GearDeck.Core.Services;

public sealed record LocalDriverInfo(
    string BrandKey,
    string DisplayName,
    string[] ProcessNames,
    string[] ExeCandidatePaths,
    string[] UninstallNameHints,
    string? ShellUri,
    string DownloadUrl);

public sealed record WebDriverInfo(
    string BrandKey,
    string DisplayName,
    string Url);

public sealed record VendorDriverLinks(
    LocalDriverInfo? Local,
    WebDriverInfo? Web);

/// <summary>各品牌官方本地设置软件与官方网页驱动（WebHID）注册目录。</summary>
public static class VendorDriverCatalog
{
    private static string[] ExpandProgramFiles(bool includeAllFixedDrives, params string[] relativePaths)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var roots = new List<string>();
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (!string.IsNullOrWhiteSpace(root))
                roots.Add(root);
        }

        if (includeAllFixedDrives)
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    string pf = Path.Combine(drive.RootDirectory.FullName, "Program Files");
                    string pf86 = Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)");
                    roots.Add(pf);
                    roots.Add(pf86);
                }
            }
            catch { }
        }

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            foreach (string rel in relativePaths)
            {
                string full = Path.Combine(root, rel);
                if (seen.Add(full))
                    candidates.Add(full);
            }
        }
        return [.. candidates];
    }

    private static readonly LocalDriverInfo LogitechLocal = new(
        "logitech",
        "打开 Logitech G HUB",
        ["lghub", "lghub_ui", "lghub_agent"],
        ExpandProgramFiles(false,
            @"LGHUB\lghub.exe",
            @"Logitech Gaming Software\LCore.exe"),
        ["Logitech G HUB"],
        null,
        "https://www.logitechg.com/innovation/g-hub.html");

    private static readonly LocalDriverInfo RogLocal = new(
        "rog",
        "打开 ROG 奥创中心",
        [],
        [],
        [],
        "shell:AppsFolder\\B9ECED6F.ArmouryCrate_qmba6cd70vzyy!App",
        "https://rog.asus.com/armoury-crate/");

    private static readonly LocalDriverInfo RazerLocal = new(
        "razer",
        "打开 Razer 雷云 Synapse",
        ["RazerAppEngine", "Razer Synapse 3", "Razer Central"],
        ExpandProgramFiles(false,
            @"Razer\RazerAppEngine\RazerAppEngine.exe",
            @"Razer\Razer Services\Razer Central\Razer Central.exe",
            @"Razer\Synapse3\WPFUI\Framework\Razer Synapse 3 Host\Razer Synapse 3.exe"),
        ["Razer Synapse", "Razer Central"],
        null,
        "https://www.razer.com/synapse-4");

    private static readonly LocalDriverInfo SonyLocal = new(
        "sony",
        "打开 Sony INZONE Hub",
        ["INZONEHub", "INZONE Hub"],
        ExpandProgramFiles(false, @"Sony\INZONE Hub\INZONEHub.exe"),
        ["INZONE Hub"],
        null,
        "https://www.sony.com/electronics/support/articles/00279263");

    private static readonly LocalDriverInfo FlydigiLocal = new(
        "flydigi",
        "打开 飞智空间站",
        ["FlydigiSpaceStation", "SpaceStation", "飞智空间站"],
        ExpandProgramFiles(true,
            @"FlydigiSpaceStation\FlydigiSpaceStation.exe",
            @"Flydigi\SpaceStation\FlydigiSpaceStation.exe"),
        ["飞智空间站", "Flydigi Space Station", "Flydigi"],
        null,
        "https://www.flydigi.com/index/down");

    private static readonly LocalDriverInfo LeobogLocal = new(
        "leobog",
        "打开 LEOBOG ONE 驱动",
        ["OemDrv", "LEOBOG ONE"],
        [],
        ["LEOBOG ONE", "LEOBOG"],
        null,
        "https://www.leobog.com/");

    private static readonly LocalDriverInfo SteelSeriesLocal = new(
        "steelseries",
        "打开 SteelSeries GG",
        ["SteelSeriesGG", "SteelSeriesGGClient", "SteelSeriesEngine"],
        ExpandProgramFiles(false,
            @"SteelSeries\GG\SteelSeriesGG.exe",
            @"SteelSeries\SteelSeries Engine 3\SteelSeriesEngine3.exe"),
        ["SteelSeries GG", "SteelSeries Engine"],
        null,
        "https://steelseries.com/gg");

    private static readonly LocalDriverInfo CorsairLocal = new(
        "corsair",
        "打开 Corsair iCUE",
        ["iCUE"],
        ExpandProgramFiles(false,
            @"Corsair\Corsair iCUE5 Software\iCUE.exe",
            @"Corsair\CORSAIR iCUE 4 Software\iCUE.exe"),
        ["CORSAIR iCUE", "iCUE"],
        null,
        "https://www.corsair.com/icue");

    private static readonly LocalDriverInfo HyperXLocal = new(
        "hyperx",
        "打开 HyperX NGENUITY",
        ["NGENUITY"],
        [],
        ["HyperX NGENUITY", "NGENUITY"],
        null,
        "https://hyperx.com/pages/ngenuity");

    private static readonly LocalDriverInfo EndgameGearLocal = new(
        "egg",
        "打开 Endgame Gear 驱动",
        ["EGG", "EndgameGear"],
        [],
        ["Endgame Gear"],
        null,
        "https://www.endgamegear.com/downloads");

    private static readonly WebDriverInfo RazerWeb = new("razer", "打开雷蛇网页驱动", "https://synapse.razer.com");
    private static readonly WebDriverInfo FlydigiWeb = new("flydigi", "打开飞智网页空间站", "https://space_station.flydigi.com/");
    private static readonly WebDriverInfo AtkWeb = new("atk", "打开 ATK 网页驱动", "https://hub.atk.pro");
    private static readonly WebDriverInfo WlmouseWeb = new("wlmouse", "打开万灵网页驱动", "https://gm.wlmouse.gg");
    private static readonly WebDriverInfo AngryMiaoWeb = new("angrymiao", "打开怒喵鼠标驱动", "https://ammaster.angrymiao.com/mouse/devices");
    private static readonly WebDriverInfo AulaWeb = new("aula", "打开狼蛛网页驱动", "https://hub.aulacn.com");
    private static readonly WebDriverInfo LamzuWeb = new("lamzu", "打开兰族网页驱动", "https://www.lamzu.net/#/project/items");
    private static readonly WebDriverInfo RogWeb = new("rog", "打开 ROG Gear Link 网页驱动", "https://gearlink.asus.com");
    private static readonly WebDriverInfo MchoseWeb = new("mchose", "打开迈从网页驱动", "https://www.maicong.cn");
    private static readonly WebDriverInfo KeychronWeb = new("keychron", "打开 Keychron Launcher", "https://launcher.keychron.com");
    private static readonly WebDriverInfo PulsarWeb = new("pulsar", "打开 Pulsar Bibimbap", "https://bibimbap.pulsar.gg");
    private static readonly WebDriverInfo ScyroxWeb = new("scyrox", "打开 Scyrox 网页驱动", "https://scyrox.net");
    private static readonly WebDriverInfo MloongWeb = new("mloong", "打开 MLOONG HUB 网页驱动", "https://hub.mloongplanet.com/");
    private static readonly WebDriverInfo NinjutsoWeb = new("ninjutso", "打开 Ninjutso NinjaForce", "https://ninjaforce.co");
    private static readonly WebDriverInfo FinalmouseWeb = new("finalmouse", "打开 Finalmouse XPANEL", "https://xpanel.finalmouse.com");
    private static readonly WebDriverInfo RapooWeb = new("rapoo", "打开雷柏官方驱动页", "https://www.rapoo.cn/downloadcenter");
    private static readonly WebDriverInfo WootingWeb = new("wooting", "打开 Wootility 网页驱动", "https://wootility.io");
    private static readonly WebDriverInfo ViaWeb = new("via", "打开 VIA 网页改键", "https://usevia.app");
    private static readonly WebDriverInfo MonsGeekWeb = new("monsgeek", "打开 MonsGeek 网页驱动", "https://web.monsgeek.com");
    private static readonly WebDriverInfo DrunkDeerWeb = new("drunkdeer", "打开 DrunkDeer Antler", "https://drunkdeer-antler.com");
    private static readonly WebDriverInfo NuPhyWeb = new("nuphy", "打开 NuPhyIO 网页驱动", "https://nuphy.io");

    public static VendorDriverLinks Resolve(PeripheralDevice? device)
    {
        if (device == null)
            return new VendorDriverLinks(null, null);

        string id = device.Id.ToLowerInvariant();
        string prov = device.Provider.ToLowerInvariant();
        string name = device.Name.ToLowerInvariant();
        ushort vid = device.VendorId ?? 0;

        if (vid == 0x046D || id.StartsWith("logi:") || prov.Contains("logitech"))
            return new VendorDriverLinks(LogitechLocal, null);

        if (vid == 0x0B05 || id.StartsWith("rog:") || prov.Contains("rog") || name.Contains("rog"))
            return new VendorDriverLinks(RogLocal, RogWeb);

        if (vid == 0x1532 || id.StartsWith("razer:") || prov.Contains("razer"))
            return new VendorDriverLinks(RazerLocal, RazerWeb);

        if (id.StartsWith("ps:"))
            return new VendorDriverLinks(null, null);

        if (vid == 0x054C || id.StartsWith("sony:") || prov.Contains("sony") || name.Contains("inzone"))
            return new VendorDriverLinks(SonyLocal, null);

        if (vid == 0x1038 || id.StartsWith("steelseries:") || prov.Contains("steelseries"))
            return new VendorDriverLinks(SteelSeriesLocal, null);

        if (vid == 0x1B1C || id.StartsWith("corsair:") || prov.Contains("corsair"))
            return new VendorDriverLinks(CorsairLocal, null);

        if (vid is 0x03F0 or 0x0951 || id.StartsWith("hyperx:") || prov.Contains("hyperx"))
            return new VendorDriverLinks(HyperXLocal, null);

        if (vid == 0x37D7 || id.StartsWith("flydigi:") || prov.Contains("flydigi") || name.Contains("飞智") || name.Contains("八爪鱼"))
            return new VendorDriverLinks(FlydigiLocal, FlydigiWeb);

        if (vid is 0x373B || id.StartsWith("atk:") || prov.Contains("atk") || name.Contains("atk") || name.Contains("vxe") || name.Contains("vgn"))
            return new VendorDriverLinks(null, AtkWeb);

        if (vid is 0x36A7 || id.StartsWith("wlmouse:") || prov.Contains("wlmouse") || name.Contains("beast") || name.Contains("万灵"))
            return new VendorDriverLinks(null, WlmouseWeb);

        if (vid is 0x0E8D or 0x35A1 || id.StartsWith("am:") || prov.Contains("angry") || name.Contains("infinity") || name.Contains("怒喵"))
            return new VendorDriverLinks(null, AngryMiaoWeb);

        if (vid is 0x38A6 or 0x384C || id.StartsWith("aula:") || prov.Contains("aula") || name.Contains("狼蛛") || name.Contains("昆仑"))
            return new VendorDriverLinks(null, AulaWeb);

        if (vid is 0x373E || id.StartsWith("lamzu:") || prov.Contains("lamzu") || name.Contains("lamzu") || name.Contains("maya") || name.Contains("inca"))
            return new VendorDriverLinks(null, LamzuWeb);

        if (vid == 0x3367 || id.StartsWith("egg:") || name.Contains("endgame"))
            return new VendorDriverLinks(EndgameGearLocal, null);

        if (id.StartsWith("rapoo:") || name.Contains("rapoo"))
            return new VendorDriverLinks(null, RapooWeb);

        if (id.StartsWith("ninjutso:") || name.Contains("ninjutso") || name.Contains("sora"))
            return new VendorDriverLinks(null, NinjutsoWeb);

        if (vid == 0x361D || id.StartsWith("finalmouse:") || name.Contains("finalmouse"))
            return new VendorDriverLinks(null, FinalmouseWeb);

        if (vid is 0x24AE or 0x3837 || id.StartsWith("mchose:") || prov.Contains("mchose") || name.Contains("mchose") || name.Contains("迈从"))
            return new VendorDriverLinks(null, MchoseWeb);

        if (vid is 0x31E3 or 0x03EB || id.StartsWith("wooting:") || name.Contains("wooting"))
            return new VendorDriverLinks(null, WootingWeb);

        if (vid == 0x3434 || id.StartsWith("keychron:") || name.Contains("keychron"))
            return new VendorDriverLinks(null, KeychronWeb);

        if (id.StartsWith("monsgeek:") || name.Contains("monsgeek") || name.Contains("akko"))
            return new VendorDriverLinks(null, MonsGeekWeb);

        if (vid == 0x352D || id.StartsWith("drunkdeer:") || name.Contains("drunkdeer"))
            return new VendorDriverLinks(null, DrunkDeerWeb);

        if (vid == 0x19F5 || id.StartsWith("nuphy:") || name.Contains("nuphy"))
            return new VendorDriverLinks(null, NuPhyWeb);

        if (id.StartsWith("via:") || prov.Contains("via/qmk"))
            return new VendorDriverLinks(null, ViaWeb);

        if (vid is 0x3710 or 0x25A7 || id.StartsWith("pulsar:") || name.Contains("pulsar"))
            return new VendorDriverLinks(null, PulsarWeb);

        if (vid is 0xA8A4 or 0xA8A5 || id.StartsWith("mloong:") || prov.Contains("mloong") || name.Contains("mx301") || name.Contains("mloong"))
            return new VendorDriverLinks(null, MloongWeb);

        if ((vid is 0x258A or 0x3554 && (id.StartsWith("leobog:") || name.Contains("leobog") || name.Contains("hi98"))) ||
            id.StartsWith("leobog:") || prov.Contains("leobog"))
            return new VendorDriverLinks(LeobogLocal, null);

        if (name.Contains("scyrox"))
            return new VendorDriverLinks(null, ScyroxWeb);

        return new VendorDriverLinks(null, null);
    }
}
