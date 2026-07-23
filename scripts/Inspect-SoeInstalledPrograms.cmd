@echo off
title Heimdall SOE Installed Programs Inspector
cd /d "%~dp0"
echo.
echo Enumerating installed programs for SOE exclude review...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -NoExit -File "%~dp0Inspect-SoeInstalledPrograms.ps1" -CompareCatalog %*
