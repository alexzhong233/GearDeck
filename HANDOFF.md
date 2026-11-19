# GearDeck 项目交接文档（给下一个 AI / 开发者）

> 更新时间：2026-11-19
> 本地项目路径：`D:\projects\GearDeck`
> GitHub 仓库：https://github.com/alexzhong233/GearDeck
> 状态：**已发布 v1.0.2（G HUB 风格分标签详情页 + PRO X3/狼蛛昆仑 L5 RT 调校、雷云四模式鼠标宏、15 套参考项目兼容提供器、官方桌面/网页驱动启动与共存守卫、全线上产品图库、GitHub Actions CI/安装包/便携包/升级包自动化发布）**

## 一、项目结构与产物

- **解决方案**：`GearDeck.slnx` · **WinUI 主程序**：`src/GearDeck.App/GearDeck.App.csproj` · **任务栏贴片**：`src/GearDeck.Widget/GearDeck.Widget.csproj` · **集中版本控制**：`Directory.Build.props` (`1.0.2`)
- **CI / 自动发布工作流**：[ci-release.yml](file:///d:/projects/GearDeck/.github/workflows/ci-release.yml)（基于 Windows runner + MSBuild + Inno Setup 6 自动生成并上传/发布安装包 `.exe`、绿色解压 `.zip`、增量补丁 `patch.zip` 与 `latest.json`）
- **发布打包与安装包脚本**：`scripts/package-release.ps1`、`scripts/installer.iss`
- **硬件协议栈 (`src/GearDeck.Core`)**：
  - `LogitechProvider`：HID++ `0x1004/0x1000`、`0x2202/0x2201`、`0x8061/0x8060`、`0x8100` Host/Onboard 切换、**`0x1B0C` HITS 电磁微动触发点 (`1..10`) / Rapid Trigger (`1..5`) / 点击触感 (`0..5`)** 与 `046D:C54F` (`PRO X3 SUPERSTRIKE`) 休眠保留。
  - `AulaFfa8Protocol` & `AulaProvider`：狼蛛 `0xFFA8` (`0x81`, `0xA0`, `0x85/0x86`, `0x9A/0x9B`, **`0xA1/0xA2` SSHS 2.0 电磁微动与 Rapid Trigger `2=ON/1=OFF`**)，覆盖 `昆仑 L5 Ultra (38A6:2F1A/2F0C)` 等全系机型。
  - `RazerProvider`：DPI/多档位、125–8000 Hz、`0x0B/0x85` + `0x0B/0x8B`（含缓存校准解码与三挡/非对称 LOD）、休眠、低电量阈值、`0x02/0x8C`/`0x02/0x0C`（`tid=0x10`）原生改键。
  - `AtkModelCatalog`、`Compx08Protocol`、`BitmouseProtocol` & `AtkProvider`：集成 192 个 VID:PID 与 166 个 CID/MID 模型映射，优先命中 `UsagePage == 0xFF02` 接口并支持 `0x10` CID/MID 查询。
  - `CompXProvider` & `MchoseProvider`：65B CompX（万灵 WLmouse、兰族 Lamzu、Endgame Gear、Pulsar、Finalmouse、Keychron、Glorious、Rapoo 等）与迈从 V2/V3 协议（含 `3837`）。
  - `StubProviders` (`RogProvider` & `AngryMiaoProvider`)：ROG 游侠 2 RX 98 无线 (`1B7A`/`1B78`)、魔导士 Ace HFX 75 (`1C7D`)、`0B05:1ACE` Omni 休眠保留与 ROG Delta II (`1AFA`)；Angry Miao AM Infinity `.97` (`0E8D:0703`) 识别（官方加密协议已提示走官方 AM Master / 网页驱动）。
  - `SonyInzoneProvider`：Sony INZONE Buds (`054C:0EC2`) 独立上报左耳 (`LeftEarPercent/Charging`)、右耳 (`RightEarPercent/Charging`) 与充电盒电量，并在任务栏贴片同格显示 `Lxx% Ryy%`。
  - `FlydigiProvider`：飞智八爪鱼 5/6 Pro / CD2 充电底座 (`37D7:6001`)，`Apex 5` 力反馈扳机采用左右手独立双包 (`side=1` & `side=2`, `apply=1`) 与匹配空间站的 `Sniper(mode=3)/Recoil(mode=2)` 映射。
  - 新增参考项目品牌提供器：`CorsairProvider`（Bragi/Legacy）、`SteelSeriesProvider`、`HyperXProvider`、`GamepadBatteryProvider`（Xbox GIP / DualSense / Switch Pro）、`BluetoothBatteryProvider`（PnP/HID Bluetooth 通用电量）。
- **核心 UI 与系统服务 (`src/GearDeck.App`)**：
  - `VendorDriverCatalog` & `VendorDriverLauncher` + `ExternalConfiguratorGuard`：官方本地驱动（G HUB / ROG 奥创中心 / 雷云 Synapse / Sony INZONE Hub / 飞智空间站 / AM Master）与网页驱动（雷蛇 / ATK / 万灵 / 怒喵 / 狼蛛 / 兰族 / ROG Gear Link 等）一键直达，启动自动暂停后台 HID 轮询 180 秒并在官方窗口处于前台时智能退让。
  - `DeviceDetailPage`：仿 Logitech G HUB 左右两栏 Hero + 顶部五标签（`概览 / 性能 / 按键 & RT / 高级 / 配置文件`）布局，集成 `AnalogSwitchCard`、`LodSlider`、合并排队防丢下发 (`_queuedUpdate`) 与 180ms 滑块防抖实时写入。
  - `GamesPage` & `GameProfileAutoService`：`全局独占策略总开关` 同时禁用/启用卡片 1 与卡片 2；卡片 2 默认 `"回退开启前的模式（默认）"`，进入前自动抓拍外设状态快照并在退出时回滚。
  - `MacroModels`、`MacroExecutionService` & `MacrosPage`：雷云风格四种触发模式（单次触发 / 按住触发 / 切换触发 + Esc 紧急停止 / 指定次数），集成 `RAWINPUTDEVICE` 实体鼠标 VID:PID 识别（移除休眠过滤）、ScanCode `SendInput` 硬件扫描码、严格防卡键清理与管理员 UIPI 重启引导。
  - `DeviceImageService` & `Assets/device-images.json (v3)`：已完全移除手绘 SVG 剪影代码，首次升级自动清理旧版 `preset-gallery/*.svg`，全量采用已校验 HTTP 200 的官方 CDN 与 `libratbag/piper` 线上产品图并行拉取缓存。

## 二、验证基线（2026-11-19）

- **单元测试**：`dotnet test tests\GearDeck.Tests\GearDeck.Tests.csproj -c Release` — **86/86 全部通过**（0 失败，0 跳过；含 `V102UpgradeTests`、`XamlSafetyTests`、`GameProfileMatcherTests`、`NewFeaturesAndDevicesTests`、`OpenMouseProtocolTests`、`ProtocolTests`、`HardwareProtocolTests`、`WidgetLayoutTests`、`UpdateClientTests`）。
- **双配置构建**：`MSBuild.exe` 编译 `GearDeck.Widget.csproj` 与 `GearDeck.App.csproj`（`Debug|x64` 与 `Release|x64`）均 **0 错误**。
- **发布归档**：`scripts/package-release.ps1 -Version 1.0.2` 在本机生成便携包 `GearDeck-v1.0.2-win-x64.zip`、补丁包 `GearDeck-patch-v1.0.2-win-x64.zip` 与 `latest.json`（`.exe` 安装包由 GitHub Actions Windows runner 自动调用 Inno Setup 6 编译发布）。
- **实机验证**：启动 `Release|x64` `GearDeck.App.exe` 与 `GearDeck.Widget.exe` 均正常运行 (`Responding=True`，无 `crash.log`)，线上设备图已并发下载并写入 `%LOCALAPPDATA%\GearDeck\settings.json` (`AutoCloudDeviceImages`)。

## 三、约束备忘

- 禁止写 `HKCU\...\StuckRects3` 或重启 `explorer.exe`；禁止带 `--preview` 启动 `GearDeck.Widget.exe`。
- 禁止伪造休眠无线设备或入盒耳机的虚假电量百分比；Razer 按键重映射写入始终使用 `tid = 0x10`。
- XAML 中任意 `<Slider>` 或 `<ProgressBar>` 必须保持 `Maximum` 写在 `Minimum` 之前且 `Minimum <= 1.0`。
