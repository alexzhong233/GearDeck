# 需求规格（2026-11-17 定稿）

## 总体决定

- **完全自研**（C# / .NET 8 WPF），协议实现参考开源项目（见 research.md）。
- 构建环境：Visual Studio 2026 Community（D 盘安装）+ .NET 8 SDK（ManagedDesktop 工作负载）。

## 功能需求

### 1. 任务栏贴片（核心功能）
- 常驻任务栏，可固定显示；**支持靠左 / 靠右对齐**（TrafficMonitor 式）。
- 显示内容**自由选择**：任选设备或设备组。
- **设备组**：
  - 内置组：按类型自动分组（全部鼠标 / 全部耳机 / 全部键盘）。
  - 鼠标悬停在贴片项上 → 弹出浮窗显示**同组设备**的电量明细。
  - **自定义组**：用户手动把任意设备划进一个组。
- 低电量提醒（阈值可配置）。

### 2. 系统监控（独立开关）
- 类 LibreHardwareMonitor 的系统监控：CPU / GPU / 内存等。
- **独立开关**：默认关闭；开启后**拉起独立的系统监控进程**（GearDeck.Monitor.exe），关闭时结束该进程。
- 主程序与监控进程解耦，监控崩溃不影响电量功能。

### 3. 电量与设置
- 阶段 1：全设备电量只读。
- 阶段 2：DPI / 回报率设置（雷蛇、罗技、ROG、怒喵优先）。

## 设备清单（用户确认）

| 品牌 | 型号 | 类别 | 连接 | 协议族 |
|---|---|---|---|---|
| 雷蛇 | 毒蝰 V3 Pro | 鼠标 | 无线接收器 | Razer HID |
| 雷蛇 | 毒蝰 V4 Pro | 鼠标 | 无线接收器 | Razer HID |
| 罗技 | GPW5 Ultra | 鼠标 | LIGHTSPEED | HID++ 2.x |
| 万灵 WLmouse | 花兽 G / 花兽 2 | 鼠标 | 2.4G | CompX 65 字节 Feature |
| 兰族 Lamzu | 冥神 | 鼠标 | 2.4G | CompX 65 字节 Feature |
| 怒喵 Angry Miao | .97 | 鼠标 | 2.4G | 私有（AngryMiaoBatteryMgr） |
| ATK | A9 mini | 鼠标 | 2.4G | ATK 16 字节（VID 373B） |
| 迈从 MCHOSE | 全系 | 鼠标 | 2.4G / 有线 | MCHOSE 新协议 |
| ROG | 游侠 98 RX 无线 | 键盘 | 无线 | ROG 私有 HID |
| ROG | 棱镜 2 | 耳机 | 无线 | ROG 统一查询 `CC 12 07` |

> PID 待首次真机枚举确认；CompX 族覆盖兰族/万灵的所有在售型号（见 FerrisPulse 文档）。
