@echo off
title Heimdall Agent Installer
cd /d "%~dp0"
echo.
echo Starting Heimdall Agent installer (elevated PowerShell)...
echo Window stays open so you can copy the log path.
echo.
REM Pass ApiUrl if needed, e.g. Install-Agent.cmd -ApiUrl http://myserver:5080
powershell.exe -NoProfile -ExecutionPolicy Bypass -NoExit -File "%~dp0install-agent.ps1" %*
