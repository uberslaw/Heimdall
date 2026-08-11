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
REM
REM Stage markers (parsed by API Client Version pack UI):
REM   HEIMDALL_PACK_STAGE=N/5 label

cd /d "%~dp0.."
set "ROOT=%CD%"
set "OUT=%ROOT%\dist\Heimdall-Client"
set "PAYLOAD=%OUT%\payload"
set "PROJECT=%ROOT%\src\Heimdall.Agent\Heimdall.Agent.csproj"
set "RID=win-x64"
set "NUGET_ORG=https://api.nuget.org/v3/index.json"
set "EXITCODE=1"
set "PACK_STAGES=5"

REM API / non-interactive hosts must never hit "pause" (would hang forever under redirected IO).
if /I "%HEIMDALL_PACK_FROM_API%"=="1" set "HEIMDALL_NOPAUSE=1"

goto :main

:emit_stage
REM %~1 = stage number, %~2 = short label
echo HEIMDALL_PACK_STAGE=%~1/%PACK_STAGES% %~2
echo [*] stage %~1/%PACK_STAGES%: %~2
exit /b 0

:main
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

call :emit_stage 1 "preparing"
echo [*] Checking for .NET 10 SDK...
dotnet --list-sdks | findstr /R "^10\." >nul
if errorlevel 1 (
  echo [WARN] No .NET 10 SDK line found — publish may fail.
  dotnet --list-sdks
)

REM Resolve next simple integer productVersion BEFORE wiping OUT (reads prior VERSION.json).
REM Bump is independent of source fingerprint — every pack advances N+1 unless ForceVersion is set.
REM Override: set HEIMDALL_CLIENT_PRODUCT_VERSION=N. Floor: HEIMDALL_PUBLISHED_CLIENT_VERSION from API.
set "CLIENT_VER="
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell.exe"
set "VER_FILE=%TEMP%\heimdall-client-ver.txt"
if exist "%VER_FILE%" del /F /Q "%VER_FILE%" >nul 2>&1

if defined HEIMDALL_CLIENT_PRODUCT_VERSION (
  if not "%HEIMDALL_CLIENT_PRODUCT_VERSION%"=="" (
    set "CLIENT_VER=%HEIMDALL_CLIENT_PRODUCT_VERSION%"
    echo [*] Using HEIMDALL_CLIENT_PRODUCT_VERSION=%CLIENT_VER%
    goto have_client_ver
  )
)

echo [*] Resolving next productVersion via Resolve-ClientPackVersion.ps1...
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\Resolve-ClientPackVersion.ps1" -RepoRoot "%ROOT%" -PackFolder "%OUT%" >"%VER_FILE%" 2>"%VER_FILE%.err"
if errorlevel 1 (
  echo [ERROR] Resolve-ClientPackVersion.ps1 failed.
  echo        PS=%PS%
  echo        Repo=%ROOT%
  if exist "%VER_FILE%.err" type "%VER_FILE%.err"
  goto fail
)
if not exist "%VER_FILE%" (
  echo [ERROR] Version file not written: %VER_FILE%
  goto fail
)
set /p CLIENT_VER=<"%VER_FILE%"
del /F /Q "%VER_FILE%" >nul 2>&1
if exist "%VER_FILE%.err" del /F /Q "%VER_FILE%.err" >nul 2>&1

:have_client_ver
if not defined CLIENT_VER (
  echo [ERROR] Could not resolve client productVersion
  goto fail
)
for /f "tokens=* delims= " %%A in ("%CLIENT_VER%") do set "CLIENT_VER=%%A"
if "%CLIENT_VER%"=="" (
  echo [ERROR] Could not resolve client productVersion ^(empty^)
  goto fail
)
echo [*] Client productVersion for this pack: %CLIENT_VER%

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

call :emit_stage 2 "building binaries"
echo [*] dotnet publish (self-contained %RID%, InformationalVersion=%CLIENT_VER%)...
echo     First run / cold disk often 2–5 min; warm publish usually 1–3 min...
dotnet publish "%PROJECT%" -c Release -r %RID% --self-contained true -o "%PAYLOAD%" --source "%NUGET_ORG%" -v minimal ^
  /p:Version=%CLIENT_VER% ^
  /p:InformationalVersion=%CLIENT_VER% ^
  /p:AssemblyVersion=%CLIENT_VER%.0.0.0 ^
  /p:FileVersion=%CLIENT_VER%.0.0.0
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

call :emit_stage 3 "publishing launcher"
echo [*] Publishing TuflowLauncher into payload\TuflowLauncher...
set "LAUNCHER_SRC=%ROOT%\tuflow-automation\TuflowLauncher\TuflowLauncher.csproj"
set "LAUNCHER_OUT=%PAYLOAD%\TuflowLauncher"
if exist "%LAUNCHER_OUT%" rmdir /S /Q "%LAUNCHER_OUT%"
dotnet publish "%LAUNCHER_SRC%" -c Release -r %RID% --self-contained false -o "%LAUNCHER_OUT%" --source "%NUGET_ORG%" -v minimal
if errorlevel 1 (
  echo [ERROR] TuflowLauncher publish failed
  goto fail
)
if not exist "%LAUNCHER_OUT%\TuflowLauncher.exe" (
  echo [ERROR] Expected %LAUNCHER_OUT%\TuflowLauncher.exe after publish
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
  "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\New-HeimdallShortcut.ps1" -ShortcutPath "%OUT%\Install.lnk" -TargetPath "%OUT%\Install.cmd" -IconPath "%OUT%\heimdall.ico" -WorkingDirectory "%OUT%" -Description "Install Heimdall Agent on this PC"
  if errorlevel 1 (
    echo [WARN] Could not create Install.lnk — use Install.cmd instead.
  )
  "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\New-HeimdallShortcut.ps1" -ShortcutPath "%OUT%\Heimdall-Setup.lnk" -TargetPath "%OUT%\Heimdall-Setup.cmd" -IconPath "%OUT%\heimdall.ico" -WorkingDirectory "%OUT%" -Description "Heimdall Setup (advanced)"
  if errorlevel 1 (
    echo [WARN] Could not create Heimdall-Setup.lnk — use Heimdall-Setup.cmd instead.
  )
  "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\New-HeimdallShortcut.ps1" -ShortcutPath "%OUT%\Heimdall-LaunchControl.lnk" -TargetPath "%OUT%\Heimdall-Setup.cmd" -IconPath "%OUT%\heimdall.ico" -WorkingDirectory "%OUT%" -Description "Heimdall Setup (advanced)"
) else (
  echo [WARN] assets\heimdall.ico missing — pack will not include helmet icon shortcuts.
)

call :emit_stage 4 "writing manifest"
echo [*] Writing VERSION.json + PACKED.txt (productVersion=%CLIENT_VER%)...
(
  echo {
  echo   "productVersion": "%CLIENT_VER%",
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
  echo ProductVersion: %CLIENT_VER%
  echo Output:      Heimdall-Client
) > "%OUT%\PACKED.txt"

echo [*] Writing MANIFEST.sha256 + sourceFingerprint...
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\Write-ClientPackManifest.ps1" -RepoRoot "%ROOT%" -PackFolder "%OUT%"
if errorlevel 1 (
  echo [ERROR] Write-ClientPackManifest.ps1 failed
  goto fail
)

call :emit_stage 5 "zip finalize"
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
