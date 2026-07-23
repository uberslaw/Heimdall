@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Heimdall Pack Workstation Collector

REM Build a portable workstation-collector folder you can copy to other PCs.
REM Requires: .NET 10 SDK on THIS machine. Target PCs need no SDK (self-contained).
REM
REM Output:
REM   dist\workstation-collector\
REM     Install-WorkstationCollector.cmd
REM     README.md
REM     FILES.md
REM     payload\   (published Heimdall.Agent.exe + deps)
REM   dist\heimdall-workstation-collector.zip  (optional, if tar available)

cd /d "%~dp0.."
set "ROOT=%CD%"
set "OUT=%ROOT%\dist\workstation-collector"
set "PAYLOAD=%OUT%\payload"
set "PROJECT=%ROOT%\src\Heimdall.Agent\Heimdall.Agent.csproj"
set "RID=win-x64"
set "EXITCODE=1"

echo.
echo ================================================================
echo   Pack Heimdall Workstation Collector
echo ================================================================
echo.
echo Repo:    %ROOT%
echo Output:  %OUT%
echo RID:     %RID% (self-contained)
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERROR] dotnet not found on PATH. Install .NET 10 SDK:
  echo   https://dotnet.microsoft.com/download/dotnet/10.0
  goto fail
)

if not exist "%PROJECT%" (
  echo [ERROR] Project not found: %PROJECT%
  echo Run this from a full Heimdall clone.
  goto fail
)

echo [*] Checking for .NET 10 SDK...
dotnet --list-sdks | findstr /R "^10\." >nul
if errorlevel 1 (
  echo [WARN] No .NET 10 SDK line found — publish may fail.
  dotnet --list-sdks
)

echo [*] Cleaning output folder...
if exist "%OUT%" rmdir /S /Q "%OUT%"
mkdir "%PAYLOAD%" >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Could not create %PAYLOAD%
  goto fail
)

echo [*] dotnet publish (self-contained %RID%)...
echo     This can take a minute...
dotnet publish "%PROJECT%" -c Release -r %RID% --self-contained true -o "%PAYLOAD%" -v minimal
if errorlevel 1 (
  echo [ERROR] dotnet publish failed
  goto fail
)

if not exist "%PAYLOAD%\Heimdall.Agent.exe" (
  echo [ERROR] Expected %PAYLOAD%\Heimdall.Agent.exe after publish
  goto fail
)

echo [*] Copying installer + docs into package...
copy /Y "%ROOT%\scripts\Install-WorkstationCollector.cmd" "%OUT%\Install-WorkstationCollector.cmd" >nul
copy /Y "%ROOT%\scripts\workstation-collector\README.md" "%OUT%\README.md" >nul
copy /Y "%ROOT%\scripts\workstation-collector\FILES.md" "%OUT%\FILES.md" >nul

echo [*] Writing package stamp...
(
  echo Packed from: %COMPUTERNAME%
  echo Packed by:   %USERNAME%
  echo Packed at:   %DATE% %TIME%
  echo Repo:        %ROOT%
  echo RID:         %RID%
  echo SelfContained: true
) > "%OUT%\PACKED.txt"

set "ZIP=%ROOT%\dist\heimdall-workstation-collector.zip"
if exist "%ZIP%" del /F /Q "%ZIP%" >nul 2>&1
where tar >nul 2>&1
if not errorlevel 1 (
  echo [*] Creating zip with tar...
  pushd "%ROOT%\dist"
  tar -a -cf "heimdall-workstation-collector.zip" "workstation-collector"
  popd
  if exist "%ZIP%" (
    echo [OK] Zip: %ZIP%
  ) else (
    echo [WARN] Zip creation failed — folder pack is still usable.
  )
) else (
  echo [WARN] tar not found — skip zip; copy the folder instead.
)

echo.
echo ================================================================
echo   SUCCESS — portable collector package ready
echo ================================================================
echo.
echo Folder:
echo   %OUT%
echo.
echo Copy that folder ^(or the zip^) to each workstation, then elevated:
echo   Install-WorkstationCollector.cmd -ApiUrl http://YOUR-API-HOST:5080 -MachineGroup SOE
echo.
echo Target PCs do NOT need the Heimdall repo or .NET SDK
echo ^(self-contained win-x64 payload^).
echo.
set "EXITCODE=0"
goto end

:fail
echo.
echo Pack failed.
set "EXITCODE=1"

:end
echo.
pause
exit /b %EXITCODE%
