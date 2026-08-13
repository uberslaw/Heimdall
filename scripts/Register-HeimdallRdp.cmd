@echo off
setlocal EnableExtensions
REM Registers heimdall-rdp to wscript + Heimdall-LaunchRdp.vbs (HKCU, and HKLM if admin).
REM No PowerShell. Pass /user to force LocalAppData + HKCU only.
set "VBS=%~dp0Heimdall-LaunchRdp.vbs"
if not exist "%VBS%" (
  echo Heimdall-LaunchRdp.vbs was not found next to this script.
  exit /b 1
)
if not exist "%SystemRoot%\System32\cscript.exe" (
  echo cscript.exe was not found. One-click Connect needs Windows Script Host.
  exit /b 1
)
"%SystemRoot%\System32\cscript.exe" //nologo "%VBS%" /register %*
exit /b %ERRORLEVEL%
