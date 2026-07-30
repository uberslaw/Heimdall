@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Heimdall Install
cd /d "%~dp0"

echo.
echo ================================================================
echo   Heimdall Workstation Collector - Install
echo ================================================================
echo.
echo Starting guided install wizard...
echo Logs: %ProgramData%\Heimdall\logs\
echo.

net session >nul 2>&1
if errorlevel 1 (
  echo Administrator rights required.
  echo Accept the UAC prompt to continue.
  echo.
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs -Wait; exit $LASTEXITCODE"
  set "EC=!ERRORLEVEL!"
  if not "!EC!"=="0" (
    echo.
    echo Install did not complete ^(exit !EC!^).
    pause
  )
  exit /b !EC!
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0Install-Client.ps1" %*
set "EC=%ERRORLEVEL%"
if not "%EC%"=="0" (
  echo.
  echo Install wizard exited with code %EC%.
  echo Check %ProgramData%\Heimdall\logs\ for details.
  pause
)
exit /b %EC%
