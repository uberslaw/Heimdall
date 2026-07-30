@echo off
setlocal EnableExtensions
title Heimdall Setup

REM Single guided entry for API install, create client pack, agent install, and tools.
REM Prefer Heimdall-Setup.lnk (helmet icon) when double-clicking from Explorer.

cd /d "%~dp0"
set "PS1=%~dp0Heimdall-LaunchControl.ps1"
if not exist "%PS1%" (
  echo [ERROR] Missing %PS1%
  pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%PS1%" %*
set "EXITCODE=%ERRORLEVEL%"
if not "%HEIMDALL_NOPAUSE%"=="1" (
  if not "%EXITCODE%"=="0" (
    echo.
    echo Setup exited with code %EXITCODE%.
    pause
  )
)
exit /b %EXITCODE%
