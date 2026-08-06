# Elevated one-shot: publish API (exclude appsettings), copy TuflowLauncher beside agent, restart HeimdallApi.
$ErrorActionPreference = 'Stop'
$publish = 'C:\Heimdall\dist\_publish\Api'
$dest = Join-Path $env:ProgramFiles 'Heimdall\Api'
$agentLauncher = Join-Path $env:ProgramFiles 'Heimdall\Agent\TuflowLauncher'
$launcherSrc = 'C:\Heimdall\dist\TuflowLauncher-publish'

Write-Host 'Publishing API...'
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish 'C:\Heimdall\src\Heimdall.Api\Heimdall.Api.csproj' -c Release -o $publish --self-contained false -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

Write-Host 'Stopping HeimdallApi...'
Stop-Service HeimdallApi -Force -ErrorAction SilentlyContinue
Start-Sleep 2

Write-Host 'Robocopy (exclude appsettings)...'
& robocopy $publish $dest /E /XF appsettings.json appsettings.*.json /NFL /NDL /NJH /NJS /nc /ns /np
# robocopy 0-7 = success
if ($LASTEXITCODE -ge 8) { throw "robocopy failed: $LASTEXITCODE" }

if (Test-Path (Join-Path $env:ProgramFiles 'Heimdall\Agent')) {
    Write-Host 'Copying TuflowLauncher beside agent...'
    New-Item -ItemType Directory -Force -Path $agentLauncher | Out-Null
    & robocopy $launcherSrc $agentLauncher /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
}

Write-Host 'Starting HeimdallApi...'
Start-Service HeimdallApi
Start-Sleep 4
$health = Invoke-RestMethod 'http://127.0.0.1:5080/api/health'
Write-Host ("Health: " + ($health | ConvertTo-Json -Compress))
$runs = Invoke-WebRequest 'http://127.0.0.1:5080/TuflowRuns' -UseBasicParsing
$fleet = Invoke-WebRequest 'http://127.0.0.1:5080/FleetSimProgress' -UseBasicParsing
Write-Host "TuflowRuns=$($runs.StatusCode) FleetSimProgress=$($fleet.StatusCode)"
Write-Host 'DONE'
