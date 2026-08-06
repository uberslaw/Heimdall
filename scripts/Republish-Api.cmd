@echo off
REM Republish Heimdall.Api to Program Files without overwriting appsettings.json.
REM Also copies TuflowLauncher beside the agent if present.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process powershell.exe -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%~dp0_tuflow-republish-api.ps1\"' -Verb RunAs -Wait"
echo.
echo If UAC was approved, check http://localhost:5080/TuflowRuns
pause
