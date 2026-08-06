# Elevated one-shot: publish API (exclude appsettings), copy TuflowLauncher beside agent, restart HeimdallApi.
# Exit 0 = publish + deploy + /api/health OK. Flood UI pages are NOT used as a gate (they return 403
# without an interactive Windows identity / flood membership).
$ErrorActionPreference = 'Stop'
$publish = 'C:\Heimdall\dist\_publish\Api'
$dest = Join-Path $env:ProgramFiles 'Heimdall\Api'
$agentLauncher = Join-Path $env:ProgramFiles 'Heimdall\Agent\TuflowLauncher'
$launcherSrc = 'C:\Heimdall\dist\TuflowLauncher-publish'
$logDir = Join-Path $env:ProgramData 'Heimdall\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir ("republish-api-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
$deployLog = Join-Path $logDir 'republish-api-deploy.log'

function Write-RepublishLog([string]$Message) {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message
    Write-Host $line
    Add-Content -LiteralPath $log -Value $line -ErrorAction SilentlyContinue
    Add-Content -LiteralPath $deployLog -Value $line -ErrorAction SilentlyContinue
}

try {
    Write-RepublishLog "Publishing API to $publish..."
    if (Test-Path -LiteralPath $publish) {
        Remove-Item -LiteralPath $publish -Recurse -Force
    }
    dotnet publish 'C:\Heimdall\src\Heimdall.Api\Heimdall.Api.csproj' -c Release -o $publish --self-contained false -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

    Write-RepublishLog 'Stopping HeimdallApi...'
    Stop-Service HeimdallApi -Force -ErrorAction SilentlyContinue
    Start-Sleep 2

    Write-RepublishLog 'Robocopy (exclude appsettings)...'
    & robocopy $publish $dest /E /XF appsettings.json appsettings.*.json /NFL /NDL /NJH /NJS /nc /ns /np
    # robocopy 0-7 = success
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed: $LASTEXITCODE" }

    if (Test-Path (Join-Path $env:ProgramFiles 'Heimdall\Agent')) {
        Write-RepublishLog 'Copying TuflowLauncher beside agent...'
        New-Item -ItemType Directory -Force -Path $agentLauncher | Out-Null
        & robocopy $launcherSrc $agentLauncher /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
        if ($LASTEXITCODE -ge 8) {
            Write-RepublishLog "WARN: TuflowLauncher robocopy exit $LASTEXITCODE (continuing)"
        }
    }

    Write-RepublishLog 'Starting HeimdallApi...'
    Start-Service HeimdallApi
    Start-Sleep 4

    $svc = Get-Service HeimdallApi
    if ($svc.Status -ne 'Running') {
        throw "HeimdallApi status is $($svc.Status), expected Running"
    }

    # Public health only — do not probe Flood-gated Razor pages (403 without flood access).
    $health = Invoke-RestMethod 'http://127.0.0.1:5080/api/health' -TimeoutSec 15
    Write-RepublishLog ("Health: " + ($health | ConvertTo-Json -Compress))
    if (-not $health -or "$($health.status)" -notmatch '^(?i)ok$') {
        throw "Unexpected health payload: $($health | ConvertTo-Json -Compress)"
    }

    Write-RepublishLog "DONE (log: $log)"
    exit 0
}
catch {
    Write-RepublishLog "FAIL: $($_.Exception.Message)"
    try { Start-Service HeimdallApi -ErrorAction SilentlyContinue } catch { }
    Write-Host "Full log: $log"
    exit 1
}
