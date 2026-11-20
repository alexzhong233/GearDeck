using System.Collections.Concurrent;

namespace GearDeck.Core.Providers;

internal enum AtkProtocolFamily { Unknown, Compx, Bitmouse }

internal enum AtkSensorKind
{
    Unknown,
    Paw3395,
    Paw3395Ultra,
    Paw3950,
    Paw3950Ultra,
    Paw3955Master,
    Paw3395Se,
    Paw3315,
    Paw3311,
    Core26K,
}

/// <summary>ATK / VXE / VGN / MAD 型号与传感器目录（VID:PID + 分协议 CID/MID 消歧 + 持久化缓存）。</summary>
internal static class AtkModelCatalog
{
    internal readonly record struct ModelSpec(string Name, AtkSensorKind Sensor);

    private static readonly Dictionary<(ushort Vid, ushort Pid), ModelSpec> UniqueByVidPid = new()
    {
        [(0x3554, 0xF502)] = new("ATK Dragonfly F1 PRO", AtkSensorKind.Paw3395),
        [(0x373B, 0x1014)] = new("ATK F1 PRO", AtkSensorKind.Paw3950),
        [(0x373B, 0x1015)] = new("ATK F1 PRO MAX", AtkSensorKind.Paw3950),
        [(0x373B, 0x1016)] = new("ATK X1 PRO", AtkSensorKind.Paw3950),
        [(0x373B, 0x1017)] = new("ATK X1 PRO MAX", AtkSensorKind.Paw3950),
        [(0x373B, 0x1018)] = new("ATK Z1 PRO", AtkSensorKind.Paw3950),
        [(0x373B, 0x1019)] = new("ATK Z1 PRO MAX", AtkSensorKind.Paw3950),
        [(0x373B, 0x102E)] = new("ATK F1 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x102F)] = new("ATK X1 Ultra", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1030)] = new("ATK FIERCE X", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x103C)] = new("ATK F1 Ultra", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x103D)] = new("ATK X1 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x103E)] = new("MAD R MAJOR", AtkSensorKind.Paw3950),
        [(0x373B, 0x103F)] = new("MAD R", AtkSensorKind.Paw3395),
        [(0x373B, 0x1045)] = new("ATK F1 EXTREME", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x104A)] = new("ATK Z1 Ultra", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x104B)] = new("MAD R +", AtkSensorKind.Paw3395),
        [(0x373B, 0x104C)] = new("MAD R MAJOR+", AtkSensorKind.Paw3950),
        [(0x373B, 0x105B)] = new("ATK F1 LEVIATAN", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1065)] = new("VXE R1 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1066)] = new("VXE R1 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1084)] = new("VXE R1S+", AtkSensorKind.Paw3311),
        [(0x373B, 0x1086)] = new("ATK U2 MINI", AtkSensorKind.Paw3950),
        [(0x373B, 0x1087)] = new("ATK U2 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x10AC)] = new("ATK U2 Ultra", AtkSensorKind.Paw3950),
        [(0x373B, 0x10C7)] = new("VXE R1 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x10C8)] = new("VXE R1 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x10D8)] = new("ATK X1", AtkSensorKind.Unknown),
        [(0x373B, 0x10E7)] = new("VXE V3 NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x10E8)] = new("VXE V3 PRO NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x10E9)] = new("ATK X1 EXTREME", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x10EC)] = new("ATK U2 Pro", AtkSensorKind.Unknown),
        [(0x373B, 0x10FD)] = new("ATK Y9 PRO MAX", AtkSensorKind.Paw3950),
        [(0x373B, 0x1106)] = new("ATK X1 S NK", AtkSensorKind.Paw3395),
        [(0x373B, 0x1107)] = new("ATK X1 SE NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x110D)] = new("ATK U2 Plus NK", AtkSensorKind.Paw3395),
        [(0x373B, 0x110E)] = new("ATK U2 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1115)] = new("ATK A9 Plus NK", AtkSensorKind.Paw3395),
        [(0x373B, 0x1116)] = new("ATK A9 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1117)] = new("ATK A9 Pro Max", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x1119)] = new("ATK A9 Ultra", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x112D)] = new("ATK A9 Pro", AtkSensorKind.Unknown),
        [(0x373B, 0x112E)] = new("ATK Y9 Pro", AtkSensorKind.Unknown),
        [(0x373B, 0x1135)] = new("ATK A9 SE NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x113E)] = new("ATK X1 Lite", AtkSensorKind.Paw3395),
        [(0x373B, 0x113F)] = new("ATK X1E", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1147)] = new("ATK A9 PRO MAX", AtkSensorKind.Unknown),
        [(0x373B, 0x1149)] = new("ATK U2 SE NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x114A)] = new("DRAGONFLY F1 V2 Plus NK", AtkSensorKind.Paw3395),
        [(0x373B, 0x114B)] = new("DRAGONFLY F1 V2 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x114C)] = new("DRAGONFLY F1 V2 SE NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x1154)] = new("ATK ZERO", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1157)] = new("ATK A9", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1158)] = new("ATK U2 Pro Max", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x115B)] = new("ATK Y9 Plus NK", AtkSensorKind.Paw3395),
        [(0x373B, 0x115C)] = new("ATK Y9 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x115D)] = new("ATK Y9 SE NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x1175)] = new("DRAGONFLY F1 V2 Pro", AtkSensorKind.Unknown),
        [(0x373B, 0x11A0)] = new("ATK A9 X", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x11A6)] = new("ATK Duckbill Ultra", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11AA)] = new("ATK U2", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x11AB)] = new("DRAGONFLY F1 V2 PRO MAX", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11B6)] = new("ATK A9 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11CC)] = new("ATK ZERO EX", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11CF)] = new("ATK Y9 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11D2)] = new("VXE R1 UlTRA", AtkSensorKind.Paw3395),
        [(0x373B, 0x11DB)] = new("ATK A9 Pro Max 2.0 NK", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11DD)] = new("ATK A9 Ultra Max 2.0", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11E3)] = new("ATK F1 EXTREME 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11E4)] = new("ATK F1 Ultimate 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11E5)] = new("ATK X1 EXTREME 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11E7)] = new("ATK X1 Ultimate 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11EC)] = new("ATK X1 Ultra Max 2.0", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11ED)] = new("ATK F1 Ultra Max 2.0", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11F0)] = new("ATK F1 LEVIATAN 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11F2)] = new("ATK A9 Ultra Max 2.0 Tri-Mode", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11F3)] = new("ATK A9 NB ULTRA", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11F5)] = new("ATK U2 Ultimate 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11F6)] = new("ATK U2 Mini 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11F7)] = new("ATK U2 Ultra Max 2.0", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11F9)] = new("VXE R1 Ultra", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x1201)] = new("ATK Z1 Ultimate 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x120A)] = new("ATK A9 Air", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x120B)] = new("ATK X1 EXTREME GT", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x120D)] = new("ATK Ghost EXTREME", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x120E)] = new("ATK Ghost Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1211)] = new("ATK A9 Ultimate 3.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1213)] = new("VXE V3 NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x1214)] = new("VXE V3 PRO NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1215)] = new("VXE V3 Plus", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x121F)] = new("VXE R1 S NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x1222)] = new("ATK A9 SE NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x1223)] = new("ATK A9 2.0 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1224)] = new("ATK A9 Plus NK", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x1230)] = new("ATK Duckbill EXTREME", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x123A)] = new("ATK Ghost Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1245)] = new("ATK A9 NB", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1248)] = new("VXE X3", AtkSensorKind.Paw3315),
        [(0x373B, 0x1249)] = new("VXE X3 Pro", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x124A)] = new("VXE X3 Turbo", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x124C)] = new("VXE R1 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x124D)] = new("VXE R1 S NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x124E)] = new("ATK ZERO", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1251)] = new("ATK A9 Mini +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1256)] = new("ATK A9 Mini", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x125A)] = new("ATK A9 Mini", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1263)] = new("ATK A9 Plus NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1264)] = new("ATK A9 Pro Max 2.0 NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1266)] = new("ATK PHANTOM EXTREME", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1268)] = new("ATK A9 Mini", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1269)] = new("ATK A9 Mini +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x126C)] = new("ATK U2 Plus NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x126D)] = new("ATK U2 Pro Max", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x126E)] = new("ATK F1 Ultra Max 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x126F)] = new("ATK A9 Ultra Max 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1270)] = new("ATK A9 Ultra Max 2.0 Tri-Mode", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1271)] = new("VXE R1", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1272)] = new("VXE R1 PRO", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1273)] = new("VXE R1 PRO MAX", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1274)] = new("ATK X1 Ultra Max 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x127B)] = new("VXE V3 PRO Max", AtkSensorKind.Core26K),
        [(0x373B, 0x127C)] = new("ATK X1 Air +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x127E)] = new("ATK A9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x127F)] = new("ATK A9 Air +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1282)] = new("ATK Duckbill ELITE", AtkSensorKind.Unknown),
        [(0x373B, 0x1285)] = new("ATK X1 EXTREME 2.0 +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1286)] = new("ATK X1 Ultimate 2.0 +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1287)] = new("ATK A9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1288)] = new("ATK A9 Air +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1289)] = new("ATK ZERO EX +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x128A)] = new("ATK Ghost EXTREME +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x128B)] = new("ATK Ghost Ultimate +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x128C)] = new("ATK U2 Ultimate 2.0 +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x128D)] = new("ATK U2 Mini 2.0 +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x128E)] = new("ATK A9 Mini +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x128F)] = new("ATK A9 Ultra Max 2.0 +", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1292)] = new("ATK A9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1293)] = new("ATK A9 Air +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1296)] = new("ATK PHANTOM ULTIMATE", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1299)] = new("ATK X1 SE Nearlink", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x129A)] = new("VXE R1 PRO MAX 2.0 NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x129B)] = new("ATK F1 V3 EXTREME +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x129C)] = new("ATK F1 Ultimate 2.0 +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x129D)] = new("ATK F1 V3 LEVIATAN +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x129E)] = new("ATK ZERO +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12A2)] = new("ATK X1 Air +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12A7)] = new("ATK F1 V3 ULTIMATE +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12A8)] = new("ATK F1 V3 ULTIMATE +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12A9)] = new("ATK F1 V3 ULTIMATE +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12AB)] = new("ATK A9 NB ULTRA +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12AF)] = new("ATK Y9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12B0)] = new("ATK Z1 Ultimate 2.0 +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12B1)] = new("VXE R2 SE NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x12B3)] = new("ATK A9 Ultimate Silent", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12B4)] = new("ATK X1 Ultimate 2.0 Silent", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12BF)] = new("VXE R1SE+ Silent", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x12C6)] = new("ATK A9 Wireless Charging", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12CE)] = new("ATK PHANTOM ULTIMATE", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12D0)] = new("ATK A9 NB ULTRA", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x12D2)] = new("VXE V3 NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x12D7)] = new("Flow 7", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x12D9)] = new("ATK X1 ULTIMATE 2.0+", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12DC)] = new("VXE R1 SE+", AtkSensorKind.Core26K),
        [(0x373B, 0x12DE)] = new("ATK X1 Ultra Max 2.0 + Tri-Mode", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12E1)] = new("VXE T1", AtkSensorKind.Unknown),
        [(0x373B, 0x12E3)] = new("ATK U2 Plus NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x12EC)] = new("ATK X1 Air+", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12F1)] = new("ATK A9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12FA)] = new("ATK F1 V3 ULTIMATE", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x12FF)] = new("ATK X1 Ultra Max 2.0", AtkSensorKind.Paw3950),
        [(0x373B, 0x1300)] = new("ATK A9 Plus NK", AtkSensorKind.Paw3950),
        [(0x373B, 0x1301)] = new("ATK A9 Pro Max 2.0 NK", AtkSensorKind.Paw3950),
        [(0x373B, 0x1304)] = new("ATK F1 V3 ULTIMATE+", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1305)] = new("ATK A9 3.0 NK", AtkSensorKind.Core26K),
        [(0x373B, 0x1306)] = new("VXE V3 PRO NK", AtkSensorKind.Core26K),
        [(0x373B, 0x1307)] = new("ATK F1 V3 ULTRA MAX", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1308)] = new("ATK F1 V3 ULTIMATE+", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x130E)] = new("ATK X1 SE NK", AtkSensorKind.Core26K),
        [(0x373B, 0x130F)] = new("ATK A9 SE 3.0 NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x1310)] = new("VXE R1 S NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x1311)] = new("VXE R1 NK", AtkSensorKind.Core26K),
        [(0x373B, 0x1312)] = new("ATK PHANTOM ULTIMATE", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1314)] = new("ATK A9 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1319)] = new("VXE R1 SE+", AtkSensorKind.Unknown),
        [(0x373B, 0x131A)] = new("ATK X1 S NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x132B)] = new("MAD R", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1337)] = new("ATK A9 Plus 3.0 NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1338)] = new("ATK A9 Pro Max 3.0 NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1366)] = new("VXE V3 Silent NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x135B)] = new("VXE R1 S NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x134D)] = new("ATK A9 SE 3.0 NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x135D)] = new("VXE V3 NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x134F)] = new("ATK A9 Plus 3.0 NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1350)] = new("ATK A9 Pro Max 3.0 NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1343)] = new("ATK A9 Air", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1344)] = new("ATK F1 V3 ULTIMATE", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1345)] = new("ATK F1 V3 ULTIMATE+", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1331)] = new("ATK ZERO + GENGAR EDITION", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x134E)] = new("ATK A9 3.0 NK", AtkSensorKind.Core26K),
        [(0x373B, 0x1368)] = new("VXE R1 SE+", AtkSensorKind.Core26K),
        [(0x373B, 0x135C)] = new("VXE R1 NK", AtkSensorKind.Core26K),
        [(0x373B, 0x1367)] = new("VXE V3 Pro Silent NK", AtkSensorKind.Core26K),
        [(0x373B, 0x1340)] = new("ATK A9 MAG", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1341)] = new("ATK X1 MAG", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x132A)] = new("ATK F1 V3 PIKACHU Edition", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x133B)] = new("ATK A9 Mini SPRIGATITO Edition", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1358)] = new("VXE R1SE+", AtkSensorKind.Core26K),
        [(0x373B, 0x1359)] = new("VXE R1SE+ Silent", AtkSensorKind.Core26K),
        [(0x373B, 0x1067)] = new("R1 NK", AtkSensorKind.Unknown),
        [(0x373B, 0x1068)] = new("R1 NK", AtkSensorKind.Unknown),
        [(0x373B, 0x1069)] = new("R1 NK", AtkSensorKind.Unknown),
        [(0x373B, 0x106A)] = new("R1 NK", AtkSensorKind.Unknown),
        [(0x373B, 0x10C9)] = new("NK Mouse Dongle", AtkSensorKind.Unknown),
        [(0x373B, 0x10CA)] = new("R1 NK", AtkSensorKind.Unknown),
        [(0x373B, 0x10CB)] = new("R1 NK", AtkSensorKind.Unknown),
        [(0x373B, 0x10CC)] = new("R1 NK", AtkSensorKind.Unknown),
    };

    private static readonly Dictionary<(ushort Vid, ushort Pid, byte Cid, byte Mid), ModelSpec> ByVidPidCidMid = new()
    {
        [(0x3554, 0xF502, 2, 1)] = new("ATK Dragonfly F1 PRO", AtkSensorKind.Paw3395),
        [(0x3554, 0xF506, 2, 5)] = new("ATK Dragonfly F1 MOBA", AtkSensorKind.Paw3395),
        [(0x3554, 0xF506, 2, 3)] = new("ATK Dragonfly F1 PRO MAX", AtkSensorKind.Paw3395),
        [(0x3554, 0xF506, 2, 48)] = new("ATK Dragonfly F1 PRO MAX Elden", AtkSensorKind.Paw3395),
        [(0x3554, 0xF506, 2, 52)] = new("ATK Dragonfly F1 PRO MAX JOJO", AtkSensorKind.Paw3395),
        [(0x3554, 0xF50A, 2, 20)] = new("ATK Dragonfly Dragonfly F1S", AtkSensorKind.Paw3395),
        [(0x3554, 0xF50A, 2, 19)] = new("ATK Dragonfly F1S", AtkSensorKind.Paw3395),
        [(0x3554, 0xF58C, 2, 7)] = new("VXE R1 PRO", AtkSensorKind.Paw3395),
        [(0x3554, 0xF58C, 2, 27)] = new("VXE R1 PRO MAX", AtkSensorKind.Paw3395),
        [(0x3554, 0xF58F, 2, 11)] = new("VXE R1", AtkSensorKind.Paw3395),
        [(0x3554, 0xF58F, 2, 29)] = new("VXE R1SE", AtkSensorKind.Paw3395Se),
        [(0x3554, 0xF58F, 2, 31)] = new("VXE R1SE+", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1014, 2, 36)] = new("ATK F1 PRO", AtkSensorKind.Paw3950),
        [(0x373B, 0x1015, 2, 37)] = new("ATK F1 PRO MAX", AtkSensorKind.Paw3950),
        [(0x373B, 0x1016, 2, 38)] = new("ATK X1 PRO", AtkSensorKind.Paw3950),
        [(0x373B, 0x1017, 2, 39)] = new("ATK X1 PRO MAX", AtkSensorKind.Paw3950),
        [(0x373B, 0x1018, 2, 40)] = new("ATK Z1 PRO", AtkSensorKind.Paw3950),
        [(0x373B, 0x1019, 2, 41)] = new("ATK Z1 PRO MAX", AtkSensorKind.Paw3950),
        [(0x373B, 0x102E, 2, 42)] = new("ATK F1 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x102F, 2, 43)] = new("ATK X1 Ultra", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1030, 2, 44)] = new("ATK FIERCE X", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x103C, 2, 45)] = new("ATK F1 Ultra", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x103D, 2, 46)] = new("ATK X1 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x103E, 2, 47)] = new("MAD R MAJOR", AtkSensorKind.Paw3950),
        [(0x373B, 0x103F, 2, 49)] = new("MAD R", AtkSensorKind.Paw3395),
        [(0x373B, 0x1045, 2, 50)] = new("ATK F1 EXTREME", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x104A, 2, 53)] = new("ATK Z1 Ultra", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x104B, 2, 54)] = new("MAD R +", AtkSensorKind.Paw3395),
        [(0x373B, 0x104C, 2, 55)] = new("MAD R MAJOR+", AtkSensorKind.Paw3950),
        [(0x373B, 0x105B, 2, 56)] = new("ATK F1 LEVIATAN", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1065, 2, 57)] = new("VXE R1 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1066, 2, 57)] = new("VXE R1 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1084, 2, 63)] = new("VXE R1S+", AtkSensorKind.Paw3311),
        [(0x373B, 0x1086, 2, 64)] = new("ATK U2 MINI", AtkSensorKind.Paw3950),
        [(0x373B, 0x1087, 2, 65)] = new("ATK U2 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x10AC, 2, 66)] = new("ATK U2 Ultra", AtkSensorKind.Paw3950),
        [(0x373B, 0x10C7, 2, 67)] = new("VXE R1 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x10C8, 2, 67)] = new("VXE R1 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x10D8, 1, 1)] = new("ATK X1", AtkSensorKind.Unknown),
        [(0x373B, 0x10E7, 2, 72)] = new("VXE V3 NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x10E8, 2, 73)] = new("VXE V3 PRO NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x10E9, 2, 70)] = new("ATK X1 EXTREME", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x10EC, 2, 1)] = new("ATK U2 Pro", AtkSensorKind.Unknown),
        [(0x373B, 0x10FD, 2, 108)] = new("ATK Y9 PRO MAX", AtkSensorKind.Paw3950),
        [(0x373B, 0x1106, 2, 76)] = new("ATK X1 S NK", AtkSensorKind.Paw3395),
        [(0x373B, 0x1107, 2, 77)] = new("ATK X1 SE NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x110D, 2, 78)] = new("ATK U2 Plus NK", AtkSensorKind.Paw3395),
        [(0x373B, 0x110E, 2, 79)] = new("ATK U2 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1115, 2, 83)] = new("ATK A9 Plus NK", AtkSensorKind.Paw3395),
        [(0x373B, 0x1116, 2, 84)] = new("ATK A9 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1117, 2, 85)] = new("ATK A9 Pro Max", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x1119, 2, 87)] = new("ATK A9 Ultra", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x112D, 2, 2)] = new("ATK A9 Pro", AtkSensorKind.Unknown),
        [(0x373B, 0x112E, 2, 3)] = new("ATK Y9 Pro", AtkSensorKind.Unknown),
        [(0x373B, 0x1135, 2, 88)] = new("ATK A9 SE NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x113E, 2, 89)] = new("ATK X1 Lite", AtkSensorKind.Paw3395),
        [(0x373B, 0x113F, 2, 90)] = new("ATK X1E", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1147, 0, 0)] = new("ATK A9 PRO MAX", AtkSensorKind.Unknown),
        [(0x373B, 0x1149, 2, 92)] = new("ATK U2 SE NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x114A, 2, 93)] = new("DRAGONFLY F1 V2 Plus NK", AtkSensorKind.Paw3395),
        [(0x373B, 0x114B, 2, 94)] = new("DRAGONFLY F1 V2 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x114C, 2, 95)] = new("DRAGONFLY F1 V2 SE NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x1154, 1, 1)] = new("ATK ZERO", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1157, 2, 99)] = new("ATK A9", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1158, 2, 100)] = new("ATK U2 Pro Max", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x115B, 2, 96)] = new("ATK Y9 Plus NK", AtkSensorKind.Paw3395),
        [(0x373B, 0x115C, 2, 97)] = new("ATK Y9 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x115D, 2, 98)] = new("ATK Y9 SE NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x1175, 4, 2)] = new("DRAGONFLY F1 V2 Pro", AtkSensorKind.Unknown),
        [(0x373B, 0x11A0, 2, 107)] = new("ATK A9 X", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x11A6, 1, 2)] = new("ATK Duckbill Ultra", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11AA, 2, 109)] = new("ATK U2", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x11AB, 2, 110)] = new("DRAGONFLY F1 V2 PRO MAX", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11B6, 1, 3)] = new("ATK A9 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11CC, 1, 4)] = new("ATK ZERO EX", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11CF, 1, 5)] = new("ATK Y9 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11D2, 2, 114)] = new("VXE R1 UlTRA", AtkSensorKind.Paw3395),
        [(0x373B, 0x11DB, 2, 1)] = new("ATK A9 Pro Max 2.0 NK", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11DD, 1, 6)] = new("ATK A9 Ultra Max 2.0", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11E3, 1, 7)] = new("ATK F1 EXTREME 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11E4, 1, 8)] = new("ATK F1 Ultimate 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11E5, 1, 9)] = new("ATK X1 EXTREME 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11E7, 1, 10)] = new("ATK X1 Ultimate 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11EC, 1, 11)] = new("ATK X1 Ultra Max 2.0", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11ED, 1, 12)] = new("ATK F1 Ultra Max 2.0", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11F0, 1, 13)] = new("ATK F1 LEVIATAN 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11F2, 1, 15)] = new("ATK A9 Ultra Max 2.0 Tri-Mode", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11F3, 2, 115)] = new("ATK A9 NB ULTRA", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11F5, 1, 16)] = new("ATK U2 Ultimate 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11F6, 1, 17)] = new("ATK U2 Mini 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x11F7, 1, 18)] = new("ATK U2 Ultra Max 2.0", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x11F9, 1, 20)] = new("VXE R1 Ultra", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x1201, 1, 19)] = new("ATK Z1 Ultimate 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x120A, 1, 21)] = new("ATK A9 Air", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x120B, 1, 22)] = new("ATK X1 EXTREME GT", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x120D, 1, 23)] = new("ATK Ghost EXTREME", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x120E, 1, 24)] = new("ATK Ghost Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1211, 1, 2)] = new("ATK A9 Ultimate 3.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1213, 2, 5)] = new("VXE V3 NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x1214, 2, 6)] = new("VXE V3 PRO NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1215, 2, 7)] = new("VXE V3 Plus", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x121F, 2, 117)] = new("VXE R1 S NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x1222, 2, 2)] = new("ATK A9 SE NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x1223, 2, 3)] = new("ATK A9 2.0 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1224, 2, 4)] = new("ATK A9 Plus NK", AtkSensorKind.Paw3395Ultra),
        [(0x373B, 0x1230, 1, 25)] = new("ATK Duckbill EXTREME", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x123A, 1, 3)] = new("ATK Ghost Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1245, 2, 120)] = new("ATK A9 NB", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1248, 2, 121)] = new("VXE X3", AtkSensorKind.Paw3315),
        [(0x373B, 0x1249, 2, 122)] = new("VXE X3 Pro", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x124A, 2, 123)] = new("VXE X3 Turbo", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x124C, 2, 8)] = new("VXE R1 NK", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x124D, 2, 9)] = new("VXE R1 S NK", AtkSensorKind.Paw3311),
        [(0x373B, 0x124E, 1, 26)] = new("ATK ZERO", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1251, 1, 4)] = new("ATK A9 Mini +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1256, 1, 27)] = new("ATK A9 Mini", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x125A, 1, 28)] = new("ATK A9 Mini", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1263, 2, 13)] = new("ATK A9 Plus NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1264, 2, 14)] = new("ATK A9 Pro Max 2.0 NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1266, 1, 30)] = new("ATK PHANTOM EXTREME", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1268, 1, 16)] = new("ATK A9 Mini", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1269, 1, 31)] = new("ATK A9 Mini +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x126C, 2, 124)] = new("ATK U2 Plus NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x126D, 2, 128)] = new("ATK U2 Pro Max", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x126E, 1, 33)] = new("ATK F1 Ultra Max 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x126F, 1, 38)] = new("ATK A9 Ultra Max 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1270, 1, 37)] = new("ATK A9 Ultra Max 2.0 Tri-Mode", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1271, 2, 127)] = new("VXE R1", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1272, 2, 125)] = new("VXE R1 PRO", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1273, 2, 126)] = new("VXE R1 PRO MAX", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1274, 1, 32)] = new("ATK X1 Ultra Max 2.0", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x127B, 2, 15)] = new("VXE V3 PRO Max", AtkSensorKind.Core26K),
        [(0x373B, 0x127C, 1, 42)] = new("ATK X1 Air +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x127E, 1, 39)] = new("ATK A9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x127F, 1, 40)] = new("ATK A9 Air +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1285, 1, 45)] = new("ATK X1 EXTREME 2.0 +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1286, 1, 46)] = new("ATK X1 Ultimate 2.0 +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1287, 1, 9)] = new("ATK A9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1288, 1, 6)] = new("ATK A9 Air +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1289, 1, 47)] = new("ATK ZERO EX +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x128A, 1, 48)] = new("ATK Ghost EXTREME +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x128B, 1, 49)] = new("ATK Ghost Ultimate +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x128C, 1, 50)] = new("ATK U2 Ultimate 2.0 +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x128D, 1, 51)] = new("ATK U2 Mini 2.0 +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x128E, 1, 52)] = new("ATK A9 Mini +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x128F, 1, 7)] = new("ATK A9 Ultra Max 2.0 +", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1292, 1, 53)] = new("ATK A9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1293, 1, 54)] = new("ATK A9 Air +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1296, 1, 55)] = new("ATK PHANTOM ULTIMATE", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1299, 2, 16)] = new("ATK X1 SE Nearlink", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x129A, 2, 17)] = new("VXE R1 PRO MAX 2.0 NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x129B, 1, 58)] = new("ATK F1 V3 EXTREME +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x129C, 1, 59)] = new("ATK F1 Ultimate 2.0 +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x129D, 1, 60)] = new("ATK F1 V3 LEVIATAN +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x129E, 1, 8)] = new("ATK ZERO +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12A2, 1, 61)] = new("ATK X1 Air +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12A7, 1, 62)] = new("ATK F1 V3 ULTIMATE +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12A8, 1, 63)] = new("ATK F1 V3 ULTIMATE +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12A9, 1, 64)] = new("ATK F1 V3 ULTIMATE +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12AB, 2, 129)] = new("ATK A9 NB ULTRA +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12AF, 1, 66)] = new("ATK Y9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12B0, 1, 67)] = new("ATK Z1 Ultimate 2.0 +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12B1, 2, 19)] = new("VXE R2 SE NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x12B3, 1, 75)] = new("ATK A9 Ultimate Silent", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12B4, 1, 76)] = new("ATK X1 Ultimate 2.0 Silent", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12BF, 2, 130)] = new("VXE R1SE+ Silent", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x12C6, 1, 71)] = new("ATK A9 Wireless Charging", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12CE, 1, 10)] = new("ATK PHANTOM ULTIMATE", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12D0, 2, 20)] = new("ATK A9 NB ULTRA", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x12D2, 2, 21)] = new("VXE V3 NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x12D7, 2, 131)] = new("Flow 7", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x12D9, 1, 12)] = new("ATK X1 ULTIMATE 2.0+", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12DC, 2, 22)] = new("VXE R1 SE+", AtkSensorKind.Core26K),
        [(0x373B, 0x12DE, 1, 13)] = new("ATK X1 Ultra Max 2.0 + Tri-Mode", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12E1, 2, 23)] = new("VXE T1", AtkSensorKind.Unknown),
        [(0x373B, 0x12E3, 2, 25)] = new("ATK U2 Plus NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x12EC, 1, 14)] = new("ATK X1 Air+", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12F1, 1, 15)] = new("ATK A9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x12FA, 1, 85)] = new("ATK F1 V3 ULTIMATE", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x12FF, 1, 87)] = new("ATK X1 Ultra Max 2.0", AtkSensorKind.Paw3950),
        [(0x373B, 0x1300, 2, 26)] = new("ATK A9 Plus NK", AtkSensorKind.Paw3950),
        [(0x373B, 0x1301, 2, 27)] = new("ATK A9 Pro Max 2.0 NK", AtkSensorKind.Paw3950),
        [(0x373B, 0x1304, 1, 17)] = new("ATK F1 V3 ULTIMATE+", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1305, 2, 29)] = new("ATK A9 3.0 NK", AtkSensorKind.Core26K),
        [(0x373B, 0x1306, 2, 30)] = new("VXE V3 PRO NK", AtkSensorKind.Core26K),
        [(0x373B, 0x1307, 1, 19)] = new("ATK F1 V3 ULTRA MAX", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1308, 1, 18)] = new("ATK F1 V3 ULTIMATE+", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x130E, 2, 33)] = new("ATK X1 SE NK", AtkSensorKind.Core26K),
        [(0x373B, 0x130F, 2, 32)] = new("ATK A9 SE 3.0 NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x1310, 2, 31)] = new("VXE R1 S NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x1311, 2, 28)] = new("VXE R1 NK", AtkSensorKind.Core26K),
        [(0x373B, 0x1312, 1, 21)] = new("ATK PHANTOM ULTIMATE", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1314, 1, 22)] = new("ATK A9 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1319, 2, 22)] = new("VXE R1 SE+", AtkSensorKind.Unknown),
        [(0x373B, 0x131A, 2, 132)] = new("ATK X1 S NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x132B, 2, 133)] = new("MAD R", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1337, 2, 34)] = new("ATK A9 Plus 3.0 NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1338, 2, 35)] = new("ATK A9 Pro Max 3.0 NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1366, 2, 48)] = new("VXE V3 Silent NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x135B, 2, 44)] = new("VXE R1 S NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x134D, 2, 39)] = new("ATK A9 SE 3.0 NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x135D, 2, 45)] = new("VXE V3 NK", AtkSensorKind.Paw3315),
        [(0x373B, 0x134F, 2, 40)] = new("ATK A9 Plus 3.0 NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1350, 2, 41)] = new("ATK A9 Pro Max 3.0 NK", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1343, 1, 26)] = new("ATK A9 Air", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x129E, 8, 1)] = new("ATK ZERO +", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1344, 1, 27)] = new("ATK F1 V3 ULTIMATE", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1345, 1, 28)] = new("ATK F1 V3 ULTIMATE+", AtkSensorKind.Paw3950Ultra),
        [(0x373B, 0x1331, 1, 24)] = new("ATK ZERO + GENGAR EDITION", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x134E, 2, 38)] = new("ATK A9 3.0 NK", AtkSensorKind.Core26K),
        [(0x373B, 0x1368, 2, 42)] = new("VXE R1 SE+", AtkSensorKind.Core26K),
        [(0x373B, 0x135C, 2, 43)] = new("VXE R1 NK", AtkSensorKind.Core26K),
        [(0x373B, 0x1367, 2, 49)] = new("VXE V3 Pro Silent NK", AtkSensorKind.Core26K),
        [(0x3554, 0xF502, 2, 2)] = new("ATK Dragonfly F1 PRO", AtkSensorKind.Paw3395),
        [(0x3554, 0xF502, 2, 104)] = new("ATK Dragonfly F1 PRO", AtkSensorKind.Paw3395),
        [(0x3554, 0xF506, 2, 4)] = new("ATK Dragonfly F1 PRO MAX", AtkSensorKind.Paw3395),
        [(0x3554, 0xF506, 2, 105)] = new("ATK Dragonfly F1 PRO MAX", AtkSensorKind.Paw3395),
        [(0x3554, 0xF506, 2, 6)] = new("ATK Dragonfly F1 MOBA", AtkSensorKind.Paw3395),
        [(0x3554, 0xF506, 2, 33)] = new("ATK Dragonfly F1 MOBA", AtkSensorKind.Paw3395),
        [(0x3554, 0xF506, 2, 106)] = new("ATK Dragonfly F1 MOBA", AtkSensorKind.Paw3395),
        [(0x3554, 0xF58C, 2, 8)] = new("VXE R1 PRO", AtkSensorKind.Paw3395),
        [(0x3554, 0xF58C, 2, 28)] = new("VXE R1 PRO MAX", AtkSensorKind.Paw3395),
        [(0x3554, 0xF58C, 2, 34)] = new("VXE R1 PRO MAX", AtkSensorKind.Paw3395),
        [(0x3554, 0xF58F, 2, 12)] = new("VXE R1", AtkSensorKind.Paw3395),
        [(0x3554, 0xF58F, 2, 30)] = new("VXE R1SE", AtkSensorKind.Paw3395Se),
        [(0x3554, 0xF58F, 2, 32)] = new("VXE R1SE+", AtkSensorKind.Paw3395Se),
        [(0x3554, 0xF58F, 2, 116)] = new("VXE R1SE+", AtkSensorKind.Paw3395Se),
        [(0x373B, 0x1340, 2, 134)] = new("ATK A9 MAG", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1341, 2, 135)] = new("ATK X1 MAG", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x132A, 1, 90)] = new("ATK F1 V3 PIKACHU Edition", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x133B, 1, 91)] = new("ATK A9 Mini SPRIGATITO Edition", AtkSensorKind.Paw3955Master),
        [(0x373B, 0x1358, 2, 136)] = new("VXE R1SE+", AtkSensorKind.Core26K),
        [(0x373B, 0x1359, 2, 137)] = new("VXE R1SE+ Silent", AtkSensorKind.Core26K),
    };

    private static readonly Dictionary<(byte Cid, byte Mid), ModelSpec> CompxByCidMid = new()
    {
        [(2, 1)] = new("ATK Dragonfly F1 PRO", AtkSensorKind.Paw3395),
        [(2, 5)] = new("ATK Dragonfly F1 MOBA", AtkSensorKind.Paw3395),
        [(2, 3)] = new("ATK Dragonfly F1 PRO MAX", AtkSensorKind.Paw3395),
        [(2, 48)] = new("ATK Dragonfly F1 PRO MAX Elden", AtkSensorKind.Paw3395),
        [(2, 52)] = new("ATK Dragonfly F1 PRO MAX JOJO", AtkSensorKind.Paw3395),
        [(2, 20)] = new("ATK Dragonfly Dragonfly F1S", AtkSensorKind.Paw3395),
        [(2, 19)] = new("ATK Dragonfly F1S", AtkSensorKind.Paw3395),
        [(2, 7)] = new("VXE R1 PRO", AtkSensorKind.Paw3395),
        [(2, 27)] = new("VXE R1 PRO MAX", AtkSensorKind.Paw3395),
        [(2, 11)] = new("VXE R1", AtkSensorKind.Paw3395),
        [(2, 29)] = new("VXE R1SE", AtkSensorKind.Paw3395Se),
        [(2, 31)] = new("VXE R1SE+", AtkSensorKind.Paw3395Se),
        [(2, 36)] = new("ATK F1 PRO", AtkSensorKind.Paw3950),
        [(2, 37)] = new("ATK F1 PRO MAX", AtkSensorKind.Paw3950),
        [(2, 38)] = new("ATK X1 PRO", AtkSensorKind.Paw3950),
        [(2, 39)] = new("ATK X1 PRO MAX", AtkSensorKind.Paw3950),
        [(2, 40)] = new("ATK Z1 PRO", AtkSensorKind.Paw3950),
        [(2, 41)] = new("ATK Z1 PRO MAX", AtkSensorKind.Paw3950),
        [(2, 42)] = new("ATK F1 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(2, 43)] = new("ATK X1 Ultra", AtkSensorKind.Paw3950Ultra),
        [(2, 44)] = new("ATK FIERCE X", AtkSensorKind.Paw3950Ultra),
        [(2, 45)] = new("ATK F1 Ultra", AtkSensorKind.Paw3950Ultra),
        [(2, 46)] = new("ATK X1 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(2, 47)] = new("MAD R MAJOR", AtkSensorKind.Paw3950),
        [(2, 49)] = new("MAD R", AtkSensorKind.Paw3395),
        [(2, 50)] = new("ATK F1 EXTREME", AtkSensorKind.Paw3950Ultra),
        [(2, 53)] = new("ATK Z1 Ultra", AtkSensorKind.Paw3950Ultra),
        [(2, 54)] = new("MAD R +", AtkSensorKind.Paw3395),
        [(2, 55)] = new("MAD R MAJOR+", AtkSensorKind.Paw3950),
        [(2, 56)] = new("ATK F1 LEVIATAN", AtkSensorKind.Paw3950Ultra),
        [(2, 57)] = new("VXE R1 NK", AtkSensorKind.Paw3395Se),
        [(2, 63)] = new("VXE R1S+", AtkSensorKind.Paw3311),
        [(2, 64)] = new("ATK U2 MINI", AtkSensorKind.Paw3950),
        [(2, 65)] = new("ATK U2 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(2, 66)] = new("ATK U2 Ultra", AtkSensorKind.Paw3950),
        [(2, 67)] = new("VXE R1 NK", AtkSensorKind.Paw3395Se),
        [(1, 1)] = new("ATK X1", AtkSensorKind.Unknown),
        [(2, 72)] = new("VXE V3 NK", AtkSensorKind.Paw3311),
        [(2, 73)] = new("VXE V3 PRO NK", AtkSensorKind.Paw3395Se),
        [(2, 70)] = new("ATK X1 EXTREME", AtkSensorKind.Paw3950Ultra),
        [(2, 108)] = new("ATK Y9 PRO MAX", AtkSensorKind.Paw3950),
        [(2, 76)] = new("ATK X1 S NK", AtkSensorKind.Paw3395),
        [(2, 77)] = new("ATK X1 SE NK", AtkSensorKind.Paw3395Se),
        [(2, 78)] = new("ATK U2 Plus NK", AtkSensorKind.Paw3395),
        [(2, 79)] = new("ATK U2 NK", AtkSensorKind.Paw3395Se),
        [(2, 83)] = new("ATK A9 Plus NK", AtkSensorKind.Paw3395),
        [(2, 84)] = new("ATK A9 NK", AtkSensorKind.Paw3395Se),
        [(2, 85)] = new("ATK A9 Pro Max", AtkSensorKind.Paw3395Ultra),
        [(2, 87)] = new("ATK A9 Ultra", AtkSensorKind.Paw3950Ultra),
        [(2, 2)] = new("ATK Dragonfly F1 PRO", AtkSensorKind.Paw3395),
        [(2, 88)] = new("ATK A9 SE NK", AtkSensorKind.Paw3311),
        [(2, 89)] = new("ATK X1 Lite", AtkSensorKind.Paw3395),
        [(2, 90)] = new("ATK X1E", AtkSensorKind.Paw3395Se),
        [(0, 0)] = new("ATK A9 PRO MAX", AtkSensorKind.Unknown),
        [(2, 92)] = new("ATK U2 SE NK", AtkSensorKind.Paw3311),
        [(2, 93)] = new("DRAGONFLY F1 V2 Plus NK", AtkSensorKind.Paw3395),
        [(2, 94)] = new("DRAGONFLY F1 V2 NK", AtkSensorKind.Paw3395Se),
        [(2, 95)] = new("DRAGONFLY F1 V2 SE NK", AtkSensorKind.Paw3311),
        [(2, 99)] = new("ATK A9", AtkSensorKind.Paw3395Se),
        [(2, 100)] = new("ATK U2 Pro Max", AtkSensorKind.Paw3395Ultra),
        [(2, 96)] = new("ATK Y9 Plus NK", AtkSensorKind.Paw3395),
        [(2, 97)] = new("ATK Y9 NK", AtkSensorKind.Paw3395Se),
        [(2, 98)] = new("ATK Y9 SE NK", AtkSensorKind.Paw3311),
        [(4, 2)] = new("DRAGONFLY F1 V2 Pro", AtkSensorKind.Unknown),
        [(2, 107)] = new("ATK A9 X", AtkSensorKind.Paw3395Se),
        [(1, 2)] = new("ATK Duckbill Ultra", AtkSensorKind.Paw3950Ultra),
        [(2, 109)] = new("ATK U2", AtkSensorKind.Paw3395Se),
        [(2, 110)] = new("DRAGONFLY F1 V2 PRO MAX", AtkSensorKind.Paw3395Ultra),
        [(1, 3)] = new("ATK A9 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(1, 4)] = new("ATK ZERO EX", AtkSensorKind.Paw3950Ultra),
        [(1, 5)] = new("ATK Y9 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(2, 114)] = new("VXE R1 UlTRA", AtkSensorKind.Paw3395),
        [(1, 6)] = new("ATK A9 Ultra Max 2.0", AtkSensorKind.Paw3395Ultra),
        [(1, 7)] = new("ATK F1 EXTREME 2.0", AtkSensorKind.Paw3950Ultra),
        [(1, 8)] = new("ATK F1 Ultimate 2.0", AtkSensorKind.Paw3950Ultra),
        [(1, 9)] = new("ATK X1 EXTREME 2.0", AtkSensorKind.Paw3950Ultra),
        [(1, 10)] = new("ATK X1 Ultimate 2.0", AtkSensorKind.Paw3950Ultra),
        [(1, 11)] = new("ATK X1 Ultra Max 2.0", AtkSensorKind.Paw3395Ultra),
        [(1, 12)] = new("ATK F1 Ultra Max 2.0", AtkSensorKind.Paw3395Ultra),
        [(1, 13)] = new("ATK F1 LEVIATAN 2.0", AtkSensorKind.Paw3950Ultra),
        [(1, 15)] = new("ATK A9 Ultra Max 2.0 Tri-Mode", AtkSensorKind.Paw3395Ultra),
        [(2, 115)] = new("ATK A9 NB ULTRA", AtkSensorKind.Paw3950Ultra),
        [(1, 16)] = new("ATK U2 Ultimate 2.0", AtkSensorKind.Paw3950Ultra),
        [(1, 17)] = new("ATK U2 Mini 2.0", AtkSensorKind.Paw3950Ultra),
        [(1, 18)] = new("ATK U2 Ultra Max 2.0", AtkSensorKind.Paw3395Ultra),
        [(1, 20)] = new("VXE R1 Ultra", AtkSensorKind.Paw3395Ultra),
        [(1, 19)] = new("ATK Z1 Ultimate 2.0", AtkSensorKind.Paw3950Ultra),
        [(1, 21)] = new("ATK A9 Air", AtkSensorKind.Paw3950Ultra),
        [(1, 22)] = new("ATK X1 EXTREME GT", AtkSensorKind.Paw3950Ultra),
        [(1, 23)] = new("ATK Ghost EXTREME", AtkSensorKind.Paw3950Ultra),
        [(1, 24)] = new("ATK Ghost Ultimate", AtkSensorKind.Paw3950Ultra),
        [(2, 6)] = new("ATK Dragonfly F1 MOBA", AtkSensorKind.Paw3395),
        [(2, 117)] = new("VXE R1 S NK", AtkSensorKind.Paw3311),
        [(2, 4)] = new("ATK Dragonfly F1 PRO MAX", AtkSensorKind.Paw3395),
        [(1, 25)] = new("ATK Duckbill EXTREME", AtkSensorKind.Paw3950Ultra),
        [(2, 120)] = new("ATK A9 NB", AtkSensorKind.Paw3395Se),
        [(2, 121)] = new("VXE X3", AtkSensorKind.Paw3315),
        [(2, 122)] = new("VXE X3 Pro", AtkSensorKind.Paw3395Se),
        [(2, 123)] = new("VXE X3 Turbo", AtkSensorKind.Paw3395Se),
        [(2, 8)] = new("VXE R1 PRO", AtkSensorKind.Paw3395),
        [(2, 9)] = new("VXE R1 S NK", AtkSensorKind.Unknown),
        [(1, 26)] = new("ATK ZERO", AtkSensorKind.Paw3950Ultra),
        [(1, 27)] = new("ATK A9 Mini", AtkSensorKind.Paw3950Ultra),
        [(1, 28)] = new("ATK A9 Mini", AtkSensorKind.Paw3950Ultra),
        [(2, 13)] = new("ATK A9 Plus NK", AtkSensorKind.Unknown),
        [(2, 14)] = new("ATK A9 Pro Max 2.0 NK", AtkSensorKind.Unknown),
        [(1, 30)] = new("ATK PHANTOM EXTREME", AtkSensorKind.Paw3955Master),
        [(1, 31)] = new("ATK A9 Mini +", AtkSensorKind.Paw3955Master),
        [(2, 124)] = new("ATK U2 Plus NK", AtkSensorKind.Paw3950Ultra),
        [(2, 128)] = new("ATK U2 Pro Max", AtkSensorKind.Paw3950Ultra),
        [(1, 33)] = new("ATK F1 Ultra Max 2.0", AtkSensorKind.Paw3950Ultra),
        [(1, 38)] = new("ATK A9 Ultra Max 2.0", AtkSensorKind.Paw3950Ultra),
        [(1, 37)] = new("ATK A9 Ultra Max 2.0 Tri-Mode", AtkSensorKind.Paw3950Ultra),
        [(2, 127)] = new("VXE R1", AtkSensorKind.Paw3950Ultra),
        [(2, 125)] = new("VXE R1 PRO", AtkSensorKind.Paw3950Ultra),
        [(2, 126)] = new("VXE R1 PRO MAX", AtkSensorKind.Paw3950Ultra),
        [(1, 32)] = new("ATK X1 Ultra Max 2.0", AtkSensorKind.Paw3950Ultra),
        [(2, 15)] = new("VXE V3 PRO Max", AtkSensorKind.Unknown),
        [(1, 42)] = new("ATK X1 Air +", AtkSensorKind.Paw3955Master),
        [(1, 39)] = new("ATK A9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(1, 40)] = new("ATK A9 Air +", AtkSensorKind.Paw3955Master),
        [(1, 45)] = new("ATK X1 EXTREME 2.0 +", AtkSensorKind.Paw3955Master),
        [(1, 46)] = new("ATK X1 Ultimate 2.0 +", AtkSensorKind.Paw3955Master),
        [(1, 47)] = new("ATK ZERO EX +", AtkSensorKind.Paw3955Master),
        [(1, 48)] = new("ATK Ghost EXTREME +", AtkSensorKind.Paw3955Master),
        [(1, 49)] = new("ATK Ghost Ultimate +", AtkSensorKind.Paw3955Master),
        [(1, 50)] = new("ATK U2 Ultimate 2.0 +", AtkSensorKind.Paw3955Master),
        [(1, 51)] = new("ATK U2 Mini 2.0 +", AtkSensorKind.Paw3955Master),
        [(1, 52)] = new("ATK A9 Mini +", AtkSensorKind.Paw3955Master),
        [(1, 53)] = new("ATK A9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(1, 54)] = new("ATK A9 Air +", AtkSensorKind.Paw3955Master),
        [(1, 55)] = new("ATK PHANTOM ULTIMATE", AtkSensorKind.Paw3955Master),
        [(2, 16)] = new("ATK X1 SE Nearlink", AtkSensorKind.Unknown),
        [(2, 17)] = new("VXE R1 PRO MAX 2.0 NK", AtkSensorKind.Unknown),
        [(1, 58)] = new("ATK F1 V3 EXTREME +", AtkSensorKind.Paw3955Master),
        [(1, 59)] = new("ATK F1 Ultimate 2.0 +", AtkSensorKind.Paw3955Master),
        [(1, 60)] = new("ATK F1 V3 LEVIATAN +", AtkSensorKind.Paw3955Master),
        [(1, 61)] = new("ATK X1 Air +", AtkSensorKind.Paw3955Master),
        [(1, 62)] = new("ATK F1 V3 ULTIMATE +", AtkSensorKind.Paw3955Master),
        [(1, 63)] = new("ATK F1 V3 ULTIMATE +", AtkSensorKind.Paw3955Master),
        [(1, 64)] = new("ATK F1 V3 ULTIMATE +", AtkSensorKind.Paw3955Master),
        [(2, 129)] = new("ATK A9 NB ULTRA +", AtkSensorKind.Paw3955Master),
        [(1, 66)] = new("ATK Y9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(1, 67)] = new("ATK Z1 Ultimate 2.0 +", AtkSensorKind.Paw3955Master),
        [(1, 75)] = new("ATK A9 Ultimate Silent", AtkSensorKind.Paw3955Master),
        [(1, 76)] = new("ATK X1 Ultimate 2.0 Silent", AtkSensorKind.Paw3955Master),
        [(2, 130)] = new("VXE R1SE+ Silent", AtkSensorKind.Paw3395Se),
        [(1, 71)] = new("ATK A9 Wireless Charging", AtkSensorKind.Paw3955Master),
        [(2, 21)] = new("VXE V3 NK", AtkSensorKind.Unknown),
        [(2, 131)] = new("Flow 7", AtkSensorKind.Paw3950Ultra),
        [(2, 22)] = new("VXE R1 SE+", AtkSensorKind.Unknown),
        [(2, 23)] = new("VXE T1", AtkSensorKind.Unknown),
        [(2, 25)] = new("ATK U2 Plus NK", AtkSensorKind.Unknown),
        [(1, 14)] = new("ATK X1 Air+", AtkSensorKind.Unknown),
        [(1, 85)] = new("ATK F1 V3 ULTIMATE", AtkSensorKind.Paw3950Ultra),
        [(1, 87)] = new("ATK X1 Ultra Max 2.0", AtkSensorKind.Paw3950),
        [(2, 26)] = new("ATK A9 Plus NK", AtkSensorKind.Unknown),
        [(2, 30)] = new("VXE R1SE", AtkSensorKind.Paw3395Se),
        [(2, 33)] = new("ATK Dragonfly F1 MOBA", AtkSensorKind.Paw3395),
        [(2, 32)] = new("VXE R1SE+", AtkSensorKind.Paw3395Se),
        [(2, 28)] = new("VXE R1 PRO MAX", AtkSensorKind.Paw3395),
        [(2, 132)] = new("ATK X1 S NK", AtkSensorKind.Paw3950Ultra),
        [(2, 133)] = new("MAD R", AtkSensorKind.Paw3950Ultra),
        [(2, 34)] = new("VXE R1 PRO MAX", AtkSensorKind.Paw3395),
        [(2, 35)] = new("ATK A9 Pro Max 3.0 NK", AtkSensorKind.Unknown),
        [(2, 104)] = new("ATK Dragonfly F1 PRO", AtkSensorKind.Paw3395),
        [(2, 105)] = new("ATK Dragonfly F1 PRO MAX", AtkSensorKind.Paw3395),
        [(2, 106)] = new("ATK Dragonfly F1 MOBA", AtkSensorKind.Paw3395),
        [(2, 12)] = new("VXE R1", AtkSensorKind.Paw3395),
        [(2, 116)] = new("VXE R1SE+", AtkSensorKind.Paw3395Se),
        [(2, 134)] = new("ATK A9 MAG", AtkSensorKind.Paw3955Master),
        [(2, 135)] = new("ATK X1 MAG", AtkSensorKind.Paw3955Master),
        [(1, 90)] = new("ATK F1 V3 PIKACHU Edition", AtkSensorKind.Paw3955Master),
        [(1, 91)] = new("ATK A9 Mini SPRIGATITO Edition", AtkSensorKind.Paw3955Master),
        [(2, 136)] = new("VXE R1SE+", AtkSensorKind.Core26K),
        [(2, 137)] = new("VXE R1SE+ Silent", AtkSensorKind.Core26K),
    };

    private static readonly Dictionary<(byte Cid, byte Mid), ModelSpec> BitmouseByCidMid = new()
    {
        [(2, 9)] = new("VXE R1 S NK", AtkSensorKind.Paw3311),
        [(2, 2)] = new("ATK A9 SE NK", AtkSensorKind.Paw3311),
        [(2, 5)] = new("VXE V3 NK", AtkSensorKind.Paw3311),
        [(2, 21)] = new("VXE V3 NK", AtkSensorKind.Paw3315),
        [(2, 48)] = new("VXE V3 Silent NK", AtkSensorKind.Paw3315),
        [(2, 31)] = new("VXE R1 S NK", AtkSensorKind.Paw3315),
        [(2, 44)] = new("VXE R1 S NK", AtkSensorKind.Paw3315),
        [(2, 32)] = new("ATK A9 SE 3.0 NK", AtkSensorKind.Paw3315),
        [(2, 39)] = new("ATK A9 SE 3.0 NK", AtkSensorKind.Paw3315),
        [(2, 19)] = new("VXE R2 SE NK", AtkSensorKind.Paw3315),
        [(2, 45)] = new("VXE V3 NK", AtkSensorKind.Paw3315),
        [(2, 3)] = new("ATK A9 2.0 NK", AtkSensorKind.Paw3395Se),
        [(2, 6)] = new("VXE V3 PRO NK", AtkSensorKind.Paw3395Se),
        [(2, 8)] = new("VXE R1 NK", AtkSensorKind.Paw3395Se),
        [(2, 16)] = new("ATK X1 SE Nearlink", AtkSensorKind.Paw3395Se),
        [(2, 7)] = new("VXE V3 Plus", AtkSensorKind.Paw3395Ultra),
        [(2, 4)] = new("ATK A9 Plus NK", AtkSensorKind.Paw3395Ultra),
        [(2, 1)] = new("ATK A9 Pro Max 2.0 NK", AtkSensorKind.Paw3395Ultra),
        [(2, 27)] = new("ATK A9 Pro Max 2.0 NK", AtkSensorKind.Paw3950),
        [(2, 26)] = new("ATK A9 Plus NK", AtkSensorKind.Paw3950),
        [(1, 1)] = new("ATK ZERO", AtkSensorKind.Paw3950Ultra),
        [(1, 2)] = new("ATK A9 Ultimate 3.0", AtkSensorKind.Paw3950Ultra),
        [(1, 16)] = new("ATK A9 Mini", AtkSensorKind.Paw3950Ultra),
        [(1, 3)] = new("ATK Ghost Ultimate", AtkSensorKind.Paw3950Ultra),
        [(2, 13)] = new("ATK A9 Plus NK", AtkSensorKind.Paw3950Ultra),
        [(2, 14)] = new("ATK A9 Pro Max 2.0 NK", AtkSensorKind.Paw3950Ultra),
        [(2, 20)] = new("ATK A9 NB ULTRA", AtkSensorKind.Paw3950Ultra),
        [(1, 7)] = new("ATK A9 Ultra Max 2.0 +", AtkSensorKind.Paw3950Ultra),
        [(2, 25)] = new("ATK U2 Plus NK", AtkSensorKind.Paw3950Ultra),
        [(1, 19)] = new("ATK F1 V3 ULTRA MAX", AtkSensorKind.Paw3950Ultra),
        [(2, 17)] = new("VXE R1 PRO MAX 2.0 NK", AtkSensorKind.Paw3950Ultra),
        [(1, 22)] = new("ATK A9 Ultimate", AtkSensorKind.Paw3950Ultra),
        [(2, 34)] = new("ATK A9 Plus 3.0 NK", AtkSensorKind.Paw3950Ultra),
        [(2, 40)] = new("ATK A9 Plus 3.0 NK", AtkSensorKind.Paw3950Ultra),
        [(2, 35)] = new("ATK A9 Pro Max 3.0 NK", AtkSensorKind.Paw3950Ultra),
        [(2, 41)] = new("ATK A9 Pro Max 3.0 NK", AtkSensorKind.Paw3950Ultra),
        [(1, 26)] = new("ATK A9 Air", AtkSensorKind.Paw3950Ultra),
        [(1, 8)] = new("ATK ZERO +", AtkSensorKind.Paw3955Master),
        [(8, 1)] = new("ATK ZERO +", AtkSensorKind.Paw3955Master),
        [(1, 9)] = new("ATK A9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(1, 15)] = new("ATK A9 Ultimate +", AtkSensorKind.Paw3955Master),
        [(1, 6)] = new("ATK A9 Air +", AtkSensorKind.Paw3955Master),
        [(1, 10)] = new("ATK PHANTOM ULTIMATE", AtkSensorKind.Paw3955Master),
        [(1, 21)] = new("ATK PHANTOM ULTIMATE", AtkSensorKind.Paw3955Master),
        [(1, 12)] = new("ATK X1 ULTIMATE 2.0+", AtkSensorKind.Paw3955Master),
        [(1, 13)] = new("ATK X1 Ultra Max 2.0 + Tri-Mode", AtkSensorKind.Paw3955Master),
        [(1, 14)] = new("ATK X1 Air+", AtkSensorKind.Paw3955Master),
        [(1, 17)] = new("ATK F1 V3 ULTIMATE+", AtkSensorKind.Paw3955Master),
        [(1, 18)] = new("ATK F1 V3 ULTIMATE+", AtkSensorKind.Paw3955Master),
        [(1, 27)] = new("ATK F1 V3 ULTIMATE", AtkSensorKind.Paw3950Ultra),
        [(1, 28)] = new("ATK F1 V3 ULTIMATE+", AtkSensorKind.Paw3950Ultra),
        [(1, 4)] = new("ATK A9 Mini +", AtkSensorKind.Paw3955Master),
        [(1, 24)] = new("ATK ZERO + GENGAR EDITION", AtkSensorKind.Paw3955Master),
        [(2, 30)] = new("VXE V3 PRO NK", AtkSensorKind.Core26K),
        [(2, 29)] = new("ATK A9 3.0 NK", AtkSensorKind.Core26K),
        [(2, 38)] = new("ATK A9 3.0 NK", AtkSensorKind.Core26K),
        [(2, 33)] = new("ATK X1 SE NK", AtkSensorKind.Core26K),
        [(2, 22)] = new("VXE R1 SE+", AtkSensorKind.Core26K),
        [(2, 42)] = new("VXE R1 SE+", AtkSensorKind.Core26K),
        [(2, 15)] = new("VXE V3 PRO Max", AtkSensorKind.Core26K),
        [(2, 28)] = new("VXE R1 NK", AtkSensorKind.Core26K),
        [(2, 43)] = new("VXE R1 NK", AtkSensorKind.Core26K),
        [(2, 49)] = new("VXE V3 Pro Silent NK", AtkSensorKind.Core26K),
    };


    private static readonly ConcurrentDictionary<(ushort Vid, ushort Pid), string> ResolvedCache = new();
    private static readonly ConcurrentDictionary<(ushort Vid, ushort Pid), AtkSensorKind> ResolvedSensorCache = new();

    public static bool IsGenericReceiverName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        string trimmed = name.Trim();
        return trimmed.Equals("Mouse", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("Wireless mouse", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("dongle", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("receiver", StringComparison.OrdinalIgnoreCase)
               || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^ATK\s+[0-9A-Fa-f]{4}:[0-9A-Fa-f]{4}$");
    }

    public static void SeedPersistentReceiverModels(IReadOnlyDictionary<string, string>? persisted)
    {
        if (persisted == null) return;
        foreach (var kv in persisted)
        {
            if (IsGenericReceiverName(kv.Value)) continue;
            if (!kv.Key.StartsWith("atk:", StringComparison.OrdinalIgnoreCase)) continue;
            string vp = kv.Key[4..];
            var parts = vp.Split('-');
            if (parts.Length == 2
                && ushort.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ushort vid)
                && ushort.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ushort pid))
            {
                ResolvedCache[(vid, pid)] = kv.Value.Trim();
            }
        }
    }

    public static void TryRememberReceiverModel(ushort vid, ushort pid, string? modelName)
    {
        if (!IsGenericReceiverName(modelName))
            ResolvedCache[(vid, pid)] = modelName!.Trim();
    }

    public static string? ResolveModelName(
        ushort vid,
        ushort pid,
        (byte Cid, byte Mid)? cidMid = null,
        string? usbProduct = null,
        AtkProtocolFamily family = AtkProtocolFamily.Unknown)
    {
        if (cidMid is { } cm)
        {
            if (ByVidPidCidMid.TryGetValue((vid, pid, cm.Cid, cm.Mid), out var exact))
            {
                Remember(vid, pid, exact);
                return exact.Name;
            }
            if (TryResolveByCidMid(cm, family, out var byCm))
            {
                Remember(vid, pid, byCm);
                return byCm.Name;
            }
        }

        if (UniqueByVidPid.TryGetValue((vid, pid), out var byVp))
        {
            Remember(vid, pid, byVp);
            return byVp.Name;
        }

        if (ResolvedCache.TryGetValue((vid, pid), out var cached))
            return cached;

        string? fallback = CleanFallback(usbProduct);
        if (!IsGenericReceiverName(fallback))
            ResolvedCache[(vid, pid)] = fallback!;
        return fallback;
    }

    public static AtkSensorKind ResolveSensor(
        ushort vid,
        ushort pid,
        (byte Cid, byte Mid)? cidMid = null,
        AtkProtocolFamily family = AtkProtocolFamily.Unknown,
        string? modelName = null)
    {
        if (cidMid is { } cm)
        {
            if (ByVidPidCidMid.TryGetValue((vid, pid, cm.Cid, cm.Mid), out var exact) && exact.Sensor != AtkSensorKind.Unknown)
            {
                ResolvedSensorCache[(vid, pid)] = exact.Sensor;
                return exact.Sensor;
            }
            if (TryResolveByCidMid(cm, family, out var byCm) && byCm.Sensor != AtkSensorKind.Unknown)
            {
                ResolvedSensorCache[(vid, pid)] = byCm.Sensor;
                return byCm.Sensor;
            }
        }

        if (UniqueByVidPid.TryGetValue((vid, pid), out var byVp) && byVp.Sensor != AtkSensorKind.Unknown)
        {
            ResolvedSensorCache[(vid, pid)] = byVp.Sensor;
            return byVp.Sensor;
        }

        if (ResolvedSensorCache.TryGetValue((vid, pid), out var cachedSensor) && cachedSensor != AtkSensorKind.Unknown)
            return cachedSensor;

        string? lookupName = !IsGenericReceiverName(modelName)
            ? modelName
            : (ResolvedCache.TryGetValue((vid, pid), out var cachedName) ? cachedName : null);

        if (!string.IsNullOrWhiteSpace(lookupName))
        {
            var dict = family == AtkProtocolFamily.Bitmouse ? BitmouseByCidMid.Values : CompxByCidMid.Values;
            foreach (var spec in dict)
            {
                if (spec.Sensor != AtkSensorKind.Unknown && spec.Name.Equals(lookupName, StringComparison.OrdinalIgnoreCase))
                    return spec.Sensor;
            }
            foreach (var spec in UniqueByVidPid.Values)
            {
                if (spec.Sensor != AtkSensorKind.Unknown && spec.Name.Equals(lookupName, StringComparison.OrdinalIgnoreCase))
                    return spec.Sensor;
            }

            string upper = lookupName.ToUpperInvariant();
            if (upper.Contains("3955") || upper.Contains("A9 MINI +") || upper.Contains("SPRIGATITO") || upper.Contains("PIKACHU"))
                return AtkSensorKind.Paw3955Master;
            if (upper.Contains("3950 ULTRA") || upper.Contains("A9 MINI") || upper.Contains("F1 ULTIMATE") ||
                upper.Contains("F1 EXTREME") || upper.Contains("F1 LEVIATAN") || upper.Contains("X1 ULTIMATE") ||
                upper.Contains("Z1 PRO MAX") || upper.Contains("Z1 ULTRA"))
                return AtkSensorKind.Paw3950Ultra;
        }

        return AtkSensorKind.Unknown;
    }

    private static bool TryResolveByCidMid((byte Cid, byte Mid) cm, AtkProtocolFamily family, out ModelSpec spec)
    {
        if (family == AtkProtocolFamily.Bitmouse)
        {
            if (BitmouseByCidMid.TryGetValue(cm, out spec)) return true;
            if (CompxByCidMid.TryGetValue(cm, out spec)) return true;
            return false;
        }
        if (CompxByCidMid.TryGetValue(cm, out spec)) return true;
        if (family != AtkProtocolFamily.Compx && BitmouseByCidMid.TryGetValue(cm, out spec)) return true;
        spec = default;
        return false;
    }

    private static void Remember(ushort vid, ushort pid, ModelSpec spec)
    {
        if (!IsGenericReceiverName(spec.Name))
            ResolvedCache[(vid, pid)] = spec.Name;
        if (spec.Sensor != AtkSensorKind.Unknown)
            ResolvedSensorCache[(vid, pid)] = spec.Sensor;
    }

    private static string? CleanFallback(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (var suffix in new[] { " Dongle", " Receiver", " RECEIVER", " 8K RECEIVER", " NearLink Receiver" })
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                s = s[..^suffix.Length];
        return s.Trim();
    }
}
