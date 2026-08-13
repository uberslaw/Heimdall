@echo off
title Heimdall Diagnostics Collector
cd /d "%~dp0"
echo.
echo Collecting Heimdall diagnostics for support / Cursor analysis...
echo Default output: C:\Temp\Heimdall.API\Logs
echo Always-on logs: %%ProgramData%%\Heimdall\logs\api  and  logs\ops
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -NoExit -File "%~dp0collect-diagnostics.ps1" %*
