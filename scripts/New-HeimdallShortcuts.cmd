@echo off
setlocal EnableExtensions
cd /d "%~dp0.."
set "ROOT=%CD%"

if not exist "%ROOT%\assets\heimdall.ico" (
  echo [ERROR] Missing icon: %ROOT%\assets\heimdall.ico
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\New-HeimdallShortcut.ps1" ^
  -ShortcutPath "%ROOT%\scripts\Heimdall-Setup.lnk" ^
  -TargetPath "%ROOT%\scripts\Heimdall-Setup.cmd" ^
  -IconPath "%ROOT%\assets\heimdall.ico" ^
  -WorkingDirectory "%ROOT%\scripts" ^
  -Description "Heimdall Setup"
if errorlevel 1 exit /b 1

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\New-HeimdallShortcut.ps1" ^
  -ShortcutPath "%ROOT%\scripts\Heimdall-LaunchControl.lnk" ^
  -TargetPath "%ROOT%\scripts\Heimdall-Setup.cmd" ^
  -IconPath "%ROOT%\assets\heimdall.ico" ^
  -WorkingDirectory "%ROOT%\scripts" ^
  -Description "Heimdall Setup"
if errorlevel 1 exit /b 1

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\New-HeimdallShortcut.ps1" ^
  -ShortcutPath "%ROOT%\scripts\Install.lnk" ^
  -TargetPath "%ROOT%\scripts\Install.cmd" ^
  -IconPath "%ROOT%\assets\heimdall.ico" ^
  -WorkingDirectory "%ROOT%\scripts" ^
  -Description "Install Heimdall Agent on this PC"
if errorlevel 1 exit /b 1

echo [OK] shortcuts updated in scripts\
exit /b 0
