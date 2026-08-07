@echo off
:: Elevated wrapper for Protect-HeimdallEntraSecret.ps1
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Protect-HeimdallEntraSecret.ps1" %*
if errorlevel 1 pause
endlocal
