---
name: geardeck-release-verify
description: >-
  Use when building GearDeck on Windows, verifying GearDeck.Widget self-checks
  and PerMonitorV2 DPI embedding, measuring co-located self-contained publish
  footprint, or dry-running Windows release packaging and GitHub tag releases.
---

# GearDeck Build, Widget Self-Check & Release Verification

Follow this checklist whenever modifying build targets, `GearDeck.Widget`, packaging scripts, or preparing a release tag.

## 1. Pre-Build Process & Desktop Isolation Check

Before compiling `GearDeck.Desktop` or `GearDeck.Widget`, record whether any `GearDeck*` processes are running and stop them to prevent file locks. Never rely solely on `SessionId` to assume desktop visibility—probe `FindWindow("Shell_TrayWnd", null)` in PowerShell if planning to launch GUI binaries.

## 2. Clean Build & Unit Tests

Run solution restore/build and unit tests using a temporary log file so terminal output stays concise:

```powershell
dotnet restore GearDeck.slnx /p:NuGetAudit=false
dotnet build GearDeck.slnx -c Release --no-restore /p:NuGetAudit=false
dotnet test tests\GearDeck.Tests\GearDeck.Tests.csproj -c Release --no-restore /p:NuGetAudit=false
```

Confirm:
- `ERRORS=0`, `WARNINGS=0`.
- `GearDeck.Widget.exe` is co-located inside `src\GearDeck.Desktop\bin\Release\net8.0\` with **no** `widget\` subdirectory.

## 3. Synchronous `GearDeck.Widget` Self-Checks (`WinExe`)

Because `GearDeck.Widget` has `<OutputType>WinExe</OutputType>`, invoking `& GearDeck.Widget.exe` directly in PowerShell returns asynchronously. Always use `Start-Process -Wait -PassThru`:

```powershell
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "gd_widget_chk"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
$w = (Resolve-Path "src\GearDeck.Desktop\bin\Release\net8.0\GearDeck.Widget.exe").Path

$p1 = Start-Process -FilePath $w -ArgumentList "--render-test=`"$tmp\rl.png`"" -Wait -PassThru -NoNewWindow
$p2 = Start-Process -FilePath $w -ArgumentList "--render-test=`"$tmp\rd.png`"", "--dark" -Wait -PassThru -NoNewWindow
$p3 = Start-Process -FilePath $w -ArgumentList "--window-test=`"$tmp\wt.png`"" -Wait -PassThru -NoNewWindow
```

Verify all three exit with `0` and produce non-empty PNGs, confirm `PerMonitorV2` is embedded in `GearDeck.Widget.exe` bytes if `app.manifest` changed, and delete `$tmp` afterwards.

## 4. Self-Contained Co-Located Publish & Packaging Dry-Run

When verifying Windows bundle footprint or packaging logic:
1. Omit `--no-restore` for RID-specific self-contained `dotnet publish -r win-x64`:
   ```powershell
   dotnet publish src\GearDeck.Desktop\GearDeck.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $pubDir
   dotnet publish src\GearDeck.Widget\GearDeck.Widget.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $pubDir
   ```
2. Re-run the three synchronous Widget self-checks against `$pubDir\GearDeck.Widget.exe` to confirm no `libSkiaSharp` version mismatch.
3. Run `scripts\package-release.ps1` with `-ExecutionPolicy Bypass` and `-SkipRootLatestJson` against `$pubDir`, verify root `GearDeck.Widget.*` entries in both the full ZIP and patch ZIP (and zero `widget/` subfolder bloat), then clean up the temporary release directory and `$pubDir`.

## 5. Release Pre-Push Gate

Before committing and tagging a release:
1. Bump `Directory.Build.props` (`Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`).
2. Confirm CI (`ci-release.yml`) restores any cross-project MSBuild target dependencies (`GearDeck.Tests.csproj` + `GearDeck.Widget.csproj`) before `--no-restore` test steps, and sets git identity inside any `git init` subdirectory step.
3. Run `git diff` static check confirming zero hardcoded local paths, usernames, or banned dependencies (`UseWindowsForms`, `System.Drawing`).
