# GearDeck · 外设控制与监控中心

Windows 10/11 原生轻量外设控制与监控台：**WinUI 3 Mica 控制中心 + 任务栏分组电量/监控贴片 + 按游戏/全屏自动切配置 + 原生硬件宏 + 自定义镜像在线更新**。
自研 C# / .NET 8 + WinUI 3（Windows App SDK），多品牌硬件协议实现参考开源社区方案。

- **GitHub 仓库**：https://github.com/alexzhong233/GearDeck
- **版本下载（Releases）**：https://github.com/alexzhong233/GearDeck/releases

## 核心功能

- **多品牌外设原生接管与参数调校**
  - **鼠标**：DPI / XY 独立灵敏度 / 多档位编辑、125–8000 Hz 回报率、性能/超频模式、MotionSync / 直线修正 / 波纹修正 / 远距增强、LOD / Razer 非对称中止高度、角度校准、防抖延迟、休眠时间、板载配置槽位切换与物理按键重映射。
  - **耳机 / 键盘 / 手柄**：Sony INZONE Buds（左耳/右耳/充电盒三段电量）、ROG Delta II / Falchion Ace 75 HE、飞智八爪鱼 5 / 6 Pro / CD2 底座（板载 4 槽位 + ForceAdapt 扳机预设）。
  - **外置设备造型规则库 (`device-images.json`)**：本地高精俯视矢量剪影 + 新设备后台静默云端拉图 + 本地自定义上传。
- **游戏与进程配置自动切换**
  - 自动识别 **本机游戏库**（Steam / Epic / Riot / WeGame / HoYoPlay / Battle.net / Xbox / EA / Ubisoft / GOG + OpenMouse 签名目录）。
  - 仅切换当前**未休眠**外设并在休眠设备唤醒后自动补齐；全屏/游戏窗口未退出时（如 Alt+Tab 切出）支持保持不提前切回默认配置；可开启 Windows 原生切换通知。
- **全局外置鼠标宏中心 & 任务栏贴片**
  - 独立录制/编辑宏动作，支持硬件板载宏写入及 per-device 侧键触发绑定。
  - 原生 WinForms 任务栏贴片（不篡改系统注册表），支持逐设备独立低电量阈值告警与 12 项系统监控指标（CPU / GPU / 显存 / 内存 / 网络 / 磁盘 / 运行时长）。
- **在线检查更新与自定义 GitHub 镜像站**
  - 设置页内置一键检查更新、SHA-256 校验、增量补丁/完整包自动下载并后台热替换重启升级。
  - 支持填写自定义 GitHub 镜像站前缀（如 `https://ghfast.top/`、`https://gh-proxy.com/`）或 `{url}` / `{url_encoded}` 模板地址，并在镜像异常时自动回退直连 GitHub。

## 已接入协议

| 品牌 / 品类 | 协议与支持能力 | 状态 |
|---|---|---|
| 雷蛇 Razer | 91B Feature（class 07/02, `tid=0x10` 按键重映射、非对称 LOD、低电量预警） | ✅ 已验证 |
| 罗技 Logitech | HID++ 2.0（`0x1004/0x1000`、`0x2202`、`0x8061`、`0x8100` Host/Onboard 模式） | ✅ 已实现 |
| 万灵 WLmouse / 兰族 Lamzu | CompX 65B Page/Command + Turbo 20K 模式与按键映射 | ✅ 已验证 |
| ATK / VXE / VGN | Compx `0x08` + BITMOUSE 64B `0x72` + VIA 键盘 | ✅ 已实现 |
| 迈从 MCHOSE | V2 (`^0xFF`) 与 V3 (`0x4D` 尾部保留) | ✅ 已实现 |
| 怒喵 Angry Miao / 狼蛛 AULA / Incott | GearHub MU + `0x13/0x4A` + Incott `0x09` 320B 硬件宏烧录 | ✅ 已验证 |
| ROG / 索尼 Sony | ROG Aura/OMNI 2.4G + Sony INZONE Buds (`054C:0EC2` 64B `0x41/0x04`) | ✅ 已验证 |
| 飞智 Flydigi | 八爪鱼 5 (`37D7:2501`) / 6 Pro (`37D7:2502`) / 黑武士 5 Pro / CD2 充电底座 (`37D7:6001`) | ✅ 已验证 |

## 构建、打包与启动

```powershell
# 编译 WinUI 3 主程序（自动连带编译 Core 与 Widget）
& "D:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
  src\GearDeck.App\GearDeck.App.csproj /restore /p:Configuration=Release /p:Platform=x64 /v:minimal

# 运行单元测试
dotnet test tests\GearDeck.Tests\GearDeck.Tests.csproj -c Debug /p:NuGetAudit=false

# 打 Release ZIP（含完整包、增量补丁包与 latest.json）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-release.ps1 -Version 1.0.1 -Notes "版本说明"
```

本地项目路径：`D:\projects\GearDeck`
快速启动：双击根目录 [`启动 GearDeck.cmd`](启动%20GearDeck.cmd)，或直接从 [GitHub Releases](https://github.com/alexzhong233/GearDeck/releases) 下载 `GearDeck-vX.Y.Z-win-x64.zip` 解压运行。

## 目录结构

```
scripts/               Release 打包脚本（完整包 + 增量热更新补丁包 + latest.json）
src/GearDeck.Core      设备模型、HID 安全通道、多品牌协议 Provider、游戏/图片规则与 GitHub 镜像更新客户端
src/GearDeck.App       WinUI 3 主控制面板、Mica 标题栏、自更新服务、自动切配/宏/通知服务
src/GearDeck.Widget    独立 WinForms 分层透明任务栏贴片进程
src/GearDeck.Monitor   独立系统监控扩展进程
tests/GearDeck.Tests   协议编解码、游戏规则保持、图片规则、镜像更新解析与 XAML 安全单元测试
```
