$ErrorActionPreference = "Stop"
$log = "C:\ProgramData\Heimdall\logs\republish-api-elevated.log"
New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
"STARTED $(Get-Date -Format o)" | Set-Content "C:\Heimdall\_publish\elev-run-marker.txt"
& "C:\Heimdall\.tmp-dbcheck\elevated-redeploy.ps1"
$code = $LASTEXITCODE
"EXIT=$code $(Get-Date -Format o)" | Add-Content "C:\Heimdall\_publish\elev-run-marker.txt"
exit $code
