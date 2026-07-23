@echo off
title Heimdall Diagnostics Collector
cd /d "%~dp0"
echo.
echo Collecting Heimdall diagnostics for support / Cursor analysis...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -NoExit -File "%~dp0collect-diagnostics.ps1" %*
