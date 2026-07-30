@echo off
setlocal EnableExtensions
title Heimdall Setup
cd /d "%~dp0"

REM Compat wrapper — prefer Heimdall-Setup.lnk / Heimdall-Setup.cmd

echo.
echo Starting Heimdall Setup...
echo Logs go to: %ProgramData%\Heimdall\logs\
echo Close the form window when finished.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0Heimdall-LaunchControl.ps1" %*
set "EC=%ERRORLEVEL%"
if not "%EC%"=="0" (
  echo.
  echo Setup exited with code %EC%.
  echo If a log path was printed above, send that file for analysis.
  pause
)
exit /b %EC%
