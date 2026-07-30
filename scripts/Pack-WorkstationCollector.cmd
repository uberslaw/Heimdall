@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Heimdall Create Client Pack

REM Build one portable Heimdall-Client folder to copy to other PCs.
REM Requires: .NET 10 SDK on THIS machine. Target PCs need no SDK (self-contained).
REM
REM Output:
REM   dist\Heimdall-Client\
REM     Install.lnk          ← only entry clients need
REM     Install.cmd / wizard scripts
REM     payload\             (published Heimdall.Agent.exe + deps)
REM   dist\heimdall-client.zip  (optional, if tar available)

cd /d "%~dp0.."
set "ROOT=%CD%"
set "OUT=%ROOT%\dist\Heimdall-Client"
set "PAYLOAD=%OUT%\payload"
set "PROJECT=%ROOT%\src\Heimdall.Agent\Heimdall.Agent.csproj"
set "RID=win-x64"
set "NUGET_ORG=https://api.nuget.org/v3/index.json"
set "EXITCODE=1"

echo.
echo ================================================================
echo   Create Heimdall Client pack
echo ================================================================
echo.
echo Repo:    %ROOT%
echo Output:  %OUT%
echo RID:     %RID% (self-contained)
echo.
echo Copy ONLY dist\Heimdall-Client\ to target PCs after SUCCESS.
echo docs\portable-client\ in the repo is documentation only.
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
REM Remove legacy pack folder name if present (avoid two confusing packs)
if exist "%ROOT%\dist\workstation-collector" rmdir /S /Q "%ROOT%\dist\workstation-collector"
mkdir "%PAYLOAD%" >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Could not create %PAYLOAD%
  goto fail
)

if not exist "%ROOT%\NuGet.config" (
  echo [WARN] NuGet.config missing at %ROOT%\NuGet.config
  echo       Add nuget.org:
  echo         dotnet nuget add source %NUGET_ORG% -n nuget.org
)

echo [*] Registered NuGet sources ^(machine/user^):
dotnet nuget list source
echo.
echo [*] Forcing restore source: %NUGET_ORG%

echo [*] dotnet publish (self-contained %RID%)...
echo     First run can take several minutes ^(download runtime packs^)...
dotnet publish "%PROJECT%" -c Release -r %RID% --self-contained true -o "%PAYLOAD%" --source "%NUGET_ORG%" -v minimal
if errorlevel 1 (
  echo [ERROR] dotnet publish failed
  echo.
  echo Common cause: no network to nuget.org, or proxy blocking it.
  echo.
  echo Quick checks:
  echo   1. Sync repo so NuGet.config exists at %ROOT%\NuGet.config
  echo   2. Add a durable source:
  echo        dotnet nuget add source %NUGET_ORG% -n nuget.org
  echo   3. Test:  curl.exe -I %NUGET_ORG%
  echo   4. Retry: scripts\Pack-WorkstationCollector.cmd
  echo     or:    scripts\Heimdall-Setup.lnk -^> Create client pack
  echo.
  goto fail
)

if not exist "%PAYLOAD%\Heimdall.Agent.exe" (
  echo [ERROR] Expected %PAYLOAD%\Heimdall.Agent.exe after publish
  goto fail
)

echo [*] Copying installers + docs into package...
copy /Y "%ROOT%\scripts\Install.cmd" "%OUT%\Install.cmd" >nul
copy /Y "%ROOT%\scripts\Install-Client.ps1" "%OUT%\Install-Client.ps1" >nul
copy /Y "%ROOT%\scripts\Heimdall-VersionCompare.ps1" "%OUT%\Heimdall-VersionCompare.ps1" >nul
copy /Y "%ROOT%\scripts\Heimdall-CollectorInstall.ps1" "%OUT%\Heimdall-CollectorInstall.ps1" >nul
copy /Y "%ROOT%\scripts\Install-WorkstationCollector.cmd" "%OUT%\Install-WorkstationCollector.cmd" >nul
copy /Y "%ROOT%\scripts\Heimdall-Setup.cmd" "%OUT%\Heimdall-Setup.cmd" >nul
copy /Y "%ROOT%\scripts\Heimdall-LaunchControl.cmd" "%OUT%\Heimdall-LaunchControl.cmd" >nul
copy /Y "%ROOT%\scripts\Heimdall-LaunchControl.ps1" "%OUT%\Heimdall-LaunchControl.ps1" >nul
copy /Y "%ROOT%\docs\portable-client\README.md" "%OUT%\README.md" >nul
copy /Y "%ROOT%\docs\portable-client\FILES.md" "%OUT%\FILES.md" >nul

