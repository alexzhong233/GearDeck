@echo off
chcp 65001 >nul
cd /d "%~dp0"
set "APP_EXE=%~dp0src\GearDeck.App\bin\x64\Debug\net8.0-windows10.0.22621.0\GearDeck.App.exe"
if not exist "%APP_EXE%" (
    echo GearDeck.App.exe 尚未编译，请先在项目根目录执行 MSBuild 构建。
    pause
    exit /b 1
)
start "" "%APP_EXE%"
