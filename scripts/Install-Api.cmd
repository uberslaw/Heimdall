@echo off
title Heimdall API Installer
cd /d "%~dp0"
echo.
echo Starting Heimdall API installer (elevated PowerShell)...
echo Window stays open so you can copy the log path.
echo.
REM -NoExit keeps the console open if the script exits early; script also pauses with Read-Host.
powershell.exe -NoProfile -ExecutionPolicy Bypass -NoExit -File "%~dp0install-api.ps1" %*
