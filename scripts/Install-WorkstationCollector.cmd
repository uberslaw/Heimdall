@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Heimdall Client Agent Installer

REM Portable installer for the Heimdall Agent (Heimdall-Client pack).
REM Entry point kept as .cmd for pack / silent UpdateClient compatibility.
REM Work is done by Install-WorkstationCollector.ps1 (lock, stages, 1072 waits, LKG rollback).
REM
REM Usage:
REM   Install-WorkstationCollector.cmd
REM   Install-WorkstationCollector.cmd -ApiUrl http://SERVER:5080
REM   Install-WorkstationCollector.cmd -ApiUrl http://SERVER:5080 -ApiKey heimdall-poc-key -MachineGroup SOE
REM   Install-WorkstationCollector.cmd ... -EnableHealWatchdog
REM   Install-WorkstationCollector.cmd -UnregisterHealWatchdog
REM   Install-WorkstationCollector.cmd -HealOnly
REM
REM Env: HEIMDALL_ENABLE_HEAL=1 enables heal add-on (same as -EnableHealWatchdog).
REM Silent UpdateClient must NOT set HEIMDALL_ENABLE_HEAL (preserves existing task only).
REM
REM Expected layout next to this script:
REM   payload\Heimdall.Agent.exe   (+ other published files)
REM   Install-WorkstationCollector.ps1
REM   Heimdall-AgentHeal.ps1       (Phase 3 add-on; copied to ProgramData when enabled)

cd /d "%~dp0"

set "APIURL=http://BNELT5CG5152D8R:5080"
set "APIKEY=heimdall-poc-key"
set "MACHINEGROUP=POC"
set "INSTALLDIR=%ProgramFiles%\Heimdall\Agent"
set "PAYLOAD=%~dp0payload"
set "LOGROOT=%ProgramData%\Heimdall\logs"
set "EXITCODE=1"
set "ENABLE_HEAL=0"
set "UNREGISTER_HEAL=0"
set "HEAL_ONLY=0"
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell.exe"
if /I "%HEIMDALL_ENABLE_HEAL%"=="1" set "ENABLE_HEAL=1"

REM Prefer Install.cmd when present (guided client wizard). Set HEIMDALL_SKIP_LAUNCH=1 to force this script.
if /I not "%HEIMDALL_SKIP_LAUNCH%"=="1" (
  if "%~1"=="" (
    if exist "%~dp0Install.cmd" (
      echo.
      echo Opening Heimdall Install ^(guided setup^)...
      echo To run this CMD installer directly: set HEIMDALL_SKIP_LAUNCH=1
      echo.
      call "%~dp0Install.cmd"
      exit /b %ERRORLEVEL%
    )
  )
)

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="-ApiUrl" goto arg_apiurl
if /I "%~1"=="-ApiKey" goto arg_apikey
if /I "%~1"=="-MachineGroup" goto arg_machinegroup
if /I "%~1"=="-InstallDir" goto arg_installdir
if /I "%~1"=="-Payload" goto arg_payload
if /I "%~1"=="-EnableHealWatchdog" goto arg_enable_heal
if /I "%~1"=="-UnregisterHealWatchdog" goto arg_unregister_heal
if /I "%~1"=="-HealOnly" goto arg_heal_only
if /I "%~1"=="-h" goto usage
if /I "%~1"=="-Help" goto usage
if /I "%~1"=="/?" goto usage
echo Unknown argument: %~1
goto usage

:arg_apiurl
if "%~2"=="" goto usage
set "APIURL=%~2"
shift
shift
goto parse_args

:arg_apikey
if "%~2"=="" goto usage
set "APIKEY=%~2"
shift
shift
goto parse_args

:arg_machinegroup
if "%~2"=="" goto usage
set "MACHINEGROUP=%~2"
shift
shift
goto parse_args

:arg_installdir
if "%~2"=="" goto usage
set "INSTALLDIR=%~2"
shift
shift
goto parse_args

:arg_payload
if "%~2"=="" goto usage
set "PAYLOAD=%~2"
shift
shift
goto parse_args

:arg_enable_heal
set "ENABLE_HEAL=1"
shift
goto parse_args

:arg_unregister_heal
set "UNREGISTER_HEAL=1"
shift
goto parse_args

:arg_heal_only
set "HEAL_ONLY=1"
shift
goto parse_args

:args_done

echo.
echo ================================================================
echo   Heimdall Client agent installer
echo ================================================================
echo.
echo Window stays open until you press a key — do not close it early.
echo Prefer guided setup: Install.cmd
echo.

