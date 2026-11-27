@echo off
chcp 65001 >nul
cd /d "%~dp0"
set "REL_EXE=%~dp0src\GearDeck.Desktop\bin\Release\net8.0\GearDeck.Desktop.exe"
set "DBG_EXE=%~dp0src\GearDeck.Desktop\bin\Debug\net8.0\GearDeck.Desktop.exe"
if exist "%REL_EXE%" (
    start "" "%REL_EXE%"
    exit /b 0
)
if exist "%DBG_EXE%" (
    start "" "%DBG_EXE%"
    exit /b 0
)
echo GearDeck.Desktop.exe 尚未编译，请先在项目根目录执行：
echo   dotnet build src\GearDeck.Desktop\GearDeck.Desktop.csproj -c Release
pause
exit /b 1
