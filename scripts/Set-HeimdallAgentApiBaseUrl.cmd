@echo off
setlocal EnableExtensions
title Heimdall - set agent API URL

REM =============================================================================
REM  EDIT THIS when the Heimdall API host IP changes:
set "API_IP=172.17.40.191"
set "API_PORT=5080"
REM =============================================================================

REM Always resolve the .ps1 next to this .cmd (works from network share / UNC).
cd /d "%~dp0"
set "PS1=%~dp0Set-HeimdallAgentApiBaseUrl.ps1"
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell.exe"

if not exist "%PS1%" (
  echo [ERROR] Set-HeimdallAgentApiBaseUrl.ps1 not found next to this .cmd
  echo Expected: %PS1%
  echo.
  pause
  exit /b 1
)

echo.
echo Heimdall Agent - set ApiBaseUrl to http://%API_IP%:%API_PORT%
echo Script folder: %~dp0
echo.

net session >nul 2>&1
if errorlevel 1 (
  echo Administrator rights required - accepting UAC will continue.
  echo.
  REM Mapped network drives often vanish under UAC. Copy the .ps1 to local TEMP first.
  copy /Y "%PS1%" "%TEMP%\Set-HeimdallAgentApiBaseUrl.ps1" >nul
  if errorlevel 1 (
    echo [ERROR] Could not copy script to TEMP - run from a local folder or UNC path.
    pause
    exit /b 1
  )
  set "ELEV=%TEMP%\heimdall-set-api-elev-%RANDOM%.cmd"
  (
    echo @echo off
    echo "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%%TEMP%%\Set-HeimdallAgentApiBaseUrl.ps1" -IpAddress %API_IP% -Port %API_PORT%
    echo set "EC=%%ERRORLEVEL%%"
    echo echo.
    echo if %%EC%% neq 0 echo [FAILED] exit %%EC%%
    echo pause
    echo exit /b %%EC%%
  ) > "%ELEV%"
  "%PS%" -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%ELEV%' -Verb RunAs -Wait"
  del /F /Q "%ELEV%" >nul 2>&1
  echo.
  echo Elevated window closed. Press a key to close this one.
  pause
  exit /b 0
)

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%PS1%" -IpAddress %API_IP% -Port %API_PORT%
set "EC=%ERRORLEVEL%"
echo.
if %EC% neq 0 echo [FAILED] exit %EC%
pause
exit /b %EC%
