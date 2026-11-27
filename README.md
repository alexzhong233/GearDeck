# GearDeck · 跨平台外设控制与监控中心

基于 **C# / .NET 8 + Avalonia 11 + FluentAvaloniaUI** 构建的 **Windows & macOS** 轻量外设控制中心：Windows 11 Mica / macOS AcrylicBlur 毛玻璃质感、22 个原生 HID Provider 参数调校、按游戏与前台窗口自动切配置、19 款 FPS 灵敏度换算、鼠标宏中心（软件宏 + 部分设备板载硬件宏）、系统监控与纯 Win32 + SkiaSharp 超轻量任务栏贴片。

- **GitHub 仓库**：https://github.com/alexzhong233/GearDeck
- **下载安装（Releases）**：https://github.com/alexzhong233/GearDeck/releases

---

## v2.2.0 核心更新亮点

- **开源协议生态扩充（22 个 Provider）**：
  - 新增 **Endgame Gear**（EggWe / OP1we / XM2w）、**Ninjutso**（Sora 4K/V2）、**Finalmouse**（ULX）、**Rapoo 雷柏**、**Audeze Maxwell**、**Sony PULSE 3D**、**SteelSeries Arctis Nova 5/5X & 7+** 等开源协议解析。
  - 新增 **Sinowealth（中颖 8051）** 鼠标、**Logitech G600 MMO** 与 **Redragon M908/M711** 原生调参及板载配置支持。
- **软件宏中心 & 板载硬件宏矩阵**：
  - 宏中心升级为列表与隔离草稿二级编辑器，支持 Windows 全局低级键鼠钩子录制、**滚轮上下（`0xF5`/`0xF6`）动作录制回放**、独立动作间隔行编排与总开关真实卸载后台钩子。
  - 宏中心常驻软件宏反作弊风险提示与一键全局关闭指引；设备页严格区分 **「绑定软件宏」** 与 **「写入硬件宏」**，写入硬件宏前弹窗提示 Flash/EEPROM 风险并建议优先使用官方驱动。
  - 基于开源协议新增 **VIA/QMK/Vial（含 Ploopy 等）** 动态宏、**罗技 HID++ 2.0 `0x8100` 经典布局（带严格 CRC-CCITT 校验）**、**Sinowealth `0x30`** 与 **Redragon `0xF3`** 板载硬件宏写入。
- **极致轻量化与功耗优化**：
  - `GearDeck.Widget` 保持纯 Win32 + SkiaSharp + `PerMonitorV2` 渲染并合并共享系统监控引擎，常驻总内存压降至约 30+ MB、稳态 CPU 接近 0%，默认不写盘诊断日志。
- **界面与交互打磨**：
  - 优化灵敏度换算页（紧凑基准游戏/FOV 与 3 卡直观展示）、任务栏贴片页（合并预设位置与带微调步进按钮的水平微调）、游戏页（按需打开页面时异步扫描游戏库，且无需点开页面即可后台热切换）与设置页（3×3 对称卡片、独立 Debug 写盘警示卡与更准确的未保存校验）。

---

## 快速安装与使用

