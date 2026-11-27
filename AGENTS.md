# GearDeck Workspace Rules

## 硬性安全与硬件约束
- 严禁在代码、脚本、CI 与文档中写入用户本机绝对路径（个人盘符目录、本地项目路径、本地 IDE 路径）、用户名、主机名、个人软件安装盘符或本机特定分辨率默认值（统一用系统 API / 环境变量 / 相对路径，默认分辨率用通用 `1920×1080` 或运行时探测）。
- 禁止修改 `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3` 或重启 `explorer.exe`。
- 禁止带 `--preview` 参数启动 `GearDeck.Widget.exe`。
- 禁止伪造从未观测过真实电量的设备百分比；休眠回退仅限 `LastKnownBatteries` 的真实历史采集值。
- Razer 原生按键重映射写入始终使用 `tid = 0x10`。
- XAML 中所有 `<Slider>` / `<ProgressBar>` 必须保持 `Maximum` 写在 `Minimum` 之前，且 `Minimum <= 1.0`。
- 非调试模式下禁止高频或周期性轮询写盘日志（`diag.log`、`widget.log` 等），仅允许在显式开启调试日志或发生致命异常时落盘，避免磨损 SSD。
- `GearDeck.Widget` 保持纯 Win32 + `SkiaSharp`（版本与 `GearDeck.Desktop` 严格一致），禁止重新引入 `UseWindowsForms` / WPF / `System.Drawing` 或独立的自包含 `widget/` 运行时目录；必须同时在 `app.manifest` 声明 `PerMonitorV2`、`Program.Main` 入口调用 `SetProcessDpiAwarenessContext(-4)` 并处理 `WM_DPICHANGED`。
- 跨平台与设备协议约束：`GearDeck.Core` 解析路径/文件名时需先统一规范化 `\` 分隔符以兼容 macOS；设备连接后保留细分厂商与协议特征标签，不可覆盖为通用类别；分体/佩戴检测类设备（如 Sony INZONE Buds）不得因瞬时轮询未返回就误判为休眠。
- CI 若在子目录执行 `git init`（如 `dist-assets`），必须在该子仓库目录内设置 `git config user.name` 与 `user.email`（或使用 `--global`）。

## 跨平台（Avalonia）与运行环境约定
- 当前工程结构为 `GearDeck.Core`、`GearDeck.Desktop`（Avalonia 跨平台主程序）、`GearDeck.Widget`（Windows 任务栏纯 Win32 挂件），已废弃旧 `GearDeck.App`。
- MSBuild 跨项目触发子构建（如 `CopyWidget`）需附带 `Exists('..\...\obj\project.assets.json')` 条件，避免仅 restore 单个测试项目时 `--no-restore` 失败。
- 本地编译进程处理：在编译 `GearDeck.Desktop` / `GearDeck.Widget` 前，先检查并记录运行中的 `GearDeck.Desktop` / `GearDeck.Widget` 进程（路径与参数），随后结束对应进程避免 DLL 锁文件；编译后仅当编译前处于启动状态且终端可访问桌面 `Shell_TrayWnd`（通过 `FindWindow("Shell_TrayWnd", null) != 0` 确认，不能仅凭 `SessionId` 判断）时重启新产物，若与桌面隔离则在测试后清理进程并提示用户在桌面端启动。

## 变更验证基线
- 解决方案编译：`dotnet restore GearDeck.slnx /p:NuGetAudit=false && dotnet build GearDeck.slnx -c Release --no-restore /p:NuGetAudit=false`
- 单元测试：`dotnet test tests\GearDeck.Tests\GearDeck.Tests.csproj -c Release --no-restore`
- 涉及 `GearDeck.Widget` 改动时，因目标为 `WinExe`，在 PowerShell 中必须用 `Start-Process ... -Wait -PassThru -NoNewWindow` 同步执行 `--render-test`（浅色与 `--dark`）及 `--window-test`，确认退出码 `0` 且输出 PNG 非空；若改动原生清单或 DPI 逻辑，还需校验产物内嵌 `PerMonitorV2`。
