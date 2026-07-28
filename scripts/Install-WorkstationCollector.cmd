@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Heimdall Workstation Collector Installer

REM Portable installer for the Heimdall Agent (workstation collector).
REM Run elevated from a packed folder produced by Pack-WorkstationCollector.cmd.
REM Prefer this over Install-Agent.cmd when deploying to other PCs without the full repo/SDK.
REM
REM Usage:
REM   Install-WorkstationCollector.cmd
REM   Install-WorkstationCollector.cmd -ApiUrl http://SERVER:5080
REM   Install-WorkstationCollector.cmd -ApiUrl http://SERVER:5080 -ApiKey heimdall-poc-key -MachineGroup SOE
REM
REM Expected layout next to this script:
REM   payload\Heimdall.Agent.exe   (+ other published files)

cd /d "%~dp0"

set "APIURL=http://localhost:5080"
set "APIKEY=heimdall-poc-key"
set "MACHINEGROUP=POC"
set "INSTALLDIR=%ProgramFiles%\Heimdall\Agent"
set "PAYLOAD=%~dp0payload"
set "LOGROOT=%ProgramData%\Heimdall\logs"
set "EXITCODE=1"

REM Prefer Launch Control when present (guided UI). Set HEIMDALL_SKIP_LAUNCH=1 to force this script.
if /I not "%HEIMDALL_SKIP_LAUNCH%"=="1" (
  if exist "%~dp0Heimdall-LaunchControl.cmd" (
    if "%~1"=="" (
      echo.
      echo Opening Heimdall Launch Control ^(guided setup^)...
      echo To run this CMD installer directly: set HEIMDALL_SKIP_LAUNCH=1
      echo.
      call "%~dp0Heimdall-LaunchControl.cmd" -Mode InstallCollector
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

:args_done

echo.
echo ================================================================
echo   Heimdall Workstation Collector installer
echo ================================================================
echo.
echo Window stays open until you press a key — do not close it early.
echo Prefer guided setup: Heimdall-LaunchControl.cmd
echo.

net session >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Administrator rights required.
  echo.
  echo Attempting to relaunch elevated - accept the UAC prompt.
  echo This window will wait until the elevated installer finishes.
  echo.
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs -Wait; exit $LASTEXITCODE"
  set "EXITCODE=!ERRORLEVEL!"
  echo.
  echo Elevated installer finished ^(exit !EXITCODE!^).
  goto end
)

if not exist "%PAYLOAD%\Heimdall.Agent.exe" (
  echo [ERROR] Payload not found: "%PAYLOAD%\Heimdall.Agent.exe"
  echo.
  echo This installer expects a packed folder from Pack-WorkstationCollector.cmd.
  echo From a full Heimdall clone ^(with .NET 10 SDK^):
  echo   scripts\Pack-WorkstationCollector.cmd
  echo   OR scripts\Heimdall-LaunchControl.cmd -^> Pack collector
  echo Then copy the WHOLE dist\workstation-collector folder ^(must include payload\^).
  echo.
  echo If you only have README/FILES under scripts\workstation-collector\, that is docs only — not installable.
  goto fail
)

if not exist "%LOGROOT%" mkdir "%LOGROOT%" >nul 2>&1
REM Locale-safe-ish stamp from DATE/TIME (no PowerShell)
set "STAMP=%DATE:~-4%%DATE:~4,2%%DATE:~7,2%-%TIME:~0,2%%TIME:~3,2%%TIME:~6,2%"
set "STAMP=!STAMP: =0!"
set "STAMP=!STAMP:/=!"
set "STAMP=!STAMP::=!"
set "STAMP=!STAMP:.=!"
set "LOGFILE=%LOGROOT%\install-workstation-collector-!STAMP!.log"

call :log INFO "Log file: !LOGFILE!"
call :log INFO "User: %USERNAME% | Machine: %COMPUTERNAME%"
call :log INFO "ApiUrl=!APIURL! MachineGroup=!MACHINEGROUP! InstallDir=!INSTALLDIR!"
call :log INFO "Payload=!PAYLOAD!"

call :log STEP "Ensure ProgramData\Heimdall"
if not exist "%ProgramData%\Heimdall" mkdir "%ProgramData%\Heimdall" >nul 2>&1
if errorlevel 1 (
  call :log ERROR "Could not create %ProgramData%\Heimdall"
  goto fail
)

call :log STEP "Ensure install directory"
if not exist "!INSTALLDIR!" mkdir "!INSTALLDIR!" >nul 2>&1
if errorlevel 1 (
  call :log ERROR "Could not create !INSTALLDIR!"
  goto fail
)

call :log STEP "Probe API health (best-effort)"
set "HEALTH=!APIURL!"
if "!HEALTH:~-1!"=="/" set "HEALTH=!HEALTH:~0,-1!"
set "HEALTH=!HEALTH!/api/health"
curl.exe -sS -m 10 "!HEALTH!" >nul 2>&1
if errorlevel 1 (
  call :log WARN "API not reachable yet at !HEALTH! — install continues; fix URL/firewall if heartbeats fail."
) else (
  call :log OK "API reachable: !HEALTH!"
)

call :log STEP "Stop existing HeimdallAgent service if present"
sc.exe query HeimdallAgent >nul 2>&1
if not errorlevel 1 (
  sc.exe stop HeimdallAgent >nul 2>&1
  timeout /t 2 /nobreak >nul
  sc.exe delete HeimdallAgent >nul 2>&1
  timeout /t 2 /nobreak >nul
  call :log INFO "Removed previous HeimdallAgent service"
) else (
  call :log INFO "No existing HeimdallAgent service"
)

call :log STEP "Copy payload to install directory"
robocopy "!PAYLOAD!" "!INSTALLDIR!" /E /NFL /NDL /NJH /NJS /nc /ns /np >nul
set "RC=!ERRORLEVEL!"
if !RC! GEQ 8 (
  call :log ERROR "robocopy failed with exit !RC!"
  goto fail
)
if not exist "!INSTALLDIR!\Heimdall.Agent.exe" (
  call :log ERROR "Heimdall.Agent.exe missing after copy"
  goto fail
)
call :log OK "Copied payload to !INSTALLDIR!"

call :log STEP "Write appsettings.json"
set "QUEUEPATH=%ProgramData%\Heimdall\queue.db"
set "QUEUEJSON=!QUEUEPATH:\=/!"
set "APIURL_ESC=!APIURL!"
set "APIKEY_ESC=!APIKEY!"
set "MG_ESC=!MACHINEGROUP!"
REM Escape backslashes in values for JSON (unlikely in URL/key/group)
set "APIURL_ESC=!APIURL_ESC:\=\\!"
set "APIKEY_ESC=!APIKEY_ESC:\=\\!"
set "MG_ESC=!MG_ESC:\=\\!"

(
  echo {
  echo   "Heimdall": {
  echo     "ApiBaseUrl": "!APIURL_ESC!",
  echo     "ApiKey": "!APIKEY_ESC!",
  echo     "MachineGroup": "!MG_ESC!",
  echo     "QueuePath": "!QUEUEJSON!"
  echo   },
  echo   "Logging": {
  echo     "LogLevel": {
  echo       "Default": "Information",
  echo       "Microsoft.Hosting.Lifetime": "Information"
  echo     }
  echo   }
  echo }
) > "!INSTALLDIR!\appsettings.json"
if errorlevel 1 (
  call :log ERROR "Failed writing appsettings.json"
  goto fail
)
call :log OK "Wrote !INSTALLDIR!\appsettings.json"
call :log INFO "QueuePath=!QUEUEPATH!"

call :log STEP "Create HeimdallAgent Windows service"
sc.exe create HeimdallAgent binPath= "\"!INSTALLDIR!\Heimdall.Agent.exe\"" start= auto DisplayName= "Heimdall Agent"
if errorlevel 1 (
  call :log ERROR "sc.exe create failed"
  goto fail
)
sc.exe description HeimdallAgent "Heimdall workstation usage reporter" >nul
call :log OK "Service created"

call :log STEP "Start HeimdallAgent"
sc.exe start HeimdallAgent
if errorlevel 1 (
  call :log ERROR "sc.exe start failed — check Event Viewer / .NET runtime"
  goto fail
)
timeout /t 2 /nobreak >nul
sc.exe query HeimdallAgent | findstr /I "RUNNING" >nul
if errorlevel 1 (
  call :log ERROR "HeimdallAgent did not reach RUNNING"
  sc.exe query HeimdallAgent
  goto fail
)

echo.
echo ================================================================
echo   SUCCESS — Workstation collector installed
echo ================================================================
call :log OK "API:     !APIURL!"
call :log OK "Service: HeimdallAgent"
call :log OK "Host:    %COMPUTERNAME% (dashboard Machines after first heartbeat)"
call :log OK "Group:   !MACHINEGROUP!"
call :log OK "Log:     !LOGFILE!"
set "EXITCODE=0"
goto end

:usage
echo.
echo Usage: Install-WorkstationCollector.cmd [options]
echo.
echo Options:
echo   -ApiUrl URL          Heimdall API base URL (default http://localhost:5080^)
echo   -ApiKey KEY          Must match API key (default heimdall-poc-key^)
echo   -MachineGroup NAME   e.g. SOE, POC, APAC/Sydney (default POC^)
echo   -InstallDir PATH     Default %%ProgramFiles%%\Heimdall\Agent
echo   -Payload PATH        Folder containing Heimdall.Agent.exe (default .\payload^)
echo.
echo Pack on a build PC first: scripts\Pack-WorkstationCollector.cmd
echo See scripts\workstation-collector\README.md for files and dependencies.
echo.
set "EXITCODE=1"
goto end

:fail
echo.
echo ================================================================
echo   FAILURE — Workstation collector install did not complete
echo ================================================================
if defined LOGFILE (
  echo Send this log for analysis:
  echo   !LOGFILE!
)
set "EXITCODE=1"
goto end

:end
echo.
echo Full log path:
if defined LOGFILE (echo   !LOGFILE!) else (echo   ^(none^))
echo.
pause
exit /b !EXITCODE!

:log
set "_LVL=%~1"
set "_MSG=%~2"
echo [%DATE% %TIME%] [%_LVL%] %_MSG%
if defined LOGFILE (
  >>"!LOGFILE!" echo [%DATE% %TIME%] [%_LVL%] %_MSG%
)
exit /b 0