### Windows（x64 · 免装 .NET 运行时）
1. 前往 [Releases](https://github.com/alexzhong233/GearDeck/releases) 下载：
   - **安装版**：`GearDeck-Setup-vX.Y.Z-win-x64.exe`（支持从旧版直接覆盖升级）
   - **绿色版**：`GearDeck-vX.Y.Z-win-x64.zip`（解压后运行 `GearDeck.Desktop.exe`）
2. 程序会自动兼容并继承本地既有 `settings.json` 与历史电量缓存；Windows 端可开启桌面左下/右下嵌入式或悬浮式任务栏贴片（`GearDeck.Widget.exe`）。

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
  - **键盘 / 耳机 / 手柄**：VIA/QMK 动态层与灯效/宏、磁轴与微动触发行程 / Rapid Trigger（HITS & SSHS 2.0）、Sony INZONE Buds 双耳与充电盒三段电量、Sony PULSE 3D / Audeze Maxwell / SteelSeries Nova 耳机、飞智八爪鱼力反馈扳机预设、ROG / LEOBOG 机械键盘调参，以及手柄与蓝牙通用电量监控。
  - **真实电量守恒**：仅展示或回退保存曾经真实观测过的电量（`LastKnownBatteries`），绝不凭空估算或伪造有线模式未上报表显电量。
  - **可配置轮询与独立 Debug 模式**：设置页支持直接调节 **HID 设备轮询间隔**（`5–300s`，默认 `15s`）与 **全局低电量阈值**（`5–80%`）；Debug 诊断日志默认关闭（Zero-SSD 损耗），仅排查问题时在独立卡片手动开启。
- **跨游戏灵敏度换算（19 款热门 FPS）**
  - 统一展示 `cm/360°`、FOV 比例缩放与 `MDH 100%` 三种换算结果与手速/1K·2K 回报率饱和物理指标，覆盖 CS2、Valorant、Apex、Overwatch 2、PUBG、BF6/2042、COD、Delta Force、R6S、Marvel Rivals、Escape from Tarkov 等，支持自定义 DPI、分辨率与 `4:3` 拉伸修正。
- **游戏与前台应用自动切配置**
  - 启动零磁盘扫描（仅访问游戏页时按需扫描 Steam / Epic / Riot / WeGame / HoYoPlay / Battle.net / Xbox / EA / Ubisoft / GOG 等），后台通过前台窗口事件实时按规则自动切换并在退出后自动恢复桌面配置。
- **鼠标宏中心（软件宏 + 硬件板载宏）**
  - **软件宏**：后台可全局开关（关闭「侧键接管」即刻卸载全局钩子并恢复原生按键），支持跨设备侧键绑定、滚轮上下录制与延迟间隔编排；界面明确提示竞技游戏反作弊风险。
  - **硬件宏**：仅对开源协议已验证的设备开放「写入硬件宏」，写入前需通过风险确认弹窗。
- **系统监控与云端图库热更新**
  - 实时采集 **12 项系统监控指标**（CPU 占用/频率、内存、GPU 占用/温度/显存、上传/下载网速、磁盘活动/可用空间、开机时长），支持桌面透明悬浮监控条与任务栏贴片双指标轮播。
  - 内置 GitHub Releases 在线更新（支持镜像加速、SHA-256 校验与异常回退直连）与 `assets` 分支设备图库自动同步（含远端版本号防回退机制）。

---

## 双端能力对比

| 功能模块 | Windows 10/11 | macOS 12+ |
| --- | --- | --- |
| 外设 HID 发现与调参（22 个 Provider） | ✅ 完整支持 | ✅ IOHID + `ioreg` 蓝牙电量 |
| 19 款 FPS 灵敏度换算 & 诊断日志 | ✅ 完整支持 | ✅ 完整支持 |
| 前台应用 / 游戏识别自动切配置 | ✅ Win32 + 全屏独占判定 | ✅ `NSWorkspace` 原生进程检测 + `/Applications` & Steam 扫描 |
| 状态栏驻留 & 实时电量展示 | ✅ Win32 托盘 + 纯 Win32 任务栏贴片 | ✅ 原生菜单栏平铺 / 分组图标 + 系统监控摘要 |
| 系统监控面板 & 独立透明悬浮条 | ✅ 12 项全支持 | ✅ CPU/内存/GPU/网络/磁盘/开机时长 |
| 开机自启 | ✅ HKCU Run（带唤醒状态恢复） | ✅ `~/Library/LaunchAgents` plist |
| 全局软件宏与硬件宏管理 | ✅ `SetWindowsHookEx` + `SendInput` + 板载直写 | ⚠️ 支持宏编辑、导入导出与板载直写（软件钩子受辅助功能边界限制） |

---

## 已接入品牌与硬件宏矩阵

| 品牌 / 品类 | 协议与支持特性 | 板载硬件宏写入 | 状态 |
| --- | --- | --- | --- |
| 狼蛛 AULA / Incott | `0xFFA8` SSHS 2.0 磁轴/微动与 `0x09`（含 DPI/回报率/LOD/映射） | ✅ `Incott 0x09`（10 组 320B） | ✅ 已验证 |
| VIA / QMK / Vial 设备 | `0xFF60:0x0061` Raw HID（键盘、磁轴键盘与 Ploopy 等 VIA 鼠标） | ✅ 标准动态宏（`0x0C`~`0x0F`） | ✅ 已验证 |
| 罗技 Logitech | HID++ 2.0（`0x1004/0x1000`、`0x2202`、`0x8061`、`0x8100`、HITS `0x1B0C`）与 G600 专有协议 | ✅ 经典 `0x8100` 布局（CRC-CCITT 校验） | ✅ 已实现 |
| Sinowealth（中颖 8051） | `0x258A` 白名单游戏鼠（Glorious Model O/D 有线、Delux M800DB 等） | ✅ `0x30` 协议 + 按键宏绑定 | ✅ 已实现 |
| Redragon 红龙 | `0x04D9` 白名单（M908 Impact / M711 Cobra，5 档板载切换） | ✅ 15 组 256B `0xF3` 宏直写 | ✅ 已实现 |
| 雷蛇 Razer | 91B Feature（class `07`/`02`，按键重映射严格 `tid=0x10`、非对称 LOD） | ⚙️ 仅支持软件宏 & 单键重映射 | ✅ 已验证 |
| 万灵 / 兰族 / Endgame / Ninjutso / Finalmouse / 雷柏 | CompX 65B / EggWe / Ninjutso / Finalmouse / Rapoo 原生协议 | ⚙️ 仅支持软件宏 | ✅ 已验证 |
| ATK / VXE / VGN | Compx `0x08` + BITMOUSE 64B `0x72`（接收器型号持久化） | ⚙️ 仅支持软件宏 | ✅ 已验证 |
| 迈从 MCHOSE / 怒喵 Angry Miao | MCHOSE V2/V3 与 Airoha RACE（`0E8D:0703`） | ⚙️ 仅支持软件宏 | ✅ 已实现 |
| 名龙星球 MLOONG / LEOBOG | MLOONG 64B & `0x08`；LEOBOG `Hi98C Pro` 有线/无线调参与改键 | ⚙️ 仅支持软件宏 | ✅ 已验证 |
| ROG / 索尼 Sony / Audeze / SteelSeries / 飞智 / 海盗船 / HyperX | Aura/OMNI、INZONE Buds & PULSE 3D、Maxwell、Nova 5/7+、八爪鱼/黑武士、Bragi | — | ✅ 已验证 |
| 手柄与蓝牙通用设备 | Windows XInput / DualSense / Switch Pro + Win32 SetupAPI & macOS `ioreg` 电量 | — | ✅ 已实现 |

---

## 本地构建与验证

确保已安装 **.NET 8 SDK**（Windows 发布完整安装包另需 Inno Setup 6）：

```powershell
# 1. 运行全量单元测试（Windows / macOS 通用）
dotnet restore GearDeck.slnx /p:NuGetAudit=false
dotnet test tests/GearDeck.Tests/GearDeck.Tests.csproj -c Release --no-restore

# 2. 构建跨平台主程序（Windows 构建时自动同步输出 GearDeck.Widget.exe 到同目录）
dotnet build src/GearDeck.Desktop/GearDeck.Desktop.csproj -c Release --no-restore

# 3. 发布自包含单目录成品
dotnet publish src/GearDeck.Desktop/GearDeck.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish/win-x64
dotnet publish src/GearDeck.Widget/GearDeck.Widget.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish/win-x64
```

Windows 构建后可直接运行 `src/GearDeck.Desktop/bin/Release/net8.0/GearDeck.Desktop.exe`。

---

## 目录结构

```
.github/workflows/     GitHub Actions 双端自动编译、测试、打包与 Release 工作流
scripts/               Windows 打包（package-release.ps1）与 Inno Setup 安装脚本（installer.iss）
src/GearDeck.Core      核心模型、HID 安全读写通道、22 个硬件 Provider、灵敏度与更新客户端
src/GearDeck.Desktop   Avalonia 11 + FluentAvaloniaUI 跨平台主程序（Windows + macOS）
src/GearDeck.Widget    Windows 纯 Win32 + SkiaSharp 独立轻量任务栏贴片（PerMonitorV2）
src/Shared             Desktop 与 Widget 复用的跨平台系统指标引擎（64 位网络统计）
tests/GearDeck.Tests   硬件协议、板载宏编码、灵敏度换算与 AXAML 安全红线单元测试
```
