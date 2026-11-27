# GearDeck Workspace Rules

## 硬性安全与硬件约束
- 严禁在代码、脚本、CI 与文档中写入用户本机绝对路径（个人盘符目录、本地项目路径、本地 IDE 路径）、用户名、主机名、个人软件安装盘符或本机特定分辨率默认值（统一用系统 API / 环境变量 / 相对路径，默认分辨率用通用 `1920×1080` 或运行时探测）。
- 禁止修改 `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3` 或重启 `explorer.exe`。
- 禁止带 `--preview` 参数启动 `GearDeck.Widget.exe`。
- 禁止伪造从未观测过真实电量的设备百分比；休眠回退仅限 `LastKnownBatteries` 的真实历史采集值。
- Razer 原生按键重映射写入始终使用 `tid = 0x10`。
- XAML 中所有 `<Slider>` / `<ProgressBar>` 必须保持 `Maximum` 写在 `Minimum` 之前，且 `Minimum <= 1.0`。

## WinUI 3 与运行环境约定
- `GearDeck.App` 未引用 `Microsoft.Win32.SystemEvents`：监听系统睡眠/唤醒事件请直接在 `TrayIconService.WindowProc` 处理 `WM_POWERBROADCAST`（`0x0218`），不要为此新增 NuGet 依赖。
- 本地编译进程处理：在编译 `GearDeck.App` / `GearDeck.Widget` 前，先检查是否有正在运行的 `GearDeck.App` / `GearDeck.Widget` 进程并记录此前是否处于启动状态（以及可执行路径与参数），随后结束对应进程避免 DLL 锁文件；编译成功后，若编译前处于启动状态则启动新编译出的对应进程，若编译前未启动则保持关闭不管。仅在确认终端与桌面 `Shell_TrayWnd` 隔离时，测试完再清理并提示用户在桌面端启动。

## 变更验证基线
- 单元测试：`dotnet test tests\GearDeck.Tests\GearDeck.Tests.csproj -c Release --no-restore`
- 主程序编译：`MSBuild.exe src\GearDeck.App\GearDeck.App.csproj /t:Build /p:Configuration=Release /p:Platform=x64`（视需要补充 `Debug|x64`）
- 涉及 `GearDeck.Widget` 改动时，额外运行 `--render-test` 与 `--window-test` 确认返回码 `0`。
