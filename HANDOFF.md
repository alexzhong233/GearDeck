# GearDeck 项目交接文档（给下一个 AI / 开发者）

> 更新时间：2026-11-19
> 本地项目路径：`D:\projects\GearDeck`
> GitHub 仓库：https://github.com/alexzhong233/GearDeck
> 状态：**已全量重命名为 `GearDeck`（工程/命名空间/进程/UI/根目录/启动脚本/%LOCALAPPDATA%\GearDeck 数据目录）、完成自定义 GitHub 镜像在线检测与热更新重启能力、并已推送至 GitHub 发布 v1.0.0 与 v1.0.1**

## 一、项目结构与产物

- **解决方案**：`GearDeck.slnx` · **快捷启动**：`启动 GearDeck.cmd` · **集中版本控制**：`Directory.Build.props`
- **发布打包脚本**：`scripts/package-release.ps1`（生成完整绿色包 `GearDeck-vX.Y.Z-win-x64.zip`、增量补丁包 `GearDeck-patch-vX.Y.Z-win-x64.zip` 与清单 `latest.json`）
- `src/GearDeck.Core`：
  - `Hid/HidSafe.cs` & `Hid/HidChannel.cs`：隔离虚拟蓝牙/传感器 HID 节点（`8087:0A1E`、`5E2A:71B4`）的 `Failed to get info.` `DeviceIOException`。
  - **多品牌原生硬件协议栈**：
    - `RazerProvider`：DPI/多档位、125–8000 Hz、`0x0B/0x85` 对称/非对称 LOD、休眠、`0x07/0x81` 低电量阈值、`0x02/0x8C`/`0x02/0x0C`（`tid=0x10`）原生按键重映射与组合键写入。
    - `LogitechProvider`：HID++ `0x1004/0x1000`、`0x2202/0x2201`、`0x8061/0x8060`、`0x8100` Host/Onboard 模式与板载配置槽位读取与切换。
    - `CompXPageProtocol` & `CompXProvider`：65B CompX 协议，覆盖万灵 WLmouse (`0x36A7`) 与兰族 Lamzu (`0x37B0` / `0x373E` / `0x3554`)。
    - `Compx08Protocol`、`BitmouseProtocol` & `AtkProvider`：ATK/VXE/VGN (`0x373B` / `0x34D7`) 与 VIA 键盘电量。
    - `MchoseProvider`：迈从全系 V2 / V3 协议与按键重映射。
    - `GearHubMuProtocol`、`Incott09Protocol`、`AulaProvider` & `AngryMiaoProvider`：怒喵 AM Infinity `.97` (`0E8D:0703` / `0x3151`)、狼蛛 `AULA SC 无线鼠标` (`38A6:2F1A` / `25A7` / `258A:0138`) 与 Incott `0x09` 320B 10 分块硬件宏烧录。
    - `SonyInzoneProvider`：Sony INZONE Buds (`054C:0EC2`) 64B 主动查询左耳 (`0x0E`) / 右耳 (`0x10`) / 充电盒 (`0x12`) 电量及入盒 (`0xFF`) 状态。
    - `FlydigiProvider`：飞智八爪鱼 5 (`37D7:2501`) / 6 Pro (`37D7:2502`) / 黑武士 5 Pro / CD2 充电底座 (`37D7:6001`) 与 `DeviceKind.Controller`。
  - `Services/GameLibraryParser.cs`、`Services/DeviceImageRuleMatcher.cs`、`Models/GameProfileRuleMatcher.cs`：Steam/Epic 清单解析 + `OpenMouseGameCatalog` 过滤匹配、外置 `device-images.json` 分层匹配、`ApplyExitRetention` 全屏保持判定。
  - `Services/UpdateClient.cs` & `GitHubMirrorHelper`：支持自定义 GitHub 镜像站（前缀、`{url}`、`{url_encoded}`）或直连 GitHub；优先拉取 `releases/latest/download/latest.json`，自动兼容 GitHub REST API 与 `raw.githubusercontent.com`，镜像故障可自动回退。
- `src/GearDeck.Widget`：WinForms 分层透明任务栏贴片进程；接入逐设备独立低电量阈值与双模式唤起 (`Local\GearDeck.Activate`)。
- `src/GearDeck.App`：
  - `MainWindow.xaml(.cs)` & `App.xaml(.cs)`：WinUI 3 Gallery 风格 `TitleBarHeightOption.Tall` Mica 标题栏（含版本号显示、Settings 更新 `InfoBadge` 徽标、自动唤醒单实例互斥锁释放、以及用于端到端自测的 `--self-update-test` / `--mirror=` 参数）。
  - `Services/AppUpdateService.cs` & `Pages/SettingsPage.xaml(.cs)`：支持一键检查更新、自定义镜像站（含 `ghfast.top` / `gh-proxy.com` 快捷填充）、SHA-256 与 ZIP 头校验、自动选用增量补丁或完整包、`%LOCALAPPDATA%\GearDeck\updates\apply-update.ps1` 后台热替换文件并自动重启。
  - `Pages/GamesPage.xaml(.cs)`、`DeviceDetailPage.xaml(.cs)`、`MacrosPage.xaml(.cs)`：本机游戏库折叠面板、全屏保持策略、设备板载预设、全局宏中心。
- 启动入口：`D:\projects\GearDeck\启动 GearDeck.cmd`（或 `src\GearDeck.App\bin\x64\Debug\net8.0-windows10.0.22621.0\GearDeck.App.exe`）。

## 二、GitHub 发布与实机自更新验证结论（2026-11-19）

- **GitHub 仓库**：https://github.com/alexzhong233/GearDeck
  - Release `v1.0.0`：https://github.com/alexzhong233/GearDeck/releases/tag/v1.0.0（含 `GearDeck-v1.0.0-win-x64.zip`、`GearDeck-patch-v1.0.0-win-x64.zip`、`latest.json`）
  - Release `v1.0.1`：https://github.com/alexzhong233/GearDeck/releases/tag/v1.0.1（含 `GearDeck-v1.0.1-win-x64.zip`、`GearDeck-patch-v1.0.1-win-x64.zip`、`latest.json`）
- **单元测试**：`76/76` 全部通过（含 `UpdateClientTests` 自定义镜像 URL 重写、多段版本对比、清单与 GitHub API 解析、镜像故障回退及 `XamlSafetyTests`）。
- **自定义镜像站与实机热更新验证**：
  - 直连、`https://ghfast.top/` 与 `https://gh-proxy.com/`（强制禁用回退）均实测检测到 `v1.0.1`。
  - 独立 `v1.0.0`（`FileVersion=1.0.0.0`）实例带 `--mirror=https://ghfast.top/` 在线拉取 `1437 KB` `v1.0.1` 补丁包（耗时 0.64 秒）、完成 SHA-256 校验与解压，通过 `apply-update.ps1` 热替换并自动重启为 `1.0.1.0`（`GearDeck.App` & `GearDeck.Widget` 均 `Responding = True`，`crash.log` 不存在）。

## 三、约束备忘

- 禁止写 `HKCU\...\StuckRects3` 或重启 `explorer.exe`；禁止带 `--preview` 启动 `GearDeck.Widget.exe`。
- 禁止伪造休眠无线设备或入盒耳机的虚假电量百分比；Razer 按键重映射写入始终使用 `tid = 0x10`。
- XAML 中任意 `<Slider>` 或 `<ProgressBar>` 必须保持 `Maximum` 写在 `Minimum` 之前。