net session >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Administrator rights required.
  echo.
  echo Attempting to relaunch elevated - accept the UAC prompt.
  echo This window will wait until the elevated installer finishes.
  echo.
  set "ELEVATE_CMD=%TEMP%\heimdall-elev-%RANDOM%.cmd"
  set "ELEV_EXTRA="
  if "!ENABLE_HEAL!"=="1" set "ELEV_EXTRA=!ELEV_EXTRA! -EnableHealWatchdog"
  if "!UNREGISTER_HEAL!"=="1" set "ELEV_EXTRA=!ELEV_EXTRA! -UnregisterHealWatchdog"
  if "!HEAL_ONLY!"=="1" set "ELEV_EXTRA=!ELEV_EXTRA! -HealOnly"
  (
    echo @echo off
    echo setlocal EnableExtensions EnableDelayedExpansion
    echo cd /d "%~dp0"
    echo set HEIMDALL_SKIP_LAUNCH=1
    if "!ENABLE_HEAL!"=="1" echo set HEIMDALL_ENABLE_HEAL=1
    echo call "%~f0" -ApiUrl "!APIURL!" -ApiKey "!APIKEY!" -MachineGroup "!MACHINEGROUP!" -InstallDir "!INSTALLDIR!" -Payload "!PAYLOAD!"!ELEV_EXTRA!
    echo exit /b %%ERRORLEVEL%%
  ) > "!ELEVATE_CMD!"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%ELEVATE_CMD%' -Verb RunAs -Wait -PassThru | ForEach-Object { exit $_.ExitCode }"
  set "EXITCODE=!ERRORLEVEL!"
  del /F /Q "!ELEVATE_CMD!" >nul 2>&1
  echo.
  echo Elevated installer finished ^(exit !EXITCODE!^).
  goto end
)

if not exist "%~dp0Install-WorkstationCollector.ps1" (
  echo [ERROR] Install-WorkstationCollector.ps1 missing next to this .cmd
  echo Pack must include both Install-WorkstationCollector.cmd and .ps1
  set "EXITCODE=1"
  goto end
)

set "PS_EXTRA="
if "!ENABLE_HEAL!"=="1" set "PS_EXTRA=!PS_EXTRA! -EnableHealWatchdog"
if "!UNREGISTER_HEAL!"=="1" set "PS_EXTRA=!PS_EXTRA! -UnregisterHealWatchdog"
if "!HEAL_ONLY!"=="1" set "PS_EXTRA=!PS_EXTRA! -HealOnly"

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-WorkstationCollector.ps1" -ApiUrl "%APIURL%" -ApiKey "%APIKEY%" -MachineGroup "%MACHINEGROUP%" -InstallDir "%INSTALLDIR%" -Payload "%PAYLOAD%"!PS_EXTRA!
set "EXITCODE=!ERRORLEVEL!"
goto end

:usage
echo.
echo Usage: Install-WorkstationCollector.cmd [options]
echo.
echo Options:
echo   -ApiUrl URL               Heimdall API base URL (default http://BNELT5CG5152D8R:5080^)
echo   -ApiKey KEY               Must match API key (default heimdall-poc-key^)
echo   -MachineGroup NAME        e.g. SOE, POC, APAC/Sydney (default POC^)
echo   -InstallDir PATH          Default %%ProgramFiles%%\Heimdall\Agent
echo   -Payload PATH             Folder containing Heimdall.Agent.exe (default .\payload^)
echo   -EnableHealWatchdog       Opt-in: register HeimdallAgentHeal ^(SYSTEM, every 15m^)
echo   -UnregisterHealWatchdog   Remove HeimdallAgentHeal scheduled task
echo   -HealOnly                 Restore agent from LKG ^(used by heal watchdog^)
echo.
echo Env: HEIMDALL_ENABLE_HEAL=1 same as -EnableHealWatchdog
echo Pack on a build PC first: scripts\Heimdall-Setup.lnk -^> Create client pack
echo See docs\portable-client\README.md for files and dependencies.
echo.
set "EXITCODE=1"
goto end

:end
echo.
if not "!EXITCODE!"=="0" (
  echo Install failed. Review the messages above and logs under:
  echo   %LOGROOT%
  echo     install-agent-*.log      ^(service install^)
  echo     install-client-*.log     ^(Install.cmd wizard^)
  echo   Durable install state ^(if present^):
  echo     %%ProgramData%%\Heimdall\update\install.lock
  echo     %%ProgramData%%\Heimdall\update\install-state.json
  echo     %%ProgramData%%\Heimdall\update\lkg\   ^(last-known-good agent^)
  echo.
  if /I not "%HEIMDALL_NOPAUSE%"=="1" pause
) else if /I not "%HEIMDALL_NOPAUSE%"=="1" (
  pause
)
exit /b !EXITCODE!
