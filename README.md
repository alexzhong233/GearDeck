# GearDeck · 跨平台外设控制与监控中心

基于 **C# / .NET 8 + Avalonia 11 + FluentAvaloniaUI** 构建的 **Windows & macOS** 轻量外设控制中心：Windows 11 Mica / macOS AcrylicBlur 毛玻璃质感、多品牌原生 HID 参数调校、按游戏与前台窗口自动切配置、19 款 FPS 灵敏度换算、系统监控与 Windows 任务栏贴片。

- **GitHub 仓库**：https://github.com/alexzhong233/GearDeck
- **下载安装（Releases）**：https://github.com/alexzhong233/GearDeck/releases

---

## 快速安装与使用

### Windows（x64 · 免装 .NET 运行时）
1. 前往 [Releases](https://github.com/alexzhong233/GearDeck/releases) 下载：
   - **安装版**：`GearDeck-Setup-vX.Y.Z-win-x64.exe`（支持从旧版直接覆盖升级）
   - **绿色版**：`GearDeck-vX.Y.Z-win-x64.zip`（解压后运行 `GearDeck.Desktop.exe`）
2. 程序会自动兼容并继承本地既有 `settings.json` 与历史电量缓存，Windows 端可开启桌面左下/右下嵌入式或悬浮式任务栏贴片（`GearDeck.Widget.exe`）。

### macOS（Apple Silicon `osx-arm64` / Intel `osx-x64` · macOS 12+）
1. 前往 [Releases](https://github.com/alexzhong233/GearDeck/releases) 下载对应芯片架构的 `.dmg` 或 `.zip` 并将 `GearDeck.app` 拖入 `/Applications`。
2. **首次启动绕过 Gatekeeper 未公证提示**（如系统提示“已损坏”或“无法验证开发者”）：
   ```bash
   xattr -cr /Applications/GearDeck.app
   ```
3. **权限授予**：如需读写 USB/蓝牙键鼠 HID 参数或自动识别前台窗口，请在「系统设置 → 隐私与安全性」中按需勾选 **输入监控（Input Monitoring）** 与 **辅助功能（Accessibility）**。

---

## 核心功能

- **多品牌外设原生协议接管**
  - **鼠标调参**：多档 DPI / XY 独立灵敏度、125–8000 Hz 回报率、性能/竞技模式、MotionSync / 直线修正 / 波纹控制 / 移动唤醒、LOD 与非对称抬起高度、角度校准、防抖延迟、休眠时间、板载配置槽位与物理按键重映射。
  - **键盘 / 耳机 / 手柄**：磁轴/微动触发行程与 Rapid Trigger 调节、Sony INZONE Buds 双耳与充电盒三段电量、飞智八爪鱼力反馈扳机预设、ROG/LEOBOG 机械键盘调参，以及手柄与蓝牙通用电量监控。
  - **真实电量守恒**：仅展示或回退保存曾经真实观测过的电量（`LastKnownBatteries`），绝不凭空估算或伪造有线模式未上报表显电量。
  - **可配置轮询频率**：设置页支持直接调节 **HID 设备轮询间隔**（默认 `15s`，可调 `5–300s`）与 **前台游戏监测间隔**（默认 `1000ms`，可调 `250–5000ms`），保存即时热更新。
- **跨游戏灵敏度换算（19 款热门 FPS）**
  - 支持 `cm/360°`、FOV 比例缩放与 `MDH 100%` 三种换算方式，覆盖 CS2、Valorant、Apex、Overwatch 2、PUBG、BF6/2042、COD、Delta Force、R6S、Marvel Rivals、Escape from Tarkov 等。
  - 精确支持自定义 DPI、自定义分辨率与 `16:9` 拉伸缩放修正。
- **游戏与前台应用自动切配置**
  - 自动扫描本地游戏库（Windows 支持 Steam / Epic / Riot / WeGame / HoYoPlay / Battle.net / Xbox / EA / Ubisoft / GOG 等；macOS 支持 `/Applications` 与 Steam 库），并支持一键导入当前前台应用。
  - 支持休眠设备唤醒自动补齐配置、窗口退出再恢复桌面默认预设，以及原生系统切换通知。
- **鼠标宏中心与系统监控**
  - 统一管理多步动作宏（按键按下/抬起、鼠标点击、延时、JSON 导入/导出与单步微调）。
  - 实时采集 **12 项系统监控指标**（CPU 占用/频率、内存、GPU 占用/温度/显存、上传/下载网速、磁盘活动/可用空间、开机时长），支持独立透明置顶桌面悬浮监控条与 Windows 任务栏贴片双指标展示。
- **在线更新与 GitHub 镜像加速**
  - 内置 GitHub Releases 检查更新、自定义镜像站前缀或 `{url}` / `{url_encoded}` 模板、强制 SHA-256 完整性校验与异常自动回退直连。

---

## 双端能力对比

| 功能模块 | Windows 10/11 | macOS 12+ |
| --- | --- | --- |
| 外设 HID 发现与调参（17 个 Provider） | ✅ 完整支持 | ✅ IOHID + `ioreg` 蓝牙电量 |
| 19 款 FPS 灵敏度换算 & 诊断日志 | ✅ 完整支持 | ✅ 完整支持 |
| 前台应用 / 游戏识别自动切配置 | ✅ Win32 + 全屏独占判定 | ✅ `NSWorkspace` 原生进程检测 + `/Applications` & Steam 扫描 |
| 状态栏驻留 & 实时电量展示 | ✅ Win32 托盘 + 独立任务栏贴片 | ✅ 原生菜单栏平铺 / 分组图标 + 系统监控摘要 |
| 系统监控面板 & 独立透明悬浮条 | ✅ 12 项全支持 | ✅ CPU/内存/GPU/网络/磁盘/开机时长 |
| 开机自启 | ✅ HKCU Run（带唤醒状态恢复） | ✅ `~/Library/LaunchAgents` plist |
| 全局软件宏回放触发 | ✅ `SetWindowsHookEx` + `SendInput` | ⚠️ 支持宏编辑管理，钩子受辅助功能边界限制 |

---

## 已接入品牌与协议

| 品牌 / 品类 | 协议与支持特性 | 状态 |
| --- | --- | --- |
| 雷蛇 Razer | 91B Feature（class `07`/`02`，按键重映射严格 `tid=0x10`、非对称 LOD、低电阈值） | ✅ 已验证 |
| 罗技 Logitech | HID++ 2.0（`0x1004/0x1000`、`0x2202`、`0x8061`、`0x8100`、HITS 电磁微动） | ✅ 已实现 |
| 万灵 WLmouse / 兰族 Lamzu 等 | CompX 65B Page/Command + Turbo 20K 模式与按键映射 | ✅ 已验证 |
| ATK / VXE / VGN | Compx `0x08` + BITMOUSE 64B `0x72` + VIA 键盘（双模接收器型号持久化） | ✅ 已验证 |
| 迈从 MCHOSE | V2 (`^0xFF`) 与 V3 (`0x4D`) | ✅ 已实现 |
| 怒喵 Angry Miao | Airoha RACE (`0E8D:0703` DPI/回报率/`0.7–2.0mm` LOD/性能开关) | ✅ 已验证 |
| 狼蛛 AULA / Incott | `0xFFA8` SSHS 2.0 磁轴/微动与 `0x09` 320B 硬件宏 | ✅ 已验证 |
| 名龙星球 MLOONG | `MX301 PRO / PRO V2 / MX300 / MX301 / MX302`（原生 64B 协议）与 `MX301 PRO X`（Compx `0x08`） | ✅ 已验证 |
| LEOBOG | `Hi98C Pro` 四页签全功能控制台：无线 `0x41` 键位查看 + `0x83/0x03/0x04/0x06` 有线安全改键与单键 RGB / 备份恢复 | ✅ 已验证 |
| ROG / 索尼 Sony | ROG Aura/OMNI + Sony INZONE Buds（左耳/右耳/充电盒三段电量） | ✅ 已验证 |
| 飞智 Flydigi | 八爪鱼 5 / 6 Pro / 黑武士 5 Pro / CD2 底座（含力反馈扳机预设） | ✅ 已验证 |
| 海盗船 / 赛睿 / HyperX | Corsair Bragi & Legacy / SteelSeries / HyperX 电池与基础配置 | ✅ 已实现 |
| 手柄与蓝牙通用设备 | Windows XInput / DualSense / Switch Pro + Win32 SetupAPI & macOS `ioreg` 电量 | ✅ 已实现 |

---

## 本地构建与验证

确保已安装 **.NET 8 SDK**（Windows 端如需构建留存的 WinUI 3 工程与安装包，另需 VS 2022+ Build Tools 与 Inno Setup 6）：

```powershell
# 1. 运行全量单元测试（Windows / macOS 通用）
dotnet test tests/GearDeck.Tests/GearDeck.Tests.csproj -c Release

# 2. 构建跨平台主程序（Windows 构建时会自动连带复制 GearDeck.Widget）
dotnet build src/GearDeck.Desktop/GearDeck.Desktop.csproj -c Release

# 3. 发布自包含单目录成品
dotnet publish src/GearDeck.Desktop/GearDeck.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
dotnet publish src/GearDeck.Desktop/GearDeck.Desktop.csproj -c Release -r osx-arm64 --self-contained true -o publish/osx-arm64
```

Windows 快速启动：在项目根目录运行 [`启动 GearDeck.cmd`](%E5%90%AF%E5%8A%A8%20GearDeck.cmd)。

---

## 目录结构

```
.github/workflows/     GitHub Actions 双端自动编译、测试、打包与 Release 工作流
scripts/               Windows 打包（package-release.ps1）与 Inno Setup 安装脚本（installer.iss）
src/GearDeck.Core      核心模型、HID 安全读写通道、17 个硬件 Provider、灵敏度与更新客户端
src/GearDeck.Desktop   Avalonia 11 + FluentAvaloniaUI 跨平台主程序（Windows + macOS）
src/GearDeck.Widget    Windows 独立轻量任务栏分组电量与系统监控贴片
src/GearDeck.App       留存的初代 WinUI 3 主程序（兼容维持可编译）
src/GearDeck.Monitor   留存的初代 WinUI 3 独立监控窗（兼容维持可编译）
tests/GearDeck.Tests   硬件协议、灵敏度换算、规则兼容与 XAML/AXAML 安全红线单元测试
```
