# GearDeck 项目交接文档（给下一个 AI / 开发者）

> 更新时间：2026-11-20
> 本地项目路径：`D:\projects\GearDeck`
> GitHub 仓库：https://github.com/alexzhong233/GearDeck
> 状态：**已发布 v1.0.3（怒喵 RACE 原生调校、ATK A9 Mini 全链路识别与 PAW3950Ultra/3955MASTER DPI 修正、左侧硬件状态 + 右上独立配置文件快切栏、实时 RT 按压力度条、0.1mm LOD 滑块、ROG UWP 奥创启动与飞智网页空间站、双耳两行贴片与历史电量回退、任务栏防闪烁防卡死）**

## 一、项目结构与产物

- **解决方案**：`GearDeck.slnx` · **WinUI 主程序**：`src/GearDeck.App/GearDeck.App.csproj` · **任务栏贴片**：`src/GearDeck.Widget/GearDeck.Widget.csproj` · **集中版本控制**：`Directory.Build.props` (`1.0.3`)
- **CI / 自动发布工作流**：[ci-release.yml](file:///d:/projects/GearDeck/.github/workflows/ci-release.yml)（基于 Windows runner + MSBuild + Inno Setup 6 自动生成并上传/发布安装包 `.exe`、绿色解压 `.zip`、增量补丁 `patch.zip` 与 `latest.json`）
- **发布打包与安装包脚本**：`scripts/package-release.ps1`、`scripts/installer.iss`
- **硬件协议栈 (`src/GearDeck.Core`)**：
  - `AngryMiaoRaceProtocol` & `AngryMiaoProvider`：怒喵 `AM Infinity .97` (`0E8D:0703`) 完整 Airoha RACE 协议电量、DPI 档位、回报率、LOD (`0.7..2.0 mm`)、防抖、Motion Sync / 直线修正 / 波纹控制读写，官方网页驱动更新为 `https://ammaster.angrymiao.com/mouse/devices`，产品图切换为单鼠标高清图 (`device-images.json` v4)。
  - `AtkModelCatalog`、`Compx08Protocol`、`BitmouseProtocol` & `AtkProvider`：对齐 `ATK HUB 3.2.33` 拆分 CompX (176 项) 与 Bitmouse (63 项) 独立 `(Cid, Mid)` 模型及传感器表（覆盖 `ATK A9 Mini` / `A9 Mini +` / `PAW3950Ultra` / `PAW3955MASTER`），支持 49B `0xFF04`/`0xFF05` 通道与休眠接收器配对 CID/MID 查询 (`0x01` / `137`)，修正 CompX EEPROM `[2]=StageCount` 与 `[4]=ActiveStageIndex` 偏移，新增 `PAW3955MASTER` `0x1B00` 6 字节 DPI 档位、`0.7..1.7 mm` `0.1 mm` 步进 LOD 与 `KnownReceiverModels` 跨会话记忆。
  - `LogitechProvider`：HID++ `0x1004/0x1000`、`0x2202/0x2201`、`0x8061/0x8060`、`0x8100` Host/Onboard 切换、`0x1B0C` HITS 电磁微动触发点 (`1..10`) / Rapid Trigger (`1..5`) / 点击触感 (`0..5`) 与 `046D:C54F` (`PRO X3 SUPERSTRIKE`) 休眠保留。
  - `AulaFfa8Protocol` & `AulaProvider`：狼蛛 `0xFFA8` (`0x81`, `0xA0`, `0x85/0x86`, `0x9A/0x9B`, `0xA1/0xA2` SSHS 2.0 电磁微动与 Rapid Trigger `2=ON/1=OFF`)，覆盖 `昆仑 L5 Ultra (38A6:2F1A/2F0C)` 等机型。
  - `RazerProvider`：DPI/多档位、125–8000 Hz、`0x0B/0x85` + `0x0B/0x8B`（`1.0/1.5/2.0 mm` 与 `0.1 mm` 步进非对称抬起/着陆高度）、休眠、低电量阈值、`0x02/0x8C`/`0x02/0x0C`（`tid=0x10`）原生改键。
  - `CompXProvider` & `MchoseProvider`：65B CompX（万灵 WLmouse、兰族 Lamzu、Endgame Gear、Pulsar、Finalmouse、Keychron、Glorious、Rapoo 等）与迈从 V2/V3 协议（含 `3837`）。
  - `StubProviders` (`RogProvider`)：ROG 游侠 2 RX 98 无线 (`1B7A`/`1B78`)、魔导士 Ace HFX 75 (`1C7D`)、`0B05:1ACE` Omni 休眠保留与 ROG Delta II (`1AFA`)。
  - `SonyInzoneProvider`：Sony INZONE Buds (`054C:0EC2`) 独立上报左耳 (`LeftEarPercent/Charging`)、右耳 (`RightEarPercent/Charging`) 与充电盒电量。
  - `FlydigiProvider`：飞智八爪鱼 5/6 Pro / CD2 充电底座 (`37D7:6001`)，`Apex 5` 力反馈扳机采用左右手独立双包 (`side=1` & `side=2`, `apply=1`) 并在 `LastTriggerModeByDevice` 中同步界面当前选中态。
  - 其他兼容提供器：`CorsairProvider`（Bragi/Legacy）、`SteelSeriesProvider`、`HyperXProvider`、`GamepadBatteryProvider`（Xbox GIP / DualSense / Switch Pro）、`BluetoothBatteryProvider`（PnP/HID Bluetooth 通用电量）。
- **核心 UI 与系统服务 (`src/GearDeck.App` & `src/GearDeck.Widget`)**：
  - `VendorDriverCatalog` & `VendorDriverLauncher` + `ExternalConfiguratorGuard`：ROG 奥创中心通过 UWP `shell:AppsFolder\B9ECED6F.ArmouryCrate_qmba6cd70vzyy!App` 启动并过滤后台服务进程；飞智补充网页空间站 `https://space_station.flydigi.com/`；启动外部软件/网页时自动暂停后台轮询 180 秒并在前台焦点位于官方软件时自动退让。
  - `DeviceDetailPage`：左侧常驻 Hero 卡片 + `硬件状态概览`，右侧顶部独立 `TopProfileBarCard`（板载槽位快切 + Host/Onboard 开关 + 本地快速预设），下方三标签（`性能` 默认 / `按键 & RT` / `高级`）；移除无用的「应用 DPI 到设备」按钮；所有 LOD 与非对称高度滑块均采用 `0.1 mm` 精度与 `"{0:0.0} mm"` 浮窗提示；`AnalogSwitchCard` 增加 `25ms` 实时按压力度与 RT 动态重置可视化条及屏蔽右键菜单的测试垫。
  - 低电量提醒策略重组：删除详情页单设备提醒卡片与设置页全局阈值输入框（固定全局默认阈值 `20%`），设置页保留 `全局设备低电量通知提醒` 开关，单设备独立阈值与提醒卡片移至 `设备总览` 页。
  - 任务栏贴片与分组弹窗 (`WidgetProcess`, `BatteryHistoryHelper`, `WidgetGroupWindow`, `GearDeck.Widget`)：分体双耳耳机（如 `Sony INZONE Buds`）在任务栏上下两行显示 `L xx%` / `R yy%⚡`；已观测过真实电量的设备在休眠时回退显示 `LastKnownBatteries` 历史值（从未读到电量的设备仍显示 `--`）；分组菜单右上角带设置齿轮按钮一键跳转贴片设置；移除 `EVENT_OBJECT_REORDER` 风暴钩子、采用无激活 `AppWindow.Show(false)`、状态去重写盘与节流 Z 序检测，消除任务栏闪烁、点击秒退与卡死。

## 二、验证基线（2026-11-20）

- **单元测试**：`dotnet test tests\GearDeck.Tests\GearDeck.Tests.csproj -c Release` — **91/91 全部通过**（0 失败，0 跳过；含 `V102UpgradeTests`、`XamlSafetyTests`、`GameProfileMatcherTests`、`NewFeaturesAndDevicesTests`、`OpenMouseProtocolTests`、`ProtocolTests`、`HardwareProtocolTests`、`WidgetLayoutTests`、`UpdateClientTests`）。
- **构建与贴片自检**：`MSBuild.exe` 编译 `GearDeck.Widget.csproj`（`Debug|x64`、`Release|x64`）与 `GearDeck.App.csproj`（`Debug|x64`、`Release|x64`）0 编译错误；`GearDeck.Widget.exe --render-test`（含双行上下叠排像素断言）与 `--window-test` 均返回退出码 `0`。
- **发布归档**：`scripts/package-release.ps1 -Version 1.0.3` 生成便携包 `GearDeck-v1.0.3-win-x64.zip`、补丁包 `GearDeck-patch-v1.0.3-win-x64.zip` 与 `latest.json`（`.exe` 安装包由 GitHub Actions Windows runner 自动调用 Inno Setup 6 编译发布）。

## 三、约束备忘

- 禁止写 `HKCU\...\StuckRects3` 或重启 `explorer.exe`；禁止带 `--preview` 启动 `GearDeck.Widget.exe`。
- 禁止伪造从未观测过电量的设备百分比（历史回退仅限 `LastKnownBatteries` 真实采集记录）；Razer 按键重映射写入始终使用 `tid = 0x10`。
- XAML 中任意 `<Slider>` 或 `<ProgressBar>` 必须保持 `Maximum` 写在 `Minimum` 之前且 `Minimum <= 1.0`。