if exist "%ROOT%\assets\heimdall.ico" (
  copy /Y "%ROOT%\assets\heimdall.ico" "%OUT%\heimdall.ico" >nul
  echo [*] Creating helmet-icon shortcuts in package...
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\New-HeimdallShortcut.ps1" -ShortcutPath "%OUT%\Install.lnk" -TargetPath "%OUT%\Install.cmd" -IconPath "%OUT%\heimdall.ico" -WorkingDirectory "%OUT%" -Description "Install Heimdall Agent on this PC"
  if errorlevel 1 (
    echo [WARN] Could not create Install.lnk — use Install.cmd instead.
  )
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\New-HeimdallShortcut.ps1" -ShortcutPath "%OUT%\Heimdall-Setup.lnk" -TargetPath "%OUT%\Heimdall-Setup.cmd" -IconPath "%OUT%\heimdall.ico" -WorkingDirectory "%OUT%" -Description "Heimdall Setup (advanced)"
  if errorlevel 1 (
    echo [WARN] Could not create Heimdall-Setup.lnk — use Heimdall-Setup.cmd instead.
  )
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\New-HeimdallShortcut.ps1" -ShortcutPath "%OUT%\Heimdall-LaunchControl.lnk" -TargetPath "%OUT%\Heimdall-Setup.cmd" -IconPath "%OUT%\heimdall.ico" -WorkingDirectory "%OUT%" -Description "Heimdall Setup (advanced)"
) else (
  echo [WARN] assets\heimdall.ico missing — pack will not include helmet icon shortcuts.
)

echo [*] Writing VERSION.json + PACKED.txt...
(
  echo {
  echo   "productVersion": "0.1.0",
  echo   "rid": "%RID%",
  echo   "selfContained": true,
  echo   "targetFramework": "net10.0",
  echo   "packedAtUtc": "%DATE% %TIME%",
  echo   "packedBy": "%USERNAME%",
  echo   "packedFrom": "%COMPUTERNAME%",
  echo   "repo": "%ROOT:\=\\%"
  echo }
) > "%OUT%\VERSION.json"

(
  echo Packed from: %COMPUTERNAME%
  echo Packed by:   %USERNAME%
  echo Packed at:   %DATE% %TIME%
  echo Repo:        %ROOT%
  echo RID:         %RID%
  echo SelfContained: true
  echo ProductVersion: 0.1.0
  echo Output:      Heimdall-Client
) > "%OUT%\PACKED.txt"

set "ZIP=%ROOT%\dist\heimdall-client.zip"
if exist "%ZIP%" del /F /Q "%ZIP%" >nul 2>&1
if exist "%ROOT%\dist\heimdall-workstation-collector.zip" del /F /Q "%ROOT%\dist\heimdall-workstation-collector.zip" >nul 2>&1
where tar >nul 2>&1
if not errorlevel 1 (
  echo [*] Creating zip with tar...
  pushd "%ROOT%\dist"
  tar -a -cf "heimdall-client.zip" "Heimdall-Client"
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
echo   SUCCESS — one client folder ready
echo ================================================================
echo.
echo Folder to copy:
echo   %OUT%
echo.
echo On each client PC, double-click:
echo   Install.lnk
echo.
echo Pack again only when the agent changes.
echo.
set "EXITCODE=0"
goto end

:fail
echo.
echo Pack failed.
set "EXITCODE=1"

:end
echo.
if /I not "%HEIMDALL_NOPAUSE%"=="1" pause
exit /b %EXITCODE%
