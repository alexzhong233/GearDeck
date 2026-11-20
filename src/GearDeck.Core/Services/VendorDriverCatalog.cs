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
    private static readonly LocalDriverInfo LogitechLocal = new(
        "logitech",
        "打开 Logitech G HUB",
        ["lghub", "lghub_ui", "lghub_agent"],
        [
            @"C:\Program Files\LGHUB\lghub.exe",
            @"C:\Program Files\Logitech Gaming Software\LCore.exe",
        ],
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
        [
            @"C:\Program Files\Razer\RazerAppEngine\RazerAppEngine.exe",
            @"C:\Program Files\Razer\Razer Services\Razer Central\Razer Central.exe",
            @"C:\Program Files (x86)\Razer\Synapse3\WPFUI\Framework\Razer Synapse 3 Host\Razer Synapse 3.exe",
        ],
        ["Razer Synapse", "Razer Central"],
        null,
        "https://www.razer.com/synapse-4");

    private static readonly LocalDriverInfo SonyLocal = new(
        "sony",
        "打开 Sony INZONE Hub",
        ["INZONEHub", "INZONE Hub"],
        [
            @"C:\Program Files\Sony\INZONE Hub\INZONEHub.exe",
            @"C:\Program Files (x86)\Sony\INZONE Hub\INZONEHub.exe",
        ],
        ["INZONE Hub"],
        null,
        "https://www.sony.com/electronics/support/articles/00279263");

    private static readonly LocalDriverInfo FlydigiLocal = new(
        "flydigi",
        "打开 飞智空间站",
        ["FlydigiSpaceStation", "SpaceStation", "飞智空间站"],
        [
            @"C:\Program Files\FlydigiSpaceStation\FlydigiSpaceStation.exe",
            @"C:\Program Files (x86)\FlydigiSpaceStation\FlydigiSpaceStation.exe",
            @"C:\Program Files\Flydigi\SpaceStation\FlydigiSpaceStation.exe",
            @"C:\Program Files (x86)\Flydigi\SpaceStation\FlydigiSpaceStation.exe",
            @"D:\Program Files\FlydigiSpaceStation\FlydigiSpaceStation.exe",
        ],
        ["飞智空间站", "Flydigi Space Station", "Flydigi"],
        null,
        "https://www.flydigi.com/index/down");

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

        if (vid == 0x054C || id.StartsWith("sony:") || prov.Contains("sony") || name.Contains("inzone"))
            return new VendorDriverLinks(SonyLocal, null);

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

        if (vid is 0x24AE or 0x3837 || id.StartsWith("mchose:") || prov.Contains("mchose") || name.Contains("mchose") || name.Contains("迈从"))
            return new VendorDriverLinks(null, MchoseWeb);

        if (vid is 0x3434 || name.Contains("keychron"))
            return new VendorDriverLinks(null, KeychronWeb);

        if (vid is 0x3710 || name.Contains("pulsar"))
            return new VendorDriverLinks(null, PulsarWeb);

        if (name.Contains("scyrox"))
            return new VendorDriverLinks(null, ScyroxWeb);

        return new VendorDriverLinks(null, null);
    }
}
